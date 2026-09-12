using System.Diagnostics;

namespace Support_Tool.Services;

// Public (not internal, unlike Setup Agent's original) - WindowsUpdateLifecycleCoordinator's
// constructor parameter references this type.
public interface IRebootService
{
    void RebootNow(Logger logger);
}

internal sealed class RealRebootService : IRebootService
{
    public void RebootNow(Logger logger)
    {
        // /t 0 - Support Tool's own WindowsUpdatePage/RebootCountdownWindow already ran a
        // technician-visible, cancellable 5-second countdown before this is ever called (see
        // WindowsUpdateLifecycleCoordinator.HandleRebootRequiredAsync/HandleRebootNowRequestedAsync),
        // and session/task persistence is already confirmed by that point - a second, separate
        // OS-level shutdown.exe timer on top of that would just be a redundant, un-cancellable
        // delay from the technician's perspective. /f forces the reboot (preserves the old /t 60
        // build's effectively-forced semantics - a 60-second wait gave apps time to prompt to
        // save work, but nothing here waits for that anymore, so force it instead of risking a
        // blocked shutdown).
        logger.Info("Rebooting to continue Windows Update.");
        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            Arguments = "/r /t 0 /f /c \"Elkjop Support Tool: restarting to continue Windows Update.\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}

internal sealed class DryRunRebootService : IRebootService
{
    public void RebootNow(Logger logger)
    {
        logger.Info("[DRY RUN] Would run 'shutdown.exe /r /t 0 /f' to reboot and continue Windows Update.");
    }
}
