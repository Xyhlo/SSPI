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
        string ManifestPath { get { return Path.Combine(AppSettings.DataDir, "downloads.json"); } }

        bool SaveManifest()
        {
            if (_manifestLoadFailed) return false;
            long version = Interlocked.Increment(ref _saveVersion);
            try
            {
                var sb = new StringBuilder();
                sb.Append("{\"items\":[");
                lock (_lock)
                {
                    for (int i = 0; i < _items.Count; i++)
                    {
                        var it = _items[i];
                        if (i > 0) sb.Append(',');
                        sb.Append('{');
                        sb.Append("\"resident_archive\":").Append(it.ResidentArchive ? "true" : "false").Append(',');
                        sb.Append("\"container_format\":\"").Append(JsonLite.Escape(it.ContainerFormat)).Append("\",");
                        sb.Append("\"resident_staged\":").Append(it.ResidentStaged ? "true" : "false").Append(',');
                        sb.Append("\"resident_remove_pending\":").Append(it.ResidentRemovePending ? "true" : "false").Append(',');
                        sb.Append("\"resident_detach_requested\":").Append(it.ResidentDetachRequested ? "true" : "false").Append(',');
                        sb.Append("\"remove_requested\":").Append(it.RemoveRequested ? "true" : "false").Append(',');
                        sb.Append("\"cancel_requested\":").Append(it.CancelRequested ? "true" : "false").Append(',');
                        sb.Append("\"install_submitted\":").Append(it.InstallSubmitted ? "true" : "false").Append(',');
                        sb.Append("\"resident_link_renewals\":").Append(it.ResidentLinkRenewals).Append(',');
                        sb.Append("\"resident_last_renewal_bytes\":").Append(it.ResidentLastRenewalBytes).Append(',');
                        sb.Append("\"resident_retry_pending\":").Append(it.ResidentRetryPending ? "true" : "false").Append(',');
                        sb.Append("\"http_retries\":").Append(it.TransientHttpRetries).Append(',');
                        sb.Append("\"http_retry_durable\":").Append(it.HttpRetryLastDurableBytes).Append(',');
                        sb.Append("\"support_retries\":").Append(it.HostSupportRetries).Append(',');
                        sb.Append("\"support_retry_durable\":").Append(it.HostSupportRetryDurableBytes).Append(',');
                        sb.Append("\"install_retries\":").Append(it.InstallRetries).Append(',');
                        sb.Append("\"http_retry_at\":").Append(it.TransientHttpRetries > 0 || it.InstallRetries > 0 ||
                            it.HostSupportRetries > 0 ? it.RetryAfterUtcTicks : 0).Append(',');
                        sb.Append("\"resident_auto_install\":").Append(it.ResidentAutoInstall ? "true" : "false").Append(',');
                        sb.Append("\"resident_generation\":\"").Append(JsonLite.Escape(it.ResidentGeneration ?? "")).Append("\",");
                        sb.Append("\"resident_pause\":\"").Append(it.ResidentPauseDesired == true ? "pause" : it.ResidentPauseDesired == false ? "resume" : "").Append("\",");
                        sb.Append("\"resolved_provider_id\":\"").Append(JsonLite.Escape(it.ResolvedProviderId)).Append("\",");
                        sb.Append("\"parked_provider\":").Append(it.ParkedForProvider ? "true" : "false").Append(',');
                        sb.Append("\"park_provider_id\":\"").Append(JsonLite.Escape(it.ParkProviderId ?? "")).Append("\",");
                        sb.Append("\"park_host_url\":\"").Append(JsonLite.Escape(it.ParkHostUrl ?? "")).Append("\",");
                        sb.Append("\"park_started_urls\":\"").Append(JsonLite.Escape(it.ParkStartedUrls ?? "")).Append("\",");
                        sb.Append("\"park_retry_without_receipt\":").Append(it.ParkRetryWithoutReceipt ? "true" : "false").Append(',');
                        sb.Append("\"park_rejected_providers\":\"").Append(JsonLite.Escape(it.ParkRejectedProviderIds ?? "")).Append("\",");
                        sb.Append("\"archive_provider_state\":\"").Append(JsonLite.Escape(it.ArchiveProviderState ?? "")).Append("\",");
                        sb.Append("\"park_poll_due\":").Append(it.ParkPollDueUtcTicks).Append(',');
                        sb.Append("\"park_poll_count\":").Append(it.ParkPollCount).Append(',');
                        sb.Append("\"park_transient_failures\":").Append(it.ParkTransientFailures).Append(',');
                        sb.Append("\"park_last_state\":\"").Append(JsonLite.Escape(it.ParkLastState ?? "")).Append("\",");
                        sb.Append("\"park_started\":").Append(it.ParkStartedUtcTicks).Append(',');
                        sb.Append("\"provider_transient_retries\":").Append(it.ProviderTransientRetries).Append(',');
                        sb.Append("\"provider_host_retries\":").Append(it.ProviderHostRetries).Append(',');
                        sb.Append("\"torbox_throttle_no_progress\":").Append(it.TorBoxThrottleNoProgress).Append(',');
                        sb.Append("\"torbox_throttle_renewals\":").Append(it.TorBoxThrottleRenewalsWithoutProgress).Append(',');
                        sb.Append("\"torbox_throttle_durable\":").Append(it.TorBoxThrottleDurableBytes).Append(',');
                        sb.Append("\"torbox_throttle_renew_pending\":").Append(it.TorBoxThrottleRenewPending ? "true" : "false").Append(',');
                        sb.Append("\"install_order_ready\":").Append(it.InstallOrderReady ? "true" : "false").Append(',');
                        sb.Append("\"archive_volumes\":\"").Append(JsonLite.Escape(it.ArchiveVolumes)).Append("\",");
                        sb.Append("\"archive_password\":\"").Append(JsonLite.Escape(it.ArchivePassword)).Append("\",");
                        sb.Append("\"archive_passwords\":\"").Append(JsonLite.Escape(it.ArchivePasswords)).Append("\",");
                        sb.Append("\"local_source\":").Append(it.LocalSource ? "true" : "false").Append(',');
                        sb.Append("\"local_source_fingerprint\":\"").Append(JsonLite.Escape(it.LocalSourceFingerprint)).Append("\",");
                        sb.Append("\"bgft_local_install\":").Append(it.BgftLocalInstall ? "true" : "false").Append(",");
                        sb.Append("\"install_after_id\":\"").Append(JsonLite.Escape(it.InstallAfterId)).Append("\",");
                        sb.Append("\"install_after_confirmed\":").Append(it.InstallAfterConfirmed ? "true" : "false").Append(',');
                        sb.Append("\"id\":\"").Append(JsonLite.Escape(it.Id)).Append("\",");
                        sb.Append("\"title_id\":\"").Append(JsonLite.Escape(it.TitleId)).Append("\",");
                        sb.Append("\"name\":\"").Append(JsonLite.Escape(it.Name)).Append("\",");
                        sb.Append("\"image_url\":\"").Append(JsonLite.Escape(it.ImageUrl)).Append("\",");
                        sb.Append("\"kind\":\"").Append(JsonLite.Escape(it.Kind)).Append("\",");
                        sb.Append("\"label\":\"").Append(JsonLite.Escape(it.Label)).Append("\",");
                        sb.Append("\"url\":\"").Append(JsonLite.Escape(it.HosterUrl)).Append("\",");
                        sb.Append("\"source_id\":\"").Append(JsonLite.Escape(it.SourceId)).Append("\",");
                        sb.Append("\"source_version\":\"").Append(JsonLite.Escape(it.SourceVersion)).Append("\",");
                    sb.Append("\"package_version\":\"").Append(JsonLite.Escape(it.PackageVersion)).Append("\",");
                        sb.Append("\"candidate_id\":\"").Append(JsonLite.Escape(it.CandidateId)).Append("\",");
                        sb.Append("\"access_type\":\"").Append(JsonLite.Escape(it.AccessType)).Append("\",");
                        sb.Append("\"mirror_candidates\":\"").Append(JsonLite.Escape(it.MirrorCandidates)).Append("\",");
                        sb.Append("\"fanout_pending_paths\":\"").Append(JsonLite.Escape(it.FanOutPendingPaths)).Append("\",");
                        sb.Append("\"expected_sha256\":\"").Append(JsonLite.Escape(it.ExpectedSha256)).Append("\",");
                        sb.Append("\"expected_size\":").Append(it.ExpectedByteSize).Append(',');
                        sb.Append("\"expected_content_id\":\"").Append(JsonLite.Escape(it.ExpectedContentId)).Append("\",");
                        sb.Append("\"source_page_url\":\"").Append(JsonLite.Escape(it.SourcePageUrl)).Append("\",");
                        sb.Append("\"expires_utc\":\"").Append(JsonLite.Escape(it.ExpiresUtc)).Append("\",");
                        sb.Append("\"source_attribution\":\"").Append(JsonLite.Escape(it.SourceAttribution)).Append("\",");
                        sb.Append("\"path\":\"").Append(JsonLite.Escape(it.DestPath)).Append("\",");
                        sb.Append("\"state\":\"").Append(it.State.ToString()).Append("\",");
                        sb.Append("\"done\":").Append(it.Done).Append(',');
                        sb.Append("\"total\":").Append(it.Total).Append(',');
                        sb.Append("\"install_confirmed\":").Append(it.InstallConfirmed ? "true" : "false").Append(',');
                        sb.Append("\"background\":").Append(it.Background ? "true" : "false").Append(',');
                        sb.Append("\"bgft_loopback\":").Append(it.BgftLoopback ? "true" : "false").Append(',');
                        sb.Append("\"bgft_resident\":").Append(it.BgftResident ? "true" : "false").Append(',');
                        sb.Append("\"bgft_direct\":").Append(it.BgftDirect ? "true" : "false").Append(',');
                        sb.Append("\"bgft_loopback_served\":").Append(it.BgftLoopbackServed ? "true" : "false").Append(',');
                        sb.Append("\"base_storage_refused\":").Append(it.BaseStorageRefused ? "true" : "false").Append(',');
                        sb.Append("\"force_local_install\":").Append(it.ForceLocalInstall ? "true" : "false").Append(',');
                        sb.Append("\"bgft_task\":").Append(it.BgftTaskId).Append(',');
                        sb.Append("\"bgft_content_id\":\"").Append(JsonLite.Escape(it.BgftContentId)).Append("\",");
                        sb.Append("\"bgft_sub_type\":").Append(it.BgftSubType).Append(',');
                        sb.Append("\"bgft_expected_size\":").Append(it.BgftExpectedSize).Append(',');
                        sb.Append("\"bgft_title_known\":").Append(it.BgftTitlePresenceKnown ? "true" : "false").Append(',');
                        sb.Append("\"bgft_title_at_start\":").Append(it.BgftTitlePresentAtStart ? "true" : "false").Append(',');
                        sb.Append("\"bgft_dl_phase_ok\":").Append(it.BgftDownloadPhaseConfirmed ? "true" : "false").Append(',');
                        sb.Append("\"error\":\"").Append(JsonLite.Escape(it.Error)).Append("\",");
                        sb.Append("\"status\":\"").Append(JsonLite.Escape(it.StatusText)).Append('"');
                        sb.Append('}');
                    }
                }
                sb.Append("]}");
                lock (_saveLock)
                {
                    if (version <= _savedVersion) return true;
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        try
                        {
                            if (File.Exists(ManifestPath))
                            {
                                string previous = File.ReadAllText(ManifestPath);
                                ReadManifestObjects(previous);
                                AtomicFile.WriteText(ManifestPath + ".bak", previous);
                            }
                            AtomicFile.WriteText(ManifestPath, sb.ToString());
                            _savedVersion = version;
                            return true;
                        }
                        catch
                        {
                            if (attempt == 2) return false;
                            Thread.Sleep(20);
                        }
                    }
                }
            }
            catch { return false; }
            return false;
        }

        void RecoverBackgroundOnStartup(DlItem item, ref string storedState)
        {
            if (item.ResidentRemovePending && (item.ResidentStaged || item.ResidentArchive))
            {
                item.Background = item.BgftResident = item.ResidentArchive = true;
                item.CancelRequested = true;
                item.State = DlState.Paused; storedState = "Paused";
                item.StatusText = "Removing background job; waiting for worker acknowledgement";
                return;
            }
            if (item.CancelRequested)
            {
                item.State = DlState.Resolving; storedState = "Resolving";
                item.StatusText = "Recovering saved cancellation request";
                return;
            }
            if (item.ResidentRetryPending && (item.ResidentStaged || item.ResidentArchive))
            {
                item.Background = item.BgftResident = item.ResidentArchive = true;
                item.State = DlState.Resolving; storedState = "Resolving";
                item.StatusText = "Recovering pending download retry";
                return;
            }
            if (string.Equals(storedState, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                item.State = DlState.Failed;
                item.StatusText = "PS4 task failed · file retained · CROSS retries";
                return;
            }
            if (item.ResidentArchive)
            {
                ResidentDownloadStatus resident;
                if (!ResidentDownloadService.HasJob(item.Id) && !ResidentDownloadService.TryGetStatus(item.Id, item.ResidentGeneration, out resident))
                {
                    item.ResidentArchive = false; item.ResidentStaged = false; item.ResidentAutoInstall = false; item.Background = false; item.BgftResident = false;
                    item.State = DlState.Queued; storedState = "Queued";
                    item.StatusText = "Retrying unpublished archive handoff";
                    return;
                }
                item.Background = true; item.BgftResident = true; item.State = DlState.Downloading;
                item.StatusText = "Reattaching resident archive job"; storedState = "Downloading";
                return;
            }
            string contentId = item.BgftContentId;
            int subType = item.BgftSubType;
            long expectedSize = item.BgftExpectedSize > 0 ? item.BgftExpectedSize :
                (item.ExpectedByteSize > 0 ? item.ExpectedByteSize : item.Total);
            string installedUpdatePath, installedUpdateVersion;
            if (TryFindInstalledUpdate(item, out installedUpdatePath, out installedUpdateVersion))
            {
                item.BgftInstalledProofPolls = 1;
                item.State = DlState.Installing; storedState = "Installing";
                item.StatusText = "Verifying installed update v" + installedUpdateVersion;
                return;
            }
            bool currentTaskFound;
            int currentTask;
            string lookupError;
            if (!item.BgftResident && PkgInstaller.TryFindBackgroundTaskIdentity(contentId, subType,
                out currentTaskFound, out currentTask, out lookupError) && !currentTaskFound)
            {
                // Confirmed task absence releases stale queue ownership even when the
                // user has already removed the source PKG. Never control a recycled ID.
                if (!PkgInstaller.CancelBackground(item.BgftTaskId, contentId, subType, out currentTask, out lookupError))
                {
                    item.State = DlState.Resolving; storedState = "Resolving";
                    item.StatusText = lookupError ?? "Waiting to reconcile the previous PS4 task";
                    return;
                }
                FeederMarkFailed(item);
                ClearBackground(item);
                item.State = File.Exists(item.DestPath) ? DlState.Queued : DlState.Failed;
                item.Error = item.State == DlState.Failed ? "Previous PS4 task and source PKG are unavailable; Retry downloads again" : null;
                item.StatusText = item.State == DlState.Queued ? "Previous PS4 task ended; retained PKG is ready to retry" : item.Error;
                storedState = item.State.ToString();
                return;
            }
            bool installedProof = item.BgftTitlePresenceKnown && !item.BgftTitlePresentAtStart &&
                subType == 6 && PkgValidator.BgftSubTypeForKind(item.Kind) == 6 &&
                !string.IsNullOrEmpty(item.TitleId) &&
                PkgInstaller.IsTitleInstalled(item.TitleId);

            if (item.Background && item.BgftDirect && !item.BgftLoopback && !item.BgftResident)
            {
                BgftProgress directProgress;
                string directProgressError;
                if (PkgInstaller.TryGetBackgroundProgress(
                    item.BgftTaskId >= 0 ? item.BgftTaskId : -1, contentId, subType,
                    expectedSize, out directProgress, out directProgressError) && directProgress != null &&
                    directProgress.ErrorResult == 0)
                {
                    item.BgftLoopbackServed = true;
                    if (directProgress.TaskId >= 0) item.BgftTaskId = directProgress.TaskId;
                    item.Done = directProgress.Done;
                    if (directProgress.Total > 0)
                        item.Total = Math.Max(item.Total, directProgress.Total);
                    item.Error = null;
                    if (item.BgftDownloadPhaseConfirmed)
                    {
                        item.State = DlState.Installing;
                        storedState = "Installing";
                    }
                    else
                    {
                        item.State = DlState.Downloading;
                        storedState = "Downloading";
                    }
                    item.StatusText = "PS4 download reattached (direct)";
                    LogBgftEvent("direct-reattach", item, "task=" + item.BgftTaskId);
                    return;
                }
                if (installedProof)
                {
                    ClearBackground(item);
                    item.State = DlState.Installed;
                    item.InstallConfirmed = true;
                    CompleteByteCounters(item);
                    item.Error = null;
                    item.StatusText = "Installed via PS4 download";
                    storedState = "Installed";
                    return;
                }
                if (!TryCancelBackgroundIdentity(item, contentId, subType,
                    "direct task not found after restart"))
                {
                    storedState = "Resolving";
                    return;
                }
                ClearBackground(item);
                item.State = DlState.Queued;
                item.InstallConfirmed = false;
                item.Error = null;
                item.StatusText = "Requeued after restart";
                storedState = "Queued";
                LogBgftEvent("direct-requeue", item, "");
                return;
            }

            if (item.BgftResident)
            {
                ResidentDownloadStatus residentStatus;
                // Reattach an existing compatible owner without loading a new worker
                // during manifest recovery. The previous revision may still be busy.
                if (ResidentDownloadService.IsAlive(item.Id) &&
                    ResidentDownloadService.TryGetStatus(item.Id, out residentStatus) &&
                    residentStatus.State != "failed" && residentStatus.State != "canceled" &&
                    residentStatus.State != "idle")
                {
                    item.Background = true;
                    item.BgftLoopback = true;
                    item.BgftResident = true;
                    item.BgftLoopbackServed = residentStatus.FullyServed;
                    item.Total = expectedSize > 0 ? expectedSize : residentStatus.Total;
                    item.Done = item.Total > 0
                        ? Math.Min(residentStatus.Done, item.Total - 1) : residentStatus.Done;
                    item.Error = null;
                    BgftProgress residentProgress;
                    string progressError;
                    bool progressAttached = PkgInstaller.TryGetBackgroundProgress(
                        item.BgftTaskId >= 0 ? item.BgftTaskId : -1,
                        contentId, subType, expectedSize,
                        out residentProgress, out progressError) && residentProgress != null &&
                        residentProgress.ErrorResult == 0;
                    if (progressAttached)
                    {
                        if (residentProgress.TaskId >= 0)
                            item.BgftTaskId = residentProgress.TaskId;
                        item.Done = residentProgress.Done;
                        if (residentProgress.Total > 0)
                            item.Total = Math.Max(item.Total, residentProgress.Total);
                    }
                    else if (residentStatus.State == "complete" && installedProof)
                    {
                        ClearBackground(item);
                        item.State = DlState.Installed;
                        item.InstallConfirmed = true;
                        CompleteByteCounters(item);
                        item.StatusText = "Installed via PS4 download";
                        storedState = "Installed";
                        return;
                    }
                    item.State = item.BgftDownloadPhaseConfirmed
                        ? DlState.Installing : DlState.Downloading;
                    item.StatusText = item.BgftDownloadPhaseConfirmed
                        ? "PS4 install reattached" : "Resident PS4 download reattached";
                    storedState = item.BgftDownloadPhaseConfirmed
                        ? "Installing" : "Downloading";
                    return;
                }
                if (ResidentDownloadService.HasJob(item.Id) &&
                    (!ResidentDownloadService.TryGetStatus(item.Id, out residentStatus) ||
                    (residentStatus.State != "failed" && residentStatus.State != "canceled" &&
                     residentStatus.State != "idle")))
                {
                    // A heartbeat can be absent while the worker resumes. Keep the
                    // durable owner for the existing background polling/retry path.
                    item.Background = true;
                    item.BgftLoopback = true;
                    item.BgftResident = true;
                    item.BgftFeederMisses = 0;
                    item.State = DlState.Downloading;
                    item.StatusText = "Waiting for background downloader";
                    storedState = "Downloading";
                    return;
                }
            }

            PkgContentKind actualKind;
            string actualKindName;
            string actualContentId;
            string actualTitleId;
            long size;
            string validationError;
            if (!TryValidateLocalPackage(item, out actualKind, out actualKindName,
                out actualContentId, out actualTitleId, out size, out validationError))
            {
                bool ownershipCleared = false;
                if (!item.Background && item.BgftLoopback && item.BgftTaskId < 0)
                {
                    bool found;
                    int foundTask;
                    string findError;
                    if (PkgInstaller.TryFindBackgroundTaskIdentity(contentId, subType,
                        out found, out foundTask, out findError))
                    {
                        if (found)
                        {
                            item.Background = true;
                            item.BgftTaskId = foundTask;
                        }
                        else ownershipCleared = true;
                    }
                }
                if (!ownershipCleared && !TryCancelBackgroundIdentity(item, contentId, subType,
                    "startup package validation failed"))
                {
                    storedState = "Resolving";
                    return;
                }
                FeederMarkFailed(item);
                ClearBackground(item);
                if (TryQueueFeederPartial(item, expectedSize, ref storedState)) return;
                if (installedProof)
                {
                    item.State = DlState.Installed;
                    item.InstallConfirmed = true;
                    CompleteByteCounters(item);
                    item.Error = null;
                    item.StatusText = "Installed via PS4 download";
                    storedState = "Installed";
                }
                else
                {
                    item.State = DlState.Failed;
                    item.InstallConfirmed = false;
                    item.Error = validationError ?? "Validated local PKG is unavailable";
                    item.StatusText = File.Exists(item.DestPath)
                        ? "Local download retained; package validation failed"
                        : "Local PKG missing after restart";
                    storedState = "Failed";
                }
                return;
            }

            int actualSubType = PkgValidator.BgftSubTypeForKind(actualKindName);
            bool identityMatches = !string.IsNullOrEmpty(contentId) &&
                string.Equals(contentId, actualContentId, StringComparison.OrdinalIgnoreCase) &&
                subType == actualSubType;
            if (!identityMatches)
            {
                if (!TryCancelBackgroundIdentity(item, contentId, subType,
                    "startup package identity changed"))
                {
                    storedState = "Resolving";
                    return;
                }
                FeederMarkFailed(item);
                ClearBackground(item);
                item.Kind = actualKindName;
                if (!string.IsNullOrEmpty(actualTitleId)) item.TitleId = actualTitleId;
                item.State = DlState.Queued;
                item.InstallConfirmed = false;
                item.ForceLocalInstall = true;
                item.Done = item.Total = size;
                item.Error = null;
                item.StatusText = "Recovering validated local PKG identity";
                storedState = "Queued";
                return;
            }

            item.Kind = actualKindName;
            item.BgftContentId = actualContentId;
            item.BgftSubType = actualSubType;
            item.BgftExpectedSize = size;
            if (!string.IsNullOrEmpty(actualTitleId)) item.TitleId = actualTitleId;
            item.Total = size;

            bool restoreLoopback = item.BgftLoopback && _cfg != null && _cfg.UseBgftDirect;
            string routeUrl = null;
            string routeError = null;
            bool busy = false;
            if (restoreLoopback)
            {
                restoreLoopback = _loopback.TryRegister(item.Id, item.DestPath, size,
                    out routeUrl, out routeError, out busy) &&
                    _loopback.TryMarkComplete(item.Id, out routeError);
            }

            if (restoreLoopback)
            {
                BgftProgress progress;
                string progressError;
                if (PkgInstaller.TryGetBackgroundProgress(
                    item.BgftTaskId >= 0 ? item.BgftTaskId : -1, actualContentId,
                    item.BgftSubType, size, out progress, out progressError) &&
                    progress != null && progress.ErrorResult == 0)
                {
                    item.Background = true;
                    item.BgftLoopback = true;
                    if (progress.TaskId >= 0) item.BgftTaskId = progress.TaskId;
                    item.Done = progress.Done;
                    if (progress.Total > 0) item.Total = Math.Max(size, progress.Total);
                    item.Error = null;
                    if (item.BgftDownloadPhaseConfirmed)
                    {
                        item.State = DlState.Installing;
                        item.StatusText = "PS4 install reattached";
                        storedState = "Installing";
                    }
                    else
                    {
                        item.State = item.BgftLocalInstall ? DlState.Installing : DlState.Downloading;
                        item.StatusText = item.BgftLocalInstall ? "PS4 package import reattached" : "PS4 download reattached";
                        storedState = item.State.ToString();
                    }
                    return;
                }
                routeError = progressError ?? "BGFT task not found after restart";
            }

            if (!TryCancelBackgroundIdentity(item, actualContentId, item.BgftSubType,
                routeError ?? "startup BGFT task could not be reattached"))
            {
                storedState = "Resolving";
                return;
            }
            FeederMarkFailed(item);
            ClearBackground(item);
            if (installedProof && actualKind == PkgContentKind.BaseGame)
            {
                item.State = DlState.Installed;
                item.InstallConfirmed = true;
                CompleteByteCounters(item);
                item.Error = null;
                item.StatusText = "Installed via PS4 download";
                storedState = "Installed";
                return;
            }
            item.State = DlState.Queued;
            item.InstallConfirmed = false;
            item.Error = null;
            item.Done = item.Total = size;
            item.ForceLocalInstall = !busy;
            item.RetryAfterUtcTicks = busy ? DateTime.UtcNow.AddSeconds(2).Ticks : 0;
            item.StatusText = busy
                ? "Waiting for active PS4 system download"
                : "Recovering with validated local PKG";
            storedState = "Queued";
            if (!string.IsNullOrEmpty(routeError))
                LogBgftEvent("startup-local", item, routeError);
        }

        static bool TryQueueFeederPartial(DlItem item, long expectedSize, ref string storedState)
        {
            string partialPath = item.DestPath + ".part";
            long partialSize = 0;
            try
            {
                if (File.Exists(partialPath)) partialSize = new FileInfo(partialPath).Length;
            }
            catch { }
            if (partialSize <= 0) return false;
            DownloadResumeInfo resume = DownloadResumeInfo.Load(partialPath);
            item.State = DlState.Queued;
            item.InstallConfirmed = false;
            item.ForceLocalInstall = false;
            item.Done = partialSize;
            item.Total = resume != null && resume.Total >= partialSize
                ? resume.Total : Math.Max(expectedSize, partialSize);
            item.Error = null;
            item.RetryAfterUtcTicks = 0;
            item.StatusText = "Resuming PS4 feeder from " + Human(partialSize);
            storedState = "Queued";
            return true;
        }

        bool TryDiscardLoadedBackground(DlItem discard, DlItem survivor)
        {
            if (discard == null || (!discard.Background && !discard.BgftLoopback)) return true;
            if (survivor != null && (survivor.Background || survivor.BgftLoopback) &&
                !string.IsNullOrEmpty(discard.BgftContentId) &&
                discard.BgftSubType > 0 &&
                string.Equals(discard.BgftContentId, survivor.BgftContentId,
                    StringComparison.OrdinalIgnoreCase) &&
                discard.BgftSubType == survivor.BgftSubType)
            {
                if (discard.BgftLoopback) FeederRelease(discard);
                return true;
            }
            if (!TryCancelBackgroundIdentity(discard, discard.BgftContentId,
                discard.BgftSubType, "discarding duplicate startup row")) return false;
            if (discard.BgftLoopback)
            {
                FeederMarkFailed(discard);
                FeederRelease(discard);
            }
            ClearBackground(discard);
            return true;
        }

        internal void RestoreLocalFileForProcessing(DlItem item, string storedState, long size)
        {
            item.Done = item.Total = size;
            item.InstallConfirmed = false;
            item.BytesPerSec = 0; item.EtaSeconds = 0;
            DlState previous;
            if (!Enum.TryParse(storedState, true, out previous)) previous = DlState.Paused;
            if (item.InstallSubmitted && (previous == DlState.Submitted || previous == DlState.Installing ||
                (previous == DlState.Completed && !item.InstallOrderReady)))
            {
                // The PS4 task survives closing SSPI. Keep its identity and keep
                // checking the installed package instead of orphaning dependencies
                // or registering a second installation of the same input.
                item.State = DlState.Submitted;
                item.Background = false;
                item.InstallOrderReady = item.InstallOrderReady &&
                    (item.Kind == "theme-license" || PkgIntegrity.IsNoDataLicense(item.DestPath));
                item.Error = null;
                item.StatusText = "Checking previous PS4 installation; local PKG retained";
                return;
            }
            ClearBackground(item);
            switch (previous)
            {
                case DlState.Failed: case DlState.Canceled: case DlState.Paused:
                    item.State = previous;
                    item.StatusText = "File retained; " + previous.ToString().ToLowerInvariant();
                    break;
                case DlState.Completed: case DlState.Submitted: case DlState.Installed:
                    item.State = DlState.Completed;
                    item.StatusText = "File retained; verify before installation";
                    break;
                default:
                    item.State = DlState.Queued;
                    item.Error = null;
                    item.StatusText = "Downloaded file queued for verification and installation";
                    break;
            }
            if (string.Equals(item.AccessType, "FanOutSource", StringComparison.OrdinalIgnoreCase))
            { item.State = DlState.Completed; item.StatusText = "Downloaded source cleanup pending"; }
        }

        void LoadManifest()
        {
            bool migrated = false;
            _manifestLoadFailed = true;
            try
            {
                RecoverManifest();
                if (!File.Exists(ManifestPath)) { _manifestLoadFailed = false; return; }
                string json = File.ReadAllText(ManifestPath);
                var loadedIds = new HashSet<string>(StringComparer.Ordinal);
                var loadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var loadedIdentities = new Dictionary<string, DlItem>(StringComparer.OrdinalIgnoreCase);

                foreach (var obj in ReadManifestObjects(json))
                {
                    string storedId = JsonLite.GetString(obj, "id");
                    var it = new DlItem
                    {
                        Id = storedId ?? Guid.NewGuid().ToString("N"),
                        TitleId = JsonLite.GetString(obj, "title_id") ?? "",
                        Name = JsonLite.GetString(obj, "name") ?? "",
                        ImageUrl = JsonLite.GetString(obj, "image_url") ?? "",
                        Kind = JsonLite.GetString(obj, "kind") ?? "game",
                        Label = JsonLite.GetString(obj, "label") ?? "",
                        HosterUrl = JsonLite.GetString(obj, "url") ?? "",
                        SourceId = JsonLite.GetString(obj, "source_id") ?? "",
                        SourceVersion = JsonLite.GetString(obj, "source_version") ?? "",
                        PackageVersion = JsonLite.GetString(obj, "package_version") ?? "",
                        CandidateId = JsonLite.GetString(obj, "candidate_id") ?? "",
                        AccessType = JsonLite.GetString(obj, "access_type") ?? "",
                        MirrorCandidates = JsonLite.GetString(obj, "mirror_candidates") ?? "",
                        ArchiveVolumes = JsonLite.GetString(obj, "archive_volumes") ?? "",
                        ArchivePassword = JsonLite.GetString(obj, "archive_password") ?? "",
                        ArchivePasswords = JsonLite.GetString(obj, "archive_passwords") ?? "",
                        LocalSource = JsonLite.GetBool(obj, "local_source"),
                        LocalSourceFingerprint = JsonLite.GetString(obj, "local_source_fingerprint") ?? "",
                        InstallAfterId = JsonLite.GetString(obj, "install_after_id") ?? "",
                        InstallAfterConfirmed = JsonLite.GetBool(obj, "install_after_confirmed"),
                        BgftLocalInstall = JsonLite.GetBool(obj, "bgft_local_install"),
                        ResidentArchive = JsonLite.GetBool(obj, "resident_archive"),
                        ContainerFormat = JsonLite.GetString(obj, "container_format") ?? "",
                        ResidentStaged = JsonLite.GetBool(obj, "resident_staged"),
                        ResidentRemovePending = JsonLite.GetBool(obj, "resident_remove_pending"),
                        ResidentDetachRequested = JsonLite.GetBool(obj, "resident_detach_requested"),
                        RemoveRequested = JsonLite.GetBool(obj, "remove_requested"),
                        CancelRequested = JsonLite.GetBool(obj, "cancel_requested"),
                        InstallSubmitted = JsonLite.GetBool(obj, "install_submitted") ||
                            string.Equals(JsonLite.GetString(obj, "state"), "Submitted", StringComparison.OrdinalIgnoreCase),
                        ResidentLinkRenewals = Math.Max(0, ParseInt(JsonLite.GetString(obj, "resident_link_renewals"), 0)),
                        ResidentLastRenewalBytes = Math.Max(0, ParseLong(JsonLite.GetString(obj, "resident_last_renewal_bytes"))),
                        ResidentRetryPending = JsonLite.GetBool(obj, "resident_retry_pending"),
                        InstallRetries = Math.Max(0, Math.Min(3, ParseInt(JsonLite.GetString(obj, "install_retries"), 0))),
                        TransientHttpRetries = Math.Max(0, ParseInt(JsonLite.GetString(obj, "http_retries"), 0)),
                        HttpRetryLastDurableBytes = Math.Max(0, ParseLong(JsonLite.GetString(obj, "http_retry_durable"))),
                        HostSupportRetries = Math.Max(0, ParseInt(JsonLite.GetString(obj, "support_retries"), 0)),
                        HostSupportRetryDurableBytes = Math.Max(0, ParseLong(JsonLite.GetString(obj, "support_retry_durable"))),
                        RetryAfterUtcTicks = Math.Max(0, Math.Min(DateTime.MaxValue.Ticks,
                            ParseLong(JsonLite.GetString(obj, "http_retry_at")))),
                        ResidentAutoInstall = JsonLite.GetBool(obj, "resident_auto_install"),
                        ResidentGeneration = JsonLite.GetString(obj, "resident_generation") ?? "",
                        ResidentPauseDesired = JsonLite.GetString(obj, "resident_pause") == "pause" ? (bool?)true :
                            JsonLite.GetString(obj, "resident_pause") == "resume" ? (bool?)false : null,
                        PauseRequested = JsonLite.GetBool(obj, "background") && JsonLite.GetString(obj, "resident_pause") == "pause",
                        ResolvedProviderId = JsonLite.GetString(obj, "resolved_provider_id") ?? "",
                        ParkedForProvider = JsonLite.GetBool(obj, "parked_provider"),
                        ParkProviderId = JsonLite.GetString(obj, "park_provider_id") ?? "",
                        ParkHostUrl = JsonLite.GetString(obj, "park_host_url") ?? "",
                        ParkStartedUrls = JsonLite.GetString(obj, "park_started_urls") ?? "",
                        ParkRetryWithoutReceipt = JsonLite.GetBool(obj, "park_retry_without_receipt"),
                        ParkRejectedProviderIds = JsonLite.GetString(obj, "park_rejected_providers") ?? "",
                        ArchiveProviderState = JsonLite.GetString(obj, "archive_provider_state") ?? "",
                        ParkPollDueUtcTicks = Math.Max(0, Math.Min(DateTime.MaxValue.Ticks, ParseLong(JsonLite.GetString(obj, "park_poll_due")))),
                        ParkPollCount = Math.Max(0, ParseInt(JsonLite.GetString(obj, "park_poll_count"), 0)),
                        ParkTransientFailures = Math.Max(0, ParseInt(JsonLite.GetString(obj, "park_transient_failures"), 0)),
                        ParkLastState = JsonLite.GetString(obj, "park_last_state") ?? "",
                        ParkStartedUtcTicks = Math.Max(0, Math.Min(DateTime.MaxValue.Ticks, ParseLong(JsonLite.GetString(obj, "park_started")))),
                        ProviderTransientRetries = Math.Max(0, Math.Min(3, ParseInt(JsonLite.GetString(obj, "provider_transient_retries"), 0))),
                        ProviderHostRetries = Math.Max(0, Math.Min(3, ParseInt(JsonLite.GetString(obj, "provider_host_retries"), 0))),
                        TorBoxThrottleNoProgress = Math.Max(0, ParseInt(JsonLite.GetString(obj, "torbox_throttle_no_progress"), 0)),
                        TorBoxThrottleRenewalsWithoutProgress = Math.Max(0, ParseInt(JsonLite.GetString(obj, "torbox_throttle_renewals"), 0)),
                        TorBoxThrottleDurableBytes = Math.Max(0, ParseLong(JsonLite.GetString(obj, "torbox_throttle_durable"))),
                        TorBoxThrottleRenewPending = JsonLite.GetBool(obj, "torbox_throttle_renew_pending"),
                        InstallOrderReady = JsonLite.GetBool(obj, "install_order_ready"),
                        FanOutPendingPaths = JsonLite.GetString(obj, "fanout_pending_paths") ?? "",
                        ExpectedSha256 = JsonLite.GetString(obj, "expected_sha256") ?? "",
                        ExpectedByteSize = ParseLong(JsonLite.GetString(obj, "expected_size")),
                        ExpectedContentId = JsonLite.GetString(obj, "expected_content_id") ?? "",
                        SourcePageUrl = JsonLite.GetString(obj, "source_page_url") ?? "",
                        ExpiresUtc = JsonLite.GetString(obj, "expires_utc") ?? "",
                        SourceAttribution = JsonLite.GetString(obj, "source_attribution") ?? "",
                        DestPath = JsonLite.GetString(obj, "path") ?? "",
                        Done = ParseLong(JsonLite.GetString(obj, "done")),
                        Total = ParseLong(JsonLite.GetString(obj, "total")),
                        InstallConfirmed = JsonLite.GetBool(obj, "install_confirmed"),
                        Background = JsonLite.GetBool(obj, "background"),
                        BgftLoopback = JsonLite.GetBool(obj, "bgft_loopback"),
                        BgftResident = JsonLite.GetBool(obj, "bgft_resident"),
                        BgftDirect = JsonLite.GetBool(obj, "bgft_direct"),
                        BgftLoopbackServed = JsonLite.GetBool(obj, "bgft_loopback_served"),
                        BaseStorageRefused = JsonLite.GetBool(obj, "base_storage_refused"),
                        ForceLocalInstall = JsonLite.GetBool(obj, "force_local_install"),
                        BgftTaskId = ParseInt(JsonLite.GetString(obj, "bgft_task"), -1),
                        BgftContentId = JsonLite.GetString(obj, "bgft_content_id") ?? "",
                        BgftSubType = ParseInt(JsonLite.GetString(obj, "bgft_sub_type"), 0),
                        BgftExpectedSize = ParseLong(JsonLite.GetString(obj, "bgft_expected_size")),
                        BgftTitlePresenceKnown = JsonLite.GetBool(obj, "bgft_title_known"),
                        BgftTitlePresentAtStart = JsonLite.GetBool(obj, "bgft_title_at_start"),
                        BgftDownloadPhaseConfirmed = JsonLite.GetBool(obj, "bgft_dl_phase_ok"),
                        Error = JsonLite.GetString(obj, "error"),
                        StatusText = JsonLite.GetString(obj, "status")
                    };
                    if (string.IsNullOrEmpty(storedId)) migrated = true;
                    if (!loadedIds.Add(it.Id))
                    {
                        it.Id = Guid.NewGuid().ToString("N");
                        loadedIds.Add(it.Id);
                        migrated = true;
                    }
                    string st = JsonLite.GetString(obj, "state") ?? "Paused";
                    if (it.CancelRequested && it.BgftTaskId >= 0) it.Background = true;
                    if (it.CancelRequested && !it.Background && !it.BgftLoopback && it.BgftTaskId < 0)
                    {
                        st = "Canceled";
                        it.CancelRequested = false;
                        it.ParkedForProvider = false;
                    }
                    string identity = (it.TitleId ?? "") + "\n" + (it.Kind ?? "") + "\n" +
                        (it.HosterUrl ?? "");
                    if (it.Background || it.BgftLoopback)
                        ResetBgftTransientPolls(it);
                    string uniqueName = it.TitleId + "_" + it.Kind + "_" + UrlTag(it.HosterUrl) + ".pkg";
                    foreach (char c in Path.GetInvalidFileNameChars()) uniqueName = uniqueName.Replace(c, '_');
                    string uniquePath = Path.Combine(AppSettings.DownloadDir, uniqueName);
                    if (it.LocalSource)
                    {
                        string canonical;
                        if (!LocalInstallSource.TryNormalizePath(it.DestPath, out canonical) ||
                            !System.Text.RegularExpressions.Regex.IsMatch(it.LocalSourceFingerprint, "^[a-f0-9]{64}$"))
                        { migrated = true; continue; }
                    }
                    else if (!IsOwnedDownloadPath(it.DestPath))
                    {
                        it.DestPath = uniquePath;
                        migrated = true;
                    }
                    else if (!it.Background && !it.ResidentStaged && !it.ResidentArchive &&
                        !string.Equals(it.DestPath, uniquePath, StringComparison.OrdinalIgnoreCase))
                    {
                        bool hasLegacyFiles = File.Exists(it.DestPath) ||
                            File.Exists(it.DestPath + ".part") || File.Exists(it.DestPath + ".part.resume");
                        if (!hasLegacyFiles) it.DestPath = uniquePath;
                        migrated = true;
                    }
                    RestoreContainerFormat(it);
                    DlItem identityOwner;
                    if (loadedIdentities.TryGetValue(identity, out identityOwner))
                    {
                        if (ManifestRank(it, st) <= ManifestRank(identityOwner, identityOwner.State.ToString()))
                        {
                            if (!TryDiscardLoadedBackground(it, identityOwner))
                            {
                                _items.Add(it);
                                migrated = true;
                                continue;
                            }
                            migrated = true;
                            continue;
                        }
                        if (!TryDiscardLoadedBackground(identityOwner, it))
                        {
                            if (TryDiscardLoadedBackground(it, identityOwner))
                            {
                                migrated = true;
                                continue;
                            }
                            _items.Add(it);
                            migrated = true;
                            continue;
                        }
                        _items.Remove(identityOwner);
                        loadedPaths.Remove(NormalizePath(identityOwner.DestPath));
                    }
                    loadedIdentities[identity] = it;
                    string pathKey = NormalizePath(it.DestPath);
                    if (!loadedPaths.Add(pathKey))
                    {
                        DlItem pathOwner = null;
                        foreach (var loaded in _items)
                            if (SamePath(loaded.DestPath, it.DestPath)) { pathOwner = loaded; break; }
                        if (pathOwner == null || PathClaimRank(it, st) <=
                            PathClaimRank(pathOwner, pathOwner.State.ToString()))
                        {
                            if (!TryDiscardLoadedBackground(it, pathOwner))
                            {
                                _items.Add(it);
                                migrated = true;
                                continue;
                            }
                            loadedIdentities.Remove(identity);
                            migrated = true;
                            continue;
                        }
                        if (!TryDiscardLoadedBackground(pathOwner, it))
                        {
                            if (TryDiscardLoadedBackground(it, pathOwner))
                            {
                                loadedIdentities.Remove(identity);
                                migrated = true;
                                continue;
                            }
                            _items.Add(it);
                            migrated = true;
                            continue;
                        }
                        _items.Remove(pathOwner);
                        string ownerIdentity = (pathOwner.TitleId ?? "") + "\n" +
                            (pathOwner.Kind ?? "") + "\n" + (pathOwner.HosterUrl ?? "");
                        DlItem mappedOwner;
                        if (loadedIdentities.TryGetValue(ownerIdentity, out mappedOwner) &&
                            object.ReferenceEquals(mappedOwner, pathOwner))
                            loadedIdentities.Remove(ownerIdentity);
                    }
                    if (!it.Background && it.Total <= 100 && File.Exists(it.DestPath))
                    { it.Total = new FileInfo(it.DestPath).Length; it.Done = it.Total; migrated = true; }
                    if (it.ResidentArchive && it.InstallConfirmed && string.Equals(st, "Installed", StringComparison.OrdinalIgnoreCase))
                    {
                        it.State = DlState.Installed; it.Background = false; it.BgftResident = false;
                        it.InstallOrderReady = true; it.StatusText = "Installed (confirmed); local files retained";
                        _items.Add(it); continue;
                    }
                    if (!it.Background && it.InstallConfirmed && string.Equals(st, "Installed", StringComparison.OrdinalIgnoreCase))
                    {
                        // Completion is persisted before optional retained-file cleanup.
                        // A missing cache file does not revoke package-level install proof.
                        it.State = DlState.Installed;
                        it.InstallOrderReady = true;
                        it.Error = null;
                        ClearBackground(it);
                        if (it.Total > 0) it.Done = it.Total;
                        else CompleteByteCounters(it);
                        it.StatusText = File.Exists(it.DestPath)
                            ? "Installed (confirmed); local PKG retained" : "Installed (confirmed)";
                        _items.Add(it); continue;
                    }
                    if (it.ResidentArchive && string.Equals(st, "Submitted", StringComparison.OrdinalIgnoreCase))
                    {
                        it.State = DlState.Submitted; it.Background = false; it.BgftResident = false;
                        it.StatusText = "Archive packages sent to PS4 in order; files retained";
                        _items.Add(it); continue;
                    }
                    if (it.Background || it.BgftLoopback)
                    {
                        RecoverBackgroundOnStartup(it, ref st);
                        _items.Add(it);
                        migrated = true;
                        continue;
                    }
                    if (it.LocalSource && !File.Exists(it.DestPath))
                    {
                        DlState previous;
                        if (!Enum.TryParse(st, true, out previous)) previous = DlState.Paused;
                        // A confirmed install stays installed when its source is gone; turning it
                        // back into a queued row would reinstall it when the same file returns.
                        bool installed = previous == DlState.Installed && it.InstallConfirmed;
                        it.State = installed || previous == DlState.Canceled || previous == DlState.Paused || previous == DlState.Failed ? previous : DlState.Queued;
                        if (installed) { it.Background = false; it.BgftTaskId = -1; }
                        it.StatusText = installed ? "Installed; source file no longer present" :
                            LocalInstallSource.IsInboxPath(it.DestPath) ? "Waiting for FTP file " + Path.GetFileName(it.DestPath) :
                            "Reconnect the USB drive containing " + Path.GetFileName(it.DestPath);
                        _items.Add(it); continue;
                    }
                    if (File.Exists(it.DestPath))
                    {
                        if (string.Equals(st, "Installed", StringComparison.OrdinalIgnoreCase) &&
                            it.InstallConfirmed)
                        {
                            it.State = DlState.Installed;
                            it.Background = false;
                            it.BgftTaskId = -1;
                            it.BgftContentId = "";
                            it.BgftSubType = 0;
                            CompleteByteCounters(it);
                            it.StatusText = "Installed (confirmed); local PKG retained";
                            _items.Add(it);
                            migrated = true;
                            continue;
                        }
                        // Reopening attaches the queue. Expensive integrity checks run
                        // only after this item's serial processing slot is acquired.
                        RestoreLocalFileForProcessing(it, st, new FileInfo(it.DestPath).Length);
                        migrated = true;
                    }
                    else
                    {
                        if (!it.Background)
                        {
                            if (string.Equals(st, "Canceled", StringComparison.OrdinalIgnoreCase))
                            {
                                it.State = DlState.Canceled;
                                it.StatusText = "Canceled";
                            }
                            else if (string.Equals(st, "Installed", StringComparison.OrdinalIgnoreCase))
                            {
                                if (it.InstallConfirmed &&
                                    !PkgInstallPolicy.IsAddonOrPatchName(it.Kind))
                                {
                                    it.State = DlState.Installed;
                                    it.Done = 1;
                                    it.Total = 0;
                                    it.StatusText = string.IsNullOrEmpty(it.StatusText)
                                        ? "Installed (confirmed)" : it.StatusText;
                                }
                                else
                                {
                                    // DLC/update cannot be proven via AppExists(base). A missing
                                    // dest file means the leftover was deleted — re-download.
                                    it.State = DlState.Failed;
                                    it.Done = 0;
                                    it.Total = 0;
                                    it.InstallConfirmed = false;
                                    it.Error = PkgInstallPolicy.IsAddonOrPatchName(it.Kind)
                                        ? "DLC/update missing on disk"
                                        : "Legacy install status could not be verified";
                                    it.StatusText = "Not on disk — CROSS re-downloads";
                                    migrated = true;
                                }
                                it.Background = false;
                            }
                            else if (string.Equals(st, "Completed", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(st, "Submitted", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(st, "Installing", StringComparison.OrdinalIgnoreCase))
                            {
                                it.State = DlState.Failed;
                                it.Error = "PKG missing on disk";
                                it.StatusText = "PKG missing — re-download";
                                it.Done = 0;
                                it.Total = 0;
                            }
                            else if (string.Equals(st, "Failed", StringComparison.OrdinalIgnoreCase))
                            {
                                it.State = DlState.Failed;
                                string part = it.DestPath + ".part";
                                if (File.Exists(part)) it.Done = ParallelDownloadCheckpoint.DurableBytes(part);
                                it.StatusText = string.IsNullOrEmpty(it.StatusText)
                                    ? "Failed — CROSS retries" : it.StatusText;
                            }
                            else if (ResumeQueuedOnStartup(it, st, BackgroundSelected))
                            {
                                it.State = DlState.Queued;
                                it.StatusText = it.ParkedForProvider ? "Resuming provider preparation" : "Preparing background queue";
                                it.HttpRetryUrl = null;
                                it.HttpRetryRenewPending = false;
                            }
                            else
                            {
                                it.State = DlState.Paused;
                                string part = it.DestPath + ".part";
                                if (File.Exists(part))
                                    it.Done = ParallelDownloadCheckpoint.DurableBytes(part);
                                it.StatusText = it.Done > 0 ? ("Paused " + Human(it.Done)) : "Paused";
                            }
                        }
                    }
                    if (it.InstallSubmitted && !it.InstallConfirmed &&
                        string.Equals(it.Error, SubmittedConfirmationTimeoutError, StringComparison.Ordinal))
                    {
                        it.State = DlState.Failed;
                        it.SubmittedNeedsAttention = true;
                        it.StatusText = "Needs attention: " + SubmittedConfirmationTimeoutError;
                    }
                    _items.Add(it);
                    migrated = true;
                }
                _manifestLoadFailed = false;
                if (migrated) SaveManifest();
            }
            catch (Exception ex)
            {
                _items.Clear();
                try { PreserveCorruptManifest(); } catch { }
                _manifestLoadFailed = File.Exists(ManifestPath);
                SspiLog.Write("download", "Queue load failed; " + (_manifestLoadFailed
                    ? "saving disabled to preserve the damaged manifest: "
                    : "starting a new queue with recovery files retained: ") + ex.Message);
            }
        }

        void RecoverManifest()
        {
            if (IsManifestCandidate(ManifestPath)) return;
            bool damaged = File.Exists(ManifestPath);
            string corrupt = damaged ? PreserveCorruptManifest() : ManifestPath + ".corrupt";
            string backup = ManifestPath + ".bak";
            string tmp = ManifestPath + ".tmp";
            string recovery = IsManifestCandidate(backup) ? backup : IsManifestCandidate(tmp) ? tmp : null;
            if (recovery != null)
            {
                AtomicFile.WriteText(ManifestPath, File.ReadAllText(recovery));
                SspiLog.Write("download", "Queue restored from " + Path.GetFileName(recovery));
            }
            else if (damaged || File.Exists(backup) || File.Exists(tmp) || File.Exists(corrupt))
            {
                string retained = File.Exists(corrupt) ? corrupt : File.Exists(backup) ? backup : tmp;
                SspiLog.Write("download", "No valid queue backup; starting a new queue, damaged copy retained as " + Path.GetFileName(retained));
            }
        }

        string PreserveCorruptManifest()
        {
            if (!File.Exists(ManifestPath)) return null;
            string corrupt = ManifestPath + ".corrupt";
            if (File.Exists(corrupt)) corrupt += "." + Guid.NewGuid().ToString("N");
            File.Move(ManifestPath, corrupt);
            SspiLog.Write("download", "Damaged queue retained as " + Path.GetFileName(corrupt));
            return corrupt;
        }

        static List<string> ReadManifestObjects(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) throw new InvalidDataException("Queue manifest is empty");
            int at = 0;
            RequireManifestToken(json, ref at, "{");
            RequireManifestToken(json, ref at, "\"items\"");
            RequireManifestToken(json, ref at, ":");
            RequireManifestToken(json, ref at, "[");
            var objects = JsonLite.ExtractObjectArray(json, "items");
            for (int i = 0; i < objects.Count; i++)
            {
                if (i > 0) RequireManifestToken(json, ref at, ",");
                RequireManifestToken(json, ref at, objects[i]);
                if (!(StrictJson.Parse(objects[i]) is Dictionary<string, object>))
                    throw new InvalidDataException("Invalid queue item");
            }
            RequireManifestToken(json, ref at, "]");
            RequireManifestToken(json, ref at, "}");
            while (at < json.Length && char.IsWhiteSpace(json[at])) at++;
            if (at != json.Length) throw new InvalidDataException("Trailing queue manifest data");
            return objects;
        }

        static void RequireManifestToken(string json, ref int at, string token)
        {
            while (at < json.Length && char.IsWhiteSpace(json[at])) at++;
            if (at + token.Length > json.Length || string.CompareOrdinal(json, at, token, 0, token.Length) != 0)
                throw new InvalidDataException("Queue manifest is invalid or truncated");
            at += token.Length;
        }

        static bool IsManifestCandidate(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                ReadManifestObjects(File.ReadAllText(path));
                return true;
            }
            catch { return false; }
        }

        static void MoveIfFree(string source, string destination)
        {
            try
            {
                if (!string.IsNullOrEmpty(source) && File.Exists(source) && !File.Exists(destination))
                    File.Move(source, destination);
            }
            catch { }
        }

        static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFullPath(path); }
            catch { return path; }
        }

        static bool SamePath(string left, string right)
        {
            return string.Equals(NormalizePath(left), NormalizePath(right),
                StringComparison.OrdinalIgnoreCase);
        }

        static long ManifestRank(DlItem item, string storedState)
        {
            long rank = 0;
            if (item.InstallConfirmed && string.Equals(storedState, "Installed", StringComparison.OrdinalIgnoreCase))
                rank += 8000000000000L;
            else if (string.Equals(storedState, "Submitted", StringComparison.OrdinalIgnoreCase))
                rank += 6000000000000L;
            if (IsOwnedDownloadPath(item.DestPath))
            {
                try
                {
                    if (File.Exists(item.DestPath)) rank += 2000000000000L;
                    string part = item.DestPath + ".part";
                    if (File.Exists(part)) rank += 1000000000000L +
                        Math.Min(999999999999L, ParallelDownloadCheckpoint.DurableBytes(part));
                }
                catch { }
            }
            // Never discard ownership of a task that could not be canceled during migration.
            if (item.Background) rank += 20000000000000L;
            if (string.Equals(storedState, "Completed", StringComparison.OrdinalIgnoreCase)) rank += 1000;
            else if (string.Equals(storedState, "Paused", StringComparison.OrdinalIgnoreCase)) rank += 500;
            return rank;
        }

        static long PathClaimRank(DlItem item, string storedState)
        {
            long rank = ManifestRank(item, storedState);
            if (!IsOwnedDownloadPath(item.DestPath) || !File.Exists(item.DestPath)) return rank;
            try
            {
                PkgValResult result;
                string detail;
                if (PkgValidator.TryValidateDownload(item.DestPath, item.TitleId, item.Kind,
                    out result, out detail))
                    rank += 4000000000000L;
            }
            catch { }
            return rank;
        }

        static long ParseLong(string s)
        {
            long v;
            return long.TryParse(s, out v) ? v : 0;
        }

        static int ParseInt(string s, int fallback)
        {
            int v;
            return int.TryParse(s, out v) ? v : fallback;
        }
    }
}
