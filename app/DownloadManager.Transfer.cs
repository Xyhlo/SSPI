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
        void RunJob(DlItem job, int attempt)
        {
            try
            {
                if (CommitRequestedStop(job, attempt)) return;
                if (UseInAppThemeLicense(job))
                {
                    PkgContentKind kind; string kindName, content, title, error; long licenseSize;
                    if (!TryValidateLocalPackage(job, out kind, out kindName, out content, out title, out licenseSize, out error))
                    { SetFailureIfCurrent(job, attempt, error); return; }
                    HandleValidatedLocalPackage(job, attempt, kind, kindName, content, title, licenseSize);
                    return;
                }
                if (WaitForSelectedBackground(job, attempt)) return;
                string capacityReason;
                if (CanPrepareResidentHandoff(job, BackgroundSelected) &&
                    !ResidentDownloadService.CanAcceptStagedJob(out capacityReason))
                { WaitForSelectedBackground(job, attempt, capacityReason); return; }
                if (job.LocalSource) { RunLocalSource(job, attempt); return; }
                bool metadataOnly;
                lock (_lock) { bool residentBusy; string waitingReason; HasBlockingPipelineOwner(out residentBusy, out waitingReason); metadataOnly = BackgroundSelected && residentBusy; }
                if (!CanPrepareNativeBgft(job, BackgroundSelected))
                    AppSettings.RequireStaging(Path.GetDirectoryName(job.DestPath));
                if (metadataOnly && File.Exists(job.DestPath))
                {
                    HandoffRetainedMetadata(job, attempt);
                    return;
                }
                if (!metadataOnly)
                {
                    Func<bool> recoveryCanceled = () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested;
                    Action<string> recoveryPhase = text => { lock (_lock) { if (job.AttemptId == attempt)
                        { job.State = DlState.Finalizing; job.StatusText = text; } } };
                    string retainedSource = !string.IsNullOrEmpty(job.HttpRetryUrl) ? job.HttpRetryUrl : job.HosterUrl;
                    if (!TransferClient.TryPublishCompletedBound(job.DestPath, job.TitleId, job.Kind,
                        job.ExpectedContentId, job.ExpectedSha256, job.ExpectedByteSize, recoveryCanceled, recoveryPhase,
                        retainedSource) && !string.Equals(retainedSource, job.HosterUrl, StringComparison.Ordinal))
                        TransferClient.TryPublishCompletedBound(job.DestPath, job.TitleId, job.Kind,
                            job.ExpectedContentId, job.ExpectedSha256, job.ExpectedByteSize, recoveryCanceled, recoveryPhase,
                            job.HosterUrl);
                    if (!File.Exists(job.DestPath))
                    {
                        if (!TryPublishCompletedManaged(job.DestPath, retainedSource, job.TitleId, job.Kind,
                            job.ExpectedContentId, job.ExpectedSha256, job.ExpectedByteSize, recoveryCanceled, recoveryPhase) &&
                            !string.Equals(retainedSource, job.HosterUrl, StringComparison.Ordinal))
                            TryPublishCompletedManaged(job.DestPath, job.HosterUrl, job.TitleId, job.Kind,
                                job.ExpectedContentId, job.ExpectedSha256, job.ExpectedByteSize, recoveryCanceled, recoveryPhase);
                    }
                }
                if (File.Exists(job.DestPath) && string.IsNullOrEmpty(job.ArchiveVolumes))
                {
                    FinishDownloadedObject(job, attempt);
                    return;
                }
                string retainedArchiveError;
                if (CheckRetainedArchiveVolumes(job, out retainedArchiveError))
                {
                    if (retainedArchiveError != null)
                    {
                        RetainUnrecognizedDownload(job, attempt, retainedArchiveError);
                        SaveManifest();
                    }
                    else
                    {
                        // A source may describe one ZIP/7z/PKG as an archive set.
                        // Match DownloadArchiveVolumes' single-payload handoff.
                        PackageObjectKind retainedKind = PackageArchive.Detect(job.DestPath);
                        if (retainedKind == PackageObjectKind.Pkg || retainedKind == PackageObjectKind.Zip ||
                            retainedKind == PackageObjectKind.SevenZip)
                        {
                            lock (_lock) { if (job.AttemptId == attempt) job.ArchiveVolumes = ""; }
                            SaveManifest();
                        }
                        FinishDownloadedObject(job, attempt);
                    }
                    return;
                }
                if (Volatile.Read(ref _residentPreparationAttempted) == 1 && !ResidentDownloadService.HasStagedDownloader)
                    Interlocked.CompareExchange(ref _residentPreparationAttempted, 0, 1);
                string residentPreparationError = PrepareResidentOnce(
                    _cfg != null && _cfg.UseBgftDirect,
                    ref _residentPreparationAttempted, PrepareResidentWorker);
                if (!string.IsNullOrEmpty(residentPreparationError))
                {
                    LogBgftEvent("resident-preparation", job, residentPreparationError);
                    if (residentPreparationError.StartsWith("Downloader updated:", StringComparison.Ordinal))
                        User.NotifyToast("Background downloader updated. Finish active jobs, then restart PS4 and re-enable GoldHEN.");
                }
                if (CommitRequestedStop(job, attempt)) return;
                if (!string.IsNullOrEmpty(job.ArchiveVolumes))
                {
                    HashSet<string> unavailable = null;
                    if (!HasRetainedArchiveVolumes(job) && !HasArchiveProviderState(job) &&
                        TryParkTorBoxPreparation(job, attempt, out unavailable)) return;
                    DownloadArchiveVolumes(job, attempt, unavailable);
                    return;
                }
                if (File.Exists(job.DestPath) && PackageArchive.Detect(job.DestPath) != PackageObjectKind.Pkg)
                {
                    FinishDownloadedObject(job, attempt);
                    return;
                }
                if (File.Exists(job.DestPath))
                {
                    PkgContentKind existingKind;
                    string existingKindName;
                    string existingContentId;
                    string existingTitleId;
                    long existingSize;
                    string existingError;
                    if (TryValidateLocalPackage(job, out existingKind, out existingKindName,
                        out existingContentId, out existingTitleId, out existingSize,
                        out existingError))
                    {
                        HandleValidatedLocalPackage(job, attempt, existingKind, existingKindName,
                            existingContentId, existingTitleId, existingSize);
                        return;
                    }
                    lock (_lock)
                    {
                        if (job.AttemptId != attempt) return;
                        RetainUnrecognizedDownload(job, attempt, existingError ?? "Local validation failed; file retained");
                    }
                    SaveManifest();
                    return;
                }
                if (string.Equals(job.AccessType, "Extracted", StringComparison.OrdinalIgnoreCase))
                {
                    SetFailureIfCurrent(job, attempt, "Extracted PKG is missing; requeue the archive");
                    return;
                }
                // In-app transfers wait while resident work exists (DeferWhileResidentTransfers
                // below). Check that before asking a provider or host for a link; otherwise each
                // deferred claim, about every 3 s, resolves the link again.
                if (!BackgroundSelected && DeferWhileResidentTransfers(job, attempt)) return;
                string direct = job.HosterUrl;
                string foregroundStatus = "Downloading in SSPI...";
                if (job.AccessType == "Personal")
                {
                    string locator = CloudCatalog.ImportLocator(job.HosterUrl);
                    if (locator != null) { job.HosterUrl = locator; job.AccessType = "Cloud"; }
                }
                if (job.AccessType == "Personal")
                {
                    // Resolve routing on the worker, never while drawing the link overlay.
                    bool hoster = FreeHosterClient.IsSupportedHoster(new Uri(job.HosterUrl));
                    if (!hoster && LinkAccessType(job.HosterUrl) != "Direct")
                        foreach (string provider in UnlockProviders.EnabledIds(_cfg))
                        {
                            try { if (DebridHostSupport.Load(_cfg, provider, false).GetState(job.HosterUrl) == DebridHostState.Supported) { hoster=true; break; } }
                            catch { }
                        }
                    job.AccessType = hoster ? "HosterLanding" : "Direct";
                }
                // A source-declared Direct candidate is already a byte-stream URL. It must not
                // be sent to a Link Service or interpreted as a free-hoster landing page.
                if (job.TransientHttpRetries > 0 && (!string.IsNullOrEmpty(job.HttpRetryUrl) || job.HttpRetryRenewPending))
                {
                    if (job.HttpRetryRenewPending || (job.TorBoxThrottleRenewPending &&
                        string.Equals(job.ResolvedProviderId, UnlockProviders.TorBoxId, StringComparison.OrdinalIgnoreCase)))
                    {
                        direct = RenewJobLink(job, attempt,
                            () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested);
                        if (string.IsNullOrWhiteSpace(direct)) throw new IOException("Link service returned an empty download link");
                        job.HttpRetryUrl = direct;
                        job.HttpRetryRenewPending = false;
                        job.TorBoxThrottleRenewPending = false;
                        job.TorBoxThrottleNoProgress = 0;
                        if (job.TorBoxThrottleRenewalsWithoutProgress < int.MaxValue)
                            job.TorBoxThrottleRenewalsWithoutProgress++;
                        if (!SaveManifest()) throw new IOException("Could not save the refreshed download link state");
                    }
                    else direct = job.HttpRetryUrl;
                }
                else if (job.AccessType == "Cloud")
                {
                    direct = CloudCatalog.Resolve(_cfg, job.HosterUrl,
                        message => { lock(_lock) { if(job.AttemptId==attempt)job.StatusText=message; } },
                        () => !_run || job.AttemptId!=attempt || job.PauseRequested || job.CancelRequested);
                    foregroundStatus = "Downloading cloud file...";
                }
                else if (IsDirectAccess(job.AccessType))
                {
                    foregroundStatus = "Downloading direct...";
                    // GitHub's short-lived file links expire, so each start asks for a fresh one.
                    if (NativeHttp.IsGitHubReleaseAsset(direct))
                        direct = NativeHttp.GitHubReleaseAssetLocation(direct, 30000,
                            () => job.AttemptId != attempt || job.CancelRequested);
                }
                // Unknown (including legacy empty values) preserves the old routing behavior.
                else if (_cfg.UseUnlockProvider && UnlockProviders.EnabledIds(_cfg).Length > 0)
                {
                    lock (_lock)
                    {
                        if (job.AttemptId != attempt) return;
                        job.StatusText = "Unlocking (" + UnlockProviders.DisplayName(_cfg.UnlockProviderId) + ")...";
                    }
                    // TorBox preparation is remote and can take minutes. Park it so
                    // the worker advances another eligible game instead of holding the
                    // single transfer slot; the park poller re-queues it when ready.
                    HashSet<string> torboxUnavailable = RejectedProviders(job);
                    DebridResolutionError prepareFailure = null;
                    try { if (TryParkProviderPreparation(job, attempt, out torboxUnavailable)) return; }
                    catch (DebridResolutionError ex) {
                        if (!ex.CanTryMirror) throw;
                        torboxUnavailable = null;
                        prepareFailure = ex;
                    }
                    direct = ResolveJobHost(job, attempt, torboxUnavailable, prepareFailure);
                    foregroundStatus = "Downloading (" + UnlockProviders.DisplayName(job.ResolvedProviderId) + ")...";
                }
                else
                {
                    // Free path: resolve interstitial hoster pages (Pixeldrain/MediaFire/…)
                    // just-in-time — do not download HTML landing pages as PKGs.
                    lock (_lock)
                    {
                        if (job.AttemptId != attempt) return;
                        job.StatusText = "Resolving free hoster...";
                    }
                    try
                    {
                        direct = ResolveFreeHost(job, attempt);
                        foregroundStatus = "Downloading (free)...";
                    }
                    catch (Exception freeEx)
                    {
                        throw new Exception(freeEx.Message);
                    }
                }
                if (CommitRequestedStop(job, attempt)) return;

                // Expired queued links must not transfer as-is: unlock-provider items
                // get a fresh URL below, anything else fails fast instead of
                // downloading an error page as PKG bytes.
                DateTime expiresUtc;
                DateTime nowUtc = DateTime.UtcNow;
                // With an unset clock expiry is unknown; the server response and
                // normal link-renewal path decide whether this URL still works.
                bool linkExpired = nowUtc.Year >= 2020 && !string.IsNullOrEmpty(job.ExpiresUtc) &&
                    DateTime.TryParse(job.ExpiresUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out expiresUtc) &&
                    expiresUtc.ToUniversalTime().Year >= 2020 && expiresUtc.ToUniversalTime() < nowUtc;
                if (linkExpired && (IsDirectAccess(job.AccessType) || !CanRefreshUnlock(job)))
                    throw new Exception("Download link expired; re-resolve the package from its source");

                string part = job.DestPath + ".part";
                long existing = 0;
                if (File.Exists(part)) existing = ParallelDownloadCheckpoint.DurableBytes(part);

                if (existing == 0)
                    foregroundStatus = "Downloading in SSPI...";

                job.HttpRetryUrl = direct;
                BeginNerdSession(job, direct);

                lock (_transferRouteGate)
                {
                    if (!OtherForegroundTransfer(job))
                    {
                        if (TryRunLoopbackBgftFeeder(job, attempt, direct)) return;
                    }
                    else TraceBackgroundRoute(NetHttp.BackgroundRoute.ForegroundBusy);
                    TraceBackgroundRoute(NetHttp.BackgroundRoute.ForegroundSelected);
                    if (WaitForSelectedBackground(job, attempt, "Waiting for a compatible resident handoff; change Download mode to In-app for this file") ||
                        DeferWhileResidentTransfers(job, attempt)) return;
                    lock (_lock) job.ForegroundTransfer = true;
                }
                foregroundStatus = "Downloading · Keep SSPI open";
                if (File.Exists(job.DestPath))
                {
                    PkgContentKind afterKind;
                    string afterKindName;
                    string afterContentId;
                    string afterTitleId;
                    long afterSize;
                    string afterError;
                    if (TryValidateLocalPackage(job, out afterKind, out afterKindName,
                        out afterContentId, out afterTitleId, out afterSize, out afterError))
                    {
                        HandleValidatedLocalPackage(job, attempt, afterKind, afterKindName,
                            afterContentId, afterTitleId, afterSize);
                        return;
                    }
                    lock (_lock)
                    {
                        if (job.AttemptId != attempt) return;
                        RetainUnrecognizedDownload(job, attempt, afterError ?? "Local validation failed; file retained");
                    }
                    SaveManifest();
                    return;
                }
                existing = ParallelDownloadCheckpoint.DurableBytes(part);

                // Preflight: refuse before bulk transfer when the known remainder
                // plus extraction/install headroom cannot fit.
                {
                    long expected = Math.Max(job.ExpectedByteSize, job.Total);
                    long retained = Math.Max(existing, RetainedTransferBytes(job.DestPath));
                    RequireTransferSpace(Math.Max(0, expected - retained), LikelyArchive(job, direct));
                }

                // Mutually exclusive: foreground owns transfer — drop any stale BGFT identity first.
                string staleContent = null;
                int staleSub = 0;
                bool stopBeforeTransfer;
                lock (_lock)
                {
                    if (job.AttemptId != attempt) return;
                    stopBeforeTransfer = job.PauseRequested || job.CancelRequested;
                    if (job.Background)
                    {
                        staleContent = job.BgftContentId;
                        staleSub = job.BgftSubType;
                    }
                }
                if (stopBeforeTransfer && CommitRequestedStop(job, attempt)) return;
                if (!string.IsNullOrEmpty(staleContent) && staleSub > 0)
                {
                    if (!TryCancelBackgroundIdentity(job, staleContent, staleSub, "foreground transfer handoff"))
                    { SaveManifest(); return; }
                }
                lock (_lock)
                {
                    if (!AcceptInstallCallback(job, attempt) || job.PauseRequested || job.CancelRequested) return;
                    ClearBackground(job);
                    job.Background = false;
                    job.State = DlState.Downloading;
                    job.StatusText = foregroundStatus;
                }
                SaveManifest();

                Func<bool> cancel = () =>
                {
                    lock (_lock)
                        return job.AttemptId != attempt || job.Background ||
                            job.PauseRequested || job.CancelRequested;
                };
                var progSync = new object();
                long lastPublishedAt = 0;
                long lastPublishedBytes = -1;
                lock (_lock)
                {
                    if (job.AttemptId != attempt || job.Background || job.PauseRequested || job.CancelRequested) return;
                    job.StatsInitialized = false;
                    UpdateTransferStats(job, existing, job.Total, "foreground", TransferClockMs(), 0);
                }
                string resumeTitleId = existing > 0 &&
                    PackageArchive.Detect(part) == PackageObjectKind.Pkg ? job.TitleId : null;
                string resumePackageId = !string.IsNullOrEmpty(job.ExpectedContentId) ? job.ExpectedContentId :
                    (!string.IsNullOrEmpty(job.ExpectedSha256) ? "sha256:" + job.ExpectedSha256 : null);
                Action<long, long, long> report =
                    (done, total, received) =>
                    {
                        // Coalesce concurrent range reports instead of blocking a network reader
                        // behind another worker's EMA/UI update.
                        if (!Monitor.TryEnter(progSync)) return;
                        try
                        {
                            lock (_lock)
                            {
                                if (job.AttemptId != attempt || job.Background ||
                                    job.PauseRequested || job.CancelRequested) return;
                            }
                            long now = TransferClockMs();
                            // Publish counter rollback immediately so the shared estimator rebases.
                            if (lastPublishedBytes >= 0 && done < lastPublishedBytes)
                            {
                                lastPublishedAt = 0;
                                lastPublishedBytes = -1;
                            }

                            if (done != total && lastPublishedBytes >= 0 && now - lastPublishedAt < 200)
                                return;

                            lock (_lock)
                            {
                                if (job.AttemptId != attempt || job.Background ||
                                    job.PauseRequested || job.CancelRequested) return;
                                UpdateLiveTransferStats(job, done, total, received, "foreground", now);
                                double bps = job.BytesPerSec;
                                if (lastPublishedBytes >= 0 && done < lastPublishedBytes)
                                    _nerd.DropEvents++;
                                if (bps < 1024 && lastPublishedBytes >= 0 &&
                                    done == lastPublishedBytes)
                                    _nerd.StallEvents++;
                                bool transferComplete = total > 0 && done >= total;
                                if (transferComplete)
                                {
                                    job.State = DlState.Finalizing;
                                    job.StatusText = "Waiting to install...";
                                    job.BytesPerSec = 0;
                                    job.EtaSeconds = 0;
                                    _nerd.Finalizing = true;
                                }
                                else if (job.State != DlState.Finalizing)
                                {
                                    job.State = DlState.Downloading;
                                    job.StatusText = "Downloading · Keep SSPI open";
                                }
                                _nerd.Done = done;
                                _nerd.Total = job.Total;
                                _nerd.PushMbps((bps * 8.0) / (1000.0 * 1000.0));
                            }
                            lastPublishedAt = now;
                            lastPublishedBytes = done;
                        }
                        finally { Monitor.Exit(progSync); }
                    };
                long size = DownloadLinkRecovery.Run(direct,
                    (url, durable) =>
                    {
                        job.HttpRetryUrl = url;
                        string knownPackageTitle = durable > 0 && PackageArchive.Detect(part) == PackageObjectKind.Pkg
                            ? job.TitleId : resumeTitleId;
                        // A fresh URL can contain an archive. Only the verified PKG
                        // preflight may supply a strict title to its range transfer.
                        return NetHttp.DownloadFileResumable(url, job.DestPath, durable,
                            null, cancel, 0, null, knownPackageTitle,
                            resumePackageId, job.ExpectedSha256, report,
                            text => { lock (_lock) { if (job.AttemptId == attempt && !job.CancelRequested && !job.PauseRequested)
                                { job.State = DlState.Finalizing; job.StatusText = text; job.BytesPerSec = 0; job.EtaSeconds = 0; } } },
                            text => ApplyTransferActivity(job, attempt, text));
                    },
                    () => TransferClient.DurableBytes(job.DestPath),
                    () => CanRefreshUnlock(job),
                    () => RenewJobLink(job, attempt, cancel),
                    cancel,
                    count => { lock (_lock) { if (job.AttemptId == attempt) job.StatusText = "Requesting a fresh download link..."; } });

                lock (_lock)
                {
                    if (job.AttemptId != attempt) return;
                    job.State = DlState.Finalizing;
                    job.BytesPerSec = 0;
                    job.EtaSeconds = 0;
                    job.StatusText = string.IsNullOrEmpty(job.ExpectedSha256)
                        ? "Checking package metadata..." : "Checking verified package SHA-256...";
                    _nerd.Finalizing = true;
                }
                SaveManifest();

                if (CommitRequestedStop(job, attempt)) return;

                FinishDownloadedObject(job, attempt);
            }
            catch (OperationCanceledException)
            {
                if (!CommitRequestedStop(job, attempt))
                    SetFailureIfCurrent(job, attempt, "Transfer canceled unexpectedly");
            }
            catch (ProviderPreparationWaitException wait)
            {
                if (!ParkDeferredProvider(job, attempt, wait))
                    SetFailureIfCurrent(job, attempt, wait.Message);
            }
            catch (Exception ex)
            {
                if (CommitRequestedStop(job, attempt)) return;
                if (TryScheduleHttpRetry(job, attempt, ex)) return;
                if (TryScheduleSupportRetry(job, attempt, ex)) return;
                job.HttpRetryUrl = null;
                SetFailureIfCurrent(job, attempt, ResolutionFailureText(job, ex));
            }
        }

        /// <summary>A provider failure for a row without another mirror must not ask the
        /// player to choose one; the same diagnosis is shown with usable advice.</summary>
        static string ResolutionFailureText(DlItem job, Exception error)
        {
            var rejection = error as DebridResolutionError;
            return rejection == null ? error.Message : rejection.MessageFor(HasOtherMirror(job));
        }

        /// <summary>True when the row still lists a compatible mirror it is not using.
        /// Archive rows never record the package's other mirrors, so their advice stays.</summary>
        static bool HasOtherMirror(DlItem job)
        {
            if (job == null || !string.IsNullOrEmpty(job.ArchiveVolumes)) return true;
            foreach (var mirror in PackageMirrorFallback.Decode(job.MirrorCandidates))
                if (!string.Equals(mirror.Url, job.HosterUrl, StringComparison.Ordinal)) return true;
            return false;
        }

        // Host support that could not be read (a busy network while renewing an
        // expired link mid-download, or a provider outage) is retried with a
        // bounded backoff instead of failing the job. Durable bytes are kept.
        // The budget (6 attempts) is separate from HTTP retries and starts again
        // once the download has made progress.
        bool TryScheduleSupportRetry(DlItem job, int attempt, Exception error)
        {
            var rejection = error as DebridResolutionError;
            for (Exception inner = error; rejection == null && inner != null; inner = inner.InnerException)
                rejection = inner as DebridResolutionError;
            if (rejection == null || rejection.ProviderCode != "SUPPORT_UNAVAILABLE") return false;
            lock (_lock)
            {
                if (job.Background) return false;
                if (CommitRequestedStop(job, attempt)) return true;
                if (job.RemoveRequested) return false;
                long durable = TransferClient.DurableBytes(RetryDurablePath(job));
                if (durable > job.HostSupportRetryDurableBytes) job.HostSupportRetries = 0;
                job.HostSupportRetryDurableBytes = Math.Max(job.HostSupportRetryDurableBytes, durable);
                if (job.HostSupportRetries >= 6) return false;
                job.HostSupportRetries++;
                long seconds = Math.Min(60L, 5L << Math.Min(job.HostSupportRetries - 1, 4));
                job.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(seconds).Ticks;
                job.State = DlState.Queued;
                job.Error = null;
                job.BytesPerSec = 0;
                job.EtaSeconds = 0;
                job.StatusText = "Host support could not be checked · retry " + job.HostSupportRetries + " in " + seconds + "s";
            }
            SaveManifest();
            return true;
        }

        // The file whose durable bytes show a retry's progress: the current RAR volume
        // while an archive set downloads, otherwise the package itself.
        static string RetryDurablePath(DlItem job)
        {
            string durablePath = job.DestPath;
            if (!string.IsNullOrEmpty(job.ArchiveVolumes) && job.StatsPhase != null &&
                job.StatsPhase.StartsWith("volume:", StringComparison.Ordinal))
            {
                int volumeIndex;
                if (int.TryParse(job.StatsPhase.Substring("volume:".Length), out volumeIndex))
                {
                    List<string> paths = ArchivePaths(job);
                    if (volumeIndex >= 0 && volumeIndex < paths.Count) durablePath = paths[volumeIndex];
                }
            }
            return durablePath;
        }

        void ApplyTransferActivity(DlItem job, int attempt, string text)
        {
            lock (_lock)
            {
                if (job == null || !_items.Contains(job) || job.AttemptId != attempt ||
                    job.State != DlState.Downloading || job.Background || job.PauseRequested ||
                    job.CancelRequested || job.RemoveRequested || job.ResidentRemovePending ||
                    string.IsNullOrEmpty(text)) return;
                job.StatusText = text;
            }
        }

        bool TryScheduleHttpRetry(DlItem job, int attempt, Exception error)
        {
            lock (_lock)
            {
                if (job.AttemptId != attempt || job.Background || job.PauseRequested || job.CancelRequested)
                    return false;
                DownloadHttpException http = DownloadHttpException.Find(error);
                long retryAt;
                if (!DownloadHttpException.TryGetRetry(error, job.TransientHttpRetries,
                    DateTime.UtcNow, out retryAt)) return false;
                long durable = TransferClient.DurableBytes(RetryDurablePath(job));
                if (durable > job.HttpRetryLastDurableBytes) job.TransientHttpRetries = 0;
                job.HttpRetryLastDurableBytes = Math.Max(job.HttpRetryLastDurableBytes, durable);
                string throttledProvider = job.ResolvedProviderId;
                if (!string.IsNullOrEmpty(job.ArchiveVolumes) && job.ArchiveRetryVolume >= 0)
                {
                    var volumes = ArchiveVolumeSet.Decode(job.ArchiveVolumes);
                    if (job.ArchiveRetryVolume < volumes.Count)
                    {
                        string volumeProvider;
                        HashSet<string> ignored;
                        GetArchiveProviderState(job, volumes[job.ArchiveRetryVolume].Url, out volumeProvider, out ignored);
                        if (!string.IsNullOrEmpty(volumeProvider)) throttledProvider = volumeProvider;
                    }
                }
                if (http != null && http.StatusCode == 429 &&
                    string.Equals(throttledProvider, UnlockProviders.TorBoxId, StringComparison.OrdinalIgnoreCase))
                {
                    if (durable > job.TorBoxThrottleDurableBytes)
                    {
                        job.TorBoxThrottleNoProgress = 0;
                        job.TorBoxThrottleRenewalsWithoutProgress = 0;
                    }
                    else if (job.TorBoxThrottleNoProgress < int.MaxValue)
                        job.TorBoxThrottleNoProgress++;
                    job.TorBoxThrottleDurableBytes = Math.Max(job.TorBoxThrottleDurableBytes, durable);
                    if (job.TorBoxThrottleNoProgress >= 3 &&
                        job.TorBoxThrottleRenewalsWithoutProgress >= MaximumTorBoxThrottleRenewalsWithoutProgress)
                    {
                        job.TorBoxThrottleRenewPending = false;
                        job.StatusText = "TorBox throttled repeated refreshed links without download progress";
                        return false;
                    }
                    job.TorBoxThrottleRenewPending = job.TorBoxThrottleNoProgress >= 3;
                }
                if (job.TransientHttpRetries < int.MaxValue) job.TransientHttpRetries++;
                if (job.TransientHttpRetries < int.MaxValue &&
                    job.TransientHttpRetries % MaximumHttpFailuresPerLink == 0)
                {
                    bool canRenew = CanRefreshUnlock(job);
                    if (!string.IsNullOrEmpty(job.ArchiveVolumes))
                    {
                        var retryVolumes = ArchiveVolumeSet.Decode(job.ArchiveVolumes);
                        canRenew = job.ArchiveRetryVolume >= 0 && job.ArchiveRetryVolume < retryVolumes.Count &&
                            CanRefreshArchiveVolume(job, retryVolumes[job.ArchiveRetryVolume]);
                    }
                    if (canRenew) job.HttpRetryRenewPending = true;
                }
                job.RetryAfterUtcTicks = retryAt;
                job.State = DlState.Queued;
                job.Error = null;
                job.BytesPerSec = 0;
                job.EtaSeconds = 0;
                long seconds = Math.Max(1, (retryAt - DateTime.UtcNow.Ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond);
                job.StatusText = job.HttpRetryRenewPending ? "Server unavailable · requesting a fresh link in " + seconds + "s" :
                    "Server temporarily unavailable · recovery retry " + job.TransientHttpRetries + " in " + seconds + "s";
            }
            SaveManifest();
            return true;
        }

        static void DiscardHeaderOnlyPartial(DlItem job)
        {
            if (job == null || job.LocalSource || string.IsNullOrEmpty(job.DestPath)) return;
            try
            {
                string part = job.DestPath + ".part";
                if (File.Exists(part) && new FileInfo(part).Length <= LoopbackPkgFeeder.HeaderBytes)
                    DownloadResumeInfo.DeletePartial(part);
            }
            catch { }
        }

        bool CommitRequestedStop(DlItem job, int attempt)
        {
            bool changed = false;
            lock (_lock)
            {
                if (job.AttemptId != attempt) return true;
                if (job.CancelRequested)
                {
                    job.State = DlState.Canceled;
                    job.StatusText = "Canceled";
                    job.Error = null;
                    job.CancelRequested = false;
                    job.PauseRequested = false;
                    job.BytesPerSec = 0;
                    job.EtaSeconds = 0;
                    _nerd.Finalizing = false;
                    // Delete before publishing Canceled so a retry cannot race stale cleanup.
                    DeleteDownloadFiles(job);
                    changed = true;
                }
                else if (job.PauseRequested)
                {
                    try
                    {
                        string part = job.DestPath + ".part";
                        if (File.Exists(part)) job.Done = ParallelDownloadCheckpoint.DurableBytes(part);
                    }
                    catch { }
                    job.State = DlState.Paused;
                    job.StatusText = "Paused " + Human(job.Done);
                    job.PauseRequested = false;
                    job.BytesPerSec = 0;
                    job.EtaSeconds = 0;
                    _nerd.Finalizing = false;
                    changed = true;
                }
            }
            if (!changed) return false;
            SaveManifest();
            return true;
        }

        void SetFailureIfCurrent(DlItem job, int attempt, string message)
        {
            bool changed = false;
            lock (_lock)
            {
                if (job.AttemptId != attempt) return;
                if (!job.Background && CommitRequestedStop(job, attempt)) return;
                LogBgftEvent("job-stage-failed", job, "stage=" + job.State +
                    " completed_file=" + File.Exists(job.DestPath) + " detail=" + message);
                job.State = DlState.Failed;
                job.Error = message;
                job.StatusText = "Fail: " + Clip(message, 40);
                job.BytesPerSec = 0;
                job.EtaSeconds = 0;
                job.CancelRequested = false;
                job.PauseRequested = false;
                _nerd.Finalizing = false;
                changed = true;
            }
            if (changed)
            {
                SaveManifest();
                User.NotifyToast("Needs attention: " + Clip(job.Name ?? job.TitleId ?? "Download", 40) +
                    ". Open Downloads > Attention for the error. Files kept for retry.");
            }
        }

        static void RequireTransferSpace(long expectedNewBytes, bool mayExtract)
        {
            long required = Math.Max(0, expectedNewBytes);
            if (mayExtract)
                required = checked(required * 2 + (512L * 1024 * 1024));
            else
                required = checked(required + (256L * 1024 * 1024));
            try { ArchiveStorage.RequireFreeSpace(AppSettings.DownloadDir, required); }
            catch (IOException ex) when (ex.Message.StartsWith("Extraction needs ", StringComparison.Ordinal))
            {
                // ArchiveStorage words every shortfall as extraction. This check also covers
                // the download itself, and a plain PKG is never extracted.
                throw new IOException((mayExtract ? "Download and extraction need " : "Download needs ") +
                    Math.Ceiling(required / 1073741824.0) + " GiB free", ex);
            }
        }

        // Bytes of a native transfer (destination.part + destination.map) that already
        // occupy disk space, so a resume only asks for what is still missing. Durable
        // chunks come from the resume map. The engine sizes .part to the full package
        // when it opens it: internal storage keeps the unwritten ranges sparse, but
        // USB drives (exFAT or FAT32) cannot store sparse files, so there the whole
        // .part length is already allocated.
        internal static long RetainedTransferBytes(string destination)
        {
            return RetainedTransferBytes(destination, IsUsbStagingPath(destination));
        }

        internal static long RetainedTransferBytes(string destination, bool allocatedPart)
        {
            if (string.IsNullOrEmpty(destination)) return 0;
            long retained = TransferClient.DurableBytes(destination);
            if (allocatedPart)
                try
                {
                    var part = new FileInfo(destination + ".part");
                    if (part.Exists) retained = Math.Max(retained, part.Length);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            return retained;
        }

        internal static bool IsUsbStagingPath(string path)
        {
            return path != null && path.Replace('\\', '/').StartsWith("/mnt/usb", StringComparison.Ordinal);
        }

        static bool LikelyArchive(DlItem job, string urlOrNull)
        {
            if (job != null && !string.IsNullOrEmpty(job.ArchiveVolumes)) return true;
            string[] candidates = { urlOrNull, job != null ? job.HosterUrl : null, job != null ? job.Label : null };
            foreach (string c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                string l = c.ToLowerInvariant();
                // A name ending in .pkg is a package even when it contains ".part"
                // (for example Game.Part.2.pkg); a query string does not change that.
                int query = l.IndexOfAny(new[] { '?', '#' });
                if ((query < 0 ? l : l.Substring(0, query)).EndsWith(".pkg")) continue;
                if (l.EndsWith(".rar") || l.EndsWith(".zip") || l.EndsWith(".7z") || l.EndsWith(".7zip") || l.Contains(".part")) return true;
            }
            return false;
        }

        // Recover a managed transfer after the final write but before its rename.
        // The saved response total and source fingerprint must bind every byte;
        // a progress counter or a file length alone is never completion proof.
        internal static bool TryPublishCompletedManaged(string destination, string source, string title,
            string kind, string content, string sha, long expectedSize, Func<bool> cancel, Action<string> phase)
        {
            string part = destination + ".part";
            if (File.Exists(destination) || !File.Exists(part) || File.Exists(destination + ".map") ||
                File.Exists(destination + ".xfer-lock") || string.IsNullOrEmpty(source)) return false;
            DownloadResumeInfo resume = DownloadResumeInfo.Load(part);
            if (resume == null) return false;
            long size = new FileInfo(part).Length;
            if (size <= 0 || resume.Total != size || (expectedSize > 0 && expectedSize != size)) return false;
            string sourceKey = DownloadResumeInfo.Create(source, source, null, null, size, title).SourceKey;
            if (string.IsNullOrEmpty(sourceKey) || !string.Equals(resume.SourceKey, sourceKey, StringComparison.Ordinal)) return false;
            if (!string.IsNullOrEmpty(resume.TitleId) && !string.IsNullOrEmpty(title) &&
                !string.Equals(resume.TitleId, title, StringComparison.OrdinalIgnoreCase)) return false;
            if ((resume.PackageId ?? "").StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                string savedSha = resume.PackageId.Substring(7);
                if (savedSha.Length != 64) return false;
                foreach (char digit in savedSha) if (!Uri.IsHexDigit(digit)) return false;
                if (!string.IsNullOrEmpty(sha) && !string.Equals(savedSha, sha, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Completed file retained: expected SHA-256 identity changed");
                sha = savedSha;
            }
            string packageId = !string.IsNullOrEmpty(content) ? content :
                !string.IsNullOrEmpty(sha) ? "sha256:" + sha : "";
            if (!string.IsNullOrEmpty(resume.PackageId) &&
                !resume.PackageId.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(packageId) &&
                !string.Equals(resume.PackageId, packageId, StringComparison.OrdinalIgnoreCase)) return false;
            string ranges = ParallelDownloadCheckpoint.RangeMapPath(part);
            if (File.Exists(ranges))
            {
                long rangeTotal; int rangeCount;
                if (!ParallelDownloadCheckpoint.TryPeekRangeMap(part, out rangeTotal, out rangeCount) ||
                    rangeTotal != size || ParallelDownloadCheckpoint.DurableBytes(part) != size) return false;
            }
            else if (resume.StrictIdentity && string.IsNullOrEmpty(sha))
                return false; // A preallocated parallel file needs a complete range map or a whole-file digest.
            if (cancel != null && cancel()) throw new OperationCanceledException();
            PackageObjectKind objectKind = PackageArchive.Detect(part);
            if (objectKind != PackageObjectKind.Pkg && objectKind != PackageObjectKind.Rar4 &&
                objectKind != PackageObjectKind.Rar5 && objectKind != PackageObjectKind.Zip &&
                objectKind != PackageObjectKind.SevenZip) return false;
            if (phase != null) phase("Recovering completed file locally...");
            PkgIntegrity.VerifyTransfer(part, resume.IntegrityHeader, title, sha, cancel);
            if (objectKind == PackageObjectKind.Pkg)
            {
                PkgValResult result; string error, actualContent;
                if (!PkgValidator.TryValidateDownload(part, title, kind, out result, out error))
                    throw new IOException("Completed PKG retained: " + error);
                if (!string.IsNullOrEmpty(content) && (!PkgValidator.TryGetContentId(part, out actualContent) ||
                    !string.Equals(content, actualContent, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Completed PKG retained: content ID mismatch");
            }
            // Archive payload CRCs are checked by the extractor before any child
            // package is queued for installation. Keep the original on failure.
            if (cancel != null && cancel()) throw new OperationCanceledException();
            File.Move(part, destination);
            if (!string.IsNullOrEmpty(sha)) PkgIntegrity.RememberVerifiedSha256(destination, sha);
            DownloadResumeInfo.Delete(part);
            ParallelDownloadCheckpoint.DeleteRangeMap(part);
            return true;
        }

        void FinishDownloadedObject(DlItem job, int attempt)
        {
            if (CommitRequestedStop(job, attempt)) return;
            PackageObjectKind objectKind;
            try { objectKind = PackageArchive.Detect(job.DestPath); }
            catch (Exception ex)
            {
                SetFailureIfCurrent(job, attempt, ex.Message);
                return;
            }

            if (objectKind == PackageObjectKind.Pkg)
            {
                job.ContainerFormat = "pkg";
                PkgContentKind actualKind;
                string actualKindName;
                string contentId;
                string actualTitleId;
                long size;
                string error;
                if (TryValidateLocalPackage(job, out actualKind, out actualKindName,
                    out contentId, out actualTitleId, out size, out error))
                    HandleValidatedLocalPackage(job, attempt, actualKind, actualKindName,
                        contentId, actualTitleId, size);
                else
                    RetainUnrecognizedDownload(job, attempt, error);
                return;
            }

            if (objectKind == PackageObjectKind.Zip || objectKind == PackageObjectKind.Rar4 ||
                objectKind == PackageObjectKind.Rar5 || objectKind == PackageObjectKind.SevenZip)
            {
                if (objectKind == PackageObjectKind.Rar4 || objectKind == PackageObjectKind.Rar5)
                    ApplySourceArchivePassword(job, new byte[] { 0x52, 0x61, 0x72, 0x21 });
                job.ContainerFormat = objectKind == PackageObjectKind.Zip ? "zip" : objectKind == PackageObjectKind.SevenZip ? "7z" : "rar";
                string error;
                if (!VerifyCandidateFile(job, out error))
                {
                    RetainUnrecognizedDownload(job, attempt, error);
                    return;
                }
                if (BackgroundSelected && string.IsNullOrEmpty(job.ArchiveVolumes))
                {
                    if (WaitForSelectedBackground(job, attempt)) return;
                    string passwordError = objectKind == PackageObjectKind.SevenZip
                        ? ResidentArchivePasswordError(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c }, job.ArchivePassword)
                        : objectKind == PackageObjectKind.Zip
                        ? ResidentArchivePasswordError(new byte[] { 0x50, 0x4b, 3, 4 }, job.ArchivePassword)
                        : ResidentArchivePasswordError(new byte[] { 0x52, 0x61, 0x72, 0x21 }, job.ArchivePassword);
                    if (passwordError != null) { SetFailureIfCurrent(job, attempt, passwordError); return; }
                    string source = !string.IsNullOrEmpty(job.HttpRetryUrl) ? job.HttpRetryUrl : job.HosterUrl;
                    Uri uri;
                    if (!Uri.TryCreate(source, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                    { WaitForSelectedBackground(job, attempt, "Retained archive needs its original source metadata for resident extraction"); return; }
                    HandoffStagedPackage(job, attempt, source, job.TitleId, job.Kind, "", new FileInfo(job.DestPath).Length, true);
                    return;
                }
                if (objectKind == PackageObjectKind.Rar4 || objectKind == PackageObjectKind.Rar5)
                {
                    var volumes = string.IsNullOrEmpty(job.ArchiveVolumes)
                        ? new List<ArchiveVolume> { new ArchiveVolume { Url = job.HosterUrl,
                            Name = ArchiveVolumeSet.FileName(job.HosterUrl, job.Label),
                            AccessType = job.AccessType, Sha256 = job.ExpectedSha256,
                            Size = new FileInfo(job.DestPath).Length } }
                        : ArchiveVolumeSet.Decode(job.ArchiveVolumes);
                    if (volumes.Count == 1 && string.IsNullOrEmpty(volumes[0].Name)) volumes[0].Name = "archive.rar";
                    if (TryHandoffArchive(job, attempt, volumes, ArchivePaths(job))) return;
                }
                FanOutArchive(job, attempt);
                return;
            }

            FanOutLinks(job, attempt);
        }
    }
}
