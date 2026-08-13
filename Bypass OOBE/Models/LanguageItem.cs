namespace Bypass_OOBE.Models;

internal sealed class LanguageItem
{
    public required string Tag { get; init; }
    public required string DisplayName { get; init; }

    public override string ToString() => DisplayName;
}
