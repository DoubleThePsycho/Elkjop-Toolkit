namespace Toolkit.Shared.Configuration;

// One named preset of catalog WingetIds to preselect on the Install page for the Setup workflow -
// see InstallerConfig.SetupPresets and Support Tool's SetupHomePageViewModel. Config-driven so shop
// policy (which apps ship in "SA Standard" vs "SA Gaming") lives in config.json, not UI code -
// adding a new preset, or extending an existing one's Apps list, requires no code change.
//
// A WingetId here selects every catalog entry that carries it, even across categories - this is
// intentional, not a bug: two entries may legitimately share one WingetId to represent different
// installable architectures (e.g. "Java 8 (x86)" / "Java 8 (x64)" both under
// Amazon.Corretto.8.JRE), and listing that one shared ID once selects both.
public class SetupPresetConfig
{
    public List<string> WingetIds { get; set; } = [];

    // Optional, additive (external review). Every app currently present in each named Installer
    // category is included in this preset's selection alongside WingetIds - resolved by category
    // NAME, not a frozen snapshot of app IDs, so a launcher added to (or removed from) that
    // category later automatically participates with no config or code change here. A configured
    // name that doesn't match any existing category is logged as a warning and otherwise ignored
    // (see SetupHomePageViewModel.ApplyPresetAsync) - never a crash, never silently required.
    public List<string> CategoryNames { get; set; } = [];
}
