using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Support_Tool.ViewModels;

namespace Support_Tool.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // A fresh SettingsPage instance is created by the DataTemplate every time the technician
    // navigates back to Settings (MainWindow's ContentControl re-templates on each CurrentPage
    // change) even though SettingsPageViewModel itself is a long-lived process singleton - so
    // this hookup (and the initial Password assignment) has to happen here, not just once.
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SettingsPageViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is SettingsPageViewModel newVm)
        {
            newVm.PropertyChanged += OnViewModelPropertyChanged;
            WifiPasswordBox.Password = newVm.WifiPassword;
        }
    }

    // Covers the case where the page is already visible before LoadAsync's awaited config load
    // completes (navigation sets CurrentPage - which this View is templated from - before
    // MainViewModel.NavigateTo's await finishes) - the PasswordBox needs to pick up the loaded
    // value once it actually arrives, not just at DataContextChanged time.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SettingsPageViewModel.WifiPassword) || sender is not SettingsPageViewModel vm)
        {
            return;
        }

        if (WifiPasswordBox.Password != vm.WifiPassword)
        {
            WifiPasswordBox.Password = vm.WifiPassword;
        }
    }

    // PasswordBox.Password is deliberately not a DependencyProperty in WPF (so a password can
    // never end up bound into the visual tree) - this is the minimal code-behind forwarding
    // needed instead, not a general attached-property framework.
    private void WifiPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsPageViewModel vm)
        {
            vm.WifiPassword = WifiPasswordBox.Password;
        }
    }
}
