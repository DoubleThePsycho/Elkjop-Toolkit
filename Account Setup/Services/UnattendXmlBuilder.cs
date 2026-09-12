using System.Security;
using Toolkit.Shared;

namespace Account_Setup.Services;

// Phase 4B: FirstLogonCommands now register and immediately run the shared
// "Elkjop Toolkit Resume" task (Toolkit.Shared.RuntimeStaging.ScheduledTaskName), targeting the
// staged Runtime\SupportTool.exe - Support Tool itself now owns Wi-Fi connection and driving
// Windows Update to completion (via its ported, Setup-workflow-gated engine), across however many
// reboots that takes, deleting the task itself once the mandatory Windows Update stage reaches
// terminal completion. Setup Agent is no longer part of the live path at all - see
// SupportToolStagingService, which stages everything this answer file's FirstLogonCommands
// reference.
internal static class UnattendXmlBuilder
{
    public static string Build(string username, string password, string uiLanguage, string architecture)
    {
        var usernameXml = Escape(username);
        var passwordXml = Escape(password);
        var uiLanguageXml = Escape(uiLanguage);
        var unattendPathXml = Escape(RuntimeStaging.UnattendPath);
        var hasPassword = !string.IsNullOrEmpty(password);

        var autoLogonXml = string.Empty;
        if (!hasPassword)
        {
            autoLogonXml =
                "      <AutoLogon>\r\n" +
                "        <Password>\r\n" +
                "          <Value></Value>\r\n" +
                "          <PlainText>true</PlainText>\r\n" +
                "        </Password>\r\n" +
                "        <Enabled>true</Enabled>\r\n" +
                "        <LogonCount>1</LogonCount>\r\n" +
                $"        <Username>{usernameXml}</Username>\r\n" +
                "      </AutoLogon>\r\n";
        }

        // The LogonTrigger alone never fires for the very first run: FirstLogonCommands run as
        // the tail end of the new account's own first interactive logon, not before it - so by
        // the time this command executes, that account's only qualifying logon event has
        // already happened. A trigger registered after the fact has nothing left to catch and
        // just sits there (confirmed on a real VM - Task Scheduler showed 0x41303/"has not yet
        // run" even after the trigger was correctly bound to the right account). Order 3 works
        // around this by firing the task once immediately via schtasks /run, which asks the
        // Task Scheduler service to launch it under its already-configured principal right now
        // (borrowing the interactive session that's already active) rather than waiting on a
        // future logon event. Returns immediately - does not block FirstLogonCommands. The
        // LogonTrigger itself stays registered for every later Windows-Update-driven reboot,
        // where a real new logon genuinely does occur, and for as long as the mandatory Setup
        // Windows Update stage remains incomplete - Support Tool itself deletes the task only at
        // that stage's genuine terminal completion (see WindowsUpdateLifecycleCoordinator).
        //
        // This immediate /run workaround is required - do not remove it.
        var resumeTaskXmlPathXml = Escape(RuntimeStaging.ResumeTaskXmlPath);
        var resumeTaskCommandXml =
            "        <SynchronousCommand wcm:action=\"add\">\r\n" +
            "          <Order>2</Order>\r\n" +
            "          <Description>Register the Elkjop Toolkit Resume scheduled task</Description>\r\n" +
            $"          <CommandLine>schtasks.exe /create /tn &quot;{RuntimeStaging.ScheduledTaskName}&quot; /xml &quot;{resumeTaskXmlPathXml}&quot; /f</CommandLine>\r\n" +
            "        </SynchronousCommand>\r\n" +
            "        <SynchronousCommand wcm:action=\"add\">\r\n" +
            "          <Order>3</Order>\r\n" +
            "          <Description>Run Elkjop Toolkit Resume immediately (LogonTrigger misses this account's first logon)</Description>\r\n" +
            $"          <CommandLine>schtasks.exe /run /tn &quot;{RuntimeStaging.ScheduledTaskName}&quot;</CommandLine>\r\n" +
            "        </SynchronousCommand>\r\n" +
            "        <SynchronousCommand wcm:action=\"add\">\r\n" +
            "          <Order>4</Order>\r\n" +
            "          <Description>Remove temporary Elkjop Toolkit Resume task definition</Description>\r\n" +
            $"          <CommandLine>cmd.exe /c del /f /q &quot;{resumeTaskXmlPathXml}&quot;</CommandLine>\r\n" +
            "        </SynchronousCommand>\r\n";

        return
$"""
<?xml version="1.0" encoding="utf-8"?>
<unattend xmlns="urn:schemas-microsoft-com:unattend">
  <settings pass="oobeSystem">
    <component name="Microsoft-Windows-International-Core" processorArchitecture="{architecture}" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
      <InputLocale>0414:00000414</InputLocale>
      <SystemLocale>nb-NO</SystemLocale>
      <UILanguage>{uiLanguageXml}</UILanguage>
      <UserLocale>nb-NO</UserLocale>
    </component>
    <component name="Microsoft-Windows-Shell-Setup" processorArchitecture="{architecture}" publicKeyToken="31bf3856ad364e35" language="neutral" versionScope="nonSxS" xmlns:wcm="http://schemas.microsoft.com/WMIConfig/2002/State" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
{autoLogonXml}      <OOBE>
        <HideEULAPage>true</HideEULAPage>
        <HideLocalAccountScreen>true</HideLocalAccountScreen>
        <HideOEMRegistrationScreen>true</HideOEMRegistrationScreen>
        <HideOnlineAccountScreens>true</HideOnlineAccountScreens>
        <HideWirelessSetupInOOBE>true</HideWirelessSetupInOOBE>
        <ProtectYourPC>3</ProtectYourPC>
      </OOBE>
      <TimeZone>W. Europe Standard Time</TimeZone>
      <UserAccounts>
        <LocalAccounts>
          <LocalAccount wcm:action="add">
            <Password>
              <Value>{passwordXml}</Value>
              <PlainText>true</PlainText>
            </Password>
            <Description>Local administrator account</Description>
            <DisplayName>{usernameXml}</DisplayName>
            <Group>Administrators</Group>
            <Name>{usernameXml}</Name>
          </LocalAccount>
        </LocalAccounts>
      </UserAccounts>
      <FirstLogonCommands>
        <SynchronousCommand wcm:action="add">
          <Order>1</Order>
          <Description>Remove temporary Account Setup answer file</Description>
          <CommandLine>cmd.exe /c del /f /q &quot;{unattendPathXml}&quot;</CommandLine>
        </SynchronousCommand>
{resumeTaskCommandXml}      </FirstLogonCommands>
    </component>
  </settings>
</unattend>
""";
    }

    private static string Escape(string? value) => value is null ? string.Empty : (SecurityElement.Escape(value) ?? string.Empty);
}
