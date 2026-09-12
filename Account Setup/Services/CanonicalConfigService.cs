using System.Text.Json;
using Toolkit.Shared;
using Toolkit.Shared.Configuration;

namespace Account_Setup.Services;

// Phase 4B: the canonical unified config is now load-bearing for the live OOBE path (it's staged
// verbatim into Runtime\config.json for Support Tool to run against), so loading it is strict -
// missing/corrupt/incomplete is fatal, never a silently-manufactured default. Deliberately does
// NOT use JsonConfigStore.LoadOrCreateAsync (its "create a default and continue" behavior is
// exactly what strict loading here must not do) and never falls back to either legacy per-tool
// config file.
internal sealed class LoadedToolkitConfig
{
    public required UnifiedToolkitConfig Config { get; init; }

    // The exact text that was successfully validated below - staged verbatim into
    // Runtime\config.json by SupportToolStagingService, rather than re-reading the source file a
    // second time (which could observe a changed file between validation and staging). Ensures
    // "the config Account Setup used" and "the config staged for Support Tool" are always
    // byte-for-byte the same document for the whole run.
    public required string RawJson { get; init; }

    // The exact portable path this was validated from (e.g. "F:\Elkjop Toolkit\config.json") -
    // carried forward so SupportToolStagingService can record the originating USB volume in
    // Runtime\source.json without independently re-deriving ToolkitPaths.GetPortableConfigPath()
    // a second time.
    public required string SourcePath { get; init; }
}

internal static class CanonicalConfigService
{
    private static readonly string[] RequiredSections = ["Account", "Connection", "Debloater", "Installer"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static LoadedToolkitConfig Load()
    {
        var path = ToolkitPaths.GetPortableConfigPath();

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"The canonical configuration file was not found at '{path}'.");
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to read the canonical configuration file '{path}': {ex.Message}", ex);
        }

        // A structural check ahead of deserialization - every UnifiedToolkitConfig section has a
        // default initializer, so e.g. "{}" would otherwise deserialize into a seemingly-valid
        // (but entirely empty/default) config with no error at all. Case-insensitive to match
        // JsonOptions below.
        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"The canonical configuration file '{path}' does not contain a JSON object at its root.");
            }

            foreach (var section in RequiredSections)
            {
                if (!HasPropertyIgnoreCase(document.RootElement, section))
                {
                    throw new InvalidOperationException($"The canonical configuration file '{path}' is missing its required '{section}' section.");
                }
            }
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"The canonical configuration file '{path}' is not valid JSON: {ex.Message}", ex);
        }

        UnifiedToolkitConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<UnifiedToolkitConfig>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"The canonical configuration file '{path}' could not be parsed: {ex.Message}", ex);
        }

        if (config is null)
        {
            throw new InvalidOperationException($"The canonical configuration file '{path}' deserialized to nothing.");
        }

        return new LoadedToolkitConfig { Config = config, RawJson = json, SourcePath = path };
    }

    private static bool HasPropertyIgnoreCase(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
