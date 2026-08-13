using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using System.Xml.Linq;
using Bypass_OOBE.Forms;
using Bypass_OOBE.Models;
using Bypass_OOBE.Services;
using Toolkit.Shared;

namespace Bypass_OOBE;

internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var prepareOnly = HasFlag(args, "prepareonly", "prepare-only");

        var logger = new Logger();
        string? username = null;

        try
        {
            logger.Info($"SetupOOBE {AppInfo.ScriptVersion} started. Executable path: '{Application.ExecutablePath}'.");

            if (!ElevationHelper.IsElevated())
            {
                throw new InvalidOperationException("This program must run as Administrator. During OOBE, launch it from Shift+F10.");
            }

#if !DEBUG
            if (!OobeGuard.IsOobeInProgress())
            {
                logger.Info("OOBE has already completed on this system. Refusing to run.");
                Dialogs.Show("This tool cannot be used on a live system. It only runs during Windows OOBE, before initial setup has completed.");
                return 0;
            }
#endif

            var config = await AppConfigService.LoadAsync();
            logger.Info($"Loaded config.json. Default username: '{config.Account.Username}'; Wi-Fi configured: {!string.IsNullOrWhiteSpace(config.Connection.WifiName)}.");

            var windowsInfo = WindowsInfoService.GetWindowsInformation();
            var architecture = WindowsInfoService.GetUnattendArchitecture();
            var internationalInfo = LanguageService.GetInternationalInformation(logger);
            var languageInfo = LanguageService.GetAvailableUiLanguages(internationalInfo.UiLanguage, logger);

            logger.Info($"Windows: {windowsInfo.ProductName}; Edition: {windowsInfo.EditionId}; Version: {windowsInfo.DisplayVersion}; Build: {windowsInfo.Build}; Installation type: {windowsInfo.InstallationType}");
            logger.Info($"Architecture: {architecture}.");
            logger.Info($"Current international settings: UI={internationalInfo.UiLanguage}; System={internationalInfo.SystemLocale}");
            logger.Info($"Available display languages ({languageInfo.Languages.Count}) via {languageInfo.DetectionMode}: {string.Join(", ", languageInfo.Languages.Select(l => l.Tag))}");

            SetupSelection? setupSelection;
            using (var form = new SetupForm(windowsInfo, architecture, languageInfo, logger, config.Account.Username))
            {
                setupSelection = form.ShowDialog() == DialogResult.OK ? form.Selection : null;
            }

            if (setupSelection is null)
            {
                logger.Info("Setup cancelled in the setup dialog.");
                return 0;
            }

            username = setupSelection.Username;
            var password = setupSelection.Password;
            var uiLanguage = setupSelection.UiLanguage;
            var hasPassword = !string.IsNullOrEmpty(password);

            logger.Info($"Selected username: '{username}'.");
            logger.Info($"Password configured: {hasPassword}.");
            logger.Info($"Selected Windows display language: '{uiLanguage}'.");
            logger.Info("Keyboard, system locale, and user locale are fixed to Norwegian Bokmål.");

            var unattendContent = UnattendXmlBuilder.Build(username, password, uiLanguage, architecture);

            // Parse before writing so obvious XML errors are caught before Sysprep is called.
            XDocument.Parse(unattendContent);

            File.WriteAllText(AppInfo.UnattendPath, unattendContent, new UTF8Encoding(true));
            logger.Info($"Generated answer file: '{AppInfo.UnattendPath}'.");

            if (!string.IsNullOrWhiteSpace(config.Connection.WifiName))
            {
                // Raw credentials, not a pre-built WLAN profile XML - Setup Agent owns building
                // the actual profile and the whole connect/retry lifecycle itself (see
                // Toolkit.Shared.BypassOobeStaging.WifiCredentialsPath for why).
                var credentialsContent = JsonSerializer.Serialize(new { config.Connection.WifiName, config.Connection.WifiPassword });
                File.WriteAllText(AppInfo.WifiCredentialsPath, credentialsContent, new UTF8Encoding(true));
                logger.Info($"Staged Wi-Fi credentials for network '{config.Connection.WifiName}': '{AppInfo.WifiCredentialsPath}'.");
            }

            StageSetupAgent(architecture, logger);

            var taskXmlContent = ScheduledTaskXmlBuilder.Build(username, AppInfo.SetupAgentExePath);
            File.WriteAllText(AppInfo.SetupAgentTaskXmlPath, taskXmlContent, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            logger.Info($"Generated Setup Agent scheduled task definition: '{AppInfo.SetupAgentTaskXmlPath}'.");

            if (prepareOnly)
            {
                logger.Info("PrepareOnly was specified. Sysprep was not started.");
                Dialogs.Show($"Preparation completed without restarting.\r\n\r\nUnattend: {AppInfo.UnattendPath}\r\nLog: {logger.LogPath}");
                return 0;
            }

            var sysprepPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Sysprep", "Sysprep.exe");
            if (!File.Exists(sysprepPath))
            {
                throw new FileNotFoundException($"Sysprep was not found at '{sysprepPath}'.");
            }

            logger.Info($"Starting Sysprep with /oobe /reboot /quiet /unattend:{AppInfo.UnattendPath}");

            using var sysprep = Process.Start(new ProcessStartInfo
            {
                FileName = sysprepPath,
                Arguments = $"/oobe /reboot /quiet /unattend:\"{AppInfo.UnattendPath}\"",
                UseShellExecute = true,
            })!;
            sysprep.WaitForExit();

            logger.Info($"Sysprep process returned exit code {sysprep.ExitCode}.");

            if (sysprep.ExitCode != 0)
            {
                throw new InvalidOperationException($"Sysprep returned exit code {sysprep.ExitCode}.");
            }

            // Normally /reboot takes over immediately. This message is only visible if it does not.
            Dialogs.Show("Windows accepted the OOBE configuration. The computer should restart automatically.");

            return 0;
        }

        catch (Exception ex)
        {
            try
            {
                logger.Error(ex.Message);
                logger.Error(ex.ToString());
            }

            catch
            {
                // Avoid masking the original error if logging itself fails.
            }

            Dialogs.Show($"SetupOOBE failed:\r\n\r\n{ex.Message}\r\n\r\nLog: {logger.LogPath}", icon: MessageBoxIcon.Error);

            if (!string.IsNullOrWhiteSpace(username))
            {
                AccountBypassFallbackService.Run(username, logger);
            }

            return 1;
        }
    }

    // Setup Agent lives as a sibling per-architecture publish folder next to this exe's own
    // (".../Elkjop Toolkit/Setup Agent/Setup Agent-<rid>.exe" next to ".../Elkjop Toolkit/Bypass
    // OOBE/"), matching the layout Bypass OOBE.bat already assumes for itself. Staged into the
    // same flat ProgramData folder as Unattend.xml so it survives the reboot Sysprep triggers.
    private static void StageSetupAgent(string architecture, Logger logger)
    {
        var ridSuffix = architecture switch
        {
            "amd64" => "x64",
            "arm64" => "arm64",
            "x86" => "x86",
            _ => throw new InvalidOperationException($"Unsupported or unknown architecture: '{architecture}'."),
        };

        var toolkitFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        var sourcePath = Path.Combine(toolkitFolder, "Setup Agent", $"Setup Agent-{ridSuffix}.exe");

        if (!File.Exists(sourcePath))
        {
            logger.Warn($"Setup Agent executable not found at '{sourcePath}'. Wi-Fi retry and Windows Update automation will not run after reboot.");
            return;
        }

        File.Copy(sourcePath, AppInfo.SetupAgentExePath, overwrite: true);
        logger.Info($"Staged Setup Agent: '{AppInfo.SetupAgentExePath}'.");
    }

    private static bool HasFlag(string[] args, params string[] names)
    {
        foreach (var arg in args)
        {
            var trimmed = arg.TrimStart('-', '/');
            foreach (var name in names)
            {
                if (string.Equals(trimmed, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
