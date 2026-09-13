using System.Diagnostics;

namespace Support_Tool.Services;

public enum RevoOutcome { Success, Failure, Unavailable }

public sealed record RevoResult(RevoOutcome Outcome, string? Message);

/// <summary>
/// Runs Revo Uninstaller Pro Portable's CLI (RevoUnPro.exe) for the two operations this pass
/// integrates - see RealUninstallExecutor for the actual Native/Revo decision flow. Revo being
/// disconnected/missing/unable to start is never an error here, just RevoOutcome.Unavailable -
/// callers always have a native fallback and must never surface a "Revo missing" dialog.
/// </summary>
/// <remarks>
/// Exit-code-0-is-success is a reasonable, standard CLI convention assumption, not something
/// confirmed against Revo's own documentation in this pass - flagged for real-hardware
/// verification alongside this feature's other manual tests.
/// </remarks>
public sealed class RevoUninstallService
{
    private readonly RevoLocator _locator;
    private readonly Logger _logger;

    public RevoUninstallService(Logger logger)
    {
        _logger = logger;
        _locator = new RevoLocator();
    }

    public bool IsAvailable => _locator.TryGetRevoExePath(_logger) is not null;

    // Windows Apps: starts the app's own built-in uninstall procedure, then automatically scans/
    // removes leftover registry/files/folders - one call does both halves, unlike /mu (see
    // CleanupProgramAsync's own remarks).
    public Task<RevoResult> RemoveWindowsAppAsync(string displayName, CancellationToken ct)
    {
        var exePath = _locator.TryGetRevoExePath(_logger);
        if (exePath is null)
        {
            return Task.FromResult(new RevoResult(RevoOutcome.Unavailable, null));
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/wa");
        psi.ArgumentList.Add(displayName);

        return RunAsync(psi, $"WindowsApp:{displayName}", ct);
    }

    // Regular programs: /mu deliberately does NOT launch the program's own built-in uninstaller
    // (per Revo's own documentation) - this is a leftover-cleanup pass only, run AFTER the
    // existing native uninstaller has already succeeded. installPath/is64Bit must be captured
    // before native uninstall runs (InstalledItem.InstallLocation/Is64Bit, populated at inventory
    // time) since the program's own registry entry is gone once native uninstall succeeds.
    // Always Moderate mode - never Advanced automatically, per this pass's explicit scope.
    public Task<RevoResult> CleanupProgramAsync(string displayName, string installPath, bool is64Bit, CancellationToken ct)
    {
        var exePath = _locator.TryGetRevoExePath(_logger);
        if (exePath is null)
        {
            return Task.FromResult(new RevoResult(RevoOutcome.Unavailable, null));
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("/mu");
        psi.ArgumentList.Add(displayName);
        psi.ArgumentList.Add("/path");
        psi.ArgumentList.Add(NormalizeRevoPath(installPath));
        psi.ArgumentList.Add("/mode");
        psi.ArgumentList.Add("Moderate");
        psi.ArgumentList.Add(is64Bit ? "/64" : "/32");

        return RunAsync(psi, $"Program:{displayName}", ct);
    }

    // Revo's own CLI documentation requires /path not end with a backslash. Registry
    // InstallLocation values commonly do (e.g. "C:\Program Files\Thing\"), so this is trimmed
    // generically here rather than trusting every caller to have already done it. A bare drive
    // root ("C:\") is left alone - trimming that would produce "C:", which is a different
    // (relative-to-current-directory-on-that-drive) path, not an equivalent root reference.
    internal static string NormalizeRevoPath(string path)
    {
        var trimmed = path.TrimEnd('\\');
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + "\\" : trimmed;
    }

    // ArgumentList (never a hand-built quoted command line) so a display name or install path
    // containing spaces/quotes is passed correctly - same discipline as RealInstallExecutor's own
    // winget invocation. Raw stdout/stderr is logged line by line as Revo actually produces it,
    // tagged per item/backend (e.g. "[Revo:WindowsApp:Clipchamp]"), through the existing shared
    // Logger/ToolkitLogger - lands in the same Verbose.log everything else does, no second log
    // file. Never surfaced to the result UI - only RevoResult.Message (kept short) is.
    private async Task<RevoResult> RunAsync(ProcessStartInfo psi, string tag, CancellationToken ct)
    {
        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    _logger.Info($"[Revo:{tag}] {e.Data}");
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    _logger.Info($"[Revo:{tag}:stderr] {e.Data}");
                }
            };

            _logger.Info($"[Revo:{tag}] Starting: \"{psi.FileName}\" {string.Join(' ', psi.ArgumentList)}");

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 0)
            {
                return new RevoResult(RevoOutcome.Success, null);
            }

            _logger.Warn($"[Revo:{tag}] Exited with code {process.ExitCode}.");
            return new RevoResult(RevoOutcome.Failure, $"Revo exited with code {process.ExitCode}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Revo:{tag}] Failed to run: {ex.Message}");
            return new RevoResult(RevoOutcome.Failure, ex.Message);
        }
    }
}
