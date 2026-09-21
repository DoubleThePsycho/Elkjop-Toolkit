using System.Windows;
using Support_Tool.ViewModels;
using Support_Tool.Views;
using Toolkit.Shared;

namespace Support_Tool.Services;

// Bridges WindowsUpdatePageViewModel's per-run outcome (Phase 2A: detect-only) to the
// session/scheduled-task/reboot lifecycle required by Phase 2B - persisting SessionState,
// creating/verifying the Elkjop Toolkit Resume task, and calling IRebootService, gated by the
// Setup/Service task-lifecycle divergence in the approved plan: task presence tracks the whole
// mandatory Setup Windows Update stage, while for Service it tracks ResumeRequired exactly.
//
// Constructed once in App.xaml.cs and wired to WindowsUpdatePageViewModel via the onOutcome
// delegate - one instance regardless of workflow, since a single process's
// SessionState.Workflow never changes during its lifetime; this branches on it at outcome time
// rather than needing two separate coordinator types.
internal sealed class WindowsUpdateLifecycleCoordinator
{
    private readonly SessionService _sessionService;
    private readonly IRebootService _rebootService;
    private readonly Logger _logger;
    private readonly SharedViewState _viewState;

    public WindowsUpdateLifecycleCoordinator(SessionService sessionService, IRebootService rebootService, Logger logger, SharedViewState viewState)
    {
        _sessionService = sessionService;
        _rebootService = rebootService;
        _logger = logger;
        _viewState = viewState;
    }

    // Set once in App.xaml.cs, right after windowsUpdateVm is constructed - avoids a
    // construction-order cycle (the ViewModel's own constructor needs this coordinator's
    // HandleOutcomeAsync/HandleRebootNowRequestedAsync delegates to already exist, so the
    // coordinator can't take the ViewModel as a constructor parameter). Used only to reflect the
    // reboot-deferred state back onto the shared ViewModel (IsRebootDeferred) so the Windows
    // Update page and the cross-page footer can show it - this coordinator otherwise has no
    // reason to read from or drive the ViewModel directly.
    public WindowsUpdatePageViewModel? Page { get; set; }

    // Raised once the coordinator has fully handled a terminal outcome (including confirming the
    // resume task is actually gone). Not raised for RebootRequired (the process is about to
    // restart), for StillPending (Windows Update remains actionable for retry - no
    // technician-override policy is invented here), or for a terminal outcome whose task-deletion
    // could not be confirmed (stays on the page so the technician can retry rather than silently
    // navigating past unconfirmed cleanup).
    //
    // SetupAdvancedPastWindowsUpdate has no subscribers as of the "stay on the Windows Update
    // page when fully up to date" change - App.xaml.cs used to wire it straight to
    // NavigateInstallCommand, auto-navigating a Setup session off this page the moment
    // FullyUpToDate persisted. Session state (Stage=Install/ResumeRequired=false/task deleted)
    // still advances exactly as before; only the automatic navigation was removed, so the
    // technician can review the "You are up-to-date." result and move to Install manually via the
    // always-visible sidebar item. Left declared (rather than deleted) in case a future need to
    // react to genuine terminal completion without polling State reappears.
    public event Action? SetupAdvancedPastWindowsUpdate;
    public event Action? ServiceReturnedToNormal;

    public async Task HandleOutcomeAsync(WindowsUpdateOutcome outcome)
    {
        var session = _sessionService.Current;

        switch (outcome)
        {
            case WindowsUpdateOutcome.RebootRequired:
            {
                // Covers the whole persist -> create/verify task -> reboot transaction. No WUA
                // COM call is in flight here (IsWuaOperationActive is already false by the time
                // this runs), but closing mid-transaction could still strand a half-applied
                // transition - e.g. a reboot with an unverified task - so this is a distinct flag
                // from IsWuaOperationActive, not a broadening of it.
                using var _ = _viewState.EnterLifecycleTransition();
                await HandleRebootRequiredAsync(session);
                break;
            }

            case WindowsUpdateOutcome.FullyUpToDate:
            {
                using var _ = _viewState.EnterLifecycleTransition();
                await HandleTerminalCompletionAsync(session);
                break;
            }

            case WindowsUpdateOutcome.StillPending:
                // Setup remains incomplete; Service may retry - neither advances Stage nor
                // touches ResumeRequired/the task, so no transition lease is needed.
                _logger.Info("Windows Update did not reach a terminal state this run; session state is unchanged for retry.");
                break;
        }
    }

    private async Task HandleRebootRequiredAsync(SessionState session)
    {
        if (!await PersistAndVerifyResumeStateAsync(session, "prepare session state for a Windows-Update-required reboot"))
        {
            return;
        }

        _logger.Info("Session persisted and resume task verified.");

        // Session/task persistence is already safe and idempotent at this point regardless - the
        // check specifically gates whether it's safe to actually REBOOT, not whether it was safe
        // to persist/verify (that ordering, persist -> verify task -> only then consider
        // rebooting, is unchanged and must never be reordered around this check). No new WU run,
        // no session reset - Windows Update itself is already done for this pass; only the
        // reboot decision is affected. MainWindow is disabled for the duration of the countdown
        // dialog below (it's shown with Owner = MainWindow), so nothing new can start once the
        // countdown is showing - a single check here, before ever showing it, is sufficient.
        if (_viewState.IsToolkitOperationActive)
        {
            _logger.Info("Another toolkit operation is still active - deferring the reboot without showing a countdown.");
            if (Page is not null)
            {
                Page.IsRebootDeferred = true;
            }

            return;
        }

        _logger.Info("No other toolkit operation is active. Showing the reboot countdown.");

        if (ShowRebootCountdownAndConfirm())
        {
            if (TryRebootNow())
            {
                if (Page is not null)
                {
                    Page.IsRebootDeferred = false;
                }
            }
            else if (Page is not null)
            {
                // Session/task are already persisted and verified above - only launching
                // shutdown.exe itself failed. Leave the technician with the same deferred
                // "Reboot now" affordance a Cancel would have produced, rather than stranding
                // them with no way to retry.
                Page.IsRebootDeferred = true;
            }
        }
        else
        {
            _logger.Info("Technician cancelled the reboot countdown. Windows Update will remain pending until 'Reboot now' is used.");
            if (Page is not null)
            {
                Page.IsRebootDeferred = true;
            }
        }
    }

    // Invoked by WindowsUpdatePageViewModel.RebootNowCommand (only enabled while
    // IsRebootDeferred) - re-runs the exact same persist-then-verify-task sequence
    // HandleRebootRequiredAsync already did (defensively; nothing should have changed since the
    // countdown was cancelled, but this must never reboot on the strength of a stale
    // verification) and then reboots immediately with no second countdown - the technician
    // explicitly asked for this reboot. Never starts a new Windows Update run, never resets the
    // session, never advances Setup's Stage.
    public async Task HandleRebootNowRequestedAsync()
    {
        using var _ = _viewState.EnterLifecycleTransition();

        // Checked before touching anything - if another toolkit operation is already active,
        // refuse outright without re-persisting/re-verifying (nothing needs it; the earlier
        // RebootRequired handling already left session/task in a fully safe, resumable state).
        if (_viewState.IsToolkitOperationActive)
        {
            RefuseRebootNowDueToActiveOperation();
            return;
        }

        var session = _sessionService.Current;

        if (!await PersistAndVerifyResumeStateAsync(session, "re-confirm session state before a technician-requested reboot"))
        {
            return;
        }

        // Re-checked after the persist/verify await above - persisting/verifying is itself
        // idempotent and harmless even if an operation started during it, but an operation could
        // have started in that window (the main window isn't disabled for this path the way it
        // is during the automatic countdown), so this is the actual authoritative gate right
        // before rebooting, not just a UX nicety like RebootNowCommand's CanExecute.
        if (_viewState.IsToolkitOperationActive)
        {
            RefuseRebootNowDueToActiveOperation();
            return;
        }

        _logger.Info("Technician requested an immediate reboot; session persisted and resume task re-verified. Proceeding to reboot.");

        if (TryRebootNow())
        {
            if (Page is not null)
            {
                Page.IsRebootDeferred = false;
            }
        }
        else if (Page is not null)
        {
            // Session/task are already re-persisted and re-verified above - only launching
            // shutdown.exe itself failed. Keep the deferred "Reboot now" action available so the
            // technician can simply retry.
            Page.IsRebootDeferred = true;
        }
    }

    // Keeps the fully-safe resumable state exactly as it already is - Stage/ResumeRequired/task
    // are never touched destructively here, matching the required invariant that Support Tool
    // must never intentionally reboot while one of its own install/uninstall/debloat operations
    // is still active.
    private void RefuseRebootNowDueToActiveOperation()
    {
        _logger.Info("Technician requested an immediate reboot, but another toolkit operation is still active. Refusing to reboot.");
        if (Page is not null)
        {
            Page.IsRebootDeferred = true;
        }

        MessageBox.Show(
            "Another toolkit operation is still running. Wait for it to finish before restarting.",
            "Windows Update reboot deferred",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    // Shared by both reboot paths. Session/task persistence has already succeeded by the time
    // this is called - only the process launch itself can fail here (e.g. shutdown.exe missing
    // or blocked) - so a failure must not crash the WPF Dispatcher or be mistaken for an
    // un-persisted state; ResumeRequired and the verified resume task are left exactly as they
    // are, and the caller is responsible for putting the page back into its deferred
    // ("Reboot now" available) state so the technician can retry.
    private bool TryRebootNow()
    {
        try
        {
            _rebootService.RebootNow(_logger);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to launch the reboot: {ex.Message}");
            ShowRebootLaunchFailed();
            return false;
        }
    }

    // Shared by both the automatic (post-outcome) and technician-requested ("Reboot now") reboot
    // paths - persists Stage=WindowsUpdate/ResumeRequired=true, then requires the resume task to
    // be positively confirmed present, in that order. Never reboots on the caller's behalf; the
    // caller decides what happens next (show the countdown, or reboot immediately).
    private async Task<bool> PersistAndVerifyResumeStateAsync(SessionState session, string persistFailureContext)
    {
        if (!await TryMutateAsync(s =>
            {
                s.Stage = ToolkitStage.WindowsUpdate;
                s.ResumeRequired = true;
            }, persistFailureContext))
        {
            ShowRebootBlocked("Could not save session state before rebooting.");
            return false;
        }

        var description = session.Workflow == ToolkitWorkflow.Setup
            ? "Elkjop Toolkit - resumes the mandatory Setup Windows Update stage after a reboot."
            : "Elkjop Toolkit - resumes a Service Windows Update reboot.";

        // Setup's task should already exist (Account Setup registers it at OOBE staging). This
        // call defensively ensures/renews it regardless, via the same force-overwrite either
        // workflow uses to actually create it the first time.
        if (!RuntimeStaging.CreateOrRenewResumeTask(description))
        {
            _logger.Error("Could not verify the Elkjop Toolkit Resume task before rebooting.");
            ShowRebootBlocked("Could not verify the Windows Update resume task before rebooting.");
            return false;
        }

        return true;
    }

    // Blocking (ShowDialog), but safe here - see RebootCountdownWindow's own remarks on why this
    // isn't the sync-over-async shape that caused the App.xaml.cs startup deadlock. Only a
    // genuinely expired countdown (DialogResult exactly true) counts as confirmation; Cancel, the
    // titlebar close button, or an unhandled close (DialogResult null) are all treated as "do not
    // reboot" - never assume consent from an ambiguous result.
    private static bool ShowRebootCountdownAndConfirm()
    {
        var owner = Application.Current?.MainWindow as Support_Tool.MainWindow;

        // Phase 5: dim the main window for the whole time this modal is open, restored in a
        // finally so it clears on every exit path - countdown expiry, Cancel, titlebar close, or
        // an exception ShowDialog() re-throws back through its own nested dispatcher frame.
        owner?.SetModalDimmed(true);
        try
        {
            var dialog = new RebootCountdownWindow
            {
                Owner = owner,
            };

            return dialog.ShowDialog() == true;
        }
        finally
        {
            owner?.SetModalDimmed(false);
        }
    }

    private async Task HandleTerminalCompletionAsync(SessionState session)
    {
        var isSetup = session.Workflow == ToolkitWorkflow.Setup;

        // Captured before the terminal mutation below, so a failed task deletion can roll the
        // persisted Stage/ResumeRequired back to exactly what they were - not just "false"/
        // "WindowsUpdate" by assumption, in case a future outcome ever reaches this method from a
        // state other than the current WindowsUpdate-stage/false-ResumeRequired shape.
        var previousStage = session.Stage;
        var previousResumeRequired = session.ResumeRequired;

        if (!await TryMutateAsync(s =>
            {
                s.ResumeRequired = false;
                if (s.Workflow == ToolkitWorkflow.Setup)
                {
                    s.Stage = ToolkitStage.Install;
                }
            }, "record Windows Update terminal completion"))
        {
            // Deliberately does not attempt task deletion or navigation if persistence failed -
            // an inconsistent on-disk state (task gone, ResumeRequired left true) would be worse
            // than simply retrying the next time this outcome is reached.
            return;
        }

        if (RuntimeStaging.DeleteResumeTaskIfPresent())
        {
            if (isSetup)
            {
                SetupAdvancedPastWindowsUpdate?.Invoke();
            }
            else
            {
                ServiceReturnedToNormal?.Invoke();
            }

            return;
        }

        _logger.Error("Could not confirm the Elkjop Toolkit Resume task was removed after Windows Update terminal completion. Rolling back the persisted terminal state so a relaunch doesn't skip Windows Update while the task may still exist.");

        if (!await TryMutateAsync(s =>
            {
                s.Stage = previousStage;
                s.ResumeRequired = previousResumeRequired;
            }, "roll back terminal Windows Update state after an unconfirmed resume-task deletion"))
        {
            // The persisted session now says "terminal" (Stage advanced / ResumeRequired false)
            // while the resume task may still be registered, and rolling that back also failed -
            // Tier 2's plain ResumeRequired check can no longer be trusted to keep the app from
            // exiting into an inconsistent state. Block closing outright until someone
            // investigates rather than guessing at further automatic recovery.
            _logger.Error("Rollback of terminal Windows Update state failed - session state may now disagree with the actual scheduled-task state. Blocking further closes until this is investigated.");
            _viewState.IsLifecycleRecoveryBlocked = true;
            ShowLifecycleRecoveryBlocked();
            return;
        }

        ShowTaskCleanupFailed();
        // Remain on the Windows Update page - do not navigate onward while a resume task might
        // still be registered. Session state has been rolled back to its pre-terminal values, so
        // a relaunch (crash/logoff) correctly returns to Windows Update instead of skipping ahead
        // to Install/Service with a stale task still present. The technician can retry (re-running
        // this outcome re-attempts deletion) or remove the task manually; no automatic
        // destructive fallback is invented here.
    }

    private async Task<bool> TryMutateAsync(Action<SessionState> mutate, string what)
    {
        try
        {
            await _sessionService.MutateAsync(mutate);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to {what}: {ex.Message}");
            return false;
        }
    }

    private static void ShowRebootLaunchFailed() =>
        MessageBox.Show(
            "Session state and the Windows Update resume task are already confirmed, but the machine could not be rebooted.\n\nThe machine has NOT been rebooted - please retry using 'Reboot now', or reboot manually.",
            "Windows Update reboot failed",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    private static void ShowRebootBlocked(string reason) =>
        MessageBox.Show(
            $"{reason}\n\nThe machine has NOT been rebooted - Windows Update will remain pending. Please retry, or investigate manually.",
            "Windows Update reboot blocked",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    private static void ShowTaskCleanupFailed() =>
        MessageBox.Show(
            "Windows Update finished, but the Elkjop Toolkit Resume task could not be confirmed removed.\n\nPlease retry, or remove it manually (Task Scheduler) before continuing.",
            "Windows Update cleanup incomplete",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    private static void ShowLifecycleRecoveryBlocked() =>
        MessageBox.Show(
            "Elkjop Toolkit hit an unrecoverable error finishing a Windows Update transition and cannot safely close.\n\nPlease investigate manually - check the 'Elkjop Toolkit Resume' scheduled task and Runtime\\session.json - before restarting this tool.",
            "Elkjop Toolkit - manual recovery required",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
}
