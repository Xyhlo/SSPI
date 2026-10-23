using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal sealed partial class DownloadManager
    {
        void RefreshResidentArchive(DlItem item)
        {
            int attempt = item.AttemptId;
            string generation = item.ResidentGeneration;
            Func<bool> savedCancelStillCurrent = () =>
            {
                lock (_lock)
                {
                    DlItem current = Find(item.Id);
                    return object.ReferenceEquals(current, item) && current.AttemptId == attempt &&
                        current.CancelRequested &&
                        string.Equals(current.ResidentGeneration ?? "", generation ?? "", StringComparison.Ordinal);
                }
            };
            Func<Action, bool> publishSavedCancelIfCurrent = publish =>
            {
                lock (_lock)
                {
                    DlItem current = Find(item.Id);
                    bool isCurrent = object.ReferenceEquals(current, item) && current.AttemptId == attempt &&
                        current.CancelRequested &&
                        string.Equals(current.ResidentGeneration ?? "", generation ?? "", StringComparison.Ordinal);
                    if (isCurrent && publish != null) publish();
                    return isCurrent;
                }
            };
            if (item.ResidentRemovePending)
            {
                // Read outside _lock (a status read may retry briefly). A worker that cannot
                // stop the job reports why; show that instead of an endless generic wait.
                ResidentDownloadStatus removal;
                string blocked = ResidentDownloadService.TryGetStatus(item.Id, out removal) &&
                    removal.State == "failed" && !string.IsNullOrEmpty(removal.Error) ? removal.Error : null;
                // A worker that is not running can never acknowledge. After a minute with
                // no heartbeat at all, retire its pre-install record here so the removal
                // finishes like any other.
                bool workerSeen = ResidentDownloadService.WorkerHeartbeatSeen;
                // Only this build's worker applies Remove anyway; an older one does after a PS4 restart.
                string workerState = ResidentDownloadService.DetachWorkerState;
                long now = DateTime.UtcNow.Ticks;
                bool retireOrphan, finishDetached;
                lock (_lock)
                {
                    if (!object.ReferenceEquals(Find(item.Id), item)) return;
                    if (item.ResidentRemoveStarted == 0) item.ResidentRemoveStarted = now;
                    if (workerSeen) item.ResidentRemoveSilentSince = 0;
                    else if (item.ResidentRemoveSilentSince == 0) item.ResidentRemoveSilentSince = now;
                    retireOrphan = !workerSeen && now - item.ResidentRemoveSilentSince >= TimeSpan.TicksPerMinute &&
                        !_activeIds.Contains(item.Id) && !item.InstallSubmitted && item.BgftTaskId < 0;
                    // Remove anyway that the running worker cannot apply now (an older build until the
                    // PS4 restarts, or none running), or did not apply within a minute: the durable
                    // request stays on disk for the worker and the row goes now, keeping its files.
                    if (item.ResidentDetachRequested && item.ResidentDetachSentAt == 0) item.ResidentDetachSentAt = now;
                    finishDetached = item.ResidentDetachRequested && !_activeIds.Contains(item.Id) &&
                        ((workerState != "current" && workerState != "unknown") || now - item.ResidentDetachSentAt >= TimeSpan.TicksPerMinute);
                }
                if (retireOrphan) ResidentDownloadService.TryRetireOrphanedStagedJob(item.Id, generation);
                if (finishDetached)
                {
                    ReleaseResidentJob(item, attempt, generation, true); // republish the request, off the lock
                    lock (_lock)
                    {
                        if (!object.ReferenceEquals(Find(item.Id), item) || _activeIds.Contains(item.Id)) return;
                        FinishDetachedRemoval(item, !ResidentDownloadService.HasJob(item.Id), workerState);
                    }
                    return;
                }
                lock (_lock)
                {
                    if (!object.ReferenceEquals(Find(item.Id), item)) return;
                    if (_activeIds.Contains(item.Id) || ResidentDownloadService.HasJob(item.Id))
                    {
                        ReleaseResidentJob(item, attempt, generation, true);
                        // After a minute without acknowledgement the removal is bounded by the
                        // user's choice: removing it again stops tracking it (Remove anyway).
                        bool stuck = !item.ResidentDetachRequested && now - item.ResidentRemoveStarted >= TimeSpan.TicksPerMinute;
                        if (stuck && !item.ResidentRemoveStuck)
                            SspiLog.Write("download", "event=remove-unconfirmed job=" + item.Id + " worker=" + workerState +
                                " choice=remove-anyway-offered detail=" + (blocked ?? "no worker acknowledgement"));
                        item.ResidentRemoveStuck = stuck;
                        item.StatusText = item.ResidentDetachRequested ? "Stopping tracking; waiting for the background worker to release it"
                            : !stuck ? (blocked != null ? "Removal waiting for the background worker: " + blocked
                                : !workerSeen ? "Background worker is not running; removal finishes within a minute"
                                : "Removing background job; waiting for worker acknowledgement")
                            : workerState == "previous-build" ? "Restart the PS4 so the updated background worker can finish removing it, or remove it again to stop tracking it now"
                            : !workerSeen ? "Background worker is not running. Remove again to stop tracking it; downloaded files are kept"
                            : "Remove again to stop tracking it; the PS4 download and files are kept. " +
                                (blocked != null ? "The PS4 did not confirm stopping it: " + blocked : "The background worker has not confirmed the removal");
                        return;
                    }
                    // The worker has released the job, so a later cleanup failure is not an
                    // unconfirmed removal and must not offer Remove anyway.
                    item.ResidentRemoveStuck = false;
                    if (item.ResidentDetachRequested) { FinishDetachedRemoval(item, true, workerState); return; }
                    // A released job can keep an install journal that is not stopped: an add-on that
                    // AppInstUtil accepted or a child the PS4 finished. Its files stay, as for any
                    // unconfirmed installation, instead of cleanup waiting on that journal forever.
                    if (!item.InstallConfirmed && !item.InstallSubmitted && HasUnstoppedResidentJournal(item))
                    {
                        item.InstallSubmitted = true;
                        SspiLog.Write("download", "event=remove-files-retained job=" + item.Id + " reason=unconfirmed-install-journal");
                    }
                    // The native owner removes its durable job only after readers, transfer
                    // threads and any owned BGFT task have stopped. Until then keep the data.
                    // Durable ownership is already gone. A missing heartbeat
                    // after shutdown must not strand an acknowledged removal.
                    // A submitted install may still be reading its input. Keep the
                    // row visible until package-level completion can be confirmed.
                    if (item.InstallSubmitted && !item.InstallConfirmed)
                    {
                        item.ResidentRemovePending = item.RemoveRequested = item.CancelRequested = false;
                        item.Background = item.ResidentArchive = item.ResidentStaged = false;
                        item.State = DlState.Canceled;
                        item.StatusText = "PS4 installation unconfirmed; package retained";
                        SaveManifest();
                        return;
                    }
                    // The worker no longer owns this job, so stop showing it as resident-owned
                    // while cleanup is retried. Its resident identity stays until cleanup succeeds.
                    bool normalized = item.Background || item.BgftResident || item.ResidentRetryPending ||
                        item.ResidentPauseDesired.HasValue || item.PauseRequested || item.State != DlState.Canceled;
                    item.Background = item.BgftResident = false;
                    item.ResidentRetryPending = false;
                    item.ResidentPauseDesired = null;
                    item.PauseRequested = false;
                    item.State = DlState.Canceled;
                    item.BytesPerSec = item.EtaSeconds = 0;
                    string previousStatus = item.StatusText, cleanupError = null;
                    if (!TryCleanupFanOutPending(item)) cleanupError = "Extracted package cleanup pending";
                    else TryCleanupOwnedFiles(item, out cleanupError);
                    if (cleanupError != null)
                    {
                        item.StatusText = cleanupError;
                        if (normalized || !string.Equals(previousStatus, cleanupError, StringComparison.Ordinal)) SaveManifest();
                        return;
                    }
                    PreserveConfirmedDependency(item);
                    _items.Remove(item);
                    SaveManifest();
                }
                return;
            }
            if (item.ResidentRetryPending)
            {
                lock (_lock)
                {
                    if (!object.ReferenceEquals(Find(item.Id), item) || !AcceptInstallCallback(item, attempt)) return;
                    // The job file disappears only after the native lanes have stopped.
                    // Never start another writer while the old signed URL still owns the file.
                    if (ResidentDownloadService.HasJob(item.Id))
                    {
                        ReleaseResidentJob(item, attempt, generation);
                        item.StatusText = "Waiting for background transfer to stop before retry";
                        return;
                    }
                    bool nativeRestart = IsNativeBgftRow(item);
                    ClearBackground(item);
                    item.ResidentArchive = false;
                    item.ResidentRetryPending = false;
                    item.State = item.CancelRequested ? DlState.Canceled : item.PauseRequested ? DlState.Paused : DlState.Queued;
                    // The released native task took its received bytes with it.
                    if (nativeRestart) item.Done = 0;
                    item.StatusText = item.CancelRequested ? "Canceled; partial download retained" :
                        item.PauseRequested ? "Paused; partial download retained" :
                        nativeRestart ? "Preparing retry; the PS4 download restarts from the beginning" : "Preparing retry; verified files retained";
                    item.CancelRequested = item.PauseRequested = false;
                    item.Error = null; item.BytesPerSec = 0; item.EtaSeconds = 0;
                    item.StatsInitialized = false;
                    // A resident retry must resolve a new URL, even if this job
                    // previously had a foreground 429/5xx retry with a cached link.
                    item.HttpRetryUrl = null; item.TransientHttpRetries = 0; item.HttpRetryRenewPending = false; item.HostSupportRetries = 0;
                    item.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(2).Ticks;
                    SaveManifest();
                }
                return;
            }
            ResidentDownloadStatus status;
            if (item.CancelRequested)
            {
                // Reissue the saved command: a crash may have happened between the
                // manifest commit and IPC publication, or the worker may have restarted.
                string cancelError;
                ResidentDownloadService.TryCancel(item.Id, item.BgftTaskId, generation,
                    publishSavedCancelIfCurrent, out cancelError);
            }
            if (!ResidentDownloadService.TryGetStatus(item.Id, item.ResidentGeneration, out status))
            {
                bool hasDownloader = ResidentDownloadService.HasDownloader;
                bool hasJob = ResidentDownloadService.HasJob(item.Id);
                bool canFinishCancel;
                int legacyTask;
                lock (_lock)
                {
                    if (!object.ReferenceEquals(Find(item.Id), item) || !AcceptInstallCallback(item, attempt)) return;
                    canFinishCancel = item.CancelRequested && !_activeIds.Contains(item.Id) && hasDownloader && !hasJob;
                    legacyTask = item.BgftTaskId;
                }
                bool legacyCleaned = false;
                if (canFinishCancel && legacyTask >= 0 && !ResidentDownloadService.OwnsBgftLifetime(item.Id))
                {
                    string cleanupError;
                    if (!ResidentDownloadService.TryCancelLegacyBgftTask(item.Id, legacyTask,
                        generation, savedCancelStillCurrent, out cleanupError))
                    {
                        lock (_lock)
                        {
                            if (!object.ReferenceEquals(Find(item.Id), item) || !AcceptInstallCallback(item, attempt)) return;
                            item.StatusText = "Cancellation saved; waiting for legacy BGFT task cleanup";
                            SaveManifest();
                        }
                        return;
                    }
                    legacyCleaned = true;
                }
                lock (_lock) {
                    if (!object.ReferenceEquals(Find(item.Id), item) || !AcceptInstallCallback(item, attempt)) return;
                    if (item.CancelRequested && !_activeIds.Contains(item.Id) &&
                        hasDownloader && !hasJob)
                    {
                        if (legacyCleaned) item.BgftTaskId = -1;
                        if (RetainUnconfirmedAddonFiles(item)) item.InstallSubmitted = true;
                        ClearBackground(item); item.ResidentArchive = false;
                        item.CancelRequested = false; item.State = DlState.Canceled;
                        item.Error = null; item.BytesPerSec = 0; item.EtaSeconds = 0;
                        item.StatusText = "Canceled; downloaded files retained";
                        SaveManifest();
                        return;
                    }
                    item.StatusText = item.CancelRequested ? "Cancellation saved; waiting for background acknowledgement" : ResidentDownloadService.HasDownloader
                        ? "Waiting for shell download acknowledgement" : "Waiting for GoldHEN shell downloader";
                }
                return;
            }
            // Old feeder records can require an app attachment. Generation-bound jobs
            // own BGFT registration, task journaling, starting and completion themselves.
            if (status.State == "awaiting-bgft" && !ResidentDownloadService.OwnsBgftLifetime(item.Id))
            {
                bool attached = ResidentDownloadService.TryAttachPendingBgft(item.Id, out int attachedTask);
                if (!attached && attachedTask < 0) attachedTask = -1;
                if (attachedTask >= 0)
                lock (_lock)
                {
                    if (object.ReferenceEquals(Find(item.Id), item) && item.AttemptId == attempt)
                    {
                        item.BgftTaskId = attachedTask;
                        if (!SaveManifest())
                            item.StatusText = "BGFT task attached; queue ownership save will retry";
                    }
                }
            }
            bool changed = false, release = false;
            lock (_lock)
            {
                if (!object.ReferenceEquals(Find(item.Id), item) || !AcceptInstallCallback(item, attempt) ||
                    !item.ResidentArchive || !item.Background) return;
                if (CanRenewResidentLink(item, status, CanRefreshUnlock(item)))
                {
                    if (status.Done > item.ResidentLastRenewalBytes) item.ResidentLinkRenewals = 0;
                    item.ResidentLinkRenewals++;
                    item.ResidentLastRenewalBytes = status.Done;
                    item.ResidentRetryPending = true;
                    item.State = DlState.Resolving;
                    item.StatusText = "Renewing expired download link";
                    item.Error = null; item.BytesPerSec = 0; item.EtaSeconds = 0;
                    if (SaveManifest()) ReleaseResidentJob(item, attempt, item.ResidentGeneration);
                    else
                    {
                        item.ResidentRetryPending = false;
                        item.ResidentLinkRenewals--;
                    }
                    return;
                }
                if (item.State == DlState.Failed && status.State == "failed" && item.Error == status.Error) return;
                if (item.ResidentStaged && !item.ResidentAutoInstall && status.State == "staged")
                {
                    long length;
                    try { length = File.Exists(item.DestPath) ? new FileInfo(item.DestPath).Length : -1; }
                    catch (IOException) { item.StatusText = "Waiting to inspect staged download"; return; }
                    catch (UnauthorizedAccessException) { item.StatusText = "Waiting for access to staged download"; return; }
                    if (TryAcceptStagedPackage(item, status, length))
                    {
                        // Persist adoption before retiring native ownership. No BGFT success is
                        // inferred here; the queued local package still has all install gates.
                        if (SaveManifest()) ReleaseResidentJob(item, attempt, item.ResidentGeneration);
                        else
                        {
                            item.Background = item.BgftResident = item.ResidentArchive = item.ResidentStaged = true;
                            item.State = DlState.Finalizing;
                        }
                    }
                    return;
                }
                // The resident publishes a separate expanded total during extraction.
                UpdateLiveTransferStats(item, status.Done, status.Total,
                    status.NetworkBytes, status.State, TransferClockMs());
                item.StatusText = status.State == "extracting" ? (!string.IsNullOrEmpty(status.Error) ? status.Error : "Extracting: reading archive headers") :
                    status.State == "installing" ? "Installing packages in order" :
                    status.State == "validating" ? "Verifying package integrity · " + Human(status.Done) + " checked" : "Resident download: " + status.State;
                ApplyResidentTransferPhase(item, status);
                if ((status.State == "feeding" || status.State == "installing") && !string.IsNullOrEmpty(status.Error))
                    item.StatusText = status.Error;
                // Reported 2 Oct: rows sat at "Resident download: feeding" with no progress.
                // After 5 minutes without new bytes, say so and what to do.
                if (status.State == "feeding" && string.IsNullOrEmpty(status.Error))
                {
                    long now = DateTime.UtcNow.Ticks, seen = status.Done + status.NetworkBytes;
                    if (item.FeedWatchTicks == 0 || seen != item.FeedWatchBytes) { item.FeedWatchBytes = seen; item.FeedWatchTicks = now; }
                    else if (now - item.FeedWatchTicks >= TimeSpan.FromMinutes(5).Ticks)
                        item.StatusText = "No download progress for 5 minutes. Pause and resume it; if it stays stuck, restart the PS4 and enable GoldHEN again.";
                }
                else item.FeedWatchTicks = 0;
                if (item.CancelRequested && status.State != "installed" && status.State != "failed" && status.State != "canceled")
                    item.StatusText = "Canceling background job; waiting for worker acknowledgement";
                if (status.State == "installed")
                {
                    item.State = DlState.Installed; item.InstallConfirmed = true; item.InstallOrderReady = true;
                    item.Background = false; item.BgftResident = false; item.Error = null;
                    item.BytesPerSec = 0; item.EtaSeconds = 0; CompleteByteCounters(item);
                    item.StatusText = "Installed · verified by PS4";
                    release = true; changed = true;
                }
                else if (status.State == "submitted")
                {
                    bool trackingTheme = (status.Error ?? "").StartsWith("THEME_UNCONFIRMED:", StringComparison.Ordinal);
                    item.State = !trackingTheme && item.CancelRequested ? DlState.Canceled : DlState.Submitted;
                    if (!trackingTheme) item.CancelRequested = false;
                    item.InstallConfirmed = false; item.InstallOrderReady = false;
                    item.InstallSubmitted = true;
                    item.Background = trackingTheme; item.BgftResident = trackingTheme;
                    item.StatusText = trackingTheme ? "Theme sent to PS4; checking Settings > Themes. Files retained" : "Packages sent to PS4 in order; files retained";
                    release = !trackingTheme; changed = true;
                }
                else if (status.State == "failed" || status.State == "canceled")
                {
                    item.BytesPerSec = 0; item.EtaSeconds = 0;
                    item.State = status.State == "canceled" ? DlState.Canceled : DlState.Failed;
                    item.Error = status.Error;
                    if ((status.Error ?? "").StartsWith("DLC_UNCONFIRMED:", StringComparison.Ordinal) ||
                        (status.Error ?? "").StartsWith("THEME_UNCONFIRMED:", StringComparison.Ordinal))
                        item.InstallSubmitted = true;
                    item.StatusText = !string.IsNullOrEmpty(status.Error) ? status.Error :
                        status.State == "canceled" ? "Resident download canceled; files retained" : "Resident download failed; files retained";
                    if (status.State == "canceled")
                    {
                        if (RetainUnconfirmedAddonFiles(item)) item.InstallSubmitted = true;
                        item.Background = false; item.BgftResident = false; item.CancelRequested = false;
                        release = true;
                    }
                    // Keep ownership: native installation journals may reference a live task.
                    changed = true;
                }
            }
            if (changed)
            {
                if (SaveManifest()) { if (release) ReleaseResidentJob(item, attempt, item.ResidentGeneration); }
                else if (release) lock (_lock) { item.Background = true; item.BgftResident = true; }
            }
        }

        internal static bool CanRenewResidentLink(DlItem item, ResidentDownloadStatus status, bool canResolve)
        {
            return item != null && status != null && canResolve && item.ResidentStaged && item.Background &&
                !item.BgftLocalInstall && !item.ResidentRetryPending &&
                !item.CancelRequested && !item.PauseRequested && item.State != DlState.Canceled &&
                (item.ResidentLinkRenewals < DownloadLinkRecovery.MaximumRenewals || status.Done > item.ResidentLastRenewalBytes) &&
                status.Id == item.Id && status.State == "failed" &&
                status.Total > 0 && status.Done < status.Total &&
                DownloadLinkRecovery.IsExpired(status.Error);
        }

        internal static void ApplyResidentTransferPhase(DlItem item, ResidentDownloadStatus status)
        {
            if (status.State == "extracting" && (string.IsNullOrEmpty(item.ContainerFormat) || item.ContainerFormat == "pkg"))
                item.ContainerFormat = "archive";
            if (status.State == "queued")
            {
                item.State = DlState.Queued; item.BytesPerSec = 0; item.EtaSeconds = 0;
                item.StatusText = string.IsNullOrEmpty(status.Error)
                    ? "Queued; waiting for the current package to finish" : status.Error;
            }
            if (status.Id == item.Id && (status.State == "feeding" || status.State == "validating" || status.State == "staged"))
                NoteBackgroundOpened(item.DestPath);
            if (status.State == "installing") item.State = DlState.Installing;
            else if (status.State == "validating" || status.State == "extracting")
            { item.State = DlState.Finalizing; }
            else if (status.State == "downloading" || status.State == "feeding" || status.State == "ready")
                item.State = DlState.Downloading;
            if (item.ResidentAutoInstall && (status.State == "waiting" || status.State == "staged"))
            {
                item.State = DlState.Queued; item.BytesPerSec = 0; item.EtaSeconds = 0;
                item.StatusText = string.IsNullOrEmpty(status.Error)
                    ? (status.Total > 0 && status.Done >= status.Total
                        ? "Downloaded; waiting for required package installation"
                        : "Waiting for PS4 download and installation") : status.Error;
            }
            if ((status.State == "storage-wait" || status.State == "waiting-storage") && !item.PauseRequested)
            { item.State = DlState.Queued; item.BytesPerSec = 0; item.EtaSeconds = 0; item.StatusText = string.IsNullOrEmpty(status.Error) ? "Reconnect the selected staging drive; background files are retained" : status.Error; }
            if ((status.State == "storage-wait" || status.State == "waiting-storage") && !item.PauseRequested &&
                ResidentCannotOpenUsb(status.Error) && (BackgroundCannotOpen(item.DestPath) || NoteBackgroundCannotOpen(item.DestPath)))
                item.StatusText = "The background service can't open this USB drive. Remove this download and add it again to download it in SSPI to the same drive (keep SSPI open), or set Staging location to PS4.";
            if (status.State == "paused" && item.ResidentPauseDesired == false)
            { item.State = DlState.Downloading; item.BytesPerSec = 0; item.EtaSeconds = 0; item.StatusText = "Background download resume requested"; }
            else if (status.State == "paused" || item.ResidentPauseDesired == true || (item.ResidentStaged && item.PauseRequested))
            { item.State = DlState.Paused; item.BytesPerSec = 0; item.EtaSeconds = 0; item.StatusText = "Background download paused"; }
        }

        internal static bool TryAcceptStagedPackage(DlItem item, ResidentDownloadStatus status, long fileSize)
        {
            if (item == null || status == null || !item.ResidentStaged || item.ResidentAutoInstall || !item.Background ||
                status.State != "staged" || !string.Equals(item.Id, status.Id, StringComparison.Ordinal)) return false;
            if (item.CancelRequested)
            {
                item.State = DlState.Canceled; item.CancelRequested = false;
                item.StatusText = "Canceled; downloaded file retained";
            }
            else
            {
                long expected = Math.Max(item.ExpectedByteSize, item.BgftExpectedSize);
                if (expected <= 0 || status.Total != expected || status.Done != expected || fileSize != expected)
                {
                    item.State = DlState.Failed; item.Error = "Staged download size did not match the complete package";
                    item.BytesPerSec = 0; item.EtaSeconds = 0;
                    return false;
                }
                item.State = item.PauseRequested ? DlState.Paused : DlState.Queued;
                item.StatusText = item.PauseRequested ? "Downloaded; paused before installation" : "Downloaded; waiting for package validation and installation";
                item.Done = item.Total = expected;
            }
            item.ResidentArchive = item.ResidentStaged = item.Background = item.BgftResident = false;
            item.ResidentPauseDesired = null;
            item.BgftTaskId = -1; item.BgftSubType = 0;
            item.InstallConfirmed = item.InstallOrderReady = false;
            item.Error = null; item.BytesPerSec = 0; item.EtaSeconds = 0; item.StatsInitialized = false;
            item.RetryAfterUtcTicks = 0;
            return true;
        }

        void RefreshBackgroundTasks()
        {
            var active = new List<DlItem>();
            lock (_lock)
            {
                foreach (var item in _items)
                {
                    if (item.ResidentRemovePending)
                    { active.Add(item); continue; }
                    if (!item.Background) {
                        if ((item.CancelRequested && !_activeIds.Contains(item.Id) &&
                                (item.BgftTaskId >= 0 || !string.IsNullOrEmpty(item.BgftContentId))) ||
                            (item.State == DlState.Submitted && (PkgValidator.BgftSubTypeForKind(item.Kind) == 6 ||
                                PkgValidator.BgftSubTypeForKind(item.Kind) == 7 ||
                                PkgValidator.BgftSubTypeForKind(item.Kind) == 8))) active.Add(item);
                        continue;
                    }
                    if (item.ResidentArchive || item.ResidentStaged || item.State == DlState.Downloading || item.State == DlState.Resolving ||
                        item.State == DlState.Installing || item.State == DlState.Submitted || item.State == DlState.Failed)
                        active.Add(item);
                }
            }

            foreach (var item in active)
            {
                if (item.ResidentRemovePending)
                { RefreshResidentArchive(item); continue; }
                if (item.CancelRequested && !item.ResidentStaged && !item.ResidentArchive)
                {
                    lock (_lock)
                    {
                        if (!object.ReferenceEquals(Find(item.Id), item) || !item.CancelRequested) continue;
                        int activeTask;
                        string cancelError;
                        if (CancelRowBackground(item, out activeTask, out cancelError))
                        {
                            RetireCanceledBgftSubmission(item);
                            ClearBackground(item);
                            item.State = DlState.Canceled; item.CancelRequested = false;
                            item.BytesPerSec = 0; item.EtaSeconds = 0; item.Error = null;
                            item.StatusText = "Canceled; downloaded files retained";
                            SaveManifest();
                        }
                        else { item.BgftTaskId = activeTask; item.StatusText = "Cancellation saved; " + cancelError; }
                    }
                    continue;
                }
                if (ConfirmInstalledTheme(item)) continue;
                if (ConfirmInstalledAddOn(item)) continue;
                if (!item.Background && item.State == DlState.Submitted) {
                    if (PkgValidator.BgftSubTypeForKind(item.Kind) == 8) { ConfirmInstalledUpdate(item); continue; }
                    if (PkgValidator.BgftSubTypeForKind(item.Kind) == 6) {
                        if (!PkgInstaller.IsBasePackageInstalled(item.DestPath, item.TitleId)) { item.BgftInstalledProofPolls = 0; continue; }
                        if (++item.BgftInstalledProofPolls < 8) continue;
                        MarkInstalled(item.Id, "Base game installation confirmed by PS4 content", true, item.AttemptId);
                        continue;
                    }
                    if (!PkgInstaller.IsAddonInstalled(item.DestPath, false)) { item.BgftInstalledProofPolls = 0; continue; }
                    if (++item.BgftInstalledProofPolls < 3) continue;
                    if (PkgInstaller.IsAddonInstalled(item.DestPath, true)) {
                        MarkInstalled(item.Id, "Add-on installation confirmed by PS4 content", false, item.AttemptId);
                        User.NotifyToast("Add-on installed");
                    } else item.BgftInstalledProofPolls = 0;
                    continue;
                }
                if (item.ResidentStaged) { RefreshResidentArchive(item); continue; }
                if (item.BgftLocalInstall && TryInterruptLocalInstall(item, item.AttemptId)) continue;
                if (ConfirmInstalledUpdate(item)) continue;
                if (item.BgftInstalledProofPolls > 0) continue;
                if (item.ResidentArchive) { RefreshResidentArchive(item); continue; }
                if (item.State == DlState.Failed) continue;
                string id;
                int attempt;
                int taskId;
                string contentId;
                int subType;
                string titleId;
                string kind;
                bool loopback;
                long expectedSize;
                lock (_lock)
                {
                    if (!item.Background) continue;
                    id = item.Id;
                    attempt = item.AttemptId;
                    taskId = item.BgftTaskId;
                    contentId = item.BgftContentId;
                    subType = item.BgftSubType;
                    titleId = item.TitleId;
                    kind = item.Kind;
                    loopback = item.BgftLoopback;
                    expectedSize = item.BgftExpectedSize > 0 ? item.BgftExpectedSize :
                        (item.ExpectedByteSize > 0 ? item.ExpectedByteSize : item.Total);
                }

                const int DownloadStablePolls = 4; // ~2s
                const int CopyStablePolls = 8;     // ~4s
                const int TitleStablePolls = 4;    // ~2s

                if (loopback)
                {
                    bool feederDead = false;
                    lock (_lock)
                    {
                        var cur = Find(id);
                        if (cur != null && cur.Background && cur.AttemptId == attempt)
                        {
                            if (FeederIsAlive(cur)) cur.BgftFeederMisses = 0;
                            else
                            {
                                cur.BgftFeederMisses++;
                                if (cur.BgftFeederMisses >= 6)
                                {
                                    FallbackBgftToLocal(cur, FeederLastError(cur) ??
                                        (string.IsNullOrEmpty(cur.Error) ? "loopback server stopped" : cur.Error));
                                    feederDead = true;
                                }
                            }
                        }
                    }
                    if (feederDead)
                    {
                        SaveManifest();
                        continue;
                    }
                }

                if (loopback && item.BgftResident)
                {
                    ResidentDownloadStatus residentStatus;
                    if (ResidentDownloadService.TryGetStatus(id, out residentStatus) &&
                        residentStatus.State != "complete")
                    {
                        if (residentStatus.State == "failed" || residentStatus.State == "canceled")
                        {
                            FallbackBgftToLocal(item, residentStatus.Error ?? "Resident transfer stopped");
                            SaveManifest(); continue;
                        }
                        lock (_lock)
                        {
                            var cur = Find(id);
                            if (cur != null && cur.Background && cur.AttemptId == attempt)
                            {
                                UpdateLiveTransferStats(cur, Math.Min(residentStatus.Done, Math.Max(0, residentStatus.Total - 1)),
                                    residentStatus.Total, residentStatus.NetworkBytes, residentStatus.State, TransferClockMs());
                                cur.StatusText = residentStatus.State == "awaiting-bgft"
                                    ? "Starting PS4 system download"
                                    : "Preparing PS4 system download " +
                                        (cur.Total > 0 ? Math.Min(99,
                                            (int)(cur.Done * 100.0 / cur.Total)) : 0) + "%";
                            }
                        }
                        continue;
                    }
                }

                BgftProgress progress;
                string error;
                if (!PkgInstaller.TryGetBackgroundProgress(taskId,
                    contentId, subType, expectedSize, out progress, out error))
                {
                    lock (_lock)
                    {
                        var cur = Find(id);
                        if (cur == null || !cur.Background || cur.AttemptId != attempt) continue;
                        if (cur.State == DlState.Completed || cur.State == DlState.Installed ||
                            cur.State == DlState.Canceled) continue;
                        cur.BgftPollFailures++;
                        ResetBgftTransientPolls(cur);
                        cur.BytesPerSec = 0;
                        cur.EtaSeconds = 0;
                        // Do NOT auto-Installed on lost task + title present (false positive).
                        if (cur.BgftPollFailures >= 40)
                        {
                            // Lost task after long plateau — continue in-app rather than dead-end Failed.
                            FallbackBgftToLocal(cur, error ?? "lost BGFT task");
                        }
                        else
                            cur.StatusText = "Reconnecting to PS4 download...";
                    }
                    SaveManifest();
                    continue;
                }

                bool stateChanged = false;
                lock (_lock)
                {
                    var cur = Find(id);
                    if (cur == null || !cur.Background || cur.AttemptId != attempt) continue;
                    if (cur.State == DlState.Completed || cur.State == DlState.Installed ||
                        cur.State == DlState.Canceled || cur.State == DlState.Failed) continue;

                    if (cur.BgftTaskId >= 0 && cur.BgftTaskId != progress.TaskId) continue;
                    if (progress.TaskId >= 0 && cur.BgftTaskId < 0)
                    {
                        cur.BgftTaskId = progress.TaskId;
                        ResetBgftTransientPolls(cur);
                        stateChanged = true;
                    }
                    if (cur.BgftLoopback && !cur.BgftLoopbackServed &&
                        FeederWasFullyServed(cur))
                    {
                        cur.BgftLoopbackServed = true;
                        stateChanged = true;
                    }

                    long expected = cur.BgftExpectedSize > 0 ? cur.BgftExpectedSize : cur.Total;
                    long tol = expected > 0
                        ? Math.Max(64L * 1024, Math.Min(4L * 1024 * 1024, expected / 10000))
                        : 0;

                    // Never shrink Total below expected size once known.
                    if (expected > 0 && (cur.Total <= 0 || cur.Total < expected))
                        cur.Total = expected;
                    if (progress.Total > 0 && progress.Total >= cur.Done &&
                        (cur.Total <= 0 || progress.Total >= expected - tol))
                        cur.Total = Math.Max(cur.Total, progress.Total);
                    if (!cur.BgftLocalInstall)
                        UpdateTransferStats(cur, progress.Done, cur.Total,
                            cur.BgftDownloadPhaseConfirmed ? "installing" : "bgft", TransferClockMs(), progress.EtaSeconds);
                    else { cur.Done = cur.Total; cur.BytesPerSec = 0; cur.EtaSeconds = 0; }
                    cur.BgftPollFailures = 0;

                    if (progress.ErrorResult != 0)
                    {
                        // Preserve the BGFT code prefix for matching, but attach stage
                        // diagnostics (task/content/subtype/kind/title/progress) so a
                        // DLC failure like 0x80991404 can be traced to transfer vs
                        // install. The downloaded file and ownership are retained.
                        // A direct provider task and a local loopback task have
                        // different failure routes; the code alone is not a diagnosis.
                        string code = "BGFT 0x" + unchecked((uint)progress.ErrorResult).ToString("X");
                        string diag = code + " task=" + cur.BgftTaskId +
                            " sub=" + cur.BgftSubType + " kind=" + (kind ?? cur.Kind) +
                            " title=" + (titleId ?? cur.TitleId) +
                            " content=" + (contentId ?? cur.BgftContentId ?? "") +
                            " done=" + progress.Done + "/" + progress.Total +
                            " copy=" + progress.LocalCopyPercent + "%" +
                            " expected=" + expected + " route=" +
                            (cur.BgftDirect ? "direct" : cur.BgftLoopback ? "loopback" : "local");
                        LogBgftEvent("task-error-retained", cur, diag, progress);
                        // Release the loopback route so a fresh retry (or the next
                        // queued package) does not reuse a stale 404 route.
                        if (cur.BgftLoopback) FeederMarkFailed(cur);
                        FallbackBgftToLocal(cur, diag);
                        stateChanged = true;
                    }
                    else
                    {
                        const int StallPolls = 150;
                        bool basePayloadComplete;
                        bool dlCandidate = BgftDownloadCompleteCandidate(cur, progress, expected, tol,
                            out basePayloadComplete);
                        if (!dlCandidate && progress.DownloadComplete && progress.Total > 0 &&
                            progress.LocalCopyPercent == 100 &&
                            !PkgInstallPolicy.CompleteBgftPayload(expected, progress.Done, progress.Total, tol) &&
                            cur.BgftRejectedCompletionTotal != progress.Total)
                        {
                            cur.BgftRejectedCompletionTotal = progress.Total;
                            LogBgftEvent("partial-completion-rejected", cur,
                                "reported=" + progress.Done + "/" + progress.Total + " expected=" + expected, progress);
                        }
                        if (!cur.BgftDownloadPhaseConfirmed)
                        {
                            long completeAt = expected > 0 ? expected - tol : long.MaxValue;
                            bool nearDone = basePayloadComplete || (expected > 0 && progress.Done >= completeAt);
                            if (!nearDone || (!basePayloadComplete && !cur.BgftDirect && !cur.BgftLoopbackServed))
                            {
                                if (nearDone || progress.Done <= cur.BgftLastProgressDone)
                                    cur.BgftStallPolls++;
                                else
                                {
                                    cur.BgftLastProgressDone = progress.Done;
                                    cur.BgftStallPolls = 0;
                                }
                                if (cur.BgftStallPolls >= StallPolls)
                                {
                                    LogBgftEvent("task-stall", cur,
                                        "polls=" + cur.BgftStallPolls + " expected=" + expected, progress);
                                    // Near the end, neither BGFT's counters nor SSPI's served-range
                                    // receipt proves a complete read; name the figures, not a cause.
                                    FallbackBgftToLocal(cur,
                                        cur.BgftDirect
                                            ? (nearDone ? "direct transfer size mismatch"
                                                : "direct transfer stalled at " + BgftStallSize(progress.Done))
                                            : (nearDone ? "PS4 stopped importing near the end without confirming the whole package (PS4 " +
                                                BgftStallSize(progress.Done) + " of " + BgftStallSize(progress.Total) + ", package " + BgftStallSize(expected) + ")"
                                                : "stalled at " + BgftStallSize(progress.Done)));
                                    stateChanged = true;
                                    // skip rest of state machine this tick
                                    goto after_bgft_tick;
                                }
                            }
                        }

                        // Install candidate only after download phase confirmed + copy 100.
                        bool copyCandidate = cur.BgftDownloadPhaseConfirmed &&
                            progress.LocalCopyPercent >= 100 &&
                            progress.LocalCopyPercent <= 100;

                        if (!cur.BgftDownloadPhaseConfirmed)
                        {
                            if (dlCandidate) cur.BgftDownloadCompletePolls++;
                            else cur.BgftDownloadCompletePolls = 0;
                            cur.BgftCopyCompletePolls = 0;
                            cur.BgftTitlePresentPolls = 0;

                            if (cur.BgftDownloadCompletePolls >= DownloadStablePolls)
                            {
                                cur.BgftDownloadPhaseConfirmed = true;
                                cur.State = DlState.Installing;
                                cur.StatusText = "Downloaded — PS4 installing";
                                cur.BgftCopyCompletePolls = 0;
                                cur.BgftStallPolls = 0;
                                stateChanged = true;
                            }
                            else
                            {
                                if (cur.State != (cur.BgftLocalInstall ? DlState.Installing : DlState.Downloading))
                                {
                                    cur.State = cur.BgftLocalInstall ? DlState.Installing : DlState.Downloading;
                                    stateChanged = true;
                                }
                                long denom = cur.Total > 0 ? cur.Total : expected;
                                int pct = denom > 0
                                    ? Math.Min(99, (int)(cur.Done * 100.0 / Math.Max(1.0, denom))) : 0;
                                cur.StatusText = cur.BgftLocalInstall ? "PS4 importing package " + (denom > 0 ? Math.Min(100, (int)(progress.Done * 100.0 / denom)) : 0) + "%" :
                                    "PS4 download " + pct + "%  " + Human(cur.Done) + (denom > 0 ? " / " + Human(denom) : "");
                            }
                        }
                        else
                        {
                            // Phase 2: installing
                            if (copyCandidate) cur.BgftCopyCompletePolls++;
                            else
                            {
                                cur.BgftCopyCompletePolls = 0;
                                cur.BgftTitlePresentPolls = 0;
                            }

                            if (cur.State != DlState.Installing && cur.State != DlState.Submitted)
                            {
                                cur.State = DlState.Installing;
                                stateChanged = true;
                            }

                            if (cur.BgftCopyCompletePolls < CopyStablePolls)
                            {
                                cur.StatusText = "PS4 installing" +
                                    (progress.LocalCopyPercent > 0
                                        ? (" " + progress.LocalCopyPercent + "%") : "...");
                            }
                            else
                            {
                                if (cur.Kind == "theme-license" && cur.BgftLoopbackServed)
                                {
                                    MarkThemeLicenseCopyReady(cur);
                                    FeederRelease(cur);
                                    cur.Background = false;
                                    cur.BgftLoopback = false;
                                    stateChanged = true;
                                    goto after_bgft_tick;
                                }
                                bool isBase = cur.BgftSubType == 6 ||
                                    PkgValidator.BgftSubTypeForKind(kind) == 6;

                                // Only auto-Installed on absent→present base transition.
                                if (isBase && cur.BgftTitlePresenceKnown && !cur.BgftTitlePresentAtStart)
                                {
                                    if (!string.IsNullOrEmpty(titleId) &&
                                        PkgInstaller.IsTitleInstalled(titleId))
                                        cur.BgftTitlePresentPolls++;
                                    else
                                        cur.BgftTitlePresentPolls = 0;

                                    if (cur.BgftTitlePresentPolls >= TitleStablePolls)
                                    {
                                        // Completion is not cancellation. Leave the PS4 task intact.
                                        cur.State = DlState.Installed;
                                        cur.InstallConfirmed = true;
                                        cur.StatusText = "Installed via PS4 download";
                                        ClearBackground(cur);
                                        stateChanged = true;
                                    }
                                    else
                                    {
                                        cur.StatusText = "PS4 finishing install...";
                                        cur.BgftStallPolls++;
                                        if (cur.BgftStallPolls >= 150)
                                        {
                                            FallbackBgftToLocal(cur, "install completion was not confirmed");
                                            stateChanged = true;
                                            goto after_bgft_tick;
                                        }
                                    }
                                }
                                else
                                {
                                    if ((PkgValidator.RequestedKind(cur.Kind) == PkgContentKind.SystemTheme || cur.Kind == "theme-license") && ++cur.BgftStallPolls >= 240)
                                    {
                                        const string unconfirmed = "THEME_UNCONFIRMED: Check PS4 Settings > Themes, then retry if missing. PKG retained.";
                                        FallbackBgftToLocal(cur, unconfirmed);
                                        if (TryCancelBackgroundIdentity(cur, contentId, subType, unconfirmed)) ClearBackground(cur);
                                        stateChanged = true;
                                        goto after_bgft_tick;
                                    }
                                    if (cur.State != DlState.Submitted)
                                    {
                                        // LocalCopy is not final promotion: keep the task and source alive.
                                        cur.State = DlState.Submitted;
                                        cur.InstallOrderReady = false;
                                        cur.InstallConfirmed = false;
                                        cur.InstallSubmitted = true;
                                        cur.StatusText = PkgValidator.RequestedKind(cur.Kind) == PkgContentKind.SystemTheme
                                            ? "Theme sent to PS4; checking Settings > Themes. PKG kept"
                                            : "Sent to PS4 — verify in library · PKG kept";
                                        cur.Background = true;
                                        stateChanged = true;
                                    }
                                }
                            }
                        }
                    }
                after_bgft_tick: ;
                }
                if (stateChanged) SaveManifest();
            }
        }

        void ClearBackground(DlItem item)
        {
            if (item == null) return;
            item.SubmittedWaitStartedMs = -1;
            item.SubmittedNeedsAttention = false;
            item.ResidentPauseDesired = null;
            if (item.BgftLoopback) FeederRelease(item);
            item.Background = false;
            item.ResidentStaged = false;
            item.ResidentAutoInstall = false;
            item.BgftLocalInstall = false;
            item.BgftLoopback = false;
            item.BgftResident = false;
            item.BgftDirect = false;
            item.BgftLoopbackServed = false;
            item.BgftTaskId = -1;
            item.BgftContentId = "";
            item.BgftSubType = 0;
            item.BgftPollFailures = 0;
            item.BgftExpectedSize = 0;
            item.BgftTitlePresenceKnown = false;
            item.BgftTitlePresentAtStart = false;
            item.BgftDownloadPhaseConfirmed = false;
            item.BgftDownloadCompletePolls = 0;
            item.BgftCopyCompletePolls = 0;
            item.BgftTitlePresentPolls = 0;
            item.BgftLastProgressDone = 0;
            item.BgftInstalledProofPolls = 0;
            item.BgftStallPolls = 0;
            // Do not zero Done/Total here — foreground progress may already be mid-file.
        }

        // Call only after CancelBackground succeeds. Content identity makes its
        // result a verified BGFT stop/not-found result, not the no-task shortcut.
        internal static bool RetireCanceledBgftSubmission(DlItem item)
        {
            if (item == null || !item.InstallSubmitted || item.InstallConfirmed ||
                string.IsNullOrWhiteSpace(item.BgftContentId) || item.BgftSubType <= 0) return false;
            item.InstallSubmitted = false;
            item.InstallOrderReady = false;
            return true;
        }

        static void ResetBgftTransientPolls(DlItem item)
        {
            if (item == null) return;
            item.BgftDownloadCompletePolls = 0;
            item.BgftCopyCompletePolls = 0;
            item.BgftTitlePresentPolls = 0;
            item.BgftStallPolls = 0;
            item.BgftRejectedCompletionTotal = 0;
        }

        internal static bool BgftDownloadCompleteCandidate(DlItem item, BgftProgress progress,
            long expected, long tolerance, out bool basePayloadComplete)
        {
            basePayloadComplete = false;
            if (item == null || progress == null || progress.ErrorResult != 0 ||
                !PkgInstallPolicy.CompleteBgftPayload(expected, progress.Done, progress.Total, tolerance)) return false;
            // A PlayGo/chunk completion or copy=100 signal never substitutes for the full PKG size.
            basePayloadComplete = PkgValidator.BgftSubTypeForKind(item.Kind) == 6 &&
                progress.DownloadComplete && progress.LocalCopyPercent == 100;
            // BGFT reads only the add-on bytes it needs, so a loopback add-on cannot
            // require the served-range receipt; its own completed copy is the signal.
            bool loopbackAddOnCopied = item.BgftLoopback && PkgValidator.BgftSubTypeForKind(item.Kind) == 7 &&
                PkgValidator.RequestedKind(item.Kind) == PkgContentKind.AddOn &&
                progress.DownloadComplete && progress.LocalCopyPercent == 100;
            return basePayloadComplete || loopbackAddOnCopied || ((item.BgftLoopback || item.BgftDirect) &&
                (item.BgftDirect || item.BgftLoopbackServed));
        }

        /// <summary>Cancels the PS4 task a row tracks. A row of this app's own install path
        /// whose task is not SSPI's lets go of it instead of retrying forever (earlier
        /// builds adopted a conflicting download after a failed registration). SSPI claims
        /// every task it registers inside the journal gate, so that task was never the
        /// row's; it is left untouched on the PS4.</summary>
        static bool CancelRowBackground(DlItem it, out int activeTask, out string error)
        {
            if (PkgInstaller.CancelBackground(it.BgftTaskId, it.BgftContentId, it.BgftSubType, out activeTask, out error)) return true;
            if (it.BgftResident || it.ResidentStaged || it.ResidentArchive || !BgftCancellation.IsForeignRefusal(error)) return false;
            LogBgftEvent("foreign-task-detached", it, error);
            activeTask = -1; error = null;
            return true;
        }

        bool TryCancelBackgroundIdentity(DlItem item, string contentId, int subType, string reason,
            bool failLoopback = true)
        { bool foreign; return TryCancelBackgroundIdentity(item, contentId, subType, reason, failLoopback, false, out foreign); }

        /// <param name="detachForeign">The row never registered a task (its registration
        /// failed), so a task SSPI does not own is left alone instead of being adopted:
        /// adopting it made the row wait on someone else's download forever.</param>
        bool TryCancelBackgroundIdentity(DlItem item, string contentId, int subType, string reason,
            bool failLoopback, bool detachForeign, out bool foreign)
        {
            int activeTask = -1;
            string cancelError = null;
            bool canceled = PkgInstaller.CancelBackground(item != null ? item.BgftTaskId : -1,
                    contentId, subType,
                    out activeTask, out cancelError);
            foreign = !canceled && BgftCancellation.IsForeignRefusal(cancelError);
            if (canceled) return true;
            if (item == null) return false;
            if (foreign && detachForeign)
            {
                LogBgftEvent("foreign-conflict", item, reason + " | " + cancelError);
                return true;
            }
            lock (_lock)
            {
                item.Background = true;
                item.BgftContentId = contentId ?? "";
                item.BgftSubType = subType;
                if (activeTask >= 0) item.BgftTaskId = activeTask;
                item.State = DlState.Resolving;
                item.InstallConfirmed = false;
                item.Error = cancelError ?? "BGFT task identity is missing";
                item.StatusText = "Clearing PS4 system download: " + ClipMsg(item.Error, 36);
                if (failLoopback && item.BgftLoopback) FeederMarkFailed(item);
            }
            LogBgftEvent("cancel-pending", item, reason + " | " + item.Error);
            return false;
        }

        void FallbackBgftToLocal(DlItem cur, string reason)
        {
            if (cur == null) return;
            lock (_lock)
            {
                if (!cur.Background) return;
                cur.State = DlState.Failed;
                cur.InstallConfirmed = false; cur.InstallOrderReady = false;
                cur.BytesPerSec = 0; cur.EtaSeconds = 0;
                cur.Error = reason ?? "PS4 task status unavailable";
                cur.StatusText = "PS4 task stopped: " + ClipMsg(cur.Error, 100) + " · file kept · CROSS retries";
                cur.RetryAfterUtcTicks = 0;
                LogBgftEvent("task-failed-retained", cur, cur.Error);
            }
        }


        static void LogBgftEvent(string kind, DlItem it, string detail, BgftProgress progress = null)
        {
            try
            {
                string dir = AppSettings.DataDir;
                if (string.IsNullOrEmpty(dir)) return;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string host = "";
                try
                {
                    if (!string.IsNullOrEmpty(it.HosterUrl))
                        host = new Uri(it.HosterUrl).Host;
                }
                catch { }
                string line = DateTime.UtcNow.ToString("o") + " " + kind +
                    " title=" + (it.TitleId ?? "") +
                    " task=" + it.BgftTaskId +
                    " done=" + it.Done +
                    " total=" + it.Total +
                    " exp=" + it.BgftExpectedSize +
                    " host=" + host +
                    " content=" + (it.BgftContentId ?? it.ExpectedContentId ?? "") + " subtype=" + it.BgftSubType +
                    " path=" + (it.DestPath ?? "") + " " + ClipMsg(detail, 1024) +
                    (progress == null ? "" : " " + PkgInstaller.FormatBackgroundProgress(progress)) +
                    Environment.NewLine;
                SspiLog.Write("download", line);
            }
            catch { }
        }
    }
}
