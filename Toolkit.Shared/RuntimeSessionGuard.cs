using System.Text.Json;
using System.Text.Json.Serialization;

namespace Toolkit.Shared;

public enum RuntimeSessionState
{
    /// <summary>Another Support Tool instance is actively running against this Runtime.</summary>
    Active,

    /// <summary>
    /// A legitimate resumable session (matching workflow, ResumeRequired true) is waiting for
    /// its reboot-relaunch - relaunch the already-staged exe, do not overwrite config/session.
    /// </summary>
    ResumeExisting,

    /// <summary>
    /// A valid session exists for a different workflow than the one being requested. Never
    /// silently converted - refuse and let the technician resolve it.
    /// </summary>
    WorkflowConflict,

    /// <summary>
    /// No live session worth preserving - missing/unparsable session.json, or a session with
    /// ResumeRequired false and no active process. Safe to clean up and stage fresh.
    /// </summary>
    Stale,
}

/// <summary>
/// Guards the single Runtime directory's staging critical section for a launcher (Account
/// Setup's SupportToolStagingService, or Service Launcher). Lives in Toolkit.Shared - so it
/// cannot depend on Support Tool's RuntimeCleanupService or any other Support Tool type; the
/// dependency only ever runs Support Tool -> Toolkit.Shared, never the reverse.
///
/// Deliberately NOT a bare "classify, then act" pair of calls - an earlier version of this type
/// exposed a stateless <c>Classify()</c> that acquired the mutex, inspected session.json, and
/// released the mutex before returning, leaving a TOCTOU window: two launchers racing (e.g. two
/// quick service.exe launches) could both observe Stale and both proceed to clean/stage/launch
/// concurrently, defeating the single-session guarantee entirely. <see cref="TryAcquire"/> instead
/// returns a disposable <see cref="RuntimeSessionLease"/> that keeps the mutex held across the
/// whole critical section - classify, stale cleanup if needed, stage files, Process.Start - and
/// is only released (via Dispose, at the end of a <c>using</c> block) once that's done. Support
/// Tool itself later acquires the identical <see cref="RuntimeStaging.MutexName"/> mutex, held by
/// its main/UI thread for the application's whole lifetime, via
/// <see cref="AcquireForProcessLifetime"/> - the two are the same shared contract, not two
/// independently-invented mutex names.
/// </summary>
public static class RuntimeSessionGuard
{
    // JsonStringEnumConverter added alongside Support Tool's own SessionService.JsonOptions
    // (Phase 2B) - both read/write the exact same session.json and must agree on how
    // Workflow/Stage are encoded. String enums also make a manually-staged test session.json
    // (see dev-scripts/) and a technician-inspected file both human-readable rather than opaque
    // integers.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Acquires the shared runtime mutex and classifies the Runtime directory's state for a
    /// launcher that wants to start <paramref name="requestedWorkflow"/>, in one atomic
    /// operation. Returns <see langword="null"/> if the mutex could not be acquired within
    /// <paramref name="acquireTimeout"/> (default: don't wait at all - a launcher deciding
    /// whether to stage should treat "can't get it immediately" as a genuinely active session,
    /// not wait around for one). On success, the caller owns the returned lease and MUST dispose
    /// it (a <c>using</c> block is enough) only after finishing the whole critical section -
    /// classify -> stale cleanup if <see cref="RuntimeSessionLease.State"/> is
    /// <see cref="RuntimeSessionState.Stale"/> -> stage files/session -> <c>Process.Start</c> the
    /// staged Support Tool. Releasing earlier reopens the exact race this type exists to close.
    ///
    /// Workflow-mismatch takes precedence over "resumable" - a resumable Setup session existing
    /// does not make it OK to silently take it over just because ResumeRequired happens to be
    /// true for a different workflow.
    /// </summary>
    public static RuntimeSessionLease? TryAcquire(ToolkitWorkflow requestedWorkflow, TimeSpan acquireTimeout = default)
    {
        var mutex = new Mutex(initiallyOwned: false, RuntimeStaging.MutexName, out _);
        bool acquired;

        try
        {
            acquired = mutex.WaitOne(acquireTimeout);
        }
        catch (AbandonedMutexException)
        {
            // A prior process died without releasing the mutex - that's not a live session,
            // treat the mutex as having been successfully acquired.
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return null;
        }

        var state = Classify(requestedWorkflow);
        return new RuntimeSessionLease(mutex, state);
    }

    /// <summary>
    /// For Support Tool itself: acquires the same shared runtime mutex and returns a
    /// <see cref="RuntimeProcessLease"/> representing that ownership - this is what makes
    /// <see cref="TryAcquire"/> correctly report <see cref="RuntimeSessionState.Active"/> to any
    /// launcher that runs while Support Tool is up. In practice the acquired mutex is held by
    /// Support Tool's main/UI thread for the application's whole lifetime, released only when
    /// that thread disposes the returned lease as the app shuts down - see
    /// <see cref="RuntimeProcessLease"/>'s remarks for why disposal is thread-affine and must not
    /// happen from anywhere else. Waits briefly by default, rather than treating an in-progress
    /// launcher hand-off as "another instance is running" - the launcher that just staged and
    /// started this exact process may still be inside its own <see cref="RuntimeSessionLease"/>'s
    /// critical section for the first moments after <c>Process.Start</c>, and that expected,
    /// brief overlap must not be mistaken for a genuine second instance. Returns
    /// <see langword="null"/> if the wait times out - a real conflict at that point, not a
    /// hand-off. Not yet called by any shipping code (Support Tool's own startup wiring is Phase
    /// 2) - established here now as the shared primitive.
    /// </summary>
    public static RuntimeProcessLease? AcquireForProcessLifetime(TimeSpan? waitTimeout = null)
    {
        var mutex = new Mutex(initiallyOwned: false, RuntimeStaging.MutexName, out _);
        bool acquired;

        try
        {
            acquired = mutex.WaitOne(waitTimeout ?? TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            acquired = true;
        }

        if (!acquired)
        {
            mutex.Dispose();
            return null;
        }

        return new RuntimeProcessLease(mutex);
    }

    // Assumes the mutex is already held by the caller - never called without it. Only decides;
    // never deletes or copies anything itself, callers act on the returned state using
    // RuntimeStaging's primitives.
    private static RuntimeSessionState Classify(ToolkitWorkflow requestedWorkflow)
    {
        var session = TryReadSession();

        if (session is null)
        {
            return RuntimeSessionState.Stale;
        }

        if (session.Workflow != requestedWorkflow)
        {
            return RuntimeSessionState.WorkflowConflict;
        }

        return session.ResumeRequired ? RuntimeSessionState.ResumeExisting : RuntimeSessionState.Stale;
    }

    private static SessionState? TryReadSession()
    {
        var path = RuntimeStaging.SessionPath;

        if (!File.Exists(path))
        {
            return null;
        }

        SessionState? session;
        try
        {
            var json = File.ReadAllText(path);
            session = JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
        }
        catch
        {
            // Unparsable session state is itself a Stale signal, not a fatal error here - this
            // classifier's whole job is to detect and safely clean up exactly this case.
            return null;
        }

        if (session is null)
        {
            return null;
        }

        // Matches Support Tool's own SessionService.LoadAsync: JsonStringEnumConverter accepts
        // numeric enum values with no range checking, so a well-formed-but-corrupt session.json
        // (e.g. a hand-edited or truncated "Stage": 99) can deserialize successfully into a
        // SessionState whose Workflow/Stage don't match any real case this codebase handles. A
        // session this classifier can't semantically trust is exactly as stale as one it
        // couldn't parse at all - do not let an invalid Stage be classified as ResumeExisting
        // merely because Workflow and ResumeRequired happened to deserialize into usable values.
        if (!Enum.IsDefined(session.Workflow) || !Enum.IsDefined(session.Stage))
        {
            return null;
        }

        return session;
    }
}

/// <summary>
/// Ownership of the shared runtime mutex for the duration of one launcher's staging critical
/// section - see <see cref="RuntimeSessionGuard.TryAcquire"/>. Dispose (directly or via
/// <c>using</c>) only after the whole critical section (stale cleanup if needed, staging,
/// <c>Process.Start</c>) has finished.
/// </summary>
public sealed class RuntimeSessionLease : IDisposable
{
    private readonly Mutex _mutex;
    private bool _released;

    internal RuntimeSessionLease(Mutex mutex, RuntimeSessionState state)
    {
        _mutex = mutex;
        State = state;
    }

    public RuntimeSessionState State { get; }

    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}

/// <summary>
/// Ownership of the shared runtime mutex for Support Tool's own process lifetime - see
/// <see cref="RuntimeSessionGuard.AcquireForProcessLifetime"/>. A <see cref="Mutex"/> requires
/// <see cref="Mutex.ReleaseMutex"/> to be called from the exact thread that acquired it (a hard
/// Win32/.NET requirement, not just convention) - so this lease is thread-affine and MUST be
/// disposed on the same thread <see cref="RuntimeSessionGuard.AcquireForProcessLifetime"/> was
/// called from, never from a finalizer, a background/worker thread, or any thread other than the
/// one that acquired it. In practice: acquired once on Support Tool's main/UI thread at startup,
/// disposed by that same thread as the application shuts down. Returning the raw
/// <see cref="Mutex"/> instead of this type would make it too easy for future code to call only
/// <c>Dispose()</c> (skipping <c>ReleaseMutex()</c>) or to dispose it from the wrong thread -
/// this type makes the correct two-step, same-thread release the only thing <c>Dispose()</c> can
/// do.
/// </summary>
public sealed class RuntimeProcessLease : IDisposable
{
    private readonly Mutex _mutex;
    private bool _released;

    internal RuntimeProcessLease(Mutex mutex)
    {
        _mutex = mutex;
    }

    /// <summary>
    /// Releases and disposes the underlying mutex. Must be called from the same thread that
    /// acquired it (see class remarks) - typically once, as Support Tool's main/UI thread shuts
    /// down.
    /// </summary>
    public void Dispose()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
