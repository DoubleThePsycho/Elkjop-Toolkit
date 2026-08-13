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
        IRestorePointService restoreService,
        SharedViewState viewState)
        : base(uninstallService, restoreService, viewState)
    {
        _inventory = inventory;
        _rules = rules;
    }

    protected override string PageType => "Programs";

    protected override async Task<IEnumerable<InstalledItemViewModel>> FetchItemsAsync()
    {
        var ruleSet = await _rules.LoadAsync();
        var programs = await _inventory.GetInstalledProgramsAsync();

        return programs
            .Select(p => new InstalledItemViewModel(p, _rules.FindRule(p, ruleSet.Programs)));
    }
}
