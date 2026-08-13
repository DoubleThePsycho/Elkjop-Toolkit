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

        process.Start();

        if (startInfo.RedirectStandardOutput) process.BeginOutputReadLine();
        if (startInfo.RedirectStandardError) process.BeginErrorReadLine();

        await process.WaitForExitAsync(ct);

        return new ProcessResult(
            process.ExitCode,
            stdOut.ToString().Trim(),
            stdErr.ToString().Trim());
    }
}
