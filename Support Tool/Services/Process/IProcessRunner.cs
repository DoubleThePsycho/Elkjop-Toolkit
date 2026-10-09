using System.Diagnostics;

namespace Support_Tool.Services;

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, CancellationToken ct = default);
}
