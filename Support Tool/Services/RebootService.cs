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
        // The "the scheduled task will relaunch..." framing and reboot-persistence orchestration
        // (write session state, ensure the resume task, only then call this) are Phase 2+
        // concerns - this method itself is a direct, unchanged port of Setup Agent's
        // RealRebootService.RebootNow.
        logger.Info("Rebooting to continue Windows Update.");
        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            Arguments = "/r /t 60 /c \"Elkjop Support Tool: restarting to continue Windows Update.\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}

internal sealed class DryRunRebootService : IRebootService
{
    public void RebootNow(Logger logger)
    {
        logger.Info("[DRY RUN] Would run 'shutdown.exe /r /t 60' to reboot and continue Windows Update.");
    }
}
