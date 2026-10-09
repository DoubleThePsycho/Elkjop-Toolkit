using Support_Tool.Models;

namespace Support_Tool.Services;

public interface IUninstallExecutor
{
    // progress carries free-text phase updates for the item currently running (e.g. "Uninstalling
    // McAfee…" then "Cleaning leftovers for McAfee…") - the same shared channel
    // UninstallService already threads through to StatusMessage, not a new progress model.
    Task<UninstallResult> UninstallAsync(InstalledItem item, IProgress<string>? progress = null, CancellationToken ct = default);
}
