namespace Setup_Agent.Forms;

// Shown once Windows Update reports zero remaining updates and no reboot is pending - the
// actual terminal "done" state. Without this, the prior behavior set a "Finishing up..."
// status and closed the progress form almost immediately after - too fast to read in practice
// (confirmed by real-machine testing: no completion message was ever actually seen). Auto-
// dismisses after a short countdown rather than blocking indefinitely, since nobody may be
// present to click it by the time Setup Agent actually finishes - that could be well after
// the machine's been handed over.
//
// Layout lives in SetupCompleteForm.Designer.cs, editable in the VS Designer - see
// UpdateProgressForm.cs for the AutoScaleDimensions/devenv.exe DPI note that applies here too.
// Bold blue headline + regular body text next to a left-side icon, separator line above a
// bottom-right button - mirrors the classic Windows system dialog style (e.g. the "Low
// Battery" warning), not a generic centered MessageBox.
internal sealed partial class SetupCompleteForm : Form
{
    private const int CountdownSeconds = 10;

    private int _secondsRemaining = CountdownSeconds;

    public SetupCompleteForm()
    {
        InitializeComponent();

        // Not Designer-serializable (computed at runtime from the exe's own embedded icon,
        // see AppInfo.AppIcon) - assigned here rather than in InitializeComponent.
        Icon = AppInfo.AppIcon;

        // headlineLabel is static (set once, matches the Designer's placeholder text already);
        // messageLabel is just the countdown line, updated every tick.
        UpdateMessage();
        Shown += (_, _) => countdownTimer.Start();
    }

    private void UpdateMessage()
    {
        messageLabel.Text = $"Setup Agent is finishing up. Closing in {_secondsRemaining}...";
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

        UpdateMessage();
    }
}
