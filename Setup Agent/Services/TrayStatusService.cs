namespace Setup_Agent.Services;

// A persistent system-tray icon spanning the whole Setup Agent run (created right after the
// elevation check, disposed as Main() exits) - so there's always some visible sign it's alive,
// even during phases with no dialog on screen (Wi-Fi retry, connectivity checks).
internal sealed class TrayStatusService : IDisposable
{
    // NotifyIcon.Text throws past 63 characters on some Windows Shell versions - keep the
    // prefix short and truncate defensively rather than risk an exception over a tooltip.
    private const int MaxTextLength = 63;

    // SetStatus is called both from Program.Main's own thread (before the update loop starts)
    // and from RunNativeUpdateCycle's background worker thread once it does - NotifyIcon isn't
    // documented as thread-safe, so this dummy Control exists purely to give it the same
    // InvokeRequired/BeginInvoke marshaling UpdateProgressForm already uses, without depending
    // on that form's lifetime (it doesn't exist yet during the earlier Wi-Fi/connectivity phases).
    private readonly Control _marshaller = new();
    private readonly NotifyIcon _icon;

    public TrayStatusService()
    {
        _marshaller.CreateControl();
        _icon = new NotifyIcon
        {
            Icon = AppInfo.AppIcon,
            Text = "Setup Agent: starting...",
            Visible = true,
        };
    }

    public void SetStatus(string text)
    {
        if (_marshaller.InvokeRequired)
        {
            _marshaller.BeginInvoke(() => SetStatus(text));
            return;
        }

        var full = $"Setup Agent: {text}";
        _icon.Text = full.Length > MaxTextLength ? full[..MaxTextLength] : full;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _marshaller.Dispose();
    }
}
