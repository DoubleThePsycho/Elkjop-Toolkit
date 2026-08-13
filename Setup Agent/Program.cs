using System.Diagnostics;
using Setup_Agent.Forms;
using Setup_Agent.Services;
using Toolkit.Shared;

namespace Setup_Agent;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var logger = new Logger();

        try
        {
            logger.Info("Setup Agent started.");

            // Diagnostic for a real report: task ran successfully (exit 0x0) but no dialog was
            // ever visible on screen. The leading suspect is Session 0 isolation - a process
            // launched non-interactively (e.g. if Task Scheduler's InteractiveToken resolution
            // doesn't correctly attach to the real desktop session when fired via schtasks
            // /run) still runs and can still finish successfully, but nothing it draws is ever
            // visible to the actual logged-on user. SessionId 0 would confirm that theory; any
            // other value would point elsewhere.
            logger.Info($"Process session ID: {Process.GetCurrentProcess().SessionId}. Environment.UserInteractive: {Environment.UserInteractive}.");

            // The Start menu's auto-open is a one-time-per-account nudge, so there's no reason
            // to pay for a watch window on every relaunch across the Windows Update reboot
            // cycle - only worth doing on the genuine first-ever launch, marked by this file's
            // absence. Window is generous (60s) since it now only ever runs once.
            if (!File.Exists(AppInfo.FirstRunMarkerPath))
            {
                StartMenuDismisser.WatchAndDismiss(TimeSpan.FromSeconds(60), logger);
                File.WriteAllText(AppInfo.FirstRunMarkerPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }

            if (!ElevationHelper.IsElevated())
            {
                logger.Error("Setup Agent must run elevated (the scheduled task should register it with /rl highest). Exiting.");
                return;
            }

            using var tray = new TrayStatusService();

#if DEBUG
            IWifiConnectionService wifiService = new DryRunWifiConnectionService();
            IWindowsUpdateTrigger updateTrigger = new DryRunWindowsUpdateTrigger();
            IRebootService rebootService = new DryRunRebootService();
            IScheduledTaskService scheduledTaskService = new DryRunScheduledTaskService();
            logger.Info("DEBUG build: running in dry-run mode. No real Wi-Fi, update trigger, reboot, or scheduled-task action will be taken.");
#else
            IWifiConnectionService wifiService = new RealWifiConnectionService();
            IWindowsUpdateTrigger updateTrigger = new RealWindowsUpdateTrigger();
            IRebootService rebootService = new RealRebootService();
            IScheduledTaskService scheduledTaskService = new RealScheduledTaskService();
#endif

            tray.SetStatus("connecting Wi-Fi...");
            var wifiOutcome = wifiService.TryConnect(logger);
            logger.Info($"Wi-Fi connection attempt result: {wifiOutcome}.");

            if (!ConnectivityService.HasInternet())
            {
                if (!ConnectivityService.HasWifiAdapter())
                {
                    logger.Warn("No Wi-Fi adapter detected and no internet connection - likely a missing Wi-Fi driver. Prompting for Ethernet.");
                    tray.SetStatus("waiting for Ethernet cable...");

                    // Blocks until connectivity is actually established - no way to skip past
                    // it (see InternetAccessForm.cs). It transitions itself to the "Connection
                    // established" panel and runs its own countdown once online, so nothing
                    // further is needed here once ShowDialog returns.
                    using var internetForm = new InternetAccessForm(InternetAccessState.WaitingForConnection);
                    internetForm.ShowDialog();
                }
                else
                {
                    logger.Warn("Wi-Fi adapter present but not connected, and no internet available. Will retry on next logon/reboot.");
                    tray.SetStatus("no internet connection. Will retry later.");
                    return;
                }
            }
            else
            {
                logger.Info("Internet connectivity confirmed.");
                tray.SetStatus("connected. Preparing Windows Update...");

                // Explicit, visible checkpoint - both so a technician watching can see the
                // agent actually got this far (this was invisible before, which is why a
                // stalled/slow run and a genuinely hung one looked identical from the screen),
                // and per the user's request for a confirm-and-proceed step before touching
                // Windows Update.
                using var internetForm = new InternetAccessForm(InternetAccessState.ConnectionEstablished);
                internetForm.ShowDialog();
            }

            RunUpdateLoop(updateTrigger, rebootService, scheduledTaskService, tray, logger);
        }
        catch (Exception ex)
        {
            try
            {
                logger.Error(ex.ToString());
            }
            catch
            {
                // Avoid masking the original error if logging itself fails.
            }
        }
    }

    // Polling happens on a background thread while UpdateProgressForm's own message loop keeps
    // the marquee bar animating and the status text current on the main thread - otherwise the
    // whole pass would run with zero visible sign anything was happening, which is what
    // prompted this in the first place.
    private static void RunUpdateLoop(IWindowsUpdateTrigger updateTrigger, IRebootService rebootService, IScheduledTaskService scheduledTaskService, TrayStatusService tray, Logger logger)
    {
        using var progressForm = new UpdateProgressForm();
        Exception? backgroundError = null;
        var isFullyComplete = false;

        var worker = new Thread(() =>
        {
            SleepPreventionService.PreventSleep(logger);

            try
            {
                isFullyComplete = RunNativeUpdateCycle(updateTrigger, rebootService, scheduledTaskService, progressForm, tray, logger);
            }
            catch (Exception ex)
            {
                backgroundError = ex;
            }
            finally
            {
                // Released before this thread exits no matter which way the cycle ended
                // (reboot, cleanup, timeout, or an error) - see SleepPreventionService for why
                // this can't be left dangling even if something above throws.
                SleepPreventionService.AllowSleep(logger);
                progressForm.CloseFromBackground();
            }
        });

        // Matches the main thread's apartment state - WUA's COM objects are created on this
        // thread, and STA is the safer default assumption for COM interop generally.
        worker.SetApartmentState(ApartmentState.STA);
        worker.IsBackground = true;
        worker.Start();

        Application.Run(progressForm);

        if (backgroundError is not null)
        {
            throw backgroundError;
        }

        if (isFullyComplete)
        {
            // Shown here (main thread, after progressForm has already closed) rather than from
            // the worker thread that detected completion - progressForm's message loop and
            // this dialog's would otherwise be two separate STA UI threads running at once,
            // which is asking for Z-order/focus trouble for no benefit.
            using (var completeForm = new SetupCompleteForm())
            {
                completeForm.ShowDialog();
            }

            DeleteCredentials(logger);
            DeleteSelf(logger);
        }
    }

    // Wi-Fi credentials are deliberately kept around (not deleted on a successful connect -
    // see WifiConnectionService.cs) so Setup Agent can keep retrying Wi-Fi at every launch
    // until it's truly done. This is the only place they get cleaned up.
    private static void DeleteCredentials(Logger logger)
    {
#if DEBUG
        logger.Info($"[DRY RUN] Would delete staged Wi-Fi credentials: '{AppInfo.WifiCredentialsPath}'.");
#else
        try
        {
            if (File.Exists(AppInfo.WifiCredentialsPath))
            {
                File.Delete(AppInfo.WifiCredentialsPath);
                logger.Info($"Deleted staged Wi-Fi credentials: '{AppInfo.WifiCredentialsPath}'.");
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to delete staged Wi-Fi credentials: {ex.Message}");
        }
#endif
    }

    // Real report: the previous behavior left SetupAgent.exe sitting in ProgramData forever
    // after finishing, which the user flagged as unwanted on a customer-facing machine.
    // Windows allows deleting a running exe's file out from under itself - the delete is just
    // deferred until the last handle (this process) closes, so this is safe to call while
    // still running from that exact path. Gated like the other destructive actions so a DEBUG
    // run on a dev machine (if one ever reached this branch) can't delete the dev build.
    private static void DeleteSelf(Logger logger)
    {
#if DEBUG
        logger.Info($"[DRY RUN] Would delete own executable: '{Application.ExecutablePath}'.");
#else
        try
        {
            File.Delete(Application.ExecutablePath);
            logger.Info($"Deleted own executable: '{Application.ExecutablePath}'.");
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to delete own executable: {ex.Message}");
        }
#endif
    }

    // Windows' own Automatic Updates client does the actual downloading/installing now (see
    // WindowsUpdateService.cs) - Setup Agent just prompts it via DetectNow(), then watches
    // (read-only WUA searches + RebootRequired) until either nothing's left, a reboot is
    // needed, or a bounded wait runs out. Timing out isn't a dead end: the scheduled task
    // retries this whole thing at the next logon/reboot regardless.
    // How often the poll loop re-calls DetectNow() while updates are still pending, not just
    // once at the start - Windows Update showing a failed item with a manual "Retry" button
    // (observed on real hardware) needs a fresh detection nudge to move again, and there's no
    // WUA signal used here that reliably distinguishes "still working normally" from "stuck on
    // a failure" - periodically nudging regardless is simpler and covers both.
    private const int RetryNudgeEveryNPolls = 10; // 10 * 30s = 5 minutes

    /// <summary>Returns true if this run reached the terminal "fully up to date" state.</summary>
    private static bool RunNativeUpdateCycle(IWindowsUpdateTrigger updateTrigger, IRebootService rebootService, IScheduledTaskService scheduledTaskService, UpdateProgressForm progressForm, TrayStatusService tray, Logger logger)
    {
        SetStatus(progressForm, tray, "Searching for available updates...");
        var searchResult = WindowsUpdateSearcher.Search(logger);
        var previousTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        progressForm.SetPendingUpdates(BuildStatusEntries(searchResult, previousTitles, logger));
        previousTitles = new HashSet<string>(searchResult.Titles, StringComparer.OrdinalIgnoreCase);

        if (searchResult.Count > 0)
        {
            // Kept short and dynamic-count-free deliberately - the tray tooltip (which gets a
            // "Setup Agent: " prefix - see TrayStatusService) truncates past 63 characters, and
            // a count-bearing sentence blows through that easily. The exact count is still
            // fully captured in the log via WindowsUpdateSearcher.Search's own logging.
            SetStatus(progressForm, tray, "Updates found. Windows Update is installing...");
            var shouldWait = updateTrigger.TriggerNativeUpdateCycle(logger);

            if (shouldWait)
            {
                var deadline = DateTime.UtcNow.AddMinutes(45);
                var pollCount = 0;

                while (DateTime.UtcNow < deadline && !WindowsRebootStatus.IsRebootRequired())
                {
                    var remaining = WindowsUpdateSearcher.Search(logger);
                    progressForm.SetPendingUpdates(BuildStatusEntries(remaining, previousTitles, logger));
                    previousTitles = new HashSet<string>(remaining.Titles, StringComparer.OrdinalIgnoreCase);
                    SetStatus(progressForm, tray, "Windows Update is working...");

                    if (remaining.Count == 0)
                    {
                        break;
                    }

                    pollCount++;
                    if (pollCount % RetryNudgeEveryNPolls == 0)
                    {
                        logger.Info($"Still {remaining.Count} update(s) pending after {pollCount} checks - re-triggering detection in case something needs a retry.");
                        updateTrigger.TriggerNativeUpdateCycle(logger);
                    }

                    Thread.Sleep(TimeSpan.FromSeconds(30));
                }
            }
        }

        if (WindowsRebootStatus.IsRebootRequired())
        {
            SetStatus(progressForm, tray, "Restarting to continue...");
            logger.Info("Reboot required. Restarting - the scheduled task will continue after logon.");
            rebootService.RebootNow(logger);
            return false;
        }

        var finalCheck = WindowsUpdateSearcher.Search(logger);
        if (finalCheck.Count == 0)
        {
            SetStatus(progressForm, tray, "No updates remaining. Finishing up...");
            logger.Info("No eligible updates remaining. Running final service stage and cleaning up.");
            FinalServiceStage.Run(logger);
            scheduledTaskService.DeleteSelf(logger);
            return true;
        }

        SetStatus(progressForm, tray, "Still waiting on Windows Update. Will retry later.");
        logger.Warn($"Timed out waiting for Windows Update with {finalCheck.Count} update(s) still pending. Will retry on next logon/reboot.");
        return false;
    }

    // Tray tooltips are capped at 63 characters (see TrayStatusService), so it gets the same
    // text as the progress form even though it'll often show truncated - still enough to tell
    // at a glance that something's actively happening.
    private static void SetStatus(UpdateProgressForm progressForm, TrayStatusService tray, string text)
    {
        progressForm.SetStatus(text);
        tray.SetStatus(text);
    }

    // Turns a raw search result into what the table actually shows: Failed for anything with a
    // recent failed/aborted history entry (see WindowsUpdateSearcher.GetRecentlyFailedTitles),
    // PendingRestart for anything that was pending on the previous check but has since dropped
    // out of the eligible list while a reboot is pending (presumably installed, just waiting on
    // the restart - shown instead of silently vanishing from the table), Pending otherwise.
    private static IReadOnlyList<UpdateStatusEntry> BuildStatusEntries(WindowsUpdateSearchResult searchResult, HashSet<string> previousTitles, Logger logger)
    {
        var failedTitles = WindowsUpdateSearcher.GetRecentlyFailedTitles(logger);
        var currentTitles = new HashSet<string>(searchResult.Titles, StringComparer.OrdinalIgnoreCase);
        var entries = new List<UpdateStatusEntry>();

        foreach (var title in searchResult.Titles)
        {
            var state = failedTitles.Contains(title) ? UpdateState.Failed : UpdateState.Pending;
            entries.Add(new UpdateStatusEntry(title, state));
        }

        if (WindowsRebootStatus.IsRebootRequired())
        {
            foreach (var title in previousTitles)
            {
                if (!currentTitles.Contains(title))
                {
                    entries.Add(new UpdateStatusEntry(title, UpdateState.PendingRestart));
                }
            }
        }

        return entries;
    }
}
