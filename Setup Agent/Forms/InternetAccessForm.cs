using Setup_Agent.Services;

namespace Setup_Agent.Forms;

internal enum InternetAccessState
{
    WaitingForConnection,
    ConnectionEstablished,
}

// Merged from two previously separate forms (EthernetPromptForm, ConnectionEstablishedForm) -
// they were always two views on the same underlying moment (getting online before Windows
// Update starts), just with different content, so one class with two panels toggled by
// visibility is simpler than two classes. Layout lives in InternetAccessForm.Designer.cs.
//
// Waiting panel has no buttons at all, deliberately - skipping it would defeat the point of
// blocking until the machine is actually online, so FormClosing cancels any close attempt
// while it's showing (Alt+F4 included). It transitions itself to the Established panel (with
// its own countdown/OK, both of which are allowed to close the form) the moment connectivity
// polling succeeds, rather than closing and making Program.cs open a second window.
//
// Each panel uses a bold blue headline + regular body text next to a left-side icon, matching
// the classic Windows system dialog style (e.g. the "Low Battery" warning) rather than a
// generic centered MessageBox - see SetupCompleteForm.cs, which uses the same pattern.
//
// See UpdateProgressForm.cs for the AutoScaleDimensions/devenv.exe DPI note that applies here
// too.
internal sealed partial class InternetAccessForm : Form
{
    private const int CountdownSeconds = 5;

    private int _secondsRemaining = CountdownSeconds;

    // Parameterless constructor exists for VS Designer support (it needs one to host a live
    // preview) - defaults to the Waiting state, matching which panel InitializeComponent
    // leaves visible. Real usage always goes through the state-taking constructor below.
    public InternetAccessForm() : this(InternetAccessState.WaitingForConnection)
    {
    }

    public InternetAccessForm(InternetAccessState initialState)
    {
        InitializeComponent();

        // Not Designer-serializable (computed at runtime from the exe's own embedded icon,
        // see AppInfo.AppIcon) - assigned here rather than in InitializeComponent.
        Icon = AppInfo.AppIcon;

        // Cannot be closed while the Waiting panel is showing, by any means (Alt+F4 included) -
        // once the Established panel is showing, its own OK button/countdown are legitimate
        // ways to close, so nothing further needs to be cancelled at that point.
        FormClosing += (_, e) =>
        {
            if (waitingPanel.Visible)
            {
                e.Cancel = true;
            }
        };

        Shown += (_, _) =>
        {
            if (initialState == InternetAccessState.WaitingForConnection)
            {
                connectivityPollTimer.Start();
            }
            else
            {
                countdownTimer.Start();
            }
        };

        if (initialState == InternetAccessState.ConnectionEstablished)
        {
            TransitionToEstablished();
        }
    }

    private void ConnectivityPollTimer_Tick(object? sender, EventArgs e)
    {
        if (ConnectivityService.HasInternet())
        {
            connectivityPollTimer.Stop();
            TransitionToEstablished();
            countdownTimer.Start();
        }
    }

    private void TransitionToEstablished()
    {
        waitingPanel.Visible = false;
        establishedContentPanel.Visible = true;
        _secondsRemaining = CountdownSeconds;
        UpdateEstablishedMessage();
    }

    private void UpdateEstablishedMessage()
    {
        // mainInstructionLabel is static (set once, matches the Designer's placeholder
        // text already) - only the body's countdown line changes per tick.
        establishedBodyLabel.Text = $"Press Cancel within {_secondsRemaining} to abort.";
    }

    private void CountdownTimer_Tick(object? sender, EventArgs e)
    {
        _secondsRemaining--;

        if (_secondsRemaining <= 0)
        {
            countdownTimer.Stop();
            DialogResult = DialogResult.OK;
            Close();
            return;
        }

        UpdateEstablishedMessage();
    }
}
