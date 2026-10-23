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
        bool TryParkProviderPreparation(DlItem job, int attempt, out HashSet<string> unavailableProviders)
        {
            if (!ForgetStaleRejections(job, out unavailableProviders)) return false;
            DebridHostSupport.RefreshEnabled(_cfg);
            if (!AllDebridIsFirstProvider(_cfg, job.HosterUrl))
                return TryParkTorBoxPreparation(job, attempt, out unavailableProviders);
            AllDebridClient.PreparedPollResult poll;
            try
            {
                poll = AllDebridClient.TryParkOrPrepare(_cfg.AllDebridApiKey, job.HosterUrl,
                    () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested);
            }
            catch (DebridResolutionError rejection)
            {
                if (rejection.CanTryProvider && UnlockProviders.HasSupportedAlternative(_cfg, new[] { job.HosterUrl }, UnlockProviders.AllDebridId))
                {
                    AddRejectedProvider(job, UnlockProviders.AllDebridId);
                    unavailableProviders.Add(UnlockProviders.AllDebridId);
                    if (!SaveManifest()) throw new IOException("Could not save provider rejection state");
                    return false;
                }
                throw;
            }
            if (CommitRequestedStop(job, attempt)) return true;
            if (poll.Kind == AllDebridClient.PreparedPollKind.Ready)
            {
                ApplyContainerFormatHint(job, null, null, null, poll.Download);
                return false;
            }
            if (poll.Kind == AllDebridClient.PreparedPollKind.Rejected || poll.Kind == AllDebridClient.PreparedPollKind.Terminal)
            {
                var rejection = AllDebridParkFailure(poll, job.HosterUrl);
                if (rejection.CanTryProvider && UnlockProviders.HasSupportedAlternative(_cfg, new[] { job.HosterUrl }, UnlockProviders.AllDebridId))
                {
                    AddRejectedProvider(job, UnlockProviders.AllDebridId);
                    unavailableProviders.Add(UnlockProviders.AllDebridId);
                    if (!SaveManifest()) throw new IOException("Could not save provider rejection state");
                    return false;
                }
                throw rejection;
            }
            long nowTicks = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (CommitRequestedStop(job, attempt)) return true;
                job.State = DlState.Queued;job.ParkedForProvider = true;job.ParkProviderId = UnlockProviders.AllDebridId;
                job.ResolvedProviderId = UnlockProviders.AllDebridId;
                job.ParkHostUrl = job.HosterUrl;
                job.ParkRetryWithoutReceipt = !AllDebridClient.HasRemoteOperation(_cfg.AllDebridApiKey, job.HosterUrl);
                job.ParkStartedUrls = EncodeParkStartedUrls(PreparedUrlsForPark(job, new[] { job.HosterUrl }, UnlockProviders.AllDebridId));
                job.ParkStartedUtcTicks = nowTicks;job.ParkPollCount = 0;job.ParkTransientFailures = 0;
                job.ParkLastState = poll.ProviderState ?? "";
                long delay = Math.Max(AllDebridClient.PollDelayMilliseconds, (long)poll.RetryAfterSeconds * 1000L);
                job.ParkPollDueUtcTicks = nowTicks + TimeSpan.FromMilliseconds(delay).Ticks;
                job.BytesPerSec = 0;job.EtaSeconds = 0;
                job.StatusText = "Preparing in AllDebrid · next check in 5s · other downloads continue";
                _nextJobStartAt = TransferClockMs();
            }
            if (!SaveManifest()) throw new IOException("Could not save AllDebrid queue state; its durable receipt was retained");
            EnsureParkPollTimer();return true;
        }

        static bool AllDebridIsFirstProvider(AppSettings cfg, string hosterUrl)
        {
            if (cfg == null || !cfg.UseUnlockProvider || !cfg.HasAllDebrid || string.IsNullOrEmpty(hosterUrl) ||
                !UnlockProviders.IsEnabled(cfg, UnlockProviders.AllDebridId)) return false;
            string[] ranked = UnlockProviders.RankedProviderIds(cfg, hosterUrl);
            return ranked.Length > 0 && string.Equals(ranked[0], UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// TorBox preparation is a remote operation that can take minutes. It is
        /// parked instead of holding the only transfer slot: the row returns to
        /// Queued with the provider's own metric, and the park poller re-queues it
        /// once the provider publishes the file. Returns true when the worker must
        /// move on to the next eligible game.
        /// </summary>
        bool TryParkTorBoxPreparation(DlItem job, int attempt, out HashSet<string> unavailableProviders)
        {
            unavailableProviders = RejectedProviders(job);
            if (unavailableProviders.Count > 0) return false;
            DebridHostSupport.RefreshEnabled(_cfg);
            var urls = TorBoxPreparationUrls(job);
            if (urls.Count == 0 || urls.Exists(url => !TorBoxIsFirstProvider(_cfg, url))) return false;
            // Prefer a mirror TorBox already holds; only a first-sight link is checked.
            bool cachedAtTorBox = string.IsNullOrEmpty(job.ArchiveVolumes) &&
                !TorBoxClient.HasPreparedDownload(_cfg.TorBoxApiKey, job.HosterUrl) &&
                PreferCachedTorBoxMirror(job, attempt);
            if (cachedAtTorBox) urls = TorBoxPreparationUrls(job);
            // A provider job is created at most once: TryParkOrPrepare reuses the
            // durable pending record and only polls when one already exists.
            TorBoxClient.PreparedPollResult poll = null;
            bool retryServerFault = false;
            bool serverFaultRetriesExhausted = false;
            bool createThrottled = false;
            int serverFaultFailures = 0;
            try
            {
                Func<bool> canceled = () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested || job.RemoveRequested;
                Action<string> progress = text => { lock (_lock) { if (!canceled()) job.StatusText = text; } };
                poll = string.IsNullOrEmpty(job.ArchiveVolumes)
                    ? TorBoxClient.TryParkOrPrepare(_cfg.TorBoxApiKey, job.HosterUrl, canceled, progress, cachedAtTorBox)
                    : TorBoxClient.PrepareMany(_cfg.TorBoxApiKey, urls, 0, canceled, progress);
            }
            catch (DebridResolutionError rejection)
            {
                if (rejection.Provider == "TorBox" && rejection.ProviderCode == "DOWNLOAD_SERVER_ERROR" && rejection.IsTransient)
                {
                    serverFaultFailures = Math.Max(0, job.ParkTransientFailures) + 1;
                    // TorBox refused to create the cloud download, so no download link exists yet.
                    SspiLog.Write("download", "event=torbox-create-server-error job=" + (job.Id ?? "") +
                        " failure=" + serverFaultFailures + " max_retries=3 http=" + rejection.HttpStatusCode +
                        " retry_after=" + rejection.RetryAfterSeconds + " cached=" + (cachedAtTorBox ? 1 : 0));
                    if (serverFaultFailures <= 3)
                    {
                        // TorBox listed no download for this link after the fault, so none was
                        // kept. The retry checks the account again before sending the link; a
                        // wait TorBox asked for applies to that create and is shown then.
                        retryServerFault = true;
                        poll = new TorBoxClient.PreparedPollResult
                        {
                            Kind = TorBoxClient.PreparedPollKind.Transient,
                            Transport = rejection,
                            Error = rejection.Message
                        };
                    }
                    else
                    {
                        serverFaultRetriesExhausted = true;
                        // The automatic retries are spent. A later Retry from Downloads gets
                        // its own three instead of failing again on the first refusal.
                        lock (_lock) if (job.AttemptId == attempt) job.ParkTransientFailures = 0;
                    }
                }
                if (poll == null)
                {
                    // A definite TorBox rejection keeps the previous multi-provider
                    // fallback: resolve again with TorBox excluded rather than failing
                    // the job. Without another supported provider the rejection is
                    // reported unchanged.
                    List<string> rejectedUrls = string.IsNullOrEmpty(job.ArchiveVolumes)
                        ? new List<string> { job.HosterUrl }
                        : urls.FindAll(url => string.Equals(DebridResolutionError.HostName(url), rejection.Host, StringComparison.OrdinalIgnoreCase));
                    if ((rejection.CanTryProvider || serverFaultRetriesExhausted) && rejectedUrls.Count > 0 &&
                        (string.IsNullOrEmpty(job.ArchiveVolumes)
                            ? UnlockProviders.HasSupportedAlternative(_cfg, rejectedUrls, UnlockProviders.TorBoxId)
                            : rejectedUrls.TrueForAll(url => UnlockProviders.HasSupportedAlternative(_cfg, new[] { url }, UnlockProviders.TorBoxId))))
                    {
                        if (string.IsNullOrEmpty(job.ArchiveVolumes)) AddRejectedProvider(job, UnlockProviders.TorBoxId);
                        else foreach (string url in rejectedUrls) SetArchiveProviderState(job, url, "", UnlockProviders.TorBoxId, true);
                        if (!SaveManifest()) throw new IOException("Could not save provider rejection state");
                        if (string.IsNullOrEmpty(job.ArchiveVolumes)) unavailableProviders.Add(UnlockProviders.TorBoxId);
                        else unavailableProviders = null;
                        return false;
                    }
                    // TorBox, or SSPI keeping to TorBox's hourly create limit, asked for a wait
                    // before the next new download. Nothing was created: wait and retry by
                    // itself, at most an hour, instead of failing the row.
                    if (!TorBoxClient.IsCreateThrottle(rejection)) throw;
                    createThrottled = true;
                    poll = new TorBoxClient.PreparedPollResult
                    {
                        Kind = TorBoxClient.PreparedPollKind.Transient,
                        Transport = rejection,
                        Error = rejection.Message,
                        RetryAfterSeconds = Math.Max(5, Math.Min(3600, rejection.RetryAfterSeconds))
                    };
                }
            }
            lock (_lock)
            {
                if (CommitRequestedStop(job, attempt)) return true;
            }
            if (poll.Kind == TorBoxClient.PreparedPollKind.Ready)
            {
                lock (_lock) if (job.AttemptId == attempt)
                {
                    job.ParkTransientFailures = 0;
                    ApplyContainerFormatHint(job, null, poll.FileName, null, null);
                }
                return false;
            }
            if (poll.Kind == TorBoxClient.PreparedPollKind.Rejected || poll.Kind == TorBoxClient.PreparedPollKind.Terminal)
            {
                var rejection = TorBoxClient.PreparationFailure(poll.FailedUrl ?? job.HosterUrl, poll);
                string rejectedUrl = poll.FailedUrl ?? job.HosterUrl;
                if (rejection.CanTryProvider && UnlockProviders.HasSupportedAlternative(_cfg, new[] { rejectedUrl }, UnlockProviders.TorBoxId))
                {
                    if (string.IsNullOrEmpty(job.ArchiveVolumes)) AddRejectedProvider(job, UnlockProviders.TorBoxId);
                    else SetArchiveProviderState(job, rejectedUrl, "", UnlockProviders.TorBoxId, true);
                    if (!SaveManifest()) throw new IOException("Could not save provider rejection state");
                    if (string.IsNullOrEmpty(job.ArchiveVolumes)) unavailableProviders.Add(UnlockProviders.TorBoxId);
                    else unavailableProviders = null;
                    return false;
                }
                throw rejection;
            }
            long nowTicks = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (CommitRequestedStop(job, attempt)) return true;
                job.State = DlState.Queued;
                job.ParkedForProvider = true;
                job.ParkProviderId = UnlockProviders.TorBoxId;
                job.ResolvedProviderId = UnlockProviders.TorBoxId;
                job.ParkHostUrl = string.IsNullOrEmpty(job.ArchiveVolumes) ? job.HosterUrl : "";
                job.ParkRetryWithoutReceipt = retryServerFault || createThrottled || (string.IsNullOrEmpty(job.ArchiveVolumes)
                    ? !TorBoxClient.HasPreparedDownload(_cfg.TorBoxApiKey, job.HosterUrl)
                    : !TorBoxPreparationUrls(job).Exists(url => TorBoxClient.HasPreparedDownload(_cfg.TorBoxApiKey, url)));
                job.ParkStartedUrls = EncodeParkStartedUrls(PreparedUrlsForPark(job, urls, UnlockProviders.TorBoxId));
                job.ParkStartedUtcTicks = nowTicks;
                job.ParkPollCount = 1;
                // A wait for TorBox's create limit is not a server fault: its count is kept.
                job.ParkTransientFailures = retryServerFault ? serverFaultFailures : createThrottled ? job.ParkTransientFailures :
                    poll.Kind == TorBoxClient.PreparedPollKind.Transient ? 1 : 0;
                job.ParkLastState = poll.ProviderState ?? "";
                long retryDelayMs = retryServerFault
                    ? Math.Min(60000L, 5000L << Math.Max(0, serverFaultFailures - 1))
                    : QueueScheduler.NextPollDelayMs(0);
                retryDelayMs = Math.Max(retryDelayMs, (long)poll.RetryAfterSeconds * 1000);
                job.ParkPollDueUtcTicks = nowTicks + TimeSpan.FromMilliseconds(retryDelayMs).Ticks;
                job.BytesPerSec = 0;
                job.EtaSeconds = 0;
                // The player has nothing to fix here: say that SSPI sends the request
                // again by itself, when, and which TorBox code refused it.
                long retrySeconds = Math.Max(1, (retryDelayMs + 999) / 1000);
                string retryIn = retrySeconds < 120 ? retrySeconds + "s" : (retrySeconds + 59) / 60 + " min";
                job.StatusText = retryServerFault
                    ? "TorBox server error · retries automatically in " + retryIn +
                        " (" + serverFaultFailures + "/3) · DOWNLOAD_SERVER_ERROR"
                    : createThrottled
                    ? TorBoxCreateWaitText(poll.Transport) + " · retries automatically in " + retryIn + " · other downloads continue"
                    : ParkedPreparationStatus(poll);
                // A park must free the queue immediately: the post-claim cooldown
                // exists to pace transfers, not to delay an unrelated game.
                _nextJobStartAt = TransferClockMs();
            }
            if (!SaveManifest()) throw new IOException("Could not save TorBox queue state; its durable receipt was retained");
            // The worker is busy inside another job while a download runs, so the
            // park poller owns its own one second cadence. Due times and the
            // single-flight latch bound it; no polling happens when nothing parks.
            EnsureParkPollTimer();
            return true;
        }

        static List<string> TorBoxPreparationUrls(DlItem job)
        {
            var urls = new List<string>();
            if (string.IsNullOrEmpty(job.ArchiveVolumes)) urls.Add(job.HosterUrl);
            else foreach (var volume in ArchiveVolumeSet.Decode(job.ArchiveVolumes))
                if (!IsDirectAccess(volume.AccessType) && !urls.Contains(volume.Url)) urls.Add(volume.Url);
            return urls;
        }

        static string ArchiveSourceKey(string url)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url ?? ""))).Replace("-", "");
        }

        static void GetArchiveProviderState(DlItem job, string url, out string provider, out HashSet<string> rejected)
        {
            provider = "";
            rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (job == null || string.IsNullOrEmpty(url)) return;
            string key = ArchiveSourceKey(url);
            foreach (string row in (job.ArchiveProviderState ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = row.Split('|');
                if (fields.Length != 3 || !string.Equals(fields[0], key, StringComparison.OrdinalIgnoreCase)) continue;
                provider = fields[1];
                foreach (string id in fields[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    rejected.Add(id);
                return;
            }
        }

        static void SetArchiveProviderState(DlItem job, string url, string provider, string rejectedProvider = null,
            bool clearProvider = false)
        {
            if (job == null || string.IsNullOrEmpty(url)) return;
            string key = ArchiveSourceKey(url);
            var rows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string row in (job.ArchiveProviderState ?? "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = row.Split('|');
                if (fields.Length == 3) rows[fields[0]] = fields[1] + "|" + fields[2];
            }
            string selected, denied;
            if (!rows.TryGetValue(key, out selected)) selected = "|";
            string[] parts = selected.Split('|');
            var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (parts.Length > 1)
                foreach (string id in parts[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) rejected.Add(id);
            if (clearProvider) parts[0] = "";
            if (!string.IsNullOrEmpty(provider)) parts[0] = provider;
            if (!string.IsNullOrEmpty(rejectedProvider)) rejected.Add(rejectedProvider);
            denied = string.Join(",", rejected);
            rows[key] = (parts.Length == 0 ? "" : parts[0]) + "|" + denied;
            var text = new StringBuilder();
            foreach (var pair in rows) text.Append(pair.Key).Append('|').Append(pair.Value).Append(';');
            job.ArchiveProviderState = text.ToString();
        }

        static HashSet<string> RejectedProviders(DlItem job)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (job != null)
                foreach (string id in (job.ParkRejectedProviderIds ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    result.Add(id);
            return result;
        }

        static void AddRejectedProvider(DlItem job, string provider)
        {
            if (job == null || string.IsNullOrEmpty(provider)) return;
            var ids = RejectedProviders(job);
            ids.Add(provider);
            job.ParkRejectedProviderIds = string.Join(",", ids);
        }

        /// <summary>A refusal recorded for this row steers its automatic retries to another
        /// enabled service. When no other enabled service supports the source, it would
        /// leave nothing to ask, so it is dropped and the remaining service is asked
        /// again. Returns true when no refusal remains.</summary>
        bool ForgetStaleRejections(DlItem job, out HashSet<string> remaining)
        {
            remaining = RejectedProviders(job);
            if (remaining.Count == 0) return true;
            DebridHostSupport.RefreshEnabled(_cfg);
            int before = remaining.Count;
            remaining.RemoveWhere(id => !UnlockProviders.HasSupportedAlternative(_cfg, new[] { job.HosterUrl }, id));
            if (remaining.Count != before)
            {
                string kept = string.Join(",", remaining);
                lock (_lock) job.ParkRejectedProviderIds = kept;
                SspiLog.Write("download", "event=provider-rejection-cleared cleared=" + (before - remaining.Count) +
                    " kept=" + remaining.Count);
            }
            return remaining.Count == 0;
        }

        /// <summary>The player chose Retry, so every enabled service is asked again.
        /// Archive parts keep the service bound to them for link renewal.</summary>
        static void ClearProviderRejections(DlItem job)
        {
            job.ParkRejectedProviderIds = "";
            if (string.IsNullOrEmpty(job.ArchiveProviderState)) return;
            var kept = new StringBuilder();
            foreach (string row in job.ArchiveProviderState.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = row.Split('|');
                if (fields.Length == 3 && fields[1].Length > 0) kept.Append(fields[0]).Append('|').Append(fields[1]).Append("|;");
            }
            job.ArchiveProviderState = kept.ToString();
        }

        static bool HasArchiveProviderState(DlItem job)
        { return job != null && !string.IsNullOrEmpty(job.ArchiveProviderState); }

        static string EncodeParkStartedUrls(IEnumerable<string> urls)
        {
            var values = new SortedSet<string>(StringComparer.Ordinal);
            if (urls != null) foreach (string url in urls) if (!string.IsNullOrEmpty(url)) values.Add(url);
            var encoded = new StringBuilder();
            foreach (string url in values)
            {
                if (encoded.Length > 0) encoded.Append('|');
                encoded.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(url)));
            }
            return encoded.ToString();
        }

        static List<string> DecodeParkStartedUrls(string encoded)
        {
            var urls = new List<string>();
            if (string.IsNullOrEmpty(encoded)) return urls;
            foreach (string item in encoded.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    string url = Encoding.UTF8.GetString(Convert.FromBase64String(item));
                    if (AllDebridClient.IsHttp(url) && !urls.Contains(url)) urls.Add(url);
                }
                catch (FormatException) { }
            }
            return urls;
        }

        List<string> PreparedUrlsForPark(DlItem job, IList<string> urls, string provider)
        {
            var started = new List<string>();
            foreach (string url in urls ?? new string[0])
            {
                bool has = string.Equals(provider, UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase)
                    ? AllDebridClient.HasRemoteOperation(_cfg.AllDebridApiKey, url)
                    : string.Equals(provider, UnlockProviders.TorBoxId, StringComparison.OrdinalIgnoreCase) &&
                        TorBoxClient.HasPreparedDownload(_cfg.TorBoxApiKey, url);
                if (has) started.Add(url);
            }
            return started;
        }

        List<string> ObservedParkReceipts(DlItem target, string provider)
        {
            IList<string> expected = string.IsNullOrEmpty(target.ArchiveVolumes)
                ? (IList<string>)new[] { string.IsNullOrEmpty(target.ParkHostUrl) ? target.HosterUrl : target.ParkHostUrl }
                : TorBoxPreparationUrls(target);
            return PreparedUrlsForPark(target, expected, provider);
        }

        static void MergeParkStartedUrls(DlItem target, IEnumerable<string> additional)
        {
            var urls = new SortedSet<string>(DecodeParkStartedUrls(target.ParkStartedUrls), StringComparer.Ordinal);
            if (additional != null) foreach (string url in additional) if (!string.IsNullOrEmpty(url)) urls.Add(url);
            target.ParkStartedUrls = EncodeParkStartedUrls(urls);
            if (!string.IsNullOrEmpty(target.ArchiveVolumes))
                foreach (string url in urls) SetArchiveProviderState(target, url, target.ParkProviderId);
        }

        static DebridResolutionError AllDebridParkFailure(AllDebridClient.PreparedPollResult poll, string url)
        {
            var typed = poll == null ? null : poll.Transport as DebridResolutionError;
            if (typed != null) return typed;
            string code = poll == null ? "" : poll.ProviderCode;
            if (string.IsNullOrEmpty(code) && poll != null && poll.ProviderState == "3") code = "LINK_DOWN";
            if (!string.IsNullOrEmpty(code))
                return DebridResolutionError.FromResponse("AllDebrid", url,
                    "{\"status\":\"error\",\"code\":\"" + JsonLite.Escape(code) + "\"}");
            return DebridResolutionError.AmbiguousCreate("AllDebrid", url, "AMBIGUOUS_CREATE",
                poll != null && !string.IsNullOrEmpty(poll.Error) ? poll.Error :
                    "The provider did not return a confirmed preparation result. Check AllDebrid before retrying this source.");
        }

        void RejectParkedProvider(DlItem target, string provider, string sourceUrl)
        {
            if (!string.IsNullOrEmpty(target.ArchiveVolumes))
                SetArchiveProviderState(target, sourceUrl, "", provider, true);
            else
                AddRejectedProvider(target, provider);
            ClearParkLocked(target);
            target.StatusText = UnlockProviders.DisplayName(provider) + " rejected this source · trying the next provider";
        }

        static void ClearParkLocked(DlItem target)
        {
            target.ParkedForProvider = false;
            target.ParkRetryWithoutReceipt = false;
            target.ParkStartedUrls = "";
            target.ParkHostUrl = "";
            target.ParkPollDueUtcTicks = 0;
            target.ParkProviderId = "";
            target.ParkLastState = "";
            target.ResolvedProviderId = "";
            target.RetryAfterUtcTicks = 0;
            target.Error = null;
        }

        /// <summary>Points a queued job at another mirror of the same package. Caller holds _lock.</summary>
        static void ApplyMirrorLocked(DlItem job, PackageMirror mirror)
        {
            job.HosterUrl = mirror.Url;
            ApplyContainerFormatHint(job, null, null, null, mirror.Url);
            job.CandidateId = mirror.CandidateId;
            job.AccessType = mirror.AccessType;
            job.SourcePageUrl = mirror.SourcePageUrl;
            job.SourceAttribution = mirror.SourceAttribution;
            job.ExpectedSha256 = string.IsNullOrEmpty(mirror.ExpectedSha256) ? "" : NormalizeSha256(mirror.ExpectedSha256, true);
            job.ExpectedContentId = mirror.ExpectedContentId ?? "";
            job.ExpectedByteSize = mirror.ExpectedByteSize;
            job.ExpiresUtc = mirror.ExpiresUtc;
            job.ArchivePassword = mirror.ArchivePassword;
            job.ArchivePasswords = mirror.ArchivePasswords;
        }

        static PackageMirror MirrorFromJob(DlItem job)
        {
            return new PackageMirror {
                Url = job.HosterUrl, CandidateId = job.CandidateId ?? "",
                AccessType = string.IsNullOrEmpty(job.AccessType) ? "Unknown" : job.AccessType,
                SourcePageUrl = job.SourcePageUrl ?? "", SourceAttribution = job.SourceAttribution ?? "",
                ExpectedSha256 = job.ExpectedSha256 ?? "", ExpectedContentId = job.ExpectedContentId ?? "",
                ExpectedByteSize = Math.Max(0, job.ExpectedByteSize), ExpiresUtc = job.ExpiresUtc ?? "",
                ArchivePassword = job.ArchivePassword ?? "", ArchivePasswords = job.ArchivePasswords ?? ""
            };
        }

        /// <summary>A single-file job with no bytes on disk can move to another
        /// mirror without mixing data from two different uploads.</summary>
        static bool CanSwitchMirror(DlItem job)
        {
            return job != null && string.IsNullOrEmpty(job.ArchiveVolumes) && !string.IsNullOrEmpty(job.MirrorCandidates) &&
                !File.Exists(job.DestPath) && !File.Exists(job.DestPath + ".part") &&
                !File.Exists(job.DestPath + ".ranges") && !File.Exists(job.DestPath + ".resume");
        }

        /// <summary>
        /// A parked provider preparation failed on its host. The synchronous
        /// resolve path already tries the package's other mirrors; the parked path
        /// must do the same instead of failing the job while mirrors remain.
        /// Caller holds _lock; <paramref name="filesClean"/> is CanSwitchMirror
        /// evaluated before the lock was taken.
        /// </summary>
        bool TrySwitchParkedMirrorLocked(DlItem target, string failedUrl, string provider, bool filesClean)
        {
            if (!filesClean || !string.IsNullOrEmpty(target.ArchiveVolumes)) return false;
            var mirrors = PackageMirrorFallback.Decode(target.MirrorCandidates);
            mirrors.RemoveAll(m => string.Equals(m.Url, failedUrl, StringComparison.Ordinal) ||
                string.Equals(m.Url, target.HosterUrl, StringComparison.Ordinal));
            if (mirrors.Count == 0) return false;
            var next = mirrors[0];
            mirrors.RemoveAt(0);
            string failedHost = DebridResolutionError.HostName(failedUrl), nextHost = DebridResolutionError.HostName(next.Url);
            ApplyMirrorLocked(target, next);
            target.MirrorCandidates = PackageMirrorFallback.EncodeMirrors(mirrors);
            ClearParkLocked(target);
            // Earlier provider rejections were for the previous host.
            target.ParkRejectedProviderIds = "";
            target.ProviderHostRetries = 0;
            target.StatusText = UnlockProviders.DisplayName(provider) + " could not fetch this file from " + failedHost +
                " · trying " + nextHost;
            SspiLog.Write("download", "event=parked-mirror-switch provider=" + provider + " from=" + failedHost + " to=" + nextHost);
            return true;
        }

        /// <summary>TorBox reports some host fetches as failed when the host was
        /// briefly unavailable. With no mirror left, a fresh job for the same link is
        /// tried once after a pause before the failure is shown. Caller holds _lock.</summary>
        static bool TryRetryParkedHostLocked(DlItem target, string provider)
        {
            if (!string.IsNullOrEmpty(target.ArchiveVolumes) || target.ProviderHostRetries >= 1) return false;
            target.ProviderHostRetries++;
            ClearParkLocked(target);
            target.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(45).Ticks;
            target.StatusText = UnlockProviders.DisplayName(provider) +
                " could not fetch this file from the host · trying again once in 45s · other downloads continue";
            SspiLog.Write("download", "event=parked-host-retry provider=" + provider);
            return true;
        }

        /// <summary>
        /// Before sending a link to TorBox, ask which of this package's mirrors it
        /// already holds. A cached mirror starts immediately instead of waiting for
        /// TorBox to fetch the file from the host. Returns true when the job's
        /// (possibly newly selected) link is cached.
        /// </summary>
        bool PreferCachedTorBoxMirror(DlItem job, int attempt)
        {
            if (!string.IsNullOrEmpty(job.ArchiveVolumes)) return false;
            var mirrors = CanSwitchMirror(job) ? PackageMirrorFallback.Decode(job.MirrorCandidates) : new List<PackageMirror>();
            var urls = new List<string> { job.HosterUrl };
            foreach (var mirror in mirrors)
                if (!urls.Contains(mirror.Url) && TorBoxIsFirstProvider(_cfg, mirror.Url)) urls.Add(mirror.Url);
            Func<bool> canceled = () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested || job.RemoveRequested;
            HashSet<string> cached = TorBoxClient.CachedLinks(_cfg.TorBoxApiKey, urls, canceled);
            if (cached.Count == 0) return false;
            if (cached.Contains(job.HosterUrl))
            {
                lock (_lock) { if (!canceled()) job.StatusText = "Cached at TorBox · starting"; }
                return true;
            }
            var pick = mirrors.Find(m => cached.Contains(m.Url));
            if (pick == null) return false;
            string from, to;
            lock (_lock)
            {
                if (canceled()) return false;
                from = DebridResolutionError.HostName(job.HosterUrl);
                to = DebridResolutionError.HostName(pick.Url);
                mirrors.Remove(pick);
                // Keep the uncached link as the first fallback for this package.
                mirrors.Insert(0, MirrorFromJob(job));
                ApplyMirrorLocked(job, pick);
                job.MirrorCandidates = PackageMirrorFallback.EncodeMirrors(mirrors);
                job.StatusText = "Cached at TorBox on " + to + " · starting";
            }
            SspiLog.Write("download", "event=torbox-cached-mirror from=" + from + " to=" + to);
            if (!SaveManifest()) throw new IOException("Could not save the selected package mirror");
            return true;
        }

        bool CanFallbackParkedRejection(DlItem target, string sourceUrl, DebridResolutionError rejection)
        {
            if (target == null || rejection == null || !rejection.CanTryProvider || string.IsNullOrEmpty(sourceUrl))
                return false;
            return UnlockProviders.HasSupportedAlternative(_cfg, new[] { sourceUrl }, target.ParkProviderId);
        }

        bool RequeueArchiveAfterParkedRejection(DlItem job, int attempt, string provider, string sourceUrl)
        {
            if (job == null || string.IsNullOrEmpty(job.ArchiveVolumes) || string.IsNullOrEmpty(sourceUrl))
                return false;
            bool sourceFound = false;
            foreach (ArchiveVolume volume in ArchiveVolumeSet.Decode(job.ArchiveVolumes))
                if (string.Equals(volume.Url, sourceUrl, StringComparison.Ordinal)) { sourceFound = true; break; }
            if (!sourceFound) return false;
            lock (_lock)
            {
                if (!object.ReferenceEquals(Find(job.Id), job) || CommitRequestedStop(job, attempt) ||
                    job.RemoveRequested) return true;
                RejectParkedProvider(job, provider, sourceUrl);
                job.State = DlState.Queued;
                _nextJobStartAt = TransferClockMs();
            }
            if (!SaveManifest())
            {
                SetFailureIfCurrent(job, attempt, "Could not save archive provider rejection state");
                return true;
            }
            return true;
        }

        void EnsureParkPollTimer()
        {
            if (Volatile.Read(ref _parkTimerStarted) == 1) return;
            if (Interlocked.CompareExchange(ref _parkTimerStarted, 1, 0) != 0) return;
            _parkPollTimer = new Timer(delegate { if (_run) PumpParkedPreparationsSafely(); }, null, 1000, 1000);
        }

        /// <summary>Identifies the initial TorBox preflight; fallback providers
        /// can also park through the shared preparation path.</summary>
        static bool TorBoxIsFirstProvider(AppSettings cfg, string hosterUrl)
        {
            if (cfg == null || !cfg.UseUnlockProvider || !cfg.HasTorBox || string.IsNullOrEmpty(hosterUrl)) return false;
            if (!UnlockProviders.IsEnabled(cfg, UnlockProviders.TorBoxId)) return false;
            string[] ranked = UnlockProviders.RankedProviderIds(cfg, hosterUrl);
            return ranked.Length > 0 && string.Equals(ranked[0], UnlockProviders.TorBoxId, StringComparison.OrdinalIgnoreCase);
        }

        static string ParkedPreparationStatus(TorBoxClient.PreparedPollResult poll, long idleMinutes = 0)
        {
            if (poll.Kind == TorBoxClient.PreparedPollKind.Transient)
                return "Preparing in TorBox · list request retry 1/" + TorBoxClient.TransientListAttempts +
                    " · other downloads continue";
            string metric = string.IsNullOrEmpty(poll.Metric) ? "" : " · " + poll.Metric;
            // Before the no-progress limit fails the row, say how long TorBox has been still.
            if (idleMinutes >= 2) metric += " · no progress for " + idleMinutes + " min";
            if (!poll.Visible)
                return "Preparing in TorBox · waiting for the provider to list it" + metric + " · other downloads continue";
            return "Preparing in TorBox" + metric + " · other downloads continue";
        }

        /// <summary>
        /// Polls one parked TorBox preparation at a time, off the worker thread.
        /// Single-flight: at most one provider request is in flight, the per-row
        /// cadence is 1s/2s/5s, and every result is re-validated against the live
        /// queue row before it is applied.
        /// </summary>
        // A Timer callback exception terminates the process under Mono, and the
        // provider receipt loaders throw on unreadable or implausible records. An
        // exception escapes only while this pass still owns the poll, so release
        // it for a later pass instead of crashing SSPI or stalling parked rows.
        void PumpParkedPreparationsSafely()
        {
            try { PumpParkedPreparations(); }
            catch (Exception ex)
            {
                Volatile.Write(ref _parkPollBusy, 0);
                long now = TransferClockMs();
                if (now - _parkPollFailureLoggedAt >= 60000)
                {
                    _parkPollFailureLoggedAt = now;
                    SspiLog.Write("download", "event=park-poll-failed exception=" + ex.GetType().Name);
                }
            }
        }

        void PumpParkedPreparations()
        {
            if (_cfg == null || Interlocked.CompareExchange(ref _parkPollBusy, 1, 0) != 0) return;
            DlItem target = null;
            int attempt = 0;
            string token = null;
            bool rearmed = false;
            bool expired = false;
            DlItem missingReceiptTarget = null;
            int missingReceiptAttempt = 0;
            string missingReceiptUrl = "";
            long nowTicks = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                // Earliest due first: a just-polled row must not starve a row that
                // has never been polled, and list order keeps equal deadlines fair.
                for (int index = _items.Count - 1; index >= 0; index--)
                {
                    var candidate = _items[index];
                    if (!candidate.ParkedForProvider) continue;
                    if (candidate.State != DlState.Queued) continue;
                    if (candidate.PauseRequested || candidate.CancelRequested || candidate.RemoveRequested)
                    {
                        candidate.ParkedForProvider = false;
                        candidate.ParkRetryWithoutReceipt = false;
                        rearmed = true;
                        continue;
                    }
                    if (candidate.ParkRetryWithoutReceipt)
                    {
                        if (nowTicks < candidate.ParkPollDueUtcTicks) continue;
                        candidate.ParkedForProvider = false;
                        candidate.ParkRetryWithoutReceipt = false;
                        candidate.ParkPollDueUtcTicks = 0;
                        candidate.StatusText = UnlockProviders.DisplayName(candidate.ParkProviderId) + " retry window elapsed · queued";
                        rearmed = true;
                        continue;
                    }
                    bool allDebrid = string.Equals(candidate.ParkProviderId, UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase);
                    List<string> expectedReceipts = DecodeParkStartedUrls(candidate.ParkStartedUrls);
                    string missingUrl = "";
                    foreach (string expectedUrl in expectedReceipts)
                    {
                        bool retained = allDebrid
                            ? AllDebridClient.HasRemoteOperation(_cfg.AllDebridApiKey, expectedUrl)
                            : TorBoxClient.HasPreparedDownload(_cfg.TorBoxApiKey, expectedUrl);
                        if (!retained) { missingUrl = expectedUrl; break; }
                    }
                    if (expectedReceipts.Count == 0 || missingUrl.Length > 0)
                    {
                        candidate.ParkedForProvider = false;
                        candidate.Error = null;
                        candidate.StatusText = (allDebrid ? "AllDebrid" : "TorBox") +
                            " preparation receipt is missing; check the provider before retrying this source";
                        missingReceiptTarget = candidate;
                        missingReceiptAttempt = candidate.AttemptId;
                        missingReceiptUrl = missingUrl;
                        rearmed = true;
                        break;
                    }
                    if (target == null || candidate.ParkPollDueUtcTicks < target.ParkPollDueUtcTicks)
                    {
                        target = candidate;
                        attempt = candidate.AttemptId;
                    }
                }
                if (target != null && nowTicks < target.ParkPollDueUtcTicks) target = null;
                if (target != null)
                {
                    bool allDebrid = string.Equals(target.ParkProviderId, UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase);
                    token = allDebrid ? _cfg.AllDebridApiKey : _cfg.TorBoxApiKey;
                    expired = allDebrid
                        ? nowTicks - target.ParkStartedUtcTicks > AllDebridClient.PreparationDeadline.Ticks
                        : TorBoxParkExpired(target, nowTicks);
                }
            }
            if (rearmed) SaveManifest();
            if (missingReceiptTarget != null)
            {
                string detail = string.IsNullOrEmpty(missingReceiptUrl) ? "" :
                    " (missing part " + DebridResolutionError.HostName(missingReceiptUrl) + ")";
                SetFailureIfCurrent(missingReceiptTarget, missingReceiptAttempt,
                    (string.Equals(missingReceiptTarget.ParkProviderId, UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase)
                        ? "AllDebrid" : "TorBox") + " preparation receipt is missing" + detail +
                    ". Check the provider before retrying this source; SSPI did not create another remote task.");
                Volatile.Write(ref _parkPollBusy, 0);
                return;
            }
            if (target == null)
            {
                Volatile.Write(ref _parkPollBusy, 0);
                return;
            }
            if (expired)
            {
                ExpireParkedPreparation(target, attempt);
                Volatile.Write(ref _parkPollBusy, 0);
                return;
            }
            string hostUrl = string.IsNullOrEmpty(target.ParkHostUrl) ? target.HosterUrl : target.ParkHostUrl;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Exception failure = null;
                    bool allDebrid = string.Equals(target.ParkProviderId, UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase);
                    if (allDebrid)
                    {
                        AllDebridClient.PreparedPollResult poll = null;
                        try { poll = string.IsNullOrEmpty(target.ArchiveVolumes) || !string.IsNullOrEmpty(target.ParkHostUrl)
                            ? AllDebridClient.PollPrepared(token, hostUrl, () => !IsParkedPollCurrent(target, attempt))
                            : AllDebridClient.PrepareMany(token, TorBoxPreparationUrls(target), target.ParkPollCount,
                                () => !IsParkedPollCurrent(target, attempt),
                                text => { lock (_lock) { if (IsParkedPollCurrent(target, attempt)) target.StatusText = text; } }); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { failure = ex; }
                        if (poll != null || failure != null) ApplyAllDebridParkedPollResult(target, attempt, poll, failure);
                    }
                    else
                    {
                        TorBoxClient.PreparedPollResult poll = null;
                        try { poll = string.IsNullOrEmpty(target.ArchiveVolumes) || !string.IsNullOrEmpty(target.ParkHostUrl)
                            ? TorBoxClient.PollPrepared(token, hostUrl, () => !IsParkedPollCurrent(target, attempt))
                            : TorBoxClient.PrepareMany(token, TorBoxPreparationUrls(target), target.ParkPollCount,
                                () => !IsParkedPollCurrent(target, attempt),
                                text => { lock (_lock) { if (IsParkedPollCurrent(target, attempt)) target.StatusText = text; } }); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { failure = ex; }
                        if (poll != null || failure != null) ApplyParkedPollResult(target, attempt, poll, failure);
                    }
                }
                finally { Volatile.Write(ref _parkPollBusy, 0); }
            });
        }

        bool IsParkedPollCurrent(DlItem target, int attempt)
        {
            lock (_lock)
            {
                return object.ReferenceEquals(Find(target.Id), target) && target.AttemptId == attempt &&
                    target.ParkedForProvider && target.State == DlState.Queued &&
                    !target.PauseRequested && !target.CancelRequested && !target.RemoveRequested;
            }
        }

        void ApplyParkedPollResult(DlItem target, int attempt, TorBoxClient.PreparedPollResult poll, Exception failure)
        {
            string failMessage = null;
            bool changed = false;
            string failedUrl = poll == null ? "" : poll.FailedUrl ?? "";
            if (string.IsNullOrEmpty(failedUrl))
                failedUrl = string.IsNullOrEmpty(target.ParkHostUrl) ? target.HosterUrl : target.ParkHostUrl;
            DebridResolutionError rejection = poll != null &&
                (poll.Kind == TorBoxClient.PreparedPollKind.Rejected || poll.Kind == TorBoxClient.PreparedPollKind.Terminal)
                    ? TorBoxClient.PreparationFailure(failedUrl, poll) : null;
            bool canFallback = CanFallbackParkedRejection(target, failedUrl, rejection);
            bool filesClean = CanSwitchMirror(target);
            List<string> observed = ObservedParkReceipts(target, UnlockProviders.TorBoxId);
            lock (_lock)
            {
                if (!object.ReferenceEquals(Find(target.Id), target) || target.AttemptId != attempt ||
                    !target.ParkedForProvider) return;
                if (target.PauseRequested || target.CancelRequested || target.RemoveRequested)
                {
                    target.ParkedForProvider = false;
                    changed = true;
                }
                else if (failure != null || poll == null)
                {
                    failMessage = ParkedTransientFailure(target, failure);
                    changed = true;
                }
                else
                {
                    MergeParkStartedUrls(target, observed);
                    switch (poll.Kind)
                    {
                        case TorBoxClient.PreparedPollKind.Ready:
                            ApplyContainerFormatHint(target, null, poll.FileName, null, null);
                            target.ParkedForProvider = false;
                            target.ParkRetryWithoutReceipt = false;
                            target.ParkHostUrl = "";
                            target.ParkStartedUrls = "";
                            target.ParkProviderId = "";
                            target.RetryAfterUtcTicks = 0;
                            target.ResolvedProviderId = UnlockProviders.TorBoxId;
                            target.ParkLastState = "";
                            target.StatusText = "TorBox ready · other downloads continue";
                            changed = true;
                            break;
                        case TorBoxClient.PreparedPollKind.Preparing:
                            target.ParkPollCount++;
                            target.ParkTransientFailures = 0;
                            target.ParkLastState = poll.ProviderState ?? "";
                            target.ParkPollDueUtcTicks = DateTime.UtcNow.Ticks +
                                TimeSpan.FromMilliseconds(Math.Max(ParkPollDelayMs(target), (long)poll.RetryAfterSeconds * 1000)).Ticks;
                            // TorBox's own progress keeps a slow host fetch parked (TorBoxParkExpired).
                            if (poll.Advanced) target.ParkProgressUtcTicks = DateTime.UtcNow.Ticks;
                            target.StatusText = ParkedPreparationStatus(poll, ParkIdleMinutes(target, DateTime.UtcNow.Ticks));
                            changed = true;
                            break;
                        case TorBoxClient.PreparedPollKind.Transient:
                            failMessage = ParkedTransientFailure(target, poll.Transport);
                            changed = true;
                            break;
                        default:
                            if (canFallback) RejectParkedProvider(target, UnlockProviders.TorBoxId, failedUrl);
                            else if (rejection != null && rejection.CanTryMirror &&
                                TrySwitchParkedMirrorLocked(target, failedUrl, UnlockProviders.TorBoxId, filesClean)) { }
                            else if (poll.Kind == TorBoxClient.PreparedPollKind.Terminal && rejection != null && rejection.CanTryMirror &&
                                TryRetryParkedHostLocked(target, UnlockProviders.TorBoxId)) { }
                            else
                            {
                                target.ParkedForProvider = false;
                                failMessage = ResolutionFailureText(target,
                                    rejection ?? TorBoxClient.PreparationFailure(failedUrl, poll));
                            }
                            changed = true;
                            break;
                    }
                }
            }
            if (!changed) return;
            if (failMessage != null) SetFailureIfCurrent(target, attempt, failMessage);
            else SaveManifest();
        }

        void ApplyAllDebridParkedPollResult(DlItem target, int attempt, AllDebridClient.PreparedPollResult poll, Exception failure)
        {
            string failMessage = null;bool changed = false;
            string failedUrl = poll == null ? "" : poll.FailedUrl ?? "";
            if (string.IsNullOrEmpty(failedUrl))
                failedUrl = string.IsNullOrEmpty(target.ParkHostUrl) ? target.HosterUrl : target.ParkHostUrl;
            DebridResolutionError rejection = null;
            if (poll != null && (poll.Kind == AllDebridClient.PreparedPollKind.Rejected ||
                poll.Kind == AllDebridClient.PreparedPollKind.Terminal))
                rejection = AllDebridParkFailure(poll, failedUrl);
            bool canFallback = CanFallbackParkedRejection(target, failedUrl, rejection);
            bool filesClean = CanSwitchMirror(target);
            List<string> observed = ObservedParkReceipts(target, UnlockProviders.AllDebridId);
            lock (_lock)
            {
                if (!object.ReferenceEquals(Find(target.Id), target) || target.AttemptId != attempt || !target.ParkedForProvider) return;
                if (target.PauseRequested || target.CancelRequested || target.RemoveRequested)
                { target.ParkedForProvider = false;changed = true; }
                else if (failure != null || poll == null)
                { MergeParkStartedUrls(target, observed);ParkedTransientFailure(target, failure);changed = true; }
                else
                {
                    MergeParkStartedUrls(target, observed);
                    switch (poll.Kind)
                    {
                        case AllDebridClient.PreparedPollKind.Ready:
                            target.ParkedForProvider = false;target.ParkRetryWithoutReceipt = false;target.ParkHostUrl = "";
                            target.ParkStartedUrls = "";target.ParkProviderId = "";
                            target.RetryAfterUtcTicks = 0;target.ParkLastState = "";
                            target.ResolvedProviderId = UnlockProviders.AllDebridId;
                            target.StatusText = "AllDebrid ready · queued";changed = true;break;
                        case AllDebridClient.PreparedPollKind.Preparing:
                            target.ParkPollCount++;target.ParkTransientFailures = 0;target.ParkLastState = poll.ProviderState ?? "";
                            target.ParkPollDueUtcTicks = DateTime.UtcNow.Ticks + TimeSpan.FromMilliseconds(
                                Math.Max(ParkPollDelayMs(target), (long)poll.RetryAfterSeconds * 1000L)).Ticks;
                            target.StatusText = "Preparing in AllDebrid · next check in 5s · other downloads continue";changed = true;break;
                        case AllDebridClient.PreparedPollKind.Transient:
                            ParkedTransientFailure(target, poll.Transport);changed = true;break;
                        default:
                            if (canFallback) RejectParkedProvider(target, UnlockProviders.AllDebridId, failedUrl);
                            else if (rejection != null && rejection.CanTryMirror &&
                                TrySwitchParkedMirrorLocked(target, failedUrl, UnlockProviders.AllDebridId, filesClean)) { }
                            else
                            {
                                target.ParkedForProvider = false;
                                failMessage = rejection == null
                                    ? (string.IsNullOrEmpty(poll.Error) ? "AllDebrid preparation failed" : poll.Error)
                                    : ResolutionFailureText(target, rejection);
                            }
                            changed = true;break;
                    }
                }
            }
            if (!changed) return;if (failMessage != null) SetFailureIfCurrent(target, attempt, failMessage);else SaveManifest();
        }

        static int ParkPollDelayMs(DlItem target)
        {
            return string.Equals(target.ParkProviderId, UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase)
                ? AllDebridClient.PollDelayMilliseconds : QueueScheduler.NextPollDelayMs(target.ParkPollCount);
        }

        /// <summary>Provider polling failures retain the remote preparation and
        /// back off visibly. They are not evidence that the remote job failed.</summary>
        string ParkedTransientFailure(DlItem target, Exception failure)
        {
            if (target.ParkTransientFailures < int.MaxValue) target.ParkTransientFailures++;
            target.ParkPollCount++;
            int shift = Math.Min(6, Math.Max(0, target.ParkTransientFailures - 1));
            long delay = Math.Max(ParkPollDelayMs(target), Math.Min(60000L, 1000L << shift));
            var rejection = failure as DebridResolutionError;
            if (rejection != null && rejection.RetryAfterSeconds > 0)
                delay = Math.Max(delay, Math.Min(24L * 60 * 60 * 1000, (long)rejection.RetryAfterSeconds * 1000L));
            delay = Math.Min(24L * 60 * 60 * 1000, delay);
            long ticks = TimeSpan.FromMilliseconds(delay).Ticks;
            target.ParkPollDueUtcTicks = DateTime.MaxValue.Ticks - DateTime.UtcNow.Ticks < ticks
                ? DateTime.MaxValue.Ticks : DateTime.UtcNow.Ticks + ticks;
            string provider = string.Equals(target.ParkProviderId, UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase) ? "AllDebrid" : "TorBox";
            target.StatusText = "Preparing in " + provider + " · provider check retry " + target.ParkTransientFailures +
                " in " + Math.Max(1, delay / 1000) + "s · other downloads continue";
            return null;
        }

        void ExpireParkedPreparation(DlItem target, int attempt)
        {
            long seconds, idleMinutes = 0;
            string detail;
            bool allDebrid = string.Equals(target.ParkProviderId, UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase);
            lock (_lock)
            {
                if (!object.ReferenceEquals(Find(target.Id), target) || target.AttemptId != attempt ||
                    !target.ParkedForProvider) return;
                target.ParkedForProvider = false;
                long nowTicks = DateTime.UtcNow.Ticks;
                seconds = Math.Max(1, (nowTicks - target.ParkStartedUtcTicks) / TimeSpan.TicksPerSecond);
                // Within its overall limit, a TorBox row expires only for lack of progress.
                if (!allDebrid && nowTicks - target.ParkStartedUtcTicks <= TorBoxClient.PreparationLimit.Ticks)
                    idleMinutes = Math.Max(1, ParkIdleMinutes(target, nowTicks));
                detail = string.IsNullOrEmpty(target.ParkLastState)
                    ? "the provider never listed the prepared download"
                    : "last provider state " + target.ParkLastState;
            }
            if (idleMinutes > 0)
                SetFailureIfCurrent(target, attempt, "TorBox reported no progress on this file for " + idleMinutes + " min (" +
                    detail + "). The TorBox download was kept; retry from Downloads to keep waiting.");
            else if (!allDebrid)
                SetFailureIfCurrent(target, attempt, "TorBox is still preparing this file after " +
                    Math.Max(1, seconds / 3600) + " h (" + detail + "). The TorBox download was kept; retry from Downloads to keep waiting.");
            else
                SetFailureIfCurrent(target, attempt, "AllDebrid is still preparing this file after " + seconds + "s (" +
                    detail + "). The prepared download ID was kept; retry from Downloads shortly.");
        }

        /// <summary>A slow host fetch at TorBox can take hours, so a parked TorBox row
        /// waits while TorBox reports progress. It fails after PreparationDeadline without
        /// any, or after PreparationLimit in total. The idle clock starts at this
        /// session's first look, so after a restart TorBox is asked before the row is
        /// judged. Caller holds _lock.</summary>
        static bool TorBoxParkExpired(DlItem target, long nowTicks)
        {
            if (target.ParkProgressUtcTicks <= target.ParkStartedUtcTicks) target.ParkProgressUtcTicks = nowTicks;
            return nowTicks - target.ParkProgressUtcTicks > TorBoxClient.PreparationDeadline.Ticks ||
                nowTicks - target.ParkStartedUtcTicks > TorBoxClient.PreparationLimit.Ticks;
        }

        static long ParkIdleMinutes(DlItem target, long nowTicks)
        {
            long since = Math.Max(target.ParkStartedUtcTicks, target.ParkProgressUtcTicks);
            return since <= 0 || nowTicks <= since ? 0 : (nowTicks - since) / TimeSpan.TicksPerMinute;
        }

        bool ParkDeferredProvider(DlItem job, int attempt, ProviderPreparationWaitException wait)
        {
            if (job == null || wait == null) return false;
            string provider = wait.ProviderId;
            bool rdTransient = string.Equals(provider, UnlockProviders.RealDebridId, StringComparison.OrdinalIgnoreCase) &&
                (wait.State == "-1" || wait.State == "6" || wait.State == "25");
            if (rdTransient)
            {
                int next = job.ProviderTransientRetries + 1;
                if (next > RealDebridClient.TransientCreateRetries) return false;
                job.ProviderTransientRetries = next;
                if (wait.RetryAfterSeconds <= 0)
                    wait = new ProviderPreparationWaitException(provider, wait.HostUrl, wait.State,
                        Math.Min(8, 1 << (next - 1)), false, wait.InnerException);
            }
            // A TorBox server fault on a create was parked for a second with no limit, so the link
            // was sent again every few seconds. Like the TorBox-first path it gets three retries;
            // each first checks the account for a download TorBox kept (TorBoxClient).
            bool torBoxFault = string.Equals(provider, UnlockProviders.TorBoxId, StringComparison.OrdinalIgnoreCase) &&
                TorBoxClient.ServerFault(wait.State);
            if (torBoxFault)
            {
                int next = job.ProviderTransientRetries + 1;
                if (next > 3)
                {
                    // Spent: a later Retry from Downloads gets its own three.
                    lock (_lock) if (job.AttemptId == attempt) job.ProviderTransientRetries = 0;
                    SetFailureIfCurrent(job, attempt, ResolutionFailureText(job, wait.InnerException ?? wait));
                    return true;
                }
                job.ProviderTransientRetries = next;
                wait = new ProviderPreparationWaitException(provider, wait.HostUrl, wait.State,
                    (int)Math.Min(60L, 5L << (next - 1)), false, wait.InnerException);
            }
            string parkHost = wait.HostUrl;
            bool batched = false;
            if (!string.IsNullOrEmpty(job.ArchiveVolumes))
            {
                List<string> urls = TorBoxPreparationUrls(job);
                bool providerSupportsAll = urls.Count > 1;
                for (int i = 0; providerSupportsAll && i < urls.Count; i++)
                    providerSupportsAll = Array.IndexOf(UnlockProviders.RankedProviderIds(_cfg, urls[i]), provider) >= 0;
                if (providerSupportsAll)
                {
                    Func<bool> canceled = () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested || job.RemoveRequested;
                    if (string.Equals(provider, UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase))
                    {
                        var result = AllDebridClient.PrepareMany(_cfg.AllDebridApiKey, urls, 0, canceled,
                            text => { lock (_lock) { if (!canceled()) job.StatusText = text; } });
                        if (result.Kind == AllDebridClient.PreparedPollKind.Rejected || result.Kind == AllDebridClient.PreparedPollKind.Terminal)
                        {
                            string failedUrl = result.FailedUrl ?? wait.HostUrl;
                            var rejection = AllDebridParkFailure(result, failedUrl);
                            if (rejection.CanTryProvider &&
                                UnlockProviders.HasSupportedAlternative(_cfg, new[] { failedUrl }, provider) &&
                                RequeueArchiveAfterParkedRejection(job, attempt, provider, failedUrl))
                                return true;
                            SetFailureIfCurrent(job, attempt, rejection.Message);
                            return true;
                        }
                        wait = new ProviderPreparationWaitException(provider, urls[0], result.ProviderState,
                            result.RetryAfterSeconds, result.StartedUrls.Count > 0, result.Transport);
                        parkHost = "";
                        batched = true;
                    }
                    else if (string.Equals(provider, UnlockProviders.TorBoxId, StringComparison.OrdinalIgnoreCase))
                    {
                        var result = TorBoxClient.PrepareMany(_cfg.TorBoxApiKey, urls, 0, canceled,
                            text => { lock (_lock) { if (!canceled()) job.StatusText = text; } });
                        if (result.Kind == TorBoxClient.PreparedPollKind.Rejected || result.Kind == TorBoxClient.PreparedPollKind.Terminal)
                        {
                            string failedUrl = result.FailedUrl ?? wait.HostUrl;
                            var rejection = TorBoxClient.PreparationFailure(failedUrl, result);
                            if (rejection.CanTryProvider &&
                                UnlockProviders.HasSupportedAlternative(_cfg, new[] { failedUrl }, provider) &&
                                RequeueArchiveAfterParkedRejection(job, attempt, provider, failedUrl))
                                return true;
                            SetFailureIfCurrent(job, attempt, rejection.Message);
                            return true;
                        }
                        wait = new ProviderPreparationWaitException(provider, urls[0], result.ProviderState,
                            result.RetryAfterSeconds, urls.Exists(url => TorBoxClient.HasPreparedDownload(_cfg.TorBoxApiKey, url)), result.Transport);
                        parkHost = "";
                        batched = true;
                    }
                }
            }
            IList<string> receiptCandidates = batched ? (IList<string>)TorBoxPreparationUrls(job) :
                (IList<string>)new[] { parkHost };
            List<string> startedUrls = PreparedUrlsForPark(job, receiptCandidates, provider);
            bool hasDurableReceipt = startedUrls.Count > 0;
            if (wait.RetryAfterSeconds <= 0 && wait.HasDurableReceipt)
                wait = new ProviderPreparationWaitException(provider, wait.HostUrl, wait.State,
                    string.Equals(provider, UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase)
                        ? AllDebridClient.PollDelayMilliseconds / 1000 : QueueScheduler.NextPollDelayMs(0) / 1000,
                    true, wait.InnerException);
            long now = DateTime.UtcNow.Ticks;
            long delay = Math.Max(1000L, (long)wait.RetryAfterSeconds * 1000L);
            lock (_lock)
            {
                if (!object.ReferenceEquals(Find(job.Id), job) || CommitRequestedStop(job, attempt) ||
                    job.RemoveRequested) return true;
                job.State = DlState.Queued;
                job.ParkedForProvider = true;
                job.ParkProviderId = provider;
                job.ParkHostUrl = batched ? "" : parkHost;
                job.ParkStartedUrls = EncodeParkStartedUrls(startedUrls);
                job.ParkRetryWithoutReceipt = !hasDurableReceipt;
                if (!string.IsNullOrEmpty(job.ArchiveVolumes))
                    foreach (string url in startedUrls) SetArchiveProviderState(job, url, provider);
                job.ParkStartedUtcTicks = now;
                job.ParkPollCount = 0;
                job.ParkTransientFailures = 0;
                job.ParkLastState = wait.State;
                job.ParkPollDueUtcTicks = now + TimeSpan.FromMilliseconds(Math.Min(24L * 60 * 60 * 1000, delay)).Ticks;
                job.BytesPerSec = 0;
                job.EtaSeconds = 0;
                job.ResolvedProviderId = provider;
                job.StatusText = rdTransient
                    ? "Real-Debrid temporary error " + wait.State + " · retry " + job.ProviderTransientRetries + "/" + RealDebridClient.TransientCreateRetries + " · other downloads continue"
                    : torBoxFault && !batched
                        ? "TorBox server error · retries automatically in " + wait.RetryAfterSeconds + "s (" +
                            job.ProviderTransientRetries + "/3) · " + wait.State
                    : string.Equals(provider, UnlockProviders.TorBoxId, StringComparison.OrdinalIgnoreCase) && wait.State == "RATE_LIMITED"
                        ? TorBoxCreateWaitText(wait.InnerException) + " · retries automatically · other downloads continue"
                    : wait.State == "provider request slot"
                        ? "Waiting for " + UnlockProviders.DisplayName(provider) + " request slot · other downloads continue"
                    // TorBox refuses new cloud downloads while every slot on the plan is busy.
                    : wait.State == "ACTIVE_LIMIT"
                        ? "Waiting for a free " + UnlockProviders.DisplayName(provider) + " download slot · retries automatically"
                    : wait.State == "COOLDOWN_LIMIT"
                        ? "Waiting for the " + UnlockProviders.DisplayName(provider) + " cooldown · retries automatically"
                    : wait.State == "waiting for capacity"
                        ? "Waiting for " + UnlockProviders.DisplayName(provider) + " capacity (download slots or cooldown) · retries automatically"
                        : "Preparing in " + UnlockProviders.DisplayName(provider) + " · other downloads continue";
                _nextJobStartAt = TransferClockMs();
            }
            if (!SaveManifest()) throw new IOException("Could not save provider preparation state; its remote receipt was retained");
            EnsureParkPollTimer();
            return true;
        }

        /// <summary>Retrying a failed row after "TorBox could not confirm whether this
        /// download was created" sends the link again for this row and its archive parts.</summary>
        void ForgetUnconfirmedTorBoxCreates(DlItem it)
        {
            string token = _cfg.TorBoxApiKey;
            if (it == null || string.IsNullOrWhiteSpace(token)) return;
            var urls = new List<string> { it.HosterUrl };
            try { if (!string.IsNullOrEmpty(it.ArchiveVolumes)) foreach (var volume in ArchiveVolumeSet.Decode(it.ArchiveVolumes)) urls.Add(volume.Url); }
            catch (Exception) { }
            foreach (string url in urls)
                try { TorBoxClient.ForgetUnconfirmedCreate(token, url); }
                catch (Exception ex) { SspiLog.Write("download", "event=torbox-unconfirmed-create-clear-failed exception=" + ex.GetType().Name); }
        }

        /// <summary>Free path. When the chosen mirror's host cannot be used without a link
        /// service (a wait or captcha page, or an app-only host), the package's other
        /// mirrors are tried before the job stops; the first host's reason is reported.</summary>
        string ResolveFreeHost(DlItem job, int attempt)
        {
            string original = job.HosterUrl, firstFailure = null;
            var mirrors = PackageMirrorFallback.Decode(job.MirrorCandidates);
            Func<bool> cancelled = () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested;
            try
            {
                return PackageMirrorFallback.Resolve(original, job.MirrorCandidates,
                    url =>
                    {
                        var mirror = url == original ? null : mirrors.Find(candidate => candidate.Url == url);
                        string access = mirror != null ? mirror.AccessType : job.AccessType;
                        if (mirror != null && IsDirectAccess(access)) return url;
                        try { return FreeHosterClient.ResolveDirect(url, string.Equals(access, "HosterLanding", StringComparison.OrdinalIgnoreCase)); }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            if (firstFailure == null) firstFailure = ex.Message;
                            throw DebridResolutionError.FreeHostUnavailable(url, ex.Message);
                        }
                    },
                    () => !cancelled() && CanSwitchMirror(job),
                    mirror =>
                    {
                        lock (_lock)
                        {
                            if (cancelled()) throw new OperationCanceledException();
                            mirrors.RemoveAll(candidate => candidate.Url == original || candidate.Url == mirror.Url);
                            ApplyMirrorLocked(job, mirror);
                            job.MirrorCandidates = PackageMirrorFallback.EncodeMirrors(mirrors);
                        }
                        SspiLog.Write("download", "event=free-mirror-selected from=" + DebridResolutionError.HostName(original) +
                            " to=" + DebridResolutionError.HostName(mirror.Url));
                        if (!SaveManifest()) throw new IOException("Could not save the selected package mirror");
                    },
                    text => { lock (_lock) { if (job.AttemptId == attempt) job.StatusText = text; } },
                    cancelled);
            }
            catch (DebridResolutionError ex) { throw new Exception(firstFailure ?? ex.Reason); }
        }

        string ResolveJobHost(DlItem job, int attempt, ISet<string> unavailable = null, DebridResolutionError prepareFailure = null)
        {
            string originalUrl = job.HosterUrl;
            var mirrors = PackageMirrorFallback.Decode(job.MirrorCandidates);
            var unavailableProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (unavailable != null) foreach (string id in unavailable) unavailableProviders.Add(id);
            foreach (string id in RejectedProviders(job)) unavailableProviders.Add(id);
            Func<bool> cancelled = () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested;
            Action<string> progress = text => { lock (_lock) { if (job.AttemptId == attempt) job.StatusText = text; } };
            return PackageMirrorFallback.Resolve(job.HosterUrl, job.MirrorCandidates,
                url => {
                    if (prepareFailure != null && url == originalUrl) throw prepareFailure;
                    if (!string.Equals(url, job.HosterUrl, StringComparison.Ordinal))
                    {
                        var mirror = mirrors.Find(candidate => string.Equals(candidate.Url, url, StringComparison.Ordinal));
                        if (mirror == null) throw new IOException("The selected package mirror is no longer available");
                        lock (_lock)
                        {
                            if (cancelled()) throw new OperationCanceledException();
                            // Persist the source before a provider can park or create a
                            // durable receipt for it. Rejections belong to the old URL.
                            mirrors.RemoveAll(candidate => candidate.Url == job.HosterUrl || candidate.Url == url);
                            ApplyMirrorLocked(job, mirror);
                            job.MirrorCandidates = PackageMirrorFallback.EncodeMirrors(mirrors);
                            job.ParkRejectedProviderIds = "";
                            job.ResolvedProviderId = "";
                            job.ProviderHostRetries = 0;
                            unavailableProviders.Clear();
                        }
                        if (!SaveManifest()) throw new IOException("Could not save the selected package mirror");
                    }
                    return UnlockProviders.Unrestrict(_cfg, url, progress, cancelled,
                        provider => { lock (_lock) { if (!cancelled()) { job.ResolvedProviderId = provider; job.ProviderTransientRetries = 0; } } },
                        job.ResolvedProviderId, unavailableProviders, true, false,
                        rejected =>
                        {
                            // A refusal is kept only to steer later attempts to another
                            // service; with none, it would block the only one.
                            if (!UnlockProviders.HasSupportedAlternative(_cfg, new[] { url }, rejected)) return;
                            lock (_lock)
                            {
                                if (cancelled()) throw new OperationCanceledException();
                                AddRejectedProvider(job, rejected);
                            }
                            if (!SaveManifest()) throw new IOException("Could not save the rejected provider state");
                        });
                },
                () => !cancelled() && string.IsNullOrEmpty(job.ArchiveVolumes) &&
                    !File.Exists(job.DestPath) && !File.Exists(job.DestPath + ".part") &&
                    !File.Exists(job.DestPath + ".ranges") && !File.Exists(job.DestPath + ".resume"),
                mirror =>
                {
                    lock (_lock)
                    {
                        if (cancelled()) throw new OperationCanceledException();
                        ApplyMirrorLocked(job, mirror);
                    }
                    if (!SaveManifest()) throw new IOException("Could not save the selected package mirror");
                }, progress, cancelled);
        }

        static bool IsDirectAccess(string accessType)
        {
            return string.Equals(accessType, "Direct", StringComparison.OrdinalIgnoreCase);
        }

        bool CanRefreshUnlock(DlItem job)
        {
            if (job == null || IsDirectAccess(job.AccessType)) return false;
            if (job.AccessType == "Cloud") return CloudCatalog.CanRenew(_cfg, job.HosterUrl);
            AppSettings cfg = _cfg;
            return cfg != null && cfg.UseUnlockProvider &&
                UnlockProviders.IsEnabled(cfg, job.ResolvedProviderId) &&
                !string.IsNullOrEmpty(job.HosterUrl);
        }

        string RenewJobLink(DlItem job, int attempt, Func<bool> cancel)
        {
            Action<string> progress = text => { lock (_lock) { if (job.AttemptId == attempt) job.StatusText = text; } };
            if (job.AccessType == "Cloud")
            {
                string provider = CloudCatalog.OwningProvider(job.HosterUrl);
                string resolved = CloudCatalog.Resolve(_cfg, job.HosterUrl, progress, cancel);
                lock (_lock) if (job.AttemptId == attempt) job.ResolvedProviderId = provider;
                return resolved;
            }
            if (string.IsNullOrEmpty(job.ResolvedProviderId))
                throw new InvalidOperationException("The original link service is not known; refusing to mix providers during link renewal");
            return UnlockProviders.RefreshSameProvider(_cfg, job.HosterUrl, job.ResolvedProviderId, progress, cancel);
        }

        static bool IsAuthExpiry(Exception ex)
        {
            return DownloadLinkRecovery.IsExpired(ex);
        }
    }
}
