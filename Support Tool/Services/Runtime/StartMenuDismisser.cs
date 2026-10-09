using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace Support_Tool.Services;

// Windows 11 automatically opens the Start menu once at a new local account's first
// interactive logon (a Microsoft onboarding nudge) - never again on later logons for that same
// account. Ported verbatim from Setup Agent (mechanism unchanged); Phase 2B wires the trigger
// condition - App.xaml.cs runs this only when
// SessionState.Workflow == ToolkitWorkflow.Setup && !SessionState.FirstDesktopActionsCompleted,
// then persists the flag once WatchAndDismissAsync's returned Task completes.
//
// Real-hardware correction history (Phase 4B): the original implementation relied entirely on
// GetForegroundWindow() plus a small set of known shell-surface process names, on the theory that
// whatever is covering Support Tool must own the Win32 foreground window. Two real OOBE runs
// disproved that: the immersive Start showcase can visibly cover Support Tool for the ENTIRE
// watch window while GetForegroundWindow() keeps reporting Support Tool's own process (or the
// desktop) as foreground the whole time - it never becomes the foreground window in the
// traditional sense. Detection is now independent of foreground ownership entirely: every tick,
// EnumWindows scans ALL visible top-level windows (not just the foreground one) for a window
// belonging to one of the known shell-surface processes, regardless of who currently owns
// GetForegroundWindow(). The narrow (explorer, "Program Manager") foreground fallback from the
// earlier fix remains as an ADDITIONAL signal (covers a build where the overlay isn't a distinct
// top-level window at all), never the only one.
//
// External-review correction: IsWindowVisible() alone (WS_VISIBLE) is not enough on modern
// Windows - a UWP/immersive shell surface can keep a top-level window WS_VISIBLE while DWM has it
// cloaked (not actually composited/presented to the user), e.g. a dormant/off-screen
// StartMenuExperienceHost window left behind between showings. Acting on a cloaked candidate would
// make the watcher fire Escape/re-activation every single tick for the whole 60-second window
// against a window nobody can actually see - hostile to use. Every candidate is now also checked
// via DwmGetWindowAttribute(DWMWA_CLOAKED): only a candidate that is BOTH WS_VISIBLE AND NOT
// DWM-cloaked is treated as actionable (triggers Escape/dismissal/re-activation). A cloaked
// candidate is still captured for diagnostics (so a future miss can be told apart from "nothing
// was there at all"), just never acted on. If the cloak state itself can't be determined, the
// candidate is conservatively treated as not actionable (same as cloaked) rather than guessing.
internal static class StartMenuDismisser
{
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

    // IsActionable is false for a candidate that is WS_VISIBLE but DWM-cloaked (or whose cloak
    // state couldn't be determined) - it's still captured here for diagnostics, but the watch
    // loop must never send Escape/PostMessage/re-activate based on it.
    private readonly record struct ShellCandidateWindow(IntPtr Hwnd, uint ProcessId, string ProcessName, string Title, string ClassName, bool IsActionable);

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
        string? lastLoggedSignature = null;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(500);

            // Includes cloaked/unknown-cloak-state candidates for diagnostics - only the
            // actionable subset (WS_VISIBLE AND not DWM-cloaked) may ever trigger dismissal.
            var candidates = EnumerateVisibleShellCandidates();
            var actionableCandidates = candidates.Where(c => c.IsActionable).ToList();
            var (fgProcessName, fgTitle) = GetForegroundWindowInfo();
            var isDesktopFallback = string.Equals(fgProcessName, DesktopFallbackProcessName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(fgTitle, DesktopFallbackWindowTitle, StringComparison.Ordinal);

            // Only log on a distinct change in what's visible, not every 500ms - keeps the log
            // readable while still giving a full timeline, with the HWND/PID/title/class/cloak
            // detail needed to diagnose a future miss, of what shell-candidate state existed
            // during the watch window.
            var signature = BuildSignature(candidates, fgProcessName, fgTitle);
            if (!string.Equals(signature, lastLoggedSignature, StringComparison.Ordinal))
            {
                logger.Info($"Shell-candidate scan: {DescribeSignature(candidates, fgProcessName, fgTitle)}");
                lastLoggedSignature = signature;
            }

            if (actionableCandidates.Count == 0 && !isDesktopFallback)
            {
                continue;
            }

            var detected = actionableCandidates.Count > 0
                ? $"{actionableCandidates.Count} actionable shell-candidate window(s) ({DescribeCandidates(actionableCandidates)})"
                : $"the desktop ('{fgProcessName}', title \"{fgTitle}\") - possibly a Start overlay that isn't itself taking the Win32 foreground";
            logger.Info($"Detected {detected}. Dismissing and re-activating Support Tool.");

            SendEscapeViaSendInput(logger);

            // Best-effort secondary nudge directly at the candidate window(s) - harmless if
            // ignored (posting a key message never closes/kills the target), on top of the real
            // SendInput keystroke above which is what actually reaches the input queue. Only the
            // actionable (uncloaked) candidates - never a cloaked/not-actually-presented window.
            foreach (var candidate in actionableCandidates)
            {
                TryPostEscapeToWindow(candidate.Hwnd, logger);
            }

            TryActivateSupportTool(logger);

            // Deliberately does not return here - the watch keeps running for the full bounded
            // window regardless of whether this attempt worked, both because a single
            // Escape/re-activate isn't provable success (activation can be denied by Windows'
            // foreground-lock rules) and because the showcase could still reopen again later in
            // the same first-logon window.
        }

        logger.Info($"Watch window ({window.TotalSeconds:0}s) elapsed.");
    }

    private static string BuildSignature(IReadOnlyList<ShellCandidateWindow> candidates, string? fgProcessName, string fgTitle) =>
        string.Join('|', candidates.Select(c => $"{c.Hwnd:X}:{c.ProcessName}:{c.IsActionable}")) + "||" + fgProcessName + "||" + fgTitle;

    private static string DescribeSignature(IReadOnlyList<ShellCandidateWindow> candidates, string? fgProcessName, string fgTitle) =>
        candidates.Count > 0
            ? DescribeCandidates(candidates) + $"; foreground='{fgProcessName}' title=\"{fgTitle}\""
            : $"no visible shell-candidate windows; foreground='{fgProcessName}' title=\"{fgTitle}\"";

    private static string DescribeCandidates(IReadOnlyList<ShellCandidateWindow> candidates) =>
        string.Join(", ", candidates.Select(c =>
            $"{c.ProcessName} (pid={c.ProcessId}, hwnd=0x{c.Hwnd:X}, class=\"{c.ClassName}\", title=\"{c.Title}\", actionable={c.IsActionable})"));

    // Independent of GetForegroundWindow() entirely - walks every visible top-level window on the
    // desktop and keeps the ones belonging to a known shell-surface process, regardless of which
    // window currently owns foreground. This is what lets the watcher catch a Start showcase that
    // covers Support Tool without ever taking the Win32 foreground away from it. Every candidate
    // is also checked for DWM cloaking - see IsActionable's own remarks - so a dormant/off-screen
    // shell window doesn't fire dismissal every tick for the whole watch window.
    private static List<ShellCandidateWindow> EnumerateVisibleShellCandidates()
    {
        var results = new List<ShellCandidateWindow>();

        NativeMethods.EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hWnd))
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
                if (pid == 0)
                {
                    return true;
                }

                string processName;
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    processName = process.ProcessName;
                }
                catch
                {
                    // Process may have exited between GetWindowThreadProcessId and
                    // GetProcessById - skip this window rather than crashing the enumeration.
                    return true;
                }

                if (!CandidateProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase))
                {
                    return true;
                }

                // Not "out _" - the enclosing EnumWindows lambda's own second parameter is
                // already named "_" (its lParam, type IntPtr), so a plain discard here would
                // resolve to that existing IntPtr binding instead of introducing a new one.
                var isActionable = !IsWindowCloaked(hWnd, out int? _cloakedValue);
                results.Add(new ShellCandidateWindow(hWnd, pid, processName, GetWindowTitle(hWnd), GetWindowClassName(hWnd), isActionable));
            }
            catch
            {
                // Never let a single misbehaving window abort the whole enumeration.
            }

            return true;
        }, IntPtr.Zero);

        return results;
    }

    // Conservative by design: returns true (treat as cloaked / not actionable) both when DWM
    // genuinely reports the window cloaked AND when the cloak state can't be determined at all -
    // acting on a window whose actual presentation state is unknown is exactly the risk this
    // check exists to avoid. cloakedValue carries the raw DWMWA_CLOAKED value for logging when the
    // query succeeds (0 = not cloaked).
    private static bool IsWindowCloaked(IntPtr hWnd, out int? cloakedValue)
    {
        try
        {
            var hr = NativeMethods.DwmGetWindowAttribute(hWnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int));
            if (hr != 0)
            {
                // Query failed (HRESULT != S_OK) - unknown state, treat conservatively.
                cloakedValue = null;
                return true;
            }

            cloakedValue = cloaked;
            return cloaked != 0;
        }
        catch
        {
            cloakedValue = null;
            return true;
        }
    }

    private static void SendEscapeViaSendInput(Logger logger)
    {
        try
        {
            var inputs = new NativeMethods.INPUT[2];
            inputs[0] = NativeMethods.MakeKeyInput(NativeMethods.VK_ESCAPE, keyUp: false);
            inputs[1] = NativeMethods.MakeKeyInput(NativeMethods.VK_ESCAPE, keyUp: true);

            var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
            if (sent != inputs.Length)
            {
                // SendInput sets the last Win32 error specifically when it returns fewer events
                // than requested (SetLastError=true above) - capturing it immediately, before any
                // other call can overwrite it, is what makes a future real-OOBE failure
                // diagnosable instead of just "0/2, no idea why".
                var errorCode = Marshal.GetLastWin32Error();
                var errorMessage = new System.ComponentModel.Win32Exception(errorCode).Message;
                logger.Warn($"SendInput reported only {sent}/{inputs.Length} Escape event(s) queued (Win32 error {errorCode}: {errorMessage}).");
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to send Escape via SendInput: {ex.Message}");
        }
    }

    private static void TryPostEscapeToWindow(IntPtr hwnd, Logger logger)
    {
        try
        {
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_KEYDOWN, (IntPtr)NativeMethods.VK_ESCAPE, IntPtr.Zero);
            NativeMethods.PostMessage(hwnd, NativeMethods.WM_KEYUP, (IntPtr)NativeMethods.VK_ESCAPE, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to post Escape to candidate window 0x{hwnd:X}: {ex.Message}");
        }
    }

    // Beyond plain WPF Window.Activate() (which real OOBE testing showed isn't always enough
    // against an immersive overlay holding attention) - restores from minimized, brings the
    // native window to the top of the Z-order, and asks Windows for foreground activation. If
    // that's refused by Windows' foreground-lock rules, makes one bounded AttachThreadInput
    // attempt (temporarily borrowing the current foreground thread's input state, exactly the
    // documented way around the lock) and always detaches again in a finally - never leaves the
    // thread-input attachment lingering, never retries more than once, and never makes the window
    // permanently Topmost.
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

                var hwnd = new WindowInteropHelper(win).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                    NativeMethods.BringWindowToTop(hwnd);

                    if (!NativeMethods.SetForegroundWindow(hwnd))
                    {
                        TryForceForegroundViaAttachThreadInput(hwnd, logger);
                    }
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

    private static void TryForceForegroundViaAttachThreadInput(IntPtr targetHwnd, Logger logger)
    {
        var foregroundHwnd = NativeMethods.GetForegroundWindow();
        if (foregroundHwnd == IntPtr.Zero || foregroundHwnd == targetHwnd)
        {
            return;
        }

        var foregroundThreadId = NativeMethods.GetWindowThreadProcessId(foregroundHwnd, out _);
        var currentThreadId = NativeMethods.GetCurrentThreadId();
        if (foregroundThreadId == 0 || foregroundThreadId == currentThreadId)
        {
            return;
        }

        var attached = false;
        try
        {
            attached = NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, true);
            if (attached)
            {
                NativeMethods.SetForegroundWindow(targetHwnd);
                NativeMethods.BringWindowToTop(targetHwnd);
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"AttachThreadInput foreground-activation retry failed: {ex.Message}");
        }
        finally
        {
            if (attached)
            {
                NativeMethods.AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    private static (string? ProcessName, string Title) GetForegroundWindowInfo()
    {
        try
        {
            var hWnd = NativeMethods.GetForegroundWindow();
            if (hWnd == IntPtr.Zero)
            {
                return (null, string.Empty);
            }

            var title = GetWindowTitle(hWnd);

            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
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

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var titleLength = NativeMethods.GetWindowTextLength(hWnd);
        if (titleLength <= 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(titleLength + 1);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetWindowClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static class NativeMethods
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public const int SW_RESTORE = 9;
        public const ushort VK_ESCAPE = 0x1B;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;
        public const int DWMWA_CLOAKED = 14;
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        // The native Win32 INPUT struct is { DWORD type; union { MOUSEINPUT; KEYBDINPUT;
        // HARDWAREINPUT; } }. External review correctly caught that the previous managed
        // definition only declared KEYBDINPUT inside the union - since MOUSEINPUT is the union's
        // largest member (it carries an extra ULONG_PTR-sized field beyond what KEYBDINPUT has),
        // omitting it made the managed INPUT struct smaller than the real native one on every
        // architecture, so Marshal.SizeOf<INPUT>() computed the wrong cbSize and SendInput
        // rejected the whole call (observed as "0/2 Escape event(s) queued"). All three union
        // members are declared here with [FieldOffset(0)] so InputUnion's own size - and
        // therefore INPUT's - is driven by MOUSEINPUT (the largest), matching the native layout
        // exactly on x86/x64/ARM64 without hardcoding any architecture's byte count; the CLR's
        // own sequential-layout alignment rules for IntPtr-sized fields do the rest, identically
        // to how the native compiler pads the real struct.
        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        public struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        public static INPUT MakeKeyInput(ushort vk, bool keyUp) => new()
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                },
            },
        };

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        // Returns an HRESULT (0 = S_OK) rather than throwing/setting Win32 last-error - callers
        // must check the return value, not GetLastError().
        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();
    }
}
