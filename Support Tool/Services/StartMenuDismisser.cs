using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Support_Tool.Services;

// Windows 11 automatically opens the Start menu once at a new local account's first
// interactive logon (a Microsoft onboarding nudge) - never again on later logons for that same
// account. Ported verbatim from Setup Agent (mechanism unchanged); Phase 2B wires the trigger
// condition - App.xaml.cs runs this only when
// SessionState.Workflow == ToolkitWorkflow.Setup && !SessionState.FirstDesktopActionsCompleted,
// then persists the flag once WatchAndDismissAsync's returned Task completes.
//
// Checks a set of candidate process names covering different Windows 11 shell-surface
// implementations (confirmed via real-machine testing that a single guess wasn't reliable) and
// logs every distinct foreground-window transition during the watch window for diagnosability.
internal static class StartMenuDismisser
{
    private const byte VkEscape = 0x1B;
    private const uint KeyEventFKeyUp = 0x0002;

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

    // Fire-and-forget from the caller's perspective at the OS level (the watch itself still runs
    // on its own background thread, exactly as before) - but returns a Task that completes once
    // the watch loop actually finishes (dismissed, or the window elapsed), so App startup can
    // await it before persisting SessionState.FirstDesktopActionsCompleted, without blocking
    // window display on the wait itself (the caller awaits this from a fire-and-forget async
    // method, not from OnStartup directly).
    public static Task WatchAndDismissAsync(TimeSpan window, Logger logger)
    {
        var tcs = new TaskCompletionSource();

        new Thread(() =>
        {
            try
            {
                WatchLoop(window, logger);
            }
            finally
            {
                tcs.TrySetResult();
            }
        })
        { IsBackground = true }.Start();

        return tcs.Task;
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
