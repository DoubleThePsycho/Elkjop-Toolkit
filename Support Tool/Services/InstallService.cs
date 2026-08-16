using Support_Tool.Models;

namespace Support_Tool.Services;

public class InstallService
{
    private readonly IInstallExecutor _dryRunExecutor;
    private readonly IInstallExecutor _realExecutor;

    public InstallService(IInstallExecutor dryRunExecutor, IInstallExecutor realExecutor)
    {
        _dryRunExecutor = dryRunExecutor;
        _realExecutor = realExecutor;
    }

    public async Task<List<InstallResult>> InstallAsync(
        IEnumerable<(InstallableApp App, AppInstallState State)> items,
        ExecutionMode mode,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
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
