# Phase 2B manual staged-runtime tests

Dev/test documentation only - not a shipping architecture component. Exercises the
session/reboot/runtime lifecycle Phase 2B adds to Support Tool, using `Stage-TestRuntime.ps1`
to manually stage `C:\ProgramData\Elkjop\Toolkit\Runtime\` directly, bypassing whichever real
launcher would normally do it.

**Phase 3 update:** `service.exe` (Service Launcher) now exists and is the real Service entry
point - see `dev-scripts/PHASE-3-MANUAL-TESTS.md`. For a plain "fresh Service" or "Service
reboot-resume" scenario, prefer running the real `service.exe` (Tests 1 and 4 there) over Test A/B
below. This script remains the right tool for states `service.exe` has no way to produce itself -
targeted fault injection (a corrupt/semantically-invalid `session.json`, a Setup session to probe
Support Tool's own startup navigation in isolation, etc.) - and for testing Support Tool's own
lifecycle in isolation from the staging/classification logic Phase 3 added. Account Setup staging
(Phase 4) still doesn't exist, so Setup scenarios below are unchanged.

Run everything on a disposable VM/snapshot - these tests create a real scheduled task, and (in
RELEASE) trigger a real reboot.

## Setup

1. Publish Support Tool (Release, matching arch) and locate an existing staged `config.json`
   (e.g. the deployed USB copy).
2. Take a VM snapshot before each test below, or re-run with `-Clean` between tests.
3. Elevated PowerShell is required (schtasks/rmdir under `C:\ProgramData` and the scheduled task
   itself both need it).

## Test A - Service fresh

```powershell
.\Stage-TestRuntime.ps1 -SourceExePath <exe> -SourceConfigPath <config> `
    -Workflow Service -Stage WindowsUpdate -Clean
```

Launch `Runtime\SupportTool.exe`.

**Expected:** normal Service/Home entry. Windows Update does not auto-run.

## Test B - Service reboot resume

```powershell
.\Stage-TestRuntime.ps1 -SourceExePath <exe> -SourceConfigPath <config> `
    -Workflow Service -Stage WindowsUpdate -Clean
```

Launch, navigate to Windows Update, click "Check for updates" and let it reach
`RebootRequired` (or force this in DEBUG by observing the DryRun executor's outcome).

**Expected before reboot:** `session.json`'s `ResumeRequired` is `true`, `Elkjop Toolkit Resume`
exists (`schtasks /query /tn "Elkjop Toolkit Resume"`), and (RELEASE) the reboot only happens
after both are confirmed.

**After reboot/logon:** Support Tool launches via the task, returns directly to Windows Update.
Once it reaches terminal completion, `ResumeRequired` clears, the task is deleted, and it
returns to the Service page.

## Test C - Setup normal mandatory WU

```powershell
.\Stage-TestRuntime.ps1 -SourceExePath <exe> -SourceConfigPath <config> `
    -Workflow Setup -Stage WindowsUpdate -FirstDesktopActionsCompleted:$false `
    -CreateInitialTask -Clean
```

Launch `Runtime\SupportTool.exe`.

**Expected:** the first-desktop action (Start menu dismissal) runs once, and
`FirstDesktopActionsCompleted` persists `true` afterward (check `session.json` after ~60s).
Setup opens directly to the embedded Windows Update page and auto-starts the check. The
`Elkjop Toolkit Resume` task remains registered throughout, even while `ResumeRequired` is
`false`.

## Test D - Setup abnormal exit without reboot

Same staging as Test C. Once Windows Update is running (a safe boundary - not mid-COM-call),
**kill the Support Tool process** (Task Manager / `Stop-Process`) rather than closing it
normally.

**Expected:** Runtime, `session.json`, and the resume task all survive (no ordinary-close
teardown ran). Manually re-run the task (`schtasks /run /tn "Elkjop Toolkit Resume"`) to
simulate the next qualifying logon.

**Expected on relaunch:** Setup returns directly to the Windows Update page (Stage-driven, not
`ResumeRequired`-driven - it was `false` the whole time).

## Test E - Setup reboot resume

Same staging as Test C, but let Windows Update actually reach `RebootRequired`.

**Expected:** `ResumeRequired` becomes `true`, the task remains registered, reboot happens only
after both are confirmed (RELEASE) or is logged as a dry-run (DEBUG). After the
reboot/logon/task-run, Support Tool resumes Windows Update. If a further pass returns to
`ResumeRequired=false` without yet reaching terminal completion, **the task must still remain
registered** - only genuine terminal completion (fresh search: 0 eligible, no reboot pending)
clears `ResumeRequired` and deletes the task.

## Test F - Close tiers

Stage any session (Setup or Service) with `Stage=WindowsUpdate`.

1. **Active WUA operation:** start a check, then try to close the window while it's running
   (RELEASE, or DEBUG with the artificial delay in `DryRunWindowsUpdateExecutor`). **Expected:**
   close is refused with the "Windows Update in progress" message.
2. **Safe boundary, `ResumeRequired=true`:** stage with `-ResumeRequired`, launch, close the
   window without starting anything. **Expected:** close succeeds; `Runtime\` (exe, config,
   session) and the resume task (if present) are all still there afterward.
3. **Safe boundary, `ResumeRequired=false`:** stage without `-ResumeRequired`, launch, close.
   **Expected:** close succeeds; after ~2 seconds the whole `Runtime\` directory is gone
   (`Get-ChildItem C:\ProgramData\Elkjop\Toolkit\Runtime` fails - directory no longer exists).

## Test H - Close blocked during a lifecycle transition

Stage a Setup or Service session with `Stage=WindowsUpdate`, launch, and let Windows Update reach
`RebootRequired` or `FullyUpToDate`. The persist -> create/verify-or-delete-task -> reboot/
navigate transaction is normally brief (schtasks calls typically complete in well under a
second), so reliably catching the window may require a debugger breakpoint inside
`WindowsUpdateLifecycleCoordinator.HandleRebootRequiredAsync`/`HandleTerminalCompletionAsync`, or
a temporary `Task.Delay` inserted for this test only.

**Expected:** while `SharedViewState.IsLifecycleTransitionActive` is true (i.e. execution is
paused inside the coordinator's transaction), attempting to close the window is refused with the
"Windows Update is finishing a background step" message - distinct from the
`IsWuaOperationActive` message, since no WUA COM call is active at this point.

## Test I - Resume-task deletion failure

Stage a session with `Stage=WindowsUpdate`, launch, and just before Windows Update reaches
terminal completion (or before an ordinary close with `ResumeRequired=false`), make
`Elkjop Toolkit Resume`'s deletion fail - e.g. open Task Scheduler and deny delete permission on
the task, or rename it so `RuntimeStaging.DeleteResumeTaskIfPresent()`'s `/query`/`/delete` calls
target a task that no longer matches.

**Expected (terminal completion):** the "Windows Update cleanup incomplete" error is shown;
`SetupAdvancedPastWindowsUpdate`/`ServiceReturnedToNormal` is NOT raised - Support Tool remains on
the Windows Update page rather than navigating onward. `session.json`'s `Stage`/`ResumeRequired`
are rolled back to their pre-terminal values (check the file directly) - a Setup session
specifically should show `Stage=WindowsUpdate` again, not `Stage=Install`, so a relaunch after a
crash/logoff still returns to Windows Update rather than skipping ahead while the task may still
exist.

**Expected (ordinary close):** the close is refused with the "Cleanup failed" error
(`e.Cancel = true`); `Runtime\` (exe, config, session) is left completely intact, including
`session.json` specifically - no detached `rmdir` is spawned, and nothing deletes `session.json`
on its own ahead of that.

## Test J - Reboot-preparation task creation failure (Tier 2 defensive check)

Stage a session with `Stage=WindowsUpdate`, launch, let Windows Update reach `RebootRequired`,
but make `RuntimeStaging.CreateOrRenewResumeTask`'s `/create` call (or its follow-up query) fail
- e.g. temporarily rename/remove `schtasks.exe`, or deny permission to create scheduled tasks for
the current user.

**Expected:** the "Windows Update reboot blocked" error is shown and no reboot happens (unchanged
from the original reboot-blocked path) - but `session.json` now has `ResumeRequired=true` with no
confirmable task. Attempting to close the window at this point must be refused with the "Resume
task not confirmed" error, even though Tier 2's `ResumeRequired` check alone would otherwise allow
the close - `MainWindow.OnClosing` additionally requires
`RuntimeStaging.IsResumeTaskConfirmedPresent()` before treating `ResumeRequired=true` as safe to
close over.

## Test K - Rollback-of-rollback failure (IsLifecycleRecoveryBlocked)

Hardest to reproduce deliberately - stage a session, reach Windows Update terminal completion,
and make BOTH the initial task deletion AND the following `SessionService.MutateAsync` rollback
call fail (e.g. by making `Runtime\` itself briefly unwritable/read-only around the rollback
attempt, in addition to blocking task deletion as in Test I).

**Expected:** the "Elkjop Toolkit - manual recovery required" error is shown;
`SharedViewState.IsLifecycleRecoveryBlocked` becomes `true` and stays `true` for the rest of the
process's life - every subsequent close attempt (regardless of `ResumeRequired`'s value or any
other state) is refused with the same error, since this flag is checked unconditionally ahead of
every other close tier.

## Test G - Process mutex

Stage any session, launch `Runtime\SupportTool.exe`, then **launch the same exe a second time**
while the first is still running.

**Expected:** the first instance owns the runtime mutex and runs normally. The second instance's
`RuntimeSessionGuard.AcquireForProcessLifetime()` call times out, shows "another Elkjop Toolkit
session is already active", and exits cleanly - without touching `Runtime\` at all (confirm
`session.json`/`config.json` timestamps are unchanged).
