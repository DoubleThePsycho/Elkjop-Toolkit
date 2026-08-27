namespace Toolkit.Shared.Configuration;

public class InstallableApp
{
    public string Name { get; init; } = "";
    public string WingetId { get; init; } = "";
    public bool Preselected { get; init; }

    // Homepage URL used to resolve the app's favicon at runtime (see AppIconCacheService).
    public string Link { get; init; } = "";
}
