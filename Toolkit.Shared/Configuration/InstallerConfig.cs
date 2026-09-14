namespace Toolkit.Shared.Configuration;

public class InstallerConfig
{
    public Dictionary<string, InstallCategoryConfig> Categories { get; set; } = [];

    // Optional, additive - absent entirely from any config.json written before this feature
    // existed. Keyed by preset display name (e.g. "SA Standard", "SA Gaming"). ConfigService
    // synthesizes built-in defaults in memory when this is empty, so an old physical USB
    // config.json keeps loading exactly as before, just without custom presets until re-saved.
    public Dictionary<string, SetupPresetConfig> SetupPresets { get; set; } = [];
}
