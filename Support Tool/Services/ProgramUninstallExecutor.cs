using Microsoft.Win32;
using Support_Tool.Models;

namespace Support_Tool.Services;

/// <summary>
/// Launches a program's registered UninstallString from the registry.
/// </summary>
/// <remarks>
/// Exit code 0 alone is never trusted as proof the uninstall actually finished - many installers
/// detach a child process and exit 0 immediately while the real removal continues in the
/// background. Succeeded requires BOTH of: the program's own exact ARP (Add/Remove Programs)
/// registration - captured on InstalledItem at inventory time - genuinely disappearing from the
/// registry, AND no live process descending from the one we launched still running (see
/// ProcessLineage) - external review, correction: ARP absence alone is not always sufficient
/// proof for a detached native uninstaller, since a bootstrapper can exit (and its ARP entry can
/// disappear) while a child installer/confirmation UI it handed off to is still genuinely open.
/// This matters beyond just correctness of the reported status: RealUninstallExecutor only runs
/// the Revo /mu leftover-cleanup pass when this reports Succeeded, and running that cleanup while
/// the real uninstaller is still active could delete files/registry state out from under it. A
/// false Started is an acceptable cost; a destructive cleanup racing a still-running uninstaller,
/// or a card disappearing while its uninstall prompt is still on screen, is not.
/// </remarks>
public class ProgramUninstallExecutor : IUninstallExecutor
{
    private readonly IProcessRunner _processRunner;

    // Bounded settlement window, not a polling loop: absorbs ordinary registry-write/process-
    // teardown lag (both an ARP key removal and a detached child process fully unwinding can lag
    // slightly behind the direct process's own exit) without waiting out a human actually
    // interacting with an uninstall wizard - if a live descendant or the ARP entry is still
    // present once this window elapses, the result is Started, however long the wizard itself
    // ultimately takes to close.
    private static readonly TimeSpan SettlementCheckDelay = TimeSpan.FromSeconds(1.5);
    private const int MaxSettlementChecks = 2;

    public ProgramUninstallExecutor(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<UninstallResult> UninstallAsync(InstalledItem item, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.UninstallCommand))
            return new UninstallResult
            {
                Item = item,
                Status = UninstallStatus.Skipped,
                ErrorMessage = "No uninstall command is registered for this program."
            };

        var startInfo = UninstallCommandParser.BuildStartInfo(item.UninstallCommand);

        if (startInfo == null)
            return new UninstallResult
            {
                Item = item,
                Status = UninstallStatus.Skipped,
                ErrorMessage = "Could not parse the registered uninstall command."
            };

        try
        {
            var result = await _processRunner.RunAsync(startInfo, ct);

            if (result.ExitCode != 0)
            {
                return new UninstallResult
                {
                    Item = item,
                    Status = UninstallStatus.Failed,
                    ExitCode = result.ExitCode,
                    ErrorMessage = $"Uninstaller exited with code {result.ExitCode}."
                };
            }

            // Succeeded requires the ARP registration gone AND no live process descending from
            // the one we just launched - checked immediately first (fast path, no artificial
            // delay when both are already true), then re-checked up to MaxSettlementChecks more
            // times, spaced SettlementCheckDelay apart, before settling on Started.
            for (var attempt = 0; ; attempt++)
            {
                var arpGone = !RegistrationStillExists(item);
                var descendantAlive = ProcessLineage.HasLiveDescendant(result.ProcessId, result.StartTimeUtc);

                if (arpGone && !descendantAlive)
                {
                    return new UninstallResult { Item = item, Status = UninstallStatus.Succeeded, ExitCode = result.ExitCode };
                }

                if (attempt >= MaxSettlementChecks)
                {
                    return new UninstallResult
                    {
                        Item = item,
                        Status = UninstallStatus.Started,
                        ExitCode = result.ExitCode,
                        ErrorMessage = "Uninstaller started — completion could not be verified"
                    };
                }

                await Task.Delay(SettlementCheckDelay, ct);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UninstallResult
            {
                Item = item,
                Status = UninstallStatus.Failed,
                ErrorMessage = ex.Message
            };
        }
    }

    // Re-opens the EXACT registry location the item was discovered under (hive/view/subkey name,
    // captured by ProgramInventoryService at inventory time) - never a name-based re-search, which
    // could match a different entry entirely. Missing identity, or any error opening the registry,
    // is treated as "still exists" (conservative - see this class's own remarks): a false Started
    // is acceptable, wrongly declaring Succeeded is not.
    private static bool RegistrationStillExists(InstalledItem item)
    {
        if (string.IsNullOrWhiteSpace(item.UninstallRegistryKeyName) || string.IsNullOrWhiteSpace(item.RegistryHiveName))
        {
            return true;
        }

        try
        {
            var hive = item.RegistryHiveName == "CurrentUser" ? RegistryHive.CurrentUser : RegistryHive.LocalMachine;
            var view = item.RegistryViewName switch
            {
                nameof(RegistryView.Registry64) => RegistryView.Registry64,
                nameof(RegistryView.Registry32) => RegistryView.Registry32,
                _ => RegistryView.Default,
            };

            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            using var subKey = uninstallKey?.OpenSubKey(item.UninstallRegistryKeyName);
            return subKey is not null;
        }
        catch
        {
            return true;
        }
    }
}
