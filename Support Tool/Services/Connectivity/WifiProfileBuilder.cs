using System.Security;

namespace Support_Tool.Services;

// Same WLAN profile schema/settings as the shop's Connect-ToWifi.ps1 (WPA2-Personal/AES,
// plaintext passphrase key). Ported verbatim from Setup Agent - pure XML string building, no
// staged-path dependency at all.
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
