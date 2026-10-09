using Support_Tool.Models;

namespace Support_Tool.Services;

public class InstallCatalogService
{
    private readonly ConfigService _configService;

    public InstallCatalogService(ConfigService configService)
    {
        _configService = configService;
    }

    public async Task<List<InstallCategory>> GetCatalogAsync()
    {
        var config = await _configService.LoadAsync();

        return config.Installer.Categories
            .Select(kvp => new InstallCategory
            {
                Name = kvp.Key,
                ShowSelectAll = kvp.Value.InstallAll,
                Apps = kvp.Value.Apps
            })
            .ToList();
    }
}
