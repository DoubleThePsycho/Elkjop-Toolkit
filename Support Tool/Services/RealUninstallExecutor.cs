using Support_Tool.Models;

namespace Support_Tool.Services;

/// <summary>
/// Delegates to AppxUninstallExecutor for Store apps and ProgramUninstallExecutor
/// for registry-registered programs. Requires a live environment to function.
/// </summary>
public class RealUninstallExecutor : IUninstallExecutor
{
    private readonly AppxUninstallExecutor _appxExecutor;
    private readonly ProgramUninstallExecutor _programExecutor;

    public RealUninstallExecutor(IProcessRunner processRunner)
    {
        _appxExecutor = new AppxUninstallExecutor(processRunner);
        _programExecutor = new ProgramUninstallExecutor(processRunner);
    }

    public Task<UninstallResult> UninstallAsync(InstalledItem item, CancellationToken ct = default)
    {
        return item.Kind switch
        {
            InstalledItemKind.App     => _appxExecutor.UninstallAsync(item, ct),
            InstalledItemKind.Program => _programExecutor.UninstallAsync(item, ct),
            _ => Task.FromResult(new UninstallResult
            {
                Item = item,
                Status = UninstallStatus.Skipped,
                ErrorMessage = $"Unsupported item kind: {item.Kind}"
            })
        };
    }
}
