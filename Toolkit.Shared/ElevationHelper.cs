using System.Runtime.Versioning;
using System.Security.Principal;

namespace Toolkit.Shared;

[SupportedOSPlatform("windows")]
public static class ElevationHelper
{
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
