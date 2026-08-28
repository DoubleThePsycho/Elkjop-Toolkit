using Toolkit.Shared;

namespace Support_Tool.Services;

// Thin generic Info/Warn/Error wrapper around ToolkitLogger, ported verbatim (shape-for-shape)
// from Setup Agent's own Services/Logger.cs so the ported Windows Update/connectivity engine
// (Services/WindowsUpdate, Services/Connectivity, RebootService, StartMenuDismisser) compiles
// with zero internal changes beyond the namespace move - those files all reference this type
// unqualified, exactly as they did in Setup Agent's Setup_Agent.Services namespace.
//
// Deliberately separate from InstallLogger/UninstallLogger, which are domain-specific session
// loggers (banner + LogResult/LogSessionEnd) for a different purpose - this one is the generic
// pass-through the ported engine code was already written against.
// Public (not internal, unlike Setup Agent's original) - WindowsUpdatePageViewModel's public
// constructor and IWindowsUpdateExecutor/IWifiConnectionService's public method signatures both
// need to reference this type, since every ViewModel/Service class in Support Tool is public by
// this codebase's own existing convention (see WindowsUpdatePageViewModel's remarks for the full
// cascade this one change is part of).
public sealed class Logger
{
    private readonly ToolkitLogger _logger = new(AppInfo.ToolName);

    public string LogPath => _logger.VerboseLogPath;

    public void Info(string message) => _logger.Info(message);

    public void Warn(string message) => _logger.Warn(message);

    public void Error(string message) => _logger.Error(message);
}
