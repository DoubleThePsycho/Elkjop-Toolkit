using Toolkit.Shared;

namespace Account_Setup;

internal static class AppInfo
{
    public const string ScriptVersion = "0.2.0-test";
    public const string DialogTitle = "Elkjøp - Account Setup";
    public const string ToolName = "Account Setup";

    // Phase 4B: rollback-only. Setup Agent is no longer the live post-OOBE path (see
    // SupportToolStagingService/UnattendXmlBuilder, which now stage/register Support Tool under
    // Toolkit.Shared.RuntimeStaging instead) - these three remain only because Setup Agent
    // staging is intentionally kept as a manual-fallback option through Phase 4B's testing
    // period (see Program.cs's own call sites). Removed once Phase 5 deletes Setup Agent
    // entirely. UnattendPath itself was removed from here - the live answer file is now
    // Toolkit.Shared.RuntimeStaging.UnattendPath.
    public static readonly string WifiCredentialsPath = AccountSetupStaging.WifiCredentialsPath;
    public static readonly string SetupAgentExePath = AccountSetupStaging.SetupAgentExePath;
    public static readonly string SetupAgentTaskXmlPath = AccountSetupStaging.SetupAgentTaskXmlPath;
}
