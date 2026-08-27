namespace Account_Setup.Models;

internal sealed class LanguageInfo
{
    public required List<LanguageItem> Languages { get; init; }
    public required string DefaultTag { get; init; }
    public required string DetectionMode { get; init; }
}
