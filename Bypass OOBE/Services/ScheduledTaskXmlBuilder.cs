using System.Security;

namespace Bypass_OOBE.Services;

// Generates a Task Scheduler XML definition (schtasks /create /xml) instead of the equivalent
// flag-based command (/sc onlogon /rl highest, no /ru). That flag form was tried first and
// found broken on a real VM: without an explicit /ru, schtasks binds the task to the creating
// context's own identity - and FirstLogonCommands runs as SYSTEM, not the technician's new
// account, so the trigger ended up bound to SYSTEM's logon rather than the real interactive
// one (confirmed via Task Scheduler showing "0x41303 - has not yet run" after a real logon,
// while a manual run worked fine). This XML form makes both the trigger and the run-as
// identity explicit and unambiguous instead of relying on schtasks' implicit defaults.
internal static class ScheduledTaskXmlBuilder
{
    public static string Build(string username, string exePath)
    {
        var usernameXml = Escape(username);
        var exePathXml = Escape(exePath);

        return
$"""
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Runs Setup Agent after each logon until Windows Update is fully current.</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
      <UserId>{usernameXml}</UserId>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <UserId>{usernameXml}</UserId>
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>HighestAvailable</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <!-- StopExisting, not IgnoreNew: if Task Scheduler's own "is an instance already running"
         tracking ever gets stuck believing a prior run is still active (plausible after a VM
         hard-reset/snapshot revert rather than a clean shutdown - common during repeated test
         cycles), IgnoreNew would silently swallow every future trigger forever with no obvious
         symptom besides "it just stopped firing." Setup Agent is safely restartable from
         scratch on every launch (no persisted mid-flight state beyond WifiProfile.xml/logs, both
         fine to re-touch), so killing a stale instance and starting fresh is the safe choice. -->
    <MultipleInstancesPolicy>StopExisting</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
    <!-- Confirmed via a real VM's Task Scheduler properties: leaving Compatibility unspecified
         registered the task at the old V1 (Vista/Server 2008) schema level instead of the
         current OS's own, and V1's more limited trigger evaluation is the likely reason the
         LogonTrigger fired once (via the immediate /run in FirstLogonCommands) but never
         reliably re-fired on later real logons. 4 = the newest defined level (covers Windows
         8 through 10/11 - the schema number was never bumped further). -->
    <Compatibility>4</Compatibility>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>{exePathXml}</Command>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
