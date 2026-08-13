using System.Diagnostics;
using System.Text;
using Support_Tool.Models;

namespace Support_Tool.Services;

/// <summary>
/// Removes an Appx/MSIX package for the current user via PowerShell Remove-AppxPackage.
/// The package name is passed through an environment variable to prevent command injection.
/// </summary>
public class AppxUninstallExecutor : IUninstallExecutor
{
    private readonly IProcessRunner _processRunner;

    // Script reads the package name from an env var so no user-supplied string enters
    // the PowerShell command line itself.
    private static readonly string Script = @"
$package = [Environment]::GetEnvironmentVariable('ELKJOP_PACKAGE_FULL_NAME')
if (-not $package) { throw 'Package name environment variable is missing.' }
Remove-AppxPackage -Package $package -ErrorAction Stop
";

    private static readonly string EncodedScript =
        Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));

    public AppxUninstallExecutor(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<UninstallResult> UninstallAsync(InstalledItem item, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(item.PackageFullName))
            return new UninstallResult
            {
                Item = item,
                Status = UninstallStatus.Skipped,
                ErrorMessage = "PackageFullName is missing; cannot remove this package."
            };

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

            if (result.ExitCode == 0)
                return new UninstallResult { Item = item, Status = UninstallStatus.Succeeded };

            var errorText = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"PowerShell exited with code {result.ExitCode}."
                : result.StandardError;

            return new UninstallResult
            {
                Item = item,
                Status = UninstallStatus.Failed,
                ExitCode = result.ExitCode,
                ErrorMessage = errorText
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
}
