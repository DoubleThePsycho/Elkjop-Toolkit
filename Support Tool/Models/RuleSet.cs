namespace Support_Tool.Models;

public class RuleSet
{
    public List<RuleEntry> Apps { get; set; } = new();
    public List<RuleEntry> Programs { get; set; } = new();
}
