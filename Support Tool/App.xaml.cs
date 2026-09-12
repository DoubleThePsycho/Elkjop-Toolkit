using System.Windows;
using Support_Tool.Services;
using Support_Tool.ViewModels;
using Toolkit.Shared;
using Toolkit.Shared.Configuration;

namespace Support_Tool;

public partial class App : Application
{
    // Held for the application's whole lifetime, acquired and disposed on this same (UI) thread
    // - a Mutex requires ReleaseMutex() to be called from the acquiring thread, which is why this
    // can't move to a background thread. See RuntimeProcessLease's own remarks.
    private RuntimeProcessLease? _processLease;

    // async void is normally avoided, but it's the only viable shape here: Application.OnStartup
    // is a protected void override with a signature this class doesn't control, and WPF's own
    // startup sequence (Application.Run -> OnStartup -> begin pumping the Dispatcher message
    // loop) is specifically designed to accommodate it - control returns to Application.Run at
    // the first await below (the dispatcher loop then starts pumping), and each continuation
    // resumes back on this same UI thread via the captured DispatcherSynchronizationContext once
    // it is. The previous version used sessionService.LoadAsync().GetAwaiter().GetResult() here,
    // reasoning that "no SynchronizationContext exists yet" - that was wrong: OnStartup already
    // runs under WPF's dispatcher context, so blocking synchronously on an async call whose
    // continuation needs to resume on that exact thread is a textbook sync-over-async deadlock.
    // It presented as SupportTool.exe staying alive (Task Scheduler: Running) with no window ever
    // appearing (MainWindowHandle=0) - execution never reached MainWindow.Show() because the
    // first awaited continuation could never resume on the thread that was blocked waiting for
    // it.
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Synchronous, runs to completion before this method's first await - still genuinely the
        // first thing to execute on this thread, before the dispatcher message loop begins
        // pumping (Application.Run hasn't gotten control back yet), exactly as before.
        _processLease = RuntimeSessionGuard.AcquireForProcessLifetime();
        if (_processLease is null)
        {
            MessageBox.Show(
                "Another Elkjop Toolkit session is already active on this machine. Close it before starting a new one.",
                "Elkjop Toolkit",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        var sessionService = new SessionService();
        var configService = new ConfigService();
        SessionState session;

        try
        {
            // Properly awaited (not blocked on) - see this method's own remarks on why the
            // previous GetAwaiter().GetResult() shape deadlocked here specifically.
            session = await sessionService.LoadAsync();
            await configService.LoadAsync();
        }
        catch (Exception ex) when (ex is SessionLoadException or ConfigLoadException)
        {
            MessageBox.Show(
                $"Elkjop Toolkit could not start: {ex.Message}",
                "Elkjop Toolkit - startup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _processLease.Dispose();
            _processLease = null;
            Shutdown(1);
            return;
        }

        var logger = new Logger();
        var sharedState = new SharedViewState();
        var runtimeCleanupService = new RuntimeCleanupService(logger);

        var ruleService    = new RuleService(configService);
        var restoreService = new DryRunRestorePointService();

        var dryRunExecutor = new DryRunUninstallExecutor();
        var realExecutor   = new RealUninstallExecutor(new ProcessRunner());
        var uninstallService = new UninstallService(dryRunExecutor, realExecutor, sharedState);

        var wingetStatusChecker  = new WingetStatusChecker(new ProcessRunner());
        var dryRunInstallExecutor = new DryRunInstallExecutor();
        var realInstallExecutor   = new RealInstallExecutor(new ProcessRunner());
        var installService = new InstallService(dryRunInstallExecutor, realInstallExecutor, sharedState);
        var installCatalogService = new InstallCatalogService(configService);
        var iconCacheService = new AppIconCacheService();

        var appsVm = new DebloaterPageViewModel(
            new AppInventoryService(), ruleService, uninstallService, restoreService, sharedState);
        var programsVm = new ProgramsPageViewModel(
            new ProgramInventoryService(), ruleService, uninstallService, restoreService, sharedState);
        var installVm = new InstallPageViewModel(
            installCatalogService, wingetStatusChecker, installService, iconCacheService, sharedState);
        var settingsVm = new SettingsPageViewModel(sharedState);

        // Same single-instance-at-startup dry-run split Setup Agent itself used (as opposed to
        // Install/Uninstall's own pattern of constructing both and picking per-call) - the ported
        // WUA engine's IWindowsUpdateExecutor/IWifiConnectionService/IRebootService are shaped
        // for exactly that, and this re-host preserves it rather than inventing a new selection
        // mechanism. DryRunRebootService never actually restarts the machine, which is exactly
        // what makes the Phase 2B session/task lifecycle testable from a DEBUG build without a
        // disposable VM - see dev-scripts/PHASE-2B-MANUAL-TESTS.md.
#if DEBUG
        IWindowsUpdateExecutor windowsUpdateExecutor = new DryRunWindowsUpdateExecutor();
        IWifiConnectionService wifiConnectionService = new DryRunWifiConnectionService();
        IRebootService rebootService = new DryRunRebootService();
#else
        IWindowsUpdateExecutor windowsUpdateExecutor = new RealWindowsUpdateExecutor();
        IWifiConnectionService wifiConnectionService = new RealWifiConnectionService();
        IRebootService rebootService = new RealRebootService();
#endif
        var lifecycleCoordinator = new WindowsUpdateLifecycleCoordinator(sessionService, rebootService, logger, sharedState);

        var windowsUpdateVm = new WindowsUpdatePageViewModel(
            windowsUpdateExecutor, wifiConnectionService, configService, sharedState,
            lifecycleCoordinator.HandleOutcomeAsync, lifecycleCoordinator.HandleRebootNowRequestedAsync)
        {
            // Fixed for this process's whole lifetime (SessionState.Workflow never changes) -
            // only ever used to pick the workflow-appropriate header subtitle.
            Workflow = session.Workflow,
        };

        // The coordinator needs to reflect the reboot-deferred state back onto this same shared
        // ViewModel (see WindowsUpdateLifecycleCoordinator.Page's own remarks) - set here, after
        // both objects exist, rather than via a constructor parameter, since the coordinator had
        // to exist first to supply windowsUpdateVm's own onOutcome/onRebootNowRequested delegates
        // above.
        lifecycleCoordinator.Page = windowsUpdateVm;

        var mainVm = new MainViewModel(
            sharedState,
            sessionService,
            runtimeCleanupService,
            new ServicePageViewModel(),
            installVm,
            appsVm,
            programsVm,
            settingsVm,
            windowsUpdateVm);

        // Wired after MainViewModel exists (its own navigation commands are what the coordinator
        // needs to trigger) rather than the coordinator taking a MainViewModel dependency itself -
        // avoids a construction-order cycle (MainViewModel needs windowsUpdateVm to already
        // exist, which needs the coordinator's delegate to already exist).
        lifecycleCoordinator.SetupAdvancedPastWindowsUpdate += () => mainVm.NavigateInstallCommand.Execute(null);
        lifecycleCoordinator.ServiceReturnedToNormal += () => mainVm.NavigateServiceCommand.Execute(null);

        var window = new MainWindow { DataContext = mainVm };

        var autoStartWindowsUpdate = ResolveStartupNavigation(mainVm, session, logger);

        window.Show();

        // Fire-and-forget from OnStartup's perspective - neither of these may block window
        // display. StartMenuDismisser's own watch loop runs on its own background thread
        // regardless; auto-starting Windows Update reuses the exact same command a technician
        // clicking "Check for updates" would invoke.
        if (session.Workflow == ToolkitWorkflow.Setup && !session.FirstDesktopActionsCompleted)
        {
            _ = RunFirstDesktopActionsAsync(sessionService, logger);
        }

        if (autoStartWindowsUpdate)
        {
            windowsUpdateVm.StartCommand.Execute(null);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _processLease?.Dispose();
        base.OnExit(e);
    }

    // Resolves which page Support Tool opens to, per the approved workflow/startup rules: Setup
    // always follows the persisted Stage regardless of ResumeRequired (so an abnormal exit that
    // never set ResumeRequired still returns to the right page on the next qualifying logon);
    // Service only diverges from its normal Home entry when ResumeRequired is true. Returns
    // whether the Windows Update page should immediately auto-start its check (Setup always,
    // since it's a mandatory unattended gate; Service only when resuming a pending reboot - a
    // normal Service visit to Windows Update stays technician-initiated).
    //
    // Windows Update is a real standalone page (MainViewModel.NavigateWindowsUpdateCommand) -
    // Setup navigates straight to it rather than to any Setup-specific wrapper page. Setup
    // remains the workflow identity and the mandatory-gate policy owner (auto-starting the check,
    // and WindowsUpdateLifecycleCoordinator refusing to advance Stage until it's genuinely done),
    // it just isn't a visual shell around the page.
    private static bool ResolveStartupNavigation(MainViewModel mainVm, SessionState session, Logger logger)
    {
        if (session.Workflow == ToolkitWorkflow.Setup)
        {
            switch (session.Stage)
            {
                case ToolkitStage.WindowsUpdate:
                    mainVm.NavigateWindowsUpdateCommand.Execute(null);
                    return true;

                case ToolkitStage.Install:
                    mainVm.NavigateInstallCommand.Execute(null);
                    return false;

                default:
                    // Debloat/Customization/Finish are not implemented yet - nothing in this
                    // phase ever advances Stage that far. Purely defensive.
                    logger.Warn($"Setup session persisted Stage '{session.Stage}', which has no page yet - falling back to Home.");
                    mainVm.NavigateHomeCommand.Execute(null);
                    return false;
            }
        }

        if (session.ResumeRequired)
        {
            mainVm.NavigateWindowsUpdateCommand.Execute(null);
            return true;
        }

        mainVm.NavigateHomeCommand.Execute(null);
        return false;
    }

    private static async Task RunFirstDesktopActionsAsync(SessionService sessionService, Logger logger)
    {
        // Identical window to Setup Agent's own original call - not re-tuned as part of this
        // port.
        await StartMenuDismisser.WatchAndDismissAsync(TimeSpan.FromSeconds(60), logger);

        try
        {
            await sessionService.MutateAsync(s => s.FirstDesktopActionsCompleted = true);
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to persist FirstDesktopActionsCompleted: {ex.Message}");
        }
    }
}
