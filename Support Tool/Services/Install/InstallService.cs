using Support_Tool.Models;
using Support_Tool.ViewModels;
using Toolkit.Shared.Configuration;

namespace Support_Tool.Services;

// Real batch state - Completed/Total is an honest numerator/denominator known up front (the
// selected item count never changes mid-batch), not an invented/animated percentage. Reported
// once before each item starts (Completed = however many have already finished, CurrentItemName =
// the one about to run, CurrentItemPercent = 0) and once more after the very last item finishes
// (Completed == Total, no further "current" item, CurrentItemPercent = 0) - a failed item still
// advances Completed exactly like a succeeded one, so the batch always reaches 100% once every
// selected item has been processed one way or another. Between those two bookends, CurrentItemPercent
// (0-100) is re-reported every time the executor learns real intra-item progress for the item
// currently running, so InstallPageViewModel can blend it into a smoothly-advancing overall
// percentage instead of only moving once per whole item - see InstallPageViewModel.InstallProgressPercent.
public sealed record InstallBatchProgress(int Completed, int Total, string CurrentItemName, double CurrentItemPercent = 0);

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
        IProgress<InstallBatchProgress>? batchProgress = null,
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
        var itemList = items as IReadOnlyCollection<(InstallableApp App, AppInstallState State)> ?? items.ToList();
        var total = itemList.Count;
        var results = new List<InstallResult>(total);

        var completed = 0;
        foreach (var (app, state) in itemList)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Processing {app.Name}…");
            batchProgress?.Report(new InstallBatchProgress(completed, total, app.Name));

            // Rebuilds and re-reports the full InstallBatchProgress on every real intra-item
            // update - completed/total/name are all closed-over loop state that stays fixed for
            // this item's whole run, only CurrentItemPercent actually changes per callback.
            var itemCompleted = completed;
            var itemProgress = new Progress<double>(percent =>
                batchProgress?.Report(new InstallBatchProgress(itemCompleted, total, app.Name, percent)));

            InstallResult result;
            try
            {
                result = await executor.InstallAsync(app, state, itemProgress, ct);
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
            completed++;
        }

        // Final report - Completed == Total, no "current" item left to name. Only reached once
        // every selected item has genuinely finished (success or failure); a cancellation throws
        // out of the loop above instead, deliberately never reporting a false 100%.
        batchProgress?.Report(new InstallBatchProgress(total, total, string.Empty));

        logger.LogSessionEnd(results.Count);
        return results;
    }
}
