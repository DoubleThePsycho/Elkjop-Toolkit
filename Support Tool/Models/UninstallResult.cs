namespace Support_Tool.Models;

public class UninstallResult
{
    public InstalledItem Item { get; init; } = null!;
    public UninstallStatus Status { get; init; }

    // General-purpose detail text for this status - a skip reason, a failure message, or (for
    // SucceededWithCleanupWarning/SkippedProtected) the concise user-facing explanation itself.
    public string? ErrorMessage { get; init; }
    public int ExitCode { get; init; }

    // Diagnostics only - never drives user-facing text (see UninstallBackend's own remarks).
    public UninstallBackend Backend { get; init; } = UninstallBackend.Native;
}
