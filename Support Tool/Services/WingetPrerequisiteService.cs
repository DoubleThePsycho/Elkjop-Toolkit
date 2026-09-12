using System.Diagnostics;
using System.Text;
using Support_Tool.ViewModels;

namespace Support_Tool.Services;

// Snapshot of what was actually observed about the installed WinGet, not just a pass/fail bit -
// kept around so callers (InstallPageViewModel's StatusMessage, logs) can explain exactly what's
// wrong rather than a generic "WinGet is broken".
public sealed record WingetDiagnostics(
    bool ProcessRuns,
    string? Version,
    bool SupportsDisableInteractivity,
    bool HealthProbePassed,
    string? Detail);

public sealed record WingetReadyResult(
    bool IsReady,
    WingetDiagnostics Diagnostics,
    bool RepairAttempted,
    bool RepairSucceeded,
    string? ErrorMessage);

// Focused prerequisite check/repair for WinGet itself - not a generic package-manager framework,
// and deliberately separate from WingetStatusChecker/RealInstallExecutor (which remain
// responsible for one status query / one package invocation each, unchanged). Exists because a
// real customer-machine case proved that "winget.exe exists" and "winget --version succeeds" are
// not sufficient readiness signals: a Windows 11 22H2 VM with DesktopAppInstaller 1.17.10691
// (winget v1.2.10691) reported a version fine, but `winget install --id Mozilla.Firefox --exact
// --silent --accept-package-agreements --accept-source-agreements` crashed winget.exe itself
// (0xC0000005 ACCESS_VIOLATION in WindowsPackageManager.dll, Application Event 1000) - and that
// same version doesn't support the --disable-interactivity flag WingetStatusChecker/
// RealInstallExecutor both already depend on. The fix proven manually on that machine was:
//   Install-PackageProvider -Name NuGet -Force
//   Install-Module -Name Microsoft.WinGet.Client -Force -Repository PSGallery
//   Repair-WinGetPackageManager -AllUsers
// which brought it to v1.29.290, after which the same install command succeeded. This class
// reproduces that check -> repair -> re-verify sequence.
public class WingetPrerequisiteService
{
    private readonly IProcessRunner _processRunner;

    // A native-crash termination (SEH exception, no handler) surfaces as an NTSTATUS-shaped exit
    // code with the high bits set - .NET/Windows report the raw Win32 exit code, which for a
    // crash is the exception code itself (e.g. STATUS_ACCESS_VIOLATION 0xC0000005, the exact
    // pattern observed on the failing VM). WinGet's own defined/legitimate non-zero exit codes
    // (e.g. "no applicable installer", "no sources configured") don't fall in this narrow NTSTATUS
    // exception-code range, so checking specifically for it - rather than treating every non-zero
    // exit as equally bad - is what lets a genuine crash be distinguished from an ordinary,
    // survivable non-zero result.
    private const uint NtStatusExceptionRangeStart = 0xC0000000;
    private const uint NtStatusExceptionRangeEnd = 0xC0000FFF;

    private const string RepairScript = """
        $ErrorActionPreference = 'Stop'
        Install-PackageProvider -Name NuGet -Force | Out-Null
        Install-Module -Name Microsoft.WinGet.Client -Force -Repository PSGallery | Out-Null
        Import-Module Microsoft.WinGet.Client -Force
        Repair-WinGetPackageManager -AllUsers
        """;

    public WingetPrerequisiteService(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    // Read-only - never mutates anything, safe to call as often as needed (the pre-load check,
    // and the cheap pre-install-batch re-check).
    public async Task<WingetDiagnostics> DiagnoseAsync(Logger logger, CancellationToken ct = default)
    {
        var (processRuns, version) = await CheckVersionAsync(logger, ct);

        var supportsDisableInteractivity = false;
        if (processRuns)
        {
            supportsDisableInteractivity = await CheckDisableInteractivitySupportAsync(logger, ct);
        }

        var healthProbePassed = false;
        string? detail = null;
        if (processRuns)
        {
            (healthProbePassed, detail) = await CheckHealthAsync(logger, ct);
        }

        return new WingetDiagnostics(processRuns, version, supportsDisableInteractivity, healthProbePassed, detail);
    }

    // Cheap, read-only readiness check with no repair attempt - for the install-command
    // defense-in-depth re-check (requirement: don't necessarily repair twice, a passing cheap
    // check is enough).
    public async Task<bool> QuickCheckAsync(Logger logger, CancellationToken ct = default) =>
        IsReady(await DiagnoseAsync(logger, ct));

    // The full check -> (LIVE-only) repair -> re-verify sequence. allowRepair is the caller's
    // ExecutionMode gate (SharedViewState.IsLiveMode) - DEBUG Dry Run must never trigger this
    // repair as a side effect of merely testing the UI (requirement 10), so callers pass false in
    // that case and this method only diagnoses, never mutates.
    public async Task<WingetReadyResult> EnsureReadyAsync(
        bool allowRepair,
        SharedViewState viewState,
        Logger logger,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report("Checking WinGet...");
        var diagnostics = await DiagnoseAsync(logger, ct);

        if (IsReady(diagnostics))
        {
            logger.Info($"WinGet is ready (version '{diagnostics.Version}'). No repair needed.");
            return new WingetReadyResult(true, diagnostics, RepairAttempted: false, RepairSucceeded: false, ErrorMessage: null);
        }

        var unreadyReason = DescribeUnready(diagnostics);

        if (!allowRepair)
        {
            logger.Warn($"WinGet is not ready and repair is disabled (Dry Run): {unreadyReason}");
            return new WingetReadyResult(false, diagnostics, RepairAttempted: false, RepairSucceeded: false, ErrorMessage: unreadyReason);
        }

        logger.Warn($"WinGet is not ready ({unreadyReason}) - attempting repair.");
        progress?.Report("Repairing WinGet...");

        bool repairSucceeded;
        string? repairError;

        // Held for the ENTIRE mutating repair operation, not just a status check -
        // Install-PackageProvider/Install-Module/Repair-WinGetPackageManager can take real time
        // and genuinely change the machine, so WindowsUpdateLifecycleCoordinator must never be
        // allowed to reboot out from under it. Deliberately not IsWuaOperationActive - unrelated
        // to Windows Update; this is the same generic lease InstallService's own install batch
        // already uses.
        using (viewState.EnterToolkitOperation())
        {
            (repairSucceeded, repairError) = await RunRepairScriptAsync(logger, ct);
        }

        progress?.Report("Verifying WinGet...");
        var postRepairDiagnostics = await DiagnoseAsync(logger, ct);
        var ready = IsReady(postRepairDiagnostics);

        logger.Info($"Post-repair WinGet readiness: {ready} (version '{postRepairDiagnostics.Version}').");

        var errorMessage = ready
            ? null
            : repairError ?? $"WinGet is still not ready after repair: {DescribeUnready(postRepairDiagnostics)}";

        return new WingetReadyResult(ready, postRepairDiagnostics, RepairAttempted: true, repairSucceeded, errorMessage);
    }

    private async Task<(bool ProcessRuns, string? Version)> CheckVersionAsync(Logger logger, CancellationToken ct)
    {
        try
        {
            var result = await RunWingetAsync("--version", ct);

            if (IsCrashExitCode(result.ExitCode))
            {
                logger.Warn($"WinGet --version crashed (exit code 0x{unchecked((uint)result.ExitCode):X8}).");
                return (false, null);
            }

            var version = result.StandardOutput.Trim();

            // Sane completion requires BOTH a clean exit AND actual version text - a process that
            // exits 0 but prints nothing (or one that fails with some other non-zero code) is not
            // a usable "winget --version" result either way.
            if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(version))
            {
                logger.Warn($"WinGet --version did not complete successfully (exit={result.ExitCode}, output='{version}').");
                return (false, null);
            }

            logger.Info($"WinGet --version: exit=0, version='{version}'.");
            return (true, version);
        }
        catch (Exception ex)
        {
            logger.Warn($"WinGet --version failed to start: {ex.Message}");
            return (false, null);
        }
    }

    private async Task<bool> CheckDisableInteractivitySupportAsync(Logger logger, CancellationToken ct)
    {
        try
        {
            var result = await RunWingetAsync("install --help", ct);

            if (IsCrashExitCode(result.ExitCode))
            {
                logger.Warn($"WinGet install --help crashed (exit code 0x{unchecked((uint)result.ExitCode):X8}).");
                return false;
            }

            // A failed command that happens to still emit some help-shaped text must not count as
            // capability success - require a clean exit before even looking at the output.
            if (result.ExitCode != 0)
            {
                logger.Warn($"WinGet install --help did not complete successfully (exit={result.ExitCode}).");
                return false;
            }

            var supported = result.StandardOutput.Contains("--disable-interactivity", StringComparison.OrdinalIgnoreCase);
            logger.Info($"WinGet --disable-interactivity capability: {supported}.");
            return supported;
        }
        catch (Exception ex)
        {
            logger.Warn($"WinGet install --help failed: {ex.Message}");
            return false;
        }
    }

    private async Task<(bool Passed, string? Detail)> CheckHealthAsync(Logger logger, CancellationToken ct)
    {
        try
        {
            // Non-mutating - lists configured sources rather than installing/changing anything,
            // while still actually exercising WindowsPackageManager.dll more meaningfully than
            // --version does (the real failing VM's crash was specifically in that DLL, reached
            // by real command execution, not by the version query).
            var result = await RunWingetAsync("source list", ct);

            if (IsCrashExitCode(result.ExitCode))
            {
                var detail = $"winget source list crashed (exit code 0x{unchecked((uint)result.ExitCode):X8}).";
                logger.Warn(detail);
                return (false, detail);
            }

            // Only a clean exit counts as healthy - an ordinary (non-crash) non-zero exit is
            // still not a sane WinGet state for this application. Treating every non-crash exit
            // as "healthy" would let a genuinely broken-but-not-crashing WinGet report Ready, and
            // WingetStatusChecker's own subsequent non-zero results would then silently become
            // false NotInstalled catalog entries - exactly what this prerequisite exists to
            // prevent. No non-zero code is currently whitelisted as an acceptable "source list"
            // result for this application; add one only with a specific, documented reason a
            // particular code still represents a usable source subsystem.
            if (result.ExitCode != 0)
            {
                var detail = $"winget source list exited with code {result.ExitCode}." +
                    (string.IsNullOrWhiteSpace(result.StandardError) ? "" : $" stderr: {Truncate(result.StandardError)}") +
                    (string.IsNullOrWhiteSpace(result.StandardOutput) ? "" : $" stdout: {Truncate(result.StandardOutput)}");
                logger.Warn(detail);
                return (false, detail);
            }

            logger.Info("WinGet health probe (source list): exit=0, healthy=true.");
            return (true, null);
        }
        catch (Exception ex)
        {
            var detail = $"winget source list failed to start: {ex.Message}";
            logger.Warn(detail);
            return (false, detail);
        }
    }

    private async Task<(bool Succeeded, string? ErrorMessage)> RunRepairScriptAsync(Logger logger, CancellationToken ct)
    {
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(RepairScript));
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            var result = await _processRunner.RunAsync(psi, ct);
            logger.Info($"WinGet repair script exit={result.ExitCode}. stdout: {Truncate(result.StandardOutput)}" +
                        (string.IsNullOrWhiteSpace(result.StandardError) ? "" : $" stderr: {Truncate(result.StandardError)}"));

            if (result.ExitCode == 0)
            {
                return (true, null);
            }

            var error = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"WinGet repair script exited with code {result.ExitCode}."
                : result.StandardError.Trim();
            return (false, error);
        }
        catch (Exception ex)
        {
            var error = $"Failed to run the WinGet repair script: {ex.Message}";
            logger.Error(error);
            return (false, error);
        }
    }

    private Task<ProcessResult> RunWingetAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "winget",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        return _processRunner.RunAsync(psi, ct);
    }

    private static bool IsReady(WingetDiagnostics d) =>
        d.ProcessRuns && d.SupportsDisableInteractivity && d.HealthProbePassed;

    private static string DescribeUnready(WingetDiagnostics d)
    {
        if (!d.ProcessRuns)
        {
            return "WinGet could not be run.";
        }

        if (!d.SupportsDisableInteractivity)
        {
            return $"the installed WinGet (version '{d.Version}') does not support the required --disable-interactivity option.";
        }

        if (!d.HealthProbePassed)
        {
            return $"WinGet failed its health check: {d.Detail}";
        }

        return "WinGet is not ready.";
    }

    private static bool IsCrashExitCode(int exitCode)
    {
        var unsigned = unchecked((uint)exitCode);
        return unsigned is >= NtStatusExceptionRangeStart and <= NtStatusExceptionRangeEnd;
    }

    private static string Truncate(string s, int max = 500) => s.Length <= max ? s : s[..max] + "...";
}
