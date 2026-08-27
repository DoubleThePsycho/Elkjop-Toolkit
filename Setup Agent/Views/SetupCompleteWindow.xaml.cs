using System.Windows;
using System.Windows.Threading;

namespace Setup_Agent.Views;

// Shown once Windows Update reports zero remaining updates and no reboot is pending - the
// actual terminal "done" state. Auto-dismisses after a short countdown rather than blocking
// indefinitely, since nobody may be present to click it by the time Setup Agent actually
// finishes - that could be well after the machine's been handed over.
//
// WPF replacement for the old WinForms SetupCompleteForm (2026-08-16 conversion), restyled from
// the native-system-dialog look to the shared Elkjøp dark theme - a deliberate, user-confirmed
// retirement of the previous "classic Windows dialog" design.
internal sealed partial class SetupCompleteWindow : Window
{
    private const int CountdownSeconds = 10;

    private readonly DispatcherTimer _countdownTimer;
    private int _secondsRemaining = CountdownSeconds;

    public SetupCompleteWindow()
    {
        InitializeComponent();

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += CountdownTimer_Tick;
        UpdateMessage();
    }

    private void Window_ContentRendered(object? sender, EventArgs e) => _countdownTimer.Start();

    private void UpdateMessage()
    {
        messageText.Text = $"Setup Agent is finishing up. Closing in {_secondsRemaining}...";
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        _secondsRemaining--;

        if (_secondsRemaining <= 0)
        {
            _countdownTimer.Stop();
            DialogResult = true; // auto-closes a ShowDialog-shown window
            return;
        }

        UpdateMessage();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        _countdownTimer.Stop();
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
