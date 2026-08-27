using System.Windows;

namespace Account_Setup.Services;

// Deliberately stays on the native system MessageBox (WPF's System.Windows.MessageBox) rather
// than a custom-themed dialog - error/confirm popups keep the OS look by design (confirmed
// choice during the WPF conversion, 2026-08-16).
internal static class Dialogs
{
    public static MessageBoxResult Show(
        string text,
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.Information)
        => MessageBox.Show(text, AppInfo.DialogTitle, buttons, icon);
}
