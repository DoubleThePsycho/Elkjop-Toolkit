namespace Toolkit.Shared.Configuration;

// Root shape of the one portable config.json (<USB root>\Elkjop Toolkit\config.json) and its
// staged copy (Runtime\config.json). Supersedes Account Setup's old AppConfig and Support
// Tool's old SupportToolConfig - both tools now deserialize/reserialize the whole document
// through this single type, each only ever reading/mutating the sections it owns (Account Setup:
// Account/Connection; Support Tool: Connection/Debloater/Installer). This is deliberate: two
// independent narrower DTOs would each silently drop the sections they don't know about the
// moment either one serialized a write, which is exactly the failure JsonConfigStore's
// single-owner design already guards against for Debloater vs. Installer - that concern now
// spans all four sections instead of two, so there can only be one root type.
public sealed class UnifiedToolkitConfig
{
    public AccountConfig Account { get; set; } = new();
    public ConnectionConfig Connection { get; set; } = new();
    public RuleSet Debloater { get; set; } = new();
    public InstallerConfig Installer { get; set; } = new();
}
