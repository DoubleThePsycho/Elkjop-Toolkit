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
}
