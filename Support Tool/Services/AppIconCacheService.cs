using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Toolkit.Shared;

namespace Support_Tool.Services;

// Resolves an app's icon to a local file, fetching it once from Google's favicon
// service (same technique WinUtil uses) and caching it to disk so subsequent loads
// never re-hit the network. WingetId is filesystem-safe (dotted alphanumeric) so it
// doubles as the cache key.
public class AppIconCacheService
{
    private const string ToolName = "Support Tool";
    private const string CacheFolderName = "app-icons";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<string?> GetOrFetchIconPathAsync(string wingetId, string link)
    {
        var cacheDir = Path.Combine(ToolkitPaths.GetConfigFolder(ToolName), CacheFolderName);
        Directory.CreateDirectory(cacheDir);

        var path = Path.Combine(cacheDir, $"{wingetId}.png");
        if (File.Exists(path))
            return path;

        if (string.IsNullOrWhiteSpace(link))
            return null;

        try
        {
            var url = $"https://www.google.com/s2/favicons?sz=64&domain_url={Uri.EscapeDataString(link)}";
            var bytes = await Http.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(path, bytes);
            return path;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppIconCacheService] Failed to fetch icon for '{wingetId}': {ex.Message}");
            return null;
        }
    }
}
