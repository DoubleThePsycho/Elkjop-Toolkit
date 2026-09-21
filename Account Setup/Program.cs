using System.Diagnostics;
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

            // Phase 5: refuse immediately if another Account Setup instance is already running -
            // closes the double-launch window the unified dispatcher's own release-before-launch
            // design otherwise leaves open. See AccountSetupInstanceLease's own remarks.
            using var accountSetupLease = AccountSetupInstanceLease.TryAcquire();
            if (accountSetupLease is null)
            {
                logger.Info("Another Account Setup instance is already running. Refusing to start a second one.");
                Dialogs.Show("Account Setup is already running.");
                return 0;
            }

#if !DEBUG
            if (!OobeGuard.IsOobeInProgress())
            {
                logger.Info("OOBE has already completed on this system. Refusing to run.");
                Dialogs.Show("This tool cannot be used on a live system. It only runs during Windows OOBE, before initial setup has completed.");
                return 0;
            }
#endif

            // Phase 4B: strict load of the canonical unified config - this is now load-bearing
            // (staged verbatim into Runtime\config.json for Support Tool to run against), so
            // missing/corrupt/incomplete throws here and is presented visibly by the outer
            // catch below, exactly like any other fatal startup failure. Never falls back to
            // either legacy per-tool config file.
            var loadedConfig = CanonicalConfigService.Load();
            logger.Info($"Loaded canonical config.json. Default username: '{loadedConfig.Config.Account.Username}'; Wi-Fi configured: {!string.IsNullOrWhiteSpace(loadedConfig.Config.Connection.WifiName)}.");

            var windowsInfo = WindowsInfoService.GetWindowsInformation();
            var architecture = WindowsInfoService.GetUnattendArchitecture();
            var internationalInfo = LanguageService.GetInternationalInformation(logger);
            var languageInfo = LanguageService.GetAvailableUiLanguages(internationalInfo.UiLanguage, logger);

            logger.Info($"Windows: {windowsInfo.ProductName}; Edition: {windowsInfo.EditionId}; Version: {windowsInfo.DisplayVersion}; Build: {windowsInfo.Build}; Installation type: {windowsInfo.InstallationType}");
            logger.Info($"Architecture: {architecture}.");
            logger.Info($"Current international settings: UI={internationalInfo.UiLanguage}; System={internationalInfo.SystemLocale}");
            logger.Info($"Available display languages ({languageInfo.Languages.Count}) via {languageInfo.DetectionMode}: {string.Join(", ", languageInfo.Languages.Select(l => l.Tag))}");

            var window = new SetupWindow(windowsInfo, architecture, languageInfo, logger, loadedConfig.Config.Account.Username);
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
            // Written into Runtime later, as part of SupportToolStagingService's own staging
            // transaction (after stale cleanup, not before) - not here.
            XDocument.Parse(unattendContent);

            // Phase 4B: required final Runtime staging - the live post-OOBE path. Throws
            // RuntimeStagingException on any failure, caught by its own dedicated handler below
            // (shown visibly, Windows left in OOBE) - deliberately NOT the generic handler's
            // account-bypass fallback, and Sysprep is never reached on failure either way, since
            // an exception here skips straight past it to the catch blocks.
            SupportToolStagingService.StageRuntime(architecture, username, unattendContent, loadedConfig, logger);

            if (prepareOnly)
            {
                logger.Info("PrepareOnly was specified. Sysprep was not started.");
                Dialogs.Show($"Preparation completed without restarting.\r\n\r\nUnattend: {RuntimeStaging.UnattendPath}\r\nLog: {logger.LogPath}");
                return 0;
            }

            var sysprepPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Sysprep", "Sysprep.exe");
            if (!File.Exists(sysprepPath))
            {
                throw new FileNotFoundException($"Sysprep was not found at '{sysprepPath}'.");
            }

            logger.Info($"Starting Sysprep with /oobe /reboot /quiet /unattend:{RuntimeStaging.UnattendPath}");

            using var sysprep = Process.Start(new ProcessStartInfo
            {
                FileName = sysprepPath,
                Arguments = $"/oobe /reboot /quiet /unattend:\"{RuntimeStaging.UnattendPath}\"",
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

        // Caught separately from, and before, the generic handler below: a final Runtime staging
        // failure must leave Windows in OOBE for manual recovery - never Sysprep, and never the
        // legacy account-bypass fallback (which can itself set BypassNRO and restart Windows).
        // That fallback exists for the Unattend/Sysprep-path failures the generic handler below
        // still covers, not for "the post-OOBE tool couldn't be prepared at all". See
        // RuntimeStagingException's own remarks - SupportToolStagingService guarantees every
        // failure it can produce emerges as this exact type, so this catch is never bypassed by
        // an unexpected exception being misclassified into the generic handler instead.
        catch (RuntimeStagingException ex)
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

            Dialogs.Show(
                $"Account Setup could not prepare the final Support Tool Runtime:\r\n\r\n{ex.Message}\r\n\r\nLog: {logger.LogPath}\r\n\r\nWindows has been left in OOBE for manual recovery - Sysprep was not run.",
                icon: MessageBoxImage.Error);

            return 1;
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
