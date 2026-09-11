using System.ComponentModel;
using System.Windows;
using Support_Tool.ViewModels;
using Toolkit.Shared;

namespace Support_Tool;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // Final three-tier close behavior (Phase 2B).
    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is not MainViewModel vm)
        {
            base.OnClosing(e);
            return;
        }

        // Checked first, unconditionally, ahead of every tier below - set only when a terminal
        // Windows Update completion's resume-task deletion AND its subsequent rollback both
        // failed. session.json may disagree with the actual scheduled-task state in a way Tier
        // 2's plain ResumeRequired check can't detect in that case, so this must never be
        // bypassed by any other tier's logic.
        if (vm.SharedState.IsLifecycleRecoveryBlocked)
        {
            e.Cancel = true;
            MessageBox.Show(
                "Elkjop Toolkit hit an unrecoverable error finishing a Windows Update transition and cannot safely close.\n\nPlease investigate manually - check the 'Elkjop Toolkit Resume' scheduled task and Runtime\\session.json - before restarting this tool.",
                "Elkjop Toolkit - manual recovery required",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        // Tier 1 - an active WUA COM operation blocks the close outright; unchanged from Phase
        // 2A. That would bypass WindowsUpdateExecutor's own RequestAbort/End*/CleanUp lifecycle
        // entirely, so no teardown of any kind runs and no new cancellation subsystem is added.
        if (vm.SharedState.IsWuaOperationActive)
        {
            e.Cancel = true;
            MessageBox.Show(
                "Windows Update is currently running. Please wait until the current operation finishes before closing.",
                "Windows Update in progress",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // A distinct guard from Tier 1 above - IsWuaOperationActive is already false here, but
        // WindowsUpdateLifecycleCoordinator's own async session/task/reboot transaction
        // (persist -> create/verify task -> reboot, or persist -> delete task -> navigate) may
        // still be mid-flight. Closing here could strand a half-applied transition, so this is
        // checked independently rather than folded into IsWuaOperationActive.
        if (vm.SharedState.IsLifecycleTransitionActive)
        {
            e.Cancel = true;
            MessageBox.Show(
                "Windows Update is finishing a background step. Please wait a moment and try again.",
                "Windows Update in progress",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        // Tier 2 - a reboot-resumable session survives the close untouched: Runtime, session.json,
        // and the resume task are all left exactly as they are so the scheduled task can resume
        // things after the reboot. Defensive addition: ResumeRequired=true is only trustworthy as
        // "safe to close" if the resume task is actually confirmed present - if the coordinator's
        // own create/verify call failed (see WindowsUpdateLifecycleCoordinator.HandleRebootRequiredAsync),
        // ResumeRequired could already be true on disk with no task to actually resume anything.
        if (vm.SessionService.Current.ResumeRequired)
        {
            if (!RuntimeStaging.IsResumeTaskConfirmedPresent())
            {
                e.Cancel = true;
                MessageBox.Show(
                    "Windows Update needs a reboot to continue, but the 'Elkjop Toolkit Resume' task could not be confirmed present. Closing now could leave this machine with no automatic way to continue after a restart.\n\nPlease retry, or investigate the scheduled task manually.",
                    "Resume task not confirmed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            base.OnClosing(e);
            return;
        }

        // Tier 3 - ordinary close: tear everything down. Cancel the close instead of exiting if
        // teardown couldn't be confirmed - better to leave the technician with a still-running,
        // recoverable Support Tool than to exit while the resume task or Runtime cleanup may not
        // actually have happened.
        if (!vm.RuntimeCleanupService.TeardownForOrdinaryClose())
        {
            e.Cancel = true;
            MessageBox.Show(
                "Elkjop Toolkit could not confirm cleanup finished (the Windows Update resume task, or the cleanup process itself). Runtime state has been left in place - please retry, or investigate manually.",
                "Cleanup failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        base.OnClosing(e);
    }
}
