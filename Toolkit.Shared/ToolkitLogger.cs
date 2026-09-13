using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace Toolkit.Shared;

public sealed class ToolkitLogger
{
    // Keyed by full log file path, not per-ToolkitLogger-instance: several independent instances
    // (Logger, InstallLogger, UninstallLogger, and any other caller across the process) each
    // construct their own ToolkitLogger, but for a given tool they all resolve to the exact same
    // Verbose.log/Minimal.log path - an instance-level lock would do nothing to serialize two
    // different instances writing to that same file. File.AppendAllText's own open-append-close
    // cycle is not safe against genuinely concurrent callers (RealInstallExecutor's own async
    // stdout/stderr handlers alone can call this from two different threads at the same instant,
    // and Windows Update/other Support Tool logging can land here at the same time too) - without
    // this lock, concurrent writers can silently interleave or lose whole lines. Keyed by path
    // (rather than one single global lock) so writes to genuinely different files never contend.
    private static readonly ConcurrentDictionary<string, object> PathLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _verbosePath;
    private readonly string _minimalPath;

    public ToolkitLogger(string toolName)
    {
        var folder = ToolkitPaths.GetLocalFolder(toolName);
        _verbosePath = Path.Combine(folder, "Verbose.log");
        _minimalPath = Path.Combine(folder, "Minimal.log");
    }

    public string VerboseLogPath => _verbosePath;

    public string MinimalLogPath => _minimalPath;

    public void Info(string message) => WriteVerbose(message, "INFO");

    public void Warn(string message) => WriteVerbose(message, "WARN");

    public void Error(string message) => WriteVerbose(message, "ERROR");

    /// <summary>
    /// Records an attempted change (add/modify/remove). Written to both logs —
    /// Verbose gets it inline with everything else, Minimal gets only this.
    /// </summary>
    public void Action(string description, string outcome)
    {
        WriteVerbose($"{description} — {outcome}", "INFO");

        var block = $"[{Timestamp()}] {description}{Environment.NewLine}- {outcome}{Environment.NewLine}{Environment.NewLine}";
        Append(_minimalPath, block);
    }

    private void WriteVerbose(string message, string level)
    {
        var line = $"[{Timestamp()}] [{level}] {message}{Environment.NewLine}";
        Append(_verbosePath, line);
    }

    private static string Timestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

    private static void Append(string path, string content)
    {
        var pathLock = PathLocks.GetOrAdd(path, static _ => new object());

        try
        {
            lock (pathLock)
            {
                File.AppendAllText(path, content, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ToolkitLogger] Failed to write '{path}': {ex.Message}");
        }
    }
}
