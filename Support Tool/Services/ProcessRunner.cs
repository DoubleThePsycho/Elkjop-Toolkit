using System.Diagnostics;
using System.Text;

namespace Support_Tool.Services;

public class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, CancellationToken ct = default)
    {
        var stdOut = new StringBuilder();
        var stdErr = new StringBuilder();

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        if (startInfo.RedirectStandardOutput)
            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdOut.AppendLine(e.Data); };
        if (startInfo.RedirectStandardError)
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stdErr.AppendLine(e.Data); };

        // Captured BEFORE Process.Start() - the conservative floor used as a fallback below if
        // the real StartTime can't be read. External review, correction: a fallback captured
        // AFTER Start() (e.g. "now," read a few milliseconds later) can be LATER than a genuine
        // child process the launched process spawns almost immediately, which would make
        // ProcessLineage's "candidate.StartTime >= rootStartTimeUtc" genuineness check wrongly
        // discard that real descendant - the opposite of "more permissive" the original comment
        // here claimed. A floor taken before Start() can only ever be EARLIER than or equal to
        // any real descendant's own start time, never later, so it can only make the check
        // slightly more permissive (accepting a rare PID-reuse false positive as Started), never
        // less safe (never causing a false Succeeded by excluding a genuine live descendant).
        var launchFloorUtc = DateTime.UtcNow;

        process.Start();

        var processId = process.Id;
        DateTime startTimeUtc;
        try
        {
            startTimeUtc = process.StartTime.ToUniversalTime();
        }
        catch
        {
            // StartTime can be unreadable in a handful of edge cases (process already exited
            // instantly, access denied to a process running under a different token) - fall back
            // to the pre-Start() floor captured above, never to a post-Start() timestamp.
            startTimeUtc = launchFloorUtc;
        }

        if (startInfo.RedirectStandardOutput) process.BeginOutputReadLine();
        if (startInfo.RedirectStandardError) process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        return new ProcessResult(
            process.ExitCode,
            stdOut.ToString().Trim(),
            stdErr.ToString().Trim(),
            processId,
            startTimeUtc);
    }
}
