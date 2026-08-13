using System.Diagnostics;
using System.Text.Json;

namespace Setup_Agent.Services;

internal enum WifiConnectionOutcome
{
    NoCredentialsStaged,
    Connected,
    ConnectFailed,
}

// Matches the JSON shape Bypass OOBE writes to BypassOobeStaging.WifiCredentialsPath
// (property names, not types, are what matter for System.Text.Json's default matching).
internal sealed record WifiCredentials(string WifiName, string WifiPassword);

internal interface IWifiConnectionService
{
    WifiConnectionOutcome TryConnect(Logger logger);
}

// Real implementation always attempts a connection if credentials are staged - not gated on
// "have I already succeeded once", and never deletes the credentials file itself (only Setup
// Agent's own terminal cleanup does, see Program.DeleteSelf). Requested 2026-08-13: assume the
// device is a laptop that may be missing its WLAN driver entirely at first logon, and keep
// retrying Wi-Fi at every single launch - not just after a prior failure - since the first
// Windows Update might be exactly what installs that driver, and getting off a technician's
// temporary Ethernet cable and onto Wi-Fi is the actual goal, not just "online by some means."
// The WLAN profile XML itself (WifiProfileBuilder output) is rebuilt fresh into a transient
// local file on every attempt and deleted right after, regardless of outcome - only the source
// credentials persist across attempts.
internal sealed class RealWifiConnectionService : IWifiConnectionService
{
    public WifiConnectionOutcome TryConnect(Logger logger)
    {
        var credentials = LoadCredentials(logger);
        if (credentials is null)
        {
            return WifiConnectionOutcome.NoCredentialsStaged;
        }

        try
        {
            var profileContent = WifiProfileBuilder.Build(credentials.WifiName, credentials.WifiPassword);
            File.WriteAllText(AppInfo.WifiProfileXmlPath, profileContent, new System.Text.UTF8Encoding(true));

            RunNetsh($"wlan add profile filename=\"{AppInfo.WifiProfileXmlPath}\" user=all", logger);
            RunNetsh($"wlan connect name=\"{credentials.WifiName}\"", logger);

            // netsh's own exit code doesn't reliably reflect whether association actually
            // succeeded - poll real connectivity briefly instead of trusting it directly.
            for (var attempt = 0; attempt < 5; attempt++)
            {
                Thread.Sleep(2000);
                if (ConnectivityService.HasInternet())
                {
                    logger.Info($"Connected to Wi-Fi network '{credentials.WifiName}'.");
                    return WifiConnectionOutcome.Connected;
                }
            }

            logger.Warn($"Wi-Fi connect attempt for '{credentials.WifiName}' did not result in internet connectivity. Will retry on next launch.");
            return WifiConnectionOutcome.ConnectFailed;
        }
        finally
        {
            // Transient - only the credentials themselves need to survive for the next retry.
            try
            {
                File.Delete(AppInfo.WifiProfileXmlPath);
            }
            catch (Exception ex)
            {
                logger.Warn($"Failed to remove transient Wi-Fi profile file: {ex.Message}");
            }
        }
    }

    private static WifiCredentials? LoadCredentials(Logger logger)
    {
        if (!File.Exists(AppInfo.WifiCredentialsPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(AppInfo.WifiCredentialsPath);
            var credentials = JsonSerializer.Deserialize<WifiCredentials>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (credentials is null || string.IsNullOrWhiteSpace(credentials.WifiName))
            {
                return null;
            }

            return credentials;
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to read staged Wi-Fi credentials '{AppInfo.WifiCredentialsPath}': {ex.Message}");
            return null;
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
    public WifiConnectionOutcome TryConnect(Logger logger)
    {
        if (!File.Exists(AppInfo.WifiCredentialsPath))
        {
            return WifiConnectionOutcome.NoCredentialsStaged;
        }

        logger.Info("[DRY RUN] Would build a WLAN profile from the staged credentials and run netsh wlan add profile / connect.");
        return WifiConnectionOutcome.ConnectFailed;
    }
}
