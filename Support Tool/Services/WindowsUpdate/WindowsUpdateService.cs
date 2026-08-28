using Microsoft.Win32;

namespace Support_Tool.Services;

internal sealed class WindowsUpdateSearchResult
{
    public required int Count { get; init; }
    public required IReadOnlyList<string> Titles { get; init; }
}

// Public (not internal, unlike Setup Agent's original) - reachable through
// WindowsUpdatePageViewModel's/UpdateRowViewModel's public constructors and
// IWindowsUpdateExecutor's public method signature.
public enum UpdateState
{
    Pending,
    Downloading,
    Downloaded,
    Installing,
    Installed,
    Failed,
    PendingRestart,
}

public sealed record UpdateStatusEntry(string Name, UpdateState State, int? PercentComplete = null);

// Always real - Search() only queries Windows Update Agent locally and makes no changes to the
// system either way, so there's no dry-run variant of this half. Purely a monitoring tool now:
// Setup Agent no longer downloads/installs updates itself (see IWindowsUpdateTrigger below) -
// this is just how it knows how many are still outstanding.
internal static class WindowsUpdateSearcher
{
    // Default criteria mirrors what Windows' own automatic-update flow searches for - preview/
    // optional updates only surface through a separate "Optional updates" opt-in search, not
    // this one, so they should already be excluded without extra filtering. The title check
    // below is a defensive second layer on top of that, not a replacement for it - flagged in
    // the plan as worth empirically confirming rather than fully trusting on faith.
    // internal (not private) so RealWindowsUpdateExecutor's typed search reuses the identical
    // literal instead of a second hand-copied copy that could drift.
    internal const string SearchCriteria = "IsInstalled=0 and IsHidden=0 and Type='Software'";

    public static WindowsUpdateSearchResult Search(Logger logger)
    {
        dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
        session.ClientApplicationID = "Elkjop Support Tool";

        dynamic searcher = session.CreateUpdateSearcher();
        dynamic searchResult = searcher.Search(SearchCriteria);
        dynamic allUpdates = searchResult.Updates;

        var titles = new List<string>();
        int count = allUpdates.Count;
        for (var i = 0; i < count; i++)
        {
            dynamic update = allUpdates.Item(i);
            string title = update.Title;

            if (title.Contains("Preview", StringComparison.OrdinalIgnoreCase))
            {
                logger.Info($"Excluding preview update from count: '{title}'.");
                continue;
            }

            titles.Add(title);
        }

        logger.Info($"Windows Update search found {titles.Count} eligible update(s) (of {count} returned by the search).");
        return new WindowsUpdateSearchResult { Count = titles.Count, Titles = titles };
    }

    // Read-only, no dry-run split needed. Used to flag a still-pending title as "Failed" in the
    // UI rather than just "Pending" when Windows itself would show a manual "Retry" button -
    // the plain eligible-updates search can't tell "not yet attempted" apart from "attempted
    // and failed" on its own, but update history can. Only entries from the last 2 hours count,
    // so a failure from an unrelated, much older Windows Update session doesn't flag a title
    // forever.
    public static HashSet<string> GetRecentlyFailedTitles(Logger logger)
    {
        var failedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
            dynamic searcher = session.CreateUpdateSearcher();

            int totalCount = searcher.GetTotalHistoryCount();
            if (totalCount == 0)
            {
                return failedTitles;
            }

            var checkCount = Math.Min(totalCount, 25);
            dynamic history = searcher.QueryHistory(0, checkCount);
            var cutoff = DateTime.Now.AddHours(-2);

            int count = history.Count;
            for (var i = 0; i < count; i++)
            {
                dynamic entry = history.Item(i);
                int resultCode = entry.ResultCode; // orcFailed=4, orcAborted=5
                DateTime date = entry.Date;

                if ((resultCode == 4 || resultCode == 5) && date > cutoff)
                {
                    string title = entry.Title;
                    failedTitles.Add(title);
                }
            }
        }
        catch (Exception ex)
        {
            logger.Warn($"Failed to query Windows Update history: {ex.Message}");
        }

        return failedTitles;
    }
}

// Read-only, no dry-run split needed - just reports the system's actual current reboot-pending
// state, regardless of who (Windows or, previously, us) installed whatever's waiting on it.
// Checks multiple independent signals rather than relying solely on the WUA COM property - real
// machine testing showed that alone wasn't reliably catching completion. The three registry
// checks below are the same well-established signals most real-world deployment/patch tooling
// uses, covering the different subsystems (Windows Update itself, CBS servicing, a generic
// pending file-replacement operation) that can each independently need a restart.
internal static class WindowsRebootStatus
{
    public static bool IsRebootRequired()
    {
        return IsWuaRebootRequired()
            || RegistryKeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
            || RegistryKeyExists(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending")
            || RegistryValueExists(@"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations");
    }

    private static bool IsWuaRebootRequired()
    {
        try
        {
            dynamic systemInfo = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.SystemInfo")!)!;
            return systemInfo.RebootRequired;
        }
        catch
        {
            return false;
        }
    }

    private static bool RegistryKeyExists(string path)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);
        return key is not null;
    }

    private static bool RegistryValueExists(string path, string valueName)
    {
        using var key = Registry.LocalMachine.OpenSubKey(path);
        return key?.GetValue(valueName) is not null;
    }
}

// Bridges two different producers of table updates so neither one clobbers the other:
// - Replace() is the existing full-snapshot behavior - pass-boundary searches
//   (BuildStatusEntries) and the executor's own live per-item progress callbacks, both of which
//   already know the complete, authoritative state of whatever they're reporting on.
// - MergeNewTitles() is additive-only, for RunNativeUpdateCycle's background availability
//   watcher - it only appends titles this table has never seen before. A title the executor is
//   actively Downloading/Installing (with a real percent) is never stomped back to a bare
//   Pending by a background availability check running concurrently.
internal sealed class PendingUpdatesTable
{
    private readonly object _gate = new();
    private readonly Action<IReadOnlyList<UpdateStatusEntry>> _onChanged;
    private List<UpdateStatusEntry> _entries = [];

    public PendingUpdatesTable(Action<IReadOnlyList<UpdateStatusEntry>> onChanged)
    {
        _onChanged = onChanged;
    }

    public void Replace(IReadOnlyList<UpdateStatusEntry> entries)
    {
        lock (_gate)
        {
            _entries = [.. entries];
        }

        _onChanged(_entries);
    }

    public void MergeNewTitles(IReadOnlyList<string> titles, Logger logger)
    {
        List<string>? newlyAdded = null;
        List<UpdateStatusEntry>? snapshot = null;

        lock (_gate)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _entries)
            {
                known.Add(entry.Name);
            }

            foreach (var title in titles)
            {
                if (known.Add(title))
                {
                    _entries.Add(new UpdateStatusEntry(title, UpdateState.Pending));
                    (newlyAdded ??= []).Add(title);
                }
            }

            if (newlyAdded is not null)
            {
                snapshot = [.. _entries];
            }
        }

        if (snapshot is not null)
        {
            logger.Info($"Availability watcher found {newlyAdded!.Count} newly available update(s) mid-pass: {string.Join(", ", newlyAdded)}.");
            _onChanged(snapshot);
        }
    }
}

// Self-driven download/install (with real progress) lives in WindowsUpdateExecutor.cs -
// IWindowsUpdateTrigger/RealWindowsUpdateTrigger/DryRunWindowsUpdateTrigger (the prior
// DetectNow()-based design) were removed in favor of it.
