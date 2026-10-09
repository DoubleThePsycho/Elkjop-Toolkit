using System.Diagnostics;

namespace Support_Tool.Services;

using Support_Tool.Models;

// One installed-package snapshot for a whole catalog load, keyed by WingetId (case-insensitive) -
// replaces the old "one `winget list --id <id>` process per catalog app" design (external review,
// real-hardware testing: cards appeared one at a time, several seconds apart, because loading N
// apps meant N sequential winget process spawns). GetState never throws and never does I/O - a
// WingetId with no entry in this snapshot is simply NotInstalled.
//
// A WingetId can map to more than one catalog row (Java 8's x86/x64 rows share one WingetId - see
// InstallableApp.Architecture's own remarks) - `winget list` has no way to report per-architecture
// state independently, so both rows deliberately share whatever this snapshot reports for that one
// WingetId. Never faked/split apart - this is a real limitation of winget itself, not a bug here.
public sealed class WingetInstalledSnapshot
{
    public static readonly WingetInstalledSnapshot Empty = new(new Dictionary<string, AppInstallState>(StringComparer.OrdinalIgnoreCase));

    private readonly IReadOnlyDictionary<string, AppInstallState> _statesByWingetId;

    public WingetInstalledSnapshot(IReadOnlyDictionary<string, AppInstallState> statesByWingetId)
    {
        _statesByWingetId = statesByWingetId;
    }

    public AppInstallState GetState(string wingetId) =>
        _statesByWingetId.TryGetValue(wingetId, out var state) ? state : AppInstallState.NotInstalled;
}

// Thrown by GetInstalledSnapshotAsync whenever the snapshot cannot be trusted - winget exited
// non-zero, or its output could not be structurally parsed. Deliberately a distinct exception
// type (not a silent "everything NotInstalled" fallback - see GetInstalledSnapshotAsync's own
// remarks for why that used to be tolerable per-app and is NOT tolerable now that one snapshot is
// authoritative for the entire catalog) so callers are forced to make an explicit, conscious
// decision about what an untrustworthy snapshot means for whatever they're doing with it.
public sealed class WingetSnapshotException : Exception
{
    public WingetSnapshotException(string message) : base(message) { }
    public WingetSnapshotException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Read-only, so unlike the uninstall/install executors there's no DryRun variant —
/// checking status never changes anything, same reasoning as AppInventoryService/
/// ProgramInventoryService.
/// </summary>
public class WingetStatusChecker
{
    private readonly IProcessRunner _processRunner;

    public WingetStatusChecker(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    // ONE `winget list` process for the whole catalog, instead of one per app (see
    // WingetInstalledSnapshot's own remarks) - InstallPageViewModel calls this once at the start
    // of a normal catalog load and once more after an install batch (to refresh only the
    // participating rows), never per individual app. No --id filter: this deliberately lists
    // everything winget currently knows is installed, so a single pass answers every catalog
    // app's status at once.
    //
    // Throws WingetSnapshotException (never silently returns Empty) whenever the result can't be
    // trusted - external review, correction of the first version of this design: a single winget
    // error, truncated output, or parser regression used to degrade to "everything NotInstalled"
    // (WingetInstalledSnapshot.Empty), which was tolerable when a failed per-app query only ever
    // affected that one row, but is NOT safe now that one query is authoritative for the WHOLE
    // catalog - it could make every already-installed app look like it needs installing, risking
    // duplicate/redundant installs. Callers must decide explicitly what an untrustworthy snapshot
    // means for them (see InstallPageViewModel.LoadCoreAsync/RefreshParticipantsAfterBatchAsync):
    // fail the load visibly on the initial fetch, or leave existing state untouched on a
    // post-install refresh. A structurally valid table with zero data rows is NOT an error - it's
    // a genuine, valid, empty snapshot (nothing winget currently reports as installed) - see
    // ParseSnapshot's own remarks for exactly where that line is drawn.
    public async Task<WingetInstalledSnapshot> GetInstalledSnapshotAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "winget",
            Arguments = "list --accept-source-agreements --disable-interactivity",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        ProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(psi, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WingetSnapshotException($"Failed to run winget: {ex.Message}", ex);
        }

        if (result.ExitCode != 0)
        {
            throw new WingetSnapshotException($"winget exited with code {result.ExitCode}.");
        }

        return new WingetInstalledSnapshot(ParseSnapshot(result.StandardOutput));
    }

    // Parses winget's own fixed-width `list` table by TABLE STRUCTURE, not by matching English
    // column-header text - external review, correction of the first version of this parser: this
    // toolkit is deployed on Norwegian Windows as well as English Windows, and winget localizes
    // its own column headers to match the OS display language, so matching literal strings like
    // "Name"/"Id"/"Version"/"Available"/"Source" silently breaks on any non-English machine.
    //
    // The table's header/divider boundary is found structurally: winget always separates its
    // header row from data rows with a solid horizontal rule of '-' characters - this line's
    // position (not its content) locates the header (the line immediately above it) and where
    // data rows begin (immediately below it), and is exactly as true on a Norwegian machine as an
    // English one. NOTE, verified directly against real captured output rather than assumed: this
    // divider is ONE CONTINUOUS run of '-' spanning the whole row width - it does NOT contain
    // separate per-column dash segments with gaps between them, so column boundaries below come
    // from the HEADER row's own token positions instead, not from the divider's shape.
    //
    // Column IDENTITY then comes from column ORDER (Name, Id, Version, [Available], Source), not
    // label text - locale-independent as long as no individual column label contains an embedded
    // space, which holds for every winget-supported display language's short column headers.
    // Verified this exact approach (structural divider location + order-based header tokens)
    // against real captured winget output AND a synthetic non-English relabeling of the same
    // real data in a standalone harness before wiring it in here - both produced byte-identical
    // parsed results.
    //
    // Throws WingetSnapshotException if the divider can't be found, or the header doesn't have
    // the expected column shape (4 or 5 tokens) - these mean the output couldn't be structurally
    // validated, which callers must treat as "the snapshot cannot be trusted," never as "zero
    // apps are installed." A divider that IS found, with a header that DOES have a valid shape,
    // but zero actual data rows below it, is a genuinely valid empty snapshot - not an error.
    private static Dictionary<string, AppInstallState> ParseSnapshot(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');

        var dividerIndex = Array.FindIndex(lines, l =>
        {
            var trimmed = l.TrimEnd('\r');
            return trimmed.Length >= 10 && trimmed.All(c => c == '-');
        });

        if (dividerIndex <= 0)
        {
            throw new WingetSnapshotException("Could not locate the winget list table header/divider in its output.");
        }

        var header = lines[dividerIndex - 1].TrimEnd('\r');

        // Start offset of every maximal non-whitespace run in the header - one per column label,
        // regardless of what language that label is written in.
        var tokenStarts = new List<int>();
        for (var i = 0; i < header.Length; i++)
        {
            if (!char.IsWhiteSpace(header[i]) && (i == 0 || char.IsWhiteSpace(header[i - 1])))
            {
                tokenStarts.Add(i);
            }
        }

        int idStart, versionStart, availableStart, sourceStart;
        switch (tokenStarts.Count)
        {
            case 5:
                // Name, Id, Version, Available, Source
                idStart = tokenStarts[1];
                versionStart = tokenStarts[2];
                availableStart = tokenStarts[3];
                sourceStart = tokenStarts[4];
                break;
            case 4:
                // Name, Id, Version, Source - winget omits the Available column header entirely
                // in some circumstances (e.g. nothing on the machine has an update pending).
                idStart = tokenStarts[1];
                versionStart = tokenStarts[2];
                availableStart = -1;
                sourceStart = tokenStarts[3];
                break;
            default:
                throw new WingetSnapshotException($"Unexpected winget list header shape ({tokenStarts.Count} columns).");
        }

        var result = new Dictionary<string, AppInstallState>(StringComparer.OrdinalIgnoreCase);

        for (var i = dividerIndex + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line) || line.Length <= idStart)
            {
                continue;
            }

            var idEnd = Math.Min(versionStart, line.Length);
            var id = line[idStart..idEnd].Trim();
            if (id.Length == 0)
            {
                // No winget package identity for this row - nothing to key a lookup by.
                continue;
            }

            var hasUpdate = false;
            if (availableStart >= 0 && line.Length > availableStart)
            {
                var availableEnd = sourceStart > availableStart ? Math.Min(sourceStart, line.Length) : line.Length;
                hasUpdate = line[availableStart..availableEnd].Trim().Length > 0;
            }

            result[id] = hasUpdate ? AppInstallState.UpdateAvailable : AppInstallState.Installed;
        }

        return result;
    }
}
