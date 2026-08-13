using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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

    public async Task<List<InstalledItem>> GetInstalledAppsAsync()
    {
        var json = await RunPowerShellAsync(
            "Get-AppxPackage | Select-Object Name,Version,Publisher,PackageFullName,InstallLocation | ConvertTo-Json -Compress");

        if (string.IsNullOrWhiteSpace(json))
            return [];

        var items = new List<InstalledItem>();

        try
        {
            var node = JsonNode.Parse(json);
            var array = node is JsonArray arr ? arr : new JsonArray(node!.DeepClone());

            foreach (var element in array)
            {
                if (element is null) continue;
                try
                {
                    var item = ParsePackageNode(element);
                    if (item is not null)
                        items.Add(item);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[AppInventory] Skipped entry: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppInventory] Failed to parse output: {ex.Message}");
        }

        return items;
    }

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

        return new InstalledItem
        {
            Name = displayName,
            Version = version,
            Publisher = publisher,
            PackageFullName = packageFullName,
            Icon = icon,
            Kind = InstalledItemKind.App,
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

    private static async Task<string> RunPowerShellAsync(string command)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -NonInteractive -Command \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start PowerShell.");
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return output;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppInventory] PowerShell failed: {ex.Message}");
            return "";
        }
    }
}
