namespace Toolkit.Shared.Configuration;

// Raw JSON shape for a single Installer category. Mapped into the view-facing
// InstallCategory (Name comes from the enclosing dictionary key, not this type).
public class InstallCategoryConfig
{
    public bool InstallAll { get; set; }
    public List<InstallableApp> Apps { get; set; } = [];
}
