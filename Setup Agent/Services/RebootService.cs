using System.Diagnostics;

namespace Setup_Agent.Services;

internal interface IRebootService
{
    void RebootNow(Logger logger);
}

internal sealed class RealRebootService : IRebootService
{
    public void RebootNow(Logger logger)
    {
        logger.Info("Rebooting to continue Windows Update. The scheduled task will relaunch Setup Agent at next logon.");
        Process.Start(new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            Arguments = "/r /t 60 /c \"Elkjop Setup Agent: restarting to continue Windows Update.\"",
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
