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

    // Real-machine correction: `Repair-WinGetPackageManager -Latest -Force -AllUsers` FAILED on
    // the actual broken App Installer state it was meant to fix - it downloaded the modern
    // dependencies, then tried to re-register/test the still-broken old package and failed.
    // `Repair-WinGetPackageManager -Force -Latest` (no -AllUsers) succeeded in the same elevated
    // account, bringing v1.2.10691 to v1.29.290. Support Tool always runs elevated and repairs for
    // the current interactive account it's already running as - -AllUsers was never actually
    // needed for that.
    //
    // $ProgressPreference = 'SilentlyContinue' first - Install-Module/Repair-WinGetPackageManager
    // otherwise write PowerShell progress-record XML that would otherwise leak into stdout as raw
    // "#< CLIXML ..." text if anything downstream ever surfaced it verbatim. The explicit
    // try/catch (rather than relying on $ErrorActionPreference alone to produce a clean non-zero
    // exit) guarantees a single plain-text line on stderr and a real non-zero exit code on
    // failure - RunRepairScriptAsync below reads exactly that, never a serialized error object.
    //
    // "STAGE: ..." lines are this class's own protocol with itself, not a PowerShell convention -
    // RunRepairScriptAsync strips the prefix and reports the remainder as a UI-facing progress
    // line, same as the real streamed detail below. Only Repair-WinGetPackageManager gets -Verbose
    // (merged into the success stream via 4>&1, the one stream that never gets CLIXML-serialized
    // when redirected) - it's the one call that actually does the interesting, slow work (download
    // sizes, per-dependency install steps); Install-PackageProvider/Install-Module don't get
    // -Verbose at all, so their own verbose stream (if any) is never separately redirected and
    // can't reintroduce the CLIXML problem this exists to avoid.
    private const string RepairScript = """
        $ProgressPreference = 'SilentlyContinue'
        $ErrorActionPreference = 'Stop'
        try {
            Write-Output "STAGE: Installing NuGet provider..."
            Install-PackageProvider -Name NuGet -Force | Out-Null

            Write-Output "STAGE: Installing WinGet repair module..."
            Install-Module -Name Microsoft.WinGet.Client -Force -Repository PSGallery | Out-Null
            Import-Module Microsoft.WinGet.Client -Force

            Write-Output "STAGE: Repairing Windows Package Manager..."
            Repair-WinGetPackageManager -Force -Latest -Verbose 4>&1

            exit 0
        }
        catch {
            [Console]::Error.WriteLine($_.Exception.Message)
            exit 1
        }
        """;

    private const string StageLinePrefix = "STAGE:";

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
    //
    // progress is the prominent current-stage heading, and it must actually track the real stage
    // - not freeze at a generic "Repairing WinGet..." while the interesting sub-steps only show
    // up in the detail log below it. So progress is also passed into RunRepairScriptAsync: when
    // the streamed repair output hits one of this class's own "STAGE: ..." markers ("Installing
    // NuGet provider...", "Installing WinGet repair module...", "Repairing Windows Package
    // Manager..."), that exact text becomes the new heading, not just a detail-log entry.
    // detailProgress receives the same stage text (once) plus the real granular verbose lines
    // (download sizes, per-dependency steps) for the scrolling detail view underneath. The two
    // bookend phases ("Checking WinGet...", "Verifying WinGet...") are reported explicitly, once
    // each, to both channels right here - the only two messages this method itself originates
    // rather than relaying from the repair script.
    public async Task<WingetReadyResult> EnsureReadyAsync(
        bool allowRepair,
        SharedViewState viewState,
        Logger logger,
        IProgress<string>? progress = null,
        IProgress<string>? detailProgress = null,
        CancellationToken ct = default)
    {
        ReportStage("Checking WinGet...", progress, detailProgress);
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
            // No generic "Repairing WinGet..." heading reported here - RunRepairScriptAsync's own
            // streamed STAGE markers immediately supply the real, specific first step
            // ("Installing NuGet provider...") as soon as the process starts, which is more
            // informative than a placeholder that would just be overwritten a moment later.
            (repairSucceeded, repairError) = await RunRepairScriptAsync(logger, progress, detailProgress, ct);
        }

        ReportStage("Verifying WinGet...", progress, detailProgress);
        var postRepairDiagnostics = await DiagnoseAsync(logger, ct);
        var ready = IsReady(postRepairDiagnostics);

        logger.Info($"Post-repair WinGet readiness: {ready} (version '{postRepairDiagnostics.Version}').");

        var errorMessage = ready
            ? null
            : repairError ?? $"WinGet is still not ready after repair: {DescribeUnready(postRepairDiagnostics)}";

        return new WingetReadyResult(ready, postRepairDiagnostics, RepairAttempted: true, repairSucceeded, errorMessage);
    }

    // Reports the same stage text to both channels exactly once - the single place this class
    // originates a message that belongs in both the prominent heading and the detail history,
    // so the two channels can never drift or duplicate against each other.
    private static void ReportStage(string text, IProgress<string>? progress, IProgress<string>? detailProgress)
    {
        progress?.Report(text);
        detailProgress?.Report(text);
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

    // Deliberately bypasses the injected IProcessRunner (which only returns a final buffered
    // result once the process exits) in favor of a small focused streaming path specific to this
    // one operation - the repair can run for real minutes and download real packages, and the
    // technician needs to see that something is actually happening as it happens, not a dump of
    // everything at the very end. Every raw line is logged in full (Verbose.log) regardless of
    // whether it's shown in the UI; detailProgress only ever receives cleaned, filtered lines.
    private async Task<(bool Succeeded, string? ErrorMessage)> RunRepairScriptAsync(Logger logger, IProgress<string>? stageProgress, IProgress<string>? detailProgress, CancellationToken ct)
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

            var stderrLines = new List<string>();
            var stderrLock = new object();

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                logger.Info($"[winget-repair] {e.Data}");
                ReportRepairLine(e.Data, stageProgress, detailProgress);
            };

            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                logger.Info($"[winget-repair:stderr] {e.Data}");
                var cleaned = CleanRepairLine(e.Data);
                if (cleaned is null)
                {
                    return;
                }

                lock (stderrLock)
                {
                    stderrLines.Add(cleaned);
                }

                detailProgress?.Report(cleaned);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 0)
            {
                return (true, null);
            }

            string error;
            lock (stderrLock)
            {
                error = stderrLines.Count > 0
                    ? string.Join(Environment.NewLine, stderrLines)
                    : $"WinGet repair script exited with code {process.ExitCode}.";
            }

            return (false, error);
        }
        catch (Exception ex)
        {
            var error = $"Failed to run the WinGet repair script: {ex.Message}";
            logger.Error(error);
            return (false, error);
        }
    }

    // Splits this class's own "STAGE: ..." markers (a fresh top-level phase, e.g. "Installing
    // NuGet provider...") from the real streamed verbose detail (e.g. a download-size line from
    // Repair-WinGetPackageManager). A STAGE marker is reported to BOTH channels - it becomes the
    // new prominent heading (stageProgress) AND a detail-history entry (detailProgress), via the
    // same ReportStage helper EnsureReadyAsync itself uses for its own two bookend messages, so
    // there is exactly one place a stage message can originate and no way for the heading to
    // drift out of sync with (or duplicate against) the detail log. Real granular verbose lines
    // only ever go to detailProgress - they're too frequent/granular to be a heading.
    private static void ReportRepairLine(string rawLine, IProgress<string>? stageProgress, IProgress<string>? detailProgress)
    {
        var trimmed = rawLine.Trim();
        if (trimmed.StartsWith(StageLinePrefix, StringComparison.Ordinal))
        {
            var stageText = trimmed[StageLinePrefix.Length..].Trim();
            if (stageText.Length > 0)
            {
                ReportStage(stageText, stageProgress, detailProgress);
            }

            return;
        }

        var cleaned = CleanRepairLine(rawLine);
        if (cleaned is not null)
        {
            detailProgress?.Report(cleaned);
        }
    }

    // User-facing cleanup only - the raw line is always logged in full separately, regardless of
    // what this returns. Strips the "VERBOSE:" prefix PowerShell's default formatter adds, drops
    // blank lines, and drops anything CLIXML/serialized-object-shaped so a technician never sees
    // raw "#< CLIXML ..." or "<Objs ...>" text even if some other stream ends up redirected in a
    // way that triggers PowerShell's object serialization.
    private static string? CleanRepairLine(string rawLine)
    {
        var line = rawLine.Trim();
        if (line.Length == 0)
        {
            return null;
        }

        if (line.StartsWith("#<", StringComparison.Ordinal) ||
            line.StartsWith("<Objs", StringComparison.Ordinal) ||
            line.StartsWith("<Obj ", StringComparison.Ordinal) ||
            line.StartsWith("<S ", StringComparison.Ordinal) ||
            line.StartsWith("<S>", StringComparison.Ordinal))
        {
            return null;
        }

        if (line.StartsWith("VERBOSE:", StringComparison.OrdinalIgnoreCase))
        {
            line = line["VERBOSE:".Length..].Trim();
        }

        return line.Length == 0 ? null : line;
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
