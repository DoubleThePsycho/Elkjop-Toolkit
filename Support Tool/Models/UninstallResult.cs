namespace Support_Tool.Models;

public class UninstallResult
{
    public InstalledItem Item { get; init; } = null!;
    public UninstallStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
    public int ExitCode { get; init; }
}
