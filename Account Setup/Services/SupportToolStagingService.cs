using System.Text;
using Toolkit.Shared;

namespace Account_Setup.Services;

// Thrown by StageRuntime on any failure - Program.cs's Main catches this type specifically,
// separately from its generic Exception handler: it shows the failure visibly and leaves Windows
// in OOBE for manual recovery, but deliberately does NOT run AccountBypassFallbackService (which
// can itself set BypassNRO and restart Windows) and never calls Sysprep. That legacy fallback
// remains appropriate for the existing Unattend/Sysprep-path failure handling, but offering it
// here would contradict Phase 4B's whole point - a failed final Runtime stage must leave the
// machine in OOBE, not continue toward a reboot into a machine with no valid post-OOBE tool.
// Never caught/swallowed inside this service itself.
internal sealed class RuntimeStagingException : Exception
{
    public RuntimeStagingException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

// Phase 4B: stages the FINAL, required Support Tool Runtime that the live post-OOBE path depends
// on entirely - SupportTool.exe, the exact validated canonical config snapshot, the temporary
// Elkjop Toolkit Resume task XML, and the Unattend answer file itself all now live under
// Runtime\, with session.json written last as the "this Runtime is complete" marker. Replaces
// Phase 4A's StageShadowRuntime, which was deliberately non-fatal - this is not.
internal static class SupportToolStagingService
{
    // Public entry point - a thin defensive wrapper around StageRuntimeCore ensuring EVERY
    // failure this method can produce emerges as a RuntimeStagingException, never an unrelated
    // exception type. This matters specifically because Program.cs's caller distinguishes
    // RuntimeStagingException (final Runtime staging failed - never Sysprep, never the legacy
    // account-bypass fallback) from every other exception (existing Unattend/Sysprep-path
    // failure handling, which still may offer that fallback) purely by catch-block type - an
    // unexpected exception slipping through unwrapped (e.g. from RuntimeSessionGuard's own Mutex
    // construction) would otherwise be misclassified into the wrong bucket.
    public static void StageRuntime(string architecture, string username, string unattendXmlContent, LoadedToolkitConfig config, Logger logger)
    {
        try
        {
            StageRuntimeCore(architecture, username, unattendXmlContent, config, logger);
        }
        catch (RuntimeStagingException)
        {
            // Already the right type - rethrow as-is rather than double-wrapping it.
            throw;
        }
        catch (Exception ex)
        {
            throw new RuntimeStagingException($"Unexpected failure while staging the final Support Tool Runtime: {ex.Message}", ex);
        }
    }

    private static void StageRuntimeCore(string architecture, string username, string unattendXmlContent, LoadedToolkitConfig config, Logger logger)
    {
        var ridSuffix = architecture switch
        {
            "amd64" => "x64",
            "arm64" => "arm64",
            "x86" => "x86",
            _ => throw new RuntimeStagingException($"Unsupported or unknown architecture: '{architecture}'."),
        };

        // Setup Agent's sibling-folder assumption, unchanged - Support Tool's own published
        // folder sits alongside it.
        var toolkitFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        var sourceSupportToolPath = Path.Combine(toolkitFolder, "Support Tool", $"Support Tool-{ridSuffix}.exe");

        // Validated before acquiring the Runtime lease at all - no point contending for the
        // mutex, let alone tearing down an existing Runtime, if staging couldn't proceed anyway.
        if (!File.Exists(sourceSupportToolPath))
        {
            throw new RuntimeStagingException($"Could not find the Support Tool executable at '{sourceSupportToolPath}'.");
        }

        // Held across classification, any stale cleanup, and the entire staging transaction -
        // never released early. Same shared contract Service Launcher's own critical section
        // uses.
        using var lease = RuntimeSessionGuard.TryAcquire(ToolkitWorkflow.Setup);

        if (lease is null)
        {
            throw new RuntimeStagingException("Another Elkjop Toolkit session or staging operation is already active on this machine.");
        }

        switch (lease.State)
        {
            case RuntimeSessionState.WorkflowConflict:
                throw new RuntimeStagingException(
                    "A valid Service workflow already owns the Runtime directory on this machine. Resolve it " +
                    "(finish or close the existing Service session) before running Account Setup again.");

            case RuntimeSessionState.ResumeExisting:
                throw new RuntimeStagingException(
                    "An existing resumable Setup session was found in the Runtime directory - this machine appears " +
                    "to already be mid-way through a Setup Windows Update cycle. This needs manual investigation " +
                    "rather than starting a new OOBE/Sysprep pass over it.");

            case RuntimeSessionState.Stale:
                StageFreshRuntime(sourceSupportToolPath, username, unattendXmlContent, config, logger);
                return;

            default:
                throw new RuntimeStagingException($"Unrecognized Runtime session state: '{lease.State}'.");
        }
    }

    // Only reached when the Runtime is genuinely Stale - the one case where staging may clean and
    // replace it.
    private static void StageFreshRuntime(string sourceSupportToolPath, string username, string unattendXmlContent, LoadedToolkitConfig config, Logger logger)
    {
        // Records which USB volume this came from (by serial number, not drive letter) so a
        // later Settings save in Support Tool can find it again after a reboot. This is part of
        // the required fresh-stage input validation, same as sourceSupportToolPath's own
        // existence check before this method is ever reached - it must be confirmed BEFORE any
        // destructive cleanup below, not after. Discovering a volume-identity failure only after
        // DeleteRuntimeNow has already destroyed the previous stale Runtime would leave the
        // machine worse off than before Account Setup ran.
        var sourceInfo = PortableConfigSourceLocator.CreateFromConfigPath(config.SourcePath)
            ?? throw new RuntimeStagingException($"Could not determine the source USB volume for '{config.SourcePath}'.");

        if (!RuntimeStaging.DeleteResumeTaskIfPresent())
        {
            throw new RuntimeStagingException("Could not confirm the previous 'Elkjop Toolkit Resume' scheduled task was removed.");
        }

        if (!RuntimeStaging.DeleteRuntimeNow())
        {
            throw new RuntimeStagingException($"Could not remove the previous Runtime directory at '{RuntimeStaging.RuntimeFolder}'.");
        }

        try
        {
            RuntimeStaging.EnsureRuntimeFolderExists();

            File.Copy(sourceSupportToolPath, RuntimeStaging.SupportToolExePath, overwrite: true);
            logger.Info($"Staged Support Tool: '{RuntimeStaging.SupportToolExePath}'.");

            // The exact validated JSON text becomes the staged snapshot - deliberately never
            // re-read the source file after validation/cleanup (which could observe a changed
            // file): "validated bytes/text == staged snapshot", not a second independent read.
            File.WriteAllText(RuntimeStaging.ConfigPath, config.RawJson);
            logger.Info($"Staged canonical configuration: '{RuntimeStaging.ConfigPath}'.");

            PortableConfigSourceLocator.Save(sourceInfo, RuntimeStaging.SourcePath);
            logger.Info($"Staged portable source identity: '{RuntimeStaging.SourcePath}'.");

            // Toolkit.Shared's shared task-XML contract, NOT the Account Setup-local
            // ScheduledTaskXmlBuilder (that one remains for the rollback-only Setup Agent task
            // only) - fully qualified since both types share the unqualified name
            // "ScheduledTaskXmlBuilder" within this same Account_Setup.Services namespace.
            const string description = "Elkjop Toolkit - resumes the mandatory Setup Windows Update stage after a reboot.";
            var resumeTaskXmlContent = Toolkit.Shared.ScheduledTaskXmlBuilder.Build(username, RuntimeStaging.SupportToolExePath, description);
            File.WriteAllText(RuntimeStaging.ResumeTaskXmlPath, resumeTaskXmlContent, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            logger.Info($"Generated Elkjop Toolkit Resume scheduled task definition: '{RuntimeStaging.ResumeTaskXmlPath}'.");

            File.WriteAllText(RuntimeStaging.UnattendPath, unattendXmlContent, new UTF8Encoding(true));
            logger.Info($"Staged answer file: '{RuntimeStaging.UnattendPath}'.");
        }
        catch (RuntimeStagingException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RuntimeStagingException($"Failed to stage the Support Tool Runtime: {ex.Message}", ex);
        }

        // Written LAST, deliberately - the final "this Runtime is complete and valid" marker,
        // only once every other required asset is already safely in place.
        var session = new SessionState
        {
            Workflow = ToolkitWorkflow.Setup,
            Stage = ToolkitStage.WindowsUpdate,
            ResumeRequired = false,
            FirstDesktopActionsCompleted = false,
        };

        try
        {
            RuntimeStaging.WriteInitialSession(session);
        }
        catch (Exception ex)
        {
            throw new RuntimeStagingException($"Failed to write the initial session state: {ex.Message}", ex);
        }

        logger.Info($"Staged final Support Tool Runtime at '{RuntimeStaging.RuntimeFolder}' (Workflow=Setup, Stage=WindowsUpdate, ResumeRequired=false, FirstDesktopActionsCompleted=false). " +
                    "'Elkjop Toolkit Resume' will be registered and run by FirstLogonCommands at first desktop logon - not created here.");
    }
}
