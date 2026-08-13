using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Support_Tool.Models;

namespace Support_Tool.Services;

public class ProgramInventoryService
{
    private static readonly string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public Task<List<InstalledItem>> GetInstalledProgramsAsync()
    {
        return Task.Run(() =>
        {
            var results = new Dictionary<string, InstalledItem>(StringComparer.OrdinalIgnoreCase);

            ReadFromHive(RegistryHive.LocalMachine, RegistryView.Registry64, results);
            ReadFromHive(RegistryHive.LocalMachine, RegistryView.Registry32, results);
            ReadFromHive(RegistryHive.CurrentUser, RegistryView.Default, results);

            return results.Values
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        });
    }

    private static void ReadFromHive(
        RegistryHive hive,
        RegistryView view,
        Dictionary<string, InstalledItem> results)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(UninstallKeyPath);
            if (uninstallKey is null) return;

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                try
                {
                    using var subKey = uninstallKey.OpenSubKey(subKeyName);
                    if (subKey is null) continue;

                    var item = ParseRegistryKey(subKey);
                    if (item is not null && !results.ContainsKey(item.Name))
                        results[item.Name] = item;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ProgramInventory] Skipped key {subKeyName}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProgramInventory] Failed to open hive {hive}/{view}: {ex.Message}");
        }
    }

    private static InstalledItem? ParseRegistryKey(RegistryKey key)
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

        long? sizeKb = null;
        var rawSize = key.GetValue("EstimatedSize");
        if (rawSize is int sizeInt && sizeInt > 0)
            sizeKb = sizeInt;
        else if (rawSize is long sizeLong && sizeLong > 0)
            sizeKb = sizeLong;

        var icon = TryLoadIcon(displayIcon);

        return new InstalledItem
        {
            Name = displayName,
            Version = version,
            Publisher = publisher,
            UninstallCommand = uninstallString,
            Icon = icon,
            Kind = InstalledItemKind.Program,
            InstalledSizeKb = sizeKb,
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
