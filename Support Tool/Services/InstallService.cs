using Support_Tool.Models;
using Support_Tool.ViewModels;
using Toolkit.Shared.Configuration;

namespace Support_Tool.Services;

public class InstallService
{
    private readonly IInstallExecutor _dryRunExecutor;
    private readonly IInstallExecutor _realExecutor;
    private readonly SharedViewState _viewState;

    public InstallService(IInstallExecutor dryRunExecutor, IInstallExecutor realExecutor, SharedViewState viewState)
    {
        _dryRunExecutor = dryRunExecutor;
        _realExecutor = realExecutor;
        _viewState = viewState;
    }

    public async Task<List<InstallResult>> InstallAsync(
        IEnumerable<(InstallableApp App, AppInstallState State)> items,
        ExecutionMode mode,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        // Held for the whole batch (every item this call installs), not per-item and not just
        // around the button click - a reboot mid-batch is exactly as unsafe as one mid-item, and
        // covers both the real and DryRun executor (see field selection below), since a close/
        // reboot during a DEBUG dry-run simulation is exactly the kind of state this is meant to
        // protect against too. Deliberately not IsWuaOperationActive - unrelated to Windows
        // Update, this exists purely so WindowsUpdateLifecycleCoordinator can avoid rebooting out
        // from under this install.
        using var operation = _viewState.EnterToolkitOperation();

        var executor = mode == ExecutionMode.DryRun ? _dryRunExecutor : _realExecutor;
        var logger = new InstallLogger(mode);
        var results = new List<InstallResult>();

        foreach (var (app, state) in items)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Processing {app.Name}…");

            InstallResult result;
            try
            {
                result = await executor.InstallAsync(app, state, ct);
            }
            catch (OperationCanceledException)
            {
                result = new InstallResult { App = app, Status = InstallOutcome.Cancelled };
                results.Add(result);
                logger.LogResult(result);
                throw;
            }
            catch (Exception ex)
            {
                result = new InstallResult
                {
                    App = app,
                    Status = InstallOutcome.Failed,
                    ErrorMessage = ex.Message
                };
            }

            results.Add(result);
            logger.LogResult(result);
        }

        logger.LogSessionEnd(results.Count);
        return results;
    }
}
