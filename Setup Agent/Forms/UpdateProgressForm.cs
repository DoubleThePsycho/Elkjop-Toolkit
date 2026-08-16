using Setup_Agent.Services;

namespace Setup_Agent.Forms;

// Stays visible for the whole search/download/install loop so there's always on-screen
// evidence something is happening - the actual work runs on a background thread (WUA's
// Search/Download/Install calls are synchronous/blocking) while this form's own message loop
// keeps the marquee bar animating and the status text updating via SetStatus.
//
// Shows the actual pending updates in a Name/State/Progress table and a "last checked"
// timestamp, not just a phase label - relying on the native ms-settings:windowsupdate page for
// real detail turned out unreliable in real-machine testing (it doesn't dependably refresh, and
// opening it on every relaunch across the reboot cycle just piled up duplicate windows), so that
// call was removed entirely and this is now the only source of feedback, not a supplement.
//
// Progress column: Setup Agent drives Download()/Install() itself (see
// WindowsUpdateExecutor.cs), subscribed to WUA's real progress-changed callbacks, so a genuine
// per-update percentage is available now. It's phase-aware, not one continuous number - the
// State column shows which phase an item is in (Downloading/Downloaded/Installing/Installed/
// etc.), so an item finishing a download at 100% and then starting to install shows as
// "Installing 0%" rather than looking like it regressed from a prior 100%. Unstarted items show
// "-", never a misleading "0%".
//
// heartbeatTimer refreshes "Last checked" every 7s regardless of whether a real WUA callback or
// search has happened recently - a UI-only proof-of-life so the window doesn't look frozen
// during quiet stretches (e.g. between search passes). Deliberately does not touch Windows
// Update itself (no extra Search() calls) - a real search has been observed taking 84s on this
// solution's own dev machine, and running one concurrently with an in-flight Download()/
// Install() COM operation is untested; this stays UI-only to avoid that risk entirely.
//
// Layout lives in UpdateProgressForm.Designer.cs, editable in the VS Designer - see
// EthernetPromptForm.cs for the AutoScaleDimensions/devenv.exe DPI note that applies here too.
internal sealed partial class UpdateProgressForm : Form
{
    private bool _allowClose;

    // Read from the background worker thread (see Program.cs's SyncSleepPrevention) without a
    // lock - a plain bool read/write is already atomic, and this exact pattern (a flag set on
    // the UI thread via a checkbox/event, read from the worker thread) is what _allowClose right
    // below already does. Pre-checked by default, matching the previous unconditional behavior.
    public bool PreventSleepEnabled { get; private set; } = true;

    public UpdateProgressForm()
    {
        InitializeComponent();

        // Not Designer-serializable (computed at runtime from the exe's own embedded icon,
        // see AppInfo.AppIcon) - assigned here rather than in InitializeComponent.
        Icon = AppInfo.AppIcon;

        // ControlBox = false was the previous way to keep this from being closed early
        // (background work is still running on a separate thread; closing this form ends
        // Application.Run and the whole process, killing that background thread with it) - but
        // ControlBox = false also removes the title-bar icon slot entirely as a side effect.
        // Cancelling FormClosing unless it's our own CloseFromBackground call achieves the same
        // "can't be closed early" result without hiding the icon.
        FormClosing += (_, e) =>
        {
            if (!_allowClose)
            {
                e.Cancel = true;
            }
        };

        heartbeatTimer.Start();
    }

    public void SetStatus(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(text));
            return;
        }

        statusLabel.Text = text;
        RefreshLastCheckedTimestamp();
    }

    private void RefreshLastCheckedTimestamp()
    {
        lastCheckedLabel.Text = $"Last checked: {DateTime.Now:HH:mm:ss}";
    }

    private void HeartbeatTimer_Tick(object? sender, EventArgs e) => RefreshLastCheckedTimestamp();

    public void SetPendingUpdates(IReadOnlyList<UpdateStatusEntry> updates)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetPendingUpdates(updates));
            return;
        }

        updatesGrid.Rows.Clear();
        foreach (var update in updates)
        {
            updatesGrid.Rows.Add(update.Name, DescribePercent(update.PercentComplete), DescribeState(update.State));
        }
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

    private void PreventSleepCheckBox_CheckedChanged(object? sender, EventArgs e)
    {
        PreventSleepEnabled = preventSleepCheckBox.Checked;
    }

    public void CloseFromBackground()
    {
        if (InvokeRequired)
        {
            BeginInvoke(CloseFromBackground);
            return;
        }

        _allowClose = true;
        Close();
    }
}
