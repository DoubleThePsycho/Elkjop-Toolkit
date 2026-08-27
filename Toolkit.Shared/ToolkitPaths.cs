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
    public static string GetConfigFolder(string toolName)
    {
        var driveRoot = Path.GetPathRoot(AppContext.BaseDirectory)!;
        var folder = Path.Combine(driveRoot, ConfigRootFolderName, toolName);
        Directory.CreateDirectory(folder);
        return folder;
    }
}
