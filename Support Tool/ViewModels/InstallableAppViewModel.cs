using System.Windows.Media;
using System.Windows.Media.Imaging;
using Support_Tool.Models;

namespace Support_Tool.ViewModels;

public class InstallableAppViewModel : ViewModelBase
{
    private bool _isSelected;

    public InstallableAppViewModel(InstallableApp app, AppInstallState state, string? iconFilePath)
    {
        App = app;
        State = state;
        _isSelected = app.Preselected;

        Icon = string.IsNullOrWhiteSpace(iconFilePath)
            ? null
            : new BitmapImage(new Uri(iconFilePath, UriKind.Absolute));
    }

    public InstallableApp App { get; }
    public string Name => App.Name;
    public AppInstallState State { get; }

    // Falls back to the shared placeholder glyph when the icon couldn't be resolved/cached.
    public ImageSource? Icon { get; }

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
