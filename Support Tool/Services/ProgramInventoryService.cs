using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Support_Tool.Models;

namespace Support_Tool.Services;

public class ProgramInventoryService
{
    private static readonly string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    // Streams one InstalledItem per registry entry as each hive is walked, instead of collecting
    // everything into a list first and returning it all at once - registry enumeration is
    // synchronous/CPU-bound, so the walk itself runs on a background thread (Task.Run) while items
    // are pushed through a Channel as soon as each is parsed, keeping the same
    // dedupe-by-name-across-hives behavior the previous bulk method had (a program can legitimately
    // appear in more than one hive - HKLM64 and HKCU, for instance - and must only become one row).
    public async IAsyncEnumerable<InstalledItem> GetInstalledProgramsIncrementalAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<InstalledItem>();

        var pumpTask = Task.Run(() =>
        {
            try
            {
                var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ReadFromHive(RegistryHive.LocalMachine, RegistryView.Registry64, seenNames, channel.Writer, ct);
                ReadFromHive(RegistryHive.LocalMachine, RegistryView.Registry32, seenNames, channel.Writer, ct);
                ReadFromHive(RegistryHive.CurrentUser, RegistryView.Default, seenNames, channel.Writer, ct);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        await foreach (var item in channel.Reader.ReadAllAsync(ct))
        {
            yield return item;
        }

        await pumpTask;
    }

    private static void ReadFromHive(
        RegistryHive hive,
        RegistryView view,
        HashSet<string> seenNames,
        ChannelWriter<InstalledItem> writer,
        CancellationToken ct)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(UninstallKeyPath);
            if (uninstallKey is null) return;

            // Registry64/Registry32 unambiguously identify architecture; HKCU entries have no
            // such view distinction (WOW6432Node redirection doesn't apply to HKCU the same way),
            // so ParseRegistryKey falls back to a path-based heuristic for those specifically -
            // see its own remarks.
            bool? architectureIs64Bit = view switch
            {
                RegistryView.Registry64 => true,
                RegistryView.Registry32 => false,
                _ => null,
            };

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    using var subKey = uninstallKey.OpenSubKey(subKeyName);
                    if (subKey is null) continue;

                    var item = ParseRegistryKey(subKey, architectureIs64Bit, hive, view, subKeyName);
                    if (item is not null && seenNames.Add(item.Name))
                    {
                        writer.TryWrite(item);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProgramInventory] Skipped key {subKeyName}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProgramInventory] Failed to open hive {hive}/{view}: {ex.Message}");
        }
    }

    private static InstalledItem? ParseRegistryKey(
        RegistryKey key,
        bool? architectureIs64Bit,
        RegistryHive hive,
        RegistryView view,
        string subKeyName)
    {
        var displayName = key.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(displayName))
            return null;

        var uninstallString = key.GetValue("UninstallString") as string;
        if (string.IsNullOrWhiteSpace(uninstallString))
            return null;

        var systemComponent = key.GetValue("SystemComponent");
        if (systemComponent is int sc && sc == 1)
            return null;

        var version = key.GetValue("DisplayVersion") as string ?? "";
        var publisher = key.GetValue("Publisher") as string ?? "";
        var displayIcon = key.GetValue("DisplayIcon") as string ?? "";
        var installLocation = key.GetValue("InstallLocation") as string ?? "";

        long? sizeKb = null;
        var rawSize = key.GetValue("EstimatedSize");
        if (rawSize is int sizeInt && sizeInt > 0)
            sizeKb = sizeInt;
        else if (rawSize is long sizeLong && sizeLong > 0)
            sizeKb = sizeLong;

        var icon = TryLoadIcon(displayIcon);

        // Captured now, before any uninstall attempt - the Revo /mu cleanup pass (see
        // RealUninstallExecutor) needs both once native uninstall has already removed the
        // program's own registry entry, so there is no later point this could be re-derived from.
        // HKCU entries (architectureIs64Bit == null) have no registry-view signal at all, so fall
        // back to a path heuristic: anything installed under "Program Files (x86)" is 32-bit,
        // everything else is assumed 64-bit (matching this machine's default for a per-user
        // installer). Wrong only risks a Revo cleanup warning, never data loss - see
        // RevoUninstallService's own remarks.
        var is64Bit = architectureIs64Bit
            ?? !installLocation.Contains(@"Program Files (x86)", StringComparison.OrdinalIgnoreCase);

        return new InstalledItem
        {
            Name = displayName,
            Version = version,
            Publisher = publisher,
            UninstallCommand = uninstallString,
            Icon = icon,
            Kind = InstalledItemKind.Program,
            InstalledSizeKb = sizeKb,
            InstallLocation = installLocation,
            Is64Bit = is64Bit,
            // The exact ARP registration this item came from - ProgramUninstallExecutor re-opens
            // this precise location after the uninstaller process exits to positively verify the
            // registration is actually gone before ever trusting "Succeeded" (see its own remarks).
            RegistryHiveName = hive == RegistryHive.CurrentUser ? "CurrentUser" : "LocalMachine",
            RegistryViewName = view.ToString(),
            UninstallRegistryKeyName = subKeyName,
        };
    }

    private static ImageSource? TryLoadIcon(string displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
            return null;

        try
        {
            var iconPath = displayIcon;
            if (iconPath.Contains(','))
                iconPath = iconPath[..iconPath.LastIndexOf(',')].Trim().Trim('"');

            if (!File.Exists(iconPath))
                return null;

            var ext = Path.GetExtension(iconPath).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
                return TryLoadBitmapImage(iconPath);

            return TryExtractIconFromExe(iconPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProgramInventory] Icon load failed for '{displayIcon}': {ex.Message}");
            return null;
        }
    }

    private static ImageSource? TryExtractIconFromExe(string path)
    {
        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;

            using var bitmap = icon.ToBitmap();
            using var ms = new System.IO.MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;

            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.StreamSource = ms;
            img.DecodePixelHeight = 44;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProgramInventory] ExtractAssociatedIcon failed: {ex.Message}");
            return null;
        }
    }

    private static BitmapImage? TryLoadBitmapImage(string path)
    {
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.UriSource = new Uri(path, UriKind.Absolute);
            img.DecodePixelHeight = 44;
            img.EndInit();
            img.Freeze();
            return img;
        }
        catch
        {
            return null;
        }
    }
}
