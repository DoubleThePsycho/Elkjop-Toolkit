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

    // Optional. Some winget packages ship separate x86/x64 installers under one shared package ID,
    // selectable only via `--architecture` (e.g. Amazon.Corretto.8.JRE - verified via `winget show
    // --architecture x86/x64` returning distinct real installer URLs for each). Generic, not tied
    // to any specific app: RealInstallExecutor appends `--architecture <value>` only when this is
    // set. Two catalog entries may legitimately share the same WingetId with different Architecture
    // values (e.g. "Java 8 (x86)" / "Java 8 (x64)") - see RealInstallExecutor and
    // WingetStatusChecker's own remarks for the one known limitation this creates (winget's own
    // `list` command has no --architecture filter, so both rows report the same install state).
    public string? Architecture { get; init; }
}
