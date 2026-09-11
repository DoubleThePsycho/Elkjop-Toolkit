using System.Diagnostics;
using System.Text;

namespace Toolkit.Shared;

/// <summary>
/// Shared contract for Support Tool's single always-local staging directory. Replaces
/// <see cref="AccountSetupStaging"/> - Setup Agent's staging concept goes away along with Setup
/// Agent itself, superseded by Support Tool always running from this one Runtime directory
/// regardless of which entry point (setup.exe/Account Setup, or service.exe) staged it.
///
/// Everything lives in one folder: <c>C:\ProgramData\Elkjop\Toolkit\Runtime\</c> - including
/// <see cref="UnattendPath"/>, which is passed directly on Sysprep.exe's own command line (see
/// Account Setup's Program.cs). Sysprep's argument parser does a naive whitespace split with no
/// quote-awareness that breaks on any space (confirmed via a real VM's Panther\setuperr.log) -
/// not even quoting the argument helps, since the parser doesn't respect quotes at all. The
/// nested "Elkjop\Toolkit\Runtime" root has no spaces in any of its segments, so this constraint
/// is satisfied for free - do not rename this folder to anything containing a space.
/// </summary>
public static class RuntimeStaging
{
    public const string ScheduledTaskName = "Elkjop Toolkit Resume";

    // Shared contract, not a magic string private to RuntimeSessionGuard - RuntimeSessionGuard
    // uses it to guard the launcher's staging critical section, and Support Tool itself will
    // later acquire this exact same mutex for its own process lifetime (see
    // RuntimeSessionGuard.AcquireForProcessLifetime). Global, not Local: the resume scheduled
    // task and an interactively-launched instance could in principle run in different sessions -
    // verify this holds on real hardware given this project's own history of session-boundary
    // surprises.
    public const string MutexName = @"Global\ElkjopToolkitRuntime";

    // Pure path calculation only - deliberately does NOT create the directory as a side effect.
    // It used to (a Lazy<string> whose factory called Directory.CreateDirectory), which meant
    // merely reading e.g. SessionPath from a read-only classifier (RuntimeSessionGuard) - or
    // even a no-op DeleteRuntimeNow() on a machine with no Runtime yet - silently created an
    // empty Runtime directory just by being evaluated. Call EnsureRuntimeFolderExists()
    // explicitly, immediately before actually writing staged files.
    public static string RuntimeFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Elkjop", "Toolkit", "Runtime");

    public static string SupportToolExePath => Path.Combine(RuntimeFolder, "SupportTool.exe");
    public static string ConfigPath => Path.Combine(RuntimeFolder, "config.json");
    public static string SessionPath => Path.Combine(RuntimeFolder, "session.json");
    public static string UnattendPath => Path.Combine(RuntimeFolder, "Unattend.xml");

    /// <summary>
    /// Creates the Runtime directory if it doesn't already exist. Callers staging files (Account
    /// Setup's SupportToolStagingService, Service Launcher) must call this explicitly right
    /// before writing - see the remarks on <see cref="RuntimeFolder"/> for why this isn't done
    /// implicitly by the path properties themselves.
    /// </summary>
    public static void EnsureRuntimeFolderExists() => Directory.CreateDirectory(RuntimeFolder);

    /// <summary>
    /// Deletes the <see cref="ScheduledTaskName"/> task if it exists. Safe to call from
    /// anywhere, including Support Tool itself while running from inside
    /// <see cref="SupportToolExePath"/> - deleting a scheduled task never touches the calling
    /// process's own files, unlike <see cref="DeleteRuntimeNow"/>. Genuinely idempotent and
    /// truthful, not just optimistic: returns <see langword="true"/> only if the task is
    /// confirmed absent afterward (whether because it never existed, or because this call
    /// removed it) and <see langword="false"/> if it's still present. Never trusts schtasks.exe's
    /// exit code alone, and never parses its (locale-dependent) text output - existence is
    /// checked via a second schtasks.exe call (<c>/query</c>), whose exit code alone is enough
    /// (0 = exists, non-zero = doesn't).
    /// </summary>
    public static bool DeleteResumeTaskIfPresent(ToolkitLogger? logger = null)
    {
        if (!TryQueryTaskExists(out var existsBefore))
        {
            logger?.Warn($"Could not determine whether scheduled task '{ScheduledTaskName}' exists - not confirmed absent.");
            return false;
        }

        if (!existsBefore)
        {
            logger?.Info($"Scheduled task '{ScheduledTaskName}' already absent.");
            return true;
        }

        try
        {
            var exitCode = RunSchtasks($"/delete /tn \"{ScheduledTaskName}\" /f");

            if (exitCode == 0 && TryQueryTaskExists(out var existsAfter) && !existsAfter)
            {
                logger?.Info($"Deleted scheduled task '{ScheduledTaskName}'.");
                return true;
            }

            logger?.Warn($"Could not confirm scheduled task '{ScheduledTaskName}' was deleted (delete exit code {exitCode}).");
            return false;
        }
        catch (Exception ex)
        {
            logger?.Warn($"Failed to delete scheduled task '{ScheduledTaskName}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Positively confirms the <see cref="ScheduledTaskName"/> task is currently registered -
    /// returns <see langword="true"/> only when the query itself succeeded AND found the task;
    /// any inability to query (not just a confirmed-absent result) returns
    /// <see langword="false"/>. Used where a caller must never proceed on the assumption a task
    /// exists without positive confirmation (e.g. refusing to close while ResumeRequired is true
    /// unless the resume task is actually there).
    /// </summary>
    public static bool IsResumeTaskConfirmedPresent(ToolkitLogger? logger = null)
    {
        if (TryQueryTaskExists(out var exists))
        {
            return exists;
        }

        logger?.Warn($"Could not determine whether scheduled task '{ScheduledTaskName}' exists.");
        return false;
    }

    /// <summary>
    /// Creates (or force-renews, via <c>/f</c>) the <see cref="ScheduledTaskName"/> task,
    /// targeting <see cref="SupportToolExePath"/> and running as the current interactive user,
    /// using the shared <see cref="ScheduledTaskXmlBuilder"/> XML shape. This completes the
    /// contract <see cref="ScheduledTaskXmlBuilder"/> only half-provided (Account Setup's own
    /// still-separate, Setup-Agent-targeting task creation predates this and is unchanged) -
    /// Support Tool is the caller in practice, both to create the task the first time a Service
    /// session needs reboot persistence, and to defensively re-verify/renew it (the same /f
    /// force-overwrite either way) immediately before every reboot regardless of workflow.
    /// Returns <see langword="true"/> only once the task is confirmed present afterward -
    /// callers (the reboot orchestration) must never reboot on the strength of an unconfirmed
    /// task.
    /// </summary>
    public static bool CreateOrRenewResumeTask(string description, ToolkitLogger? logger = null)
    {
        var username = Environment.UserName;
        var xmlContent = ScheduledTaskXmlBuilder.Build(username, SupportToolExePath, description);

        // A transient scratch file, not part of the portable/staged contract - written fresh on
        // every call and always cleaned up afterward, success or failure.
        var tempXmlPath = Path.Combine(RuntimeFolder, "ResumeTask.xml");

        try
        {
            EnsureRuntimeFolderExists();
            File.WriteAllText(tempXmlPath, xmlContent, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

            var exitCode = RunSchtasks($"/create /tn \"{ScheduledTaskName}\" /xml \"{tempXmlPath}\" /f");

            if (exitCode == 0 && TryQueryTaskExists(out var exists) && exists)
            {
                logger?.Info($"Created/renewed scheduled task '{ScheduledTaskName}' targeting '{SupportToolExePath}'.");
                return true;
            }

            logger?.Warn($"Failed to create/renew scheduled task '{ScheduledTaskName}' (exit code {exitCode}, not confirmed present afterward).");
            return false;
        }
        catch (Exception ex)
        {
            logger?.Warn($"Failed to create/renew scheduled task '{ScheduledTaskName}': {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                File.Delete(tempXmlPath);
            }
            catch
            {
                // Best-effort cleanup of a transient scratch file - not worth failing task
                // creation/renewal over.
            }
        }
    }

    /// <summary>
    /// Synchronously deletes the whole Runtime directory as one unit. For external/stale
    /// cleanup ONLY - called by a launcher (Account Setup, Service Launcher) exclusively when
    /// <see cref="RuntimeSessionGuard"/> has classified the existing Runtime as Stale, which
    /// already confirms via the named mutex that no Support Tool process owns files inside it.
    /// A running Support Tool must NEVER call this against its own Runtime - it cannot
    /// synchronously delete the directory containing its own running exe; see
    /// RuntimeCleanupService's detached-cmd.exe teardown instead. Returns <see langword="true"/>
    /// only once the directory is confirmed gone (or never existed) - a launcher must never
    /// proceed to stage fresh files over a Runtime whose required cleanup failed, and this
    /// return value is what lets it detect and stop instead of silently staging over stale
    /// state.
    /// </summary>
    public static bool DeleteRuntimeNow(ToolkitLogger? logger = null)
    {
        try
        {
            if (Directory.Exists(RuntimeFolder))
            {
                Directory.Delete(RuntimeFolder, recursive: true);
                logger?.Info($"Deleted stale Runtime directory '{RuntimeFolder}'.");
            }

            return true;
        }
        catch (Exception ex)
        {
            logger?.Warn($"Failed to delete stale Runtime directory '{RuntimeFolder}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Queries whether <see cref="ScheduledTaskName"/> is registered, distinguishing "the query
    /// ran and told us" from "the query itself could not be run at all" - a caller that only
    /// gets a plain bool back cannot tell those apart, which previously meant a query failure
    /// (e.g. schtasks.exe itself failing to start) was treated the same as a confirmed result.
    /// That was safe for <see cref="DeleteResumeTaskIfPresent"/>'s old "assume still present"
    /// default, but not for task CREATION - a query failure there must never be read as
    /// "confirmed present" (which a plain <see langword="true"/> return would risk being
    /// mistaken for). Exit code 0 = exists, non-zero = genuinely absent (schtasks' own reported
    /// result, trusted as before); the try/catch here only guards against the query not having
    /// run at all.
    /// </summary>
    private static bool TryQueryTaskExists(out bool exists)
    {
        try
        {
            exists = RunSchtasks($"/query /tn \"{ScheduledTaskName}\"") == 0;
            return true;
        }
        catch
        {
            exists = false;
            return false;
        }
    }

    private static int RunSchtasks(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        process!.WaitForExit();
        return process.ExitCode;
    }
}
