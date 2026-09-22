using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Toolkit.Shared;
using Toolkit.Shared.Configuration;

namespace Elkjop_Toolkit;

// Elkjop Toolkit.exe - Phase 5's unified technician-facing double-click entry point, replacing
// both setup.exe (Setup Launcher) and service.exe (Service Launcher). Deliberately a thin
// dispatcher: it decides which of two things to do, then gets out of the way -
//   - Setup workflow: launch Account Setup-{arch}.exe. No staging happens here at all - Account
//     Setup owns its own OOBE dialog, Unattend build, and SupportToolStagingService.StageRuntime
//     critical section exactly as it always has.
//   - Service workflow: stage (if needed) and launch Support Tool directly - moved verbatim from
//     the old Service Launcher.
// No new "Agent" - every substantial piece of logic already lived in Account Setup or Support
// Tool before this project existed, and still does.
//
// Published as a single self-contained win-x86 binary, same rationale both predecessor
// launchers used: x86 native on x86, under WOW64 on x64, under Windows' own x86 emulation on
// ARM64 - one launcher binary covers every architecture this toolkit targets.
internal static class Program
{
    [STAThread]
    private static int Main()
    {
#if COLOR
        // DEVELOPMENT-ONLY preview entry point (see CLAUDE.md's "Color preview build" section) -
        // unconditionally returns before any of the real dispatcher logic below ever runs, so a
        // Color build can never acquire RuntimeSessionGuard, inspect/modify Runtime, inspect OOBE
        // for workflow selection, stage Support Tool, read/write canonical config, or touch
        // ProgramData toolkit state - regardless of what the rest of this method does.
        return RunColorPreview();
#endif

        var architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
        if (string.IsNullOrEmpty(architecture))
        {
            architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
        }

        architecture = architecture?.ToUpperInvariant();
        if (architecture is not ("AMD64" or "ARM64" or "X86"))
        {
            ShowError($"Unsupported or unrecognized processor architecture: {architecture}");
            return 1;
        }

        // Advisory only (Phase 5 external review - see RuntimeSessionGuard.TryPeekSessionWorkflow's
        // own remarks): chooses which workflow to REQUEST from TryAcquire below. The authoritative
        // decision happens moments later, inside TryAcquire's held mutex, via the exact same
        // classification logic this peek is a mutex-free variant of. A valid existing session's
        // own Workflow always wins over an environment guess here - this is what prevents a
        // normal-looking desktop from ever silently substituting Service for a genuinely valid,
        // in-progress Setup session (Stage is authoritative for Setup regardless of
        // ResumeRequired - see RuntimeSessionState.ResumeExisting's own remarks). Only when no
        // session exists at all does the OOBE check decide which workflow to request fresh.
        var requestedWorkflow = RuntimeSessionGuard.TryPeekSessionWorkflow()
            ?? (OobeGuard.IsOobeInProgress() ? ToolkitWorkflow.Setup : ToolkitWorkflow.Service);

        // Held across the ENTIRE critical section below for the Service-Stale branch (classify,
        // stale cleanup if any, staging, Process.Start) - never released early there. For the
        // Setup-Stale branch, this lease is explicitly released BEFORE Account Setup is launched
        // (see that branch's own remarks) - Account Setup acquires its own TryAcquire(Setup)
        // lease a moment later, once its own OOBE dialog completes, and this dispatcher performs
        // no staging of its own in that branch, so there is nothing left to hold the lease for.
        using var lease = RuntimeSessionGuard.TryAcquire(requestedWorkflow);

        if (lease is null)
        {
            ShowError("Elkjop Toolkit is already running or being staged on this machine. Please close it before starting a new session.");
            return 1;
        }

        switch (lease.State)
        {
            case RuntimeSessionState.Active:
                ShowError("Elkjop Toolkit is already running on this machine. Please close it before starting a new session.");
                return 1;

            case RuntimeSessionState.WorkflowConflict:
                var otherWorkflow = requestedWorkflow == ToolkitWorkflow.Setup ? "Service" : "Setup";
                ShowError($"A valid {otherWorkflow} workflow already owns the Runtime directory on this machine. Finish or close it before starting a new session.");
                return 1;

            // A valid Setup session is preserved regardless of ResumeRequired (see
            // RuntimeSessionState.ResumeExisting's own remarks) - but Account Setup writes that
            // session.json as the LAST step of StageRuntime, before Sysprep /oobe /reboot runs
            // (and --prepare-only stops deliberately right there, still in OOBE). So a valid,
            // worth-preserving Setup session can legitimately exist while Windows is still in
            // OOBE, and launching Runtime\SupportTool.exe directly in that window would run
            // Support Tool over defaultuser0 before the intended first-desktop transition. The
            // session still wins - it is never deleted, reset, or substituted with Service - this
            // check only decides whether it is SAFE TO LAUNCH the preserved session yet. The
            // scheduled task's own first-desktop launch is unaffected: it runs Runtime\
            // SupportTool.exe directly and never passes through this dispatcher.
            case RuntimeSessionState.ResumeExisting when requestedWorkflow == ToolkitWorkflow.Setup && OobeGuard.IsOobeInProgress():
                ShowError("Setup is already prepared and Windows is still in OOBE. Wait for Account Setup/Windows setup to finish and reach the first desktop.");
                return 1;

            case RuntimeSessionState.ResumeExisting:
                return ResumeExistingSession();

            case RuntimeSessionState.Stale when requestedWorkflow == ToolkitWorkflow.Setup:
                // No staging performed here at all - just release and hand off. See this
                // method's own top-level remarks for why the lease must be released before
                // Process.Start, not merely at Main's eventual return.
                lease.Dispose();
                return LaunchAccountSetup(architecture);

            case RuntimeSessionState.Stale:
                return StageFreshServiceSession(architecture);

            default:
                ShowError($"Unrecognized Runtime session state: {lease.State}");
                return 1;
        }
    }

    // A resumable session already exists - matching workflow, and (for Setup specifically) any
    // ResumeRequired value at all, since Stage is authoritative there (see
    // RuntimeSessionState.ResumeExisting's own remarks). Relaunch the already-staged exe exactly
    // as-is. Deliberately does NOT read, validate, or touch the portable source Support Tool exe
    // or config at all: this path exists specifically so an already-staged session can continue
    // even if the portable source has since changed or the USB is gone entirely ("USB
    // independence"). Shared by both workflows - the check and the action are identical either
    // way, since RuntimeStaging's paths are the same fixed locations regardless of workflow.
    private static int ResumeExistingSession()
    {
        if (!File.Exists(RuntimeStaging.SupportToolExePath) ||
            !File.Exists(RuntimeStaging.ConfigPath) ||
            !File.Exists(RuntimeStaging.SessionPath))
        {
            // A valid session claims to exist but its required staged files are missing - this
            // is an inconsistent state that needs a technician's manual recovery. Explicitly NOT
            // auto-deleted, NOT silently re-staged, NOT falling back to the other workflow, and
            // NOT reset - any of those could destroy diagnosable state or silently abandon real
            // progress.
            ShowError(
                "A resumable session exists, but the staged Support Tool/configuration/session files are missing from " +
                $"\"{RuntimeStaging.RuntimeFolder}\". Please investigate manually rather than starting a new session over this state.");
            return 1;
        }

        return LaunchStagedSupportTool();
    }

    // Setup, Stale: no staging performed here - Account Setup owns that critical section itself,
    // once its own OOBE dialog completes. This dispatcher only ever picks the right
    // architecture's exe and starts it, exactly like the old setup.exe did.
    private static int LaunchAccountSetup(string architecture)
    {
        var targetName = architecture switch
        {
            "AMD64" => "Account Setup-x64.exe",
            "ARM64" => "Account Setup-arm64.exe",
            "X86" => "Account Setup-x86.exe",
            _ => null,
        };

        if (targetName is null)
        {
            ShowError($"Unsupported or unrecognized processor architecture: {architecture}");
            return 1;
        }

        // AppContext.BaseDirectory resolves correctly to this exe's own folder even under
        // PublishSingleFile - same assumption every other sibling-folder staging in this
        // solution already relies on.
        var targetPath = Path.Combine(AppContext.BaseDirectory, "Elkjop Toolkit", "Account Setup", targetName);

        if (!File.Exists(targetPath))
        {
            ShowError($"Could not find \"{targetPath}\".");
            return 1;
        }

        // UseShellExecute=false calls CreateProcess directly rather than going through the
        // shell's own elevation check - this process is already elevated (see app.manifest), so
        // Account Setup simply inherits that token instead of triggering its own UAC prompt.
        Process.Start(new ProcessStartInfo
        {
            FileName = targetPath,
            UseShellExecute = false,
        });

        return 0;
    }

    // Service, Stale: no valid resumable session exists - the only path where this dispatcher
    // actually stages fresh files. Moved verbatim from the old Service Launcher/Program.cs.
    private static int StageFreshServiceSession(string architecture)
    {
        var supportToolExeName = architecture switch
        {
            "AMD64" => "Support Tool-x64.exe",
            "ARM64" => "Support Tool-arm64.exe",
            "X86" => "Support Tool-x86.exe",
            _ => null,
        };

        if (supportToolExeName is null)
        {
            ShowError($"Unsupported or unrecognized processor architecture: {architecture}");
            return 1;
        }

        var sourceSupportToolPath = Path.Combine(AppContext.BaseDirectory, "Elkjop Toolkit", "Support Tool", supportToolExeName);

        // The canonical unified portable config - <USB root>\Elkjop Toolkit\config.json.
        var sourceConfigPath = ToolkitPaths.GetPortableConfigPath();

        // Validate every fresh portable input BEFORE touching any existing stale persistence - a
        // dispatcher must never destroy a stale Runtime only to then discover staging can't
        // proceed.
        if (!File.Exists(sourceSupportToolPath))
        {
            ShowError($"Could not find \"{sourceSupportToolPath}\".");
            return 1;
        }

        if (!File.Exists(sourceConfigPath))
        {
            ShowError($"Could not find the portable configuration file at \"{sourceConfigPath}\".");
            return 1;
        }

        string configJson;
        try
        {
            configJson = File.ReadAllText(sourceConfigPath);
        }
        catch (Exception ex)
        {
            ShowError($"Could not read the portable configuration file \"{sourceConfigPath}\": {ex.Message}");
            return 1;
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
            ShowError($"The portable configuration file \"{sourceConfigPath}\" is corrupt or invalid: {ex.Message}");
            return 1;
        }

        // Recorded from sourceConfigPath while it's still known-good and known-connected -
        // identifies the volume by serial number (never the drive letter, which can change after
        // a reboot) so a later Settings save in Support Tool can find this same USB again. This
        // is part of the required fresh-stage INPUT validation, same as the exe/config existence
        // and parse checks above - it must be confirmed BEFORE any destructive cleanup below, not
        // after.
        var sourceInfo = PortableConfigSourceLocator.CreateFromConfigPath(sourceConfigPath);
        if (sourceInfo is null)
        {
            ShowError($"Could not determine the source USB volume for \"{sourceConfigPath}\". Aborting rather than staging a Runtime with no portable source identity.");
            return 1;
        }

        // Only now, with every fresh input (including source volume identity) confirmed good, may
        // destructive cleanup of whatever stale persistence exists begin - each step must be
        // positively confirmed before proceeding to the next.
        if (!RuntimeStaging.DeleteResumeTaskIfPresent())
        {
            ShowError("Could not confirm the previous 'Elkjop Toolkit Resume' scheduled task was removed. Aborting rather than staging over uncertain state.");
            return 1;
        }

        if (!RuntimeStaging.DeleteRuntimeNow())
        {
            ShowError($"Could not remove the previous Runtime directory at \"{RuntimeStaging.RuntimeFolder}\". Aborting rather than staging over it.");
            return 1;
        }

        try
        {
            RuntimeStaging.EnsureRuntimeFolderExists();
            File.Copy(sourceSupportToolPath, RuntimeStaging.SupportToolExePath, overwrite: true);
            File.Copy(sourceConfigPath, RuntimeStaging.ConfigPath, overwrite: true);
            PortableConfigSourceLocator.Save(sourceInfo, RuntimeStaging.SourcePath);
        }
        catch (Exception ex)
        {
            ShowError($"Failed to stage Support Tool/configuration into Runtime: {ex.Message}");
            return 1;
        }

        // Fresh Service session: Stage=WindowsUpdate is fine even though a fresh Service launch
        // opens normal Home/Service, since Support Tool's own startup navigation deliberately
        // ignores Stage for Service unless ResumeRequired=true. No resume task is created here -
        // it's only ever created later, if Support Tool determines a reboot is actually required.
        var session = new SessionState
        {
            Workflow = ToolkitWorkflow.Service,
            Stage = ToolkitStage.WindowsUpdate,
            ResumeRequired = false,
            FirstDesktopActionsCompleted = true,
        };

        try
        {
            // Written last, deliberately - it acts as the final "this Runtime is a complete,
            // valid session" marker once every other staged file is already in place.
            RuntimeStaging.WriteInitialSession(session);
        }
        catch (Exception ex)
        {
            ShowError($"Failed to write the initial session state: {ex.Message}");
            return 1;
        }

        return LaunchStagedSupportTool();
    }

    private static int LaunchStagedSupportTool()
    {
        try
        {
            // UseShellExecute=false calls CreateProcess directly rather than going through the
            // shell's own elevation check - this process is already elevated (see app.manifest),
            // so the child simply inherits that token instead of triggering its own UAC prompt.
            // Support Tool then acquires the same Runtime mutex on its own UI thread via
            // RuntimeSessionGuard.AcquireForProcessLifetime() - not a second/competing mechanism,
            // the same shared contract this dispatcher's own lease is held against.
            Process.Start(new ProcessStartInfo
            {
                FileName = RuntimeStaging.SupportToolExePath,
                WorkingDirectory = RuntimeStaging.RuntimeFolder,
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            // The staged Runtime itself is left exactly as it is - a failed launch here is not a
            // reason to discard potentially useful diagnostic state, and a later manual/relaunch
            // attempt should still find a recoverable (or resumable) Runtime.
            ShowError($"Failed to start Support Tool at \"{RuntimeStaging.SupportToolExePath}\": {ex.Message}");
            return 1;
        }

        return 0;
    }

    private static void ShowError(string message) =>
        MessageBoxW(IntPtr.Zero, message, "Elkjøp Toolkit", MB_ICONERROR);

    private const uint MB_ICONERROR = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

#if COLOR
    // DEVELOPMENT-ONLY preview launcher (see CLAUDE.md's "Color preview build" section). Launches
    // the sibling Color builds of Account Setup and Support Tool (each independently inert in
    // Color mode - see their own Program.cs/App.xaml.cs remarks) and exits - no staging, no
    // architecture selection (a plain `dotnet build -c Color` is not RID-specific/self-contained,
    // so there is exactly one Color build of each project, not per-architecture ones).
    private static int RunColorPreview()
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            ShowError(
                "Color preview: could not locate the repository root (no .slnx file found in any parent " +
                "directory of this exe). This mode only works when run from a normal build output folder " +
                "inside the repository.");
            return 1;
        }

        var accountSetupPath = Path.Combine(repoRoot, "Account Setup", "bin", "Color", "net10.0-windows", "AccountSetup.exe");
        var supportToolPath = Path.Combine(repoRoot, "Support Tool", "bin", "Color", "net10.0-windows", "Support Tool.exe");

        // Never falls back to a Debug or Release binary - a missing Color build is reported
        // visibly so the developer knows exactly which project to build.
        if (!File.Exists(accountSetupPath))
        {
            ShowError(
                $"Color preview: could not find \"{accountSetupPath}\".\n\n" +
                "Build the Account Setup project's Color configuration first:\n" +
                "dotnet build \"Account Setup/Account Setup.csproj\" -c Color");
            return 1;
        }

        if (!File.Exists(supportToolPath))
        {
            ShowError(
                $"Color preview: could not find \"{supportToolPath}\".\n\n" +
                "Build the Support Tool project's Color configuration first:\n" +
                "dotnet build \"Support Tool/Support Tool.csproj\" -c Color");
            return 1;
        }

        Process.Start(new ProcessStartInfo { FileName = accountSetupPath, UseShellExecute = false });
        Process.Start(new ProcessStartInfo { FileName = supportToolPath, UseShellExecute = false });

        return 0;
    }

    // Walks upward from this exe's own folder looking for the repository root, identified by the
    // presence of the solution file - a small, clearly COLOR-only helper. Never used by, and
    // never contaminates, the production path resolution above (AppContext.BaseDirectory-relative
    // sibling-folder lookups for Account Setup/Support Tool's real staged exes).
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (dir.GetFiles("*.slnx").Length > 0)
            {
                return dir.FullName;
            }
        }

        return null;
    }
#endif
}
