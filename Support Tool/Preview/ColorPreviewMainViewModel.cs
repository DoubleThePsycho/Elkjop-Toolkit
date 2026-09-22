#if COLOR
using Support_Tool.ViewModels;

namespace Support_Tool.Preview;

// DEVELOPMENT-ONLY preview DataContext for MainWindow (see CLAUDE.md's "Color preview build"
// section) - NOT a MainViewModel, and this whole file only exists in a Color build. Exposes
// exactly the property/command names MainWindow.xaml's own bindings need so the real
// MainWindow.xaml renders with representative content, without constructing any of the real
// production services (ConfigService, SessionService, WindowsUpdatePageViewModel,
// InstallPageViewModel, etc.) a real MainViewModel would otherwise require - several of which
// read Runtime\config.json/session.json at construction/load time, which a Color build must
// never require. Every navigation command is a harmless no-op (clicking a sidebar item does
// nothing) rather than a disabled command, so the sidebar's own hover/visual feedback still works
// normally for layout/theme comparison purposes.
//
// MainWindow.xaml.cs's OnClosing casts DataContext to MainViewModel before running any of its
// three-tier close/teardown logic - since this preview's DataContext is a different type
// entirely, that cast always fails and OnClosing falls straight through to a plain
// base.OnClosing(e), so closing this preview window can never touch Runtime/session/config/
// scheduled tasks.
public sealed class ColorPreviewMainViewModel
{
    public ColorPreviewMainViewModel()
    {
        CurrentPage = new HomePageViewModel(new RelayCommand(() => { }), new RelayCommand(() => { }));
    }

    public object CurrentPage { get; }

    public bool IsHomeNavigationActive => true;
    public bool IsServicePage => false;
    public bool IsInstallPage => false;
    public bool IsAppsPage => false;
    public bool IsProgramsPage => false;
    public bool IsSettingsPage => false;
    public bool IsWindowsUpdatePage => false;

    public bool ShowWindowsUpdateFooter => false;
    public bool ShowInstallFooter => false;

    // Never actually rendered while the footers above stay Collapsed - present only so
    // MainWindow.xaml's own footer-scoped bindings (Grid DataContext="{Binding WindowsUpdatePage}"
    // / "{Binding InstallPage}") resolve cleanly instead of logging a binding-not-found trace.
    public object? WindowsUpdatePage => null;
    public object? InstallPage => null;

    public RelayCommand NavigateHomeCommand { get; } = new(() => { });
    public RelayCommand NavigateServiceCommand { get; } = new(() => { });
    public RelayCommand NavigateInstallCommand { get; } = new(() => { });
    public RelayCommand NavigateAppsCommand { get; } = new(() => { });
    public RelayCommand NavigateProgramsCommand { get; } = new(() => { });
    public RelayCommand NavigateSettingsCommand { get; } = new(() => { });
    public RelayCommand NavigateWindowsUpdateCommand { get; } = new(() => { });
}
#endif
