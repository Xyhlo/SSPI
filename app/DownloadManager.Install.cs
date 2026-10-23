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
        void RetryConfirmedInstallCleanup()
        {
            long now = TransferClockMs();
            if (now < _nextOwnedCleanupAt) return;
            _nextOwnedCleanupAt = now + 15000;
            lock (_lock)
            {
                // A prior MarkInstalled save may have failed. Never let this timer
                // delete its input until the confirmed outcome is durable.
                if (!SaveManifest()) return;
                bool changed = false;
                foreach (DlItem item in _items)
                {
                    if (item.State != DlState.Installed || !item.InstallConfirmed || item.Background ||
                        _activeIds.Contains(item.Id) || item.BgftTaskId >= 0 ||
                        ((item.ResidentArchive || item.ResidentStaged || !string.IsNullOrEmpty(item.ResidentGeneration)) &&
                            ResidentDownloadService.HasJob(item.Id))) continue;
                    if (item.LocalSource)
                    {
                        if (item.ResidentArchive) TryDeleteResidentExtractionDirectory(item);
                        continue;
                    }
                    string error;
                    bool cleaned = TryCleanupOwnedFiles(item, out error);
                    if (!cleaned)
                    {
                        if (item.StatusText == null ||
                            item.StatusText.IndexOf("cleanup pending", StringComparison.OrdinalIgnoreCase) < 0)
                        { item.StatusText = (item.StatusText ?? "Installed") + "; local cleanup pending"; changed = true; }
                    }
                    else if (!string.IsNullOrEmpty(item.StatusText))
                    {
                        string status = item.StatusText.Replace("; PKG retained", "")
                            .Replace("; local cleanup pending", "")
                            .Replace("; local PKG retained", "");
                        if (status != item.StatusText) { item.StatusText = status; changed = true; }
                    }
                }
                if (changed) SaveManifest();
            }
        }

        void PreserveConfirmedDependency(DlItem removed)
        {
            bool confirmed = removed.State == DlState.Installed && removed.InstallConfirmed;
            foreach (DlItem child in _items)
            {
                if (object.ReferenceEquals(child, removed) ||
                    !string.Equals(child.InstallAfterId, removed.Id, StringComparison.Ordinal)) continue;

                if (confirmed)
                {
                    child.InstallAfterConfirmed = true;
                    continue;
                }

                string dependencyId;
                bool dependencyConfirmed;
                bool hasDependency = ResolveInstallDependency(removed, out dependencyId, out dependencyConfirmed);
                DlItem dependency = hasDependency ? Find(dependencyId) : null;
                bool preservesEarlierPackage = dependency != null &&
                    InstallPrecedes(dependency, removed) && InstallPrecedes(dependency, child);
                bool preservesConfirmedMissingAnchor = dependency == null && hasDependency && dependencyConfirmed &&
                    InstallRank(removed.Kind) == 1 && InstallRank(child.Kind) >= 1 &&
                    !string.Equals(child.Kind, "theme", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(child.Kind, "theme-license", StringComparison.OrdinalIgnoreCase);

                // Archive manifests created before independent DLC ordering was fixed can
                // contain DLC -> DLC chains. Splice those through to the chain's base/update
                // anchor, retaining a missing failed anchor instead of inventing proof.
                if (IsSameTitleDlcDependency(removed, child) || preservesEarlierPackage || preservesConfirmedMissingAnchor)
                {
                    child.InstallAfterId = dependencyId;
                    child.InstallAfterConfirmed = dependency != null
                        ? InstallPrerequisiteReady(dependency)
                        : dependencyConfirmed && hasDependency;
                }
            }
        }

        public void QueueLocalInstall(string id)
        {
            lock (_lock) {
                var it = Find(id); if (it == null || it.Background || it.State == DlState.Installing || it.State == DlState.Submitted) return;
                it.State = DlState.Queued; it.ForceLocalInstall = !it.LocalSource && !BackgroundSelected; it.Error = null;
                it.InstallRetries = 0;
                it.InstallOrderReady = false;
                it.PauseRequested = it.CancelRequested = false; it.RetryAfterUtcTicks = 0;
                it.StatusText = BackgroundSelected ? "Queued for resident installation" : "Queued for package verification";
            }
            SaveManifest();
        }

        public bool QueuePreparedLocalInstall(string id, int attempt, string installError = null)
        {
            lock (_lock)
            {
                var item = Find(id);
                if (item == null || item.AttemptId != attempt || item.State != DlState.Installing ||
                    item.Background || item.BgftTaskId >= 0 || item.InstallSubmitted) return false;
                if (item.CancelRequested || item.RemoveRequested || item.PauseRequested)
                {
                    bool cancel = item.CancelRequested || item.RemoveRequested;
                    item.State = cancel ? DlState.Canceled : DlState.Paused;
                    item.StatusText = cancel ? "Canceled; PKG retained" : "Paused; PKG retained";
                    item.Error = null;
                    item.CancelRequested = item.PauseRequested = false;
                    item.BytesPerSec = item.EtaSeconds = 0;
                    SaveManifest();
                    return false;
                }
                // The PS4 refused the storage route without a task: later attempts for this
                // base use loopback BGFT. Losing the flag only repeats that refusal once.
                if (installError != null && !item.BaseStorageRefused &&
                    installError.StartsWith(PkgInstallPolicy.StorageHttpFallback, StringComparison.Ordinal))
                {
                    item.BaseStorageRefused = true;
                    SspiLog.Write("download", "event=base-storage-refused job=" + id + " next=loopback detail=" +
                        installError.Substring(PkgInstallPolicy.StorageHttpFallback.Length));
                }
                item.State = DlState.Completed;
                QueueLocalInstall(id);
                return item.State == DlState.Queued;
            }
        }

        static void CompleteByteCounters(DlItem item)
        {
            long size = item.Total > 100 ? item.Total : Math.Max(item.ExpectedByteSize, item.BgftExpectedSize);
            try { if (File.Exists(item.DestPath)) size = new FileInfo(item.DestPath).Length; } catch { }
            item.Done = item.Total = Math.Max(0, size);
        }

        public int MarkInstalling(string id, string msg)
        {
            lock (_lock)
            {
                var it = Find(id);
                if (it == null) return -1;
                it.AttemptId++;
                it.State = DlState.Installing;
                it.InstallConfirmed = false;
                it.InstallOrderReady = false;
                it.StatusText = msg ?? "Installing...";
                SaveManifest();
                return it.AttemptId;
            }
        }

        static bool AcceptInstallCallback(DlItem item, int attempt)
        {
            return item != null && (attempt < 0 || item.AttemptId == attempt) &&
                item.State != DlState.Canceled && item.State != DlState.Installed;
        }

        public void MarkInstalled(string id, string msg)
        {
            MarkInstalled(id, msg, false);
        }

        public void MarkInstallAccepted(string id, string msg)
        {
            MarkInstallAccepted(id, msg, -1);
        }

        public void MarkInstallAccepted(string id, string msg, int bgftTaskId, int attempt = -1)
        {
            lock (_lock)
            {
                var it = Find(id);
                if (!AcceptInstallCallback(it, attempt)) return;
                it.State = DlState.Submitted;
                it.InstallConfirmed = false;
                it.InstallSubmitted = true;
                it.InstallOrderReady = false;
                it.Error = null;
                it.StatusText = msg ?? "Sent to PS4 — verify · PKG kept";
                SspiLog.Write("download", "event=install-verification-pending title=" + it.TitleId +
                    " kind=" + it.Kind + " task=" + bgftTaskId + " job=" + it.Id);
                it.BytesPerSec = 0;
                it.EtaSeconds = 0;
                if (bgftTaskId >= 0)
                    it.BgftTaskId = bgftTaskId;
                // AppInstUtil exposes no stop handle. Honor a request made during
                // registration by releasing tracking, retaining its accepted input.
                if (bgftTaskId < 0 && (it.CancelRequested || it.RemoveRequested))
                {
                    it.State = DlState.Canceled;
                    it.CancelRequested = false;
                    it.StatusText = "Stopped tracking installation; unconfirmed package retained";
                }
                if (!string.IsNullOrEmpty(it.DestPath) && File.Exists(it.DestPath))
                    it.Done = it.Total = new FileInfo(it.DestPath).Length;
                SaveManifest();
            }
        }

        public void MarkAlreadyInstalled(string id, string msg, int attempt = -1)
        {
            lock (_lock)
            {
                var it = Find(id);
                if (!AcceptInstallCallback(it, attempt)) return;
                it.State = DlState.Completed;
                it.InstallConfirmed = false;
                it.Error = null;
                it.StatusText = msg ?? "Already installed; PKG kept";
                it.InstallOrderReady = true;
                if (!string.IsNullOrEmpty(it.DestPath) && File.Exists(it.DestPath))
                    it.Done = it.Total = new FileInfo(it.DestPath).Length;
                _installRevision++;
                SaveManifest();
            }
        }

        public bool HasPendingInstall(string titleId)
        {
            lock (_lock)
            {
                foreach (var it in _items)
                    if (string.Equals(it.TitleId, titleId, StringComparison.OrdinalIgnoreCase) &&
                        (it.State == DlState.Installing || it.State == DlState.Submitted))
                        return true;
                return false;
            }
        }

        public bool DismissSubmitted(string id, out string error)
        {
            error = null;
            lock (_lock)
            {
                var it = Find(id);
                if (it == null)
                {
                    error = "Download not found";
                    return false;
                }
                if (it.State != DlState.Submitted)
                {
                    error = "Install is not awaiting verification";
                    return false;
                }
                if (it.Background || it.BgftTaskId >= 0)
                { error = "Use Remove to stop the owned PS4 task before retrying"; return false; }
                it.InstallSubmitted = true;
                it.AttemptId++;
                it.InstallConfirmed = false;
                it.InstallOrderReady = false;
                if (!string.IsNullOrEmpty(it.DestPath) && File.Exists(it.DestPath))
                {
                    it.State = DlState.Completed;
                    it.InstallConfirmed = false;
                    it.StatusText = "Submission cleared — CROSS retries install";
                }
                else
                {
                    it.State = DlState.Failed;
                    it.InstallConfirmed = false;
                    it.Error = "Submitted PKG is missing";
                    it.StatusText = "PKG missing — re-download";
                    it.Done = 0;
                    it.Total = 0;
                }
                SaveManifest();
                return true;
            }
        }

        /// <summary>
        /// Confirmed install success. Owned downloaded input is removed after
        /// proof is saved; the legacy bool argument does not affect cleanup.
        /// Borrowed local originals are always retained.
        /// </summary>
        public bool MarkInstalled(string id, string msg, bool deleteLocalPackage, int attempt = -1)
        {
            lock (_lock)
            {
                var it = Find(id);
                if (!AcceptInstallCallback(it, attempt)) return false;
                it.State = DlState.Installed;
                it.InstallConfirmed = true;
                _nextInstallHandoffAt = TransferClockMs() + 3000;
                it.InstallOrderReady = true;
                PreserveConfirmedDependency(it);
                _installRevision++;
                it.Error = null;
                it.StatusText = msg ?? "Installed";
                it.BytesPerSec = 0;
                it.EtaSeconds = 0;
                // Staging storage may disappear after the existence check. The
                // confirmed install keeps its recorded size if metadata is unavailable.
                try { if (File.Exists(it.DestPath)) it.Total = new FileInfo(it.DestPath).Length; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                it.Done = it.Total;
                ClearBackground(it);
                it.Background = false;
                bool deleted = false;
                // Save proof before deletion and retain ownership until cleanup finishes.
                // A retry cannot replace this attempt between the save and file deletion.
                bool proofSaved = SaveManifest();
                if (proofSaved && IsOwnedDownloadPath(it.DestPath))
                    deleted = DeleteUnclaimedDownloadFiles(it);
                if (!proofSaved)
                    it.StatusText = (msg ?? "Installed") +
                        "; PKG retained because history could not be saved";
                else if (!deleted)
                    it.StatusText = (msg ?? "Installed") + "; PKG retained";
                SaveManifest();
                SspiLog.Write("download", "event=install-confirmed title=" + it.TitleId + " kind=" + it.Kind +
                    " job=" + it.Id + " bytes=" + it.Total + " input_deleted=" + deleted);
                return deleted;
            }
        }

        public void UpdateInstallProgress(string id, int percent, int attempt = -1)
        {
            lock (_lock)
            {
                var it = Find(id);
                if (!AcceptInstallCallback(it, attempt)) return;
                it.State = DlState.Installing;
                it.InstallConfirmed = false;
                it.StatusText = percent >= 0 ? "Installing " + percent + "%" : "Installing...";
                if (percent >= 0)
                {
                    if (File.Exists(it.DestPath)) it.Total = new FileInfo(it.DestPath).Length;
                    it.Done = it.Total;
                }
            }
        }

        public void SetTitleArtwork(string titleId, string name, string imageUrl)
        {
            if (string.IsNullOrEmpty(titleId) || string.IsNullOrEmpty(imageUrl)) return;
            bool changed = false;
            lock (_lock)
            {
                foreach (var item in _items)
                {
                    if (!string.Equals(item.TitleId, titleId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.Equals(item.ImageUrl, imageUrl, StringComparison.Ordinal))
                    {
                        item.ImageUrl = imageUrl;
                        changed = true;
                    }
                    if (!string.IsNullOrEmpty(name) &&
                        !string.Equals(item.Name, name, StringComparison.Ordinal))
                    {
                        item.Name = name;
                        changed = true;
                    }
                }
            }
            if (changed) SaveManifest();
        }

        public void MarkInstallFailed(string id, string err, int attempt = -1)
        {
            string path;
            string titleId;
            lock (_lock)
            {
                var it = Find(id);
                if (!AcceptInstallCallback(it, attempt)) return;
                if (attempt < 0) attempt = it.AttemptId;
                if (it.BgftTaskId >= 0 && it.InstallSubmitted && !it.InstallConfirmed)
                {
                    MarkInstallAccepted(id, "PS4 task retained after install failure: " + ClipMsg(err, 110),
                        it.BgftTaskId, attempt);
                    return;
                }
                path = it.DestPath;
                titleId = it.TitleId;
            }

            PkgValResult vr;
            string detail;
            string kind = null;
            lock (_lock)
            {
                var current = Find(id);
                if (current != null) kind = current.Kind;
            }
            bool valid = PkgValidator.TryValidateDownload(path, titleId, kind, out vr, out detail);
            // Keep a valid local PKG so user can CROSS install again — do not delete on install fail.
            lock (_lock)
            {
                var it = Find(id);
                if (!AcceptInstallCallback(it, attempt)) return;
                it.InstallOrderReady = false;
                if (valid)
                {
                    it.State = DlState.Completed;
                    it.InstallConfirmed = false;
                    it.Error = err;
                    it.StatusText = "Install failed: " + ClipMsg(err ?? "unknown error", 110);
                    it.Done = it.Total = new FileInfo(path).Length;
                    it.Background = false;
                    ClearBackground(it);
                }
                else
                {
                    it.State = DlState.Failed;
                    it.InstallConfirmed = false;
                    it.Error = err;
                    it.StatusText = "Package invalid: " + (detail ?? err ?? "");
                }
                SaveManifest();
            }
        }

        public void MarkPackageInvalid(string id, string err, int attempt = -1)
        {
            lock (_lock)
            {
                var it = Find(id);
                if (!AcceptInstallCallback(it, attempt)) return;
                // Validation failure retains bytes for inspection or stage retry.
                it.State = DlState.Failed;
                it.InstallConfirmed = false;
                it.InstallOrderReady = false;
                it.Error = err;
                it.StatusText = "Bad PKG — CROSS to re-download";
            }
            SaveManifest();
        }

        void RunLocalSource(DlItem job, int attempt)
        {
            if (CommitRequestedStop(job, attempt)) return;
            if (!File.Exists(job.DestPath))
            {
                lock (_lock) if (job.AttemptId == attempt) {
                    if (CommitRequestedStop(job, attempt)) return;
                    job.State = DlState.Queued; job.Error = null; job.BytesPerSec = 0; job.EtaSeconds = 0;
                    job.StatusText = "Reconnect the USB drive containing " + Path.GetFileName(job.DestPath);
                    job.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(5).Ticks;
                }
                SaveManifest(); return;
            }
            LocalInstallSource source = LocalInstallSource.Read(job.DestPath, false);
            if (source.Fingerprint != job.LocalSourceFingerprint || source.Size != job.ExpectedByteSize)
                throw new IOException("USB file changed since it was queued; remove this row and select the file again");
            if (LocalInstallSource.IsInboxPath(job.DestPath))
            {
                // FTP uploads can also be opened with the PS4 package installer. Check
                // the live install state right before registering, never an older row.
                bool installed;
                string conflict = LocalInstallConflict(source, out installed);
                if (installed) { MarkAlreadyInstalled(job.Id, conflict, attempt); return; }
                if (conflict != null) { SetFailureIfCurrent(job, attempt, conflict); return; }
            }
            // Only a worker that advertises the FTP inbox admits inbox files as borrowed
            // sources; with an older worker or another data folder SSPI installs them.
            bool residentSource = !LocalInstallSource.IsInboxPath(job.DestPath) ||
                (LocalInstallSource.ResidentCanBorrow(job.DestPath) && ResidentDownloadService.WatchesFtpInbox);
            if (!BackgroundSelected || !residentSource)
            {
                lock (_lock) { job.ForceLocalInstall = true; job.StatusText = "Preparing USB installation in SSPI"; }
                if (source.ExpectedKind == 0)
                {
                    AppSettings.RequireStaging(AppSettings.DownloadDir);
                    FanOutArchive(job, attempt);
                }
                else FinishDownloadedObject(job, attempt);
                return;
            }
            string dependency = ResidentDependencyId(job);
            if (source.ExpectedKind == 0 && PackageArchive.Detect(job.DestPath) == PackageObjectKind.SevenZip)
            {
                string archiveError = ResidentArchivePasswordError(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c }, job.ArchivePassword);
                if (archiveError != null) { SetFailureIfCurrent(job, attempt, archiveError); return; }
            }
            if (!CanInstallWithResidentDependency(dependency))
            {
                lock (_lock) if (job.AttemptId == attempt) {
                    if (CommitRequestedStop(job, attempt)) return;
                    job.State = DlState.Queued; job.StatusText = "Waiting for the required package to finish installing";
                    job.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(2).Ticks;
                }
                SaveManifest(); return;
            }
            HandoffStagedPackage(job, attempt, "", source.TitleId, source.Kind, source.ContentId,
                source.Size, source.ExpectedKind == 0, false, true);
        }

        // Fresh install state for a local PKG: a live PS4 task that SSPI does not own
        // (for example the PS4 package installer) or an identical installed container.
        // Patches share the base content ID, so a live base task also blocks a patch.
        internal static string LocalInstallConflict(LocalInstallSource source, out bool installed)
        {
            installed = false;
            if (source == null || source.ExpectedKind < 6 || source.ExpectedKind > 8 || string.IsNullOrEmpty(source.ContentId)) return null;
            int task;
            // The task state cannot show every finished task (TryFindActiveForeignTask), so
            // the message says how to clear one that has finished or stopped.
            if (PkgInstaller.TryFindActiveForeignTask(source.ContentId, source.ExpectedKind, out task) ||
                (source.ExpectedKind == 8 && PkgInstaller.TryFindActiveForeignTask(source.ContentId, 6, out task)))
                return "PS4 is already installing this package; no second install was started. File kept. " +
                    "If it is not installing, open PS4 Notifications > Downloads, clear the finished or stopped entry for this title, then retry";
            installed = source.ExpectedKind == 6 ? PkgInstaller.IsBasePackageInstalled(source.Path, source.TitleId) :
                source.ExpectedKind == 8 ? PkgInstaller.IsPatchPackageInstalled(source.Path, source.TitleId) :
                PkgInstaller.IsAddonInstalled(source.Path, false);
            return installed ? "Already installed; file kept" : null;
        }

        static bool TryValidateLocalPackage(DlItem item, out PkgContentKind actualKind,
            out string actualKindName, out string contentId, out string actualTitleId,
            out long size, out string error)
        {
            actualKind = PkgContentKind.Unknown;
            actualKindName = "";
            contentId = "";
            actualTitleId = item != null ? item.TitleId : "";
            size = 0;
            if (!VerifyCandidateFile(item, out error)) return false;
            if (!PkgValidator.TryGetContentKind(item.DestPath, out actualKind, out error)) return false;
            if (item.Kind == "theme-license" && actualKind == PkgContentKind.AddOn &&
                PkgIntegrity.IsNoDataLicense(item.DestPath)) actualKind = PkgContentKind.SystemThemeLicense;
            actualKindName = KindName(actualKind, item.Kind);
            PkgValResult result;
            if (!PkgValidator.TryValidateDownload(item.DestPath, item.TitleId, item.Kind,
                out result, out error)) return false;
            if (!PkgValidator.TryGetContentId(item.DestPath, out contentId) ||
                string.IsNullOrEmpty(contentId))
            {
                error = "PKG content ID is missing";
                return false;
            }
            if (!string.IsNullOrEmpty(item.ExpectedContentId) &&
                !string.Equals(item.ExpectedContentId.Trim(), contentId,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "PKG content ID mismatch";
                return false;
            }
            string parsedTitleId;
            if (PkgValidator.TryGetTitleIdFromContentId(contentId, out parsedTitleId))
                actualTitleId = parsedTitleId;
            if (!PkgValidator.CheckRequestedIdentity(item.Kind, actualKind, item.TitleId, contentId, out error)) return false;
            size = new FileInfo(item.DestPath).Length;
            return size >= 4;
        }

        static string KindName(PkgContentKind kind, string requested = null)
        {
            if (kind == PkgContentKind.SystemThemeLicense) return "theme-license";
            if (kind == PkgContentKind.SystemTheme) return "theme";
            if (kind == PkgContentKind.Patch) return string.Equals(requested, "backport", StringComparison.OrdinalIgnoreCase) ? "backport" : "update";
            if (kind == PkgContentKind.AddOn) return "dlc";
            return "game";
        }

        void HandleValidatedLocalPackage(DlItem job, int attempt, PkgContentKind actualKind,
            string actualKindName, string contentId, string actualTitleId, long size)
        {
            string packageVersion = actualKind == PkgContentKind.BaseGame && InstalledTitleScan.IsHomebrew(actualTitleId)
                ? HomebrewPackageVersion(job.DestPath, job.PackageVersion) : job.PackageVersion;
            bool replace = ReplacesOlderHomebrew(actualTitleId, actualKind, packageVersion);
            if (BackgroundSelected && !UseInAppThemeLicense(job) && !replace &&
                !InstalledTitleScan.IsHomebrew(actualTitleId) && !BackgroundCannotOpen(job.DestPath))
            {
                if (WaitForSelectedBackground(job, attempt)) return;
                string source = !string.IsNullOrEmpty(job.HttpRetryUrl) ? job.HttpRetryUrl : job.HosterUrl;
                Uri uri;
                if (!Uri.TryCreate(source, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                { WaitForSelectedBackground(job, attempt, "Retained package needs its original source metadata for resident installation"); return; }
                HandoffStagedPackage(job, attempt, source, actualTitleId, actualKindName, contentId, size);
                return;
            }
            lock (_packageInstallGate)
            {
                bool busy = TransferClockMs() < _nextInstallHandoffAt;
                lock (_lock) foreach (var other in _items)
                    if (other != job && (other.State == DlState.Installing ||
                        (other.State == DlState.Submitted && !other.InstallOrderReady && !other.SubmittedNeedsAttention))) busy = true;
                if (busy) { RequeueValidatedLocalPackage(job, attempt, size); return; }
                try { HandleValidatedLocalPackageCore(job,attempt,actualKind,actualKindName,contentId,actualTitleId,size,replace,packageVersion); }
                finally { _nextInstallHandoffAt=TransferClockMs()+3000; }
            }
        }

        void HandleValidatedLocalPackageCore(DlItem job, int attempt, PkgContentKind actualKind,
            string actualKindName, string contentId, string actualTitleId, long size, bool replace, string packageVersion)
        {
            lock (_packageInstallGate)
            {
                if (CommitRequestedStop(job, attempt)) return;
                lock (_lock)
                {
                    if (job.AttemptId != attempt) return;
                    job.Kind = actualKindName;
                    // A sister-edition DLC keeps the row under the game that declares it.
                    bool sisterAddon = actualKind == PkgContentKind.AddOn && !string.IsNullOrEmpty(job.TitleId) &&
                        !PkgValidator.ContentIdMatchesTitleId(contentId, job.TitleId);
                    if (!string.IsNullOrEmpty(actualTitleId) && !sisterAddon) job.TitleId = actualTitleId;
                    job.Done = job.Total = size;
                    job.BytesPerSec = 0;
                    job.EtaSeconds = 0;
                    job.Error = null;
                    job.InstallConfirmed = false;
                    job.RetryAfterUtcTicks = 0;
                    _nerd.Finalizing = false;
                }

                string metadataError;
                if (!PkgIntegrity.CheckMetadata(job.DestPath, actualTitleId,
                    PkgInstallPolicy.MetadataCategory(actualKind), out metadataError))
                { MarkInstallFailed(job.Id, metadataError, attempt); return; }
                if (!InstallDependencyReady(job))
                {
                    lock (_lock)
                    {
                        if (CommitRequestedStop(job, attempt)) return;
                        job.State = DlState.Queued;
                        job.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(2).Ticks;
                    }
                    SaveManifest(); return;
                }
                bool useLoopback = !replace && (PkgInstaller.UseLoopbackInstall(actualKind, job.DestPath) ||
                    (actualKind == PkgContentKind.BaseGame && job.BaseStorageRefused) ||
                    (actualKind == PkgContentKind.AddOn && job.AddOnBgftFallback));
                if (useLoopback && _loopback.IsOwnedByOther(job.Id))
                {
                    RequeueValidatedLocalPackage(job, attempt, size);
                    return;
                }

                if (actualKind == PkgContentKind.Patch)
                {
                    string integrityError;
                    if (!PkgIntegrity.WaitForInstalledBase(job.DestPath, actualTitleId,
                        text => { lock (_lock) { if (AcceptInstallCallback(job, attempt)) job.StatusText = text; } },
                        () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested, out integrityError))
                    {
                        if (CommitRequestedStop(job, attempt)) return;
                        if (PkgIntegrity.IsInstalledBaseUnavailable(integrityError) &&
                            RetryLocalInstall(job, attempt, integrityError)) return;
                        MarkInstallFailed(job.Id, integrityError, attempt); return;
                    }
                    if (!PkgIntegrity.ValidatePatch(job.DestPath, actualTitleId, FirmwareInfo.Probe(),
                        text => { lock (_lock) { if (AcceptInstallCallback(job, attempt)) job.StatusText = text; } },
                        () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested, out integrityError))
                    { MarkInstallFailed(job.Id, integrityError, attempt); return; }
                    string identityError;
                    if (!PkgValidator.CheckRequestedIdentity(actualKindName, actualKind, actualTitleId, contentId, out identityError))
                    { MarkInstallFailed(job.Id, identityError, attempt); return; }
                    try { job.PackageVersion = PkgIntegrity.SfoValue(PkgIntegrity.Entry(job.DestPath, 0x1000), "APP_VER"); } catch { }
                    string installedName, installedVersion, installedIcon, versionError;
                    InstalledTitleScan.ReadMeta(actualTitleId, out installedName, out installedVersion, out installedIcon);
                    if (!PkgIntegrity.CheckUpdateVersion(job.PackageVersion, installedVersion, out versionError))
                    { MarkInstallFailed(job.Id, versionError, attempt); return; }
                }
                if (WaitForSelectedBackground(job, attempt, "Background selected; validated package is waiting for resident installation")) return;
                bool baseReady = !PkgInstallPolicy.RequiresInstalledBase(actualKind) ||
                    (!string.IsNullOrEmpty(actualTitleId) && PkgInstaller.IsTitleInstalled(actualTitleId)) ||
                    (actualKind == PkgContentKind.AddOn && PkgInstaller.InstalledGameAcceptingAddon(contentId, job.TitleId) != null);
                string fallbackReason = null;
                bool foreignConflict = false;
                if (useLoopback && baseReady)
                {
                    string url;
                    string routeError;
                    bool busy;
                    if (!_loopback.TryRegister(job.Id, job.DestPath, size,
                        out url, out routeError, out busy))
                    {
                        if (busy)
                        {
                            RequeueValidatedLocalPackage(job, attempt, size);
                            return;
                        }
                        fallbackReason = routeError;
                    }
                    else if (!_loopback.TryMarkComplete(job.Id, out routeError))
                    {
                        _loopback.MarkFailed(job.Id);
                        fallbackReason = routeError;
                    }
                    else
                    {
                        int subType = PkgValidator.BgftSubTypeForKind(actualKindName);
                        bool titleAtStart;
                        bool titleKnown = TryInitialTitlePresence(actualKind, actualTitleId, out titleAtStart);
                        string displayName = !string.IsNullOrEmpty(job.Name)
                            ? job.Name : (!string.IsNullOrEmpty(actualTitleId) ? actualTitleId : "package");
                        bool registrationCurrent;
                        lock (_lock)
                        {
                            registrationCurrent = job.AttemptId == attempt &&
                                !job.PauseRequested && !job.CancelRequested;
                            if (registrationCurrent)
                            {
                                job.BgftLoopback = true;
                                job.BgftLocalInstall = true;
                                job.BgftLoopbackServed = false;
                                job.BgftContentId = contentId;
                                job.BgftSubType = subType;
                                job.BgftExpectedSize = size;
                                job.BgftTitlePresenceKnown = titleKnown;
                                job.BgftTitlePresentAtStart = titleAtStart;
                                job.BgftDownloadPhaseConfirmed = false;
                                ResetBgftTransientPolls(job);
                                job.State = DlState.Resolving;
                                job.Done = size;
                                job.Total = size;
                                job.StatusText = "Registering verified package for installation...";
                            }
                        }
                        if (!registrationCurrent)
                        {
                            _loopback.MarkFailed(job.Id);
                            _loopback.Release(job.Id);
                            CommitRequestedStop(job, attempt);
                            return;
                        }
                        SaveManifest();

                        int taskId;
                        string startError;
                        string packageType = PkgIntegrity.PackageType(job.DestPath);
                        long declaredSize = PkgIntegrity.BgftPackageSize(job.DestPath);
                        bool started = PkgInstaller.TryStartLoopbackBgftDownload(url, actualTitleId, contentId,
                            displayName, subType, size, out taskId, out startError, packageType, actualKind, declaredSize);
                        // Console evidence: with a zero declaration the internal API returns
                        // 0x80990004 and its debug-API retry returns 0x80990006, both taskless.
                        // Retry once with the real byte length in both the BGFT parameter and
                        // the reference JSON (the Remote Package Installer declaration format).
                        string zeroRejection = startError ?? "";
                        if (!started && taskId < 0 && declaredSize == 0 &&
                            actualKind == PkgContentKind.SystemThemeLicense &&
                            (zeroRejection.IndexOf("0x80990004", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             zeroRejection.IndexOf("0x80990006", StringComparison.OrdinalIgnoreCase) >= 0) &&
                            _loopback.DeclareTransferSize(job.Id))
                        {
                            LogBgftEvent("theme-license-size-fallback", job, startError);
                            started = PkgInstaller.TryStartLoopbackBgftDownload(url, actualTitleId, contentId,
                                displayName, subType, size, out taskId, out startError, packageType, actualKind, -1);
                        }
                        if (started)
                        {
                            bool accepted;
                            lock (_lock)
                            {
                                accepted = job.AttemptId == attempt &&
                                    !job.PauseRequested && !job.CancelRequested;
                                if (accepted)
                                {
                                    job.Background = true;
                                    job.BgftLoopback = true;
                                    job.BgftLocalInstall = true;
                                    job.BgftLoopbackServed = false;
                                    job.BgftTaskId = taskId;
                                    job.InstallSubmitted = true;
                                    job.BgftContentId = contentId;
                                    job.BgftSubType = subType;
                                    job.BgftPollFailures = 0;
                                    job.BgftExpectedSize = size;
                                    job.BgftTitlePresenceKnown = titleKnown;
                                    job.BgftTitlePresentAtStart = titleAtStart;
                                    job.BgftDownloadPhaseConfirmed = false;
                                    job.ForceLocalInstall = false;
                                    ResetBgftTransientPolls(job);
                                    job.State = DlState.Installing;
                                    job.Done = size;
                                    job.Total = size;
                                    job.StatusText = UseInAppThemeLicense(job) ? "PS4 importing theme license; keep SSPI open" : "PS4 importing verified package";
                                }
                            }
                            if (accepted)
                            {
                                SaveManifest();
                                User.NotifyToast("PS4 installation started");
                                return;
                            }
                            lock (_lock) job.BgftTaskId = taskId;
                            if (!TryCancelBackgroundIdentity(job, contentId, subType,
                                "registration completed after queue stop"))
                            {
                                SaveManifest();
                                return;
                            }
                            _loopback.MarkFailed(job.Id);
                            _loopback.Release(job.Id);
                            CommitRequestedStop(job, attempt);
                            return;
                        }

                        string registrationError = startError ?? "BGFT registration failed";
                        // Older installers strip 0x80990088 from this specific message.
                        // Recheck version intent before interpreting it as an ownership conflict.
                        bool alreadyInstalled = registrationError.IndexOf("0x80990088", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            registrationError.StartsWith("ALREADY_INSTALLED:", StringComparison.Ordinal) ||
                            registrationError.StartsWith("BGFT_UNRESOLVED:PS4 reports existing content, but this installation is not confirmed.", StringComparison.Ordinal);
                        if (taskId < 0 && alreadyInstalled &&
                            ReplacesOlderHomebrew(actualTitleId, actualKind, packageVersion))
                        {
                            _loopback.MarkFailed(job.Id);
                            _loopback.Release(job.Id);
                            lock (_lock)
                            {
                                if (CommitRequestedStop(job, attempt)) return;
                                ClearBackground(job);
                                job.ForceLocalInstall = true;
                            }
                            InstallValidatedLocalPackage(job, attempt, actualKind, actualKindName, actualTitleId, true, packageVersion);
                            return;
                        }
                        bool unresolvedTask = taskId >= 0 || registrationError.StartsWith(
                            "BGFT_UNRESOLVED:", StringComparison.Ordinal);
                        if (unresolvedTask)
                        {
                            if (taskId >= 0)
                                lock (_lock) job.BgftTaskId = taskId;
                            if (!TryCancelBackgroundIdentity(job, contentId, subType,
                                registrationError, true, taskId < 0, out foreignConflict))
                            {
                                SaveManifest();
                                return;
                            }
                        }
                        _loopback.MarkFailed(job.Id);
                        fallbackReason = registrationError;
                    }
                }
                else if (useLoopback)
                {
                    fallbackReason = actualKind == PkgContentKind.Patch
                        ? "Update requires installed base game"
                        : "DLC requires installed base game";
                }

                _loopback.Release(job.Id);
                if (useLoopback)
                {
                    string error = fallbackReason ?? "BGFT package registration failed; PKG retained";
                    // Another PS4 download holds this content; a local retry would meet the same conflict.
                    if (foreignConflict || !RetryLocalInstall(job, attempt, error)) MarkInstallFailed(job.Id, error, attempt);
                    return;
                }
                lock (_lock)
                {
                    if (job.AttemptId != attempt) return;
                    ClearBackground(job);
                    job.ForceLocalInstall = true;
                    if (!string.IsNullOrEmpty(fallbackReason))
                        job.StatusText = "Using local PS4 install: " + ClipMsg(fallbackReason, 42);
                }
                if (!string.IsNullOrEmpty(fallbackReason))
                    LogBgftEvent("fallback-local", job, fallbackReason);
                SaveManifest();
                InstallValidatedLocalPackage(job, attempt, actualKind, actualKindName, actualTitleId, replace, packageVersion);
            }
        }

        void RequeueValidatedLocalPackage(DlItem job, int attempt, long size)
        {
            lock (_lock)
            {
                if (CommitRequestedStop(job, attempt)) return;
                job.State = DlState.Queued;
                job.Done = job.Total = size;
                job.Error = null;
                job.StatusText = "Waiting for PS4 installer; file retained";
                job.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(3).Ticks;
                _nerd.Finalizing = false;
            }
            SaveManifest();
        }

        /// <summary>A homebrew update is the whole app again, with the same content ID as the
        /// installed copy, and the PS4 does not install over it. When the verified package is a
        /// newer version, SSPI uninstalls the older one first, as the Reinstall action does.</summary>
        internal static bool ReplacesOlderHomebrew(string titleId, PkgContentKind kind, string packageVersion)
        {
            if (kind != PkgContentKind.BaseGame || !InstalledTitleScan.IsHomebrew(titleId)) return false;
            string name, installedVersion, icon;
            InstalledTitleScan.ReadMeta(titleId, out name, out installedVersion, out icon);
            return IsNewerAppVersion(packageVersion, installedVersion);
        }

        /// <summary>The verified package's own APP_VER, so an archive's PKG or a mislabelled
        /// listing still compares correctly; the listed version only when the file cannot be read.</summary>
        internal static string HomebrewPackageVersion(string path, string listed)
        {
            try
            {
                string own = PkgIntegrity.SfoValue(PkgIntegrity.Entry(path, 0x1000), "APP_VER");
                if (own.Length > 0) return own;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            return listed ?? "";
        }

        /// <summary>APP_VER comparison ("02.32" > "02.06", "1.1.0" equals "1.1"); false when either is unreadable.</summary>
        internal static bool IsNewerAppVersion(string candidate, string installed)
        {
            Version a, b;
            return OrbisClient.TryAppVersion(candidate, out a) && OrbisClient.TryAppVersion(installed, out b) && a > b;
        }

        void InstallValidatedLocalPackage(DlItem job, int attempt, PkgContentKind actualKind,
            string actualKindName, string actualTitleId, bool replace, string packageVersion)
        {
            var watch = new LocalInstallWatch(job, attempt, actualKind == PkgContentKind.AddOn);
            try { InstallValidatedLocalPackage(job, attempt, actualKind, actualKindName, actualTitleId, replace, packageVersion, watch); }
            finally { StopLocalInstallWatch(watch); }
        }

        void InstallValidatedLocalPackage(DlItem job, int attempt, PkgContentKind actualKind,
            string actualKindName, string actualTitleId, bool replace, string packageVersion, LocalInstallWatch watch)
        {
            if (CommitRequestedStop(job, attempt)) return;
            lock (_lock)
            {
                if (job.AttemptId != attempt) return;
                job.State = DlState.Installing;
                job.InstallConfirmed = false;
                job.Done = job.Total = new FileInfo(job.DestPath).Length;
                job.StatusText = "Installing validated local PKG...";
            }
            SaveManifest();

            string installedTitleId;
            string error;
            int taskId;
            if (WaitForSelectedBackground(job, attempt, "Local package installation is waiting for the resident worker")) return;
            if (replace)
            {
                lock (_lock) { if (job.AttemptId == attempt) job.StatusText = "Replacing the installed version with " + packageVersion + "..."; }
                LogBgftEvent("homebrew-replace", job, "Installed version is older; uninstalling it before installing " + packageVersion);
            }
            StartLocalInstallWatch(watch);
            InstallOutcome outcome = PkgInstaller.InstallLocal(job.DestPath, actualTitleId,
                actualKindName, out installedTitleId, out error, out taskId, replace, watch.NoteCheck);
            if (outcome != InstallOutcome.Started && taskId >= 0)
            {
                TrackLocalInstallTask(job.Id, attempt, taskId);
                MarkInstallAccepted(job.Id, "PS4 registration is unconfirmed; task and PKG retained: " + ClipMsg(error, 110),
                    taskId, attempt);
                return;
            }
            if (outcome == InstallOutcome.QueueRequired)
            {
                QueuePreparedLocalInstall(job.Id, attempt, error);
                return;
            }
            if (outcome == InstallOutcome.Started)
            {
                if (taskId < 0)
                {
                    MarkInstallAccepted(job.Id, "Sent to PS4 — verification pending · PKG kept", -1, attempt);
                    // A license has nothing left to copy once AppInstUtil returns, so later
                    // packages need not wait while its registration is being confirmed.
                    if (actualKind == PkgContentKind.AddOn && PkgIntegrity.IsNoDataLicense(job.DestPath))
                        lock (_lock) { if (AcceptInstallCallback(job, attempt) && job.State == DlState.Submitted) job.InstallOrderReady = true; }
                    return;
                }
                TrackLocalInstallTask(job.Id, attempt, taskId);
                string checkTitleId = !string.IsNullOrEmpty(installedTitleId)
                    ? installedTitleId : actualTitleId;
                bool localCopyComplete;
                string waitError;
                bool completed = PkgInstaller.WaitForInstall(taskId, checkTitleId, actualKind,
                    percent =>
                    {
                        watch.NotePercent(percent);
                        lock (_lock)
                        {
                            if (!AcceptInstallCallback(job, attempt)) return;
                            job.State = DlState.Installing;
                            job.StatusText = percent >= 0
                                ? "Installing " + percent + "%" : "Installing...";
                            if (percent >= 0)
                            {
                                job.Done = job.Total;
                            }
                        }
                    }, out localCopyComplete, out waitError, () => TryInterruptLocalInstall(job, attempt), job.DestPath);
                lock (_lock) if (job.State == DlState.Canceled || job.State == DlState.Queued) return;
                if (completed)
                {
                    lock (_lock) { if (AcceptInstallCallback(job, attempt)) job.InstallOrderReady = localCopyComplete; }
                    bool confirmedBase = actualKind == PkgContentKind.BaseGame &&
                        localCopyComplete && !string.IsNullOrEmpty(checkTitleId) &&
                        PkgInstaller.IsBasePackageInstalled(job.DestPath, checkTitleId);
                    if (confirmedBase)
                        MarkInstalled(job.Id, "Installed " + checkTitleId, true, attempt);
                    else
                        MarkInstallAccepted(job.Id,
                            actualKind == PkgContentKind.Patch || actualKind == PkgContentKind.AddOn
                                ? "Sent to PS4 — verify update/DLC · PKG kept"
                                : "Sent to PS4 — verification pending · PKG kept",
                            taskId, attempt);
                    return;
                }
                if ((waitError ?? "").StartsWith("BGFT progress") || (waitError ?? "").IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                    MarkInstallAccepted(job.Id, "PS4 install status unavailable · task and PKG retained", taskId, attempt);
                else MarkInstallFailed(job.Id, waitError ?? "Local PKG install failed", attempt);
                return;
            }
            // AppInstUtil rejected the add-on before creating any task, so nothing is
            // installed or owned. Retry once through the BGFT route that installs updates.
            if (outcome == InstallOutcome.InstallFailed && actualKind == PkgContentKind.AddOn &&
                (error ?? "").IndexOf("ADDCONT_BROKEN", StringComparison.Ordinal) >= 0)
            {
                bool retryBgft;
                lock (_lock)
                {
                    retryBgft = job.AttemptId == attempt && !job.AddOnBgftFallback &&
                        !job.PauseRequested && !job.CancelRequested && File.Exists(job.DestPath);
                    if (retryBgft) job.AddOnBgftFallback = true;
                }
                if (retryBgft)
                {
                    LogBgftEvent("addon-bgft-fallback", job, error);
                    RequeueValidatedLocalPackage(job, attempt, new FileInfo(job.DestPath).Length);
                    return;
                }
            }
            if (CommitRequestedStop(job, attempt)) return;
            if (outcome == InstallOutcome.AlreadyInstalled)
            {
                // InstallLocal returns this for an add-on only after comparing every byte with the
                // installed copy, minutes for a large one; checking that it is still there suffices.
                if (actualKind == PkgContentKind.AddOn && PkgInstaller.IsAddonInstalled(job.DestPath, false))
                { MarkInstalled(job.Id, "Add-on installation confirmed by PS4 content", false, attempt); return; }
                MarkAlreadyInstalled(job.Id, "Already installed; local PKG kept", attempt);
                return;
            }
            if (outcome == InstallOutcome.NotReady && RetryLocalInstall(job, attempt, error)) return;
            MarkInstallFailed(job.Id, error ?? "Local PKG install failed", attempt);
        }

        /// <summary>Keeps an In-app install row current while the single queue worker waits on the
        /// PS4. The add-on installer call reports no progress and may return only after the copy,
        /// and comparing an add-on already on the PS4 with the PKG takes minutes for a large one.
        /// The watch only writes the status line and logs; it never fails, retries or deletes.</summary>
        sealed class LocalInstallWatch
        {
            internal const long LogIntervalMs = 5 * 60 * 1000;
            internal readonly DlItem Job;
            internal readonly int Attempt;
            internal readonly bool AddOn;
            internal Timer Timer;
            internal string[] InstalledPaths = new string[0];
            internal bool Stopped, Grew, Quiet;
            internal int Busy, Percent = -1;
            internal long Bytes, BeganMs, PhaseMs, ProgressMs, NextLogMs, CheckDone = -1, CheckTotal, InstalledBytes = -1;

            internal LocalInstallWatch(DlItem job, int attempt, bool addOn) { Job = job; Attempt = attempt; AddOn = addOn; }

            // Queue worker: progress of the comparison with an add-on already on the PS4,
            // then (-1, -1) when the add-on installer is called.
            internal void NoteCheck(long done, long total)
            {
                long now = TransferClockMs();
                if (total < 0) { Interlocked.Exchange(ref CheckTotal, 0); Interlocked.Exchange(ref CheckDone, -1); Interlocked.Exchange(ref PhaseMs, now); }
                else { Interlocked.Exchange(ref CheckDone, done); Interlocked.Exchange(ref CheckTotal, total); }
                Interlocked.Exchange(ref ProgressMs, now);
            }

            // Queue worker: the PS4 task reported a new percentage.
            internal void NotePercent(int percent)
            {
                Interlocked.Exchange(ref Percent, percent);
                Interlocked.Exchange(ref ProgressMs, TransferClockMs());
            }
        }

        void StartLocalInstallWatch(LocalInstallWatch watch)
        {
            try
            {
                if (watch.AddOn) watch.InstalledPaths = PkgInstaller.InstalledAddonPaths(watch.Job.DestPath);
                try { watch.Bytes = new FileInfo(watch.Job.DestPath).Length; } catch (Exception) { }
                watch.InstalledBytes = InstalledAddonBytes(watch.InstalledPaths);
                watch.BeganMs = watch.PhaseMs = watch.ProgressMs = TransferClockMs();
                watch.NextLogMs = watch.BeganMs + LocalInstallWatch.LogIntervalMs;
                LogBgftEvent("local-install-wait-start", watch.Job, DescribeLocalInstallWatch(watch, watch.BeganMs));
                lock (_lock)
                    if (!watch.Stopped && watch.Timer == null)
                        watch.Timer = new Timer(delegate { TickLocalInstallWatch(watch); }, null, 2000, 5000);
            }
            catch (Exception) { } // Status only: the install never depends on its watch.
        }

        void StopLocalInstallWatch(LocalInstallWatch watch)
        {
            Timer timer;
            lock (_lock)
            {
                if (watch.Stopped) return;
                watch.Stopped = true;
                timer = watch.Timer;
                watch.Timer = null;
            }
            if (timer == null) return;
            timer.Dispose();
            LogBgftEvent("local-install-wait-end", watch.Job, DescribeLocalInstallWatch(watch, TransferClockMs()) +
                " state=" + watch.Job.State + " status=" + ClipMsg(watch.Job.StatusText, 120));
        }

        void TickLocalInstallWatch(LocalInstallWatch watch)
        {
            if (Interlocked.Exchange(ref watch.Busy, 1) != 0) return;
            try
            {
                long now = TransferClockMs();
                bool checking = Interlocked.Read(ref watch.CheckTotal) > 0;
                long installed = InstalledAddonBytes(watch.InstalledPaths);
                if (installed != watch.InstalledBytes)
                {
                    // The add-on installer reports nothing; its installed container growing is the
                    // only sign of progress. A full-size file at once may be preallocated.
                    if (installed > 0 && installed > watch.InstalledBytes && installed < watch.Bytes) watch.Grew = true;
                    watch.InstalledBytes = installed;
                    if (!checking) Interlocked.Exchange(ref watch.ProgressMs, now);
                }
                int percent = !watch.AddOn ? Volatile.Read(ref watch.Percent) :
                    watch.Grew && installed > 0 && installed < watch.Bytes ? (int)(installed * 100 / watch.Bytes) : -1;
                long quietMs = now - Interlocked.Read(ref watch.ProgressMs);
                long limit = PkgInstallPolicy.LocalInstallQuietLimitMs(watch.Bytes, watch.Grew || (!watch.AddOn && percent >= 0));
                string text = PkgInstallPolicy.LocalInstallWaitStatus(!watch.AddOn, now - Interlocked.Read(ref watch.PhaseMs),
                    quietMs, limit, percent, Interlocked.Read(ref watch.CheckDone), Interlocked.Read(ref watch.CheckTotal));
                bool quiet = !checking && quietMs >= limit;
                lock (_lock)
                {
                    DlItem job = watch.Job;
                    if (watch.Stopped || job.AttemptId != watch.Attempt || job.State != DlState.Installing ||
                        !object.ReferenceEquals(Find(job.Id), job)) return;
                    // A requested stop keeps its own message until the installer returns.
                    if (text != null && !job.CancelRequested && !job.RemoveRequested && !job.PauseRequested) job.StatusText = text;
                }
                if (quiet != watch.Quiet || now >= watch.NextLogMs)
                {
                    watch.Quiet = quiet;
                    watch.NextLogMs = now + LocalInstallWatch.LogIntervalMs;
                    LogBgftEvent(quiet ? "local-install-quiet" : "local-install-wait", watch.Job, DescribeLocalInstallWatch(watch, now));
                }
            }
            catch (Exception) { }
            finally { Interlocked.Exchange(ref watch.Busy, 0); }
        }

        static string DescribeLocalInstallWatch(LocalInstallWatch watch, long now)
        {
            return "add_on=" + watch.AddOn + " elapsed_s=" + (now - watch.BeganMs) / 1000 +
                " phase_s=" + (now - Interlocked.Read(ref watch.PhaseMs)) / 1000 +
                " quiet_s=" + (now - Interlocked.Read(ref watch.ProgressMs)) / 1000 +
                " percent=" + Volatile.Read(ref watch.Percent) +
                " check=" + Interlocked.Read(ref watch.CheckDone) + "/" + Interlocked.Read(ref watch.CheckTotal) +
                " installed_bytes=" + watch.InstalledBytes + " bytes=" + watch.Bytes;
        }

        static long InstalledAddonBytes(string[] paths)
        {
            long size = -1;
            foreach (string path in paths)
                try { var info = new FileInfo(path); if (info.Exists) size = Math.Max(size, info.Length); }
                catch (Exception) { }
            return size;
        }

        public void TrackLocalInstallTask(string id, int attempt, int taskId)
        {
            lock (_lock)
            {
                var item = Find(id);
                if (item == null || item.AttemptId != attempt || taskId < 0) return;
                string content;
                int subtype;
                if (PkgInstaller.TryGetOwnedBackgroundIdentity(taskId, out content, out subtype))
                { item.BgftContentId = content; item.BgftSubType = subtype; }
                else
                {
                    if (PkgValidator.TryGetContentId(item.DestPath, out content)) item.BgftContentId = content;
                    item.BgftSubType = PkgValidator.BgftSubTypeForKind(item.Kind);
                }
                item.BgftTaskId = taskId;
                item.BgftExpectedSize = item.Total;
                item.InstallSubmitted = true;
                item.BgftLocalInstall = true;
                SaveManifest();
            }
        }

        public bool TryInterruptLocalInstall(string id, int attempt)
        {
            lock (_lock) return TryInterruptLocalInstall(Find(id), attempt);
        }

        bool TryInterruptLocalInstall(DlItem item, int attempt)
        {
            lock (_lock)
            {
                if (item == null || !object.ReferenceEquals(Find(item.Id), item) || item.AttemptId != attempt) return true;
                if (item.State == DlState.Canceled || (item.State == DlState.Queued && !item.BgftLocalInstall)) return true;
                bool stop = item.CancelRequested || item.RemoveRequested;
                // A late base/update may become a prerequisite after this task
                // was registered. Release the PS4 task before yielding the queue;
                // its complete local PKG remains available for the later retry.
                if (!stop && InstallDependencyReady(item)) return false;
                if (string.IsNullOrEmpty(item.BgftContentId) || item.BgftSubType <= 0) return false;
                int activeTask; string error;
                if (!CancelRowBackground(item, out activeTask, out error))
                {
                    item.BgftTaskId = activeTask;
                    item.StatusText = (stop ? "Stopping installation; " : "Yielding to required package; ") + error;
                    return false;
                }
                ClearBackground(item);
                item.InstallSubmitted = item.InstallConfirmed = item.InstallOrderReady = false;
                item.CancelRequested = false;
                item.State = stop ? DlState.Canceled : DlState.Queued;
                item.Error = null; item.BytesPerSec = 0; item.EtaSeconds = 0;
                item.StatusText = stop ? "Installation stopped; downloaded file retained" : "Waiting for required package; downloaded file retained";
                SaveManifest();
                return true;
            }
        }

        bool RetryLocalInstall(DlItem job, int attempt, string reason)
        {
            // Unresolved ownership and deterministic PS4 rejections do not change on an
            // automatic retry; registering again only repeats the conflict.
            if ((reason ?? "").IndexOf("BGFT_UNRESOLVED:", StringComparison.Ordinal) >= 0 ||
                (reason ?? "").IndexOf("BGFT_TERMINAL:", StringComparison.Ordinal) >= 0)
                return false;
            lock (_lock)
            {
                if (job.Background) return false;
                if (CommitRequestedStop(job, attempt)) return true;
                if (job.InstallRetries >= 3 || !File.Exists(job.DestPath)) return false;
                int seconds = 3 << job.InstallRetries++;
                job.Done = job.Total = new FileInfo(job.DestPath).Length;
                job.State = DlState.Queued;
                job.Error = null;
                job.StatusText = "PS4 installer settling; retry in " + seconds + "s (file retained)";
                job.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(seconds).Ticks;
                _nextInstallHandoffAt = TransferClockMs() + seconds * 1000;
                job.BytesPerSec = 0; job.EtaSeconds = 0;
                LogBgftEvent("local-install-retry", job, reason ?? "Installer not ready");
                SaveManifest();
                return true;
            }
        }

        void RetainUnrecognizedDownload(DlItem job, int attempt, string error)
        {
            string status = "Not a PKG or archive";
            try
            {
                PackageObjectKind kind = PackageArchive.Detect(job.DestPath);
                if (kind == PackageObjectKind.Pkg)
                    status = "Package verification failed; file retained";
                else if (kind == PackageObjectKind.Zip || kind == PackageObjectKind.Rar4 ||
                    kind == PackageObjectKind.Rar5 || kind == PackageObjectKind.SevenZip)
                    status = "Archive verification failed; file retained";
                else if (!string.IsNullOrEmpty(error) &&
                    (error.IndexOf("SHA-256", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     error.IndexOf("size mismatch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     error.IndexOf("content ID mismatch", StringComparison.OrdinalIgnoreCase) >= 0))
                    status = "Package verification failed; file retained";
            }
            catch { }
            lock (_lock)
            {
                if (job.AttemptId != attempt) return;
                ClearBackground(job);
                job.State = DlState.Failed;
                job.InstallConfirmed = false;
                job.Error = error ?? status;
                job.StatusText = status;
                job.BytesPerSec = 0;
                job.EtaSeconds = 0;
                job.CancelRequested = false;
                job.PauseRequested = false;
                _nerd.Finalizing = false;
                // Keep completed input for retry, missing-volume recovery, or inspection.
            }
            SaveManifest();
            User.NotifyToast("Download retained");
        }

        internal static bool MatchesInstalledUpdateIdentity(string content, string version, string title,
            string installedContent, string installedVersion, string installedTitle, string category)
        {
            Version wanted, actual;
            return category == "gp" && !string.IsNullOrEmpty(content) &&
                PkgValidator.ContentIdMatchesTitleId(content, title) &&
                string.Equals(content, installedContent, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(title, installedTitle, StringComparison.OrdinalIgnoreCase) &&
                Version.TryParse((version ?? "").TrimStart('v', 'V'), out wanted) &&
                Version.TryParse((installedVersion ?? "").TrimStart('v', 'V'), out actual) && actual >= wanted;
        }

        bool TryFindInstalledUpdate(DlItem item, out string installedPath, out string version)
        {
            installedPath = version = null;
            if (item == null || (!item.Background && item.State != DlState.Submitted) || item.State == DlState.Canceled ||
                !string.IsNullOrEmpty(item.ArchiveVolumes) || PkgValidator.BgftSubTypeForKind(item.Kind) != 8) return false;
            if (string.IsNullOrEmpty(item.PackageVersion))
            {
                try { item.PackageVersion = PkgIntegrity.SfoValue(PkgIntegrity.Entry(item.DestPath, 0x1000), "APP_VER"); }
                catch { return false; }
                if (string.IsNullOrEmpty(item.PackageVersion)) return false;
            }
            foreach (string root in new[] { "/user/patch/", "/mnt/ext0/user/patch/" })
            {
                string installed = root + item.TitleId + "/patch.pkg";
                if (!TryMatchInstalledUpdate(item, installed, out version)) continue;
                installedPath = installed; break;
            }
            return version != null;
        }

        internal static bool TryMatchInstalledUpdate(DlItem item, string installed, out string version)
        {
            version = null;
            try
            {
                // An older normal update may share both content ID and APP_VER
                // with a backport. Transfer completion cannot prove replacement.
                if (item == null || !PkgInstallPolicy.MatchesInstalledContainer(item.DestPath, installed)) return false;
                byte[] sfo = PkgIntegrity.Entry(installed, 0x1000);
                string installedContent;
                if (!PkgValidator.TryGetContentId(installed, out installedContent)) return false;
                string candidate = PkgIntegrity.SfoValue(sfo, "APP_VER");
                string content = !string.IsNullOrEmpty(item.BgftContentId) ? item.BgftContentId : item.ExpectedContentId;
                if (!MatchesInstalledUpdateIdentity(content, item.PackageVersion, item.TitleId, installedContent,
                    candidate, PkgIntegrity.SfoValue(sfo, "TITLE_ID"), PkgIntegrity.SfoValue(sfo, "CATEGORY"))) return false;
                version = candidate;
                return true;
            }
            catch { return false; }
        }

        bool ConfirmInstalledUpdate(DlItem item)
        {
            string version, installedPath;
            int attempt = item.AttemptId;
            if (!TryFindInstalledUpdate(item, out installedPath, out version))
            { item.BgftInstalledProofPolls = 0; return false; }
            // BGFT reference-package totals can omit metadata. The promoted patch
            // is stronger evidence than byte counters, feeder lifetime or an old error.
            if (++item.BgftInstalledProofPolls < 8) return false;
            string verificationError;
            if (!PkgIntegrity.ValidatePatch(installedPath, item.TitleId, "", null, null, out verificationError))
            { item.BgftInstalledProofPolls = 0; return false; }
            lock (_lock) {
                if (!object.ReferenceEquals(Find(item.Id), item) || (!item.Background && item.State != DlState.Submitted) ||
                    !AcceptInstallCallback(item, attempt)) return false;
                item.State = DlState.Installed; item.InstallConfirmed = true; item.InstallOrderReady = true;
                PreserveConfirmedDependency(item);
                _installRevision++;
                _nextInstallHandoffAt = TransferClockMs() + 3000;
                item.Error = null;
                item.StatusText = "Installed update v" + version; item.Done = item.Total; item.BytesPerSec = 0; item.EtaSeconds = 0;
                if (item.ResidentArchive) { ReleaseResidentJob(item, attempt, item.ResidentGeneration); item.ResidentArchive = false; }
                ClearBackground(item);
                SspiLog.Write("download", "event=install-confirmed title=" + item.TitleId + " kind=" + item.Kind +
                    " job=" + item.Id + " version=" + version);
            }
            SaveManifest(); return true;
        }

        internal static bool RetainUnconfirmedAddonFiles(DlItem item)
        {
            return !item.InstallConfirmed && (PkgValidator.BgftSubTypeForKind(item.Kind) == 7 || item.Kind == "theme-license") &&
                (item.ResidentAutoInstall || item.InstallSubmitted);
        }

        bool ConfirmInstalledTheme(DlItem item)
        {
            if (PkgValidator.RequestedKind(item.Kind) != PkgContentKind.SystemTheme ||
                !item.InstallSubmitted || item.ResidentArchive ||
                (!item.Background && item.State != DlState.Submitted)) return false;
            if (!PkgInstaller.IsThemeInstalled(item.DestPath, false)) { item.BgftInstalledProofPolls = 0; return false; }
            if (++item.BgftInstalledProofPolls < 3) return true;
            if (!PkgInstaller.IsThemeInstalled(item.DestPath, true)) { item.BgftInstalledProofPolls = 0; return false; }
            MarkInstalled(item.Id, "Theme installed; select it in PS4 Settings > Themes", true, item.AttemptId);
            return true;
        }

        // BGFT reads only the add-on bytes it needs, so a loopback served-range
        // receipt can stay incomplete after a successful install. The installed
        // add-on content is the stronger proof, as for updates and themes.
        bool ConfirmInstalledAddOn(DlItem item)
        {
            if (PkgValidator.RequestedKind(item.Kind) != PkgContentKind.AddOn ||
                !item.Background || !item.BgftLoopback || item.ResidentArchive) return false;
            if (!PkgInstaller.IsAddonInstalled(item.DestPath, false)) { item.BgftInstalledProofPolls = 0; return false; }
            if (++item.BgftInstalledProofPolls < 3) return true;
            if (!PkgInstaller.IsAddonInstalled(item.DestPath, true)) { item.BgftInstalledProofPolls = 0; return false; }
            MarkInstalled(item.Id, "Add-on installation confirmed by PS4 content", false, item.AttemptId);
            User.NotifyToast("Add-on installed");
            return true;
        }

        static string NormalizeSha256(string value, bool rejectInvalid)
        {
            string hash = (value ?? "").Trim().Replace("-", "").ToLowerInvariant();
            if (hash.Length == 0) return "";
            bool valid = hash.Length == 64;
            for (int i = 0; valid && i < hash.Length; i++)
            {
                char c = hash[i];
                valid = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            }
            if (!valid)
            {
                if (rejectInvalid) throw new Exception("Invalid expected SHA-256");
                return "";
            }
            return hash;
        }

        static bool VerifyCandidateFile(DlItem item, out string error)
        {
            return VerifyCandidateFile(item, text => item.StatusText = text,
                () => item.CancelRequested || item.PauseRequested, out error);
        }

        static bool VerifyCandidateFile(DlItem item, Action<string> status, Func<bool> cancel, out string error)
        {
            error = null;
            if (item == null || string.IsNullOrEmpty(item.DestPath) || !File.Exists(item.DestPath))
            {
                error = "Downloaded package is missing";
                return false;
            }
            long actualSize = new FileInfo(item.DestPath).Length;
            if (item.ExpectedByteSize > 0 && actualSize != item.ExpectedByteSize)
            {
                error = "Package size mismatch: expected " + item.ExpectedByteSize + ", got " + actualSize;
                return false;
            }
            if (string.IsNullOrWhiteSpace(item.ExpectedSha256)) return true;
            string expected = NormalizeSha256(item.ExpectedSha256, false);
            if (expected.Length == 0)
            {
                error = "Invalid persisted expected SHA-256";
                return false;
            }
            return PkgIntegrity.VerifyFile(item.DestPath, expected, status, cancel, out error);
        }
    }
}
