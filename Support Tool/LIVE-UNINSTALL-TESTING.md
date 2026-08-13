# Live Uninstall Testing Guide

This document describes how to safely test real uninstall execution.
Live mode must **never** be tested on the development host machine.

---

## Prerequisites

- A Windows VM (Hyper-V, VMware, or VirtualBox) with a snapshot taken **before** testing
- The VM must be isolated — no shared drives, no shared clipboard for sensitive data
- The build output (`bin\Debug\net10.0-windows\`) copied into the VM

---

## Enabling Live Mode

1. Open `%LocalAppData%\Elkjop Support Tool\settings.json` inside the VM
2. Change `"executionMode"` from `"DryRun"` to `"Real"`:
   ```json
   { "executionMode": "Real" }
   ```
3. Relaunch the app — the banner on the Apps and Programs pages will change to red
   **"LIVE MODE — Selected software will be permanently removed"**

---

## What Changes in Live Mode

| Behavior               | DryRun                          | Real                                     |
|------------------------|---------------------------------|------------------------------------------|
| Mode banner            | Green / "DRY RUN MODE"          | Red / "LIVE MODE"                        |
| Confirmation dialog    | None (MessageBox preview only)  | Typed "UNINSTALL" required               |
| App removal            | No-op, 400 ms simulated delay   | `Remove-AppxPackage` via PowerShell      |
| Program removal        | No-op, 400 ms simulated delay   | UninstallString launched via shell       |
| Session log            | Written with `[DRY RUN]` prefix | Written with `[REAL]` prefix             |

---

## App Uninstall (AppxPackage)

`AppxUninstallExecutor` calls:
```
powershell.exe -NoProfile -NonInteractive -EncodedCommand <base64>
```
The script reads the package name from the `ELKJOP_PACKAGE_FULL_NAME` environment variable
and calls `Remove-AppxPackage -Package $package -ErrorAction Stop`.

Exit code 0 → `UninstallStatus.Succeeded`
Any other exit code → `UninstallStatus.Failed` (error message from stderr)

---

## Program Uninstall (Registry)

`ProgramUninstallExecutor` parses `UninstallString` from the registry using
`UninstallCommandParser`, then launches the installer with `UseShellExecute=true`.

Because many vendor uninstallers spawn a subprocess and exit immediately,
the result is reported as `UninstallStatus.Started`, not `Succeeded`.
Verify actual removal by checking the registry or Add/Remove Programs after the
uninstaller window closes.

---

## Session Logs

Each uninstall session writes a timestamped log to:
```
%LocalAppData%\Elkjop Support Tool\Logs\uninstall-YYYYMMDD-HHmmss.log
```

Log lines include: timestamp, prefix (`[DRY RUN]` / `[REAL]`), kind, item name,
status, exit code (if non-zero), and error message (if any).

---

## After Testing

1. Review the session log in `%LocalAppData%\Elkjop Support Tool\Logs\`
2. Revert the VM to the snapshot taken before testing
3. Set `"executionMode"` back to `"DryRun"` in the development copy of `settings.json`

---

## Safety Invariants (never violate these)

- `RealUninstallExecutor` must never be wired in the development build that runs on the host
- `RealRestorePointService` is intentionally not implemented; it always throws `NotImplementedException`
- The app defaults to `DryRun` on any missing, blank, or unrecognized `executionMode` value
- Do not commit a `settings.json` with `"executionMode": "Real"` to version control
