using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Support_Tool.ViewModels;
using Toolkit.Shared;

namespace Support_Tool;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += MainWindow_StateChanged;
    }

    // WindowChrome's own maximize behavior can cover the taskbar (a long-standing WPF issue:
    // without this hook, WindowState=Maximized on a WindowStyle=None window sizes itself to the
    // full monitor bounds rather than the monitor's work area). Handling WM_GETMINMAXINFO
    // ourselves and clamping to the correct monitor's work area is the standard, correct fix -
    // simpler workarounds (e.g. padding the window on maximize) don't handle multi-monitor setups
    // correctly. RebootCountdownWindow doesn't need this - it's ResizeMode="NoResize" and never
    // maximizes.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;

        if (msg == WM_GETMINMAXINFO)
        {
            NativeMethods.SetMaxWorkAreaBounds(hwnd, lParam);
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeIcon.Visibility = isMaximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreIcon.Visibility = isMaximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

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

// The standard WM_GETMINMAXINFO fix for a WindowStyle="None" + WindowChrome window's maximize
// behavior otherwise covering the taskbar - clamps the maximized position/size to the correct
// monitor's work area (not just its full bounds), so this is correct on multi-monitor setups too.
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    private const int MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    public static void SetMaxWorkAreaBounds(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor != IntPtr.Zero)
        {
            var monitorInfo = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(monitor, ref monitorInfo))
            {
                var workArea = monitorInfo.rcWork;
                var monitorArea = monitorInfo.rcMonitor;

                mmi.ptMaxPosition.X = Math.Abs(workArea.Left - monitorArea.Left);
                mmi.ptMaxPosition.Y = Math.Abs(workArea.Top - monitorArea.Top);
                mmi.ptMaxSize.X = Math.Abs(workArea.Right - workArea.Left);
                mmi.ptMaxSize.Y = Math.Abs(workArea.Bottom - workArea.Top);
                mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
                mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;
            }
        }

        Marshal.StructureToPtr(mmi, lParam, true);
    }
}
