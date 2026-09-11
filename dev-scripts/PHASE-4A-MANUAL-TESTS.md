# Phase 4A manual tests - Account Setup shadow-staging of Support Tool

Manual VM tests for Phase 4A. Account Setup still runs the live Setup Agent OOBE flow unchanged;
this phase only adds a non-fatal "shadow" stage of the final Support Tool Runtime alongside it,
for inspection. Run on a disposable OOBE VM/snapshot.

## Test 1 - Migrate USB config

Run against a **copy** of the real toolkit USB layout (not the live deployed stick):

```powershell
.\Migrate-UnifiedConfig.ps1 -UsbRoot "<copy of USB root>"
```

**Verify:**
* `<copy>\Elkjop Toolkit\config.json` is created;
* it contains all four sections: `Account`, `Connection`, `Debloater`, `Installer`;
* values match the two source files exactly (spot-check a few, e.g. `Account.Username`,
  `Connection.WifiName`, a few `Debloater.Apps`/`Installer.Categories` entries);
* the two legacy files (`Account Setup\config.json`, `Support Tool\config.json`) are byte-for-byte
  unchanged (hash them before/after).

Also verify the refusal/force behavior:
* re-running the same command without `-Force` fails with a clear error and does not touch the
  already-written canonical file;
* re-running with `-Force` overwrites it.

## Test 2 - Account Setup prepare-only

On a disposable OOBE VM, with the migrated canonical `config.json` in place on the USB:

```
Account Setup-<arch>.exe --prepare-only
```

**Expected:**
* the existing live Setup Agent assets are staged exactly as before this phase:
  `Unattend.xml`, `WifiCredentials.json` (if Wi-Fi configured), `SetupAgent.exe`,
  `SetupAgentTask.xml` under `C:\ProgramData\Elkjop\Toolkit\Temp\`;
* `SetupAgentTask.xml` still targets `SetupAgent.exe` (open it and check the `<Command>` element);
* additionally, `C:\ProgramData\Elkjop\Toolkit\Runtime\` now contains `SupportTool.exe`,
  `config.json`, `session.json`;
* `Runtime\session.json` reads exactly `Workflow=Setup`, `Stage=WindowsUpdate`,
  `ResumeRequired=false`, `FirstDesktopActionsCompleted=false`;
* `Runtime\config.json` matches the canonical USB config content;
* **no** `Elkjop Toolkit Resume` scheduled task exists
  (`schtasks /query /tn "Elkjop Toolkit Resume"` fails) - only the existing Setup Agent task was
  registered.

## Test 3 - Architecture mapping

Repeat Test 2 (or inspect the staged files) on x64 and, where available, x86/ARM64 hardware or
VMs.

**Verify the mapping:**
```
amd64 → Support Tool-x64.exe
x86   → Support Tool-x86.exe
arm64 → Support Tool-arm64.exe
```

Hash `Runtime\SupportTool.exe` and compare it against the matching
`<USB root>\Elkjop Toolkit\Support Tool\Support Tool-<arch>.exe` source file - they must be
identical.

## Test 4 - Missing/corrupt canonical config

Remove or corrupt `<USB root>\Elkjop Toolkit\config.json` (leave the two legacy per-tool configs
in place), then run `Account Setup-<arch>.exe --prepare-only`.

**Expected:**
* a `[Shadow stage]`-prefixed warning is logged (check the Account Setup log file path shown in
  the completion dialog) explaining the canonical config is missing/corrupt;
* the existing Setup Agent preparation (Unattend.xml, Wi-Fi credentials, SetupAgent.exe,
  SetupAgentTask.xml) still succeeds exactly as in Test 2;
* no shadow `Runtime\` is created (or, if one already existed from a prior run, it is left
  untouched).

## Test 5 - Workflow conflict

Manually stage a valid Service Runtime first (e.g. via `service.exe`, or
`Stage-TestRuntime.ps1 -Workflow Service`), then run `Account Setup-<arch>.exe --prepare-only`
with a valid canonical config present.

**Expected:**
* a `[Shadow stage]` warning is logged noting a Service workflow already owns the Runtime;
* the Service Runtime (`SupportTool.exe`/`config.json`/`session.json`, and its resume task if
  present) is completely untouched;
* the existing Setup Agent preparation still succeeds exactly as in Test 2.

---

Manual tests only - these were prepared and documented, not executed, from the development
environment (no disposable OOBE VM available there).
