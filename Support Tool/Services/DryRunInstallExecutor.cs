using System.Diagnostics;
using Support_Tool.Models;
using Toolkit.Shared.Configuration;

namespace Support_Tool.Services;

public class DryRunInstallExecutor : IInstallExecutor
{
    public async Task<InstallResult> InstallAsync(InstallableApp app, AppInstallState currentState, CancellationToken ct = default)
    {
        Debug.WriteLine($"[DRY RUN] Would install/update: {app.Name} ({app.WingetId})");
        await Task.Delay(400, ct);
        return new InstallResult { App = app, Status = InstallOutcome.DryRun };
    }
}
