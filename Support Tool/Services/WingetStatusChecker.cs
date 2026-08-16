using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Support_Tool.Services;

using Support_Tool.Models;

/// <summary>
/// Read-only, so unlike the uninstall/install executors there's no DryRun variant —
/// checking status never changes anything, same reasoning as AppInventoryService/
/// ProgramInventoryService.
/// </summary>
public class WingetStatusChecker
{
    private readonly IProcessRunner _processRunner;

    public WingetStatusChecker(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task<AppInstallState> GetStateAsync(string wingetId, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "winget",
            Arguments = $"list --id {wingetId} --exact --accept-source-agreements --disable-interactivity",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            var result = await _processRunner.RunAsync(psi, ct);

            if (result.ExitCode != 0)
                return AppInstallState.NotInstalled;

            return HasAvailableColumn(result.StandardOutput)
                ? AppInstallState.UpdateAvailable
                : AppInstallState.Installed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WingetStatusChecker] Failed to check '{wingetId}': {ex.Message}");
            return AppInstallState.NotInstalled;
        }
    }

    // winget's fixed-width table gains one extra "Available" column (Name/Id/Version/
    // Available/Source instead of Name/Id/Version/Source) only when an update exists —
    // detected by field count on the data row, not by matching English header text, so
    // this holds regardless of the machine's display language. Verified against real
    // `winget list --id <id> --exact` output for both an up-to-date and an outdated
    // package before relying on this.
    private static bool HasAvailableColumn(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length < 3)
            return false;

        var fields = Regex.Split(lines[2], @"\s{2,}");
        return fields.Length >= 5;
    }
}
