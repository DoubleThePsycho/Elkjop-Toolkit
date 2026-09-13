using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Support_Tool.Models;
using Toolkit.Shared.Configuration;

namespace Support_Tool.Services;

/// <summary>
/// Runs `winget install` for a new app, or `winget upgrade` when the caller already
/// knows an update is available — winget's own `install` command doesn't reliably
/// double as upgrade across versions, so the verb is chosen explicitly here.
/// </summary>
/// <remarks>
/// Deliberately does not use the shared IProcessRunner (which only returns a final buffered
/// result once the process exits) - a technician watching a multi-minute Battle.net/Steam/etc.
/// download needs to see real intra-item progress as it happens, so this drives its own Process
/// with line-by-line streaming, mirroring the same bypass WingetPrerequisiteService already uses
/// for its own live repair-script streaming.
/// </remarks>
public class RealInstallExecutor : IInstallExecutor
{
    // APPINSTALLER_CLI_ERROR_INSTALL_LOCATION_REQUIRED. winget's own documented exit code for "this
    // package's manifest requires --location and none was given" - checked generically (never a
    // per-app if-statement) so any current or future catalog entry that needs a location, not just
    // Battle.net, gets the same actionable message the moment its InstallLocation is genuinely
    // empty/missing from config.json.
    private const int InstallLocationRequiredExitCode = unchecked((int)0x8A15005F);

    public async Task<InstallResult> InstallAsync(InstallableApp app, AppInstallState currentState, IProgress<double>? itemProgress = null, CancellationToken ct = default)
    {
        var verb = currentState == AppInstallState.UpdateAvailable ? "upgrade" : "install";

        var psi = new ProcessStartInfo
        {
            FileName = "winget",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // ArgumentList (not a manually-quoted Arguments string) so an install location containing
        // spaces is passed correctly without hand-rolled quoting logic - .NET builds the native
        // command line for us.
        psi.ArgumentList.Add(verb);
        psi.ArgumentList.Add("--id");
        psi.ArgumentList.Add(app.WingetId);
        psi.ArgumentList.Add("--exact");
        psi.ArgumentList.Add("--silent");
        psi.ArgumentList.Add("--accept-package-agreements");
        psi.ArgumentList.Add("--accept-source-agreements");
        psi.ArgumentList.Add("--disable-interactivity");

        // Some winget manifests (e.g. Blizzard.BattleNet) require an explicit install location
        // and otherwise fail with APPINSTALLER_CLI_ERROR_INSTALL_LOCATION_REQUIRED
        // (0x8A15005F / -1978335137) - generic per-app config, not a Battle.net-specific
        // if-statement. Every existing catalog entry has InstallLocation null/empty and is
        // completely unaffected - the exact same command as before is sent for those.
        if (!string.IsNullOrWhiteSpace(app.InstallLocation))
        {
            psi.ArgumentList.Add("--location");
            psi.ArgumentList.Add(Environment.ExpandEnvironmentVariables(app.InstallLocation));
        }

        var stdOutLines = new List<string>();
        var stdErrLines = new List<string>();
        var linesLock = new object();
        var progressTracker = new WingetProgressTracker(itemProgress);

        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (linesLock)
                {
                    stdOutLines.Add(e.Data);
                }

                progressTracker.ReportIfTrustworthy(e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }

                lock (linesLock)
                {
                    stdErrLines.Add(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct);

            string stdOut, stdErr;
            lock (linesLock)
            {
                stdOut = string.Join(Environment.NewLine, stdOutLines);
                stdErr = string.Join(Environment.NewLine, stdErrLines);
            }

            if (process.ExitCode == 0)
            {
                // Completion is itself a trustworthy 100% - InstallService's own batch-level
                // Completed/Total step is what actually moves the needle on into the next item;
                // this just closes out this item's own 0-100 arc cleanly rather than leaving it
                // frozen at whatever the last parsed line happened to be.
                itemProgress?.Report(100);
                return new InstallResult { App = app, Status = InstallOutcome.Succeeded };
            }

            // Generic, not Battle.net-specific: any package whose manifest requires --location,
            // reported via this exact winget exit code, with none configured for it.
            if (process.ExitCode == InstallLocationRequiredExitCode && string.IsNullOrWhiteSpace(app.InstallLocation))
            {
                return new InstallResult
                {
                    App = app,
                    Status = InstallOutcome.Failed,
                    ExitCode = process.ExitCode,
                    ErrorMessage = $"{app.Name} requires an install location, but none is configured."
                };
            }

            // Prefer a real, human-readable explanation over a bare signed HRESULT - winget
            // usually explains a failure on stderr, but sometimes only on stdout (interleaved
            // with its own animated progress-bar/spinner glyphs), so both are inspected before
            // ever falling back to the raw exit code. No hardcoded table of known winget error
            // codes/messages - this is a generic text-cleanup pass, not error-specific logic.
            var errorText = ExtractMeaningfulText(stdErr)
                ?? ExtractMeaningfulText(stdOut)
                ?? $"winget exited with code {process.ExitCode}.";

            return new InstallResult
            {
                App = app,
                Status = InstallOutcome.Failed,
                ExitCode = process.ExitCode,
                ErrorMessage = errorText
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new InstallResult
            {
                App = app,
                Status = InstallOutcome.Failed,
                ErrorMessage = $"Failed to start winget: {ex.Message}"
            };
        }
    }

    // winget's console output mixes real explanatory text with animated progress-bar/spinner
    // glyphs (drawn via \r line-redraws and Unicode block characters), plus standard license/
    // attribution boilerplate that appears on essentially every install regardless of outcome.
    // Splitting on both \r and \n and keeping only lines that actually look like words (at least
    // a few letters) discards the animation noise without needing to understand winget's specific
    // rendering; dropping the known boilerplate lines on top of that stops them from crowding out
    // the actual error when it happens to be short. The most relevant explanation is usually near
    // the end of the output, so only the last few meaningful lines are kept rather than the entire
    // (possibly large) capture. Full raw output is still captured by the caller's InstallLogger
    // regardless of what this returns.
    private static string? ExtractMeaningfulText(string raw, int maxLines = 5)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var meaningfulLines = raw
            .Split('\r', '\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && line.Count(char.IsLetter) >= 3)
            .Where(line => !IsKnownBoilerplate(line))
            .ToList();

        if (meaningfulLines.Count == 0)
        {
            return null;
        }

        var tail = meaningfulLines.Count > maxLines
            ? meaningfulLines.Skip(meaningfulLines.Count - maxLines)
            : meaningfulLines;

        return string.Join(Environment.NewLine, tail);
    }

    // Standard winget boilerplate that appears near the start of essentially every install
    // regardless of what actually happens - a targeted denylist, not an attempt to understand
    // every possible winget message. Full raw output always still goes to the log.
    private static bool IsKnownBoilerplate(string line) =>
        line.StartsWith("Found ", StringComparison.Ordinal)
        || line.StartsWith("This application is licensed to you by its owner", StringComparison.OrdinalIgnoreCase)
        || line.StartsWith("Microsoft is not responsible for", StringComparison.OrdinalIgnoreCase);

    // Parses only trustworthy, explicitly-numeric progress out of winget's raw console lines -
    // never the visual width of its Unicode block-character bar, never an elapsed-time estimate.
    // Kept as its own small type (rather than inline lambdas) so the three real sources it
    // recognizes, and the "never move backwards within one item" retention rule, are easy to
    // follow in one place.
    private sealed class WingetProgressTracker
    {
        // ConEmu/Windows Terminal taskbar-progress OSC sequence - winget's own ConsoleProgressBar
        // emits this in addition to human-readable text when the terminal supports it:
        // ESC ] 9 ; 4 ; <state> ; <percent> (ESC \ or BEL). State 1 = normal determinate progress;
        // 0/2/3/4 (removed/error/indeterminate/paused) carry no trustworthy percentage and are
        // deliberately ignored rather than guessed at.
        private static readonly Regex OscProgress = new(
            "]9;4;(?<state>[0-9]);(?<percent>[0-9]{1,3})",
            RegexOptions.Compiled);

        // An explicit standalone percentage in the human-readable text, e.g. the number winget
        // prints next to/inside its progress bar. Bounded to 1-3 digits so a stray "100" can't
        // swallow unrelated larger numbers.
        private static readonly Regex PercentText = new(
            @"(?<!\d)(?<percent>\d{1,3}(?:\.\d+)?)\s?%(?!\d)",
            RegexOptions.Compiled);

        // "1.99 MB / 5.00 MB" style byte counters winget prints while downloading. Computing a
        // ratio from two real byte counts is exactly as trustworthy as an explicit percentage.
        private static readonly Regex ByteRatio = new(
            @"(?<downloaded>[\d.]+)\s?(?<downloadedUnit>B|KB|MB|GB)\s*/\s*(?<total>[\d.]+)\s?(?<totalUnit>B|KB|MB|GB)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private readonly IProgress<double>? _itemProgress;
        private double _lastReported = -1;

        public WingetProgressTracker(IProgress<double>? itemProgress)
        {
            _itemProgress = itemProgress;
        }

        public void ReportIfTrustworthy(string rawLine)
        {
            if (_itemProgress is null)
            {
                return;
            }

            var percent = TryParse(rawLine);
            if (percent is not { } value)
            {
                return;
            }

            value = Math.Clamp(value, 0, 100);

            // Retained monotonically within this one item's run - a later out-of-order or noisy
            // snapshot must never visually walk the bar backwards mid-download/install. The next
            // item always starts its own fresh tracker instance at -1, so this never bleeds across
            // the batch-level item boundary.
            if (value <= _lastReported)
            {
                return;
            }

            _lastReported = value;
            _itemProgress.Report(value);
        }

        private static double? TryParse(string rawLine)
        {
            var osc = OscProgress.Match(rawLine);
            if (osc.Success && osc.Groups["state"].Value == "1"
                && double.TryParse(osc.Groups["percent"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var oscPercent))
            {
                return oscPercent;
            }

            var percentText = PercentText.Match(rawLine);
            if (percentText.Success
                && double.TryParse(percentText.Groups["percent"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var textPercent))
            {
                return textPercent;
            }

            var byteRatio = ByteRatio.Match(rawLine);
            if (byteRatio.Success)
            {
                var downloaded = ToBytes(byteRatio.Groups["downloaded"].Value, byteRatio.Groups["downloadedUnit"].Value);
                var total = ToBytes(byteRatio.Groups["total"].Value, byteRatio.Groups["totalUnit"].Value);
                if (downloaded is { } d && total is { } t && t > 0 && d <= t)
                {
                    return d / t * 100.0;
                }
            }

            return null;
        }

        private static double? ToBytes(string amountText, string unit)
        {
            if (!double.TryParse(amountText, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                return null;
            }

            var multiplier = unit.ToUpperInvariant() switch
            {
                "B" => 1.0,
                "KB" => 1024.0,
                "MB" => 1024.0 * 1024.0,
                "GB" => 1024.0 * 1024.0 * 1024.0,
                _ => 0.0,
            };

            return multiplier > 0 ? amount * multiplier : null;
        }
    }
}
