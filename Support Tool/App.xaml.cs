using System.Windows;
using Support_Tool.Services;
using Support_Tool.ViewModels;

namespace Support_Tool;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var sharedState = new SharedViewState();

        var ruleService    = new RuleService();
        var restoreService = new DryRunRestorePointService();

        var dryRunExecutor = new DryRunUninstallExecutor();
        var realExecutor   = new RealUninstallExecutor(new ProcessRunner());
        var uninstallService = new UninstallService(dryRunExecutor, realExecutor);

        var appsVm = new DebloaterPageViewModel(
            new AppInventoryService(), ruleService, uninstallService, restoreService, sharedState);
        var programsVm = new ProgramsPageViewModel(
            new ProgramInventoryService(), ruleService, uninstallService, restoreService, sharedState);
        var settingsVm = new SettingsPageViewModel(sharedState);

        var mainVm = new MainViewModel(
            new SetupPageViewModel(),
            new ServicePageViewModel(),
            appsVm,
            programsVm,
            settingsVm);

        var window = new MainWindow { DataContext = mainVm };
        window.Show();
    }
}
