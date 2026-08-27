using Account_Setup.Models;
using Microsoft.Win32;

namespace Account_Setup.Services;

internal static class WindowsInfoService
{
    public static WindowsInfo GetWindowsInformation()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")
            ?? throw new InvalidOperationException("Could not open the Windows CurrentVersion registry key.");

        var displayVersion = key.GetValue("DisplayVersion") as string
            ?? key.GetValue("ReleaseId") as string
            ?? "Unknown";

        var ubr = key.GetValue("UBR")?.ToString() ?? "0";
        var buildNumber = key.GetValue("CurrentBuildNumber") as string ?? "0";

        return new WindowsInfo
        {
            ProductName = key.GetValue("ProductName") as string ?? string.Empty,
            EditionId = key.GetValue("EditionID") as string ?? string.Empty,
            DisplayVersion = displayVersion,
            Build = $"{buildNumber}.{ubr}",
            InstallationType = key.GetValue("InstallationType") as string ?? string.Empty,
        };
    }

    public static string GetUnattendArchitecture()
    {
        var architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE")?.ToUpperInvariant() ?? string.Empty;

        return architecture switch
        {
            "AMD64" => "amd64",
            "ARM64" => "arm64",
            "X86" => "x86",
            _ => throw new InvalidOperationException($"Unsupported or unknown processor architecture: '{architecture}'."),
        };
    }
}
