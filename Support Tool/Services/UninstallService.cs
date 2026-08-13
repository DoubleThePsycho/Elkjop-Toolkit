using Support_Tool.Models;

namespace Support_Tool.Services;

public class UninstallService
{
    private readonly IUninstallExecutor _dryRunExecutor;
    private readonly IUninstallExecutor _realExecutor;

    public UninstallService(IUninstallExecutor dryRunExecutor, IUninstallExecutor realExecutor)
    {
        _dryRunExecutor = dryRunExecutor;
        _realExecutor = realExecutor;
    }

    public async Task<List<UninstallResult>> UninstallAsync(
        IEnumerable<InstalledItem> items,
        ExecutionMode mode,
        string pageType,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
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
