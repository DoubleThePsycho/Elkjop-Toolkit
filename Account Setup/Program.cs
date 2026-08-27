using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Xml.Linq;
using Account_Setup.Models;
using Account_Setup.Services;
using Account_Setup.Views;
using Toolkit.Shared;

namespace Account_Setup;

internal static class Program
{
    // Synchronous, not async Task<int>: a WinExe Main has no SynchronizationContext, so after
    // the first real await everything used to resume on a thread-pool MTA thread - WinForms
    // quietly tolerated constructing the form there, but WPF hard-enforces STA in every
    // DispatcherObject constructor. The one await (config load) is blocked on instead, which is
    // safe here: no dispatcher frame or sync-context exists yet, so there is nothing to
    // deadlock against. DPI (PerMonitorV2) moved from the deleted SetHighDpiMode call into
    // app.manifest.
    [STAThread]
    private static int Main(string[] args)
    {
        // Explicit Application (no App.xaml/StartupUri - control flow stays procedural).
        // OnExplicitShutdown so closing a dialog never tears WPF down mid-flow; it also hosts
        // the shared Elkjøp theme so every window resolves {StaticResource ...} without
        // per-window merges.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/Themes/ElkjopTheme.xaml"),
        });

        var prepareOnly = HasFlag(args, "prepareonly", "prepare-only");

        var logger = new Logger();
        string? username = null;

        try
        {
            logger.Info($"Account Setup {AppInfo.ScriptVersion} started. Executable path: '{Environment.ProcessPath}'.");

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

            var config = AppConfigService.LoadAsync().GetAwaiter().GetResult();
            logger.Info($"Loaded config.json. Default username: '{config.Account.Username}'; Wi-Fi configured: {!string.IsNullOrWhiteSpace(config.Connection.WifiName)}.");

            var windowsInfo = WindowsInfoService.GetWindowsInformation();
            var architecture = WindowsInfoService.GetUnattendArchitecture();
            var internationalInfo = LanguageService.GetInternationalInformation(logger);
            var languageInfo = LanguageService.GetAvailableUiLanguages(internationalInfo.UiLanguage, logger);

            logger.Info($"Windows: {windowsInfo.ProductName}; Edition: {windowsInfo.EditionId}; Version: {windowsInfo.DisplayVersion}; Build: {windowsInfo.Build}; Installation type: {windowsInfo.InstallationType}");
            logger.Info($"Architecture: {architecture}.");
            logger.Info($"Current international settings: UI={internationalInfo.UiLanguage}; System={internationalInfo.SystemLocale}");
            logger.Info($"Available display languages ({languageInfo.Languages.Count}) via {languageInfo.DetectionMode}: {string.Join(", ", languageInfo.Languages.Select(l => l.Tag))}");

            var window = new SetupWindow(windowsInfo, architecture, languageInfo, logger, config.Account.Username);
            var setupSelection = window.ShowDialog() == true ? window.Selection : null;

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
                // Toolkit.Shared.AccountSetupStaging.WifiCredentialsPath for why).
                var credentialsContent = JsonSerializer.Serialize(new { config.Connection.WifiName, config.Connection.WifiPassword });
                File.WriteAllText(AppInfo.WifiCredentialsPath, credentialsContent, new UTF8Encoding(true));
                logger.Info($"Staged Wi-Fi credentials for network '{config.Connection.WifiName}': '{AppInfo.WifiCredentialsPath}'.");
            }

            StageSetupAgent(architecture, logger);

            var taskXmlContent = Account_Setup.Services.ScheduledTaskXmlBuilder.Build(username, AppInfo.SetupAgentExePath);
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

            Dialogs.Show($"Account Setup failed:\r\n\r\n{ex.Message}\r\n\r\nLog: {logger.LogPath}", icon: MessageBoxImage.Error);

            if (!string.IsNullOrWhiteSpace(username))
            {
                AccountBypassFallbackService.Run(username, logger);
            }

            return 1;
        }
    }

    // Setup Agent lives as a sibling per-architecture publish folder next to this exe's own
    // (".../Elkjop Toolkit/Setup Agent/Setup Agent-<rid>.exe" next to ".../Elkjop Toolkit/Account
    // Setup/"), matching the layout setup.exe assumes when it picks this exe's own per-arch
    // build. Staged into the same ProgramData Temp folder as Unattend.xml so it survives the
    // reboot Sysprep triggers.
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
