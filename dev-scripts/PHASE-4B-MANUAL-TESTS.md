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

Manual tests only - these were prepared and documented, not executed, from the development
environment (no disposable OOBE VM available there). Do not treat any of the above as verified
until actually run.
