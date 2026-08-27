// System.Drawing is imported explicitly - the csproj removes the WinForms global usings so WPF
// code-behind never sees ambiguous Application/MessageBox/Timer types; this file's Icon is only
// consumed by TrayStatusService's NotifyIcon (the one deliberate WinForms remnant).
using System.Drawing;
using Toolkit.Shared;

namespace Setup_Agent;

internal static class AppInfo
{
    public const string DialogTitle = "Elkjøp - Setup Agent";
    public const string ToolName = "Setup Agent";

    // Must match the /tn value Account Setup's UnattendXmlBuilder registers this task under.
    public const string ScheduledTaskName = "Elkjop Setup Agent";

    // Shared contract with Account Setup - see Toolkit.Shared.AccountSetupStaging. Raw
    // SSID/password, not a pre-built WLAN profile - Setup Agent builds the actual
    // netsh-consumable profile XML itself (see Services/WifiProfileBuilder.cs), fresh on every
    // attempt, so it can keep retrying across every launch without depending on an
    // Account-Setup-authored file it can't regenerate. Only deleted at Setup Agent's own
    // terminal cleanup (Program.DeleteSelf).
    public static readonly string WifiCredentialsPath = AccountSetupStaging.WifiCredentialsPath;

    // Setup-Agent-internal bookkeeping only (not part of the Account Setup contract, so neither
    // belongs in AccountSetupStaging) - lives alongside the other staged files in the same folder.
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
    // Setup Agent.csproj, pointing at the shared Toolkit.Shared/Images/favicon.ico). Since the
    // WPF conversion this only feeds TrayStatusService's NotifyIcon (a System.Drawing.Icon by
    // API) - the WPF windows get their icon via the XAML Icon attribute and the linked
    // Resources/favicon.ico instead.
    public static readonly Icon AppIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application;
}
