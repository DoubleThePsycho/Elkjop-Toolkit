using System.Diagnostics;
using Support_Tool.Models;
using Toolkit.Shared.Configuration;

namespace Support_Tool.Services;

public class DryRunInstallExecutor : IInstallExecutor
{
    // Deliberately never reports through itemProgress - Dry Run simulates deterministic item
    // completion (the batch-level Completed/Total step still advances normally in InstallService),
    // but it has no real download/install happening to report a trustworthy intra-item percentage
    // for, and inventing one would contradict the "never invent movement" rule real progress
    // follows in RealInstallExecutor.
    public async Task<InstallResult> InstallAsync(InstallableApp app, AppInstallState currentState, IProgress<double>? itemProgress = null, CancellationToken ct = default)
    {
        Debug.WriteLine($"[DRY RUN] Would install/update: {app.Name} ({app.WingetId})");
        await Task.Delay(400, ct);
        return new InstallResult { App = app, Status = InstallOutcome.DryRun };
    }
}
