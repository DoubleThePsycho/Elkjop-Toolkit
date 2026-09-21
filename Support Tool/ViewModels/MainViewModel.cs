using Support_Tool.Services;
using Toolkit.Shared;

namespace Support_Tool.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly HomePageViewModel _homePage;
    private readonly SetupHomePageViewModel _setupHomePage;
    private object _currentPage;

    public MainViewModel(
        SharedViewState sharedState,
        SessionService sessionService,
        RuntimeCleanupService runtimeCleanupService,
        ServicePageViewModel servicePage,
        InstallPageViewModel installPage,
        DebloaterPageViewModel appsPage,
        ProgramsPageViewModel programsPage,
        SettingsPageViewModel settingsPage,
        WindowsUpdatePageViewModel windowsUpdatePage)
    {
        SharedState  = sharedState;
        SessionService = sessionService;
        RuntimeCleanupService = runtimeCleanupService;
        ServicePage  = servicePage;
        InstallPage  = installPage;
        AppsPage     = appsPage;
        ProgramsPage = programsPage;
        SettingsPage = settingsPage;
        WindowsUpdatePage = windowsUpdatePage;

        NavigateServiceCommand  = new RelayCommand(() => NavigateTo(servicePage));

        // Install always goes straight to the raw catalog for BOTH workflows - Setup Home (below)
        // is reached via the logo/Home entry instead, not by redirecting this command. An earlier
        // version of this pass made this command itself Setup-aware, which made Setup Home read as
        // an "Install gateway" rather than genuinely being Setup's Home - corrected per external
        // review.
        NavigateInstallCommand  = new RelayCommand(() => NavigateTo(installPage));

        NavigateAppsCommand     = new RelayCommand(() => NavigateTo(appsPage));
        NavigateProgramsCommand = new RelayCommand(() => NavigateTo(programsPage));
        NavigateSettingsCommand = new RelayCommand(() => NavigateTo(settingsPage));
        NavigateWindowsUpdateCommand = new RelayCommand(() => NavigateTo(windowsUpdatePage));

        _homePage = new HomePageViewModel(
            new RelayCommand(() => NavigateTo(appsPage)),
            new RelayCommand(() => NavigateTo(programsPage)));

        // Constructed here (not passed in from App.xaml.cs) for the same reason _homePage is -
        // its one quick-access card needs NavigateInstallCommand, which only exists once this
        // constructor has already run past the line above. SetupHomePageViewModel no longer owns
        // any profile logic (moved to InstallPageViewModel, see its own remarks), so it only ever
        // needs this one command.
        _setupHomePage = new SetupHomePageViewModel(NavigateInstallCommand);
        SetupHomePage = _setupHomePage;

        // Workflow-aware: the logo/Home entry is Setup's OWN home for the Setup workflow (Setup
        // Home) matching App.xaml.cs's ResolveStartupNavigation's own Stage==Install destination,
        // which calls this exact command. Service is completely unaffected - always goes to the
        // existing generic HomePageViewModel, exactly as before.
        NavigateHomeCommand = new RelayCommand(() => NavigateTo(
            sessionService.Current.Workflow == ToolkitWorkflow.Setup ? _setupHomePage : _homePage));

        _currentPage = _homePage;

        // Re-raised whenever the flag the footer's visibility actually depends on changes -
        // NotifyPageActiveChanged (below) covers the other half (leaving/entering the Windows
        // Update page itself).
        WindowsUpdatePage.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(WindowsUpdatePageViewModel.HasActiveOrPendingOperation))
            {
                OnPropertyChanged(nameof(ShowWindowsUpdateFooter));
            }
        };

        // Same pattern as the Windows Update footer above - InstallPage is already the one
        // long-lived InstallPageViewModel instance (no second install engine, no polling); this
        // just re-raises the persistent Install footer's own visibility flag whenever the state
        // it depends on changes.
        InstallPage.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(InstallPageViewModel.IsInstalling))
            {
                OnPropertyChanged(nameof(ShowInstallFooter));
            }
        };
    }

    public SharedViewState SharedState { get; }
    public SessionService SessionService { get; }
    public RuntimeCleanupService RuntimeCleanupService { get; }
    public ServicePageViewModel  ServicePage  { get; }
    public InstallPageViewModel  InstallPage  { get; }
    public DebloaterPageViewModel AppsPage    { get; }
    public ProgramsPageViewModel ProgramsPage { get; }
    public SettingsPageViewModel SettingsPage { get; }
    public WindowsUpdatePageViewModel WindowsUpdatePage { get; }
    public SetupHomePageViewModel SetupHomePage { get; }

    public RelayCommand NavigateHomeCommand     { get; }
    public RelayCommand NavigateServiceCommand  { get; }
    public RelayCommand NavigateInstallCommand  { get; }
    public RelayCommand NavigateAppsCommand     { get; }
    public RelayCommand NavigateProgramsCommand { get; }
    public RelayCommand NavigateSettingsCommand { get; }
    public RelayCommand NavigateWindowsUpdateCommand { get; }

    // The persistent cross-page Windows Update footer: visible whenever there's an active run or
    // something that needs attention (WindowsUpdatePage.HasActiveOrPendingOperation) AND the
    // technician isn't already looking at the full Windows Update page itself (no point showing
    // a compact summary of the page they're already on).
    public bool ShowWindowsUpdateFooter => WindowsUpdatePage.HasActiveOrPendingOperation && !IsWindowsUpdatePage;

    // Same idea for an active install batch - follows the technician to any other page, but never
    // duplicates the Install page's own full-size progress UI while they're already looking at it.
    // WU and Install can legitimately be active at the same time (e.g. Windows Update running in
    // the background while the technician starts an install elsewhere) - both footers are
    // independent and MainWindow.xaml stacks them rather than one replacing the other.
    public bool ShowInstallFooter => InstallPage.IsInstalling && !IsInstallPage;

    public object CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
                NotifyPageActiveChanged();
        }
    }

    // Strictly "is the generic HomePageViewModel instance active" - kept narrow/unchanged in
    // meaning in case anything else ever comes to depend on that exact distinction. The sidebar
    // logo's own active/highlight visual should NOT use this alone - see IsHomeNavigationActive.
    public bool IsHomePage     => _currentPage is HomePageViewModel;
    public bool IsServicePage  => _currentPage is ServicePageViewModel;
    public bool IsInstallPage  => _currentPage is InstallPageViewModel;
    public bool IsAppsPage     => _currentPage is DebloaterPageViewModel;
    public bool IsProgramsPage => _currentPage is ProgramsPageViewModel;
    public bool IsSettingsPage => _currentPage is SettingsPageViewModel;
    public bool IsWindowsUpdatePage => _currentPage is WindowsUpdatePageViewModel;
    public bool IsSetupHomePage => _currentPage is SetupHomePageViewModel;

    // What the sidebar logo/"Home" nav entry should actually treat as "currently active" - true
    // for either landing page NavigateHomeCommand can send a technician to (the generic
    // HomePageViewModel for Service, or SetupHomePageViewModel for Setup), since both are reached
    // via that same one entry point and neither should be mistaken for the raw Install page.
    public bool IsHomeNavigationActive => IsHomePage || IsSetupHomePage;

    private void NotifyPageActiveChanged()
    {
        OnPropertyChanged(nameof(IsHomePage));
        OnPropertyChanged(nameof(IsServicePage));
        OnPropertyChanged(nameof(IsInstallPage));
        OnPropertyChanged(nameof(IsAppsPage));
        OnPropertyChanged(nameof(IsProgramsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        OnPropertyChanged(nameof(IsWindowsUpdatePage));
        OnPropertyChanged(nameof(IsSetupHomePage));
        OnPropertyChanged(nameof(IsHomeNavigationActive));
        OnPropertyChanged(nameof(ShowWindowsUpdateFooter));
        OnPropertyChanged(nameof(ShowInstallFooter));
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
            case SettingsPageViewModel vm:
                // Config load happens once per process (HasLoaded), like the other pages above -
                // but portable-source status is refreshed on every navigation regardless, since
                // the USB can be inserted/removed/reinserted under a different drive letter at
                // any point while Support Tool keeps running, independent of whether the config
                // itself has already been loaded.
                if (!vm.HasLoaded)
                {
                    await vm.LoadAsync();
                }

                await vm.RefreshPortableStatusAsync();
                break;
        }
    }
}
