# Phase 5 manual tests

Unified `Elkjop Toolkit.exe` dispatcher, corrected session/resume precedence, Account Setup
duplicate-instance protection, Setup Agent removal, WU download-polling fix, and the accumulated
WU/Install/Apps/Programs UI polish. Run these against a disposable OOBE VM and a normal-desktop
VM. `dev-scripts\Stage-TestRuntime.ps1` builds the artificial `session.json`/Runtime fixtures
tests 3, 4, 5, 7, and 9 need without requiring a real OOBE/reboot cycle for every case.

1. **Fresh OOBE, no session** — boot to the OOBE placeholder profile only, run `Elkjop
   Toolkit.exe`: expect `OobeGuard.IsOobeInProgress()` → `true` → requests `Setup` → `Stale` →
   launches `Account Setup-{arch}.exe` → existing OOBE flow proceeds unchanged → Setup Agent
   never exists/runs anywhere in the process.
2. **Normal desktop, no session** — real user profile present, run `Elkjop Toolkit.exe`: expect
   OOBE check `false` → requests `Service` → `Stale` → stages fresh Service Runtime → Support
   Tool opens to Home, Service workflow.
3. **Setup session preserved regardless of ResumeRequired (the corrected case)** — using
   `Stage-TestRuntime.ps1`, stage a Setup session with `Stage=Install`, `ResumeRequired=false`,
   valid `Runtime\SupportTool.exe`/`config.json`/`session.json` all present, on what now looks
   like a normal desktop. Run `Elkjop Toolkit.exe`. **Expected**: the dispatcher peeks `Setup`
   from the session, `TryAcquire` returns `ResumeExisting`, the already-staged Support Tool is
   launched directly; Account Setup is **not** launched; `Runtime\config.json`/`session.json` are
   **not** overwritten; `Stage` remains `Install`; Support Tool's own existing Stage-authoritative
   navigation returns the technician to the Install-stage experience.
4. **Setup resume, files missing** — stage the same valid-looking Setup session, then delete
   `Runtime\SupportTool.exe` (or `config.json`, or `session.json`) before running `Elkjop
   Toolkit.exe`. **Expected**: a visible failure naming what's missing; no destructive cleanup; no
   automatic re-staging; no fallback to Service; no Account Setup relaunch.
5. **Setup resume, valid session but still in OOBE** — stage a valid, resumable Setup session
   (e.g. via `Account Setup --prepare-only`, or `Stage-TestRuntime.ps1` with `-Workflow Setup`)
   while Windows is still at the OOBE placeholder profile (`OobeGuard.IsOobeInProgress()` still
   `true`). Run `Elkjop Toolkit.exe`. **Expected**: a visible refusal naming that Setup is
   already prepared and Windows is still in OOBE; Support Tool does **not** launch; Account Setup
   is **not** relaunched; `Runtime\config.json`/`session.json` hashes are unchanged before and
   after. Once the machine reaches a real first desktop (OOBE check now `false`), the identical
   session resumes normally per test 3.
6. **Setup mid-WU, actually resumable** (`ResumeRequired=true`) on a normal-looking desktop —
   same `ResumeExisting` path as test 3, confirming the pre-existing case still works after the
   `Classify` change.
7. **Service resumable / non-resumable** — `ResumeRequired=true` → `ResumeExisting`, relaunch
   as-is; `ResumeRequired=false` → `Stale`, fresh Service re-stage — confirming Service's
   narrower rule is genuinely unchanged by this phase.
8. **Live mutex held** — Support Tool running, double-click `Elkjop Toolkit.exe`: refused with
   "already running," regardless of what the environment would have guessed.
9. **Workflow conflict backstop** — a valid Setup session exists; force the dispatcher to request
   Service anyway (artificial test path): confirm `WorkflowConflict` refusal names the
   conflicting workflow correctly.
10. **Abandoned mutex** — kill a Support Tool process holding the mutex without releasing it,
    relaunch the dispatcher: confirm `AbandonedMutexException` is treated as a successful
    acquisition and classification proceeds normally.
11. **Setup Agent fully absent** — confirm no `Setup Agent-*.exe`, no `WifiCredentials.json`, no
    Setup-Agent-targeting task XML anywhere; confirm `Elkjop Toolkit\Setup Agent\` doesn't exist
    on a freshly-deployed USB.
12. **Account Setup duplicate-launch protection** — during a fresh-OOBE run, rapidly double- (or
    triple-) click `Elkjop Toolkit.exe` several times in quick succession before completing the
    `SetupWindow` dialog. **Expected**: exactly one Account Setup UI survives; any other Account
    Setup process(es) show "Account Setup is already running" and exit cleanly, without touching
    Runtime/config/session; only one eventual Runtime staging transaction occurs once the
    surviving instance's dialog is completed.
13. **Account Setup mutex clean-exit handoff** — finish (or Cancel) a first Account Setup instance
    normally, then immediately launch `Elkjop Toolkit.exe` again. **Expected**: the second launch
    acquires the `AccountSetupInstanceLease` mutex normally via a plain `WaitOne(TimeSpan.Zero)`
    success — it does **not** go through the `AbandonedMutexException` path merely because the
    previous instance exited cleanly, confirming the first instance's `Dispose()` genuinely called
    `ReleaseMutex()` rather than leaving the mutex abandoned.
14. **WU download callback-silence fix** — real multi-update download batch with one slow update
    and several fast ones; confirm the grid visibly refreshes at least every ~2s even during a
    stretch with zero push-callback activity for the *download* phase specifically; confirm the
    *install* phase's behavior is completely unchanged (no polling there).
15. **WU deadline preserved** — force a download past its absolute deadline: confirm total
    elapsed wait across every poll slice still respects the original deadline, and the existing
    abort/`AbortGracePeriod`/`AbandonedOperations` path fires identically to before.
16. **No UI churn on identical polls** — during a quiet stretch with genuinely no state change,
    confirm the row grid does not visibly flicker/re-layout and `LastActivityText` does not
    restamp on every poll tick, only when a row or status genuinely changes; confirm StatusText
    changes that ARE genuinely new (e.g. "Checking connectivity..." at the start of a fresh check)
    DO restamp it immediately, even on a second run that would otherwise still show a stale
    timestamp from the previous run.
17. **WU column swap + wording** — visually confirm `Update | Status | Progress` ordering (with
    Status now the wider slot, Progress the narrower one) at wide and at `MainWindow.MinWidth`;
    confirm `Pending...`/`Preparing...`/`Downloading...`/`Installing...` wording (three ASCII
    periods) and unchanged terminal-state wording.
18. **Reboot countdown + modal** — trigger a real reboot-required WU outcome: confirm the
    countdown starts at 10s; confirm the darker palette/topmost/warning-accent-strip/dimming
    overlay all appear; confirm the overlay clears on all 3 exit paths (expiry, Cancel, titlebar
    close); confirm the rendered order top-to-bottom is titlebar, then the thin amber accent
    strip, then the body (not accent-strip-above-titlebar); confirm a thin outer border is
    visible around the whole modal.
19. **Install page** — confirm no browser selector exists; confirm SA Standard/SA Gaming cards
    scroll away together with the Applications catalog; confirm Firefox (or catalog fallback) is
    still merged into every preset application with no way to change it from this page (only via
    the real Browsers category checkboxes below); confirm the two cards show exactly "Firefox,
    Java 8, Spotify, VLC" / "Standard + Discord + game launchers".
20. **LIVE-banner removal** — confirm no banner space is reserved on Install/Apps/Programs in
    LIVE mode; confirm the compact `DryRunIndicator` appears correctly in a DEBUG build with the
    Settings-page toggle flipped on, without affecting that separate toggle.
