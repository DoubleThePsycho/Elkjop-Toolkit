using Toolkit.Shared;

namespace Setup_Agent;

internal static class AppInfo
{
    public const string DialogTitle = "Elkjøp - Setup Agent";
    public const string ToolName = "Setup Agent";

    // Must match the /tn value Bypass OOBE's UnattendXmlBuilder registers this task under.
    public const string ScheduledTaskName = "Elkjop Setup Agent";

    // Shared contract with Bypass OOBE - see Toolkit.Shared.BypassOobeStaging. Raw SSID/password,
    // not a pre-built WLAN profile - Setup Agent builds the actual netsh-consumable profile XML
    // itself (see Services/WifiProfileBuilder.cs), fresh on every attempt, so it can keep
    // retrying across every launch without depending on a Bypass-OOBE-authored file it can't
    // regenerate. Only deleted at Setup Agent's own terminal cleanup (Program.DeleteSelf).
    public static readonly string WifiCredentialsPath = BypassOobeStaging.WifiCredentialsPath;

    // Setup-Agent-internal bookkeeping only (not part of the Bypass OOBE contract, so neither
    // belongs in BypassOobeStaging) - lives alongside the other staged files in the same folder.
    private static readonly string TempFolder = Path.GetDirectoryName(WifiCredentialsPath)!;

    // Tells Program.cs "this is the very first launch" apart from every later relaunch across
    // the Windows Update reboot cycle.
    public static readonly string FirstRunMarkerPath = Path.Combine(TempFolder, "SetupAgentFirstRun.marker");

    // Transient WLAN profile XML netsh actually reads (its "add profile" command needs a real
    // file path) - rebuilt fresh from WifiCredentialsPath on every connection attempt and
    // deleted right after that attempt, regardless of outcome. Not the same file as
    // WifiCredentialsPath, which persists across attempts.
    public static readonly string WifiProfileXmlPath = Path.Combine(TempFolder, "WifiProfile.xml");

    // Extracted once from the running exe's own embedded icon (set via <ApplicationIcon> in
    // Setup Agent.csproj, pointing at the shared Toolkit.Shared/Images/favicon.ico) - cached
    // rather than re-extracted per form/tray icon. WinForms doesn't apply a project's
    // ApplicationIcon to individual Form title bars or the taskbar automatically; each window
    // (and the NotifyIcon) needs this assigned explicitly.
    public static readonly Icon AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
}
