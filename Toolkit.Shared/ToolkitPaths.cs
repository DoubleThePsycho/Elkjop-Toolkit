namespace Toolkit.Shared;

public static class ToolkitPaths
{
    // Local (ProgramData) root - nested, space-free segments merging into the same
    // C:\ProgramData\Elkjop\ root already used by other, already-deployed tools outside this
    // solution (2026-08-16 restructure, requested alongside the Bypass OOBE -> Account Setup
    // rename). Deliberately NOT the same as the portable config root below - the two folders
    // were never actually the same path and don't need to move together.
    private const string LocalRootSegment1 = "Elkjop";
    private const string LocalRootSegment2 = "Toolkit";

    // Portable (USB drive root) folder name - unchanged by the 2026-08-16 ProgramData
    // restructure; this is what's already on live technician USB sticks as "Elkjop Toolkit\...".
    private const string ConfigRootFolderName = "Elkjop Toolkit";

    /// <summary>
    /// Per-machine data that stays behind on the PC being worked on — logs, and
    /// anything else tied to that specific machine (e.g. state that must survive
    /// a reboot). Not portable; never travels with the tool.
    /// </summary>
    public static string GetLocalFolder(string toolName)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var folder = Path.Combine(root, LocalRootSegment1, LocalRootSegment2, toolName);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// Portable config that travels with the tool — root of whichever drive the
    /// running executable is on (e.g. a USB stick), identical on every machine visited.
    /// </summary>
    /// <remarks>
    /// Retained for Account Setup, which genuinely still runs from the USB during OOBE. Support
    /// Tool must NOT use this — it always runs from a fixed local Runtime path (see
    /// RuntimeStaging), where AppContext.BaseDirectory has no relationship to wherever the USB
    /// actually is. Support Tool only ever reads its staged local copy at RuntimeStaging.ConfigPath.
    /// </remarks>
    public static string GetConfigFolder(string toolName)
    {
        var driveRoot = Path.GetPathRoot(AppContext.BaseDirectory)!;
        var folder = Path.Combine(driveRoot, ConfigRootFolderName, toolName);
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>
    /// The one unified portable config file — <c>&lt;drive root&gt;\Elkjop Toolkit\config.json</c>
    /// — holding the Account/Connection/Debloater/Installer sections side by side (see
    /// Toolkit.Shared.Configuration.UnifiedToolkitConfig). Tool-name-independent, unlike
    /// <see cref="GetConfigFolder"/>, since it's no longer split per tool. Used only by Account
    /// Setup during OOBE and by the staging layer (SupportToolStagingService, Service Launcher)
    /// to locate the source file to copy into RuntimeStaging.ConfigPath — Support Tool itself
    /// never calls this.
    /// </summary>
    public static string GetPortableConfigPath()
    {
        var driveRoot = Path.GetPathRoot(AppContext.BaseDirectory)!;
        return Path.Combine(driveRoot, ConfigRootFolderName, "config.json");
    }
}
