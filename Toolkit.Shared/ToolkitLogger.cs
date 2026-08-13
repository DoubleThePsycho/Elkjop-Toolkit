using System.Diagnostics;
using System.Text;

namespace Toolkit.Shared;

public sealed class ToolkitLogger
{
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
        try
        {
            File.AppendAllText(path, content, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ToolkitLogger] Failed to write '{path}': {ex.Message}");
        }
    }
}
