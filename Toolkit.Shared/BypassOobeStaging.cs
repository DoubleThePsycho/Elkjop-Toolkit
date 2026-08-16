namespace Toolkit.Shared;

/// <summary>
/// Shared contract between Bypass OOBE (writes these files, then reboots) and Setup Agent
/// (reads/consumes them across the reboot) — the two compile separately, so the staging
/// folder/filenames live here once instead of as two independently-maintained copies that
/// could silently drift apart.
///
/// Split across two folders, not one:
/// - <see cref="UnattendPath"/> stays in a flat, space-free folder because it's passed
///   directly on Sysprep.exe's own command line (see Program.cs), and Sysprep's argument
///   parser does a naive whitespace split with no quote-awareness that breaks on any space
///   (confirmed via a real VM's Panther\setuperr.log) - not even quoting the argument helps,
///   since the parser doesn't respect quotes at all.
/// - Everything else lives under the shared, properly-namespaced ProgramData root instead
///   (requested 2026-08-07, tidier alongside the per-tool log folders already living under
///   there) - safe to have spaces in the path since these are only ever referenced from
///   properly-quoted cmd.exe/schtasks.exe command lines within FirstLogonCommands, never
///   passed to Sysprep's own raw command line.
/// </summary>
public static class BypassOobeStaging
{
    // Lazy, not eager readonly fields - C# runs ALL of a type's static field initializers
    // together the first time ANY static member is touched, so an eager CreateFolder() call
    // here would create the Unattend folder even when only a Temp-folder path (e.g. Setup
    // Agent reading WifiCredentialsPath) was ever actually accessed, and vice versa. Each Lazy
    // only creates its own folder the moment something actually asks for a path under it.
    private static readonly Lazy<string> UnattendFolder = new(() => CreateFolder(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BypassOOBE")));

    private static readonly Lazy<string> TempFolder = new(() => CreateFolder(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Elkjop Toolkit", "Temp")));

    public static string UnattendPath => Path.Combine(UnattendFolder.Value, "Unattend.xml");

    // Raw credentials (SSID/password), not a pre-built WLAN profile XML - Setup Agent owns the
    // whole Wi-Fi connection story itself (building the profile, attempting it, retrying), so
    // it needs the source values directly rather than a Bypass-OOBE-authored derivative it
    // can't regenerate or safely retry against once consumed. Requested 2026-08-13: Wi-Fi
    // retries continue at every Setup Agent launch (a laptop's WLAN driver might only arrive
    // via the first Windows Update) until Setup Agent's own terminal cleanup, so the
    // credentials can't be a one-shot file that gets deleted after a single attempt.
    public static string WifiCredentialsPath => Path.Combine(TempFolder.Value, "WifiCredentials.json");
    public static string SetupAgentExePath => Path.Combine(TempFolder.Value, "SetupAgent.exe");
    public static string SetupAgentTaskXmlPath => Path.Combine(TempFolder.Value, "SetupAgentTask.xml");

    private static string CreateFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        return folder;
    }
}
