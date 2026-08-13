using Support_Tool.Models;

namespace Support_Tool.Services;

/// <summary>
/// Launches a program's registered UninstallString from the registry.
/// Reports Started because many installers spawn a child process and exit before
/// the actual removal is complete.
/// </summary>
public class ProgramUninstallExecutor : IUninstallExecutor
{
    private readonly IProcessRunner _processRunner;

    public ProgramUninstallExecutor(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<UninstallResult> UninstallAsync(InstalledItem item, CancellationToken ct = default)
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

            // Many uninstallers launch a child and exit quickly; we report Started
            // rather than Succeeded to avoid false certainty.
            return new UninstallResult
            {
                Item = item,
                Status = UninstallStatus.Started,
                ExitCode = result.ExitCode
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
                ErrorMessage = ex.Message
            };
        }
    }
}
