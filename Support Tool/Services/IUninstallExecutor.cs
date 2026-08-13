using Support_Tool.Models;

namespace Support_Tool.Services;

public interface IUninstallExecutor
{
    Task<UninstallResult> UninstallAsync(InstalledItem item, CancellationToken ct = default);
}
