using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using Setup_Agent.Services;

namespace Setup_Agent.Views;

// Stays visible for the whole search/download/install loop so there's always on-screen evidence
// something is happening - the actual work runs on a background STA thread (WUA's COM calls are
// synchronous/blocking) while this window's dispatcher keeps the marquee bar animating and the
// status text updating via SetStatus.
//
// WPF replacement for the old WinForms UpdateProgressForm (2026-08-16 conversion), restyled to
// the shared Elkjøp dark theme. The public surface is preserved verbatim - Program.cs passes
// SetStatus/SetPendingUpdates as delegates into the WUA executor, whose COM progress callbacks
// invoke them from arbitrary threads, so every public method keeps the marshal-if-needed guard
// (Dispatcher.CheckAccess/BeginInvoke replacing WinForms' InvokeRequired/BeginInvoke).
//
// heartbeatTimer refreshes "Last checked" every 7s regardless of whether a real WUA callback or
// search has happened recently - a UI-only proof-of-life so the window doesn't look frozen
// during quiet stretches. Deliberately does not touch Windows Update itself (no extra Search()
// calls) - a real search has been observed taking 84s on this solution's own dev machine, and
// running one concurrently with an in-flight Download()/Install() COM operation is untested.
internal sealed partial class UpdateProgressWindow : Window
{
    // Grid row shape for the ListView - formatting happens in SetPendingUpdates so the cell
    // bindings stay plain {Binding} strings.
    private sealed record UpdateRow(string Name, string Percent, string State);

    private readonly DispatcherTimer _heartbeatTimer;
    private bool _allowClose;

    // Set on the UI thread by the checkbox handlers, read lock-free from the background worker
    // thread (Program.SyncSleepPrevention) - a plain bool read/write is atomic, same pattern
    // _allowClose uses. Pre-checked by default, matching the checkbox's initial state.
    public bool PreventSleepEnabled { get; private set; } = true;

    public UpdateProgressWindow()
    {
        InitializeComponent();

        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(7) };
        _heartbeatTimer.Tick += (_, _) => RefreshLastCheckedTimestamp();
        _heartbeatTimer.Start();
    }

    // Blocks closing (X and Alt+F4 alike) while background work is still running - closing this
    // window ends its ShowDialog frame and the whole update flow with it, so only
    // CloseFromBackground (the worker's finally block) may let a close through.
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    public void SetStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetStatus(text));
            return;
        }

        statusText.Text = text;
        RefreshLastCheckedTimestamp();
    }

    private void RefreshLastCheckedTimestamp()
    {
        lastCheckedText.Text = $"Last checked: {DateTime.Now:HH:mm:ss}";
    }

    public void SetPendingUpdates(IReadOnlyList<UpdateStatusEntry> updates)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetPendingUpdates(updates));
            return;
        }

        var rows = new List<UpdateRow>(updates.Count);
        foreach (var update in updates)
        {
            rows.Add(new UpdateRow(update.Name, DescribePercent(update.PercentComplete), DescribeState(update.State)));
        }

        updatesList.ItemsSource = rows; // full replacement, matching the old Rows.Clear()+Add
    }

    private static string DescribeState(UpdateState state) => state switch
    {
        UpdateState.Downloading => "Downloading",
        UpdateState.Downloaded => "Downloaded",
        UpdateState.Installing => "Installing",
        UpdateState.Installed => "Installed",
        UpdateState.Failed => "Failed",
        UpdateState.PendingRestart => "Pending restart",
        _ => "Pending",
    };

    private static string DescribePercent(int? percent) => percent is { } p ? $"{p}%" : "-";

    private void PreventSleepCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        PreventSleepEnabled = preventSleepCheckBox.IsChecked == true;
    }

    // GridView columns are fixed-width, so proportional sizing (the old DataGridView FillWeights
    // 55/15/20) is reapplied on every size change. The window is NoResize, so in practice this
    // runs once at layout.
    private void UpdatesList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var available = updatesList.ActualWidth - SystemParameters.VerticalScrollBarWidth - 14;
        if (available <= 0)
        {
            return;
        }

        nameColumn.Width = available * 0.61;    // 55 / 90
        percentColumn.Width = available * 0.22; // 20 / 90
        stateColumn.Width = available * 0.17;   // 15 / 90
    }

    public void CloseFromBackground()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(CloseFromBackground);
            return;
        }

        _allowClose = true;
        Close();
    }
}
