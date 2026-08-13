using Bypass_OOBE.Models;
using Bypass_OOBE.Services;

namespace Bypass_OOBE.Forms;

// All input fields live inside contentPanel with fixed padding. Fields are anchored
// Left+Right (instead of given a hardcoded Width) so resizing the window restretches
// them automatically - only the panel's padding defines the margins.
internal partial class SetupForm : Form
{
    // "hide"/"show" eye glyphs (Nucleo-style outline icons), stripped down to just the path data.
    private const string HideIconPath =
        "M2.22 2.22a.75.75 0 0 0-.073.976l.073.084 4.034 4.035a9.986 9.986 0 0 0-3.955 5.75.75.75 0 0 0 1.455.364 8.49 8.49 0 0 1 3.58-5.034l1.81 1.81A4 4 0 0 0 14.8 15.86l5.919 5.92a.75.75 0 0 0 1.133-.977l-.073-.084-6.113-6.114.001-.002-6.95-6.946.002-.002-1.133-1.13L3.28 2.22a.75.75 0 0 0-1.06 0ZM12 5.5c-1 0-1.97.148-2.889.425l1.237 1.236a8.503 8.503 0 0 1 9.899 6.272.75.75 0 0 0 1.455-.363A10.003 10.003 0 0 0 12 5.5Zm.195 3.51 3.801 3.8a4.003 4.003 0 0 0-3.801-3.8Z";
    private const string ShowIconPath =
        "M12 9.005a4 4 0 1 1 0 8 4 4 0 0 1 0-8ZM12 5.5c4.613 0 8.596 3.15 9.701 7.564a.75.75 0 1 1-1.455.365 8.503 8.503 0 0 0-16.493.004.75.75 0 0 1-1.455-.363A10.003 10.003 0 0 1 12 5.5Z";

    private const int EmGetRect = 0xB2;
    private const int EmSetRect = 0xB3;

    private Logger _logger = new();
    private bool _passwordVisible;
    private Bitmap? _showIcon;
    private Bitmap? _hideIcon;

    public SetupSelection? Selection { get; private set; }

    public SetupForm()
    {
        InitializeComponent();
        InitializeRevealIcon();
    }

    public SetupForm(WindowsInfo windowsInfo, string architecture, LanguageInfo languageInfo, Logger logger, string defaultUsername)
        : this()
    {
        _logger = logger;
        usernameBox.Text = defaultUsername;
        ApplyWindowsInfo(windowsInfo, architecture, languageInfo);
    }

    private void ApplyWindowsInfo(WindowsInfo windowsInfo, string architecture, LanguageInfo languageInfo)
    {
        detailsLabel.Text =
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
            languageCombo.Enabled = false;
        }
    }

    private void SetupForm_Shown(object? sender, EventArgs e)
    {
        usernameBox.Focus();
        usernameBox.SelectAll();
        UpdatePasswordTextInset();
    }

    private void SetupForm_FormClosed(object? sender, FormClosedEventArgs e)
    {
        errorTimer.Dispose();
        _showIcon?.Dispose();
        _hideIcon?.Dispose();
    }

    private void ErrorTimer_Tick(object? sender, EventArgs e)
    {
        errorTimer.Stop();
        ClearPasswordError();
    }

    private void PasswordBox_TextChanged(object? sender, EventArgs e) => ClearPasswordError();

    private void ConfirmPasswordBox_TextChanged(object? sender, EventArgs e) => ClearPasswordError();

    private void ClearPasswordError()
    {
        errorTimer.Stop();
        passwordErrorBorder.Visible = false;
        confirmPasswordErrorBorder.Visible = false;
    }

    private void ShowPasswordError()
    {
        passwordErrorBorder.Visible = true;
        confirmPasswordErrorBorder.Visible = true;
        errorTimer.Stop();
        errorTimer.Start();
    }

    private void InitializeRevealIcon()
    {
        _showIcon = VectorIcon.Render(ShowIconPath, revealIcon.Width, SystemColors.GrayText);
        _hideIcon = VectorIcon.Render(HideIconPath, revealIcon.Width, SystemColors.GrayText);
        revealIcon.Image = _showIcon;
        revealIcon.BringToFront();
        passwordBox.SizeChanged += (_, _) => UpdatePasswordTextInset();
    }

    private void RevealIcon_Click(object? sender, EventArgs e)
    {
        ClearPasswordError();
        _passwordVisible = !_passwordVisible;

        if (_passwordVisible)
        {
            passwordBox.PasswordChar = '\0';
            confirmPasswordBox.Clear();
            confirmPasswordBox.Enabled = false;
            revealIcon.Image = _hideIcon;
            eyeToolTip.SetToolTip(revealIcon, "Hide password");
        }
        else
        {
            passwordBox.PasswordChar = '●';
            confirmPasswordBox.Clear();
            confirmPasswordBox.Enabled = true;
            revealIcon.Image = _showIcon;
            eyeToolTip.SetToolTip(revealIcon, "Show password");
        }

        passwordBox.Focus();
        passwordBox.SelectionStart = passwordBox.Text.Length;
    }

    // Keeps typed text from running underneath the overlaid icon, without disturbing the
    // control's default top/bottom/left text margins (only the right edge is pulled in).
    private void UpdatePasswordTextInset()
    {
        if (!passwordBox.IsHandleCreated)
        {
            return;
        }

        var rect = default(NativeRect);
        NativeMethods.SendMessage(passwordBox.Handle, EmGetRect, IntPtr.Zero, ref rect);
        rect.Right = passwordBox.ClientSize.Width - revealIcon.Width - 6;
        NativeMethods.SendMessage(passwordBox.Handle, EmSetRect, IntPtr.Zero, ref rect);
    }

    // Focuses the field without selecting its text, so the selection highlight doesn't
    // sit on top of (and color-clash with) the red error border underneath it.
    private static void FocusWithoutSelection(TextBox box)
    {
        box.Focus();
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
    }

    private void StartButton_Click(object? sender, EventArgs e)
    {
        var usernameResult = UsernameValidator.Validate(usernameBox.Text, _logger);

        if (!usernameResult.Valid)
        {
            Dialogs.Show(usernameResult.Error!, icon: MessageBoxIcon.Warning);
            FocusWithoutSelection(usernameBox);
            return;
        }

        if (!_passwordVisible)
        {
            var passwordsMatch = string.Equals(passwordBox.Text, confirmPasswordBox.Text, StringComparison.Ordinal);

            if (!passwordsMatch)
            {
                ShowPasswordError();
                // Neither password field keeps focus here: a focused TextBox draws its own
                // blue accent along the bottom edge, which clashes with the red error border.
                ActiveControl = null;
                return;
            }
        }

        if (languageCombo.SelectedIndex < 0)
        {
            Dialogs.Show("No Windows display language is available.", icon: MessageBoxIcon.Warning);
            return;
        }

        var selectedLanguage = (LanguageItem)languageCombo.SelectedItem!;

        Selection = new SetupSelection(usernameResult.Username, passwordBox.Text, selectedLanguage.Tag);

        startButton.Enabled = false;
        cancelButton.Enabled = false;
        startButton.Text = "Preparing...";
        DialogResult = DialogResult.OK;
        Close();
    }
}
