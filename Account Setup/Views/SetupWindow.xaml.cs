using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Account_Setup.Models;
using Account_Setup.Services;

namespace Account_Setup.Views;

// WPF replacement for the old WinForms SetupForm (deleted with the 2026-08-16 WPF conversion),
// styled to the shared Elkjøp dark theme. Behavior is a faithful port:
// - Password reveal toggles between a PasswordBox and an overlaid plaintext TextBox (WPF's
//   PasswordBox cannot switch to plaintext in place the way WinForms' PasswordChar could).
//   The confirm box is cleared and disabled while revealed - seeing the password replaces
//   confirming it, same as before. The 36px right padding on both boxes keeps typed text from
//   running under the eye button - this replaces the old Win32 EM_SETRECT hack outright.
// - Password-mismatch shows 2px red underlines under both password fields, auto-cleared after
//   3 seconds or on any edit, and focus is deliberately dropped so no field's green focus
//   border sits right on top of the red underline.
internal sealed partial class SetupWindow : Window
{
    private readonly Logger _logger = new();
    private readonly DispatcherTimer _errorTimer;
    private bool _passwordVisible;

    public SetupSelection? Selection { get; private set; }

    public SetupWindow()
    {
        InitializeComponent();

        _errorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _errorTimer.Tick += (_, _) => ClearPasswordError();
    }

    public SetupWindow(WindowsInfo windowsInfo, string architecture, LanguageInfo languageInfo, Logger logger, string defaultUsername)
        : this()
    {
        _logger = logger;
        usernameBox.Text = defaultUsername;
        ApplyWindowsInfo(windowsInfo, architecture, languageInfo);
    }

    private void ApplyWindowsInfo(WindowsInfo windowsInfo, string architecture, LanguageInfo languageInfo)
    {
        detailsText.Text =
            $"Windows {windowsInfo.EditionId} {windowsInfo.DisplayVersion} | Build {windowsInfo.Build} | {architecture}";

        foreach (var language in languageInfo.Languages)
        {
            languageCombo.Items.Add(language);
        }

        for (var index = 0; index < languageCombo.Items.Count; index++)
        {
            var item = (LanguageItem)languageCombo.Items[index]!;
            if (string.Equals(item.Tag, languageInfo.DefaultTag, StringComparison.OrdinalIgnoreCase))
            {
                languageCombo.SelectedIndex = index;
                break;
            }
        }

        if (languageCombo.SelectedIndex < 0 && languageCombo.Items.Count > 0)
        {
            languageCombo.SelectedIndex = 0;
        }

        if (languageCombo.Items.Count <= 1)
        {
            languageCombo.IsEnabled = false;
        }
    }

    private void Window_ContentRendered(object? sender, EventArgs e)
    {
        usernameBox.Focus();
        usernameBox.SelectAll();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void UsernameBox_TextChanged(object sender, RoutedEventArgs e)
    {
        usernameWatermark.Visibility = usernameBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PasswordInput_Changed(object sender, RoutedEventArgs e) => ClearPasswordError();

    private void ClearPasswordError()
    {
        _errorTimer.Stop();
        passwordErrorBorder.Visibility = Visibility.Collapsed;
        confirmPasswordErrorBorder.Visibility = Visibility.Collapsed;
    }

    private void ShowPasswordError()
    {
        passwordErrorBorder.Visibility = Visibility.Visible;
        confirmPasswordErrorBorder.Visibility = Visibility.Visible;
        _errorTimer.Stop();
        _errorTimer.Start();
    }

    // The two boxes are only both "live" at the swap instant (one-way copy), so no continuous
    // sync is needed - and the confirm box is unusable while the password is visible, exactly
    // matching the old WinForms semantics.
    private string CurrentPassword => _passwordVisible ? passwordRevealBox.Text : passwordBox.Password;

    private void RevealButton_Click(object sender, RoutedEventArgs e)
    {
        ClearPasswordError();
        _passwordVisible = !_passwordVisible;

        if (_passwordVisible)
        {
            passwordRevealBox.Text = passwordBox.Password;
            passwordBox.Visibility = Visibility.Collapsed;
            passwordRevealBox.Visibility = Visibility.Visible;
            confirmPasswordBox.Clear();
            confirmPasswordBox.IsEnabled = false;
            revealGlyph.Data = (Geometry)FindResource("EyeHideGlyph");
            revealButton.ToolTip = "Hide password";
            passwordRevealBox.Focus();
            passwordRevealBox.CaretIndex = passwordRevealBox.Text.Length;
        }
        else
        {
            passwordBox.Password = passwordRevealBox.Text;
            passwordRevealBox.Visibility = Visibility.Collapsed;
            passwordBox.Visibility = Visibility.Visible;
            confirmPasswordBox.Clear();
            confirmPasswordBox.IsEnabled = true;
            revealGlyph.Data = (Geometry)FindResource("EyeShowGlyph");
            revealButton.ToolTip = "Show password";
            passwordBox.Focus(); // PasswordBox places the caret at the end on focus
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        var usernameResult = UsernameValidator.Validate(usernameBox.Text, _logger);

        if (!usernameResult.Valid)
        {
            Dialogs.Show(usernameResult.Error!, icon: MessageBoxImage.Warning);
            usernameBox.Focus();
            usernameBox.CaretIndex = usernameBox.Text.Length;
            return;
        }

        if (!_passwordVisible)
        {
            var passwordsMatch = string.Equals(passwordBox.Password, confirmPasswordBox.Password, StringComparison.Ordinal);

            if (!passwordsMatch)
            {
                ShowPasswordError();
                // Neither password field keeps focus here: a focused box draws the green accent
                // border, which would sit directly on top of the red error underline.
                Keyboard.ClearFocus();
                return;
            }
        }

        if (languageCombo.SelectedIndex < 0)
        {
            Dialogs.Show("No Windows display language is available.", icon: MessageBoxImage.Warning);
            return;
        }

        var selectedLanguage = (LanguageItem)languageCombo.SelectedItem!;

        Selection = new SetupSelection(usernameResult.Username, CurrentPassword, selectedLanguage.Tag);
        DialogResult = true; // auto-closes a ShowDialog-shown window
    }
}
