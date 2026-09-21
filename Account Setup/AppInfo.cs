namespace Account_Setup;

internal static class AppInfo
{
    public const string ScriptVersion = "0.2.0-test";
    public const string DialogTitle = "Elkjøp - Account Setup";
    public const string ToolName = "Account Setup";

    // Phase 5: guards against two Account Setup UI instances existing concurrently - see
    // AccountSetupInstanceLease. Deliberately separate from Toolkit.Shared.RuntimeStaging.MutexName
    // (the Runtime staging mutex) - that one cannot be held across the minutes of technician
    // interaction with SetupWindow (SupportToolStagingService.StageRuntimeCore doesn't acquire it
    // until after this dialog completes), which is exactly the window this dedicated mutex closes.
    public const string SingleInstanceMutexName = @"Global\ElkjopToolkitAccountSetup";
}
