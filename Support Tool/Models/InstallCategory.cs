namespace Support_Tool.Models;

public class InstallCategory
{
    public string Name { get; init; } = "";
    public bool ShowSelectAll { get; init; }
    public List<InstallableApp> Apps { get; init; } = [];
}
