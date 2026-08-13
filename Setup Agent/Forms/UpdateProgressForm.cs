using Setup_Agent.Services;

namespace Setup_Agent.Forms;

// Stays visible for the whole search/download/install loop so there's always on-screen
// evidence something is happening - the actual work runs on a background thread (WUA's
// Search/Download/Install calls are synchronous/blocking) while this form's own message loop
// keeps the marquee bar animating and the status text updating via SetStatus.
//
// Shows the actual pending updates in a Name/State table and a "last checked" timestamp, not
// just a phase label - relying on the native ms-settings:windowsupdate page for real detail
// turned out unreliable in real-machine testing (it doesn't dependably refresh, and opening it
// on every relaunch across the reboot cycle just piled up duplicate windows), so that call was
// removed entirely and this is now the only source of feedback, not a supplement.
//
// No Percentage column: real per-update download percentage only comes from WUA's progress-
// changed callbacks tied to a download/install operation Setup Agent itself initiates, and
// Setup Agent deliberately no longer drives installs itself (see WindowsUpdateService.cs) - it
// has no handle into whatever Windows' own Automatic Updates client is doing internally, so
// there's no real percentage available to show.
//
// Layout lives in UpdateProgressForm.Designer.cs, editable in the VS Designer - see
// EthernetPromptForm.cs for the AutoScaleDimensions/devenv.exe DPI note that applies here too.
internal sealed partial class UpdateProgressForm : Form
{
    private bool _allowClose;

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
    }

    public void SetStatus(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(text));
            return;
        }

        statusLabel.Text = text;
        lastCheckedLabel.Text = $"Last checked: {DateTime.Now:HH:mm:ss}";
    }

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
            updatesGrid.Rows.Add(update.Name, DescribeState(update.State));
        }
    }

    private static string DescribeState(UpdateState state) => state switch
    {
        UpdateState.Failed => "Failed",
        UpdateState.PendingRestart => "Pending restart",
        _ => "Pending",
    };

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
