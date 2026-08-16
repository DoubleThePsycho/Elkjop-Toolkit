using System.Diagnostics;
using Support_Tool.Models;

namespace Support_Tool.Services;

/// <summary>
/// Runs `winget install` for a new app, or `winget upgrade` when the caller already
/// knows an update is available — winget's own `install` command doesn't reliably
/// double as upgrade across versions, so the verb is chosen explicitly here.
/// </summary>
public class RealInstallExecutor : IInstallExecutor
{
    private readonly IProcessRunner _processRunner;

    public RealInstallExecutor(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<InstallResult> InstallAsync(InstallableApp app, AppInstallState currentState, CancellationToken ct = default)
    {
        var verb = currentState == AppInstallState.UpdateAvailable ? "upgrade" : "install";

        var psi = new ProcessStartInfo
        {
            FileName = "winget",
            Arguments = $"{verb} --id {app.WingetId} --exact --silent " +
                        "--accept-package-agreements --accept-source-agreements --disable-interactivity",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            var result = await _processRunner.RunAsync(psi, ct);

            if (result.ExitCode == 0)
                return new InstallResult { App = app, Status = InstallOutcome.Succeeded };

            var errorText = string.IsNullOrWhiteSpace(result.StandardError)
                ? $"winget exited with code {result.ExitCode}."
                : result.StandardError;

            return new InstallResult
            {
                App = app,
                Status = InstallOutcome.Failed,
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
            return new InstallResult
            {
                App = app,
                Status = InstallOutcome.Failed,
                ErrorMessage = $"Failed to start winget: {ex.Message}"
            };
        }
    }
}
