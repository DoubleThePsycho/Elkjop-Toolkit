using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Toolkit.Shared;
using Toolkit.Shared.Configuration;

namespace Service_Launcher;

// service.exe - the technician-facing double-click entry point for the Service workflow,
// replacing Support Tool.bat (kept as rollback until this is externally reviewed AND manually
// verified - see Support Tool.bat itself). Unlike setup.exe/Account Setup (which only ever picks
// and starts a sibling exe), this launcher owns the Runtime staging critical section itself:
// classify the existing Runtime, resume an already-staged resumable Service session as-is, or
// safely stage a fresh one - see RuntimeSessionGuard's own remarks for why that's a held lease
// across the whole critical section rather than a stateless classify-then-act pair of calls.
//
// Published as a single self-contained win-x86 binary, same rationale as setup.exe: x86 native
// on x86, under WOW64 on x64, under Windows' own x86 emulation on ARM64 - one launcher binary
// covers every architecture this toolkit targets, even though the Support Tool it stages is
// published per-architecture (x64/x86/arm64) like every other real tool in this solution.
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // Mirrors Setup Launcher's own architecture detection exactly: PROCESSOR_ARCHITEW6432 is
        // only set when this process is running under WOW64 (a 32-bit process on a 64-bit OS)
        // and holds the *real* underlying architecture in that case; PROCESSOR_ARCHITECTURE alone
        // would report "x86" for a WOW64 process even on a 64-bit machine, so it's only the
        // fallback for genuinely native processes.
        var architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
        if (string.IsNullOrEmpty(architecture))
        {
            architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
        }

        var supportToolExeName = architecture?.ToUpperInvariant() switch
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

        // AppContext.BaseDirectory resolves correctly to this exe's own folder even under
        // PublishSingleFile - same assumption Setup Launcher/Account Setup's staging already
        // relies on for the identical sibling-folder pattern.
        var sourceSupportToolPath = Path.Combine(AppContext.BaseDirectory, "Elkjop Toolkit", "Support Tool", supportToolExeName);

        // The canonical unified portable config - <USB root>\Elkjop Toolkit\config.json. Only
        // ever read on the Stale (fresh-stage) path below; ResumeExisting deliberately never
        // touches this at all (see RuntimeSessionState.ResumeExisting's own remarks).
        var sourceConfigPath = ToolkitPaths.GetPortableConfigPath();

        // Held across the ENTIRE critical section below - classification, stale cleanup if any,
        // staging, and Process.Start - never released early. Support Tool's own
        // RuntimeSessionGuard.AcquireForProcessLifetime() waits briefly for exactly this
        // launcher-to-Support-Tool handoff, so releasing this lease only happens implicitly when
        // Main returns (the `using` declaration's scope), well after Process.Start.
        using var lease = RuntimeSessionGuard.TryAcquire(ToolkitWorkflow.Service);

        if (lease is null)
        {
            ShowError("Elkjop Toolkit is already running or being staged on this machine. Please close it before starting a new Service session.");
            return 1;
        }

        return lease.State switch
        {
            RuntimeSessionState.Active =>
                ShowErrorAndFail("Elkjop Toolkit is already running on this machine. Please close it before starting a new Service session."),

            RuntimeSessionState.WorkflowConflict =>
                ShowErrorAndFail("Another Elkjop Toolkit workflow is already staged or in progress on this machine (Setup). Finish or close it before starting a Service session."),

            RuntimeSessionState.ResumeExisting => ResumeExistingSession(),

            RuntimeSessionState.Stale => StageFreshSession(sourceSupportToolPath, sourceConfigPath),

            _ => ShowErrorAndFail($"Unrecognized Runtime session state: {lease.State}"),
        };
    }

    // A resumable Service session already exists (ResumeRequired=true) - relaunch the
    // already-staged exe exactly as-is. Deliberately does NOT read, validate, or touch the
    // portable source Support Tool exe or config at all: this path exists specifically so an
    // already-staged resumable session can continue even if the portable source has since
    // changed or the USB is gone entirely (see Phase 3's "USB independence" requirement).
    private static int ResumeExistingSession()
    {
        if (!File.Exists(RuntimeStaging.SupportToolExePath) ||
            !File.Exists(RuntimeStaging.ConfigPath) ||
            !File.Exists(RuntimeStaging.SessionPath))
        {
            return ShowErrorAndFail(
                "A resumable Service session exists, but the staged Support Tool/configuration/session files are missing from " +
                $"\"{RuntimeStaging.RuntimeFolder}\". Please investigate manually rather than starting a new session over this state.");
        }

        return LaunchStagedSupportTool();
    }

    // No valid resumable session exists (missing/corrupt/semantically-invalid session.json, or a
    // valid Service session with ResumeRequired=false) - the only path where this launcher
    // actually stages fresh files.
    private static int StageFreshSession(string sourceSupportToolPath, string sourceConfigPath)
    {
        // Validate every fresh portable input BEFORE touching any existing stale persistence -
        // a launcher must never destroy a stale Runtime only to then discover staging can't
        // proceed.
        if (!File.Exists(sourceSupportToolPath))
        {
            return ShowErrorAndFail($"Could not find \"{sourceSupportToolPath}\".");
        }

        if (!File.Exists(sourceConfigPath))
        {
            return ShowErrorAndFail($"Could not find the portable configuration file at \"{sourceConfigPath}\".");
        }

        string configJson;
        try
        {
            configJson = File.ReadAllText(sourceConfigPath);
        }
        catch (Exception ex)
        {
            return ShowErrorAndFail($"Could not read the portable configuration file \"{sourceConfigPath}\": {ex.Message}");
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
            return ShowErrorAndFail($"The portable configuration file \"{sourceConfigPath}\" is corrupt or invalid: {ex.Message}");
        }

        // Only now, with every fresh input confirmed good, clean up whatever stale persistence
        // exists - each step must be positively confirmed before proceeding to the next.
        if (!RuntimeStaging.DeleteResumeTaskIfPresent())
        {
            return ShowErrorAndFail("Could not confirm the previous 'Elkjop Toolkit Resume' scheduled task was removed. Aborting rather than staging over uncertain state.");
        }

        if (!RuntimeStaging.DeleteRuntimeNow())
        {
            return ShowErrorAndFail($"Could not remove the previous Runtime directory at \"{RuntimeStaging.RuntimeFolder}\". Aborting rather than staging over it.");
        }

        try
        {
            RuntimeStaging.EnsureRuntimeFolderExists();
            File.Copy(sourceSupportToolPath, RuntimeStaging.SupportToolExePath, overwrite: true);
            File.Copy(sourceConfigPath, RuntimeStaging.ConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            return ShowErrorAndFail($"Failed to stage Support Tool/configuration into Runtime: {ex.Message}");
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
            return ShowErrorAndFail($"Failed to write the initial session state: {ex.Message}");
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
            // the same shared contract this launcher's own lease is held against above.
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
            return ShowErrorAndFail($"Failed to start Support Tool at \"{RuntimeStaging.SupportToolExePath}\": {ex.Message}");
        }

        return 0;
    }

    private static int ShowErrorAndFail(string message)
    {
        ShowError(message);
        return 1;
    }

    private static void ShowError(string message) =>
        MessageBoxW(IntPtr.Zero, message, "Elkjøp - Service", MB_ICONERROR);

    private const uint MB_ICONERROR = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
