namespace Support_Tool.Services;

// ProcessId/StartTimeUtc identify the exact launched process this result came from - added so
// ProgramUninstallExecutor can look for a still-running descendant of THIS SPECIFIC invocation
// (via ProcessLineage) rather than matching by executable name, which could accidentally pick up
// an unrelated process. StartTimeUtc also lets that check reject a stale/reused PID - a candidate
// process that started before this one physically cannot be one of its descendants.
//
// StartTimeUtc is not always the process's own exact reported StartTime - if that can't be read
// (see ProcessRunner), it's a conservative floor captured immediately BEFORE Process.Start()
// instead, which can only ever be EARLIER than any real descendant's own start time, never later.
// A fallback timestamp taken any later than that (e.g. "now" read a few milliseconds after Start)
// could exclude a genuine child the launched process spawns almost immediately - see
// ProcessLineage's own genuineness check, which compares a candidate's start time against this
// value.
public record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    int ProcessId,
    DateTime StartTimeUtc);
