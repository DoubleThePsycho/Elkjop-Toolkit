using System.Text.Json;
using Toolkit.Shared;
using Toolkit.Shared.Configuration;

namespace Account_Setup.Services;

// Phase 4A: stages a "shadow" Support Tool Runtime alongside the still-live Setup Agent path,
// purely to prove the final Runtime staging logic is correct before Phase 4B's actual cutover
// (switching the post-OOBE scheduled task from Setup Agent to Support Tool). Deliberately
// NON-FATAL to Account Setup's own OOBE flow at every step - see Program.cs's call site, which
// runs this after Setup Agent staging/task-XML generation and continues regardless of outcome.
// This service never touches the Setup Agent task, never creates "Elkjop Toolkit Resume", and
// never launches Support Tool - it only stages files for later inspection/testing.
internal static class SupportToolStagingService
{
    public static void StageShadowRuntime(string architecture, Logger logger)
    {
        try
        {
            StageShadowRuntimeCore(architecture, logger);
        }
        catch (Exception ex)
        {
            // Deliberately swallowed at this outermost boundary - every specific failure below
            // already logs its own reason and returns before reaching here; this only catches
            // something unexpected (e.g. the architecture switch's own exception) that slipped
            // through. Phase 4A shadow staging must never take down Account Setup's live flow.
            logger.Warn($"[Shadow stage] Unexpected failure while staging the shadow Support Tool Runtime: {ex}. The live Setup Agent path is unaffected.");
        }
    }

    private static void StageShadowRuntimeCore(string architecture, Logger logger)
    {
        // Setup Agent lives as a sibling per-architecture publish folder next to this exe's own -
        // same sibling-folder assumption StageSetupAgent already relies on. Support Tool's own
        // published folder sits alongside it.
        var ridSuffix = architecture switch
        {
            "amd64" => "x64",
            "arm64" => "arm64",
            "x86" => "x86",
            _ => throw new InvalidOperationException($"Unsupported or unknown architecture: '{architecture}'."),
        };

        var toolkitFolder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        var sourceSupportToolPath = Path.Combine(toolkitFolder, "Support Tool", $"Support Tool-{ridSuffix}.exe");
        var sourceConfigPath = ToolkitPaths.GetPortableConfigPath();

        // Validate every fresh input BEFORE acquiring the Runtime lease or touching anything -
        // no point contending for the mutex at all if staging couldn't proceed regardless, and
        // this guarantees an existing Runtime is never deleted only to discover afterward that a
        // shadow-stage source file is unavailable.
        if (!File.Exists(sourceSupportToolPath))
        {
            logger.Warn($"[Shadow stage] Support Tool executable not found at '{sourceSupportToolPath}'. Skipping shadow Runtime staging - the live Setup Agent path is unaffected.");
            return;
        }

        if (!File.Exists(sourceConfigPath))
        {
            logger.Warn($"[Shadow stage] Canonical unified config not found at '{sourceConfigPath}'. Skipping shadow Runtime staging - the live Setup Agent path is unaffected.");
            return;
        }

        string configJson;
        try
        {
            configJson = File.ReadAllText(sourceConfigPath);
        }
        catch (Exception ex)
        {
            logger.Warn($"[Shadow stage] Could not read canonical config '{sourceConfigPath}': {ex.Message}. Skipping shadow Runtime staging.");
            return;
        }

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed = JsonSerializer.Deserialize<UnifiedToolkitConfig>(configJson, options);

            if (parsed is null)
            {
                throw new InvalidDataException("Configuration deserialized to nothing.");
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"[Shadow stage] Canonical config '{sourceConfigPath}' is corrupt or invalid: {ex.Message}. Skipping shadow Runtime staging.");
            return;
        }

        // Held across classify -> any stale cleanup -> staging, exactly like Service Launcher's
        // own critical section - never released early.
        using var lease = RuntimeSessionGuard.TryAcquire(ToolkitWorkflow.Setup);

        if (lease is null)
        {
            logger.Warn("[Shadow stage] Another Elkjop Toolkit session or staging operation is already active. Skipping shadow Runtime staging - the live Setup Agent path is unaffected.");
            return;
        }

        switch (lease.State)
        {
            case RuntimeSessionState.WorkflowConflict:
                logger.Warn("[Shadow stage] A valid Service workflow already owns the Runtime directory. Skipping shadow Runtime staging without touching it.");
                return;

            case RuntimeSessionState.ResumeExisting:
                logger.Info("[Shadow stage] An existing resumable Setup Runtime was found and is being preserved as-is - skipping shadow (re-)staging.");
                return;

            case RuntimeSessionState.Stale:
                StageFreshShadowRuntime(sourceSupportToolPath, configJson, logger);
                return;

            default:
                logger.Warn($"[Shadow stage] Unrecognized Runtime session state '{lease.State}'. Skipping shadow Runtime staging.");
                return;
        }
    }

    // Only reached when the Runtime is genuinely Stale - the one case where shadow staging may
    // clean and replace it.
    private static void StageFreshShadowRuntime(string sourceSupportToolPath, string validatedConfigJson, Logger logger)
    {
        if (!RuntimeStaging.DeleteResumeTaskIfPresent())
        {
            logger.Warn("[Shadow stage] Could not confirm the previous 'Elkjop Toolkit Resume' scheduled task was removed. Skipping shadow Runtime staging rather than staging over uncertain state.");
            return;
        }

        if (!RuntimeStaging.DeleteRuntimeNow())
        {
            logger.Warn($"[Shadow stage] Could not remove the previous Runtime directory at '{RuntimeStaging.RuntimeFolder}'. Skipping shadow Runtime staging rather than staging over it.");
            return;
        }

        try
        {
            RuntimeStaging.EnsureRuntimeFolderExists();
            File.Copy(sourceSupportToolPath, RuntimeStaging.SupportToolExePath, overwrite: true);

            // The exact validated JSON text becomes the staged snapshot - deliberately never
            // re-read the source file after validation/cleanup (which could observe a changed
            // file): "validated bytes/text == staged snapshot", not a second independent read.
            File.WriteAllText(RuntimeStaging.ConfigPath, validatedConfigJson);
        }
        catch (Exception ex)
        {
            logger.Warn($"[Shadow stage] Failed to stage Support Tool/configuration into Runtime: {ex.Message}.");
            return;
        }

        // Stage=WindowsUpdate is fine even though nothing launches this shadow session - Support
        // Tool's own startup navigation (if it were ever started against this Runtime) would
        // follow Stage for Setup regardless of ResumeRequired. FirstDesktopActionsCompleted=false
        // matches Setup's real initial session shape exactly.
        var session = new SessionState
        {
            Workflow = ToolkitWorkflow.Setup,
            Stage = ToolkitStage.WindowsUpdate,
            ResumeRequired = false,
            FirstDesktopActionsCompleted = false,
        };

        try
        {
            // Written last, deliberately - it acts as the final "this Runtime is a complete,
            // valid session" marker once every other staged file is already in place.
            RuntimeStaging.WriteInitialSession(session);
        }
        catch (Exception ex)
        {
            logger.Warn($"[Shadow stage] Failed to write the shadow session state: {ex.Message}.");
            return;
        }

        logger.Info($"[Shadow stage] Staged shadow Support Tool Runtime at '{RuntimeStaging.RuntimeFolder}' (Workflow=Setup, Stage=WindowsUpdate) alongside the live Setup Agent path. No 'Elkjop Toolkit Resume' task was created.");
    }
}
