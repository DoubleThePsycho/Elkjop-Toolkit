using Support_Tool.Models;
using Toolkit.Shared;

namespace Support_Tool.Services;

public class UninstallLogger
{
    private const string ToolName = "Support Tool";

    private readonly ToolkitLogger _logger = new(ToolName);

    public UninstallLogger(ExecutionMode mode, string pageType)
    {
        _logger.Info("=== Elkjøp Support Tool — Uninstall Session ===");
        _logger.Info($"Mode: {mode}");
        _logger.Info($"Page: {pageType}");
    }

    public void LogResult(UninstallResult result)
    {
        var kind = result.Item.Kind.ToString();
        var description = $"Attempted to uninstall {kind}: {result.Item.Name}";
        var outcome = DescribeOutcome(result);

        _logger.Action(description, outcome);
    }

    public void LogSessionEnd(int total) =>
        _logger.Info($"Session complete. Total items processed: {total}");

    private static string DescribeOutcome(UninstallResult result)
    {
        var outcome = result.Status switch
        {
            UninstallStatus.DryRun    => "Simulated (dry run) — no software was removed",
            UninstallStatus.Succeeded => "Success",
            UninstallStatus.Failed    => $"Failed — {result.ErrorMessage ?? "unknown error"}",
            UninstallStatus.Skipped   => $"Skipped — {result.ErrorMessage ?? "no reason given"}",
            UninstallStatus.Cancelled => "Cancelled by user",
            UninstallStatus.Started   => "Uninstaller started — verify removal after reboot",
            _                         => result.Status.ToString()
        };

        if (result.ExitCode != 0 && result.Status != UninstallStatus.DryRun)
            outcome += $" (exit code {result.ExitCode})";

        return outcome;
    }
}
