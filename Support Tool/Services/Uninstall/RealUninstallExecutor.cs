using Support_Tool.Models;

namespace Support_Tool.Services;

/// <summary>
/// Delegates to AppxUninstallExecutor for Store apps and ProgramUninstallExecutor for registry-
/// registered programs, integrating Revo Uninstaller Pro Portable where it can safely improve
/// removal - see the two backend-specific methods below for the exact flow. Revo is always
/// optional: RevoUninstallService.IsAvailable being false (USB disconnected, Revo missing, wrong
/// architecture, etc.) never blocks an uninstall, it just means the native-only path is used.
/// </summary>
public class RealUninstallExecutor : IUninstallExecutor
{
    private readonly AppxUninstallExecutor _appxExecutor;
    private readonly ProgramUninstallExecutor _programExecutor;
    private readonly RevoUninstallService _revo;

    public RealUninstallExecutor(IProcessRunner processRunner, RevoUninstallService revo)
    {
        _appxExecutor = new AppxUninstallExecutor(processRunner);
        _programExecutor = new ProgramUninstallExecutor(processRunner);
        _revo = revo;
    }

    public Task<UninstallResult> UninstallAsync(InstalledItem item, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        return item.Kind switch
        {
            InstalledItemKind.App     => UninstallAppAsync(item, progress, ct),
            InstalledItemKind.Program => UninstallProgramAsync(item, progress, ct),
            _ => Task.FromResult(new UninstallResult
            {
                Item = item,
                Status = UninstallStatus.Skipped,
                ErrorMessage = $"Unsupported item kind: {item.Kind}"
            })
        };
    }

    // Windows App flow (see module doc-comment header for the full decision table):
    //   protected? -> SkippedProtected, never attempted
    //   Revo available? -> try /wa (starts the app's own uninstall, then auto-cleans leftovers),
    //       then ALWAYS verify via PackageFullName regardless of what Revo's own exit code said -
    //       that exit code is not a confirmed-trustworthy success/failure contract (see
    //       RevoUninstallService's own remarks), but package presence is a real, strong signal:
    //         package now absent, Revo exit 0   -> Succeeded (Revo cleanly did the whole job)
    //         package now absent, Revo exit !=0 -> Succeeded, but Revo's own cleanup phase
    //                                              self-reported a problem after the app was
    //                                              already gone -> SucceededWithCleanupWarning
    //         package still present (or presence couldn't be verified) -> Revo attempt did not
    //                                              provably finish the job -> one native Appx
    //                                              fallback (never bounce more than once)
    //   Revo unavailable -> native Appx directly
    private async Task<UninstallResult> UninstallAppAsync(InstalledItem item, IProgress<string>? progress, CancellationToken ct)
    {
        if (item.IsProtectedSystemComponent)
        {
            return new UninstallResult
            {
                Item = item,
                Status = UninstallStatus.SkippedProtected,
                ErrorMessage = "Protected Windows component",
                Backend = UninstallBackend.Native
            };
        }

        if (_revo.IsAvailable)
        {
            progress?.Report($"Removing {item.Name} via Revo…");
            var revoResult = await _revo.RemoveWindowsAppAsync(item.Name, ct);
            var stillPresent = await _appxExecutor.IsPackagePresentAsync(item.PackageFullName, ct);

            if (stillPresent == false)
            {
                return revoResult.Outcome == RevoOutcome.Success
                    ? new UninstallResult { Item = item, Status = UninstallStatus.Succeeded, Backend = UninstallBackend.Revo }
                    : new UninstallResult
                    {
                        Item = item,
                        Status = UninstallStatus.SucceededWithCleanupWarning,
                        ErrorMessage = "Cleanup warning: Revo reported a problem after the app was already removed.",
                        Backend = UninstallBackend.Revo
                    };
            }

            // Either genuinely still present, or presence couldn't be verified - conservatively
            // treat both as "not provably finished" and fall back. Attempting the native Appx
            // removal here is safe even if the package turns out to already be gone
            // (Remove-AppxPackage against an absent package is a harmless no-op/clear failure,
            // never destructive) - one native fallback maximum, never bounce back and forth.
            progress?.Report($"Revo could not remove {item.Name}; trying built-in removal…");
        }

        var native = await _appxExecutor.UninstallAsync(item, progress, ct);
        return WithBackend(native, UninstallBackend.Native);
    }

    // Program flow (see module doc-comment header): native uninstaller ALWAYS runs first - Revo's
    // own documentation states /mu does not launch a program's built-in uninstaller, so it can
    // never replace this step, only clean up after it. Revo cleanup is only ever attempted once
    // native uninstall itself has succeeded, using metadata (install path, architecture) captured
    // at inventory time before the program's registry entry is removed. A Revo cleanup problem is
    // reported as a warning on an otherwise-successful removal, never as the batch item failing -
    // and native uninstall failing never triggers an automatic Revo cleanup as a substitute.
    private async Task<UninstallResult> UninstallProgramAsync(InstalledItem item, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report($"Uninstalling {item.Name}…");
        var native = await _programExecutor.UninstallAsync(item, progress, ct);

        if (native.Status != UninstallStatus.Succeeded)
        {
            // Failed/Skipped as-is - no destructive Revo purge as a substitute for a failed
            // native uninstall (that's explicitly out of scope for this pass).
            return WithBackend(native, UninstallBackend.Native);
        }

        var hasCleanupMetadata = !string.IsNullOrWhiteSpace(item.InstallLocation);
        if (!_revo.IsAvailable || !hasCleanupMetadata)
        {
            return WithBackend(native, UninstallBackend.Native);
        }

        progress?.Report($"Cleaning leftovers for {item.Name}…");
        var revoResult = await _revo.CleanupProgramAsync(item.Name, item.InstallLocation, item.Is64Bit, ct);

        if (revoResult.Outcome == RevoOutcome.Success)
        {
            return new UninstallResult { Item = item, Status = UninstallStatus.Succeeded, Backend = UninstallBackend.NativeThenRevo };
        }

        // Native removal is still a real success even though the cleanup pass itself didn't pan
        // out - never reported as Failed.
        return new UninstallResult
        {
            Item = item,
            Status = UninstallStatus.SucceededWithCleanupWarning,
            ErrorMessage = "Cleanup warning: Revo could not scan leftovers.",
            Backend = UninstallBackend.NativeThenRevo
        };
    }

    private static UninstallResult WithBackend(UninstallResult result, UninstallBackend backend) =>
        result.Backend == backend
            ? result
            : new UninstallResult
            {
                Item = result.Item,
                Status = result.Status,
                ErrorMessage = result.ErrorMessage,
                ExitCode = result.ExitCode,
                Backend = backend
            };
}
