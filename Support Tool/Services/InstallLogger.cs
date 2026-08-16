using Support_Tool.Models;
using Toolkit.Shared;

namespace Support_Tool.Services;

public class InstallLogger
{
    private const string ToolName = "Support Tool";

    private readonly ToolkitLogger _logger = new(ToolName);

    public InstallLogger(ExecutionMode mode)
    {
        _logger.Info("=== Elkjøp Support Tool — Install Session ===");
        _logger.Info($"Mode: {mode}");
    }

    public void LogResult(InstallResult result)
    {
        var description = $"Attempted to install/update: {result.App.Name} ({result.App.WingetId})";
        var outcome = DescribeOutcome(result);

        _logger.Action(description, outcome);
    }

    public void LogSessionEnd(int total) =>
        _logger.Info($"Session complete. Total items processed: {total}");

    private static string DescribeOutcome(InstallResult result)
    {
        var outcome = result.Status switch
        {
            InstallOutcome.DryRun     => "Simulated (dry run) — no software was installed",
            InstallOutcome.Succeeded  => "Success",
            InstallOutcome.Failed     => $"Failed — {result.ErrorMessage ?? "unknown error"}",
            InstallOutcome.Cancelled  => "Cancelled by user",
            _                         => result.Status.ToString()
        };

        if (result.ExitCode != 0 && result.Status != InstallOutcome.DryRun)
            outcome += $" (exit code {result.ExitCode})";

        return outcome;
    }
}
