using System.Runtime.InteropServices;
using WUApiLib;

namespace Support_Tool.Services;

internal sealed record WindowsUpdateExecutionResult(
    int DownloadedCount, int InstalledCount, int FailedCount,
    bool AnyProgressMade, bool TimedOut, bool RebootRequired);

internal interface IWindowsUpdateExecutor
{
    /// <summary>
    /// Runs one self-driven search-download-install batch, reporting live per-update progress
    /// via onProgress as WUA's own callbacks fire. Both the download and install phases respect
    /// the given absolute deadline - a single BeginDownload()/BeginInstall() call is aborted
    /// rather than allowed to block past it. Returns after the whole batch finishes (or times
    /// out); callers loop this for retry-within-window behavior, same as the DetectNow()-era
    /// poll loop it replaces.
    /// </summary>
    WindowsUpdateExecutionResult DownloadAndInstall(
        Action<IReadOnlyList<UpdateStatusEntry>> onProgress,
        Action<string> onStatusChange,
        Logger logger,
        DateTime deadline);
}

internal sealed class DryRunWindowsUpdateExecutor : IWindowsUpdateExecutor
{
    public WindowsUpdateExecutionResult DownloadAndInstall(Action<IReadOnlyList<UpdateStatusEntry>> onProgress, Action<string> onStatusChange, Logger logger, DateTime deadline)
    {
        logger.Info("[DRY RUN] Would create a typed WUA session and self-drive Download()/Install() with progress callbacks.");
        return new WindowsUpdateExecutionResult(0, 0, 0, AnyProgressMade: false, TimedOut: false, RebootRequired: false);
    }
}

// Pure, COM-independent classification logic - kept separate from the COM orchestration below
// so it can be exercised via a throwaway InternalsVisibleTo console harness without needing a
// real WUA session. OperationResultCode is just a plain generated enum, safe to construct/pass
// around with no live COM object involved.
internal static class WindowsUpdateClassification
{
    /// <summary>
    /// How to represent an update WUA has already moved past (index &lt; CurrentUpdateIndex)
    /// during a live download progress callback. Moving on to a later update does not prove the
    /// earlier one succeeded - this is read from that item's own per-update result, not inferred
    /// from position alone.
    /// </summary>
    public static UpdateState ClassifyPastDownloadItemState(OperationResultCode resultCode, bool isDownloaded) => resultCode switch
    {
        OperationResultCode.orcSucceeded => UpdateState.Downloaded,
        // orcSucceededWithErrors means the operation completed but results may be incomplete -
        // never treated as automatic success; gated on the update's own verifiable IsDownloaded.
        OperationResultCode.orcSucceededWithErrors => isDownloaded ? UpdateState.Downloaded : UpdateState.Failed,
        OperationResultCode.orcFailed => UpdateState.Failed,
        OperationResultCode.orcAborted => UpdateState.Failed,
        // NotStarted/InProgress despite being before CurrentUpdateIndex - a genuinely odd edge
        // case; Pending is the most truthful representation available (no percent to show).
        _ => UpdateState.Pending,
    };

    /// <summary>Same idea as above, for a live install progress callback.</summary>
    public static UpdateState ClassifyPastInstallItemState(OperationResultCode resultCode, bool rebootRequired) => resultCode switch
    {
        OperationResultCode.orcSucceeded => rebootRequired ? UpdateState.PendingRestart : UpdateState.Installed,
        // Conservative by design: a callback-time SucceededWithErrors doesn't confirm a clean
        // installed state, so it's represented as Failed rather than inventing a success.
        OperationResultCode.orcSucceededWithErrors => UpdateState.Failed,
        OperationResultCode.orcFailed => UpdateState.Failed,
        OperationResultCode.orcAborted => UpdateState.Failed,
        _ => UpdateState.Pending,
    };

    /// <summary>
    /// DownloadedCount means updates that transitioned IsDownloaded: false -> true this pass -
    /// not "everything currently downloaded." Already-cached updates are still install-ready but
    /// don't count as new progress. This is the install-readiness gate itself: built from
    /// post-download IsDownloaded, not from OperationResultCode (the two don't always agree -
    /// e.g. orcSucceededWithErrors can still leave IsDownloaded true).
    /// </summary>
    public static (int downloadedCount, List<int> installReadyIndices) ClassifyDownloadBatch(
        IReadOnlyList<bool> preDownloadState, IReadOnlyList<bool> postDownloadState)
    {
        var downloadedCount = 0;
        var installReadyIndices = new List<int>();

        for (var i = 0; i < postDownloadState.Count; i++)
        {
            if (!postDownloadState[i])
            {
                continue;
            }

            installReadyIndices.Add(i);
            if (!preDownloadState[i])
            {
                downloadedCount++;
            }
        }

        return (downloadedCount, installReadyIndices);
    }

    public static bool CombineRebootSignals(bool windowsRebootStatus, bool wuaPreInstall, bool anyPostInstallPerUpdate) =>
        windowsRebootStatus || wuaPreInstall || anyPostInstallPerUpdate;

    public static TimeSpan RemainingTime(DateTime deadline, DateTime now)
    {
        var remaining = deadline - now;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }
}

internal sealed class RealWindowsUpdateExecutor : IWindowsUpdateExecutor
{
    // How long to wait for WUA's completed callback to still fire after RequestAbort() before
    // giving up and quarantining the job/callback references instead. WUA documents that the
    // completed callback still fires with an aborted status after a successful abort request -
    // this is the bounded window for that to happen.
    private static readonly TimeSpan AbortGracePeriod = TimeSpan.FromSeconds(30);

    // Rare-path quarantine: if WUA never acknowledges RequestAbort within the grace period, the
    // async job may still be running after DownloadAndInstall() itself returns. CleanUp() blocks
    // until the operation finishes, which would defeat the hard deadline this whole mechanism
    // exists to enforce - so instead of calling it, these references are kept strongly rooted
    // for the rest of the process's life so the GC can't collect them out from under an in-flight
    // WUA callback. At most one abandoned operation should ever need quarantining in a normal
    // run, since TimedOut propagates out and the update loop stops.
    private static readonly List<AbandonedWuaOperation> AbandonedOperations = [];

    private sealed record AbandonedWuaOperation(object Job, object ProgressCallback, object CompletedCallback);

    public WindowsUpdateExecutionResult DownloadAndInstall(Action<IReadOnlyList<UpdateStatusEntry>> onProgress, Action<string> onStatusChange, Logger logger, DateTime deadline)
    {
        // Own typed session - deliberately not sharing WindowsUpdateSearcher's dynamic session
        // object across the dynamic/typed boundary. Created via late-bound Activator.CreateInstance
        // (same ProgID WindowsUpdateSearcher already uses) rather than `new UpdateSessionClass()` -
        // EmbedInteropTypes=true (chosen so the single-file publish doesn't need a loose
        // Interop.WUApiLib.dll alongside it) blocks direct coclass instantiation (CS1752); the
        // interface returned here is still the real generated typed interface, not dynamic.
        var session = (UpdateSession)Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!)!;
        try
        {
            session.ClientApplicationID = "Elkjop Support Tool";
        }
        catch (COMException ex)
        {
            logger.Warn($"Could not set WUA ClientApplicationID: {ex.Message}");
        }

        var searcher = session.CreateUpdateSearcher();
        var searchResult = searcher.Search(WindowsUpdateSearcher.SearchCriteria);
        UpdateCollection updates = searchResult.Updates;

        if (updates.Count == 0)
        {
            return new WindowsUpdateExecutionResult(0, 0, 0, AnyProgressMade: false, TimedOut: false, RebootRequired: false);
        }

        // Required for Install() to proceed at all on updates that carry one - a new incidental
        // behavior versus the native DetectNow()-driven flow, necessary here since nothing else
        // in this unattended tool would ever accept one.
        foreach (IUpdate update in updates)
        {
            if (!update.EulaAccepted)
            {
                update.AcceptEula();
            }
        }

        var preDownloadState = new bool[updates.Count];
        for (var i = 0; i < updates.Count; i++)
        {
            preDownloadState[i] = updates[i].IsDownloaded;
        }

        var downloadCallback = new WindowsUpdateProgressCallback(updates, preDownloadState, onProgress);
        var downloader = session.CreateUpdateDownloader();
        downloader.Updates = updates;

        var downloadJob = downloader.BeginDownload(downloadCallback, downloadCallback, null);
        var downloadCompleted = downloadCallback.WaitForDownloadCompletion(
            WindowsUpdateClassification.RemainingTime(deadline, DateTime.UtcNow));

        if (!downloadCompleted)
        {
            return HandleDownloadTimeout(downloadJob, downloadCallback, downloader, logger);
        }

        IDownloadResult downloadResult;
        try
        {
            downloadResult = downloader.EndDownload(downloadJob);
        }
        finally
        {
            SafeCleanUp(downloadJob, logger);
        }

        var postDownloadState = new bool[updates.Count];
        for (var i = 0; i < updates.Count; i++)
        {
            var itemResult = downloadResult.GetUpdateResult(i);
            logger.Info($"Download result for '{updates[i].Title}': {itemResult.ResultCode} (HResult=0x{itemResult.HResult:X8}).");
            postDownloadState[i] = updates[i].IsDownloaded;
        }

        var (downloadedCount, installReadyIndices) = WindowsUpdateClassification.ClassifyDownloadBatch(preDownloadState, postDownloadState);
        logger.Info($"Self-driven download batch: {downloadedCount} newly downloaded, {installReadyIndices.Count}/{updates.Count} ready to install.");

        // Same EmbedInteropTypes constraint as the session above - `new UpdateCollectionClass()`
        // isn't allowed, so this goes through the documented "Microsoft.Update.UpdateColl" ProgID
        // (the standard way WUA scripting examples create a blank IUpdateCollection).
        UpdateCollection toInstall = (UpdateCollection)Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.UpdateColl")!)!;
        foreach (var i in installReadyIndices)
        {
            toInstall.Add(updates[i]);
        }

        var downloadOnlyResult = new WindowsUpdateExecutionResult(
            downloadedCount, InstalledCount: 0, FailedCount: 0,
            AnyProgressMade: downloadedCount > 0, TimedOut: false, RebootRequired: false);

        if (toInstall.Count == 0)
        {
            return downloadOnlyResult;
        }

        // Installer preflight - only reached once the download phase has completed.
        var installer = session.CreateUpdateInstaller();
        installer.AllowSourcePrompts = false; // No ForceQuiet - not all updates support it; this is the safer unattended baseline.

        var preInstallRebootRequired = WindowsRebootStatus.IsRebootRequired() || installer.RebootRequiredBeforeInstallation;
        if (preInstallRebootRequired)
        {
            logger.Info("Reboot required before installation can proceed. Skipping install this pass.");
            return downloadOnlyResult with { RebootRequired = true };
        }

        if (installer.IsBusy)
        {
            logger.Info("WUA installer reports busy. Will retry on the next pass.");
            return downloadOnlyResult;
        }

        installer.Updates = toInstall;
        var installCallback = new WindowsUpdateProgressCallback(toInstall, preDownloadState: null, onProgress);

        // All preflight checks passed and installation is actually about to start now - without
        // this, the whole download phase (which can take a while) ends with the grid just
        // sitting at "Downloaded, 100%" with no visible sign anything is about to happen next.
        onStatusChange("All updates downloaded. Starting installation...");

        IInstallationJob installJob;
        try
        {
            installJob = installer.BeginInstall(installCallback, installCallback, null);
        }
        catch (COMException ex)
        {
            // No safe way to observe the exact HRESULT WUA returns for "another installation is
            // already in progress" without triggering a real install race - out of scope for
            // this session (see the IsBusy preflight above, which already covers the common
            // case). Re-checking IsBusy after the exception is an empirically-groundable
            // heuristic rather than a guessed HRESULT constant - confirm/refine on first
            // real-machine run.
            if (installer.IsBusy)
            {
                logger.Info("WUA installation could not start because another installation is in progress. Will retry on the next pass.");
                return downloadOnlyResult;
            }

            logger.Error($"BeginInstall failed unexpectedly: {ex}");
            throw;
        }

        var installCompleted = installCallback.WaitForInstallCompletion(
            WindowsUpdateClassification.RemainingTime(deadline, DateTime.UtcNow));

        if (!installCompleted)
        {
            var timeoutResult = HandleInstallTimeout(installJob, installCallback, installer, logger);
            return timeoutResult with { DownloadedCount = downloadedCount };
        }

        IInstallationResult installResult;
        try
        {
            installResult = installer.EndInstall(installJob);
        }
        finally
        {
            SafeCleanUp(installJob, logger);
        }

        var installedCount = 0;
        var failedCount = 0;
        var anyPerUpdateRebootRequired = false;

        for (var i = 0; i < toInstall.Count; i++)
        {
            var itemResult = installResult.GetUpdateResult(i);
            var state = WindowsUpdateClassification.ClassifyPastInstallItemState(itemResult.ResultCode, itemResult.RebootRequired);
            logger.Info($"Install result for '{toInstall[i].Title}': {itemResult.ResultCode} (HResult=0x{itemResult.HResult:X8}, RebootRequired={itemResult.RebootRequired}).");

            if (state is UpdateState.Installed or UpdateState.PendingRestart)
            {
                installedCount++;
            }
            else
            {
                failedCount++;
            }

            if (itemResult.RebootRequired)
            {
                anyPerUpdateRebootRequired = true;
            }
        }

        // Final authoritative snapshot so the grid settles on real final states instead of the
        // last transient callback state (e.g. a row stuck showing "Installing").
        onProgress(BuildFinalInstallEntries(toInstall, installResult));

        var rebootRequired = WindowsUpdateClassification.CombineRebootSignals(
            windowsRebootStatus: false, // already checked pre-install above; a fresh post-install signal is what matters here
            wuaPreInstall: installResult.RebootRequired,
            anyPostInstallPerUpdate: anyPerUpdateRebootRequired);

        var anyProgressMade = downloadedCount > 0 || installedCount > 0;
        return new WindowsUpdateExecutionResult(downloadedCount, installedCount, failedCount, anyProgressMade, TimedOut: false, rebootRequired);
    }

    private WindowsUpdateExecutionResult HandleDownloadTimeout(IDownloadJob job, WindowsUpdateProgressCallback callback, IUpdateDownloader downloader, Logger logger)
    {
        logger.Warn("Download phase exceeded the run deadline. Requesting abort.");
        job.RequestAbort();
        var acknowledged = callback.WaitForDownloadCompletion(AbortGracePeriod);

        if (acknowledged)
        {
            try
            {
                downloader.EndDownload(job);
            }
            catch (COMException ex)
            {
                logger.Warn($"EndDownload after abort failed (expected for an aborted job): {ex.Message}");
            }

            SafeCleanUp(job, logger);
        }
        else
        {
            logger.Warn("WUA did not acknowledge RequestAbort within the grace period. The job and callback references will remain rooted until the process exits. No further Windows Update operation will be started.");
            AbandonedOperations.Add(new AbandonedWuaOperation(job, callback, callback));
        }

        return new WindowsUpdateExecutionResult(0, 0, 0, AnyProgressMade: false, TimedOut: true, RebootRequired: false);
    }

    private WindowsUpdateExecutionResult HandleInstallTimeout(IInstallationJob job, WindowsUpdateProgressCallback callback, IUpdateInstaller installer, Logger logger)
    {
        logger.Warn("Install phase exceeded the run deadline. Requesting abort.");
        job.RequestAbort();
        var acknowledged = callback.WaitForInstallCompletion(AbortGracePeriod);

        if (acknowledged)
        {
            try
            {
                installer.EndInstall(job);
            }
            catch (COMException ex)
            {
                logger.Warn($"EndInstall after abort failed (expected for an aborted job): {ex.Message}");
            }

            SafeCleanUp(job, logger);
        }
        else
        {
            logger.Warn("WUA did not acknowledge RequestAbort within the grace period. The job and callback references will remain rooted until the process exits. No further Windows Update operation will be started.");
            AbandonedOperations.Add(new AbandonedWuaOperation(job, callback, callback));
        }

        return new WindowsUpdateExecutionResult(0, 0, 0, AnyProgressMade: false, TimedOut: true, RebootRequired: false);
    }

    private static void SafeCleanUp(IDownloadJob job, Logger logger)
    {
        try
        {
            job.CleanUp();
        }
        catch (COMException ex)
        {
            logger.Warn($"WUA download job CleanUp() failed: {ex.Message}");
        }
    }

    private static void SafeCleanUp(IInstallationJob job, Logger logger)
    {
        try
        {
            job.CleanUp();
        }
        catch (COMException ex)
        {
            logger.Warn($"WUA install job CleanUp() failed: {ex.Message}");
        }
    }

    private static List<UpdateStatusEntry> BuildFinalInstallEntries(UpdateCollection toInstall, IInstallationResult installResult)
    {
        var entries = new List<UpdateStatusEntry>();

        for (var i = 0; i < toInstall.Count; i++)
        {
            var itemResult = installResult.GetUpdateResult(i);
            var state = WindowsUpdateClassification.ClassifyPastInstallItemState(itemResult.ResultCode, itemResult.RebootRequired);
            entries.Add(new UpdateStatusEntry(toInstall[i].Title, state, state is UpdateState.Installed or UpdateState.PendingRestart ? 100 : null));
        }

        return entries;
    }
}

// Implements all four WUA callback interfaces - matches the standard C# pattern for driving WUA
// downloads/installs with real progress (confirmed against a real open-source reference,
// samysilva/WUManager's WUAProgressManager.cs). One instance is constructed per phase (download
// vs install), scoped to that phase's own collection, so only the relevant Invoke overloads ever
// actually fire in practice - both are implemented regardless, since the interfaces require it.
internal sealed class WindowsUpdateProgressCallback :
    IDownloadProgressChangedCallback, IDownloadCompletedCallback,
    IInstallationProgressChangedCallback, IInstallationCompletedCallback
{
    private readonly UpdateCollection _collection;
    private readonly bool[]? _preDownloadState; // only meaningful for the download-phase instance
    private readonly Action<IReadOnlyList<UpdateStatusEntry>> _onProgress;
    private readonly ManualResetEventSlim _downloadDone = new(false);
    private readonly ManualResetEventSlim _installDone = new(false);

    // WUA reports live progress for exactly one "current" index at a time - it does NOT expose a
    // percent for every item simultaneously, and it can genuinely interleave which index it's
    // reporting on (observed on real hardware: an item with visible progress can be "left" while
    // WUA reports on a different one, then revisited later). Without remembering the last real
    // percent seen for each item, an item that had progressed would visually snap back to blank
    // the moment WUA's reporting focus moves elsewhere, even though nothing about its actual
    // download/install state changed - purely a display artifact of a single-item-at-a-time
    // progress API. These are sticky: only cleared (implicitly) once an item reaches a real
    // terminal state via GetUpdateResult.
    private readonly int?[] _lastKnownPercent;

    public WindowsUpdateProgressCallback(UpdateCollection collection, bool[]? preDownloadState, Action<IReadOnlyList<UpdateStatusEntry>> onProgress)
    {
        _collection = collection;
        _preDownloadState = preDownloadState;
        _onProgress = onProgress;
        _lastKnownPercent = new int?[collection.Count];
    }

    public void Invoke(IDownloadJob downloadJob, IDownloadProgressChangedCallbackArgs callbackArgs) =>
        _onProgress(BuildDownloadEntries(callbackArgs.Progress));

    public void Invoke(IDownloadJob downloadJob, IDownloadCompletedCallbackArgs callbackArgs) => _downloadDone.Set();

    public void Invoke(IInstallationJob installationJob, IInstallationProgressChangedCallbackArgs callbackArgs) =>
        _onProgress(BuildInstallEntries(callbackArgs.Progress));

    public void Invoke(IInstallationJob installationJob, IInstallationCompletedCallbackArgs callbackArgs) => _installDone.Set();

    public bool WaitForDownloadCompletion(TimeSpan timeout) => _downloadDone.Wait(timeout);

    public bool WaitForInstallCompletion(TimeSpan timeout) => _installDone.Wait(timeout);

    private List<UpdateStatusEntry> BuildDownloadEntries(IDownloadProgress progress)
    {
        var currentIndex = progress.CurrentUpdateIndex;
        var currentPercent = progress.CurrentUpdatePercentComplete;
        _lastKnownPercent[currentIndex] = currentPercent;
        var entries = new List<UpdateStatusEntry>();

        for (var i = 0; i < _collection.Count; i++)
        {
            var update = _collection[i];

            if (i < currentIndex)
            {
                var itemResult = progress.GetUpdateResult(i);
                var state = WindowsUpdateClassification.ClassifyPastDownloadItemState(itemResult.ResultCode, update.IsDownloaded);

                if (state == UpdateState.Downloaded)
                {
                    entries.Add(new UpdateStatusEntry(update.Title, state, 100));
                }
                else if (state == UpdateState.Pending && _lastKnownPercent[i] is { } remembered)
                {
                    // WUA hasn't reported this item as finished yet despite reporting focus
                    // having moved past it (interleaved/parallel downloading) - keep showing its
                    // last real percent instead of visually snapping back to blank.
                    entries.Add(new UpdateStatusEntry(update.Title, UpdateState.Downloading, remembered));
                }
                else
                {
                    entries.Add(new UpdateStatusEntry(update.Title, state, null));
                }
            }
            else if (i == currentIndex)
            {
                entries.Add(new UpdateStatusEntry(update.Title, UpdateState.Downloading, currentPercent));
            }
            else if (_lastKnownPercent[i] is { } previouslySeen)
            {
                // Was the "current" item earlier in this same download pass, then WUA's
                // reporting focus moved on without finishing it - same sticky treatment.
                entries.Add(new UpdateStatusEntry(update.Title, UpdateState.Downloading, previouslySeen));
            }
            else
            {
                var alreadyDownloaded = _preDownloadState is not null && _preDownloadState[i];
                entries.Add(alreadyDownloaded
                    ? new UpdateStatusEntry(update.Title, UpdateState.Downloaded, 100)
                    : new UpdateStatusEntry(update.Title, UpdateState.Pending, null));
            }
        }

        return entries;
    }

    private List<UpdateStatusEntry> BuildInstallEntries(IInstallationProgress progress)
    {
        var currentIndex = progress.CurrentUpdateIndex;
        var currentPercent = progress.CurrentUpdatePercentComplete;
        _lastKnownPercent[currentIndex] = currentPercent;
        var entries = new List<UpdateStatusEntry>();

        for (var i = 0; i < _collection.Count; i++)
        {
            var update = _collection[i];

            if (i < currentIndex)
            {
                var itemResult = progress.GetUpdateResult(i);
                var state = WindowsUpdateClassification.ClassifyPastInstallItemState(itemResult.ResultCode, itemResult.RebootRequired);

                if (state is UpdateState.Installed or UpdateState.PendingRestart)
                {
                    entries.Add(new UpdateStatusEntry(update.Title, state, 100));
                }
                else if (state == UpdateState.Pending && _lastKnownPercent[i] is { } remembered)
                {
                    entries.Add(new UpdateStatusEntry(update.Title, UpdateState.Installing, remembered));
                }
                else
                {
                    entries.Add(new UpdateStatusEntry(update.Title, state, null));
                }
            }
            else if (i == currentIndex)
            {
                entries.Add(new UpdateStatusEntry(update.Title, UpdateState.Installing, currentPercent));
            }
            else if (_lastKnownPercent[i] is { } previouslySeen)
            {
                entries.Add(new UpdateStatusEntry(update.Title, UpdateState.Installing, previouslySeen));
            }
            else
            {
                // Every member of the install collection was already verified IsDownloaded
                // before BeginInstall() was called - honest, not an assumption.
                entries.Add(new UpdateStatusEntry(update.Title, UpdateState.Downloaded, 100));
            }
        }

        return entries;
    }
}
