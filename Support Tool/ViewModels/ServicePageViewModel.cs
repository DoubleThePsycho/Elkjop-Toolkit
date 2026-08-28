namespace Support_Tool.ViewModels;

// Windows Update is an optional tool here (unlike Setup, where it's embedded directly as a
// mandatory gate) - the technician navigates to it via MainViewModel.NavigateWindowsUpdateCommand
// (bound in ServicePage.xaml through the ancestor Window's DataContext, since that command lives
// on MainViewModel, not here) and can navigate away to any other page at any time.
public class ServicePageViewModel : ViewModelBase { }
