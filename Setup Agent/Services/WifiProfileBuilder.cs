using System.Security;

namespace Setup_Agent.Services;

// Same WLAN profile schema/settings as the shop's Connect-ToWifi.ps1 (WPA2-Personal/AES,
// plaintext passphrase key). Previously lived in Account Setup (then named Bypass OOBE) and was
// built once at OOBE time; moved here 2026-08-13 so Setup Agent owns the whole Wi-Fi connection
// story itself, including rebuilding this profile fresh on every retry across every launch (a
// laptop's WLAN driver might only arrive via the first Windows Update) rather than depending on
// a one-shot file Account Setup authored before it could know whether the driver would ever
// show up.
internal static class WifiProfileBuilder
{
    public static string Build(string ssid, string password)
    {
        var ssidXml = Escape(ssid);
        var passwordXml = Escape(password);

        return
$"""
<?xml version="1.0"?>
<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
    <name>{ssidXml}</name>
    <SSIDConfig>
        <SSID>
            <name>{ssidXml}</name>
        </SSID>
    </SSIDConfig>
    <connectionType>ESS</connectionType>
    <connectionMode>auto</connectionMode>
    <MSM>
        <security>
            <authEncryption>
                <authentication>WPA2PSK</authentication>
                <encryption>AES</encryption>
                <useOneX>false</useOneX>
            </authEncryption>
            <sharedKey>
                <keyType>passPhrase</keyType>
                <protected>false</protected>
                <keyMaterial>{passwordXml}</keyMaterial>
            </sharedKey>
        </security>
    </MSM>
</WLANProfile>
""";
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;
}
