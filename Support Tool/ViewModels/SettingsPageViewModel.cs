namespace Support_Tool.ViewModels;

public class SettingsPageViewModel : ViewModelBase
{
    public SharedViewState ViewState { get; }

    public SettingsPageViewModel(SharedViewState viewState)
    {
        ViewState = viewState;
    }
}
