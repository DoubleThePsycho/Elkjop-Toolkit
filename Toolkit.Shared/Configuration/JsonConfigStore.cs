using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Toolkit.Shared;

public static class JsonConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<T> LoadOrCreateAsync<T>(string path, Func<T> createDefault) where T : class
    {
        if (!File.Exists(path))
        {
            var defaults = createDefault();
            await SaveAsync(path, defaults);
            return defaults;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<T>(json, Options) ?? createDefault();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[JsonConfigStore] Failed to load '{path}': {ex.Message}");
            return createDefault();
        }
    }

    public static async Task SaveAsync<T>(string path, T value)
    {
        try
        {
            var json = JsonSerializer.Serialize(value, Options);
            await File.WriteAllTextAsync(path, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[JsonConfigStore] Failed to save '{path}': {ex.Message}");
        }
    }
}
