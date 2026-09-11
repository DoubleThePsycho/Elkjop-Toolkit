namespace Support_Tool.Services;

// Public (not internal, unlike the orchestrator class itself) - flows through
// WindowsUpdatePageViewModel's public onOutcome delegate parameter
// (Func<WindowsUpdateOutcome, Task>) into WindowsUpdateLifecycleCoordinator.
public enum WindowsUpdateOutcome
{
    /// <summary>Fresh search found 0 eligible updates and no reboot is pending.</summary>
    FullyUpToDate,

    /// <summary>
    /// WUA (or WindowsRebootStatus) reports a reboot is required to continue. Phase 2A stops
    /// here - detecting and reporting this is this orchestrator's job; deciding whether/when to
    /// actually reboot, persist state, and resume afterward is Phase 2B/3 orchestration layered
    /// on top by the caller.
    /// </summary>
    RebootRequired,

    /// <summary>Hit the overall run deadline with updates still eligible and no reboot pending.</summary>
    StillPending,
}

/// <summary>
/// Re-hosts Setup Agent's Program.cs RunNativeUpdateCycle/StartAvailabilityWatcher/
/// BuildStatusEntries as a standalone, UI-framework-agnostic orchestrator - the engine
/// underneath (WindowsUpdateSearcher, IWindowsUpdateExecutor, PendingUpdatesTable,
/// WindowsRebootStatus) is untouched, ported in Phase 1. This class has no threading opinions of
/// its own beyond the availability watcher's own background thread (unchanged from Setup Agent) -
/// <see cref="Run"/> is a synchronous, blocking call, and the caller is responsible for invoking
/// it from a dedicated STA thread, since the WUA COM objects created inside
/// <see cref="IWindowsUpdateExecutor.DownloadAndInstall"/> and the
/// <see cref="SleepPreventionService.PreventSleep"/>/<see cref="SleepPreventionService.AllowSleep"/>
/// calls made directly inside this method are both thread-affine.
///
/// Phase 2A scope: detects and reports <see cref="WindowsUpdateOutcome.RebootRequired"/> but does
/// NOT reboot, write session state, or create/delete a scheduled task - Setup Agent's own
/// IRebootService/IScheduledTaskService/FinalServiceStage calls are deliberately not part of this
/// re-host at all.
/// </summary>
internal sealed class WindowsUpdateOrchestrator
{
    // Identical values to Setup Agent's Program.cs - preserved, not re-tuned, per the plan's
    // "do not rewrite the proven engine" instruction extending to the orchestration shell's own
    // timing too.
    private static readonly TimeSpan PassDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan AvailabilityWatchInterval = TimeSpan.FromSeconds(45);

#if DEBUG
    // Shortened purely so an interactive DEBUG dry-run pass doesn't sit through the real 45-minute
    // deadline, or a real 5-minute no-progress backoff on a machine that happens to have genuine
    // pending updates (WindowsUpdateSearcher.Search is always real, even in DEBUG - only
    // DownloadAndInstall is swapped for the dry-run executor). RELEASE keeps the exact proven
    // values below, unchanged.
    private static readonly TimeSpan RunDeadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan NoProgressBackoff = TimeSpan.FromSeconds(10);
#else
    private static readonly TimeSpan RunDeadline = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan NoProgressBackoff = TimeSpan.FromMinutes(5);
#endif

    private readonly IWindowsUpdateExecutor _executor;

    public WindowsUpdateOrchestrator(IWindowsUpdateExecutor executor)
    {
        _executor = executor;
    }

    /// <summary>
    /// Runs one full search/download/install cycle, blocking the calling thread until it reaches
    /// a terminal outcome. <paramref name="table"/>'s Replace/MergeNewTitles calls (and therefore
    /// <paramref name="onStatusChange"/>) may be invoked from this thread, the availability
    /// watcher's own background thread, or WUA's own COM callback threads inside the executor -
    /// the caller's <paramref name="table"/>/<paramref name="onStatusChange"/> callbacks must be
    /// safe to receive calls from any of those (see WindowsUpdatePageViewModel, which wraps both
    /// in <c>Progress&lt;T&gt;</c> for exactly this reason).
    /// </summary>
    public WindowsUpdateOutcome Run(
        PendingUpdatesTable table,
        Action<string> onStatusChange,
        Func<bool> getPreventSleepEnabled,
        Logger logger,
        Func<IDisposable> enterWuaScope)
    {
        onStatusChange("Searching for available updates...");
        var searchResult = InWuaScope(enterWuaScope, () => WindowsUpdateSearcher.Search(logger));
        var previousTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        table.Replace(InWuaScope(enterWuaScope, () => BuildStatusEntries(searchResult, previousTitles, logger)));
        previousTitles = new HashSet<string>(searchResult.Titles, StringComparer.OrdinalIgnoreCase);

        if (searchResult.Count > 0)
        {
            var deadline = DateTime.UtcNow.Add(RunDeadline);

            // Runs for the whole pass loop below, on its own thread/WUA session - purely to catch
            // updates that become newly eligible mid-pass, something neither the executor's own
            // inner search nor the pass-boundary search below can see until the pass itself ends,
            // up to PassDuration later.
            var watcherCts = new CancellationTokenSource();
            var watcherThread = StartAvailabilityWatcher(table, watcherCts.Token, logger, enterWuaScope);

            // Owned by this thread for the rest of the method - SetThreadExecutionState is
            // scoped to whichever thread calls it (see SleepPreventionService), so this must
            // only ever run on the same STA thread the caller is running Run() on.
            var sleepPrevented = false;
            SyncSleepPrevention(getPreventSleepEnabled, ref sleepPrevented, logger);

            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    var rebootRequiredBeforePass = InWuaScope(enterWuaScope, () => WindowsRebootStatus.IsRebootRequired());
                    if (rebootRequiredBeforePass)
                    {
                        break;
                    }

                    // Re-applies the checkbox's current value at each pass boundary - can't check
                    // more often than this since this thread is blocked inside
                    // DownloadAndInstall for the rest of the loop body.
                    SyncSleepPrevention(getPreventSleepEnabled, ref sleepPrevented, logger);
                    onStatusChange("Downloading and installing updates...");

                    var passCap = DateTime.UtcNow.Add(PassDuration);
                    var passDeadline = passCap < deadline ? passCap : deadline;
                    var batch = InWuaScope(enterWuaScope, () => _executor.DownloadAndInstall(table.Replace, onStatusChange, logger, passDeadline));

                    if (batch.RebootRequired)
                    {
                        // WUA-specific signal - carried explicitly rather than relying solely on
                        // the WindowsRebootStatus check below to rediscover it, otherwise this
                        // could re-enter another pass, hit the same pre-install block again, and
                        // repeat until the deadline.
                        logger.Info("Executor reports a reboot is required. Stopping this cycle.");
                        break;
                    }

                    if (batch.TimedOut)
                    {
                        // Most commonly just the per-pass cap doing its job, not a sign anything's
                        // actually stuck - fall through to the re-search/refresh below rather than
                        // giving up on the whole run.
                        logger.Info("Update pass reached its time cap - re-checking for newly available updates.");
                    }

                    var remaining = InWuaScope(enterWuaScope, () => WindowsUpdateSearcher.Search(logger));
                    table.Replace(InWuaScope(enterWuaScope, () => BuildStatusEntries(remaining, previousTitles, logger)));
                    previousTitles = new HashSet<string>(remaining.Titles, StringComparer.OrdinalIgnoreCase);

                    var rebootRequiredAfterPass = InWuaScope(enterWuaScope, () => WindowsRebootStatus.IsRebootRequired());
                    if (remaining.Count == 0 || rebootRequiredAfterPass)
                    {
                        break;
                    }

                    if (!batch.AnyProgressMade)
                    {
                        // Nothing moved forward this pass - avoid spinning the run's time budget
                        // on rapid empty retries. A safe boundary, deliberately NOT held under a
                        // WUA activity lease - no live COM operation is in flight during this
                        // sleep, so the window must remain closable throughout it.
                        Thread.Sleep(NoProgressBackoff);
                    }
                }
            }
            finally
            {
                watcherCts.Cancel();
                watcherThread.Join(TimeSpan.FromSeconds(5));
                watcherCts.Dispose();

                // Released before returning no matter how the loop above ended (reboot detected,
                // timeout, or an exception) - never leave sleep prevented once this thread is
                // done, regardless of the checkbox's final state.
                if (sleepPrevented)
                {
                    SleepPreventionService.AllowSleep(logger);
                }
            }
        }

        if (InWuaScope(enterWuaScope, () => WindowsRebootStatus.IsRebootRequired()))
        {
            onStatusChange("A restart is required to continue Windows Update.");
            logger.Info("Reboot required. Stopping - Phase 2A does not reboot or persist resume state.");
            return WindowsUpdateOutcome.RebootRequired;
        }

        var finalCheck = InWuaScope(enterWuaScope, () => WindowsUpdateSearcher.Search(logger));
        if (finalCheck.Count == 0)
        {
            onStatusChange("No updates remaining.");
            logger.Info("No eligible updates remaining.");
            return WindowsUpdateOutcome.FullyUpToDate;
        }

        onStatusChange("Still waiting on Windows Update.");
        logger.Warn($"Reached the run deadline with {finalCheck.Count} update(s) still pending.");
        return WindowsUpdateOutcome.StillPending;
    }

    // Wraps exactly one real WUA COM call (search, download/install, or a reboot-status/history
    // query) with a SharedViewState activity lease for its duration - acquired immediately before
    // the call and released immediately after, never spanning Thread.Sleep or other non-WUA
    // control flow. Safe to call from any thread (this orchestrator's own STA worker thread, and
    // the availability watcher's independent background thread both use it) since
    // SharedViewState.EnterWuaOperation is Interlocked-based, not Dispatcher-marshaled - so
    // concurrent scopes from both threads correctly keep IsWuaOperationActive true until the last
    // one exits, per the reference-counted design in SharedViewState.
    private static T InWuaScope<T>(Func<IDisposable> enterWuaScope, Func<T> action)
    {
        using var lease = enterWuaScope();
        return action();
    }

    private static void SyncSleepPrevention(Func<bool> getPreventSleepEnabled, ref bool sleepPrevented, Logger logger)
    {
        var desired = getPreventSleepEnabled();
        if (desired == sleepPrevented)
        {
            return;
        }

        if (desired)
        {
            SleepPreventionService.PreventSleep(logger);
        }
        else
        {
            SleepPreventionService.AllowSleep(logger);
        }

        sleepPrevented = desired;
    }

    // Always real, same as WindowsUpdateSearcher.Search() itself - a read-only availability
    // check, no dry-run split needed. Uses its own independent WUA session rather than sharing
    // the executor's typed session that's actively driving Download()/Install().
    //
    // The whole loop body is wrapped in one top-level try/catch: this thread must never surface
    // an unhandled exception, which would crash the entire process even after the real update
    // cycle already finished successfully - including the case where this thread is still
    // mid-Search() (observed taking up to 84s elsewhere in this project) when the pass loop above
    // cancels and disposes watcherCts, and this thread's next WaitHandle check races the dispose.
    private static Thread StartAvailabilityWatcher(PendingUpdatesTable table, CancellationToken token, Logger logger, Func<IDisposable> enterWuaScope)
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (!token.WaitHandle.WaitOne(AvailabilityWatchInterval))
                {
                    var discovered = InWuaScope(enterWuaScope, () => WindowsUpdateSearcher.Search(logger));
                    table.MergeNewTitles(discovered.Titles, logger);
                }
            }
            catch (Exception ex)
            {
                logger.Warn($"Availability watcher stopped: {ex.Message}");
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return thread;
    }

    // Turns a raw search result into what the table actually shows: Failed for anything with a
    // recent failed/aborted history entry, PendingRestart for anything that was pending on the
    // previous check but has since dropped out of the eligible list while a reboot is pending
    // (presumably installed, just waiting on the restart), Pending otherwise.
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
