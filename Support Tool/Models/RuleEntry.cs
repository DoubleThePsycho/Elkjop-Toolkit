namespace Support_Tool.Models;

public class RuleEntry
{
    public string Match { get; set; } = "";
    public string MatchType { get; set; } = "exact";
    public bool Preselected { get; set; }
    public bool Warn { get; set; }
    public bool Blocked { get; set; }
    public string Reason { get; set; } = "";
}
