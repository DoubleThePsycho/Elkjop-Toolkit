using Support_Tool.Models;

namespace Support_Tool.Services;

public interface IInstallExecutor
{
    Task<InstallResult> InstallAsync(InstallableApp app, AppInstallState currentState, CancellationToken ct = default);
}
