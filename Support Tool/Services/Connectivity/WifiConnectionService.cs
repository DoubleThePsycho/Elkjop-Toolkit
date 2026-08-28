using System.Diagnostics;
using System.IO;
using System.Text;
using Toolkit.Shared;
using Toolkit.Shared.Configuration;

namespace Support_Tool.Services;

// Both public (not internal, unlike Setup Agent's original) - IWifiConnectionService is a
// WindowsUpdatePageViewModel public constructor parameter, and its own method returns this enum.
public enum WifiConnectionOutcome
{
    NoCredentialsStaged,
    Connected,
    ConnectFailed,
}

// Adapted from Setup Agent's WifiConnectionService: the credential source is now the unified
// staged config's Connection section (Toolkit.Shared.Configuration.ConnectionConfig) instead of
// a bespoke WifiCredentials.json handoff file - the caller (Phase 2+) passes it in directly
// rather than this service reading a file path off AppInfo. Everything else - retry/poll
// semantics, the transient profile XML lifecycle - is unchanged.
public interface IWifiConnectionService
{
    WifiConnectionOutcome TryConnect(ConnectionConfig connection, Logger logger);
}

// Real implementation always attempts a connection if a Wi-Fi name is configured - not gated on
// "have I already succeeded once". Matches Setup Agent's own 2026-08-13 behavior: assume the
// device may be missing its WLAN driver entirely at first logon, and keep retrying at every
// single call - not just after a prior failure - since the first Windows Update might be
// exactly what installs that driver. The WLAN profile XML itself (WifiProfileBuilder output) is
// rebuilt fresh into a transient local file on every attempt and deleted right after, regardless
// of outcome - only the source ConnectionConfig persists across attempts (owned by the caller).
internal sealed class RealWifiConnectionService : IWifiConnectionService
{
    public WifiConnectionOutcome TryConnect(ConnectionConfig connection, Logger logger)
    {
        if (string.IsNullOrWhiteSpace(connection.WifiName))
        {
            return WifiConnectionOutcome.NoCredentialsStaged;
        }

        // Per-machine scratch file, not part of the portable/staged config or session model -
        // deliberately kept out of Toolkit.Shared.RuntimeStaging for Phase 1 (nothing here
        // touches the Runtime/session contract yet).
        var profilePath = Path.Combine(ToolkitPaths.GetLocalFolder(AppInfo.ToolName), "WifiProfile.xml");

        try
        {
            var profileContent = WifiProfileBuilder.Build(connection.WifiName, connection.WifiPassword);
            File.WriteAllText(profilePath, profileContent, new UTF8Encoding(true));

            RunNetsh($"wlan add profile filename=\"{profilePath}\" user=all", logger);
            RunNetsh($"wlan connect name=\"{connection.WifiName}\"", logger);

            // netsh's own exit code doesn't reliably reflect whether association actually
            // succeeded - poll real connectivity briefly instead of trusting it directly.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                Thread.Sleep(2000);
                if (ConnectivityService.HasInternet())
                {
                    logger.Info($"Connected to Wi-Fi network '{connection.WifiName}'.");
                    return WifiConnectionOutcome.Connected;
                }
            }

            logger.Warn($"Wi-Fi connect attempt for '{connection.WifiName}' did not result in internet connectivity. Will retry on next attempt.");
            return WifiConnectionOutcome.ConnectFailed;
        }
        finally
        {
            // Transient - only the source ConnectionConfig needs to survive for the next retry.
            try
            {
                File.Delete(profilePath);
            }
            catch (Exception ex)
            {
                logger.Warn($"Failed to remove transient Wi-Fi profile file: {ex.Message}");
            }
        }
    }

    private static void RunNetsh(string arguments, Logger logger)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "netsh.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            process.WaitForExit();
            logger.Info($"netsh {arguments} -> exit code {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to run 'netsh {arguments}': {ex.Message}");
        }
    }
}

internal sealed class DryRunWifiConnectionService : IWifiConnectionService
{
    public WifiConnectionOutcome TryConnect(ConnectionConfig connection, Logger logger)
    {
        if (string.IsNullOrWhiteSpace(connection.WifiName))
        {
            return WifiConnectionOutcome.NoCredentialsStaged;
        }

        logger.Info("[DRY RUN] Would build a WLAN profile from the configured Wi-Fi connection and run netsh wlan add profile / connect.");
        return WifiConnectionOutcome.ConnectFailed;
    }
}
