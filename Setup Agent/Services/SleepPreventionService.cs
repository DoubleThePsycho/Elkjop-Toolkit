using System.Runtime.InteropServices;

namespace Setup_Agent.Services;

// Keeps the system from sleeping while Setup Agent is actively waiting on Windows Update, via
// SetThreadExecutionState rather than changing any persisted power/sleep setting. Deliberately
// not touching the actual power-plan timeout values (e.g. powercfg): that would mean tracking
// an "original value" to restore, and if this process ever got killed unexpectedly (forced
// reboot, crash, task manager) before the restore ran, a customer's machine would be left
// permanently unable to sleep with nothing to reset it. SetThreadExecutionState has no such
// failure mode - it's a request tied to this thread, automatically released the moment the
// thread/process exits for any reason, clean or not.
internal static class SleepPreventionService
{
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
    }

    [DllImport("kernel32.dll")]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    public static void PreventSleep(Logger logger)
    {
        // SystemRequired alone only resets the system idle timer - it does nothing to stop the
        // display from timing out or the machine locking on its own schedule, which reads as
        // "it went to sleep anyway" to anyone watching (confirmed on a real VM: the checkbox was
        // checked and this was already being called, but the screen still went dark/locked).
        // DisplayRequired closes that gap.
        SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired | ExecutionState.DisplayRequired);
        logger.Info("Preventing system and display sleep while waiting on Windows Update.");
    }

    public static void AllowSleep(Logger logger)
    {
        SetThreadExecutionState(ExecutionState.Continuous);
        logger.Info("Released the sleep-prevention request - normal power behavior resumes.");
    }
}
