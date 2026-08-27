using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Setup_Agent.Services;

namespace Setup_Agent.Views;

internal enum InternetAccessState
{
    WaitingForConnection,
    ConnectionEstablished,
}

// Merged two-state window (Waiting-for-Ethernet / Connection-established) - one class with two
// panels toggled by visibility, since they're two views on the same underlying moment (getting
// online before Windows Update starts). WPF replacement for the old WinForms InternetAccessForm
// (2026-08-16 conversion), restyled to the shared Elkjøp dark theme.
//
// The Waiting panel has no buttons at all, deliberately - skipping it would defeat the point of
// blocking until the machine is actually online, so Window_Closing cancels any close attempt
// (Alt+F4 included) while it's showing. It transitions itself to the Established panel (with
// its own countdown/OK, both of which are allowed to close the window) the moment connectivity
// polling succeeds, rather than closing and making Program.cs open a second window.
internal sealed partial class InternetAccessWindow : Window
{
    private const int CountdownSeconds = 5;

    private readonly InternetAccessState _initialState;
    private readonly DispatcherTimer _connectivityPollTimer;
    private readonly DispatcherTimer _countdownTimer;
    private int _secondsRemaining = CountdownSeconds;

    public InternetAccessWindow(InternetAccessState initialState)
    {
        InitializeComponent();
        _initialState = initialState;

        _connectivityPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _connectivityPollTimer.Tick += ConnectivityPollTimer_Tick;

        _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdownTimer.Tick += CountdownTimer_Tick;

        if (initialState == InternetAccessState.ConnectionEstablished)
        {
            TransitionToEstablished();
        }
    }

    // Cannot be closed while the Waiting panel is showing, by any means (Alt+F4 included) -
    // once the Established panel is showing, its own OK button/countdown are legitimate ways
    // to close, so nothing further needs cancelling at that point.
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (waitingPanel.Visibility == Visibility.Visible)
        {
            e.Cancel = true;
        }
    }

    private void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_initialState == InternetAccessState.WaitingForConnection)
        {
            _connectivityPollTimer.Start();
        }
        else
        {
            _countdownTimer.Start();
        }
    }

    private void ConnectivityPollTimer_Tick(object? sender, EventArgs e)
    {
        if (ConnectivityService.HasInternet())
        {
            _connectivityPollTimer.Stop();
            TransitionToEstablished();
            _countdownTimer.Start();
        }
    }

    private void TransitionToEstablished()
    {
        waitingPanel.Visibility = Visibility.Collapsed;
        establishedPanel.Visibility = Visibility.Visible;
        _secondsRemaining = CountdownSeconds;
        UpdateEstablishedMessage();
    }

    private void UpdateEstablishedMessage()
    {
        establishedBodyText.Text = $"Press Cancel within {_secondsRemaining} to abort.";
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        _secondsRemaining--;

        if (_secondsRemaining <= 0)
        {
            _countdownTimer.Stop();
            DialogResult = true; // auto-closes; the Closing guard permits it (waiting panel hidden)
            return;
        }

        UpdateEstablishedMessage();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        _countdownTimer.Stop();
        DialogResult = true;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
