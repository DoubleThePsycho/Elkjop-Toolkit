using System.Diagnostics;
using Support_Tool.Models;

namespace Support_Tool.Services;

public class DryRunUninstallExecutor : IUninstallExecutor
{
    public async Task<UninstallResult> UninstallAsync(InstalledItem item, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        Debug.WriteLine($"[DRY RUN] Would uninstall: {item.Name} ({item.Kind})");
        await Task.Delay(400, ct);
        return new UninstallResult { Item = item, Status = UninstallStatus.DryRun };
    }
}
