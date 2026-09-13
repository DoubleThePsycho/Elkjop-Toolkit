namespace Support_Tool.Models;

// Which pipeline actually handled a given UninstallResult - purely informational (logging/
// diagnostics), never used to decide the user-facing Status text. See RealUninstallExecutor for
// the actual Native/Revo/NativeThenRevo decision flow this records.
public enum UninstallBackend
{
    Native,
    Revo,
    NativeThenRevo,
}
