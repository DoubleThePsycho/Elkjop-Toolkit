using System.Reflection;

namespace Support_Tool.ViewModels;

public class HomePageViewModel : ViewModelBase
{
    public HomePageViewModel(RelayCommand navigateApps, RelayCommand navigatePrograms)
    {
        NavigateAppsCommand     = navigateApps;
        NavigateProgramsCommand = navigatePrograms;

        var ver = Assembly.GetExecutingAssembly().GetName().Version;
        AppVersion = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v1.0";
    }

    public RelayCommand NavigateAppsCommand     { get; }
    public RelayCommand NavigateProgramsCommand { get; }
    public string AppVersion { get; }
}
