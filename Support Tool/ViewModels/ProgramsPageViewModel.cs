using Support_Tool.Services;

namespace Support_Tool.ViewModels;

public class ProgramsPageViewModel : InstalledItemsPageViewModel
{
    private readonly ProgramInventoryService _inventory;
    private readonly RuleService _rules;

    public ProgramsPageViewModel(
        ProgramInventoryService inventory,
        RuleService rules,
        UninstallService uninstallService,
        SharedViewState viewState)
        : base(uninstallService, viewState)
    {
        _inventory = inventory;
        _rules = rules;
    }

    protected override string PageType => "Programs";
    protected override string ItemsNoun => "programs";

    protected override async IAsyncEnumerable<InstalledItemViewModel> DiscoverAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var ruleSet = await _rules.LoadAsync();

        await foreach (var program in _inventory.GetInstalledProgramsIncrementalAsync(ct).WithCancellation(ct))
        {
            yield return new InstalledItemViewModel(program, _rules.FindRule(program, ruleSet.Programs));
        }
    }
}
