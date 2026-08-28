namespace Support_Tool.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly HomePageViewModel _homePage;
    private object _currentPage;

    public MainViewModel(
        SharedViewState sharedState,
        SetupPageViewModel setupPage,
        ServicePageViewModel servicePage,
        InstallPageViewModel installPage,
        DebloaterPageViewModel appsPage,
        ProgramsPageViewModel programsPage,
        SettingsPageViewModel settingsPage,
        WindowsUpdatePageViewModel windowsUpdatePage)
    {
        SharedState  = sharedState;
        SetupPage    = setupPage;
        ServicePage  = servicePage;
        InstallPage  = installPage;
        AppsPage     = appsPage;
        ProgramsPage = programsPage;
        SettingsPage = settingsPage;
        WindowsUpdatePage = windowsUpdatePage;

        NavigateSetupCommand    = new RelayCommand(() => NavigateTo(setupPage));
        NavigateServiceCommand  = new RelayCommand(() => NavigateTo(servicePage));
        NavigateInstallCommand  = new RelayCommand(() => NavigateTo(installPage));
        NavigateAppsCommand     = new RelayCommand(() => NavigateTo(appsPage));
        NavigateProgramsCommand = new RelayCommand(() => NavigateTo(programsPage));
        NavigateSettingsCommand = new RelayCommand(() => NavigateTo(settingsPage));
        NavigateWindowsUpdateCommand = new RelayCommand(() => NavigateTo(windowsUpdatePage));

        _homePage = new HomePageViewModel(
            new RelayCommand(() => NavigateTo(appsPage)),
            new RelayCommand(() => NavigateTo(programsPage)));

        NavigateHomeCommand = new RelayCommand(() => NavigateTo(_homePage));

        _currentPage = _homePage;
    }

    public SharedViewState SharedState { get; }
    public SetupPageViewModel    SetupPage    { get; }
    public ServicePageViewModel  ServicePage  { get; }
    public InstallPageViewModel  InstallPage  { get; }
    public DebloaterPageViewModel AppsPage    { get; }
    public ProgramsPageViewModel ProgramsPage { get; }
    public SettingsPageViewModel SettingsPage { get; }
    public WindowsUpdatePageViewModel WindowsUpdatePage { get; }

    public RelayCommand NavigateHomeCommand     { get; }
    public RelayCommand NavigateSetupCommand    { get; }
    public RelayCommand NavigateServiceCommand  { get; }
    public RelayCommand NavigateInstallCommand  { get; }
    public RelayCommand NavigateAppsCommand     { get; }
    public RelayCommand NavigateProgramsCommand { get; }
    public RelayCommand NavigateSettingsCommand { get; }
    public RelayCommand NavigateWindowsUpdateCommand { get; }

    public object CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
                NotifyPageActiveChanged();
        }
    }

    public bool IsHomePage     => _currentPage is HomePageViewModel;
    public bool IsSetupPage    => _currentPage is SetupPageViewModel;
    public bool IsServicePage  => _currentPage is ServicePageViewModel;
    public bool IsInstallPage  => _currentPage is InstallPageViewModel;
    public bool IsAppsPage     => _currentPage is DebloaterPageViewModel;
    public bool IsProgramsPage => _currentPage is ProgramsPageViewModel;
    public bool IsSettingsPage => _currentPage is SettingsPageViewModel;
    public bool IsWindowsUpdatePage => _currentPage is WindowsUpdatePageViewModel;

    private void NotifyPageActiveChanged()
    {
        OnPropertyChanged(nameof(IsHomePage));
        OnPropertyChanged(nameof(IsSetupPage));
        OnPropertyChanged(nameof(IsServicePage));
        OnPropertyChanged(nameof(IsInstallPage));
        OnPropertyChanged(nameof(IsAppsPage));
        OnPropertyChanged(nameof(IsProgramsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsWindowsUpdatePage));
    }

    private async void NavigateTo(object page)
    {
        CurrentPage = page;
        switch (page)
        {
            case InstalledItemsPageViewModel vm when !vm.HasLoaded:
                await vm.LoadAsync();
                break;
            case InstallPageViewModel vm when !vm.HasLoaded:
                await vm.LoadAsync();
                break;
        }
    }
}
