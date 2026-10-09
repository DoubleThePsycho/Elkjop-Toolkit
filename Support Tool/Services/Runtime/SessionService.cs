using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Toolkit.Shared;

namespace Support_Tool.Services;

// Thrown when the staged Runtime\session.json is missing or fails to load - deliberately
// distinct from JsonConfigStore's swallow-and-default behavior (see ConfigService's own
// remarks). A missing or corrupt session is never safe to silently paper over with a fresh
// default: doing so could make Support Tool forget it's supposed to be resuming an
// already-scheduled reboot and start a brand new session on top of one that's still mid-flight.
public sealed class SessionLoadException : Exception
{
    public SessionLoadException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

// Owns Runtime\session.json for Support Tool's process lifetime. LoadAsync is called once at
// startup and throws SessionLoadException rather than manufacturing a default. Every mutation
// goes through MutateAsync - the ONLY public write API - which serializes the whole
// mutate-then-persist sequence behind a gate, so two genuinely concurrent callers (the
// first-desktop-actions watch completing at the same moment a Windows Update lifecycle
// transition is persisting) can never race on session.json.tmp or silently clobber each other's
// change. Deliberately does not persist update titles/KB IDs/percentages/individual WUA
// progress - only Workflow/Stage/ResumeRequired/FirstDesktopActionsCompleted, per
// Toolkit.Shared.SessionState's own contract; WUA always re-searches fresh after a reboot.
public sealed class SessionService
{
    // JsonStringEnumConverter matches Toolkit.Shared.RuntimeSessionGuard's own JsonOptions - both
    // read/write the exact same session.json and must agree on how Workflow/Stage are encoded.
    // String enums also make a manually-staged test session.json (see dev-scripts/) and a
    // technician-inspected file both human-readable, rather than opaque integers.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    // Guards the whole mutate -> serialize -> temp-write -> atomic-rename sequence as one
    // logical operation - not just the file write. A plain lock/Monitor can't be held across an
    // await, which the file I/O here requires, hence SemaphoreSlim(1, 1) as the async-friendly
    // equivalent of a mutex.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SessionState? _current;

    // A plain field read - safe without the gate. Every WRITE to a field of the referenced
    // SessionState only ever happens synchronously inside MutateAsync's gated section, so a
    // concurrent reader here always sees either the state from before or after one complete
    // mutation, never a field-by-field partial one. Callers may read fields (e.g. Workflow) from
    // this freely; only mutation needs to go through MutateAsync.
    public SessionState Current =>
        _current ?? throw new InvalidOperationException("Session has not been loaded yet - call LoadAsync() first.");

    public async Task<SessionState> LoadAsync()
    {
        var path = RuntimeStaging.SessionPath;

        if (!File.Exists(path))
        {
            throw new SessionLoadException($"Session file not found at '{path}'.");
        }

        string json;
        try
        {
            json = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            throw new SessionLoadException($"Failed to read session file '{path}': {ex.Message}", ex);
        }

        SessionState? state;
        try
        {
            state = JsonSerializer.Deserialize<SessionState>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            // Covers both malformed JSON and a well-formed document missing a `required`
            // member (System.Text.Json throws for a missing Workflow/Stage rather than
            // defaulting) - either way this is a corrupt/incomplete session, not a fresh one.
            throw new SessionLoadException($"Session file '{path}' is corrupt: {ex.Message}", ex);
        }

        if (state is null)
        {
            throw new SessionLoadException($"Session file '{path}' deserialized to nothing.");
        }

        // A well-formed JSON document can still carry a numeric enum value with no corresponding
        // named member (e.g. a hand-edited or corrupted "Stage": 99) - JsonStringEnumConverter
        // accepts numbers as well as names and performs no range checking, so this would
        // otherwise silently produce a SessionState whose Workflow/Stage don't match any real
        // case this codebase handles. Reject it explicitly rather than letting it fall through
        // as an unrecognized value later (e.g. defaulting to Home, or being misread as Service).
        if (!Enum.IsDefined(state.Workflow))
        {
            throw new SessionLoadException($"Session file '{path}' has an unrecognized Workflow value ({(int)state.Workflow}).");
        }

        if (!Enum.IsDefined(state.Stage))
        {
            throw new SessionLoadException($"Session file '{path}' has an unrecognized Stage value ({(int)state.Stage}).");
        }

        await _gate.WaitAsync();
        try
        {
            _current = state;
        }
        finally
        {
            _gate.Release();
        }

        return state;
    }

    /// <summary>
    /// Atomically mutates the current session and persists the result - the only way callers
    /// should change session state (there is deliberately no public "mutate Current directly,
    /// then Save" path - that shape is exactly what let two concurrent writers race on the same
    /// temp file). Acquires the gate for the whole mutate + serialize + temp-write + atomic-
    /// rename sequence: a second concurrent call blocks until the first's mutation has been
    /// fully persisted, so mutations apply and land on disk in a well-defined order rather than
    /// overlapping.
    ///
    /// Copy-on-write: the mutation is applied to a cloned candidate, never to the object
    /// <see cref="Current"/> already returns. <see cref="Current"/> is only reassigned to that
    /// candidate AFTER serialization/write/rename all succeed - if any of them throws, this
    /// method propagates the exception and <see cref="Current"/> is left completely unchanged,
    /// still reflecting whatever was last actually persisted to disk. Without this, a failed
    /// persist could leave in-memory state (e.g. ResumeRequired=true) disagreeing with what
    /// session.json still says on disk, which lifecycle/close-tier logic elsewhere relies on
    /// matching.
    /// </summary>
    public async Task MutateAsync(Action<SessionState> mutate)
    {
        await _gate.WaitAsync();
        try
        {
            var candidate = Current.Clone();
            mutate(candidate);

            if (!Enum.IsDefined(candidate.Workflow))
            {
                throw new InvalidOperationException($"Mutation produced an unrecognized Workflow value ({(int)candidate.Workflow}).");
            }

            if (!Enum.IsDefined(candidate.Stage))
            {
                throw new InvalidOperationException($"Mutation produced an unrecognized Stage value ({(int)candidate.Stage}).");
            }

            RuntimeStaging.EnsureRuntimeFolderExists();
            var path = RuntimeStaging.SessionPath;
            var tempPath = path + ".tmp";
            var json = JsonSerializer.Serialize(candidate, JsonOptions);

            try
            {
                // Temp-file-then-rename: an NTFS rename is atomic, so a crash or power loss
                // mid-write can never leave a half-written session.json on disk - a reader always
                // sees either the complete old file or the complete new one, never a partial one.
                await File.WriteAllTextAsync(tempPath, json);
                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup of a stray temp file - not worth masking the real
                    // failure (propagated below) over this.
                }

                throw;
            }

            _current = candidate;
        }
        finally
        {
            _gate.Release();
        }
    }
}
