using Support_Tool.Models;
using Support_Tool.ViewModels;

namespace Support_Tool.Services;

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
        CancellationToken ct = default)
    {
        // Held for the whole batch, not per-item and not just around the button click - see
        // InstallService.InstallAsync's identical remark. Covers app uninstall, Programs &
        // Features uninstall, and Debloat alike (all three funnel through this one method).
        using var operation = _viewState.EnterToolkitOperation();

        var executor = mode == ExecutionMode.DryRun ? _dryRunExecutor : _realExecutor;
        var logger = new UninstallLogger(mode, pageType);
        var results = new List<UninstallResult>();

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Processing {item.Name}…");

            UninstallResult result;
            try
            {
                result = await executor.UninstallAsync(item, ct);
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
        }

        logger.LogSessionEnd(results.Count);
        return results;
    }
}
