namespace Bypass_OOBE.Models;

internal sealed class WindowsInfo
{
    public required string ProductName { get; init; }
    public required string EditionId { get; init; }
    public required string DisplayVersion { get; init; }
    public required string Build { get; init; }
    public required string InstallationType { get; init; }
}
