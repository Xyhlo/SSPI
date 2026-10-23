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
        public string DownloadNext(string id)
        {
            lock (_lock)
            {
                var target = Find(id);
                if (target == null) return "Package is no longer in the queue";
                if (target.State != DlState.Queued && target.State != DlState.Paused && target.State != DlState.Downloading && target.State != DlState.Resolving)
                    return "Only queued, paused, or downloading packages can be prioritized";
                if (!InstallDependencyReady(target))
                    return "Resume this game's base or earlier update first; installation order is preserved";
                // Pause competing transfers through their existing owner, never stop an installer.
                foreach (var other in _items.ToArray())
                    if (other.Id != id && !other.PauseRequested &&
                        (other.State == DlState.Queued || other.State == DlState.Downloading || other.State == DlState.Resolving))
                        TogglePause(other.Id);
                if (target.State == DlState.Paused) TogglePause(id);
                // The scheduler scans oldest/end first. Persist this explicit choice.
                _items.Remove(target); _items.Add(target);
                _nextJobStartAt = 0;
                SaveManifest();
                return "Download prioritized; other transfers paused. Active installation and dependencies finish first.";
            }
        }

        public bool SetArchivePasswordAndRetry(string id, string password, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(password))
            { error = "Enter the archive password"; return false; }
            if (!ResidentDownloadService.ValidArchivePassword(password))
            { error = "Archive password exceeds 256 UTF-8 bytes or is invalid"; return false; }
            lock (_lock)
            {
                var item = Find(id);
                if (item == null) { error = "Download not found"; return false; }
                if (item.State != DlState.Failed || item.ResidentRemovePending ||
                    item.ResidentRetryPending || item.CancelRequested)
                { error = "Wait for the archive to stop before entering its password"; return false; }
                string format = item.ContainerFormat ?? "";
                string failure = item.Error ?? "";
                bool rar = string.Equals(format, "rar", StringComparison.OrdinalIgnoreCase) ||
                    !string.IsNullOrEmpty(item.ArchiveVolumes) ||
                    ((format.Length == 0 || format == "archive") &&
                        (failure.IndexOf("RAR", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         failure.IndexOf("archive password", StringComparison.OrdinalIgnoreCase) >= 0));
                if (!rar) { error = "This download is not a failed RAR archive"; return false; }
                // Commit the explicit password before any worker receives a retry. Source
                // defaults only fill empty passwords, so retained parts use this value.
                string previous = item.ArchivePassword, previousList = item.ArchivePasswords;
                // The source's own password stays a fallback: an outer and an inner
                // archive can each need a different one.
                if (!string.IsNullOrEmpty(previous) && previous != password)
                {
                    var kept = new List<string>(ArchivePasswordDefaults.Decode(item.ArchivePasswords));
                    kept.Remove(previous); kept.Insert(0, previous);
                    item.ArchivePasswords = ArchivePasswordDefaults.EncodeLenient(kept);
                }
                item.ArchivePassword = password;
                if (!SaveManifest())
                {
                    item.ArchivePassword = previous; item.ArchivePasswords = previousList;
                    error = "Could not save the archive password"; return false;
                }
                TogglePause(id);
                return true;
            }
        }

        public void TogglePause(string id)
        {
            string residentCommandId = null;
            bool residentRelease = false, residentPause = false;
            DlItem residentCommandItem = null;
            int residentCommandAttempt = 0;
            string residentCommandGeneration = null;
            bool restoreResidentPause = false, previousResidentPause = false;
            bool? previousResidentDesired = null;
            DlState previousResidentState = DlState.Queued;
            long residentPauseRevision = 0;
            lock (_lock)
            {
                var it = Find(id);
                if (it == null) return;
                if (it.Background && it.ResidentArchive)
                {
                    if (it.State == DlState.Failed)
                    {
                        it.ResidentRetryPending = true;
                        it.ResidentLinkRenewals = 0; it.ResidentLastRenewalBytes = 0;
                        it.CancelRequested = it.PauseRequested = false;
                        it.State = DlState.Resolving;
                        // Releasing a native job unregisters its PS4 task and the bytes BGFT held.
                        it.StatusText = IsNativeBgftRow(it) ? "Stopping previous attempt; the PS4 download will restart from the beginning"
                            : "Stopping previous attempt; verified files retained for retry";
                        if (SaveManifest())
                        {
                            residentCommandId = it.Id; residentRelease = true;
                            residentCommandItem = it; residentCommandAttempt = it.AttemptId;
                            residentCommandGeneration = it.ResidentGeneration;
                        }
                    }
                    else
                    {
                        bool pause = !(it.ResidentPauseDesired ?? (it.State == DlState.Paused));
                        previousResidentDesired = it.ResidentPauseDesired;
                        previousResidentPause = it.PauseRequested;
                        previousResidentState = it.State;
                        string previousStatus = it.StatusText;
                        it.ResidentPauseDesired = pause;
                        it.PauseRequested = pause;
                        it.State = pause ? DlState.Paused : DlState.Downloading;
                        it.StatusText = pause ? "Background download pause requested" : "Background download resume requested";
                        if (SaveManifest())
                        {
                            residentCommandId = it.Id;
                            residentPause = pause;
                            residentCommandItem = it; residentCommandAttempt = it.AttemptId;
                            residentCommandGeneration = it.ResidentGeneration;
                            residentPauseRevision = ++it.ResidentPauseRevision;
                            restoreResidentPause = true;
                        }
                        else
                        {
                            it.ResidentPauseDesired = previousResidentDesired;
                            it.PauseRequested = previousResidentPause;
                            it.State = previousResidentState;
                            it.StatusText = previousStatus;
                        }
                    }
                }
                else if (it.Background)
                {
                    int activeTask;
                    string error;
                    if (it.State == DlState.Downloading || it.State == DlState.Resolving)
                    {
                        bool paused = PkgInstaller.PauseBackground(it.BgftTaskId, it.BgftContentId,
                            it.BgftSubType, out activeTask, out error);
                        it.BgftTaskId = activeTask;
                        if (paused)
                        {
                            if (it.BgftResident)
                            {
                                residentCommandId = it.Id; residentPause = true;
                                residentCommandItem = it; residentCommandAttempt = it.AttemptId;
                                residentCommandGeneration = it.ResidentGeneration;
                            }
                            it.State = DlState.Paused;
                            it.StatusText = "Background download paused";
                        }
                        else
                            it.StatusText = error;
                    }
                    else if (it.State == DlState.Paused)
                    {
                        bool resumed = PkgInstaller.ResumeBackground(it.BgftTaskId, it.BgftContentId,
                            it.BgftSubType, out activeTask, out error);
                        it.BgftTaskId = activeTask;
                        if (resumed)
                        {
                            if (it.BgftResident)
                            {
                                residentCommandId = it.Id; residentPause = false;
                                residentCommandItem = it; residentCommandAttempt = it.AttemptId;
                                residentCommandGeneration = it.ResidentGeneration;
                            }
                            it.State = DlState.Downloading;
                            it.StatusText = "Background download resumed";
                        }
                        else
                            it.StatusText = error;
                    }
                    else if (it.State == DlState.Failed)
                    {
                        if (CancelRowBackground(it, out activeTask, out error))
                        {
                            RetireCanceledBgftSubmission(it);
                            ClearBackground(it);
                            it.State = DlState.Queued;
                            it.Error = null;
                            it.StatusText = "Queued for a fresh background link";
                        }
                        else
                        {
                            it.BgftTaskId = activeTask;
                            it.StatusText = error;
                        }
                    }
                    SaveManifest();
                    if (residentCommandId == null) return;
                }
                else if (it.State == DlState.Queued)
                {
                    it.State = DlState.Paused;
                    it.PauseRequested = true;
                    it.HttpRetryUrl = null;
                    it.RetryAfterUtcTicks = 0;
                    it.StatusText = "Paused · partial file kept";
                    SaveManifest();
                    return;
                }
                else if (!it.PauseRequested && (it.State == DlState.Downloading || it.State == DlState.Resolving ||
                    it.State == DlState.Finalizing))
                {
                    if (it.State == DlState.Finalizing && !(it.StatusText ?? "").StartsWith("Extracting", StringComparison.Ordinal))
                    {
                        it.StatusText = "Finalizing cannot be paused";
                        SaveManifest();
                        return;
                    }
                    it.PauseRequested = true;
                    it.StatusText = "Pausing...";
                    // Snap progress to on-disk .part so resume UI shows real bytes
                    try
                    {
                        string part = it.DestPath + ".part";
                        if (File.Exists(part))
                            it.Done = ParallelDownloadCheckpoint.DurableBytes(part);
                    }
                    catch { }
                }
                else if (it.PauseRequested || it.State == DlState.Paused || it.State == DlState.Failed ||
                         it.State == DlState.Canceled)
                {
                    if (it.State == DlState.Failed) ForgetUnconfirmedTorBoxCreates(it);
                    // Retry asks every enabled service again. A refusal recorded for this row
                    // only steered its automatic retries; kept, it blocked the only service.
                    if (it.State == DlState.Failed) ClearProviderRejections(it);
                    bool restartRequired = it.State == DlState.Failed &&
                        !File.Exists(it.DestPath) && DownloadResumeInfo.IsRestartRequired(it.Error) &&
                        !TransferClient.HasCompletedJournal(it.DestPath);
                    if (restartRequired && !it.LocalSource)
                    {
                        DeletePartialOwned(it.DestPath);
                        it.Done = 0;
                        it.Total = 0;
                    }
                    try
                    {
                        string part = it.DestPath + ".part";
                        if (File.Exists(part))
                            it.Done = ParallelDownloadCheckpoint.DurableBytes(part);
                        else if (File.Exists(it.DestPath))
                            it.Done = it.Total = new FileInfo(it.DestPath).Length;
                        else
                        {
                            it.Done = 0;
                            it.Total = 0;
                        }
                    }
                    catch { }
                    if (_activeIds.Contains(it.Id)) it.AttemptId++;
                    it.State = DlState.Queued;
                    it.SubmittedNeedsAttention = false;
                    it.SubmittedWaitStartedMs = -1;
                    it.Error = null;
                    it.PauseRequested = false;
                    it.CancelRequested = false;
                    it.TransientHttpRetries = 0;
                    it.HttpRetryRenewPending = false;
                    it.HostSupportRetries = 0;
                    it.InstallRetries = 0;
                    it.HttpRetryUrl = null;
                    it.RetryAfterUtcTicks = 0;
                    it.StatusText = restartRequired
                        ? "Restarting from zero (server rejected resume)"
                        : it.Done > 0
                        ? ("Resume from " + Human(it.Done))
                        : "Queued";
                }
                SaveManifest();
            }
            if (residentCommandId == null) return;
            Func<Action, bool> publishIfCurrent = publish =>
            {
                lock (_lock)
                {
                    var current = Find(residentCommandId);
                    if (!object.ReferenceEquals(current, residentCommandItem) || current.AttemptId != residentCommandAttempt ||
                        !string.Equals(current.ResidentGeneration ?? "", residentCommandGeneration ?? "", StringComparison.Ordinal) ||
                        (restoreResidentPause && current.ResidentPauseRevision != residentPauseRevision))
                        return false;
                    bool currentCommand = residentRelease
                        ? current.ResidentRetryPending
                        : !current.CancelRequested && !current.ResidentRemovePending && !IsTerminal(current.State) &&
                            (current.ResidentArchive
                                ? current.ResidentPauseDesired == residentPause
                                : current.State == (residentPause ? DlState.Paused : DlState.Downloading));
                    if (currentCommand && publish != null) publish();
                    return currentCommand;
                }
            };
            // DownloadNext pauses competing rows while it holds _lock; the command is then
            // sent from the ThreadPool (RunResidentCommandOffLock) and publishIfCurrent still
            // decides whether it applies.
            RunResidentCommandOffLock(() =>
            {
                if (residentRelease)
                {
                    ResidentDownloadService.Release(residentCommandId, residentCommandGeneration, publishIfCurrent);
                    return;
                }
                if (!ResidentDownloadService.SetPaused(residentCommandId, residentPause,
                    residentCommandGeneration, publishIfCurrent))
                {
                    lock (_lock)
                    {
                        var it = Find(residentCommandId);
                        if (publishIfCurrent(null))
                        {
                            if (restoreResidentPause)
                            {
                                it.ResidentPauseDesired = previousResidentDesired;
                                it.PauseRequested = previousResidentPause;
                                it.State = previousResidentState;
                            }
                            it.StatusText = "Background command could not be saved: " + ResidentDownloadService.LastError;
                            SaveManifest();
                        }
                    }
                }
            });
        }

        public bool Cancel(string id, out string error)
        {
            error = null;
            string residentCancelId = null;
            int residentCancelTask = -1;
            DlItem residentCancelItem = null;
            int residentCancelAttempt = 0;
            string residentCancelGeneration = null;
            lock (_lock)
            {
                var it = Find(id);
                if (it == null)
                {
                    error = "Download not found";
                    return false;
                }
                if (IsTerminal(it.State) && !(it.State == DlState.Failed && it.Background && it.ResidentArchive))
                {
                    error = "Already finished — use Remove";
                    return false;
                }
                if (!it.Background && it.State == DlState.Installing)
                {
                    bool previous = it.CancelRequested;
                    it.CancelRequested = true;
                    if (!SaveManifest()) { it.CancelRequested = previous; error = "Could not save the cancellation request"; return false; }
                    it.StatusText = "Canceling installation; waiting for PS4 acknowledgement";
                    return true;
                }
                if (it.Background && it.ResidentArchive)
                {
                    bool previous = it.CancelRequested;
                    if (RetainUnconfirmedAddonFiles(it)) it.InstallSubmitted = true;
                    it.CancelRequested = true;
                    it.StatusText = "Canceling background job...";
                    if (!SaveManifest())
                    { it.CancelRequested = previous; error = "Could not save the cancellation request"; return false; }
                    residentCancelId = it.Id;
                    residentCancelTask = it.BgftTaskId;
                    residentCancelItem = it;
                    residentCancelAttempt = it.AttemptId;
                    residentCancelGeneration = it.ResidentGeneration;
                }
                else if (it.Background || (it.State == DlState.Submitted && it.BgftTaskId >= 0))
                {
                    bool previous = it.CancelRequested;
                    it.CancelRequested = true;
                    if (it.State == DlState.Submitted) it.InstallSubmitted = true;
                    if (!SaveManifest())
                    { it.CancelRequested = previous; error = "Could not save the cancellation request"; return false; }
                    int activeTask;
                    string cancelError;
                    if (CancelRowBackground(it, out activeTask, out cancelError))
                    {
                        RetireCanceledBgftSubmission(it);
                        ClearBackground(it);
                        it.State = DlState.Canceled;
                        it.CancelRequested = false;
                        it.StatusText = "Canceled; downloaded files retained";
                    }
                    else
                    {
                        it.BgftTaskId = activeTask;
                        // The durable request is retried by the worker and after restart.
                        it.StatusText = "Cancellation saved; " + (cancelError ?? "waiting for PS4 acknowledgement");
                    }
                    SaveManifest();
                    return error == null;
                }
                else if (_activeIds.Contains(it.Id) &&
                    (it.State == DlState.Resolving || it.State == DlState.Downloading ||
                     it.State == DlState.Finalizing))
                {
                    bool previousCancel = it.CancelRequested, previousPause = it.PauseRequested;
                    it.CancelRequested = true;
                    it.PauseRequested = false;
                    it.StatusText = "Canceling...";
                    if (!SaveManifest())
                    {
                        it.CancelRequested = previousCancel; it.PauseRequested = previousPause;
                        error = "Could not save the cancellation request"; return false;
                    }
                    return true;
                }
                else
                {
                    if (_activeIds.Contains(it.Id)) it.AttemptId++;
                    if (it.State == DlState.Submitted) it.InstallSubmitted = true;
                    bool wasCancelRequested = it.CancelRequested;
                    it.CancelRequested = true;
                    if (!SaveManifest())
                    { it.CancelRequested = wasCancelRequested; error = "Could not save the cancellation request"; return false; }
                    if (!TryCleanupFanOutPending(it))
                    {
                        error = "Extracted package cleanup failed";
                        return false;
                    }
                    it.State = DlState.Canceled;
                    it.CancelRequested = false;
                    it.PauseRequested = false;
                    bool cleaned = it.LocalSource || DeleteDownloadFiles(it);
                    it.StatusText = it.InstallSubmitted ? "Stopped tracking installation; unconfirmed package retained" :
                        cleaned ? "Canceled" : "Canceled; local cleanup pending";
                    SaveManifest();
                    return true;
                }
            }
            if (residentCancelId != null)
            {
                string cancelError;
                Func<Action, bool> publishIfCurrent = publish =>
                {
                    lock (_lock)
                    {
                        var current = Find(residentCancelId);
                        bool isCurrent = object.ReferenceEquals(current, residentCancelItem) &&
                            current.AttemptId == residentCancelAttempt && current.CancelRequested &&
                            string.Equals(current.ResidentGeneration ?? "", residentCancelGeneration ?? "", StringComparison.Ordinal);
                        if (isCurrent && publish != null) publish();
                        return isCurrent;
                    }
                };
                if (!ResidentDownloadService.TryCancel(residentCancelId, residentCancelTask,
                    residentCancelGeneration, publishIfCurrent, out cancelError))
                {
                    lock (_lock)
                    {
                        var item = Find(residentCancelId);
                        if (item != null && item.CancelRequested)
                        {
                            item.StatusText = "Cancellation saved; waiting to reach the background worker";
                            SaveManifest();
                        }
                    }
                }
                return true;
            }
            return false;
        }

        public bool MoveUp(string id, out string error)
        {
            error = null;
            lock (_lock)
            {
                var item = Find(id);
                if (item == null) { error = "Download not found"; return false; }
                int index = _items.IndexOf(item);
                if (index <= 0) { error = "Already first"; return false; }
                if (_activeIds.Contains(item.Id) || item.State == DlState.Downloading ||
                    item.State == DlState.Resolving || item.State == DlState.Finalizing ||
                    item.State == DlState.Installing)
                { error = "Active download cannot move"; return false; }
                _items.RemoveAt(index);
                _items.Insert(index - 1, item);
                SaveManifest();
                return true;
            }
        }

        public static bool IsTerminal(DlState s)
        {
            return s == DlState.Completed || s == DlState.Installed ||
                   s == DlState.Failed || s == DlState.Canceled;
        }

        /// <summary>Remove a queue row after its owned files and installer have stopped.</summary>
        public bool Remove(string id, out string error)
        {
            error = null;
            string residentReleaseId = null;
            DlItem residentReleaseItem = null;
            int residentReleaseAttempt = 0;
            string residentReleaseGeneration = null;
            lock (_lock)
            {
                var it = Find(id);
                if (it == null)
                {
                    error = "Download not found";
                    return false;
                }
                if (it.State == DlState.Submitted) it.InstallSubmitted = true;
                if ((it.ResidentStaged || it.ResidentArchive) &&
                    (it.Background || it.ResidentRemovePending || ResidentDownloadService.HasJob(it.Id)))
                {
                    bool wasPending = it.ResidentRemovePending, wasCanceled = it.CancelRequested;
                    bool wasDetached = it.ResidentDetachRequested;
                    string wasGeneration = it.ResidentGeneration;
                    string previousStatus = it.StatusText;
                    // Removing again a removal the worker has not confirmed for a minute is the
                    // user's Remove anyway: SSPI stops tracking it and leaves the PS4 task as it is.
                    bool removeAnyway = wasPending && it.ResidentRemoveStuck && !wasDetached;
                    if (removeAnyway) it.ResidentDetachRequested = true;
                    string ownedGeneration;
                    bool ownedJobFound = ResidentDownloadService.TryGetJobIdentity(it.Id, it.DestPath, out ownedGeneration);
                    if (string.IsNullOrEmpty(it.ResidentGeneration) && ownedJobFound && !string.IsNullOrEmpty(ownedGeneration))
                        it.ResidentGeneration = ownedGeneration;
                    it.ResidentRemovePending = true;
                    it.CancelRequested = true;
                    it.StatusText = it.ResidentDetachRequested ? "Stopping tracking; the PS4 download and downloaded files are kept"
                        : "Removing background job; waiting for worker acknowledgement";
                    if (!SaveManifest())
                    {
                        it.ResidentRemovePending = wasPending; it.CancelRequested = wasCanceled;
                        it.ResidentGeneration = wasGeneration; it.ResidentDetachRequested = wasDetached;
                        it.StatusText = previousStatus; error = "Could not save the removal request"; return false;
                    }
                    if (removeAnyway)
                    {
                        it.ResidentDetachSentAt = DateTime.UtcNow.Ticks; it.ResidentRemoveStuck = false;
                        SspiLog.Write("download", "event=remove-anyway job=" + it.Id + " generation=" +
                            (string.IsNullOrEmpty(it.ResidentGeneration) ? "none" : it.ResidentGeneration) +
                            " worker=" + ResidentDownloadService.DetachWorkerState);
                    }
                    // ReleaseResidentJob adopts the owner of this ID when the saved
                    // generation or destination is stale; skipping here left it unreleased.
                    residentReleaseId = it.Id;
                    residentReleaseItem = it;
                    residentReleaseAttempt = it.AttemptId;
                    residentReleaseGeneration = it.ResidentGeneration;
                }
                else
                {
                    if ((it.Background || (it.State == DlState.Submitted && it.BgftTaskId >= 0)) &&
                        !_activeIds.Contains(it.Id))
                    {
                        bool requested = it.RemoveRequested, canceled = it.CancelRequested;
                        it.RemoveRequested = it.CancelRequested = true;
                        if (!SaveManifest())
                        { it.RemoveRequested = requested; it.CancelRequested = canceled; error = "Could not save the removal request"; return false; }
                        int activeTask;
                        if (!CancelRowBackground(it, out activeTask, out error))
                        {
                            it.BgftTaskId = activeTask;
                            it.StatusText = "Removal saved; waiting for PS4 acknowledgement";
                            error = null;
                            return true;
                        }
                        RetireCanceledBgftSubmission(it);
                        ClearBackground(it);
                        it.CancelRequested = false;
                        it.State = DlState.Canceled;
                        it.StatusText = it.InstallSubmitted ?
                            "PS4 installation unconfirmed; package retained" : "Canceled";
                        if (!SaveManifest())
                        { error = "Could not save the installer cancellation proof; package retained"; return false; }
                    }
                    // Only a claimed worker writes Resolving/Downloading/Finalizing rows. With no
                    // worker, BGFT task or resident job, nothing would ever acknowledge a stop
                    // request, so the stale row is canceled and removed now.
                    if (!_activeIds.Contains(it.Id) && !it.Background && it.BgftTaskId < 0 &&
                        (it.State == DlState.Resolving || it.State == DlState.Downloading || it.State == DlState.Finalizing) &&
                        !_activePaths.Contains(NormalizePath(it.DestPath)) && !ResidentDownloadService.HasJob(it.Id))
                    {
                        it.CancelRequested = it.PauseRequested = false;
                        it.BytesPerSec = it.EtaSeconds = 0;
                        it.State = DlState.Canceled;
                        it.StatusText = "Canceled";
                    }
                    if (_activeIds.Contains(it.Id) || it.State == DlState.Resolving ||
                        it.State == DlState.Downloading || it.State == DlState.Finalizing ||
                        it.State == DlState.Installing)
                    {
                        bool requested = it.RemoveRequested, canceled = it.CancelRequested;
                        it.RemoveRequested = it.CancelRequested = true;
                        if (SaveManifest()) { it.StatusText = it.State == DlState.Installing
                            ? "Removing; waiting for PS4 installer acknowledgement" : "Removing; waiting for file writer to stop"; return true; }
                        it.RemoveRequested = requested; it.CancelRequested = canceled;
                        error = "Could not save the removal request"; return false;
                    }
                    if (!IsTerminal(it.State) && it.State != DlState.Queued && it.State != DlState.Paused &&
                        it.State != DlState.Submitted)
                    {
                        error = "Cannot remove now";
                        return false;
                    }
                    bool wasRemoveRequested = it.RemoveRequested;
                    it.RemoveRequested = true;
                    if (!SaveManifest())
                    { it.RemoveRequested = wasRemoveRequested; error = "Could not save the removal request"; return false; }
                    // A submission without a remaining stop handle may still read
                    // its input. Explicit removal drops tracking and retains all files.
                    if (it.InstallSubmitted && !it.InstallConfirmed && !it.Background &&
                        it.BgftTaskId < 0 && !ResidentDownloadService.HasJob(it.Id))
                    {
                        _items.Remove(it);
                        SaveManifest();
                        return true;
                    }
                    // Queued/Paused: treat as remove-from-queue (cancel leftovers).
                    if (it.State == DlState.Queued || it.State == DlState.Paused)
                    {
                        if (it.Background || it.State == DlState.Submitted)
                        {
                            int activeTask;
                            string cancelError;
                            if (!CancelRowBackground(it, out activeTask, out cancelError))
                            {
                                it.BgftTaskId = activeTask;
                                error = cancelError ?? "BGFT cancel failed";
                                return false;
                            }
                            RetireCanceledBgftSubmission(it);
                            ClearBackground(it);
                            if (!SaveManifest())
                            { error = "Could not save the installer cancellation proof; package retained"; return false; }
                        }
                        if (!TryCleanupFanOutPending(it))
                        {
                            error = "Extracted package cleanup failed";
                            return false;
                        }
                        if (!TryCleanupOwnedFiles(it, out error))
                        { it.StatusText = error; SaveManifest(); return false; }
                        _items.Remove(it);
                        SaveManifest();
                        return true;
                    }
                    if (it.Background && it.State == DlState.Failed)
                    {
                        int activeTask;
                        string cancelError;
                        if (!CancelRowBackground(it, out activeTask, out cancelError))
                        {
                            it.BgftTaskId = activeTask;
                            error = cancelError ?? "Cancel background first";
                            return false;
                        }
                        RetireCanceledBgftSubmission(it);
                        ClearBackground(it);
                        if (!SaveManifest())
                        { error = "Could not save the installer cancellation proof; package retained"; return false; }
                    }
                    if (string.Equals(it.AccessType, "FanOutSource",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        if (!TryCleanupOwnedFiles(it, out error))
                        { it.StatusText = error; SaveManifest(); return false; }
                        _items.Remove(it);
                        SaveManifest();
                        return true;
                    }
                    if (!TryCleanupFanOutPending(it))
                    {
                        error = "Extracted package cleanup failed";
                        return false;
                    }
                    if (!TryCleanupOwnedFiles(it, out error))
                    { it.StatusText = error; SaveManifest(); return false; }
                    PreserveConfirmedDependency(it);
                    _items.Remove(it);
                    SaveManifest();
                }
            }
            if (residentReleaseId != null)
            {
                ReleaseResidentJob(residentReleaseItem, residentReleaseAttempt, residentReleaseGeneration, true);
            }
            return true;
        }

        /// <summary>
        /// Remove disposable terminal history rows. A completed PKG remains actionable and must
        /// stay until explicitly removed; submitted installs remain verification-pending.
        /// Does not cancel active or ambiguous BGFT tasks.
        /// </summary>
        public int ClearTerminal(out int skipped, out string error)
        {
            error = null;
            skipped = 0;
            var candidates = new List<DlItem>();
            var drop = new List<DlItem>();
            var priorRequests = new List<bool>();
            lock (_lock)
            {
                foreach (var it in _items)
                {
                    if (_activeIds.Contains(it.Id))
                    {
                        skipped++;
                        continue;
                    }
                    // "Clear finished" must not orphan an installable PKG. Submitted is not
                    // currently terminal, but keep the guard explicit if that classification
                    // changes later.
                    if ((it.State == DlState.Completed || it.State == DlState.Submitted) &&
                        !string.IsNullOrEmpty(it.DestPath) && File.Exists(it.DestPath))
                    {
                        skipped++;
                        continue;
                    }
                    if (!IsTerminal(it.State))
                    {
                        skipped++;
                        continue;
                    }
                    if (it.Background || (it.InstallSubmitted && !it.InstallConfirmed))
                    {
                        skipped++;
                        continue;
                    }
                    candidates.Add(it);
                }
                foreach (var it in candidates)
                { priorRequests.Add(it.RemoveRequested); it.RemoveRequested = true; }
                if (candidates.Count > 0 && !SaveManifest())
                {
                    for (int i = 0; i < candidates.Count; i++) candidates[i].RemoveRequested = priorRequests[i];
                    skipped += candidates.Count;
                    error = "Could not save the cleanup request";
                    return 0;
                }
                foreach (var it in candidates)
                {
                    if (!TryCleanupFanOutPending(it))
                    {
                        it.StatusText = "Extracted package cleanup pending";
                        skipped++;
                        continue;
                    }
                    string cleanupError;
                    if (!TryCleanupOwnedFiles(it, out cleanupError))
                    {
                        it.StatusText = cleanupError;
                        skipped++;
                        continue;
                    }
                    drop.Add(it);
                }
                foreach (var it in drop)
                {
                    PreserveConfirmedDependency(it);
                    _items.Remove(it);
                }
                if (candidates.Count > 0)
                    SaveManifest();
            }
            return drop.Count;
        }
    }
}
