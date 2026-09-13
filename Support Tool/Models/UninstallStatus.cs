namespace Support_Tool.Models;

public enum UninstallStatus
{
    DryRun,
    Succeeded,

    // The program's native uninstaller succeeded, but the follow-up Revo /mu leftover cleanup
    // either failed or couldn't run (missing metadata, Revo unavailable at that moment, etc.) -
    // the removal itself is still a real success, this is a secondary, non-fatal warning. See
    // RealUninstallExecutor's Program flow for exactly when this is used.
    SucceededWithCleanupWarning,

    // A known/protected Windows component (Get-AppxPackage's own NonRemovable flag, or the
    // reactive 0x80073CFA classification) - never attempted, or attempted and rejected by
    // Windows itself. Distinct from Skipped so the UI can show "Protected Windows component"
    // instead of a generic skip reason, and never counts as "Removed".
    SkippedProtected,

    Failed,
    Skipped,
    Cancelled,
    Started
}
