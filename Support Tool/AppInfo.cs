namespace Support_Tool;

internal static class AppInfo
{
    // Matches the ToolName const already duplicated locally in ConfigService/AppIconCacheService/
    // InstallLogger/UninstallLogger - not consolidated onto this in Phase 1 (out of scope for a
    // behavior-preserving port), but new code (Logger.cs, the ported Windows Update/connectivity
    // services) uses this one shared constant instead of adding a fifth local copy.
    public const string ToolName = "Support Tool";
}
