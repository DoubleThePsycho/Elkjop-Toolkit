using Account_Setup.Models;
using Toolkit.Shared;

namespace Account_Setup.Services;

internal static class AppConfigService
{
    private static readonly string ConfigPath = Path.Combine(ToolkitPaths.GetConfigFolder(AppInfo.ToolName), "config.json");

    public static Task<AppConfig> LoadAsync() =>
        JsonConfigStore.LoadOrCreateAsync(ConfigPath, () => new AppConfig());
}
