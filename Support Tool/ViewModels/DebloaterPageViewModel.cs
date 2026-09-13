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
        SharedViewState viewState)
        : base(uninstallService, viewState)
    {
        _inventory = inventory;
        _rules = rules;
    }

    protected override string PageType => "Apps";
    protected override string ItemsNoun => "apps";

    protected override async IAsyncEnumerable<InstalledItemViewModel> DiscoverAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var ruleSet = await _rules.LoadAsync();

        await foreach (var app in _inventory.GetInstalledAppsIncrementalAsync(ct).WithCancellation(ct))
        {
            yield return new InstalledItemViewModel(app, _rules.FindRule(app, ruleSet.Apps));
        }
    }
}
