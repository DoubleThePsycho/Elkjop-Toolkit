using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;

namespace Support_Tool.Views;

// A small modal dialog shown by WindowsUpdateLifecycleCoordinator once session persistence and
// resume-task creation/verification have both already succeeded (see its own remarks - this
// window is never shown before that, and cancelling it never undoes it). ShowDialog() blocks the
// caller (safe here: the coordinator is already running on the UI thread via the awaited
// continuation chain from WindowsUpdatePageViewModel.StartAsync, and ShowDialog() pumps its own
// nested Dispatcher frame rather than blocking on a Task - not the same sync-over-async shape
// that caused the App.xaml.cs startup deadlock).
//
// DialogResult is true only if the countdown genuinely expired - every other way of leaving this
// window (Cancel, the titlebar close button, Alt+F4) leaves DialogResult false or null, and the
// caller treats "not exactly true" as "do not reboot", so an ambiguous close can never be
// misread as consent to reboot.
public partial class RebootCountdownWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _secondsRemaining;

    public RebootCountdownWindow(int seconds = 5)
    {
        InitializeComponent();

        _secondsRemaining = seconds;
        UpdateCountdownText();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _secondsRemaining--;

        if (_secondsRemaining <= 0)
        {
            _timer.Stop();
            DialogResult = true;
            Close();
            return;
        }

        UpdateCountdownText();
    }

    private void UpdateCountdownText() =>
        CountdownText.Text = $"Restarting in {_secondsRemaining} second{(_secondsRemaining == 1 ? "" : "s")}…";

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Always stop the timer on the way out, regardless of how the window is closing (Cancel,
        // the titlebar button, or an external Close()/Alt+F4) - a tick firing after Close() has
        // already been called would otherwise try to set DialogResult a second time and throw.
        _timer.Stop();
        base.OnClosing(e);
    }
}
