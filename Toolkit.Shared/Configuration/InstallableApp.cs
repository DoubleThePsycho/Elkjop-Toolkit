namespace Toolkit.Shared.Configuration;

public class InstallableApp
{
    public string Name { get; init; } = "";
    public string WingetId { get; init; } = "";
    public bool Preselected { get; init; }

    // Homepage URL used to resolve the app's favicon at runtime (see AppIconCacheService).
    public string Link { get; init; } = "";

    // Optional. Some winget manifests (e.g. Blizzard.BattleNet) require an explicit install
    // location and fail with APPINSTALLER_CLI_ERROR_INSTALL_LOCATION_REQUIRED
    // (0x8A15005F / -1978335137) without one - there is no generic way to detect this from
    // winget itself ahead of time, so it's a per-app config value rather than something
    // RealInstallExecutor infers. May contain environment-variable references (e.g.
    // "%PROGRAMFILES(X86)%\Battle.net") - RealInstallExecutor expands them before passing
    // `--location` to winget. Null/empty (the default for every existing entry) means "no
    // explicit location" and preserves the exact command used before this property existed.
    public string? InstallLocation { get; init; }
}
