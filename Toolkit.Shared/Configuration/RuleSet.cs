namespace Toolkit.Shared.Configuration;

public class RuleSet
{
    public List<RuleEntry> Apps { get; set; } = new();
    public List<RuleEntry> Programs { get; set; } = new();
}
