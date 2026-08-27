using Microsoft.Win32;

namespace Account_Setup.Services;

internal static class OobeGuard
{
    // Windows' own placeholder account, created automatically during OOBE before a real
    // account exists. Confirmed empirically: a live mid-OOBE VM had only this profile under
    // ProfileList; a normal completed system had a real named one instead. (OOBEInProgress and
    // ImageState were tried first and found unreliable - both read the same on live and mid-OOBE
    // systems, since OOBEInProgress tracks OOBE's UI having foreground focus, not setup completion,
    // and Shift+F10 knocks it out of the foreground.)
    private const string PlaceholderProfileName = "defaultuser0";

    /// <summary>
    /// True if the only user profile under ProfileList (besides the built-in system/service
    /// SIDs) is Windows' OOBE placeholder account - i.e. no real account has been created yet.
    /// Anything unreadable or unexpected is treated as "completed" (fail closed): this check
    /// exists specifically to keep Sysprep /oobe /reboot away from a live, already-provisioned
    /// system, so an unknown state should block, not allow.
    /// </summary>
    public static bool IsOobeInProgress()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        if (key is null)
        {
            return false;
        }

        foreach (var sidName in key.GetSubKeyNames())
        {
            // Well-known system/service SIDs (S-1-5-18 LocalSystem, -19 LocalService, -20
            // NetworkService, etc.) are never real user accounts - only real (and the OOBE
            // placeholder) accounts get an S-1-5-21-* domain/local SID.
            if (!sidName.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var profileKey = key.OpenSubKey(sidName);
            var imagePath = profileKey?.GetValue("ProfileImagePath") as string;
            var profileName = Path.GetFileName(imagePath?.TrimEnd('\\', '/'));

            if (!string.Equals(profileName, PlaceholderProfileName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
