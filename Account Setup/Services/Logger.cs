using Toolkit.Shared;

namespace Account_Setup.Services;

internal sealed class Logger
{
    private readonly ToolkitLogger _logger = new(AppInfo.ToolName);

    public string LogPath => _logger.VerboseLogPath;

    public void Info(string message) => _logger.Info(message);

    public void Warn(string message) => _logger.Warn(message);

    public void Error(string message) => _logger.Error(message);
}
