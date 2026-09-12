using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;

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
//
// Real-hardware correction (first Phase 4B OOBE run): on the actual build tested, none of the
// known shell-surface processes were ever observed foreground - the log showed
// "Foreground window is now 'explorer' (title: \"Program Manager\")" for the whole watch window,
// while the Start showcase visibly covered Support Tool on screen. Some Windows 11 builds render
// this first-logon overlay as an immersive popup that never itself becomes the Win32 foreground
// window - GetForegroundWindow() keeps reporting the desktop (explorer's own "Program Manager"
// window) underneath it. Escape/re-activate is harmless when that combination is just an
// ordinary idle desktop with nothing covering Support Tool, so - scoped specifically to this
// bounded first-desktop watch, never globally - that exact (process, title) pair is now also
// treated as a candidate, and the loop keeps retrying (Escape + actively re-activating Support
// Tool) every tick until the watch window elapses, rather than returning after a single match.
//
// External-review correction: the loop must NOT return early just because Support Tool happens
// to be foreground on some early tick. Windows can (and did, on real hardware) open the Start
// showcase several seconds AFTER Support Tool first appears - the whole point of this bounded
// watch is to survive that delayed first-logon shell timing. Being foreground on a given tick
// only means nothing needs dismissing on THAT tick; it keeps watching regardless, all the way to
// the bounded deadline, in case the showcase opens moments later.
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

    // Deliberately exact and narrow - only the desktop's own shell window (Progman's title),
    // never any other explorer.exe window (e.g. a real file-browser window a technician might
    // have open), so this fallback can't misfire against legitimate foreground content.
    private const string DesktopFallbackProcessName = "explorer";
    private const string DesktopFallbackWindowTitle = "Program Manager";

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
        var ownProcessId = (uint)Environment.ProcessId;
        string? lastLoggedProcessName = null;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);

            var (processName, title, pid) = GetForegroundWindowInfo();

            // Only log on change, not every 500ms - keeps the log readable while still giving
            // a full timeline of what had foreground focus during the watch window.
            if (!string.Equals(processName, lastLoggedProcessName, StringComparison.OrdinalIgnoreCase))
            {
                logger.Info($"Foreground window is now '{processName}' (title: \"{title}\").");
                lastLoggedProcessName = processName;
            }

            // Nothing to dismiss on THIS tick - but this must not end the watch. Windows can (and
            // did, on real hardware) open the Start showcase several seconds after Support Tool
            // first appears, so the loop keeps watching all the way to the bounded deadline in
            // case that happens moments later - never send Escape or re-activate when we're
            // already foreground, but never treat it as "done" either.
            if (pid != 0 && pid == ownProcessId)
            {
                continue;
            }

            var isKnownShellSurface = processName is not null
                && CandidateProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase);
            var isDesktopFallback = string.Equals(processName, DesktopFallbackProcessName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(title, DesktopFallbackWindowTitle, StringComparison.Ordinal);

            if (!isKnownShellSurface && !isDesktopFallback)
            {
                continue;
            }

            var detected = isKnownShellSurface
                ? $"known Start/Shell surface '{processName}'"
                : $"the desktop ('{processName}', title \"{title}\") - possibly a Start overlay that isn't itself taking the Win32 foreground";
            logger.Info($"Detected {detected}. Sending Escape and re-activating Support Tool.");

            try
            {
                keybd_event(VkEscape, 0, 0, UIntPtr.Zero);
                keybd_event(VkEscape, 0, KeyEventFKeyUp, UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                logger.Warn($"Failed to send Escape: {ex.Message}");
            }

            TryActivateSupportTool(logger);

            // Deliberately does not return here - the watch keeps running for the full bounded
            // window regardless of whether this attempt worked, both because a single Escape/
            // re-activate isn't provable success (activation can be denied by Windows' foreground-
            // lock rules) and because the showcase could still reopen again later in the same
            // first-logon window.
        }

        logger.Info($"Watch window ({window.TotalSeconds:0}s) elapsed.");
    }

    private static void TryActivateSupportTool(Logger logger)
    {
        try
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var win = Application.Current?.MainWindow;
                if (win is null)
                {
                    return;
                }

                if (win.WindowState == WindowState.Minimized)
                {
                    win.WindowState = WindowState.Normal;
                }

                win.Activate();
            });
        }
        catch (Exception ex)
        {
            // Foreground activation can be denied by Windows (e.g. foreground-lock timeout) -
            // never let that crash the watch loop; just log and let the next tick retry.
            logger.Warn($"Failed to re-activate Support Tool's window: {ex.Message}");
        }
    }

    private static (string? ProcessName, string Title, uint ProcessId) GetForegroundWindowInfo()
    {
        try
        {
            var hWnd = GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                return (null, string.Empty, 0);
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
                return (null, title, 0);
            }

            using var process = Process.GetProcessById((int)pid);
            return (process.ProcessName, title, pid);
        }
        catch
        {
            // Process may have already exited between GetWindowThreadProcessId and
            // GetProcessById, or the handle may be otherwise inaccessible - treat as unknown
            // for this tick rather than crashing the watch loop.
            return (null, string.Empty, 0);
        }
    }
}
