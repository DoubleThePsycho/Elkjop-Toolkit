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

---

Manual tests only - these were prepared and documented, not executed, from the development
environment (no disposable OOBE VM available there). Do not treat any of the above as verified
until actually run.
