using System.Windows.Media;

namespace Support_Tool.Models;

public class InstalledItem
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public ImageSource? Icon { get; set; }
    public string PackageFullName { get; set; } = "";
    public string UninstallCommand { get; set; } = "";
    public InstalledItemKind Kind { get; set; }
    public long? InstalledSizeKb { get; set; }

    // Programs only - captured at inventory time (before any uninstall attempt), since this is
    // the only point the registry reliably has this data. Used by the Revo /mu cleanup pass,
    // which needs the install path and 32/64-bit identity to scan the right locations - never
    // re-derived after native uninstall has already removed the program.
    public string InstallLocation { get; set; } = "";
    public bool Is64Bit { get; set; } = true;

    // Programs only - the exact ARP (Add/Remove Programs) registration this item was read from,
    // captured at inventory time. ProgramUninstallExecutor re-opens this EXACT location after the
    // registered uninstaller process exits to positively verify the registration is actually
    // gone before ever trusting "Succeeded" - a launched uninstaller's own exit code alone is not
    // proof of completion (many detach a child process and exit 0 immediately). "LocalMachine" or
    // "CurrentUser"; ViewName is "Registry64"/"Registry32"/"Default" (HKCU has no Registry64/32
    // distinction).
    public string RegistryHiveName { get; set; } = "";
    public string RegistryViewName { get; set; } = "";
    public string UninstallRegistryKeyName { get; set; } = "";

    // Apps only - true when Windows itself reports this package as NonRemovable (a genuine OS
    // component, not a per-user-removable app). Captured once at discovery time from the same
    // Get-AppxPackage call that lists it, so uninstall can skip known-protected packages
    // immediately without ever attempting (and failing) a real removal - see
    // AppxUninstallExecutor's own remarks.
    public bool IsProtectedSystemComponent { get; set; }
}
