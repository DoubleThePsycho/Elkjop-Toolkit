using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Support_Tool.Models;

namespace Support_Tool.Services;

public class AppInventoryService
{
    private static readonly string[] FrameworkPrefixes =
    [
        "Microsoft.NET.",
        "Microsoft.VCLibs.",
        "Microsoft.UI.",
        "Microsoft.WindowsAppRuntime.",
        "Microsoft.DesktopAppInstaller",
        "Microsoft.StorePurchaseApp",
        "Microsoft.Services.Store",
        "Windows.CBSPreview",
        "Microsoft.D3DMappingLayers",
        "Microsoft.LanguageExperiencePack",
        "MicrosoftCorporationII.WindowsSubsystem",
        "Microsoft.AAD.",
        "Microsoft.LockApp",
        "Microsoft.Win32WebViewHost",
        "Microsoft.Windows.SecureAssessmentBrowser",
    ];

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHLoadIndirectString(
        string pszSource,
        StringBuilder pszOutBuf,
        int cchOutBuf,
        IntPtr ppvReserved);

    // Streams one InstalledItem per Appx package as PowerShell discovers it, instead of waiting
    // for the whole catalog and returning it all at once - the previous bulk
    // "ConvertTo-Json" (on the whole collection) can't emit anything until every package has been
    // resolved, which is exactly the long blocking "Loading…" this replaces. The script instead
    // pipes each package through its own ConvertTo-Json -Compress, so PowerShell writes one
    // complete, single-line JSON object per package as soon as that package is ready - each line
    // is parsed and yielded the moment OutputDataReceived delivers it, bridged from that push-
    // based callback to this pull-based async stream via an unbounded Channel.
    public async IAsyncEnumerable<InstalledItem> GetInstalledAppsIncrementalAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateUnbounded<InstalledItem>();

        var psi = new ProcessStartInfo("powershell.exe")
        {
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {EncodedStreamingScript}",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var pumpTask = Task.Run(async () =>
        {
            Process? process = null;
            try
            {
                process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.OutputDataReceived += (_, e) =>
                {
                    if (string.IsNullOrWhiteSpace(e.Data)) return;
                    var item = TryParseLine(e.Data);
                    if (item is not null) channel.Writer.TryWrite(item);
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        Debug.WriteLine($"[AppInventory] PowerShell stderr: {e.Data}");
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Killed on cancellation (e.g. the page was navigated away from mid-scan) - .NET's
                // WaitForExitAsync(ct) only stops waiting on cancellation, it doesn't kill the
                // process on its own, which would otherwise leave a PowerShell instance running in
                // the background for every superseded discovery.
                using var killOnCancel = ct.Register(() =>
                {
                    try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                    catch { /* already exited or couldn't be killed - not fatal here */ }
                });

                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Expected on discovery cancellation - the kill above already requested it.
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AppInventory] Incremental enumeration failed: {ex.Message}");
            }
            finally
            {
                process?.Dispose();
                channel.Writer.TryComplete();
            }
        }, ct);

        await foreach (var item in channel.Reader.ReadAllAsync(ct))
        {
            yield return item;
        }

        await pumpTask;
    }

    private static InstalledItem? TryParseLine(string line)
    {
        try
        {
            var node = JsonNode.Parse(line);
            return node is null ? null : ParsePackageNode(node);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppInventory] Skipped unparsable line: {ex.Message}");
            return null;
        }
    }

    private const string StreamingScript = @"
$ProgressPreference = 'SilentlyContinue'
Get-AppxPackage | ForEach-Object {
    $_ | Select-Object Name,Version,Publisher,PackageFullName,InstallLocation,NonRemovable | ConvertTo-Json -Compress
}
";

    private static readonly string EncodedStreamingScript =
        Convert.ToBase64String(Encoding.Unicode.GetBytes(StreamingScript));

    private static InstalledItem? ParsePackageNode(JsonNode node)
    {
        var rawName = node["Name"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(rawName)) return null;

        if (Array.Exists(FrameworkPrefixes, p =>
            rawName.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return null;

        var version = node["Version"]?.GetValue<string>() ?? "";
        var rawPublisher = node["Publisher"]?.GetValue<string>() ?? "";
        var packageFullName = node["PackageFullName"]?.GetValue<string>() ?? "";
        var installLocation = node["InstallLocation"]?.GetValue<string>() ?? "";

        var manifest = TryParseManifest(installLocation);

        // Resolve display name: manifest → fallback to cleaned package name
        string? displayName = null;
        if (manifest != null)
            displayName = ResolveManifestString(manifest.DisplayName, packageFullName, manifest.IdentityName);

        if (string.IsNullOrWhiteSpace(displayName))
            displayName = CleanPackageName(rawName);

        if (string.IsNullOrWhiteSpace(displayName) || IsGuidLike(displayName))
            return null;

        // Resolve publisher: manifest PublisherDisplayName → DN parser fallback
        string publisher;
        if (manifest?.PublisherDisplayName != null)
        {
            var resolvedPub = ResolveManifestString(manifest.PublisherDisplayName, packageFullName, manifest.IdentityName);
            publisher = string.IsNullOrWhiteSpace(resolvedPub)
                ? CleanPublisherDN(rawPublisher)
                : resolvedPub;
        }
        else
        {
            publisher = CleanPublisherDN(rawPublisher);
        }

        // Load icon from manifest logo path
        BitmapImage? icon = null;
        if (!string.IsNullOrWhiteSpace(manifest?.LogoRelPath) && !string.IsNullOrWhiteSpace(installLocation))
        {
            var logoPath = Path.Combine(installLocation, manifest.LogoRelPath);
            icon = TryLoadBitmapImage(logoPath) ?? TryLoadScaledVariant(logoPath);
        }

        // Captured now, before any uninstall attempt, from the same Get-AppxPackage call that
        // lists it - lets AppxUninstallExecutor skip known-protected packages immediately
        // instead of attempting (and failing) a real removal. See InstalledItem's own remarks.
        var isProtected = node["NonRemovable"]?.GetValue<bool>() ?? false;

        return new InstalledItem
        {
            Name = displayName,
            Version = version,
            Publisher = publisher,
            PackageFullName = packageFullName,
            Icon = icon,
            Kind = InstalledItemKind.App,
            IsProtectedSystemComponent = isProtected,
        };
    }

    private record ManifestData(
        string? IdentityName,
        string? DisplayName,
        string? PublisherDisplayName,
        string? LogoRelPath);

    private static ManifestData? TryParseManifest(string installLocation)
    {
        if (string.IsNullOrWhiteSpace(installLocation)) return null;

        var manifestPath = Path.Combine(installLocation, "AppxManifest.xml");
        if (!File.Exists(manifestPath)) return null;

        try
        {
            var doc = XDocument.Load(manifestPath);

            var identityName = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Identity")
                ?.Attribute("Name")?.Value;

            var propsEl = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "Properties");

            var displayName = propsEl?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value;

            var publisherDisplayName = propsEl?.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "PublisherDisplayName")?.Value;

            var visualEl = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "VisualElements");

            if (string.IsNullOrWhiteSpace(displayName))
                displayName = visualEl?.Attribute("DisplayName")?.Value;

            var logoRelPath =
                visualEl?.Attribute("Square44x44Logo")?.Value
                ?? visualEl?.Attribute("Square150x150Logo")?.Value
                ?? propsEl?.Elements().FirstOrDefault(e => e.Name.LocalName == "Logo")?.Value;

            return new ManifestData(identityName, displayName, publisherDisplayName, logoRelPath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppInventory] Manifest parse failed: {ex.Message}");
            return null;
        }
    }

    private static string? ResolveManifestString(string? raw, string packageFullName, string? identityName)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!raw.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
            return raw; // plain string — use directly

        if (string.IsNullOrWhiteSpace(identityName) || string.IsNullOrWhiteSpace(packageFullName))
            return null;

        if (raw.StartsWith("ms-resource://", StringComparison.OrdinalIgnoreCase))
            return SHLoadIndirectStringCall($"@{{{packageFullName}?{raw}}}");

        var key = raw["ms-resource:".Length..].TrimStart('/');
        // Try /Resources/Key (most common convention) then bare /Key
        return SHLoadIndirectStringCall($"@{{{packageFullName}?ms-resource://{identityName}/Resources/{key}}}")
            ?? SHLoadIndirectStringCall($"@{{{packageFullName}?ms-resource://{identityName}/{key}}}");
    }

    private static string? SHLoadIndirectStringCall(string input)
    {
        try
        {
            var sb = new StringBuilder(512);
            int hr = SHLoadIndirectString(input, sb, sb.Capacity, IntPtr.Zero);
            return hr == 0 && sb.Length > 0 ? sb.ToString() : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppInventory] SHLoadIndirectString failed: {ex.Message}");
            return null;
        }
    }

    private static string CleanPublisherDN(string publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return "Unknown publisher";
        if (!publisher.Contains('=')) return publisher;

        // Extract O= (Organization) — most readable field in an X.509 DN
        var oMatch = Regex.Match(publisher, @"(?:^|,\s*)O=([^,]+)");
        if (oMatch.Success)
        {
            var org = oMatch.Groups[1].Value.Trim().Trim('"');
            if (!string.IsNullOrEmpty(org)) return org;
        }

        // Fallback: CN= (Common Name)
        var cnMatch = Regex.Match(publisher, @"(?:^|,\s*)CN=([^,]+)");
        if (cnMatch.Success)
        {
            var cn = cnMatch.Groups[1].Value.Trim().Trim('"');
            if (!string.IsNullOrEmpty(cn)) return cn;
        }

        return "Unknown publisher";
    }

    private static string CleanPackageName(string packageName)
    {
        var name = packageName;
        foreach (var prefix in new[] { "Microsoft.", "Windows.", "MicrosoftCorporationII." })
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..];
                break;
            }
        }
        // CamelCase → Title Case with spaces
        name = Regex.Replace(name, @"(?<=[a-z])(?=[A-Z])", " ");
        return name.Trim();
    }

    private static bool IsGuidLike(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2) return true;
        if (Regex.IsMatch(name, @"^\{?[0-9A-Fa-f]{8}[-]?[0-9A-Fa-f]{4}[-]?[0-9A-Fa-f]{4}[-]?[0-9A-Fa-f]{4}[-]?[0-9A-Fa-f]{12}\}?$"))
            return true;
        if (Regex.IsMatch(name, @"^[\d\-_\.]+$"))
            return true;
        return false;
    }

    private static BitmapImage? TryLoadScaledVariant(string originalPath)
    {
        var dir = Path.GetDirectoryName(originalPath);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(originalPath);
        var ext = Path.GetExtension(originalPath);
        if (dir is null) return null;

        foreach (var scale in new[] { "scale-100", "scale-150", "scale-200", "scale-125" })
        {
            var img = TryLoadBitmapImage(Path.Combine(dir, $"{nameWithoutExt}.{scale}{ext}"));
            if (img is not null) return img;
        }

        return null;
    }

    private static BitmapImage? TryLoadBitmapImage(string path)
    {
        if (!File.Exists(path)) return null;

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
