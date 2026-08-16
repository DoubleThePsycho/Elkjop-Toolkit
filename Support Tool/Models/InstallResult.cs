namespace Support_Tool.Models;

public class InstallResult
{
    public InstallableApp App { get; init; } = null!;
    public InstallOutcome Status { get; init; }
    public string? ErrorMessage { get; init; }
    public int ExitCode { get; init; }
}
