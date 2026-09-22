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
#if COLOR
        // DEVELOPMENT-ONLY preview entry point (see CLAUDE.md's "Color preview build" section) -
        // unconditionally returns before any of the real production startup below ever runs, so
        // a Color build can never perform elevation, the OOBE guard, config loading, language/
        // system discovery, AccountSetupInstanceLease, Runtime staging, Unattend generation,
        // Sysprep, the account bypass fallback, or any filesystem/config write - regardless of
        // what the rest of this method does.
        return StartColorPreview();
#endif

        // Explicit Application (no App.xaml/StartupUri - control flow stays procedural).
        // OnExplicitShutdown so closing a dialog never tears WPF down mid-flow. The shared
        // Elkjøp theme is merged in SetupWindow.xaml's own Window.Resources now, not here - see
        // its own remarks for why (the VS XAML designer never runs this method).
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

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

#if COLOR
    // DEVELOPMENT-ONLY preview startup (see CLAUDE.md's "Color preview build" section). Shows the
    // REAL SetupWindow with deterministic synthetic data using its dedicated Color-only
    // constructor - never constructs a Logger (which would itself create
    // C:\ProgramData\Elkjop\Toolkit\Account Setup\ via ToolkitLogger/ToolkitPaths.GetLocalFolder,
    // a real filesystem mutation this preview must never perform just to render) and marks
    // isColorPreview so StartButton_Click there short-circuits before ever reaching
    // UsernameValidator (which shells out to net.exe)/Dialogs/Runtime/Sysprep. Cancel/close just
    // close this dialog, same as production.
    private static int StartColorPreview()
    {
        // The shared theme is merged in SetupWindow.xaml's own Window.Resources now, not here -
        // see its own remarks.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var windowsInfo = new WindowsInfo
        {
            ProductName = "Windows 11 Pro",
            EditionId = "Professional",
            DisplayVersion = "24H2",
            Build = "26100",
            InstallationType = "Client",
        };

        var languages = new List<LanguageItem>
        {
            new() { Tag = "nb-NO", DisplayName = "Norwegian (Bokmål)" },
            new() { Tag = "en-US", DisplayName = "English (United States)" },
            new() { Tag = "en-GB", DisplayName = "English (United Kingdom)" },
            new() { Tag = "sv-SE", DisplayName = "Swedish" },
        };
        var languageInfo = new LanguageInfo
        {
            Languages = languages,
            DefaultTag = "nb-NO",
            DetectionMode = "Color preview (synthetic)",
        };

        var window = new SetupWindow(windowsInfo, "X64", languageInfo, "Customer", isColorPreview: true)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

        PositionColorPreviewWindow(window);

        window.ShowDialog();

        app.Shutdown();
        return 0;
    }

    // Slot 0 of the shared 3-window preview layout - always the work area's top-left corner in
    // both the side-by-side and stacked fallback layouts Support Tool's own matching preview
    // computes for itself (slot 1) and RebootCountdownWindow (slot 2), so no cross-process
    // coordination beyond this one shared corner is needed for all three to line up. See Support
    // Tool/App.xaml.cs's own PositionColorPreviewWindows for the full layout algorithm and the
    // matching width/gap constants.
    private static void PositionColorPreviewWindow(Window window)
    {
        const double Gap = 16;

        var workArea = SystemParameters.WorkArea;
        window.Left = workArea.Left + Gap;
        window.Top = workArea.Top + Gap;
    }
#endif

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
