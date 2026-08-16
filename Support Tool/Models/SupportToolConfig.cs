namespace Support_Tool.Models;

// Root shape of config.json — everything the tool persists lives under one of
// these two keys so a single ConfigService can own the whole file without two
// independent readers/writers clobbering each other's section.
public class SupportToolConfig
{
    public RuleSet Debloater { get; set; } = new();
    public InstallerConfig Installer { get; set; } = new();
}
