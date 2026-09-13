namespace Support_Tool.Models;

// One labeled count line in the result dialog, e.g. ("Removed", 30). Order of the list is
// display order.
public sealed record ResultCountLine(string Label, int Count);

// The one shared shape both Install and Uninstall batch completions present through
// Views.BatchResultDialog - counts always shown, Details shown only when non-empty (and only
// ever concise, already-cleaned text; never raw WinGet/PowerShell/Revo output - that stays in
// Verbose.log). Plain POCO, not a ViewModelBase: the dialog's content never changes after the
// batch has already finished, so no INotifyPropertyChanged is needed for one-time binding.
public sealed class BatchResultSummary
{
    public required string Title { get; init; }
    public required IReadOnlyList<ResultCountLine> Counts { get; init; }
    public IReadOnlyList<string> Details { get; init; } = [];

    // Drives the details section's visibility - a plain bool since WPF's own NullToVisibility
    // converter can't tell "empty list" from "non-null list", and Details.Count is always
    // non-null regardless.
    public bool HasDetails => Details.Count > 0;

    // Drives the dialog's icon/accent: true shows the warning/attention visual instead of the
    // plain all-green success one. Broader than "something failed outright" - a cleanup warning
    // or a protected/skipped item is also "not a perfectly clean run" and gets the same non-
    // pure-success treatment, even though none of those are counted as a failure.
    public bool HasIssues { get; init; }
}
