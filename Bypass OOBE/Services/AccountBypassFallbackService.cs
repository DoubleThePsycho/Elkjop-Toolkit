using System.Diagnostics;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Bypass_OOBE.Services;

internal static class AccountBypassFallbackService
{
    public static void Run(string username, Logger logger)
    {
        logger.Warn("Beginning Microsoft-account bypass fallback chain.");

        var fallbackPrompt =
            "The Unattend/Sysprep path failed.\r\n\r\n" +
            "Try the local-account fallback chain now?\r\n\r\n" +
            "The first fallback may open a manual account page. Use this username:\r\n" +
            username;

        var answer = Dialogs.Show(fallbackPrompt, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

        if (answer != DialogResult.Yes)
        {
            logger.Warn("Fallback chain declined by user.");
            return;
        }

        // 1. ms-cxh:localonly
        try
        {
            logger.Info("Fallback 1: attempting ms-cxh:localonly.");
            Process.Start(new ProcessStartInfo("ms-cxh:localonly") { UseShellExecute = true });
            logger.Info("Fallback 1 was handed to the registered URI handler.");
            Dialogs.Show(
                $"The local-account page was opened. Use '{username}' if Windows asks for a username.",
                icon: MessageBoxIcon.Information);
            return;
        }
        catch (Exception ex)
        {
            logger.Warn($"Fallback 1 failed: {ex.Message}");
        }

        // 2. OOBE\BYPASSNRO
        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var bypassNroCandidates = new[]
        {
            Path.Combine(systemRoot, "System32", "OOBE", "BypassNRO.cmd"),
            Path.Combine(systemRoot, "System32", "OOBE", "BypassNRO.exe"),
        };

        foreach (var candidate in bypassNroCandidates)
        {
            if (File.Exists(candidate))
            {
                try
                {
                    logger.Info($"Fallback 2: starting '{candidate}'.");
                    Process.Start(new ProcessStartInfo(candidate) { UseShellExecute = true });
                    return;
                }
                catch (Exception ex)
                {
                    logger.Warn($"Fallback 2 candidate failed: {ex.Message}");
                }
            }
        }

        logger.Warn("Fallback 2 was unavailable.");

        // 3. Registry BypassNRO override
        try
        {
            logger.Info("Fallback 3: setting the BypassNRO registry value and restarting.");
            using var oobeKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\OOBE")
                ?? throw new InvalidOperationException("Could not open or create the OOBE registry key.");

            oobeKey.SetValue("BypassNRO", 1, RegistryValueKind.DWord);

            Process.Start(new ProcessStartInfo(Path.Combine(systemRoot, "System32", "shutdown.exe"))
            {
                Arguments = "/r /t 0",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            logger.Error($"Fallback 3 failed: {ex.Message}");
            Dialogs.Show($"All fallback methods failed.\r\n\r\nLog: {logger.LogPath}", icon: MessageBoxIcon.Error);
        }
    }
}
