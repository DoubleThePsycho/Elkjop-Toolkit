using Support_Tool.Models;
using Toolkit.Shared.Configuration;

namespace Support_Tool.Services;

public interface IInstallExecutor
{
    // itemProgress (0-100) reports real, trustworthy intra-item progress as it becomes known -
    // see RealInstallExecutor for what "trustworthy" means. Optional: a caller that only cares
    // about the final result (or an executor with no meaningful intra-item signal, like
    // DryRunInstallExecutor) simply never reports through it.
    Task<InstallResult> InstallAsync(InstallableApp app, AppInstallState currentState, IProgress<double>? itemProgress = null, CancellationToken ct = default);
}
