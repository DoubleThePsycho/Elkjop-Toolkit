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

    // Fast, synchronous, cache-only lookup - never touches the network. Lets an Install card show
    // its real icon immediately if it's already cached, without blocking card visibility on a
    // fetch; GetOrFetchIconPathAsync (below) still runs afterward for a genuine cache miss, and
    // the card's Icon is updated in place once/if that resolves.
    public string? TryGetCachedIconPath(string wingetId)
    {
        var path = GetCachePath(wingetId);
        return File.Exists(path) ? path : null;
    }

    private static string GetCachePath(string wingetId)
    {
        var cacheDir = Path.Combine(ToolkitPaths.GetConfigFolder(ToolName), CacheFolderName);
        return Path.Combine(cacheDir, $"{wingetId}.png");
    }

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
