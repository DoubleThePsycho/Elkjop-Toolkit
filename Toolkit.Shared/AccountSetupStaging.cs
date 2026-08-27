namespace Toolkit.Shared;

/// <summary>
/// Shared contract between Account Setup (writes these files, then reboots) and Setup Agent
/// (reads/consumes them across the reboot) — the two compile separately, so the staging
/// folder/filenames live here once instead of as two independently-maintained copies that
/// could silently drift apart.
///
/// All of it - including <see cref="UnattendPath"/> - now lives in one folder:
/// <c>C:\ProgramData\Elkjop\Toolkit\Temp\</c> (2026-08-16 restructure). Unattend.xml used to
/// live in its own separate flat folder (<c>C:\ProgramData\BypassOOBE\</c>) specifically
/// because it's passed directly on Sysprep.exe's own command line (see Account Setup's
/// Program.cs), and Sysprep's argument parser does a naive whitespace split with no
/// quote-awareness that breaks on any space (confirmed via a real VM's Panther\setuperr.log) -
/// not even quoting the argument helps, since the parser doesn't respect quotes at all. That
/// constraint hasn't gone away, but the nested "Elkjop\Toolkit\Temp" nested root has no spaces
/// in any of its segments either, so Unattend.xml can now share the same Temp staging folder
/// everything else already uses instead of needing a folder of its own.
/// </summary>
public static class AccountSetupStaging
{
    private static readonly Lazy<string> TempFolder = new(() => CreateFolder(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Elkjop", "Toolkit", "Temp")));

    public static string UnattendPath => Path.Combine(TempFolder.Value, "Unattend.xml");

    // Raw credentials (SSID/password), not a pre-built WLAN profile XML - Setup Agent owns the
    // whole Wi-Fi connection story itself (building the profile, attempting it, retrying), so
    // it needs the source values directly rather than an Account-Setup-authored derivative it
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
