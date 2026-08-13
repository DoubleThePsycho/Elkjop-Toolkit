namespace Toolkit.Shared;

public static class ToolkitPaths
{
    private const string RootFolderName = "Elkjop Toolkit";

    /// <summary>
    /// Per-machine data that stays behind on the PC being worked on — logs, and
    /// anything else tied to that specific machine (e.g. state that must survive
    /// a reboot). Not portable; never travels with the tool.
    /// </summary>
    public static string GetLocalFolder(string toolName)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var folder = Path.Combine(root, RootFolderName, toolName);
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
        var folder = Path.Combine(driveRoot, RootFolderName, toolName);
        Directory.CreateDirectory(folder);
        return folder;
    }
}
