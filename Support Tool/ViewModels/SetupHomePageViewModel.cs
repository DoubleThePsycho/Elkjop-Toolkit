namespace Support_Tool.ViewModels;

// Setup Home - the Setup workflow's own Home destination (see App.xaml.cs's
// ResolveStartupNavigation and MainViewModel.NavigateHomeCommand, both of which route a Setup
// session here instead of the generic Service HomePageViewModel). Deliberately kept general/
// neutral, not a "choose the customer's setup package" gateway - browser/SA Standard/SA Gaming
// profile selection moved to InstallPageViewModel, which is now their canonical home (external
// review). This page intentionally has no async load step and no state of its own beyond the one
// navigation command below - room is left for later expansion, and possibly later consolidation
// with the Service workflow's own Home, but neither of those happens in this pass.
public class SetupHomePageViewModel : ViewModelBase
{
    public SetupHomePageViewModel(RelayCommand navigateInstall)
    {
        NavigateInstallCommand = navigateInstall;
    }

    public RelayCommand NavigateInstallCommand { get; }
}
