using System.Diagnostics;
using Toolkit.Shared;

namespace Support_Tool.Services;

// Tier-3 (ordinary close) teardown only - MainWindow.OnClosing gates this so it's never called
// while a WUA operation is active or while session.ResumeRequired is true (tiers 1/2). Never
// calls RuntimeStaging.DeleteRuntimeNow(): Support Tool is running from inside the very
// directory that would need deleting and cannot synchronously remove its own containing folder
// (that primitive is for an external launcher acting on a Runtime it's confirmed nothing owns).
// Instead this spawns a short detached cmd.exe that waits for this process to release its file
// handle on its own exe, then removes the whole Runtime directory as one unit - no separate
// helper executable.
public sealed class RuntimeCleanupService
{
    private readonly Logger _logger;

    public RuntimeCleanupService(Logger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns <see langword="true"/> only if the resume task is confirmed absent AND the
    /// detached cleanup process was successfully spawned - MainWindow.OnClosing must cancel the
    /// close on <see langword="false"/> rather than letting Support Tool exit while
    /// "Elkjop Toolkit Resume" might still be registered pointing at a Runtime that's about to be
    /// deleted (or wasn't deleted at all, if the spawn itself failed).
    /// </summary>
    public bool TeardownForOrdinaryClose()
    {
        if (!RuntimeStaging.DeleteResumeTaskIfPresent())
        {
            _logger.Error("Resume task could not be confirmed absent during ordinary-close teardown - aborting teardown so Runtime/session remain intact for recovery.");
            return false;
        }

        // session.json is deliberately NOT deleted synchronously here - it's removed together
        // with the rest of Runtime by the detached rmdir below, once this process actually
        // exits. Deleting it up front would break the "return false means Runtime/session are
        // left intact" guarantee if the cleanup-process spawn below then failed.
        return SpawnDetachedRuntimeDelete();
    }

    private bool SpawnDetachedRuntimeDelete()
    {
        var runtimeFolder = RuntimeStaging.RuntimeFolder;

        try
        {
            // Not wrapped in an extra pair of outer quotes for cmd's own /c parsing - cmd.exe's
            // quote-stripping rules for a fully-quoted /c argument are notoriously inconsistent
            // (see RuntimeStaging.RunSchtasks' own manually-built Arguments strings for the same
            // convention already used elsewhere in this codebase). Only the path argument itself
            // is quoted, matching normal cmd.exe usage.
            var arguments = $"/c timeout /t 2 & rmdir /s /q \"{runtimeFolder}\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            _logger.Info($"Spawned detached cleanup to remove '{runtimeFolder}' once this process exits.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"Failed to spawn detached Runtime cleanup for '{runtimeFolder}': {ex.Message}");
            return false;
        }
    }
}
