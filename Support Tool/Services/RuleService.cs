using Support_Tool.Models;

namespace Support_Tool.Services;

public class RuleService
{
    private readonly ConfigService _configService;

    public RuleService(ConfigService configService)
    {
        _configService = configService;
    }

    public async Task<RuleSet> LoadAsync()
    {
        var config = await _configService.LoadAsync();
        return config.Debloater;
    }

    // Checks Name, PackageFullName, and Publisher. Appx display names rarely retain
    // the "Microsoft." publisher prefix these rules key off, but PackageFullName
    // always does. Publisher catches items like Samsung's Bixby/SmartThings/Smart
    // Switch, which carry Samsung as publisher without "Samsung" anywhere in the
    // item's own name — same signal the reference cleanup script uses
    // ($publisherDisplayName -like '*Samsung*').
    public RuleEntry? FindRule(InstalledItem item, IEnumerable<RuleEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Match))
                continue;

            if (Matches(item.Name, entry) || Matches(item.PackageFullName, entry) || Matches(item.Publisher, entry))
                return entry;
        }
        return null;
    }

    private static bool Matches(string? value, RuleEntry entry)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        return entry.MatchType?.ToLowerInvariant() == "contains"
            ? value.Contains(entry.Match, StringComparison.OrdinalIgnoreCase)
            : string.Equals(value, entry.Match, StringComparison.OrdinalIgnoreCase);
    }
}
