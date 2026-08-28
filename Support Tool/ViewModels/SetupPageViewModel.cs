namespace Support_Tool.ViewModels;

// Windows Update is embedded directly (not navigated to and away from, unlike Service) because
// it's the mandatory gate for Setup: there is currently nothing else implemented for Setup to
// progress to once it completes, so "prevent progressing past the gate" per the approved plan is
// satisfied by construction rather than needing separate navigation-blocking logic. Later Setup
// stages (Install/Debloat/Customization) are explicitly out of scope for Phase 2A - see
// WindowsUpdatePage's own "later stages not yet implemented" framing once WU finishes.
public class SetupPageViewModel : ViewModelBase
{
    public SetupPageViewModel(WindowsUpdatePageViewModel windowsUpdate)
    {
        WindowsUpdate = windowsUpdate;
    }

    public WindowsUpdatePageViewModel WindowsUpdate { get; }
}
