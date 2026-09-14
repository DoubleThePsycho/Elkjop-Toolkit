using System.Windows.Media;
using System.Windows.Media.Imaging;
using Support_Tool.Models;
using Toolkit.Shared.Configuration;

namespace Support_Tool.ViewModels;

public class InstallableAppViewModel : ViewModelBase
{
    private bool _isSelected;
    private AppInstallState _state;
    private ImageSource? _icon;

    public InstallableAppViewModel(InstallableApp app, AppInstallState state, string? iconFilePath)
    {
        App = app;
        _state = state;
        _isSelected = app.Preselected;
        _icon = ToImageSource(iconFilePath);
    }

    public InstallableApp App { get; }
    public string Name => App.Name;

    // Settable (not just constructor-assigned) so InstallPageViewModel's post-batch targeted
    // refresh (see its own remarks) can update this exact instance in place instead of the page
    // rebuilding/reloading - StatusText re-notifies alongside it since it's derived.
    public AppInstallState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    // Settable for the same reason as State - the incremental catalog loader (see
    // InstallPageViewModel.LoadCoreAsync) shows a cached/placeholder icon immediately, then
    // updates this in place once a background fetch resolves, without ever blocking the card's own
    // visibility on the network round-trip. Falls back to the shared placeholder glyph when null.
    public ImageSource? Icon
    {
        get => _icon;
        set => SetProperty(ref _icon, value);
    }

    public static ImageSource? ToImageSource(string? iconFilePath) =>
        string.IsNullOrWhiteSpace(iconFilePath)
            ? null
            : new BitmapImage(new Uri(iconFilePath, UriKind.Absolute));

    public string StatusText => State switch
    {
        AppInstallState.NotInstalled    => "Not installed",
        AppInstallState.Installed       => "Installed",
        AppInstallState.UpdateAvailable => "Update available",
        _ => ""
    };

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
