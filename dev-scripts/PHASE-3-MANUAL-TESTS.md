# Phase 3 manual tests - service.exe / Service Launcher

Manual VM/real-hardware tests for the Service Launcher (`service.exe`) built in Phase 3. Run on a
disposable VM/snapshot - several of these create a real scheduled task and a real
`C:\ProgramData\Elkjop\Toolkit\Runtime\`.

Deployment layout assumed (matches `setup.exe`'s existing sibling-folder convention):

```
<USB root>\
    service.exe
    Elkjop Toolkit\
        config.json                     (canonical unified portable config)
        Support Tool\
            Support Tool-x64.exe
            Support Tool-x86.exe
            Support Tool-arm64.exe
```

## Test 1 - Fresh Service launch

Starting with no `C:\ProgramData\Elkjop\Toolkit\Runtime\` on the machine, double-click
`service.exe` from the real USB/deployment layout.

**Expected:**
* exactly one UAC elevation prompt (service.exe's own manifest; Support Tool inherits the token);
* the correct-architecture `Support Tool-<arch>.exe` is staged as `Runtime\SupportTool.exe`;
* Support Tool opens to normal Home/Service (no automatic Windows Update);
* `Runtime\` contains `SupportTool.exe`, `config.json`, `session.json`;
* `session.json` reads `Workflow=Service`, `Stage=WindowsUpdate`, `ResumeRequired=false`,
  `FirstDesktopActionsCompleted=true`;
* no `Elkjop Toolkit Resume` scheduled task exists (`schtasks /query /tn "Elkjop Toolkit Resume"`
  fails).

## Test 2 - Second service.exe while Support Tool is active

With Support Tool from Test 1 still running, double-click `service.exe` again.

**Expected:**
* the second launch is refused with an "already running" message;
* the first Support Tool instance is completely unaffected;
* `Runtime\` files are unchanged (compare timestamps before/after).

## Test 3 - Ordinary close

Close the fresh Service Support Tool from Test 1 normally (no Windows Update ever run).

**Expected:**
* the resume task is confirmed absent (it never existed);
* the detached cleanup runs;
* the whole `Runtime\` directory disappears within a couple of seconds after Support Tool exits.

## Test 4 - Service reboot/resume

From a fresh Service session, run Windows Update until it reaches `RebootRequired`.

**Expected:**
* `session.json`'s `ResumeRequired` becomes `true`;
* `Elkjop Toolkit Resume` is confirmed present (`schtasks /query`);
* the reboot happens only after both are confirmed;
* after login, the scheduled task launches `Runtime\SupportTool.exe` directly - `service.exe` is
  not involved in this relaunch at all;
* Support Tool returns to Windows Update and continues.

## Test 5 - Manual service.exe during a resumable Service state

With Support Tool closed and `session.json` showing `ResumeRequired=true` (e.g. paused mid-way
through Test 4), modify or delete the portable source `config.json` and/or the sibling
`Support Tool-<arch>.exe`, then double-click `service.exe`.

**Expected:**
* Service Launcher classifies `ResumeExisting` and launches the already-staged
  `Runtime\SupportTool.exe` directly;
* `Runtime\SupportTool.exe`/`config.json`/`session.json` are NOT replaced (compare file hashes/
  timestamps before and after);
* the launch succeeds even though the portable source was changed or removed.

## Test 6 - Workflow conflict

Stage a valid Setup Runtime (e.g. via `dev-scripts/Stage-TestRuntime.ps1 -Workflow Setup`), then
double-click `service.exe`.

**Expected:**
* refused with a workflow-conflict message, for both `ResumeRequired=false` and `true` on the
  Setup session;
* the Setup Runtime (`SupportTool.exe`/`config.json`/`session.json`, and the resume task if
  present) is completely untouched.

## Test 7 - Stale recovery

Stage a Service session with `ResumeRequired=false` (e.g. leftover from an abnormal exit) with no
Support Tool running, then double-click `service.exe`.

**Expected:**
* the old task (if any) and the old Runtime are removed;
* a fresh Service session is staged and launched, per Test 1's expectations.

## Test 8 - USB independence

After Test 1's fresh Service session is staged and Support Tool is running, physically
remove/unplug the USB stick (or otherwise make the portable source unreachable).

**Expected:**
* Support Tool's core operation - Windows Update, session persistence, the resume/reboot
  lifecycle, ordinary close - continues unaffected, since it only ever reads from
  `Runtime\config.json`/`session.json`, never the portable source.

---

## Notes for testers

* `dev-scripts/Stage-TestRuntime.ps1` (from Phase 2B) is still useful for staging specific
  synthetic session states to fault-inject into a scenario (e.g. Test 6's Setup session, or a
  semantically-corrupt `session.json` to exercise Service Launcher's `Stale` classification) - it
  is NOT a substitute for these tests when what's actually being verified is `service.exe`'s own
  staging/classification logic. Where the two overlap (a plain "fresh Service" or
  "Service reboot-resume" scenario), prefer running the real `service.exe` as in Tests 1 and 4
  above; reach for the manual script when you specifically need a state `service.exe` itself has
  no way to produce (e.g. a corrupt or semantically-invalid `session.json`, tested directly
  against a manually-staged Support Tool rather than through the launcher).
* Corrupt/missing canonical config: with no/stale Runtime, `service.exe` must show a visible error
  and must NOT touch any existing stale Runtime; with a `ResumeExisting` session, the canonical
  config is irrelevant and the existing staged Runtime launches regardless of the portable
  source's state - both are exercised implicitly by Tests 5 and 7's setups if the portable config
  is deliberately broken beforehand.
