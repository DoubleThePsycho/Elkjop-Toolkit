using Support_Tool.Models;
using Support_Tool.ViewModels;

namespace Support_Tool.Services;

// Real batch state - Completed/Total is an honest numerator/denominator known up front (the
// selected item count never changes mid-batch), not an invented/animated percentage. Same shape
// as InstallService's own InstallBatchProgress, kept as a separate type since Install and
// Uninstall are separate pipelines with separate result/item types - reported once before each
// item starts (Completed = however many have already finished, CurrentItemName = the one about to
// run) and once more after the very last item finishes (Completed == Total, no further "current"
// item). A failed/skipped/started item still advances Completed exactly like a succeeded one, so
// the batch always reaches 100% once every selected item has been processed one way or another.
public sealed record UninstallBatchProgress(int Completed, int Total, string CurrentItemName);

// Shared by app-uninstall, classic Programs & Features uninstall, and Debloat (both
// ProgramsPageViewModel and DebloaterPageViewModel are driven by the same UninstallService
// instance/pipeline - see App.xaml.cs) - one lease placement here covers all three.
public class UninstallService
{
    private readonly IUninstallExecutor _dryRunExecutor;
    private readonly IUninstallExecutor _realExecutor;
    private readonly SharedViewState _viewState;

    public UninstallService(IUninstallExecutor dryRunExecutor, IUninstallExecutor realExecutor, SharedViewState viewState)
    {
        _dryRunExecutor = dryRunExecutor;
        _realExecutor = realExecutor;
        _viewState = viewState;
    }

    public async Task<List<UninstallResult>> UninstallAsync(
        IEnumerable<InstalledItem> items,
        ExecutionMode mode,
        string pageType,
        IProgress<string>? progress = null,
        IProgress<UninstallBatchProgress>? batchProgress = null,
        CancellationToken ct = default)
    {
        // Held for the whole batch, not per-item and not just around the button click - see
        // InstallService.InstallAsync's identical remark. Covers app uninstall, Programs &
        // Features uninstall, and Debloat alike (all three funnel through this one method).
        using var operation = _viewState.EnterToolkitOperation();

        var executor = mode == ExecutionMode.DryRun ? _dryRunExecutor : _realExecutor;
        var logger = new UninstallLogger(mode, pageType);
        // Materialized once so Total is fixed for the whole batch - items is often a lazily-
        // evaluated LINQ query (e.g. Items.Where(...).Select(...)) at the call site, and it must
        // not be re-enumerated (or have its count change) partway through.
        var itemList = items as IReadOnlyCollection<InstalledItem> ?? items.ToList();
        var total = itemList.Count;
        var results = new List<UninstallResult>(total);

        var completed = 0;
        foreach (var item in itemList)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Processing {item.Name}…");
            batchProgress?.Report(new UninstallBatchProgress(completed, total, item.Name));

            UninstallResult result;
            try
            {
                result = await executor.UninstallAsync(item, progress, ct);
            }
            catch (OperationCanceledException)
            {
                result = new UninstallResult { Item = item, Status = UninstallStatus.Cancelled };
                results.Add(result);
                logger.LogResult(result);
                throw;
            }
            catch (Exception ex)
            {
                result = new UninstallResult
                {
                    Item = item,
                    Status = UninstallStatus.Failed,
                    ErrorMessage = ex.Message
                };
            }

            results.Add(result);
            logger.LogResult(result);
            completed++;
        }

        // Final report - Completed == Total, no "current" item left to name. Only reached once
        // every selected item has genuinely finished (succeeded, failed, skipped, or started); a
        // cancellation throws out of the loop above instead, deliberately never reporting a false
        // 100%.
        batchProgress?.Report(new UninstallBatchProgress(total, total, string.Empty));

        logger.LogSessionEnd(results.Count);
        return results;
    }
}
