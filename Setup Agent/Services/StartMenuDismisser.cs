using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Setup_Agent.Services;

// Windows 11 automatically opens the Start menu once at a new local account's first
// interactive logon (a Microsoft onboarding nudge) - never again on later logons for that same
// account. A single Escape keypress sent right at Setup Agent startup turned out too early in
// real testing (the Start menu doesn't necessarily auto-open the instant the desktop
// initializes), and even after fixing that timing and extending the window, real-machine
// testing still showed it not closing - meaning the actual problem is more likely the
// StartMenuExperienceHost.exe process-name guess itself being wrong for this Windows build,
// not timing. Rather than guess a third name blindly, this now (a) checks a wider set of
// candidate names covering different Windows 11 shell-surface implementations, and (b) logs
// every distinct foreground-window transition (process name + title) during the watch window,
// so the next real test's log will show conclusively what Windows actually reports as
// foreground when the Start menu is open - real data instead of another guess.
internal static class StartMenuDismisser
{
    private const byte VkEscape = 0x1B;
    private const uint KeyEventFKeyUp = 0x0002;

    // Broadened from a single guess (StartMenuExperienceHost) after that alone didn't work in
    // real testing - covers several shell-surface hosts across Windows 10/11 builds, since
    // which one actually owns the Start menu isn't confirmed for the target machine's build.
    private static readonly string[] CandidateProcessNames =
    [
        "StartMenuExperienceHost",
        "ShellExperienceHost",
        "SearchHost",
    ];

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    public static void WatchAndDismiss(TimeSpan window, Logger logger)
    {
        new Thread(() => WatchLoop(window, logger)) { IsBackground = true }.Start();
    }

    private static void WatchLoop(TimeSpan window, Logger logger)
    {
        var deadline = DateTime.UtcNow.Add(window);
        string? lastLoggedProcessName = null;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);

            var (processName, title) = GetForegroundWindowInfo();

            // Only log on change, not every 500ms - keeps the log readable while still giving
            // a full timeline of what had foreground focus during the watch window.
            if (!string.Equals(processName, lastLoggedProcessName, StringComparison.OrdinalIgnoreCase))
            {
                logger.Info($"Foreground window is now '{processName}' (title: \"{title}\").");
                lastLoggedProcessName = processName;
            }

            if (processName is not null && CandidateProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    keybd_event(VkEscape, 0, 0, UIntPtr.Zero);
                    keybd_event(VkEscape, 0, KeyEventFKeyUp, UIntPtr.Zero);
                    logger.Info($"Matched candidate Start menu process '{processName}' and sent Escape to dismiss it.");
                }
                catch (Exception ex)
                {
                    logger.Warn($"Found a candidate Start menu window open but failed to send Escape: {ex.Message}");
                }

                return;
            }
        }

        logger.Info($"No candidate Start menu process was observed in the foreground during the {window.TotalSeconds:0}-second watch window.");
    }

    private static (string? ProcessName, string Title) GetForegroundWindowInfo()
    {
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                return (null, string.Empty);
            }

            var titleLength = GetWindowTextLength(hWnd);
            var title = string.Empty;
            if (titleLength > 0)
            {
                var sb = new StringBuilder(titleLength + 1);
                GetWindowText(hWnd, sb, sb.Capacity);
                title = sb.ToString();
            }

            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0)
            {
                return (null, title);
            }

            using var process = Process.GetProcessById((int)pid);
            return (process.ProcessName, title);
        }
        catch
        {
            // Process may have already exited between GetWindowThreadProcessId and
            // GetProcessById, or the handle may be otherwise inaccessible - treat as unknown
            // for this tick rather than crashing the watch loop.
            return (null, string.Empty);
        }
    }
}
