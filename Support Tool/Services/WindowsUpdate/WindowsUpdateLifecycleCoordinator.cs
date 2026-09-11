using System.Windows;
using Support_Tool.ViewModels;
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

    // Raised once the coordinator has fully handled a terminal outcome (including confirming the
    // resume task is actually gone) and the caller (App's navigation wiring) should move off the
    // Windows Update page - Setup advances into Install; Service returns to the Service page.
    // Not raised for RebootRequired (the process is about to restart), for StillPending (Windows
    // Update remains actionable for retry - no technician-override policy is invented here), or
    // for a terminal outcome whose task-deletion could not be confirmed (stays on the page so the
    // technician can retry rather than silently navigating past unconfirmed cleanup).
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
        if (!await TryMutateAsync(s =>
            {
                s.Stage = ToolkitStage.WindowsUpdate;
                s.ResumeRequired = true;
            }, "prepare session state for a Windows-Update-required reboot"))
        {
            ShowRebootBlocked("Could not save session state before rebooting.");
            return;
        }

        var description = session.Workflow == ToolkitWorkflow.Setup
            ? "Elkjop Toolkit - resumes the mandatory Setup Windows Update stage after a reboot."
            : "Elkjop Toolkit - resumes a Service Windows Update reboot.";

        // Setup's task should already exist (Account Setup registers it at OOBE staging, once
        // Phase 4 wires that up) - this call defensively ensures/renews it regardless, via the
        // same force-overwrite either workflow uses to actually create it the first time.
        if (!RuntimeStaging.CreateOrRenewResumeTask(description))
        {
            _logger.Error("Could not verify the Elkjop Toolkit Resume task before rebooting.");
            ShowRebootBlocked("Could not verify the Windows Update resume task before rebooting.");
            return;
        }

        _logger.Info("Session persisted and resume task verified. Proceeding to reboot.");
        _rebootService.RebootNow(_logger);
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
