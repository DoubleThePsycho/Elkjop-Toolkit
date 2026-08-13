using Toolkit.Shared;

namespace Bypass_OOBE;

internal static class AppInfo
{
    public const string ScriptVersion = "0.2.0-test";
    public const string DialogTitle = "Elkjøp - Bypass OOBE";
    public const string ToolName = "Bypass OOBE";

    // Staging paths are a shared contract with the Setup Agent project (which reads these
    // same files after the reboot Bypass OOBE triggers) - see Toolkit.Shared.BypassOobeStaging
    // for why the folder is flat/space-free and why it's centralized instead of duplicated.
    public static readonly string UnattendPath = BypassOobeStaging.UnattendPath;
    public static readonly string WifiCredentialsPath = BypassOobeStaging.WifiCredentialsPath;
    public static readonly string SetupAgentExePath = BypassOobeStaging.SetupAgentExePath;
    public static readonly string SetupAgentTaskXmlPath = BypassOobeStaging.SetupAgentTaskXmlPath;
}
