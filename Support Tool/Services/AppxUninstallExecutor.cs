using System.Diagnostics;
using System.Text;
using Support_Tool.Models;

namespace Support_Tool.Services;

/// <summary>
/// Removes an Appx/MSIX package for the current user via PowerShell Remove-AppxPackage.
/// The package name is passed through an environment variable to prevent command injection.
/// </summary>
/// <remarks>
/// The script emits exactly one structured "ELKJOP::..." sentinel line to stdout - the only thing
/// this class ever parses to decide the outcome. Everything else the process prints (including
/// PowerShell's own default error formatting, which can otherwise leak raw CLIXML - `&lt;Objs&gt;`,
/// `_x000D_`, `&lt;S S="Error"&gt;`, etc. - into what used to be treated as the error message) is
/// logged raw to Verbose.log and never reaches the caller's ErrorMessage. $ProgressPreference and
/// $ErrorActionPreference plus the script's own try/catch are what actually stop that leak at the
/// source, per this pass's explicit requirement to fix the source, not just reformat it afterward.
/// </remarks>
public class AppxUninstallExecutor : IUninstallExecutor
{
    private readonly IProcessRunner _processRunner;
    private readonly Logger _logger = new();

    // APPX_E_NOT_UNINSTALLABLE (Windows' own "this package is part of the OS and cannot be
    // uninstalled per-user" HRESULT) - the reactive half of protected-package classification. The
    // proactive half is InstalledItem.IsProtectedSystemComponent, captured once at inventory time
    // from Get-AppxPackage's own NonRemovable property; this HRESULT check is the safety net for
    // anything that flag didn't catch (stale/missing metadata, an older PowerShell without the
    // property, etc.) - both paths land on the exact same SkippedProtected classification.
    private const string NotUninstallableHResult = "0x80073CFA";

    // $ProgressPreference suppresses Remove-AppxPackage's own progress stream (a common CLIXML
    // source when redirected); $ErrorActionPreference + the script's own try/catch mean every
    // failure path - expected or not - always produces exactly one clean "ELKJOP::" line instead
    // of PowerShell's default uncaught-terminating-error formatting. The package name is read
    // from an env var so no user-supplied string enters the PowerShell command line itself.
    private static readonly string Script = @"
$ProgressPreference = 'SilentlyContinue'
$ErrorActionPreference = 'Stop'
$package = [Environment]::GetEnvironmentVariable('ELKJOP_PACKAGE_FULL_NAME')
if (-not $package) {
    Write-Output 'ELKJOP::ERROR::Package name environment variable is missing.'
    exit 1
}
try {
    Remove-AppxPackage -Package $package -ErrorAction Stop
    Write-Output 'ELKJOP::OK'
}
catch {
    $hresult = $null
    try { $hresult = '0x{0:X8}' -f $_.Exception.HResult } catch {}
    $msg = $_.Exception.Message -replace '[\r\n]+', ' '
    if ($hresult -eq '0x80073CFA') {
        Write-Output ""ELKJOP::PROTECTED::$msg""
    } else {
        Write-Output ""ELKJOP::ERROR::$msg""
    }
    exit 1
}
";

    private static readonly string EncodedScript =
        Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));

    // Same env-var-not-command-line-injection discipline as the removal script above. Used by
    // RealUninstallExecutor to verify a package's actual presence after a Revo /wa attempt -
    // PackageFullName is a much stronger truth source than trusting Revo's own exit code alone
    // (see RevoUninstallService's own remarks on that assumption).
    private static readonly string PresenceScript = @"
$ProgressPreference = 'SilentlyContinue'
$package = [Environment]::GetEnvironmentVariable('ELKJOP_PACKAGE_FULL_NAME')
if (-not $package) {
    Write-Output 'ELKJOP::ERROR::Package name environment variable is missing.'
    exit 1
}
try {
    # Get-AppxPackage has no -Package parameter (that's Remove-AppxPackage's) - an exact
    # PackageFullName comparison against the current user's full package list is the correct
    # equivalent, same current-user scope Get-AppxPackage already uses everywhere else in this
    # class/AppInventoryService. Deliberately -eq, never -like/wildcard.
    $pkg = Get-AppxPackage -ErrorAction SilentlyContinue | Where-Object { $_.PackageFullName -eq $package }
    if ($pkg) { Write-Output 'ELKJOP::PRESENT' } else { Write-Output 'ELKJOP::ABSENT' }
}
catch {
    $msg = $_.Exception.Message -replace '[\r\n]+', ' '
    Write-Output ""ELKJOP::ERROR::$msg""
    exit 1
}
";

    private static readonly string EncodedPresenceScript =
        Convert.ToBase64String(Encoding.Unicode.GetBytes(PresenceScript));

    public AppxUninstallExecutor(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    // null = couldn't determine (PowerShell itself failed, or no recognizable sentinel came back)
    // - callers must treat that conservatively (i.e. the same as "still might be present"), never
    // as a confirmed absence.
    public async Task<bool?> IsPackagePresentAsync(string packageFullName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(packageFullName))
        {
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {EncodedPresenceScript}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment["ELKJOP_PACKAGE_FULL_NAME"] = packageFullName;

        try
        {
            var result = await _processRunner.RunAsync(psi, ct);
            LogRawOutput($"{packageFullName}:presence-check", result.StandardOutput, result.StandardError);

            var sentinel = ExtractSentinelLine(result.StandardOutput);
            return sentinel?.Kind switch
            {
                "PRESENT" => true,
                "ABSENT" => false,
                _ => null,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warn($"[Appx:{packageFullName}:presence-check] Failed to run: {ex.Message}");
            return null;
        }
    }

    public async Task<UninstallResult> UninstallAsync(InstalledItem item, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.PackageFullName))
            return new UninstallResult
            {
                Item = item,
                Status = UninstallStatus.Skipped,
                ErrorMessage = "PackageFullName is missing; cannot remove this package."
            };

        // Proactive classification - known-protected packages are never even attempted, so they
        // can never generate the giant PowerShell failure manual testing observed for things like
        // Cloud Experience Host / Narrator QuickStart / Xbox Game Callable UI.
        if (item.IsProtectedSystemComponent)
        {
            _logger.Info($"[Appx:{item.Name}] Skipped - Windows reports this package as NonRemovable.");
            return new UninstallResult
            {
                Item = item,
                Status = UninstallStatus.SkippedProtected,
                ErrorMessage = "Protected Windows component"
            };
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -EncodedCommand {EncodedScript}",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment["ELKJOP_PACKAGE_FULL_NAME"] = item.PackageFullName;

        try
        {
            var result = await _processRunner.RunAsync(psi, ct);

            LogRawOutput(item.Name, result.StandardOutput, result.StandardError);

            var sentinel = ExtractSentinelLine(result.StandardOutput);

            if (sentinel is { Kind: "OK" })
            {
                return new UninstallResult { Item = item, Status = UninstallStatus.Succeeded };
            }

            if (sentinel is { Kind: "PROTECTED" })
            {
                return new UninstallResult
                {
                    Item = item,
                    Status = UninstallStatus.SkippedProtected,
                    ExitCode = result.ExitCode,
                    ErrorMessage = "Protected Windows component"
                };
            }

            if (sentinel is { Kind: "ERROR" } errorSentinel)
            {
                return new UninstallResult
                {
                    Item = item,
                    Status = UninstallStatus.Failed,
                    ExitCode = result.ExitCode,
                    ErrorMessage = errorSentinel.Message
                };
            }

            // No recognized sentinel line at all - PowerShell itself failed to even run our
            // script (e.g. the encoded command couldn't be parsed). Defensively cleaned, never
            // raw CLIXML, even in this unexpected path.
            return new UninstallResult
            {
                Item = item,
                Status = UninstallStatus.Failed,
                ExitCode = result.ExitCode,
                ErrorMessage = CleanFallbackText(result.StandardError) ?? $"PowerShell exited with code {result.ExitCode}."
            };
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
                ErrorMessage = $"Failed to start PowerShell: {ex.Message}"
            };
        }
    }

    private void LogRawOutput(string itemName, string stdOut, string stdErr)
    {
        foreach (var line in SplitLines(stdOut))
        {
            _logger.Info($"[Appx:{itemName}] {line}");
        }

        foreach (var line in SplitLines(stdErr))
        {
            _logger.Info($"[Appx:{itemName}:stderr] {line}");
        }
    }

    private static IEnumerable<string> SplitLines(string text) =>
        string.IsNullOrEmpty(text)
            ? []
            : text.Split('\r', '\n').Where(l => l.Length > 0);

    private sealed record Sentinel(string Kind, string Message);

    // Finds our own "ELKJOP::KIND::message" line anywhere in stdout - deliberately not assuming
    // it's the only or the last line, since PowerShell can still emit warnings/other noise
    // alongside it even with $ProgressPreference suppressed.
    private static Sentinel? ExtractSentinelLine(string stdOut)
    {
        const string prefix = "ELKJOP::";

        foreach (var line in SplitLines(stdOut))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = trimmed[prefix.Length..];
            var separatorIndex = rest.IndexOf("::", StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                if (rest == "OK")
                {
                    return new Sentinel("OK", "");
                }

                continue;
            }

            var kind = rest[..separatorIndex];
            var message = rest[(separatorIndex + 2)..];
            return new Sentinel(kind, message);
        }

        return null;
    }

    // Last-resort cleanup for the "no sentinel line at all" path - recognizes CLIXML/serialized-
    // diagnostic-transport shapes and refuses to surface them, rather than showing an XML novel.
    private static string? CleanFallbackText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var meaningful = SplitLines(raw)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0
                && !l.StartsWith("#<", StringComparison.Ordinal)
                && !l.Contains("<Objs", StringComparison.OrdinalIgnoreCase)
                && !l.Contains("<S S=", StringComparison.OrdinalIgnoreCase)
                && !l.Contains("_x000D_", StringComparison.OrdinalIgnoreCase))
            .ToList();

        return meaningful.Count > 0 ? string.Join(' ', meaningful.TakeLast(3)) : null;
    }
}
