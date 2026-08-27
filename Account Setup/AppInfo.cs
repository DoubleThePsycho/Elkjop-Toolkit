using Toolkit.Shared;

namespace Account_Setup;

internal static class AppInfo
{
    public const string ScriptVersion = "0.2.0-test";
    public const string DialogTitle = "Elkjøp - Account Setup";
    public const string ToolName = "Account Setup";

    // Staging paths are a shared contract with the Setup Agent project (which reads these
    // same files after the reboot Account Setup triggers) - see Toolkit.Shared.AccountSetupStaging
    // for why the folder is space-free and why it's centralized instead of duplicated.
    public static readonly string UnattendPath = AccountSetupStaging.UnattendPath;
    public static readonly string WifiCredentialsPath = AccountSetupStaging.WifiCredentialsPath;
    public static readonly string SetupAgentExePath = AccountSetupStaging.SetupAgentExePath;
    public static readonly string SetupAgentTaskXmlPath = AccountSetupStaging.SetupAgentTaskXmlPath;
}
