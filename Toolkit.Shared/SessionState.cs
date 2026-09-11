namespace Toolkit.Shared;

public enum ToolkitWorkflow
{
    Setup,
    Service,
}

public enum ToolkitStage
{
    WindowsUpdate,
    Install,
    Debloat,
    Customization,
    Finish,
}

// Runtime\session.json - minimal workflow/resume state, deliberately not a mirror of Windows
// Update's own state. WUA always re-searches fresh after a reboot (Windows itself stays
// authoritative), so nothing about update titles/progress/KB state is persisted here - only
// enough to know which page to return to and whether a reboot is specifically pending right now.
public sealed class SessionState
{
    // Mutable (get/set, not get/init) - the orchestration throughout this design mutates these
    // in place across a session's lifetime (ResumeRequired flips true->false around each reboot,
    // Stage advances through Setup, FirstDesktopActionsCompleted flips false->true once). Owned
    // and persisted by Support Tool's SessionService.
    public required ToolkitWorkflow Workflow { get; set; }
    public required ToolkitStage Stage { get; set; }

    // Scoped to "does WU specifically need a reboot right now" - not the same thing as whether
    // the Elkjop Toolkit Resume task is registered, and not a permanent "this session has
    // resumed at some point" marker. Set true only while a reboot is actually pending; cleared
    // back to false once that specific reboot has been handled. For Setup, the task itself stays
    // registered for the whole mandatory Windows Update stage regardless of this flag (see
    // Toolkit.Shared.ScheduledTaskXmlBuilder's consumers) - it's deliberately a narrower signal
    // than "should this workflow resume", which is why Support Tool's own startup navigation
    // for Setup uses Stage, not this flag, to decide where to land (a Setup session interrupted
    // by a crash rather than a reboot must still return to the right page even though this was
    // never set true for that interruption).
    public bool ResumeRequired { get; set; }

    // Setup-only in practice, despite being a plain bool on a shared type. Not redundant with
    // session.json's mere existence - Account Setup writes session.json before Support Tool's
    // very first launch, so the file already exists by the time Support Tool ever checks it.
    // This is the explicit signal for one-time, first-desktop-only actions (StartMenuDismisser)
    // that exist specifically for a newly-created post-OOBE desktop/account, and must never run
    // for a Service session against an already-existing customer machine. Account Setup's
    // initial session writes this as false; Service Launcher's initial session writes it as
    // true (so the gate is a no-op there even by construction) - callers should still check
    // Workflow == Setup alongside this flag rather than trusting it alone.
    public bool FirstDesktopActionsCompleted { get; set; }

    /// <summary>
    /// A shallow copy - sufficient since every field here is a value type. Used by
    /// Support Tool's SessionService for copy-on-write mutation: a candidate is cloned, mutated,
    /// and only swapped in as the new Current once it's been fully persisted to disk, so a
    /// mutation that fails partway through (serialization, write, or rename) never leaves Current
    /// reflecting values that don't actually match what's on disk.
    /// </summary>
    public SessionState Clone() => new()
    {
        Workflow = Workflow,
        Stage = Stage,
        ResumeRequired = ResumeRequired,
        FirstDesktopActionsCompleted = FirstDesktopActionsCompleted,
    };
}
