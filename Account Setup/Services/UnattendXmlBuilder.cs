using System.Security;

namespace Account_Setup.Services;

internal static class UnattendXmlBuilder
{
    // Task Scheduler's own trusted-elevation-broker behavior: a task with "run with highest
    // privileges" set elevates silently for an admin user, no UAC prompt - the account this
    // tool creates is always an Administrator (see UserAccounts below). The task itself is
    // registered from a Task Scheduler XML file (see ScheduledTaskXmlBuilder) rather than
    // schtasks' own /sc onlogon flags - the flag form was tried first and found to bind to the
    // wrong identity, see that file's header comment.
    private const string ScheduledTaskName = "Elkjop Setup Agent";

    public static string Build(string username, string password, string uiLanguage, string architecture)
    {
        var usernameXml = Escape(username);
        var passwordXml = Escape(password);
        var uiLanguageXml = Escape(uiLanguage);
        var unattendPathXml = Escape(AppInfo.UnattendPath);
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

        // Registers Setup Agent to run at every subsequent logon (elevated, no UAC prompt - see
        // ScheduledTaskName comment above). Setup Agent owns Wi-Fi connection (with retry, since
        // a fresh machine's Wi-Fi driver often only arrives via Windows Update itself) and then
        // drives Windows Update to completion across however many reboots that takes. It deletes
        // this task itself once there are no updates left to install. The task XML itself is
        // written to disk by Program.cs alongside Unattend.xml/WifiProfile.xml - only its already-
        // staged path is referenced here.
        //
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
        // where a real new logon genuinely does occur.
        var taskXmlPathXml = Escape(AppInfo.SetupAgentTaskXmlPath);
        var setupAgentCommandXml =
            "        <SynchronousCommand wcm:action=\"add\">\r\n" +
            "          <Order>2</Order>\r\n" +
            "          <Description>Register Setup Agent scheduled task</Description>\r\n" +
            $"          <CommandLine>schtasks.exe /create /tn &quot;{ScheduledTaskName}&quot; /xml &quot;{taskXmlPathXml}&quot; /f</CommandLine>\r\n" +
            "        </SynchronousCommand>\r\n" +
            "        <SynchronousCommand wcm:action=\"add\">\r\n" +
            "          <Order>3</Order>\r\n" +
            "          <Description>Run Setup Agent immediately (LogonTrigger misses this account's first logon)</Description>\r\n" +
            $"          <CommandLine>schtasks.exe /run /tn &quot;{ScheduledTaskName}&quot;</CommandLine>\r\n" +
            "        </SynchronousCommand>\r\n" +
            "        <SynchronousCommand wcm:action=\"add\">\r\n" +
            "          <Order>4</Order>\r\n" +
            "          <Description>Remove temporary Setup Agent task definition</Description>\r\n" +
            $"          <CommandLine>cmd.exe /c del /f /q &quot;{taskXmlPathXml}&quot;</CommandLine>\r\n" +
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
{setupAgentCommandXml}      </FirstLogonCommands>
    </component>
  </settings>
</unattend>
""";
    }

    private static string Escape(string? value) => value is null ? string.Empty : (SecurityElement.Escape(value) ?? string.Empty);
}
