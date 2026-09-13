# Phase 4B manual tests - live Account Setup cutover to Support Tool

Manual VM tests for Phase 4B - the live post-OOBE path switches from Setup Agent to the staged
Support Tool. Setup Agent remains buildable and its rollback assets (`Temp\SetupAgent.exe`,
`Temp\WifiCredentials.json`, `Temp\SetupAgentTask.xml`) are still staged during this testing
period, but nothing in the live Unattend path registers or runs them anymore. Run everything on a
disposable OOBE VM/snapshot.

## Test 1 - Prepare-only final artifacts

On a disposable OOBE VM, with a valid canonical `<USB root>\Elkjop Toolkit\config.json` present:

```
Account Setup-<arch>.exe --prepare-only
```

**Verify:**
* `C:\ProgramData\Elkjop\Toolkit\Runtime\` contains `SupportTool.exe`, `config.json`,
  `session.json`, `Unattend.xml`, `ResumeTask.xml`;
* `session.json` reads exactly `Workflow=Setup`, `Stage=WindowsUpdate`, `ResumeRequired=false`,
  `FirstDesktopActionsCompleted=false`;
* `Runtime\config.json` matches the canonical USB config content exactly;
* `Runtime\ResumeTask.xml`'s `<Command>` targets `Runtime\SupportTool.exe` (not
  `Temp\SetupAgent.exe`), and its `<UserId>` elements show the OOBE-selected username;
* `Runtime\Unattend.xml`'s `FirstLogonCommands` register and `/run` "Elkjop Toolkit Resume" (not
  "Elkjop Setup Agent"), then delete `Runtime\ResumeTask.xml`;
* `Temp\SetupAgent.exe`/`Temp\WifiCredentials.json` (if Wi-Fi configured)/`Temp\SetupAgentTask.xml`
  may also exist (rollback-only) - their presence is expected, not a failure;
* **no** scheduled task is actually registered yet (`schtasks /query /tn "Elkjop Toolkit Resume"`
  fails) - FirstLogonCommands hasn't run.

## Test 2 - Missing canonical config

Temporarily rename/remove `<USB root>\Elkjop Toolkit\config.json` (legacy per-tool configs may
remain), then run `Account Setup-<arch>.exe --prepare-only`.

**Expected:**
* a visible Account Setup failure dialog naming the missing canonical config;
* no `Runtime\` staging occurs at all (this check happens before the setup dialog even shows);
* no Sysprep;
* no silent fallback to either legacy per-tool config.

Also verify a corrupt (malformed JSON, or valid JSON missing e.g. the `Installer` section)
canonical config produces the same outcome.

## Test 2b - Runtime staging failure does NOT offer the account-bypass fallback

This is the fix from external review of `111dd19`: a final Runtime staging failure must leave
Windows in OOBE, never offer the legacy account-bypass fallback (which can itself set BypassNRO
and restart Windows), and never call Sysprep.

On a disposable OOBE VM, with a valid canonical config present, complete Account Setup's setup
dialog normally (so a username has actually been selected) but force `StageRuntime` to fail
afterward - any one of these is sufficient:
* temporarily rename/remove the architecture-correct `Support Tool-<arch>.exe` source;
* pre-stage a valid Service Runtime first (e.g. via `service.exe`) to force `WorkflowConflict`;
* pre-stage a resumable Setup session (`ResumeRequired=true`) to force `ResumeExisting`.

**Expected:**
* the failure dialog reads "Account Setup could not prepare the final Support Tool Runtime" (not
  the generic "Account Setup failed" message) and states Windows has been left in OOBE;
* the account-bypass Yes/No prompt ("Try the local-account fallback chain now?") is **never**
  shown;
* Sysprep is never invoked (no reboot, no BypassNRO registry change, no `shutdown /r`);
* the VM remains sitting in OOBE afterward, exactly where it was before running Account Setup.

Also verify the inverse still works: force an **actual Sysprep failure** (e.g. after successful
Runtime staging, with `--prepare-only` NOT set) - the original generic failure dialog and
account-bypass fallback prompt must still appear exactly as before this fix, since that path is
intentionally unchanged.

## Test 3 - Full real OOBE cutover

Using a snapshot/disposable VM:
1. Restore to a clean pre-OOBE state.
2. Run the normal technician `setup.exe`.
3. Complete Account Setup normally (real account selection, not `--prepare-only`).
4. Let Sysprep reboot.
5. Complete/allow Windows-controlled OOBE through to first desktop.

**Expected:**
* no standalone Setup Agent update window ever appears;
* Support Tool opens directly on first desktop;
* the Setup workflow goes straight to the embedded Windows Update page (no Home/Service
  detour);
* the first-desktop action (Start menu dismissal) runs once;
* `schtasks /query /tn "Elkjop Toolkit Resume"` succeeds while the mandatory Windows Update
  stage is incomplete;
* `schtasks /query /tn "Elkjop Setup Agent"` fails (task does not exist) at every checkpoint.

## Test 3b - Regression: Support Tool must actually show a window at first logon

**Background:** the first real Phase 4B OOBE run hit a sync-over-async deadlock in
`Support Tool/App.xaml.cs`'s `OnStartup` - it blocked the WPF UI thread on
`sessionService.LoadAsync().GetAwaiter().GetResult()` (and the equivalent `configService` call)
while already running under WPF's own `DispatcherSynchronizationContext`, so the awaited
continuation could never resume on the very thread sitting there blocked waiting for it. Fixed by
making `OnStartup` `async void` and properly `await`-ing both loads instead. This test exists
specifically to catch a regression back to that failure mode.

**Failure mode observed (pre-fix), for reference:**
* `Elkjop Toolkit Resume` scheduled task registers and shows `Running` in Task Scheduler;
* `SupportTool.exe` is alive in the interactive session (`Get-Process` shows it, `Responding =
  True`);
* but `MainWindowHandle = 0` and `MainWindowTitle` is empty;
* no Support Tool window ever appears on screen;
* the technician sees an empty desktop with no way to proceed.

**Test:** repeat Test 3 (full real OOBE cutover) through first desktop logon. Immediately after
the task launches (don't wait), check from an elevated PowerShell/cmd prompt on the VM:

```powershell
Get-Process -Name SupportTool* | Select-Object Id, SessionId, Responding, MainWindowHandle, MainWindowTitle
```

**Expected (fixed behavior):**
* the task launches the staged `Runtime\SupportTool.exe`;
* startup's session/config load completes (no indefinite hang);
* `MainWindow.Show()` is reached - `MainWindowHandle` is non-zero and a real window is visibly on
  screen within a few seconds of the task running;
* the Setup workflow opens visibly (embedded Windows Update page, per Test 3's own expectations).

If `MainWindowHandle` stays `0` with the process otherwise alive and responding, this is the same
regression - do not proceed with the rest of Test 3 until it's fixed.

## Test 4 - Wi-Fi

On a VM/device with usable Wi-Fi hardware, with no wired internet at first desktop:

**Expected:**
* Support Tool connects using `Runtime\config.json`'s `Connection` section (the same canonical
  config staged at OOBE time) through its own ported Wi-Fi/connectivity service;
* no dependency on `Temp\WifiCredentials.json` for the live path (that file, if present, is
  rollback-only and unused).

If the available VM/hardware lacks Wi-Fi capability, mark this test not executable there rather
than claiming success.

## Test 5 - Windows Update reboot/resume

Using an image with genuine pending cumulative updates:

**Expected:**
* Support Tool's embedded Windows Update installs updates;
* when a reboot is required: session persisted (`Stage=WindowsUpdate`, `ResumeRequired=true`),
  resume task confirmed present, and the reboot happens only after both are confirmed;
* after the reboot/login sequence, the same staged `Runtime\SupportTool.exe` relaunches (via the
  still-registered task) and returns directly to the Setup Windows Update page;
* Setup Agent is not involved at any point in this sequence.

## Test 6 - Non-reboot abnormal exit

During Setup Windows Update, at a safe boundary (not mid-COM-call), with `ResumeRequired=false`
still persisted, kill Support Tool's process abnormally (not a normal close). Then log
off/reboot/log back in.

**Expected:**
* the resume task, Runtime, and session all survive the abnormal exit (no ordinary-close teardown
  ran);
* the next qualifying logon relaunches Support Tool via the still-registered task;
* Setup resumes directly at the persisted `WindowsUpdate` stage (Stage-driven, not
  `ResumeRequired`-driven).

## Test 7 - Terminal Windows Update completion

Once Windows Update reaches genuine terminal completion (fresh search: 0 eligible, no reboot
pending):

**Expected:**
* `ResumeRequired=false` persisted (already was, but re-confirmed);
* `Elkjop Toolkit Resume` is deleted and confirmed absent;
* Setup's persisted `Stage` advances to `Install`;
* Support Tool remains the single substantial UI - no separate window/process ever appears.

## Test 8 - No old live task, at every checkpoint

At every checkpoint above (prepare-only, post-Sysprep, first desktop, post-reboot-resume,
terminal completion):

```
schtasks /query /tn "Elkjop Setup Agent"
```

**must** report the task absent. Rollback files existing on disk (`Temp\SetupAgent.exe`, etc.)
during Phase 4B testing are expected and acceptable; the old task actually being registered or
running automatically is not.

---

## Test 9 - Windows Update is a standalone page (no embedded double-header)

**Background:** Windows Update used to be embedded inside `SetupPage`, producing a stacked
"Setup" header above the Windows Update page's own header. `SetupPage`/`SetupPageViewModel` have
been removed entirely - Setup's mandatory Windows Update gate now navigates straight to the
standalone Windows Update page, and the same shared `WindowsUpdatePageViewModel` instance serves
both the Setup workflow and the Service workflow's own "Windows Update" nav item.

On a Setup-workflow session at `Stage=WindowsUpdate`, and separately from Service by clicking the
"Windows Update" item under Tools in the sidebar:

**Expected:**
* exactly one header reading "Windows Update" is visible - never two stacked headers;
* the header's subtitle reads "Required before setup can continue" for the Setup workflow, and
  "Check and install available Windows updates" for the Service workflow, while idle; once a
  check/install is in progress the subtitle shows live status text instead;
* the Setup sidebar has no separate "Setup" nav item any more - Setup is a startup-time routing
  decision, not a page.

## Test 10 - Background navigation keeps the same Windows Update run alive, with a footer

Start Windows Update (Setup workflow, or Service via the Windows Update nav item), then while it
is actively checking/downloading/installing, navigate to a different page (e.g. Apps/Programs).

**Expected:**
* the in-progress Windows Update run is **not** cancelled, restarted, or reset by navigating away;
* a thin footer/status strip appears at the bottom of the window showing "Windows Update", live
  status text, and progress (e.g. "x/y updates"), with a `[View]` button;
* for the Setup workflow specifically, confirm navigating away does **not** advance
  `session.json`'s `Stage` - the mandatory gate stays mandatory;
* clicking `[View]` navigates back to the Windows Update page showing the same live run already
  in progress - **not** a fresh "Check for updates" idle state, and no second Windows Update
  operation is started (check logs/WUA activity - only one search/download/install sequence runs
  the whole time);
* navigating back to the Windows Update page itself hides the footer (no page showing its own
  footer under itself).

## Test 10b - Regression: reboot countdown window creation must not throw

**Background:** the first real Phase 4B VM run of the reboot-countdown UI reached
`WindowsUpdateOutcome.RebootRequired`, and lifecycle persistence succeeded fully
(`Stage=WindowsUpdate`, `ResumeRequired=true`, `Elkjop Toolkit Resume` task verified present) -
but constructing `RebootCountdownWindow` immediately afterward threw:

```
System.Windows.Markup.XamlParseException: Provide value on
'System.Windows.Baml2006.TypeConverterMarkupExtension' threw an exception.
  ---> System.IO.IOException: Cannot locate resource 'views/resources/favicon.ico'.
   at RebootCountdownWindow.InitializeComponent()
```

Cause: `RebootCountdownWindow.xaml` lives under `Views/` and referenced its titlebar icon as
`Icon="Resources/favicon.ico"` - a path relative to the XAML file's own location, which WPF
resolved as `Views/Resources/favicon.ico` (that path doesn't exist; the icon is a `Resource`-linked
build item at the project-root-relative `Resources/favicon.ico`, the same one `MainWindow.xaml`
already references successfully because `MainWindow.xaml` itself sits at the project root). Fixed
by changing the reference to the root-qualified `Icon="/Resources/favicon.ico"`, which resolves
against the application root regardless of which XAML file references it - no icon file was
copied, moved, or duplicated.

**Test:** repeat Test 11 below through the point where lifecycle persistence completes and the
reboot countdown is about to show.

**Expected (fixed behavior):**
* `RebootCountdownWindow` constructs and displays without throwing;
* its titlebar icon renders correctly (not a blank/missing-icon placeholder);
* the 5-second cancellable countdown then proceeds exactly as described in Test 11.

If a `XamlParseException`/`IOException` referencing a missing icon resource appears again at this
point, this is the same regression - do not proceed with the rest of Test 11 until it's fixed.

## Test 11 - Reboot countdown: persistence and task verification happen before any countdown

Using an image with a genuine pending-reboot update, let Windows Update reach the point where a
reboot is required.

**Expected, in this exact order:**
1. `session.json` is persisted (`Stage=WindowsUpdate`, `ResumeRequired=true`) and the
   `Elkjop Toolkit Resume` task is created/renewed and positively confirmed present -
   **before** any countdown UI appears;
2. only then does a countdown dialog appear ("Restart required... Restarting in 5 seconds...")
   with a `Cancel` button;
3. if left alone, the countdown reaches 0 and the machine reboots **immediately** - time the gap
   between the countdown showing "Restarting in 1 second..." and the machine actually going down;
   it must be on the order of a couple of seconds, **not** an additional ~60-second Windows
   shutdown-timer wait. (This is a regression test for a real bug found in external review of the
   first cut of this pass: `RealRebootService` called `shutdown.exe /r /t 60`, which stacked a
   second, un-cancellable 60-second OS-level countdown on top of Support Tool's own 5-second one.
   It now calls `shutdown.exe /r /t 0 /f`.)
4. after logon, the same staged `SupportTool.exe` resumes via the task and returns directly to
   the Windows Update page, continuing the same Setup/Service session (no second run started, no
   session reset).

Also verify the negative case: temporarily force session persistence or task
creation/verification to fail (e.g. rename the staged exe so `CreateOrRenewResumeTask` can't
target it, or make `Runtime\` read-only). **Expected:** an actionable error dialog appears, no
countdown is ever shown, no reboot happens, and Support Tool keeps running normally afterward.

## Test 12 - Cancelling the reboot countdown defers it cleanly

Repeat Test 11's setup, but click `Cancel` during the countdown instead of letting it expire.

**Expected:**
* the machine does **not** reboot;
* `session.json` still shows `ResumeRequired=true`, `Stage=WindowsUpdate` (unchanged - not
  cleared, not advanced);
* the `Elkjop Toolkit Resume` task is still registered and confirmed present (not deleted);
* the Windows Update page's terminal panel now shows a `Reboot now` button in place of `Check
  again`;
* the footer strip (visible on any other page) shows "Windows Update - Restart required to
  continue" together with a `Reboot now` action, and following `[View]` from the footer lands
  back on the same deferred-reboot state (not a fresh check).

## Test 13 - "Reboot now" resumes the automatic flow without starting a new run

Following on directly from Test 12's deferred state, click `Reboot now` (either from the Windows
Update page's own terminal panel, or from the footer).

**Expected:**
* no new Windows Update search/download/install run is started;
* `session.json`'s `Stage`/`ResumeRequired` and the resume task are defensively re-verified
  (re-persisted/re-created if needed) before the reboot actually happens - confirm via logs this
  re-verification step runs even though nothing had actually changed since Test 12;
* no second countdown dialog appears - the reboot happens promptly once re-verification succeeds,
  with **no** additional ~60-second Windows shutdown-timer wait (same regression check as Test
  11 step 3 - "Reboot now" must mean now, not "now, then wait another minute");
* after logon, the session resumes exactly as in Test 11 step 4.

Also verify the negative case: force re-verification to fail at this point (same technique as
Test 11's negative case). **Expected:** an actionable error appears, no reboot happens, and the
deferred footer/`Reboot now` state remains visible afterward (technician can retry).

## Test 13b - Reboot launch itself fails (shutdown.exe cannot be started)

Force `shutdown.exe` to be unusable at the moment `RebootNow` is called - e.g. temporarily rename/
remove `C:\Windows\System32\shutdown.exe`, or block it via a policy/AV rule - then either let a
Test 11 countdown expire, or click `Reboot now` per Test 13.

**Expected:**
* the thrown exception from launching the process is caught, logged, and does **not** crash the
  WPF Dispatcher or the app;
* an actionable error dialog appears (distinct from the "could not save session state"/"could not
  verify the resume task" dialogs - this one specifically says the machine could not be rebooted,
  not that persistence/verification failed);
* `session.json` still shows `ResumeRequired=true`, `Stage=WindowsUpdate`, and the
  `Elkjop Toolkit Resume` task remains registered and confirmed present - persistence/verification
  already succeeded before the failed launch, so neither is touched or rolled back;
* the page/footer return to (or remain in) the deferred `IsRebootDeferred` state with `Reboot now`
  available, so the technician can restore `shutdown.exe` and retry without losing the session.

## Test 14 - Fully up to date: no countdown, footer disappears, Setup advances normally

On a machine already fully current (fresh search: 0 eligible updates, no reboot pending):

**Expected:**
* no reboot countdown is ever shown for the `FullyUpToDate` outcome;
* for the Setup workflow: `ResumeRequired=false` persisted, the `Elkjop Toolkit Resume` task is
  deleted and confirmed absent, `Stage` advances to `Install`, and navigation moves to the Install
  page automatically;
* for the Service workflow: `ResumeRequired=false` persisted, the task deleted if present, and
  the workflow returns to normal Service use;
* the footer never appears during this pass (no active run reaches a pending/deferred state at
  any point) and, if it happened to be visible from an earlier run, it disappears once this
  terminal state is reached.

## Test 15 - Custom title bar: chrome, window controls, and close guards

**Background:** Support Tool's `MainWindow` previously used the default Windows title bar with no
custom chrome. This pass adds the same `WindowChrome`-based custom titlebar/window-control
convention already used by Account Setup/Setup Agent (icon, title, minimize, maximize/restore,
close), plus the WM_GETMINMAXINFO fix so maximizing doesn't cover the taskbar, plus
`PerMonitorV2` DPI awareness in `app.manifest`. This is a new custom chrome for this window, not a
restoration of a prior one - Support Tool never had custom chrome before this pass.

**Expected:**
* the window shows a custom titlebar (Elkjøp visual language, no default Windows title bar) with
  working minimize, maximize/restore (icon swaps correctly between the two states), close, and
  click-and-drag-to-move on the titlebar itself;
* maximizing does **not** cover the taskbar, including when tested on a secondary monitor with a
  different work area (multi-monitor check);
* resizing from the window edges/corners still works (this chrome is resizable, unlike the
  close-only `RebootCountdownWindow`);
* on a display with non-100% scaling, or when dragged between monitors of different DPI, the
  titlebar/buttons/content all scale correctly with no blurriness or mis-sized hit targets;
* clicking the custom close button (X) goes through the exact same `OnClosing` guards as before
  this pass - repeat the relevant slice of Tests 5/6/11/12 (WUA-active block, lifecycle-transition
  block, `IsLifecycleRecoveryBlocked` block, ResumeRequired+task-confirmed preserve-on-close, and
  ordinary teardown) using the new close button specifically, to confirm none of those guards were
  weakened by the chrome change.

---

## Test 16 - Regression: first-desktop Start menu no longer covers Support Tool

**Background:** the first real Phase 4B OOBE run showed Windows' automatic first-logon Start
showcase covering Support Tool for the entire 60-second watch window - the log showed
`Foreground window is now 'explorer' (title: "Program Manager")` the whole time, so none of
`StartMenuDismisser`'s known candidate processes (`StartMenuExperienceHost`,
`ShellExperienceHost`, `SearchHost`) were ever matched, and the watcher completed having done
nothing. Some Windows 11 builds render this overlay as an immersive popup that never itself
becomes the Win32 foreground window - the desktop underneath (`explorer`/"Program Manager") stays
reported as foreground the whole time. `StartMenuDismisser` now also treats that exact
`(explorer, "Program Manager")` pair as a candidate (scoped to this bounded first-desktop watch
only), sends Escape, and actively re-activates Support Tool's own window - repeating this every
poll tick until the 60-second window elapses.

**Test:** a fresh Setup/OOBE run through first desktop logon, on hardware/a VM build that
reproduces the original Start-showcase-covers-Support-Tool symptom.

**Expected:**
* the Windows Start showcase does not remain covering Support Tool for the whole watch window;
* if it opens, it is dismissed (Escape) and Support Tool ends up foregrounded and visibly usable;
* neither Explorer nor `StartMenuExperienceHost`/`ShellExperienceHost`/`SearchHost` is killed at
  any point - only Escape is sent and Support Tool's own window is activated;
* the log shows what was detected each tick and what action was taken, ending with the
  window-elapsed message - never a crash;
* repeat a normal Service-workflow launch (`service.exe`) on the same kind of machine and confirm
  this logic does not run at all (`FirstDesktopActionsCompleted` starts `true` for Service, so the
  `Workflow == Setup && !FirstDesktopActionsCompleted` gate in `App.xaml.cs` never fires it) -
  this remains a bounded, Setup-only, first-desktop action.

## Test 16b - Regression: watcher must survive a Start showcase that opens AFTER Support Tool

**Background:** an earlier cut of this fix returned from the watch loop as soon as Support Tool
was observed foreground on any tick (`pid == ownProcessId` -> `return`). External review caught
that this defeats the watcher's actual purpose: Windows can open the first-logon Start showcase
*several seconds after* Support Tool has already appeared and been briefly foreground, and an
early return leaves nothing running to catch that later overlay. Fixed: being foreground on a
given tick now only skips Escape/re-activation for *that* tick (`continue`, not `return`) - the
loop keeps polling for the entire bounded 60-second window regardless, so a showcase that opens
mid-window is still caught.

**Test:** on a VM/build where the timing can be controlled or reproduced, arrange for (or confirm
via logs from a real run) a sequence like:

```
t=0.5s  Support Tool is foreground
t=4s    Windows opens the Start showcase
t=4.5s  (next poll tick)
```

**Expected:**
* the watcher does **not** exit merely because Support Tool was foreground at `t=0.5s`;
* at the `t=4.5s` tick (or whichever poll tick first observes it), the showcase/fallback candidate
  is detected, Escape is sent, and Support Tool is re-activated - exactly as if the showcase had
  appeared immediately at `t=0`;
* the watch continues polling after that (in case the showcase reopens again) until the full
  60-second window elapses, never returning early just because Support Tool is foreground again;
* still no unbounded loop - the watcher still terminates and logs "elapsed" once the 60-second
  deadline passes, whether or not it ever saw the showcase.

## Test 16c - Regression: detect a Start showcase even while Support Tool owns foreground

**Background:** real OOBE testing failed again even with Test 16b's fix in place - the immersive
Start showcase visibly covered Support Tool for the *entire* watch window while
`GetForegroundWindow()` kept reporting Support Tool's own process as foreground the whole time (or
the desktop, per the earlier fallback). The watcher's detection was still fundamentally built
around "whatever is covering us must own the Win32 foreground window", which real hardware has now
disproven twice. `StartMenuDismisser` no longer depends on foreground ownership for detection at
all: every tick, it walks every visible top-level window on the desktop via `EnumWindows` and
checks each one's owning process against the known candidate list
(`StartMenuExperienceHost`/`ShellExperienceHost`/`SearchHost`), regardless of which window
currently owns foreground. The narrow `(explorer, "Program Manager")` foreground fallback from
Test 16/16b remains as an *additional* signal, not the only one. Dismissal now sends Escape via
`SendInput` (not `keybd_event`) plus a best-effort targeted key-message post to the candidate
window itself, and re-activation is strengthened beyond plain WPF `Activate()`:
`ShowWindow`/`BringWindowToTop`/`SetForegroundWindow` on the actual native window handle, with a
bounded, carefully-scoped `AttachThreadInput` retry (always detached in a `finally`, never
repeated, never leaves the window permanently Topmost) if Windows' foreground-lock rules refuse
the plain `SetForegroundWindow` call.

**Test:** fresh Setup/OOBE run through first desktop logon, specifically arranging for (or
confirming via logs from a real run) the exact previously-failing sequence:

```
t=0.5s  Support Tool owns GetForegroundWindow()
t=4s    the immersive Start/showcase becomes visibly over Support Tool,
        but GetForegroundWindow() may STILL report Support Tool (or the desktop)
t=4.5s  next poll tick
```

**Expected:**
* at the `t=4.5s` tick (or whichever tick first observes it), the `EnumWindows` scan finds a
  visible `StartMenuExperienceHost`/`ShellExperienceHost`/`SearchHost` window **even though**
  `GetForegroundWindow()` never changed away from Support Tool - this is the specific case Test
  16/16b's foreground-only detection could not catch;
* the log shows the distinct candidate signature (HWND/PID/process name/title/class) at the moment
  it changes, not spammed every 500ms;
* Escape is sent (via `SendInput`), the showcase is dismissed, and Support Tool is brought
  genuinely to the front (verify it's not just "activated" internally but actually visibly on top
  and receiving input);
* neither `StartMenuExperienceHost`, `ShellExperienceHost`, `SearchHost`, nor `explorer` is killed
  or restarted at any point;
* no registry/policy changes were made;
* Support Tool is not left permanently `Topmost` after this (normal alt-tab/z-order behavior
  resumes for it once the showcase is dismissed);
* the watch still terminates at the bounded 60-second deadline;
* if this still somehow fails, the log's captured HWND/PID/process/title/class/visible-state data
  for every candidate window observed during the run is sufficient to diagnose why, without
  needing to reproduce on hardware again.

## Test 16d - Regression: a DWM-cloaked shell candidate must not trigger dismissal

**Background:** `IsWindowVisible()` (WS_VISIBLE) alone is not sufficient on modern Windows - a
UWP/immersive shell surface can remain `WS_VISIBLE` while DWM has it cloaked (not actually
composited/presented to the user), e.g. a dormant `StartMenuExperienceHost` window left behind
between showings. Test 16c's `EnumWindows` scan would previously treat any such window as a live
candidate, which could fire Escape/`PostMessage`/`BringWindowToTop`/`SetForegroundWindow` every
500ms for the entire 60-second window against a window nobody can actually see - making the first
desktop hostile to use. Every candidate is now also checked via
`DwmGetWindowAttribute(DWMWA_CLOAKED)`: only a candidate that is both `WS_VISIBLE` **and** not
DWM-cloaked is treated as actionable. A cloaked candidate is still captured for diagnostics, never
acted on; if the cloak state itself can't be determined, the candidate is conservatively treated
as not actionable rather than guessing.

**Test (code inspection / diagnostic, since deliberately reproducing a real dormant cloaked
`StartMenuExperienceHost` window on demand may not be practical):**
* arrange for (or confirm via logs from a real run where this was previously observed) a matching
  `StartMenuExperienceHost`/`ShellExperienceHost`/`SearchHost` window where `IsWindowVisible` is
  `true` but `DWMWA_CLOAKED` is non-zero;
* **Expected:** the window may appear in the shell-candidate scan log (diagnostic value, marked
  `actionable=False`), but Escape is **not** sent, `PostMessage` is **not** posted to it, and
  Support Tool's foreground activation is **not** attempted purely on account of this cloaked
  window - the watcher takes no dismissal action unless a *different*, actually-actionable signal
  (an uncloaked candidate, or the `explorer`/"Program Manager" foreground fallback) is also
  present that tick;
* separately, confirm a genuinely uncloaked, actually-visible candidate (Test 16c's own scenario)
  still triggers dismissal normally - this fix must not make real detection stop working;
* if cloak-state querying itself fails for a given window (e.g. `DwmGetWindowAttribute` returns a
  non-zero HRESULT), confirm that window is likewise treated as not actionable (never force-
  activates Support Tool against a window of unknown presentation state), and that this doesn't
  crash the watch loop.

## Test 16e - Regression: SendInput must actually queue both Escape events

**Background:** a real OOBE run's log showed `SendInput reported only 0/2 Escape event(s)
queued.` for the entire watch window - Escape was never actually reaching the input queue. Root
cause: the managed `INPUT` struct's union only declared `KEYBDINPUT`, but the native Win32 `INPUT`
union's largest member is `MOUSEINPUT` (it carries an extra pointer-sized field beyond what
`KEYBDINPUT` has) - so the managed struct was smaller than the real native one on every
architecture, `Marshal.SizeOf<INPUT>()` computed the wrong `cbSize`, and `SendInput` rejected the
whole call. Fixed by declaring the full native union (`MOUSEINPUT`/`KEYBDINPUT`/`HARDWAREINPUT`,
all at `[FieldOffset(0)]`) so the struct's size is correctly driven by `MOUSEINPUT` on every
platform - no architecture's byte count is hardcoded; `Marshal.SizeOf<INPUT>()` computes the right
value automatically on x86/x64/ARM64 via the CLR's own layout rules.

**Test:** fresh Setup/OOBE run through first desktop logon, on a build/VM where the Start showcase
reproduces.

**Expected:**
* the log shows `SendInput` actually queuing **2/2** events (no more "0/2 Escape event(s) queued"
  warning) when a candidate is detected;
* the Start showcase is genuinely dismissed by the synthetic Escape keystroke itself (not just by
  the secondary `PostMessage` nudge or lucky timing);
* Support Tool ends up visibly in front;
* if `SendInput` ever again reports fewer events than requested (on this build or a future one),
  the log now also captures `Marshal.GetLastWin32Error()`'s numeric code and its readable
  `Win32Exception` message, so a future real-OOBE failure is diagnosable without needing to
  reproduce on hardware to find out why.

## Start-menu finding (latest real-OOBE result) - document only, no code change this pass

A real OOBE run against the build with Test 16e's fix confirmed:
* the `SendInput reported only 0/2 Escape event(s) queued.` warning is **gone** - the INPUT
  native-layout fix worked, `SendInput` is now accepted by Windows and genuinely queues both
  events;
* **the Start/showcase still remains visible over Support Tool** despite that.

Conclusion: the remaining problem is **no longer** the `INPUT` struct/`cbSize` issue Test 16e
fixed - `SendInput` is confirmed working at the Win32 API level now. The remaining gap is in
either (a) whether Escape is actually the right dismissal input for this specific showcase, or (b)
whether `StartMenuDismisser`'s shell-surface detection is actually finding/targeting the right
window in the first place (or some combination). `StartMenuDismisser` was deliberately **not**
modified in this pass pending that separate investigation - do not assume or reimplement a fix
here without first confirming which of those two is actually failing on real hardware (e.g. by
comparing the logged shell-candidate scan data against what's actually on screen at the moment the
showcase is visible).

## Test 17 - Windows Update column headers: visual polish

While Windows Update is actively running with at least a couple of updates listed, including at
least one with a status value like "Pending restart" (the longest status string) and a "100%"
progress value:

**Expected:**
* a header row reading "Update / Progress / Status" is visible above the scrolling update rows;
* the header text is bold, sized 13px (larger than the small dim label used before this pass), and
  uses `PrimaryTextBrush` (white/bright) rather than the dim secondary gray used for the row values
  themselves - clearly readable, while still visually subordinate to the page's own 22px "Windows
  Update" header above it;
* the header's three columns (`* | 80 | 100`) are aligned with the row template's own columns -
  "Update" lines up over the name column, "Progress" over the percentage column, "Status" over the
  state column, for every row, including after scrolling;
* the tightened Progress/Status column widths leave visibly less unused whitespace than before,
  giving long update names more room, while neither column clips or truncates normal values -
  specifically verify "100%" and "Pending restart" (the longest realistic status string) both
  display in full, not just short ones like "50%"/"Installed".

## Test 17b - Regression: breathing room between update title and percentage

**Background:** manual visual testing found the update-title text running directly into the
percentage column with no gap (e.g. "...Current C...50%"), since the Name and Progress columns sat
flush against each other with zero spacing. First fix attempt added a 10px right margin on the
Name column's text AND a 10px left margin on the Progress column's text - external review caught
that this created an effective ~20px gap and, more importantly, shifted the row's percentage text
10px to the right of the "Progress" header label, breaking header/row alignment. Corrected to use
only the Name column's own right-side margin (10px) - the Progress column's text keeps no extra
margin of its own, so it stays exactly where the "Progress" header expects it. Column widths
(`* | 80 | 100`) are unchanged throughout; this is inset spacing only, not a redesign.

**Expected:**
* a clear, comfortable gap (roughly 8-12px) is visible between a truncated/ellipsized update name
  and its percentage value, even for names that fill the entire Name column width;
* the update name still trims with `CharacterEllipsis` rather than wrapping or overflowing into
  the Progress column;
* percentage values line up exactly under the "Progress" header label - no rightward shift versus
  the header (this is the specific regression the correction fixes: verify the percentage text's
  left edge, not just "roughly under", actually matches the header label's left edge);
* header and rows remain aligned for all three columns;
* the Progress column itself was not made significantly wider to create this gap - it's inset
  spacing on the Name column only, not a layout redesign.

## Test 18 - Reboot interlock: never reboot while another toolkit operation is active

**Background:** the first real Phase 4B OOBE/Service run had Windows Update running in the
background while the technician navigated to Apps/Programs and started an uninstall; WUA reached
`RebootRequired` mid-uninstall, and the old logic proceeded straight into the reboot-countdown
flow regardless - unsafe, since the uninstall was still actively running. A new
`SharedViewState.EnterToolkitOperation()`/`IsToolkitOperationActive` lease (held for the whole
duration of any Install/Uninstall/Debloat batch - see `InstallService.InstallAsync`/
`UninstallService.UninstallAsync`) is now checked by `WindowsUpdateLifecycleCoordinator` before
ever showing the countdown, and again by "Reboot now" (both before and after its own persist/
verify step, to close the race where an operation starts while that await is in flight).

**Test:**
1. Start Windows Update (Setup, or Service via the Windows Update nav item) so it's running in
   the background.
2. Navigate to Apps/Programs/Install and start a real, long-running install/uninstall/debloat
   operation - ideally one that takes long enough to still be running when step 3 completes.
3. Let Windows Update reach `RebootRequired` while that operation is still in progress.

**Expected while the operation is still active:**
* `session.json` is persisted (`Stage=WindowsUpdate`, `ResumeRequired=true`) and the
  `Elkjop Toolkit Resume` task is created/verified present, exactly as before;
* **no** reboot countdown appears;
* **no** reboot happens;
* the footer (visible from any other page) reads "Windows Update - Restart required - waiting
  for current operation to finish" (not the plain "Restart required to continue" wording used
  when nothing else is running);
* on the Windows Update page itself, the terminal panel shows "Waiting for the current operation
  to finish before this can restart." alongside a visibly disabled `Reboot now` button;
* clicking `Reboot now` anyway (if not fully disabled, or via a quick double-click race) is
  refused with a clear message ("Another toolkit operation is still running. Wait for it to finish
  before restarting."), and `session.json`/the resume task are left exactly as they were - no
  `shutdown.exe` launch, confirm via process list/logs.

**Expected once the unrelated operation finishes:**
* no surprise automatic countdown begins - the reboot simply remains deferred;
* the footer/page text updates back to the plain "Restart required to continue" wording, and
  `Reboot now` becomes enabled;
* clicking `Reboot now` now re-verifies session/task state and reboots immediately
  (`shutdown.exe /r /t 0 /f`, no second countdown);
* after logon, Support Tool resumes the Windows Update stage normally, exactly as in Test 11
  step 4.

Also verify the counterfactual: repeat the same scenario with **no** other operation active when
`RebootRequired` is reached - the original 5-second cancellable countdown must still appear and
behave exactly as in Tests 11-13 (this pass must not change behavior when nothing else is
running).

## Test 19 - Setup stays on the Windows Update page when fully up to date

**Background:** previously, a Setup session reaching `WindowsUpdateOutcome.FullyUpToDate`
auto-navigated straight to the Install page the instant `Stage=Install`/`ResumeRequired=false`/
task-deleted persistence succeeded. That backend transition was correct but undesirable UX -
Support Tool now remains on the Windows Update page showing the result, and the technician
navigates to Install manually.

**Test:** run the Setup workflow's Windows Update to genuine terminal completion on a machine
with no pending updates (or let a real run reach 0 eligible updates with no reboot pending).

**Expected:**
* `session.json` still persists exactly as before: `Stage=Install`, `ResumeRequired=false`, and
  the `Elkjop Toolkit Resume` task is deleted and confirmed absent - verify via
  `schtasks /query /tn "Elkjop Toolkit Resume"` (must fail/report absent) and by inspecting
  `Runtime\session.json`;
* Support Tool does **not** automatically navigate away - it remains on the Windows Update page;
  the terminal panel and header subtitle both read "You are up-to-date.";
* the cross-page footer does not appear (unchanged from before - `HasActiveOrPendingOperation` is
  already false once `FullyUpToDate` is reached);
* the "Install" item in the sidebar is visible and clickable (it always is, regardless of
  workflow/stage) - clicking it navigates to the Install page manually;
* close and relaunch Support Tool (or, on a real OOBE VM, reboot) while this session/Runtime still
  exists on disk - startup navigation still respects the persisted `Stage=Install` and opens
  directly to Install on that next launch, confirming the Stage model itself is unchanged and only
  the immediate post-completion auto-navigation was removed;
* repeat for the Service workflow reaching the same `FullyUpToDate` outcome - unchanged: it still
  returns to normal Service use (`ServiceReturnedToNormal` navigation was not touched by this
  pass).

## Test 20 - Settings page: no Release crash, and portable configuration save

**Background:** clicking Settings in a real Release build previously crashed/disappeared Support
Tool. Root cause: `SharedViewState.IsDryRun`'s setter only existed under `#if DEBUG` (RELEASE's
backing field was `readonly`), while `SettingsPage.xaml`'s Dry Run `CheckBox` bound
`IsChecked="{Binding ViewState.IsDryRun, Mode=TwoWay}"` unconditionally - the SAFETY section is
only `Visibility`-collapsed in Release (via `IsDebugBuild`), not removed from the visual tree, so
the binding is still constructed against a property with no public setter at all. `IsDryRun` now
has a setter in every configuration; in Release the setter body intentionally does nothing (the
backing field stays the `readonly false` it was constructed with) - Release remains permanently
live-mode, never actually switchable into Dry Run.

Separately, this pass replaces the Settings stub with a real editor for `Account.Username`,
`Connection.WifiName`, and `Connection.WifiPassword` (Debloater/Installer stay out of scope for
this page). Saving always updates the local staged `Runtime\config.json` first, then makes a
best-effort attempt to also update the canonical USB `config.json` by relocating the originating
volume via `Runtime\source.json` (recorded by volume serial number at staging time, not drive
letter) - so it still works after a reboot changes the USB's drive letter, and is never required
for Support Tool to keep running if the USB is gone entirely.

**Test 20a - Release Settings page no longer crashes:**
* in a **Release** build specifically (not DEBUG - DEBUG's setter already worked), click the
  Settings sidebar item;
* **Expected:** the page opens normally, no crash, no disappearing window; the SAFETY/Dry-Run
  section is not visible (collapsed, Release-only via `IsDebugBuild`); ACCOUNT, CONNECTION, and
  CONFIGURATION sections are all visible.

**Test 20a2 - Load/refresh lifecycle (SettingsPageViewModel is a process-lifetime singleton):**
* first navigation to Settings: confirm the Username/Wi-Fi name/Wi-Fi password fields populate
  from the current `Runtime\config.json` (not blank, not stale placeholder text) and that the
  portable-source status line reflects the USB's actual current state;
* navigate away (e.g. to Home) and back to Settings **without** restarting Support Tool - confirm
  the edited-but-unsaved field values are NOT silently reloaded/overwritten from disk on this
  second visit (config load happens once per process via `HasLoaded`), but the portable-source
  status line IS re-checked and updated every time Settings is opened;
* concretely: with the USB connected, open Settings (status shows connected), navigate away,
  physically remove the USB, then navigate back to Settings - the status must now show
  "not currently connected" without needing a restart; reinsert the USB (any drive letter),
  navigate away and back again - status must show connected again.

**Test 20a3 - Regression: a failed initial load must not permanently block/blank a later save:**

**Background:** an earlier cut of the load lifecycle set `HasLoaded = true` unconditionally at the
start of the load attempt, before the actual `ConfigService.LoadAsync()` call. If that load then
failed, `HasLoaded` stayed stuck `true` with `_loadedConfig` still `null` and the editor fields
still blank/default - `MainViewModel.NavigateTo` would never retry the load on a later visit to
Settings (it only loads `when !HasLoaded`), and `SaveAsync`'s old `_loadedConfig ?? await
_configService.LoadAsync()` fallback meant a later, unrelated, incidentally-successful load could
supply a valid Debloater/Installer baseline while Save still combined it with the still-blank
Username/WifiName/WifiPassword fields - silently blanking out Account/Connection on save.
`HasLoaded` now means "successfully loaded", never merely "a load was attempted": a failed load
resets both `HasLoaded` and `_loadedConfig` back to false/null, and `SaveAsync` refuses to write
anything unless a load has genuinely succeeded (retrying the load once itself, then stopping with
no write if that retry also fails).

A second, subtler cut of this same fix had a reentrancy/cleanup race: `_loadTask`'s `??=`
assignment and `LoadCoreAsync`'s own `finally { _loadTask = null; }` could interleave wrongly when
`LoadCoreAsync` completes **synchronously** (e.g. an immediate failure) - `LoadCoreAsync` could run
to completion and clear `_loadTask` *before* the `??=` on the caller's side ever assigned it,
so the assignment would then latch that already-dead, already-failed task into `_loadTask` for
good. Every later `LoadAsync()` call would see `_loadTask` non-null and just re-await that same
stale completed task forever - `HasLoaded` would correctly stay `false`, but no actual new load
attempt would ever run again, so restoring a valid config and retrying would never recover on its
own. Fixed by moving `_loadTask`'s entire lifecycle (assignment and clearing) into the outer
`LoadAsync()` wrapper, with a `ReferenceEquals` check before clearing so a concurrent caller that
already moved on to a newer attempt can't have its own in-flight task field wiped out.

**Test:**
1. temporarily force `ConfigService.LoadAsync()` to fail the first time Settings is opened (e.g.
   rename/corrupt `Runtime\config.json` momentarily, or a debug hook) and open Settings;
2. **Expected:** the page shows a clear load-failure message; the editor fields remain
   blank/default (not silently populated with anything); click "Save" in this state -
   **expected:** it retries the load, that retry also fails (config is still broken), and Save
   **stops without writing** `Runtime\config.json` at all - confirm via file timestamp/content
   that nothing was written;
3. **while still leaving the config broken**, trigger at least one more retry (another "Save"
   click, or navigate away and back to Settings) - **expected:** this must be observably a
   **genuinely new** `ConfigService.LoadAsync()` attempt each time (e.g. confirm via a log/side
   effect that fires once per real attempt, or by toggling what's broken between retries and
   observing the failure reason change accordingly) - not merely re-awaiting a single stale
   completed task from step 1 with no new attempt actually made;
4. restore a valid `Runtime\config.json`;
5. navigate away from Settings and back (or click "Save" again, which now retries the load as
   part of the same click) - **expected:** the load now succeeds, the editor fields populate
   correctly from the restored config, and a subsequent "Save" click writes normally (local
   Runtime update, plus the usual portable-USB best-effort behavior from Tests 20b-20e). This step
   is the one the earlier synchronous-completion race would have silently broken (recovery would
   never happen no matter how many retries followed step 1) - it must succeed on the very next
   retry once the config is actually valid again, not require an app restart.

**Test 20b - Editing and saving with the USB still connected:**
* on a Setup or Service session staged fresh from a real USB (so `Runtime\source.json` exists),
  with the USB still connected at its original drive letter, open Settings, change the username,
  Wi-Fi name, and Wi-Fi password fields to new values, and click "Save";
* **Expected:**
  * `Runtime\config.json`'s `Account.Username`/`Connection.WifiName`/`Connection.WifiPassword`
    contain the new values;
  * the USB's own `<drive>\Elkjop Toolkit\config.json` contains the exact same new values;
  * both files' `Debloater` and `Installer` sections are byte-for-byte unchanged from before the
    save (confirms editing Account/Connection never erases or recreates unrelated sections);
  * both files still have present, well-formed `Account`/`Connection` sections (not dropped);
  * the result text reads "Saved. Also updated the canonical USB configuration at ...".

**Test 20c - Portable save after the USB's drive letter changes:**
* after Test 20b's staging, eject/reconnect the same USB (or reboot) so Windows assigns it a
  different drive letter than it had at staging time, change a field again (e.g. the username),
  and save;
* **Expected:** the save still finds and updates the USB's `config.json` at its *new* drive letter
  with the current edited values - confirming resolution is by volume serial number (via
  `Runtime\source.json`), not a remembered/hardcoded drive letter, and not a scan for the first
  drive containing a matching file (verify by also connecting a second, unrelated USB stick that
  happens to contain some other `Elkjop Toolkit\config.json` and confirming the save does **not**
  target that one).

**Test 20d - Editing and saving with the USB disconnected:**
* disconnect the originating USB entirely, change the username/Wi-Fi fields, and click "Save";
* **Expected:** `Runtime\config.json` updates locally with the new values; the result text clearly
  states the change is not portable (e.g. "the originating USB is not currently connected") and
  does **not** claim success writing to the USB; Support Tool continues running normally - nothing
  about this requires the USB to be present.

**Test 20e - Older Runtime with no source.json:**
* using (or simulating) a Runtime staged before this feature existed - i.e. no
  `Runtime\source.json` file present at all - open Settings, change a field, and click "Save";
* **Expected:** the session remains fully usable; `Runtime\config.json` still updates locally with
  the new value; the status text/result clearly state no portable source is recorded for this
  session (not an error, not a crash) - a missing `source.json` must never make the session itself
  unusable.

## Test 21 - Regression: source-volume identification must be validated BEFORE stale Runtime cleanup

**Background:** an earlier cut of the source.json feature computed `PortableConfigSourceLocator.
CreateFromConfigPath(...)` (part of the required fresh-stage input validation, alongside the
exe/config existence and parse checks) only AFTER `DeleteResumeTaskIfPresent`/`DeleteRuntimeNow`
had already destroyed the previous stale Runtime in both Service Launcher's `StageFreshSession`
and Account Setup's `SupportToolStagingService.StageFreshRuntime`. A volume-identity failure
discovered at that point would leave the machine with no Runtime at all - worse than when the
launcher started. Fixed: source-volume identification now happens alongside the other fresh-input
validation, strictly before any destructive cleanup, in both entry points; `session.json` remains
the final file written in both.

**Test (Service Launcher, code-inspection/fault-injection - may be difficult to reproduce with a
real USB):**
* stage a valid Service session once (so a real Runtime with a resume task potentially registered
  exists), then force `PortableConfigSourceLocator.CreateFromConfigPath` to fail on the next fresh
  stage attempt (e.g. temporarily point `ToolkitPaths.GetPortableConfigPath()` at a config file
  sitting on a drive whose volume information can't be queried, or otherwise simulate the P/Invoke
  call failing) while a stale (non-resumable) session is in place, and run `service.exe` again;
* **Expected:** the old stale Runtime (files, and any registered resume task) is confirmed **NOT**
  deleted - `RuntimeStaging.DeleteResumeTaskIfPresent()`/`DeleteRuntimeNow()` must never be
  reached; the launcher fails visibly with a clear "could not determine the source USB volume"
  message and exits non-zero, leaving the machine exactly as it was.
* Repeat the same principle for Account Setup's `SupportToolStagingService` - a forced
  volume-identity failure during a fresh Setup stage must throw `RuntimeStagingException` (Account
  Setup's own existing fatal-staging path: OOBE stays in place, no Sysprep, no account-bypass
  fallback) before `DeleteResumeTaskIfPresent`/`DeleteRuntimeNow` run, not after.

## Test 22 - WinGet prerequisite: broken/ancient WinGet is repaired before catalog enumeration

**Background:** an old Windows 11 22H2 VM exposed a real customer-machine failure: WinGet reported
a version fine (`winget --version` → `v1.2.10691`, DesktopAppInstaller 1.17.10691.0), but a real
`winget install --id Mozilla.Firefox --exact --silent --accept-package-agreements
--accept-source-agreements` crashed `winget.exe` itself (exit `-1073741819` / `0xC0000005`
ACCESS_VIOLATION in `WindowsPackageManager.dll`, Application Event 1000) - and that same version
doesn't support `--disable-interactivity`, which `WingetStatusChecker`/`RealInstallExecutor` both
already depend on. `WingetPrerequisiteService` reproduces a check→repair→re-verify sequence, run
BEFORE `InstallPageViewModel` enumerates catalog app states (not just before the actual install
click) - `WingetStatusChecker` shells out to `winget list ... --disable-interactivity` too, so
running it against a broken WinGet would otherwise mark every single catalog app `NotInstalled`,
indistinguishable from "genuinely not installed."

**Two real-machine corrections folded into this test, both from the same failing VM:**
1. The repair command itself. `Repair-WinGetPackageManager -Latest -Force -AllUsers` **failed** on
   the actual broken state (it downloaded the modern dependencies, then tried to re-register/test
   the still-broken old package and failed). `Repair-WinGetPackageManager -Force -Latest` (no
   `-AllUsers`) succeeded in the same elevated account, bringing it to `v1.29.290` - Support Tool
   always repairs for the current interactive elevated account it's already running as, so
   `-AllUsers` was never actually needed. The repair script now uses the working command.
2. Raw PowerShell CLIXML/progress serialization (`#< CLIXML ...`) previously leaked into the
   Install page's small footer text on a repair failure - never acceptable UI content. The repair
   script now sets `$ProgressPreference = 'SilentlyContinue'` first and wraps everything in an
   explicit try/catch that writes one plain-text line to stderr and exits non-zero on failure, so
   only clean human-readable text ever reaches the UI (raw diagnostics still go to the log).

**Also new in this pass: WinGet preparation is a distinct full-page state**, not a generic
"Checking available applications..." spinner with the small footer quietly saying "Repairing
WinGet...". The Install page now has four states (`PreparingPrerequisite`/`LoadingCatalog`/
`LoadFailed`/`CatalogReady`), each with its own dedicated full-page panel - see the UI checks
below.

**Also new: real live streaming progress during repair, and no Install confirmation dialog.**
`Repair-WinGetPackageManager -Force -Latest -Verbose` now streams its verbose output (download
sizes, per-dependency install steps) line-by-line as the process runs - not buffered and dumped
after the fact - into a scrolling detail log under the "Preparing WinGet" heading, alongside the
script's own "STAGE: ..." markers ("Installing NuGet provider...", "Installing WinGet repair
module...", "Repairing Windows Package Manager..."). Separately, clicking "Install Selected" no
longer shows a "You are about to install/update... Yes/No" confirmation dialog in either LIVE or
Dry Run - it begins immediately.

**Correction folded in: the prominent heading must track the real stage, not freeze.** An earlier
cut of the streaming work left the prominent `StatusMessage` heading stuck reading "Repairing
WinGet..." for the whole repair, with the actual sub-steps ("Installing NuGet provider...", etc.)
only visible in the detail log below it - a real UX mismatch, since the page's whole point was to
show both the current high-level stage AND the live detail. Fixed: `WingetPrerequisiteService`'s
own `ReportStage` helper now reports every stage message (its own "STAGE: ..." markers, plus the
two bookend "Checking WinGet.../Verifying WinGet..." messages) to BOTH the heading and the detail
log from exactly one place, so the heading now genuinely advances through "Checking WinGet..." →
"Installing NuGet provider..." → "Installing WinGet repair module..." → "Repairing Windows Package
Manager..." → "Verifying WinGet..." - matching the detail log exactly, with no duplicate lines in
either place (real granular verbose output, e.g. download sizes, is still detail-log-only, never
promoted to the heading).

**Test (use the old VM/snapshot if still recoverable - DesktopAppInstaller 1.17 / winget 1.2.10691):**
1. navigate to the Install page in **LIVE mode** (Release, or DEBUG with Dry Run explicitly
   toggled off);
2. **Expected during load - distinct full-page states, not the small footer:** a centered
   "Preparing WinGet" panel appears first, with its own secondary heading text that genuinely
   ADVANCES through the real current stage - "Checking WinGet..." → "Installing NuGet
   provider..." → "Installing WinGet repair module..." → "Repairing Windows Package Manager..." →
   "Verifying WinGet..." - never frozen on a generic "Repairing WinGet..." while only the detail
   log below it moves; no footer, no app cards, no "Install Selected" button visible during this
   state;
3. **Expected during the actual repair specifically:** a scrolling read-only detail area appears
   below the heading/progress bar, showing real-time lines as they arrive (not everything at once
   at the end) - the same stage messages as the heading (each appearing exactly once, no
   duplicates) plus genuine `Repair-WinGetPackageManager -Verbose` output such as download sizes
   and per-dependency install steps interspersed in the correct chronological order; the log
   auto-scrolls to the latest line; the detail area contains **no** raw "VERBOSE:" prefixes, no
   blank lines, and no CLIXML/serialized-object text at any point; the indeterminate progress bar
   (if still present) is not the only evidence of activity - the detail log is the real evidence;
4. **Expected - must NOT happen:** the catalog is **not** populated with every app marked
   `NotInstalled` while WinGet is still broken; no install batch is launched against a broken
   WinGet; no dialog listing per-app failures for a batch that never should have started; the
   fixed "Checking available applications..." text must **not** appear until WinGet is positively
   confirmed ready; no visible PowerShell console window appears at any point;
5. after repair, `winget --version` is re-checked (expect `v1.29.290` or newer), the
   `--disable-interactivity` capability is re-confirmed, and the non-mutating health probe
   (`winget source list`) is re-run and passes - only once all three pass does the page transition
   to the "Checking available applications..." (`LoadingCatalog`) state and catalog enumeration
   proceed;
6. the Install page then reaches `CatalogReady` - app cards and the footer (with "Install
   Selected") appear only now, loaded with correct per-app status;
7. select Firefox (or another catalog app) and click "Install Selected" - **expected:** installation
   begins **immediately**, with no "You are about to install/update... Continue?" confirmation
   dialog first, and succeeds, confirming the repaired WinGet is genuinely usable for real
   installs, not just readiness-check theater; repeat in Dry Run and confirm the simulated
   operation also begins immediately with no confirmation dialog;
8. check the log for: detected version (before and after repair), `--disable-interactivity`
   support (before: false, after: true), health-probe result, whether repair was attempted, the
   repair script's exit code (and stdout/stderr if non-zero), post-repair readiness, and the full
   raw streamed repair output (Verbose.log should contain everything, even lines filtered out of
   the UI) - no credentials/secrets should appear in any of it.

## Test 22a2 - Regression: InstallPage doesn't leak event subscriptions across navigation

**Background:** the streaming detail log's auto-scroll needed `InstallPage.xaml.cs` to subscribe
to `InstallPageViewModel.PropertyChanged` - an earlier cut did this via `DataContextChanged`,
unsubscribing the "old" ViewModel whenever `DataContextChanged` reported one. External review
caught that `DataContextChanged` is not a reliable place to unsubscribe: `InstallPageViewModel` is
a long-lived process singleton, but a fresh `InstallPage` visual instance is created by the
DataTemplate every time the technician navigates back to Install, and a DataTemplate-generated
root element's `DataContext` can simply stop being read once detached from the visual tree with no
guarantee `DataContextChanged` fires again to report it - so old, otherwise-unloaded `InstallPage`
instances could stay strongly referenced (and their handler still firing) via the singleton
ViewModel's own `PropertyChanged` event, accumulating across repeated navigation. Fixed:
subscription is now owned by `Loaded`/`Unloaded` (the actual visual-tree lifetime hooks) via a
tracked `_subscribedViewModel` field with a `ReferenceEquals` guard against double-subscribing;
`DataContextChanged` only acts while already `Loaded` (handling the unlikely case of a DataContext
swap on an already-loaded instance), and never on its own.

**Test (code-inspection/diagnostic - a real memory leak is slow to observe manually):**
* navigate to Install, then away, then back to Install repeatedly (10+ times) in the same running
  session;
* **Expected:** the "Preparing WinGet"/catalog page continues to behave normally each time (no
  missing auto-scroll, no visible slowdown); using a memory profiler or `!gcroot`-style inspection
  if available, confirm old `InstallPage` instances are actually collectible after navigating away
  (not rooted via `InstallPageViewModel.PropertyChanged`'s invocation list) - or, short of a
  profiler, confirm by code inspection that `SubscribeToViewModel` is only ever called from
  `OnLoaded`/`OnUnloaded`/`OnDataContextChanged` (guarded by `_isLoaded`), and that `OnUnloaded`
  unconditionally clears the subscription;
* confirm auto-scroll-to-end still works correctly after multiple navigations away and back (the
  detail log for a fresh "Preparing WinGet" load still scrolls to its latest line).

## Test 22b - Missing WinGet entirely

If practical (a machine/VM with `winget` not on PATH or App Installer not present at all), confirm
`winget --version` fails/is not found as a starting condition, then:
1. navigate to Install page in LIVE mode - **expected:** the `PreparingPrerequisite` full-page
   state appears immediately; the version check reports "could not be run";
2. repair is attempted automatically (`Install-PackageProvider`/`Install-Module -Name
   Microsoft.WinGet.Client`/`Repair-WinGetPackageManager -Force -Latest`), with progress text
   updating in the same centered panel (not the footer);
3. `winget --version`, the `--disable-interactivity` capability check, and the `winget source
   list` health probe are all re-run and must all pass before the page moves on;
4. only then does the page switch to the `LoadingCatalog` state ("Checking available
   applications...") and the catalog appears (`CatalogReady`);
5. afterwards, independently running `winget --version` from a terminal on the same machine must
   now succeed (confirms the repair genuinely fixed the system-wide WinGet installation, not just
   something Support Tool's own process sees);
6. log clearly shows the missing-WinGet diagnosis distinct from a crash/ancient-version diagnosis.

## Test 22c - Repair failure: centered failure state with Retry, no CLIXML

* force the repair script to fail (no network access to PSGallery, or another induced failure)
  while WinGet is broken/ancient;
* **Expected:**
  * the page reaches the dedicated `LoadFailed` full-page state - centered icon, heading
    "WinGet could not be prepared", and a clean human-readable reason underneath (the repair
    script's own plain-text stderr line, e.g. from its `catch { [Console]::Error.WriteLine(...) }`
    - never a raw `#< CLIXML ...` block or any other serialized PowerShell object);
  * a **Retry** button is visible and is the only action offered;
  * no install batch runs; no app catalog/cards are shown; **no footer at all** is visible in this
    state (not just an empty one - `IsCatalogReady`-gated visibility hides it entirely);
  * `HasLoaded` remains `false`;
  * clicking **Retry** genuinely re-runs the whole check→repair→verify sequence from scratch (not
    a cached/stale result) - confirm via logs that a new `winget --version`/repair attempt actually
    executes; if the injected fault is then removed, Retry succeeds and reaches `CatalogReady`
    normally;
  * the log captures the actual repair failure detail (PowerShell exit code / clean stderr line),
    not just "failed", and still contains no raw CLIXML either.

## Test 22d - DEBUG Dry Run never mutates WinGet

* in a **DEBUG** build with Dry Run left at its default (enabled), on a machine with a
  broken/ancient WinGet, navigate to Install;
* **Expected:** `WingetPrerequisiteService` diagnoses (logs version/capability/health) but
  **never** runs `Install-PackageProvider`/`Install-Module`/`Repair-WinGetPackageManager` - no
  PowerShell repair process is launched at all; if WinGet is unhealthy, the page reports that
  diagnostically (clean failure message, empty catalog) rather than silently repairing the test
  machine out from under the tester; the existing `DryRunInstallExecutor` remains untouched and
  non-mutating regardless;
* repeat with Dry Run explicitly toggled OFF in the same DEBUG build (Settings' DEBUG-only
  toggle) - **expected:** repair now runs, exactly as in Test 22, since readiness gating is keyed
  on `ViewState.IsLiveMode` (the actual execution mode), not the build configuration.

## Test 22e - Reboot interlock during WinGet repair

**Setup:** Windows Update running in the background (or with `RebootRequired` reached), while a
WinGet repair is deliberately slowed or kept running (e.g. throttle network so
`Install-Module`/`Repair-WinGetPackageManager` takes noticeably long).

**Expected:**
* while the repair's `SharedViewState.EnterToolkitOperation()` lease is held, if Windows Update
  reaches `RebootRequired` during that window: no automatic reboot, no countdown - the reboot
  remains safely deferred exactly as in Test 18, with the same footer/page messaging
  ("waiting for current operation to finish");
* once the repair operation completes (success or failure) and releases its lease, the deferred
  reboot behaves exactly as Test 18 describes (`Reboot now` becomes usable, no surprise automatic
  countdown);
* confirm via code inspection/logs that this used the generic `IsToolkitOperationActive` lease,
  never `IsWuaOperationActive`, and that the existing reboot lifecycle semantics (persist → verify
  task → countdown-or-defer) were not altered by this pass.

## Test 22f - Regression: an ordinary (non-crash) WinGet health failure must also mean NOT READY

**Background:** an earlier cut of `CheckHealthAsync` only failed the health probe on a
crash-shaped exit code, treating every other non-zero `winget source list` result as healthy. That
undermines the whole prerequisite: an ordinary (non-crash) failure of `source list` is still not a
sane WinGet state for this application - if it were accepted as "Ready", `WingetStatusChecker`'s
own subsequent non-zero results would again silently become false `NotInstalled` catalog entries,
the exact false-catalog problem this service exists to prevent. Fixed: `winget --version`,
`winget install --help`, and `winget source list` all now require a clean `ExitCode == 0` (plus,
for `--version`, actual non-empty version output, and for `install --help`, the flag string
present in the output) before counting as a pass - no code is currently whitelisted as an
acceptable non-zero result for any of the three checks. This is explicitly distinct from Test 22's
native-crash case.

**Test:** fault-inject `winget source list` (or `--version`/`install --help`) to return an
ordinary non-zero exit code that is **not** in the NTSTATUS crash-exception range (e.g. exit code
`1`) - not a crash, just a normal command failure - while `--version`/`install --help` (whichever
isn't the one being fault-injected) still succeed normally.

**Expected:**
* the prerequisite result is `NOT READY` for exactly the failing check - the log clearly
  distinguishes this from a crash-shaped exit (no "crashed" wording, includes the actual exit code
  and any stdout/stderr captured);
* in LIVE mode, repair is attempted automatically, exactly as it would be for a crash or a missing
  `--disable-interactivity` capability;
* catalog/status enumeration (`WingetStatusChecker.GetStateAsync` calls) does **not** run before
  readiness succeeds - confirm via logs/process monitoring that no `winget list` calls happen
  while the prerequisite is still unready;
* if repair resolves the injected failure, post-repair verification passes and the page loads
  normally; if the fault stays injected, the page shows one clear prerequisite-failure message
  with no false `NotInstalled` catalog and no install batch.

## Observed servicing anomaly (Windows Update) - not addressed in this pass

During the same old-22H2 VM testing session used for Test 22, Windows temporarily displayed an
old/Windows-10-looking Explorer shell during cumulative servicing, and a later WUA re-check then
requested another reboot. This is recorded here as an **observed anomaly to watch for**, not
something this pass investigates or fixes - no WUA COM/STA/reboot-detection code was changed
based on this single incident. If it recurs on a future test pass, capture: what the shell looked
like, whether Windows Update's own status/log showed anything unusual at that point, and whether
the "another reboot requested" was itself a legitimate multi-stage servicing requirement (cumulative
updates routinely need more than one reboot) versus a genuine detection bug - only escalate to a
code change if a repeatable, clearly-wrong pattern emerges.

## Test 23 - Game Launchers "Select all": Battle.net install-location fix

**Background:** manual acceptance testing selected all of Game Launchers and installing Battle.net
failed with `winget exited with code -1978335137` (0x8A15005F,
`APPINSTALLER_CLI_ERROR_INSTALL_LOCATION_REQUIRED`) - Blizzard.BattleNet's winget manifest requires
an explicit install location. Fixed generically, not with a Battle.net-specific if-statement:
`Toolkit.Shared.Configuration.InstallableApp` gained an optional `InstallLocation` property; when
set, `RealInstallExecutor` expands any environment variables in it
(`Environment.ExpandEnvironmentVariables`) and passes `--location <expanded path>` via
`ProcessStartInfo.ArgumentList` (not a manually-quoted string, so a path containing spaces is
handled correctly). Every existing catalog entry has `InstallLocation` null/empty and sends the
exact same command as before. The repo/dev default catalog
(`ConfigService.CreateDefaultConfig()`) now sets Battle.net's `InstallLocation` to
`%PROGRAMFILES(X86)%\Battle.net` (winget's own documented default for this package) -
**the technician USB's actual portable `config.json` still needs the same
`"InstallLocation": "%PROGRAMFILES(X86)%\\Battle.net"` addition to its Battle.net entry - this was
deliberately NOT applied to `X:\` during this external-review pass and must be done as part of the
next approved USB refresh.**

**Test:**
1. on a machine with the corrected portable `config.json` staged (once the USB has actually been
   updated - see above), navigate to Install, expand "Game Launchers", click "Select all";
2. click "Install Selected" (no confirmation dialog, per the earlier fix) in LIVE mode;
3. **Expected:** Battle.net installs successfully - no `0x8A15005F`/`-1978335137` failure; other
   launchers (Steam, Epic Games Launcher, GOG Galaxy, Ubisoft Connect, EA app, Rockstar Games
   Launcher) are unaffected - confirm via log that their winget commands are byte-for-byte
   unchanged (no `--location` argument appears for any of them);
4. separately, force some other single item in the same batch to fail (e.g. temporarily break its
   `WingetId`) - **expected:** the batch continues processing the remaining items rather than
   stopping, and the failed item still advances the batch's completed count (see Test 24).

## Test 23b - Cleaner install failure text

**Background:** the raw fallback `winget exited with code -1978335137` is poor technician UX.
`RealInstallExecutor` now prefers a cleaned, human-readable explanation: it inspects `stderr` first
(stripping blank lines and lines that are mostly progress-bar/spinner glyph noise, keeping up to
the last 5 meaningful lines), falls back to cleaned `stdout` if `stderr` had nothing meaningful, and
only falls back to the bare numeric exit code if neither has any real text. No hardcoded table of
known winget error codes/messages was introduced.

**Test:** force a real winget failure with actual explanatory output on stderr and/or stdout (e.g.
an invalid `--location` value, or a genuinely unavailable package) and confirm the Install Complete
summary dialog shows the real explanatory text, not a bare `winget exited with code ...` line,
and never shows raw progress-bar/spinner glyph garbage.

## Test 24 - Real determinate install progress (no more indeterminate racing bar)

**Background:** the Install page's active-install progress bar was a purely animated indeterminate
bar with no relationship to actual progress. `InstallService.InstallAsync` now reports real
`InstallBatchProgress` (Completed/Total/CurrentItemName - an honest, known-up-front
numerator/denominator, never an invented percentage) once before each item starts and once more
after the last item finishes; `InstallPageViewModel` exposes `CompletedItems`/`TotalItems`/
`CurrentItemName`/`InstallProgressPercent`/`InstallProgressText`, and both the Install page's own
footer and the new persistent cross-page Install footer bind to the same state via a determinate
`ProgressBar` (`Minimum=0 Maximum=100`).

**Test:** select exactly 7 apps and click "Install Selected" (LIVE or Dry Run):
* **Expected:** progress starts at 0/7 (0%); after the first item finishes (success or failure),
  progress shows 1/7 (~14.3%); it advances by exactly one step per processed item regardless of
  whether that item succeeded or failed; after the 7th item finishes, progress reaches 7/7 (100%)
  - confirm the bar visually reaches full width, not just close to it;
* the progress text reads "Installing `<current app>` - N of 7 complete" while an item is actively
  installing, and "7 of 7 complete" once the whole batch has finished;
* repeat in **Dry Run** and confirm the exact same deterministic 0/7 → 1/7 → ... → 7/7 progression
  (`DryRunInstallExecutor` still simulates each item, but batch progression is identical since it
  comes from `InstallService`'s own loop, not the executor);
* confirm no percentage is ever invented for the individual package download/install itself (only
  the batch-level completed/total ratio is shown) - this deliberately does NOT attempt
  per-package percentages.

## Test 25 - Persistent cross-page Install footer

**Background:** mirrors the existing Windows Update persistent footer. While an install batch is
active, a compact footer now follows the technician to any other page (`MainViewModel.
ShowInstallFooter`), binding into the same long-lived `InstallPageViewModel` the full page uses -
no second install engine, no polling.

**Test:** start a real (or Dry Run, for a controllable duration) install batch of several apps,
then:
1. navigate to Home, then Debloat (Apps), then Settings while the batch is still running -
   **expected:** a persistent "Installing applications" footer is visible on each of these pages,
   showing the live current app name and completed/total count, updating in real time as the batch
   progresses (not a static snapshot from when you navigated away);
2. navigate back to the Install page itself - **expected:** the compact cross-page footer is
   **hidden** (no duplicate progress UI) because the full Install page's own progress panel is
   now visible instead;
3. confirm the install itself was never interrupted, restarted, or lost track of by any of this
   navigation - the batch is the same one long-lived operation throughout;
4. once the batch completes: the real progress bar reaches 100% first, then the normal result
   summary dialog appears, then the persistent footer disappears (on whichever page you're on),
   and the catalog refreshes as before.

## Test 26 - Windows Update and Install active simultaneously

**Background:** WU and an install batch can legitimately run at the same time - the two persistent
footers are independent and `MainWindow.xaml` stacks them (a `StackPanel` wrapping both, each
contributing zero height while its own `Visibility` is `Collapsed`) rather than one replacing the
other.

**Test:** get Windows Update into an active/needs-attention state (e.g. still running, or a
deferred reboot) and, separately, start an install batch, then navigate to a third page (e.g.
Settings):
* **Expected:** both the Windows Update footer AND the Install footer are visible at the same
  time, stacked (not overlapping, not visually corrupting each other);
* each shows its own correct live state independently;
* if Windows Update reaches `RebootRequired` while the install batch's toolkit-operation lease is
  still held, the reboot remains safely deferred exactly as in Test 18 (no automatic reboot/
  countdown while the install is active) - confirm the existing generic
  `SharedViewState.EnterToolkitOperation()` lease (held for the whole install batch, unchanged
  from before this pass) is what's actually providing this protection, not `IsWuaOperationActive`;
* once the install batch finishes, if Windows Update was still waiting on the deferred reboot, it
  behaves exactly as Test 18 describes from that point (`Reboot now` becomes usable, etc.).

## Test 27 - Real determinate uninstall/debloat progress (Apps and Programs)

**Background:** the Apps (Debloater) and Programs & Features pages' active-uninstall progress bars
were purely animated indeterminate bars, same issue as pre-Test-24 Install. `UninstallService.
UninstallAsync` (shared by both pages, and by Debloat) now reports a real `UninstallBatchProgress`
(Completed/Total/CurrentItemName - an honest, known-up-front numerator/denominator, never an
invented percentage) once before each item starts and once more after the last item finishes.
`InstalledItemsPageViewModel` (the shared abstract base for both `DebloaterPageViewModel` and
`ProgramsPageViewModel`) exposes `CompletedItems`/`TotalItems`/`CurrentItemName`/
`UninstallProgressPercent`/`UninstallProgressText` from this one place, so both pages get identical
behavior for free. `DebloaterPage.xaml` and `ProgramsPage.xaml`'s active-uninstall footers now bind
to this state via a determinate `ProgressBar` (`Minimum=0 Maximum=100`); their separate "Loading
installed apps…"/"Loading installed programs…" overlay bars are unchanged and remain indeterminate
(a discovery/enumeration scan has no trustworthy numerator/denominator up front).

**Test (repeat once for Apps/Debloater, once for Programs):** select several items (e.g. 5) and
click "Uninstall Selected" (LIVE or Dry Run):
* **Expected:** progress starts at 0/5 (0%); after the first item finishes, progress shows 1/5
  (20%); it advances by exactly one step per processed item regardless of whether that item
  succeeded, failed, was skipped, or was merely started (uninstaller launched, not yet confirmed);
  after the 5th item finishes, progress reaches 5/5 (100%) - confirm the bar visually reaches full
  width, not just close to it;
* the progress text reads "Removing `<current item>` - N of 5 complete" while an item is actively
  being processed, and "5 of 5 complete" once the whole batch has finished;
* mix in at least one item expected to fail and one expected to be skipped - confirm both still
  advance the completed count exactly like a success;
* start a batch and cancel partway through (if a cancellation path is exposed) - **expected:** the
  bar never jumps to 100% as a result of the cancellation; whatever count had genuinely completed
  before the cancel is the last value shown;
* repeat in **Dry Run** and confirm the exact same deterministic 0/5 → 1/5 → ... → 5/5 progression;
* confirm the "Loading installed apps…"/"Loading installed programs…" overlay bar shown while the
  page's item list is (re)loading is still indeterminate, both before the uninstall (initial page
  load) and after it (the automatic reload once the batch finishes) - this was deliberately left
  unchanged;
* confirm no indeterminate racing bar remains at any point during the active-uninstall phase itself
  for either page.

## Test 28 - Battle.net install-location: config-chain diagnostic (real fix, not a hack)

**Background:** manual acceptance still reproduced the `0x8A15005F`/
`APPINSTALLER_CLI_ERROR_INSTALL_LOCATION_REQUIRED` failure after Test 23's fix landed. The
generic executor support (`InstallableApp.InstallLocation` -> `Environment.ExpandEnvironmentVariables`
-> `--location` via `ProcessStartInfo.ArgumentList`) was already correct and is **unchanged** -
confirmed by re-reading `RealInstallExecutor.InstallAsync`, `ConfigService`, `InstallCatalogService`,
and the shared `Toolkit.Shared.Configuration` DTOs end to end: `InstallLocation` is a plain
nullable string property with no custom JSON converter anywhere in the chain, so
`System.Text.Json` deserializes/passes it through unchanged from `config.json` all the way to
`RealInstallExecutor`. The real cause is staleness, not a code bug:

```
X:\Elkjop Toolkit\config.json                          (canonical, on the USB)
    | staged by Account Setup / service.exe
    v
C:\ProgramData\Elkjop\Toolkit\Runtime\config.json       (what a running Support Tool actually reads)
    v
Support Tool (ConfigService.LoadAsync)
```

`ConfigService.CreateDefaultConfig()` (the in-repo default, used only when generating a brand new
config from scratch) has carried
`InstallLocation = "%PROGRAMFILES(X86)%\\Battle.net"` on the Battle.net entry since Test 23 - but
an **already-deployed** `config.json` is never regenerated or merged against that default
(`ConfigService.LoadAsync` only ever deserializes whatever bytes are already on disk). A USB
config.json created/last-saved before Test 23 landed - or a Runtime copy staged from it - simply
has no `InstallLocation` key for Battle.net at all, and deserializes that property back to `null`
exactly as if it had never been added. **A running/staged Support Tool never re-reads the USB
copy on its own** - only a fresh staging pass (or a manual edit of the Runtime copy) can update
what it actually sees.

**Diagnostic - check BOTH files, not just one:**
1. Open the canonical USB copy, `X:\Elkjop Toolkit\config.json`, and confirm the Battle.net entry
   under `Installer.Categories["Game Launchers"].Apps` has
   `"InstallLocation": "%PROGRAMFILES(X86)%\\Battle.net"`. If it's missing, the USB itself predates
   Test 23 and needs updating (see rollout step below) - this is data, not something any code
   change here fixes automatically.
2. Separately open the staged copy on whatever machine/VM is under test,
   `C:\ProgramData\Elkjop\Toolkit\Runtime\config.json`, and check the exact same field. **This can
   be stale even after the USB is fixed** - a Runtime copy is only written once, at staging time;
   fixing the USB does nothing for a machine already staged from the old one.

**For the eventual approved USB refresh** (not part of this code change - X:\ is deliberately not
touched until external review, per standing instructions): the actual canonical
`X:\Elkjop Toolkit\config.json` must gain the `InstallLocation` field on its Battle.net entry
before re-testing on real hardware. If that file was hand-migrated/edited independently of
`ConfigService.CreateDefaultConfig()`'s literal text at some point, diff the two rather than
assuming they match.

**For a VM/machine already staged** (Runtime\config.json already exists there from an old USB):
either of the following gets a valid retest, they are not both required:
* manually add the same `InstallLocation` field to that machine's
  `C:\ProgramData\Elkjop\Toolkit\Runtime\config.json` for an immediate retest without restaging, or
* run the VM through a fresh staging/setup cycle (re-run Account Setup/`service.exe` staging
  against a USB that already has the fix) so the Runtime copy is regenerated from a corrected
  source.

**Once both files genuinely have the field, run Game Launchers -> Select all** (LIVE mode) and
confirm:
* the winget command actually invoked for Battle.net includes `--location` with the fully
  expanded path (check Verbose.log for the exact argument list logged by `InstallLogger`) - not
  just that install "worked", since a silently-skipped `--location` would still coincidentally
  succeed if Battle.net happened to already be installed;
* no `0x8A15005F`/`-1978335137` failure occurs;
* other Game Launchers entries (Steam, Epic, GOG, Ubisoft Connect, EA app, Rockstar) are
  unaffected - they still have no `InstallLocation` and get the exact same command as before any
  of this work.

**Generic HRESULT handling (new in this pass, not Battle.net-specific):** separately, with a
config that still lacks `InstallLocation` for some entry (simulating a not-yet-updated
Runtime copy), attempt to install that entry and confirm the result dialog shows the concise,
actionable text `"<App name> requires an install location, but none is configured."` instead of
the raw HRESULT or a wall of winget boilerplate - `RealInstallExecutor` now recognizes
`APPINSTALLER_CLI_ERROR_INSTALL_LOCATION_REQUIRED` (`0x8A15005F`) generically (by exit code, for
any app whose `InstallLocation` is empty), not via a Battle.net-specific if-statement - this
message would appear for any future catalog entry with the same problem, not just Battle.net.

**Boilerplate filtering (new in this pass):** for a genuinely unknown/different install failure,
confirm the result dialog no longer includes winget's standard
`Found <App> [<id>] Version <x>` / `This application is licensed to you by its owner.` /
`Microsoft is not responsible for...` lines crowding out the real error - `RealInstallExecutor`
now drops these specific known boilerplate lines before picking the last few meaningful lines,
on top of the existing progress-bar/spinner-glyph filtering. Full raw output (including the
boilerplate) is still always in `Verbose.log` regardless of what the dialog shows.

## Test 29 - Install progress: blended current-item percentage (no more once-per-app jumps)

**Background:** the Install page's batch progress previously only advanced once an entire app
finished (`5/10 = 50%` -> app #6 finishes -> `6/10 = 60%`), with nothing moving while app #6 was
actually downloading/installing. `RealInstallExecutor` now runs its own live-streaming winget
process (bypassing the buffered `IProcessRunner`) and parses real, trustworthy numeric progress
out of winget's console output - an explicit `NN%` in the text, a `<downloaded> / <total>` byte
ratio, or winget's own ConEmu/Windows Terminal taskbar-progress OSC sequence (`]9;4;<state>;<percent>`)
when present - **never** the visual width of its Unicode block-character progress bar and never an
elapsed-time estimate. `InstallService.InstallBatchProgress` gained a `CurrentItemPercent` field
carrying this; `InstallPageViewModel.InstallProgressPercent` now blends it in:

```
overall = (CompletedItems + CurrentItemPercent / 100.0) / TotalItems * 100
```

**Test:** select 10 apps (or use DEBUG instrumentation to control a single app's reported
percentages) and install (LIVE, so real winget output exists to parse):
* with 5 apps already fully completed and the 6th app's real progress at each of the following
  points, confirm the overall bar/text read exactly:

  | 6th app's own progress | Overall |
  |---|---|
  | 0% | 50% |
  | 25% | 52.5% |
  | 50% | 55% |
  | 70% | 57% |
  | 100% (just before it's marked complete) | 60% |
  | (6th app completes; 7th app begins at 0%) | 60% - no backward jump |

* confirm the overall percentage never moves backwards within one app's run even if winget's
  output momentarily reports a lower-looking number than a previous line (the executor retains
  the highest trustworthy value seen for the current item);
* confirm that for an app/phase where winget prints no trustworthy numeric signal at all (e.g. a
  brief verification step with only spinner glyphs), the bar holds its last real value rather than
  freezing at 0% or jumping arbitrarily - it should never visibly reverse or teleport;
* repeat in **Dry Run** and confirm the *old* once-per-app-only stepping still applies exactly as
  before (0/10 -> 1/10 -> ... -> 10/10) - Dry Run deliberately never simulates intra-item
  percentages (see `DryRunInstallExecutor`'s own remarks), so `CurrentItemPercent` stays 0
  throughout each simulated item and only the batch-level step moves.

## Test 30 - Cross-page Install footer shows the same blended value

**Background:** both the full Install page's own progress bar and the persistent cross-page
footer (`MainWindow.xaml`) bind to the exact same `InstallPageViewModel.InstallProgressPercent`/
`InstallProgressText` - there is only one blended-progress calculation, never a second one that
could drift out of sync.

**Test:** start a real LIVE install batch, then navigate to Home/Debloat/Settings while it's
running:
* confirm the persistent footer's progress bar and the full page's own bar (when you navigate
  back to Install) always show the identical percentage at any given moment - not a stale
  snapshot, not a differently-rounded number;
* confirm this holds throughout an app's intra-item progress climbing (e.g. both show 57% at the
  same instant), not just at the once-per-app boundaries.

## Test 31 - Windows Update: two-phase aggregate math for a single update

**Background:** the Windows Update page already receives real per-update `State`/`PercentComplete`
rows. `WindowsUpdatePageViewModel` now builds one real aggregate
`OverallWindowsUpdateProgressPercent` from them, treating each update as two equal work units
(download, then install):

```
Pending -> 0.0   Downloading p% -> p/100   Downloaded -> 1.0
Installing p% -> 1.0 + p/100   Installed/PendingRestart -> 2.0
overall = sum(contributions) / (2 * updateCount) * 100
```

**Test:** with exactly one pending update (or DEBUG instrumentation forcing single-update state
transitions), confirm:

| State | Overall |
|---|---|
| Downloading 0% | 0% |
| Downloading 50% | 25% |
| Downloading 100% | 50% |
| Installing 0% | 50% - **no drop from the 50% just above** |
| Installing 50% | 75% |
| Installing 100% / Installed | 100% |

The Downloaded(100%) -> Installing(0%) transition is the specific case to watch closely - confirm
the overall bar visibly holds flat at 50% across that transition rather than dipping.

## Test 32 - Windows Update: aggregate across multiple updates

**Background:** the same formula sums contributions across every update in the current pass, not
just one.

**Test:** with several (3+) pending updates, let a real pass run and confirm the overall
percentage reflects the sum of all updates' individual two-phase contributions divided by
`2 * updateCount` at every point - e.g. with 4 updates, one fully Installed (contributes 2.0) and
one at Downloading 50% (contributes 0.5) and two still Pending (0.0 each), overall should read
`(2.0 + 0.5 + 0 + 0) / 8 * 100 = 31.25%`. Confirm the number only ever increases as the pass
progresses (aside from the accepted exception in Test 33) and reaches exactly 100% once every
update in the pass is Installed/PendingRestart/Failed.

## Test 33 - Windows Update: a mid-pass newly-discovered update may legitimately lower the aggregate,
and stays part of the pass even once a later snapshot omits it

**Background:** the background availability watcher can append a genuinely new update mid-pass
(`PendingUpdatesTable.MergeNewTitles`) - this enlarges the denominator, and the user has
explicitly accepted that the aggregate percentage may legitimately drop as a result. This is the
one accepted exception to "never go backwards"; the retained-highest-contribution logic per
update still prevents any *other* kind of backward jump. Once a title has been seen anywhere in
the current pass, `WindowsUpdatePageViewModel._updateContributions`' key set - the pass's active
title universe - only ever grows or gets cleared wholesale at a genuine new-pass boundary; a later
narrower snapshot (e.g. the install-phase callback, which only ever contains `toInstall`) can
never silently drop a title out of the denominator just because that one callback doesn't mention
it (see Test 35 for the same guarantee in the download-vs-install case specifically).

**Test:** during an active pass with the aggregate already partway advanced (e.g. 60%), force (or
wait for/simulate) the availability watcher to discover and append a brand new update title (call
it D, joining existing A/B/C):
* confirm D appears in `Rows` at `Pending` (0.0 contribution);
* confirm `OverallWindowsUpdateProgressPercent` recalculates with the larger denominator
  (`2 * 4 = 8` work units instead of `2 * 3 = 6`) and may visibly decrease - this is expected and
  correct, not a bug;
* confirm none of the *already-known* updates' (A/B/C) own retained contributions were reduced by
  this - only the denominator changed;
* continue the pass until a later executor callback reports progress for only A/B/C again (D not
  install-ready yet, or simply not part of that particular callback's subset) - confirm D **remains
  part of the pass universe** at whatever contribution it last had (0.0 if nothing further happened
  to it), the denominator **stays at 8** (not silently dropping back to 6), and the aggregate
  percentage does not jump upward just because D briefly disappeared from one callback's contents.

## Test 34 - Windows Update page/footer visual polish

**Background:** the overall progress bar (both the full page and the persistent footer) now
switches from indeterminate to determinate once real rows exist, and the update table gained
subtle alternating-row shading and vertical column dividers.

**Test:**
* during connectivity check / the update search itself (before any rows exist), confirm the
  overall bar is still the animated indeterminate racer - unchanged from before;
* once real update rows exist, confirm the SAME bar switches to determinate
  (`Minimum=0/Maximum=100`) and visibly tracks `OverallWindowsUpdateProgressPercent` as it climbs,
  on both the full Windows Update page and the persistent cross-page footer - the exact same
  number in both places at any instant;
* in the update table, confirm alternating rows have a subtle (not loud) brightness difference
  using the existing card-surface color, and confirm visible vertical divider lines appear between
  the Update|Progress and Progress|Status columns, with the header row's own dividers lining up
  exactly with the rows below it;
* confirm the existing 10px title breathing room, ellipsis trimming on long update names, header
  alignment, and the existing horizontal row separators are all still intact - this was a visual
  addition, not a layout rewrite.

## Test 35 - Windows Update: the download→install subset must not shrink the denominator

**Background:** `WindowsUpdateExecutor`'s download phase reports progress for the whole download
collection, but its install phase (`toInstall`, per its own remarks) only ever contains the subset
that actually made it to being install-ready - a title that failed to download, or otherwise
didn't qualify, can legitimately be absent from every install-phase callback for the rest of the
pass. That must not make it vanish from the aggregate's denominator: `RecomputeOverallProgress`
only ever adds/updates from what a snapshot contains, it never removes a title from
`_updateContributions` just because the latest snapshot omits it - see its own remarks and
`WindowsUpdateOrchestrator`'s `onNewPassStarting` callback, which is the only thing that clears it.

**Test:** with 4 pending updates (A, B, C, D) where D is expected to fail to download (or is
otherwise excluded from becoming install-ready) - DEBUG instrumentation forcing this is
acceptable if a real reproducible case isn't available:
* during the download phase, confirm all 4 titles appear and the denominator is `2 * 4 = 8` work
  units;
* once the pass transitions into its install phase and the executor's own callbacks now only ever
  contain A/B/C (D excluded), confirm the denominator **remains 8**, not `2 * 3 = 6` - D does not
  silently disappear from the aggregate math merely because the snapshot shape narrowed;
* confirm D's own retained contribution (whatever it last reached during the download phase) is
  still counted in the numerator too - it isn't zeroed out just because it dropped out of view.

## Test 36 - Windows Update: an internal second pass gets a fresh baseline without restarting the run

**Background:** `WindowsUpdateOrchestrator.Run`'s own while-loop can execute multiple full
search/download/install passes inside one `StartAsync` call (each iteration re-searches after a
`DownloadAndInstall` cycle and, if updates remain, loops around into another one). Each of those
re-searches is a genuine new pass and must start the aggregate calculation over from a real fresh
baseline - a title Installed (contributing 2.0) in pass 1 that legitimately shows up Pending again
in pass 2 (e.g. a follow-up update in the same KB family) must not keep silently crediting pass
1's finished work. This reset is driven by the orchestrator's own `onNewPassStarting` callback,
fired at exactly the very first search, and - for every subsequent pass - only at the top of the
loop iteration that actually goes on to call `DownloadAndInstall` again, immediately after that
iteration's own `rebootRequiredBeforePass` check has already passed (a pass-boundary re-search
finding updates remaining and no reboot pending only ever records that a reset is *owed*; see
Test 38 for why the reset itself is deferred to the next iteration rather than fired right there).
It is deliberately never fired when one pass's own download phase transitions into its install
phase, since that's still the same pass (see Test 31's Downloaded->Installing continuity
requirement, which must still hold within a pass even after this fix), and the existing
search/`BuildStatusEntries`/reboot-check WUA call sequence at the pass boundary is completely
unchanged from before `onNewPassStarting` existed - the signal is injected around it, never
interleaved into it.

**Test:** force (via DEBUG instrumentation, or a real machine with enough pending updates to
require two internal passes) a scenario where pass 1 covers titles A and B and finishes (both
reach Installed/PendingRestart, aggregate at 100%), and the loop then finds C and D still/newly
eligible and begins pass 2 without the technician touching "Check for updates" again (i.e. all
within the same `StartAsync` invocation - no page navigation or restart involved):
* confirm the aggregate percentage resets to a fresh 0% baseline at the start of pass 2, not a
  value inflated by A/B's already-finished 2.0 contributions carrying over;
* confirm A and B are no longer part of the denominator in pass 2 (denominator is `2 * 2 = 4` for
  C/D only, not `2 * 4 = 8` still counting the finished pair);
* confirm C and D's contributions in pass 2 reflect their own real, fresh state (e.g. both truly
  start at Pending/0.0) rather than any stale value that might coincidentally have been left over
  from being seen (or not) in pass 1;
* confirm this reset happened without any indeterminate-bar flash or page reload that would
  suggest `StartAsync` itself re-ran - it's an internal orchestrator loop iteration, driven purely
  by the new pass-boundary signal, not a restart of the whole Windows Update check.

## Test 37 - RealInstallExecutor: full raw WinGet output actually reaches Verbose.log

**Background:** `RealInstallExecutor` now logs every stdout/stderr line through the same generic
`Logger`/`ToolkitLogger("Support Tool")` infrastructure already used elsewhere in Support Tool,
tagged per app/command (`[winget install <WingetId>]` / `[winget install <WingetId>:stderr]`),
streamed line by line as winget actually produces it - not reassembled and dumped once at the end.
This lands in the exact same `Verbose.log` (`C:\ProgramData\Elkjop\Toolkit\Support Tool\Verbose.log`)
`InstallLogger`'s own per-item `Action(...)` entries already go to - no second log file.

**Test:** run a real LIVE install (a normal success, and separately one that fails, e.g. the
Battle.net location-required case from Test 28 with a deliberately-unfixed config):
* open `Verbose.log` and confirm a `Starting: winget install/upgrade ...` line appears for the
  attempted app, immediately followed by every real line winget printed for that install, each
  tagged with the same app/WingetId - including the license/boilerplate lines that are
  deliberately filtered out of the result dialog (Test 28's boilerplate-filtering check);
* confirm stderr lines are present too, tagged with the `:stderr` suffix;
* confirm the concise `InstallLogger` summary line (`Attempted to install/update: ... — <outcome>`)
  is still present as before, and is not a second copy of the raw output - it's the same short,
  cleaned outcome text as always;
* confirm the result MessageBox shown to the technician still only ever shows the concise, cleaned
  text (no license boilerplate, no raw progress-bar noise) - the full raw log is for diagnosis, not
  for the UI;
* with several apps installing in the same batch, confirm the per-app tag makes it possible to
  tell each app's interleaved output apart in the log.

## Test 38 - Windows Update: no reset fires for a pass that never actually starts (both failure modes)

**Background:** a pass-boundary re-search finding updates remaining and no reboot pending is
necessary but not sufficient proof that another pass will actually run - two different things can
still prevent the next loop iteration from ever reaching `DownloadAndInstall`:

1. the `while` loop's own deadline check can fail (the no-progress backoff sleep,
   `NoProgressBackoff` - 5 minutes in RELEASE - can itself run out the run's overall deadline);
2. the next iteration's own `rebootRequiredBeforePass` check can discover a reboot became required
   in the meantime and `break` immediately.

Two earlier drafts each caught only one of these. The fix records "a reset is owed"
(`passResetPending`) at the pass-boundary re-search instead of firing immediately, and only
actually invokes `onNewPassStarting` at the top of the *next* loop iteration, immediately after
that iteration's existing `rebootRequiredBeforePass` check has already passed - the earliest point
that genuinely guarantees `DownloadAndInstall` is about to run. If the next iteration is never
entered at all (case 1) or breaks before reaching that point (case 2), `passResetPending` is simply
never consumed and no reset ever fires. No extra WUA/reboot COM call was added anywhere to support
this, and the search/`BuildStatusEntries`/`table.Replace`/post-pass-reboot-check ordering is
byte-identical to before `onNewPassStarting` existed.

**Test (both cases, using DEBUG's shortened `RunDeadline`/`NoProgressBackoff` - 2 minutes / 10
seconds - to make forcing this practical):**

**Case 1 - deadline expires before the next iteration:**
* arrange a pass-boundary re-search that finds updates remaining, no reboot pending, and
  `AnyProgressMade == false` (nothing downloaded/installed that pass), timed so the backoff sleep
  pushes past `RunDeadline`;
* confirm the run ends (`StillPending`, most likely) without the aggregate/`Rows` having been
  wiped to a fresh-baseline 0% state right before ending - whatever the last real state was stays
  displayed.

**Case 2 - the next iteration begins but its own `rebootRequiredBeforePass` check aborts it:**
* arrange a pass-boundary re-search that finds updates remaining and no reboot pending (so
  `passResetPending` is set), with enough deadline remaining that the next iteration *is* entered,
  but force a reboot to become required right at (or just before) that next iteration's own
  `rebootRequiredBeforePass` check (DEBUG instrumentation forcing this is acceptable if a real
  reproducible timing window isn't available);
* confirm the loop breaks via that check exactly as before this fix existed, `DownloadAndInstall`
  is never called again, and the aggregate was **not** reset for that never-started pass - whatever
  the last real state was (from the pass that just finished) stays displayed until the run's
  terminal `RebootRequired` state takes over.

**Both cases, for contrast - confirm the positive case still works:** arrange the same
pass-boundary re-search (updates remaining, no reboot pending) with enough deadline remaining and
no reboot becoming required, so the next iteration's `rebootRequiredBeforePass` check passes
cleanly - confirm `onNewPassStarting` *does* fire at that point (immediately before
`DownloadAndInstall` runs again) and the aggregate genuinely resets to a fresh baseline for the new
pass, per Test 36.

## Test 39 - Verbose.log: concurrent writers do not lose lines

**Background:** `ToolkitLogger.Append` previously called `File.AppendAllText` with no
synchronization at all - multiple independent `ToolkitLogger` instances (one each inside `Logger`,
`InstallLogger`, `UninstallLogger`, and now `RealInstallExecutor`'s own per-line streaming logging
from Test 37) can all resolve to the exact same `Verbose.log` path and be called from different
threads at the same instant (a winget process's async stdout *and* stderr callbacks alone are two
separate threads; add Windows Update's own STA worker/watcher threads logging concurrently and the
collision risk is real). `Append` now serializes writes with a lock keyed by the resolved log file
path (shared across every `ToolkitLogger` instance targeting that same path, not a per-instance or
per-caller lock), so genuinely concurrent writers can no longer interleave or silently drop lines
against each other, while writes to unrelated files never contend.

**Test:**
* start a real LIVE install batch of several apps (so `RealInstallExecutor` is streaming stdout
  *and* stderr concurrently for each app) at the same time as an active Windows Update run (Test 26's
  scenario) - both log to the same `Verbose.log`;
* let the batch run to completion, then inspect `Verbose.log` line by line: confirm no lines are
  garbled/interleaved mid-line (each logged line should be a complete, single, readable entry - not
  two lines spliced together), and confirm no gaps where an expected sequence of lines (e.g. a
  `Starting: winget ...` line immediately followed by that install's own output) is missing an
  entry;
* for one install specifically, count the real lines winget actually printed (cross-check against
  what a manual `winget install` run for the same package produces) and confirm the same count of
  tagged lines appears in `Verbose.log` for that app - no silently dropped lines;
* confirm `Minimal.log` (written by `InstallLogger`/`UninstallLogger`'s own `Action(...)` calls,
  and Windows Update's task-lifecycle logging) is similarly intact - no half-written or missing
  blocks - under the same concurrent load;
* confirm this holds across a normal Debug run and, if practical, a stress pass with several
  install batches or repeated Windows Update checks run back to back, to increase the chance of
  catching a genuine race if the lock were somehow ineffective.

## Test 40 - Windows Update: terminal outcome race between the final search and reboot status

**Background:** manual acceptance repeatedly observed a real race: Windows Update finished with
"You are up-to-date", the technician clicked "Check again", and immediately got "A restart is
required" - meaning the *previous* run's terminal decision was already wrong by the time it was
made. `Run`'s terminal block took its authoritative reboot-status check BEFORE the final `Search()`
- but `Search()` itself can take real time, and Windows servicing can flip reboot-required to true
purely as a side effect of finishing while that search is still running. The old sequence
(`reboot=false` → final search begins → servicing finishes and sets reboot-required → final search
returns 0 → returns `FullyUpToDate`) let a genuinely-reboot-required state get reported as fully
up to date.

The terminal predicate is now:

```
FullyUpToDate = fresh final search == 0 eligible updates
                AND fresh reboot-status check taken AFTER that final search == false
```

The cheap early reboot check right after the pass loop ends is kept as a short-circuit (skips the
final search outright when a reboot is already known), but it is no longer authoritative on its
own - the final `Search()` always runs, and a **second**, authoritative `IsRebootRequired()` check
always runs immediately after it, before `FullyUpToDate`/`StillPending` is decided.
`RebootRequired` takes precedence even if the final search still shows updates remaining (a reboot
becoming required mid-search doesn't un-discover those updates, and nothing further can happen
until the restart regardless).

**Test - the 4 outcome combinations** (DEBUG instrumentation forcing the WUA reboot-status/search
results independently is acceptable and likely necessary to hit combinations 1 and 3 reliably on
demand; combination 1 specifically is the real repro from manual acceptance and is the one that
matters most):

| # | Pre-final reboot status | Final search result | Post-final reboot status | Expected outcome |
|---|---|---|---|---|
| 1 | false | 0 eligible | **true** | `RebootRequired` - **never** `FullyUpToDate` |
| 2 | false | 0 eligible | false | `FullyUpToDate` |
| 3 | false | >0 eligible | **true** | `RebootRequired` (precedence over remaining updates) |
| 4 | false | >0 eligible | false | `StillPending` |

For each: confirm the reported outcome matches exactly, confirm the early short-circuit reboot
check does **not** itself decide the outcome (it only skips the final search when already true,
which isn't exercised by any of these 4 - all start with pre-final reboot `false` specifically to
force the final search to actually run), and confirm the final search itself always executes for
all 4 cases (never skipped).

**Test - the real-world repro, end to end:** run Windows Update to a genuine `FullyUpToDate`
result, then immediately click "Check again":
* confirm the *previous* run's `FullyUpToDate` conclusion was correct at the moment it was made -
  i.e. this immediate re-check does **not** suddenly reveal a reboot that the previous terminal
  decision should already have caught (case 1 above is exactly the scenario that made this
  possible before this fix);
* if Windows Update servicing genuinely completes and sets reboot-required in the narrow window
  between one run ending and the next "Check again" being clicked (a real, if rare, timing case
  this fix cannot and isn't meant to prevent - it only fixes the race *within* a single run's own
  terminal decision), that's expected and correct, not a regression - the point of this test is
  that the *same* run's own final search and reboot check must agree with each other, not that two
  separate, sequential runs are guaranteed to see identical state.

---

Manual tests only - these were prepared and documented, not executed, from the development
environment (no disposable OOBE VM available there). Do not treat any of the above as verified
until actually run.
