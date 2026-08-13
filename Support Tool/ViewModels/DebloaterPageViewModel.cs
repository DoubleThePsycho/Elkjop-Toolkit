using Support_Tool.Services;

namespace Support_Tool.ViewModels;

public class DebloaterPageViewModel : InstalledItemsPageViewModel
{
    private readonly AppInventoryService _inventory;
    private readonly RuleService _rules;

    public DebloaterPageViewModel(
        AppInventoryService inventory,
        RuleService rules,
        UninstallService uninstallService,
        IRestorePointService restoreService,
        SharedViewState viewState)
        : base(uninstallService, restoreService, viewState)
    {
        _inventory = inventory;
        _rules = rules;
    }

    protected override string PageType => "Apps";

    protected override async Task<IEnumerable<InstalledItemViewModel>> FetchItemsAsync()
    {
        var ruleSet = await _rules.LoadAsync();
        var apps = await Task.Run(async () => await _inventory.GetInstalledAppsAsync());

        return apps
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => new InstalledItemViewModel(a, _rules.FindRule(a, ruleSet.Apps)));
    }
}
