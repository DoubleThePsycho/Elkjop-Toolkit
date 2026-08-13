using System.Diagnostics;

namespace Setup_Agent.Services;

internal interface IScheduledTaskService
{
    /// <summary>Removes the scheduled task that launches Setup Agent - called once there are no updates left to install.</summary>
    void DeleteSelf(Logger logger);
}

internal sealed class RealScheduledTaskService : IScheduledTaskService
{
    public void DeleteSelf(Logger logger)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/delete /tn \"{AppInfo.ScheduledTaskName}\" /f",
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            process.WaitForExit();
            logger.Info($"Deleted scheduled task '{AppInfo.ScheduledTaskName}' (exit code {process.ExitCode}). Setup Agent will not run again.");
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to delete scheduled task '{AppInfo.ScheduledTaskName}': {ex.Message}");
        }
    }
}

internal sealed class DryRunScheduledTaskService : IScheduledTaskService
{
    public void DeleteSelf(Logger logger)
    {
        logger.Info($"[DRY RUN] Would delete scheduled task '{AppInfo.ScheduledTaskName}'.");
    }
}
