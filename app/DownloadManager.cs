using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal enum DlState
    {
        Queued,
        Resolving,
        Downloading,
        Finalizing,
        Paused,
        Completed,
        Failed,
        Canceled,
        Installing,
        Submitted,
        Installed
    }

    /// <summary>Live download telemetry for nerd stats UI. No tokens/URLs with secrets.</summary>
    internal sealed class NerdTelemetry
    {
        public const int Cap = 72;
        public readonly double[] Mbps = new double[Cap];
        public int Count;
        public int Write;
        public string TitleId = "";
        public string FileName = "";
        public string CdnHost = "";
        public string RegionHint = "";
        public string TransferMode = "";
        public long Done;
        public long Total;
        public bool Finalizing;
        public double MbpsInstant;
        public double MbpsAvg;
        public double Stability01 = 1;
        public int StallEvents;
        public int DropEvents;
        public string StabilityLabel = "—";

        public void Reset()
        {
            Count = 0; Write = 0;
            TitleId = FileName = CdnHost = RegionHint = TransferMode = "";
            Done = Total = 0;
            Finalizing = false;
            MbpsInstant = MbpsAvg = 0;
            Stability01 = 1;
            StallEvents = DropEvents = 0;
            StabilityLabel = "idle";
        }

        public void PushMbps(double mbps)
        {
            if (mbps < 0) mbps = 0;
            Mbps[Write] = mbps;
            Write = (Write + 1) % Cap;
            if (Count < Cap) Count++;
            MbpsInstant = mbps;
            double sum = 0, sum2 = 0;
            int n = Count;
            for (int i = 0; i < n; i++)
            {
                double v = Mbps[i];
                sum += v;
                sum2 += v * v;
            }
            MbpsAvg = n > 0 ? sum / n : 0;
            if (n < 4 || MbpsAvg < 0.05)
            {
                Stability01 = 1;
                StabilityLabel = n < 4 ? "warming" : "idle";
                return;
            }
            double var = Math.Max(0, (sum2 / n) - (MbpsAvg * MbpsAvg));
            double std = Math.Sqrt(var);
            double cov = std / Math.Max(0.05, MbpsAvg);
            Stability01 = Math.Max(0, Math.Min(1, 1.0 - cov));
            if (Stability01 >= 0.82) StabilityLabel = "stable";
            else if (Stability01 >= 0.55) StabilityLabel = "jitter";
            else StabilityLabel = "unstable";
        }

        public double SampleAt(int age)
        {
            if (Count <= 0) return 0;
            int idx = (Write - 1 - age + Cap * 4) % Cap;
            if (age >= Count) return 0;
            return Mbps[idx];
        }
    }

    internal sealed class DlItem
    {
        public string Id;
        public string TitleId;
        public string Name;
        public string Kind;
        public string Label;
        public string HosterUrl;
        /// <summary>Package Source provenance snapshot. Empty values identify a legacy queue row.</summary>
        public string SourceId = "";
        public string SourceVersion = "";
        public string PackageVersion = "";
        public string CandidateId = "";
        /// <summary>Direct, HosterLanding, or Unknown. Empty is a legacy Unknown value.</summary>
        public string AccessType = "";
        public string MirrorCandidates = "";
        public string FanOutPendingPaths = "";
        public string ArchiveVolumes = "";
        public string ArchivePassword = "";
        public string ArchivePasswords = "";
        public string ContainerFormat = "";
        public bool LocalSource;
        public string LocalSourceFingerprint = "";
        public string InstallAfterId = "";
        public bool InstallAfterConfirmed;
        public bool BgftLocalInstall;
        // Persisted as resident_archive for compatibility; V4 PKGs also let the
        // resident own installation through to BGFT acknowledgement.
        public bool ResidentArchive;
        public bool ResidentStaged;
        public bool ResidentRemovePending;
        public bool RemoveRequested;
        public bool ResidentAutoInstall;
        public string ResidentGeneration;
        public string ResolvedProviderId = "";
        internal bool ForegroundTransfer;
        internal string BackgroundWaitLoggedReason;
        public bool InstallOrderReady;
        public string ExpectedSha256 = "";
        public long ExpectedByteSize;
        public string ExpectedContentId = "";
        public string SourcePageUrl = "";
        public string ExpiresUtc = "";
        public string SourceAttribution = "";
        public string ImageUrl;
        public string DestPath;
        public DlState State;
        public long Done;
        public long Total;
        public double BytesPerSec;
        public int EtaSeconds;
        internal long StatsAt, StatsDone, StatsTotal, StatsAdvancedAt, StatsObservedDone;
        internal bool StatsInitialized;
        internal LiveTransferMeter LiveMeter;
        internal double[] RateSamples;
        internal long BgftRejectedCompletionTotal;
        internal string StatsPhase;
        internal int StatsAttempt;
        public string Error;
        public string StatusText;
        /// <summary>CDN hostname only (no path/query).</summary>
        public string CdnHost;
        public string TransferMode;
        public bool InstallConfirmed;
        // Accepted installation without package-level completion proof. Cancellation
        // may stop queue tracking, but must not delete input still held by the PS4.
        public bool InstallSubmitted;
        public bool Background;
        public bool BgftLoopback;
        public bool BgftResident;
        public bool BgftDirect;
        public bool BgftLoopbackServed;
        public int BgftTaskId = -1;
        public string BgftContentId;
        public int BgftSubType;
        public int BgftPollFailures;
        public int BgftFeederMisses;
        /// <summary>Preflight Content-Range total for web BGFT (must match transfer).</summary>
        public long BgftExpectedSize;
        public bool BgftTitlePresenceKnown;
        public bool BgftTitlePresentAtStart;
        public bool BgftDownloadPhaseConfirmed;
        // Transient (not persisted) — consecutive poll evidence for web BGFT.
        public int BgftDownloadCompletePolls;
        public int BgftCopyCompletePolls;
        public int BgftTitlePresentPolls;
        public int BgftInstalledProofPolls;
        /// <summary>Done bytes last time progress advanced (stall detection).</summary>
        public long BgftLastProgressDone;
        public int BgftStallPolls;
        /// <summary>Skip system BGFT for this attempt (after stall/fail → in-app).</summary>
        public bool ForceLocalInstall;
        public long RetryAfterUtcTicks;
        public int TransientHttpRetries;
        public long HttpRetryLastDurableBytes;
        public int InstallRetries;
        // Reuse a resolved URL for transient failures; never persist it in diagnostics.
        public string HttpRetryUrl;
        public int ResidentLinkRenewals;
        public long ResidentLastRenewalBytes;
        public bool ResidentRetryPending;
        /// <summary>Incremented each transfer claim; Refresh must not stomp a newer attempt.</summary>
        public int AttemptId;
        public bool PauseRequested;
        // Last accepted resident command wins over a status sampled before it.
        public bool? ResidentPauseDesired;
        public bool CancelRequested;

        // Provider preparation parking. A provider-side prepare must not own the one
        // transfer slot, so the row stays Queued with its provider progress while
        // the worker advances other games. Transient by design: a restart rebuilds
        // it from the account-bound durable pending record. Queue ownership and
        // poll cadence are persisted; signed URLs and provider credentials are not.
        internal bool ParkedForProvider;
        internal long ParkPollDueUtcTicks;
        internal int ParkPollCount;
        internal int ParkTransientFailures;
        internal string ParkProviderId = "";
        internal string ParkLastState = "";
        internal long ParkStartedUtcTicks;
    }

    /// <summary>Bounded transfer queue with independent, ordered package installation.</summary>
    internal sealed class DownloadManager
    {
        sealed class PendingLocalChild
        {
            public DlItem Item;
            public string ExtractedPath;
            public int Ordinal;
            public int Priority;
        }

        readonly object _lock = new object();
        readonly object _saveLock = new object();
        readonly object _packageInstallGate = new object();
        long _nextInstallHandoffAt, _nextJobStartAt;
        readonly object _bgftRefreshGate = new object();
        readonly object _transferRouteGate = new object();
        readonly LoopbackPkgServer _loopback = new LoopbackPkgServer();
        readonly List<DlItem> _items = new List<DlItem>();
        readonly AppSettings _cfg;
        const int WorkerCount = 1;
        bool _startupCleanupPending = true;
        long _startupCleanupAfter;
        readonly Thread[] _workers = new Thread[WorkerCount];
        readonly HashSet<string> _activeIds = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> _activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        volatile bool _run = true;
        long _saveVersion;
        long _savedVersion;
        int _installRevision;
        public int InstallRevision { get { lock (_lock) return _installRevision; } }
        int _residentPreparationAttempted;
        int _parkPollBusy;
        int _parkTimerStarted;
        Timer _parkPollTimer;
        readonly NerdTelemetry _nerd = new NerdTelemetry();
        public NerdTelemetry Nerd { get { return _nerd; } }

        public DownloadManager(AppSettings cfg)
        {
            NetHttp.ConfigureDownloadTrace(BuildIdentity.SourceHash);
            _cfg = cfg;
            if (_cfg != null) _cfg.ApplyToNetHttp();
            LoadManifest();
            _startupCleanupAfter = TransferClockMs() + 5000;
            for (int i = 0; i < _workers.Length; i++)
            {
                int workerIndex = i;
                _workers[i] = new Thread(() => WorkerLoop(workerIndex))
                    { IsBackground = true, Name = "DlMgr-" + (i + 1) };
                try { _workers[i].Priority = ThreadPriority.BelowNormal; } catch (PlatformNotSupportedException) { }
                _workers[i].Start();
            }
            if (_items.Exists(item => item.ParkedForProvider)) EnsureParkPollTimer();
        }

        public bool IsBusyDownloading
        {
            get
            {
                lock (_lock)
                {
                    foreach (var i in _items)
                        if (i.State == DlState.Downloading || i.State == DlState.Resolving ||
                            i.State == DlState.Finalizing)
                            return true;
                }
                return false;
            }
        }

        public bool IsPipelineActive
        {
            get
            {
                // UI metadata refreshes must defer instead of waiting for disk/BGFT work.
                if (!Monitor.TryEnter(_lock)) return true;
                try
                {
                    return _activeIds.Count != 0 || _items.Exists(item => item.Background ||
                        item.State == DlState.Installing || item.State == DlState.Submitted);
                }
                finally { Monitor.Exit(_lock); }
            }
        }

        public string Enqueue(GameHit game, PkgLink link)
        {
            string message;
            return Enqueue(game, link, out message);
        }

        public string Enqueue(GameHit game, PkgLink link, out string message)
        {
            return Enqueue(game, link, "", "", "", "", "", "", out message);
        }

        /// <summary>Queues a complete immutable Package Source candidate snapshot.</summary>
        public string Enqueue(GameHit game, PackageCandidate candidate, out string message)
        {
            return Enqueue(game, candidate, null, out message);
        }

        public string Enqueue(GameHit game, PackageCandidate candidate, IList<PackageCandidate> mirrors, out string message)
        {
            if (candidate == null) throw new Exception("Bad package candidate");
            if (!string.IsNullOrWhiteSpace(candidate.ResolutionError))
                throw new Exception(candidate.ResolutionError);
            if (candidate.ExpectedByteSize.HasValue && candidate.ExpectedByteSize.Value < 0)
                throw new Exception("Invalid expected package size");
            if (!string.IsNullOrEmpty(candidate.ArchiveVolumes)) ArchiveVolumeSet.Decode(candidate.ArchiveVolumes);
            var link = PackageCandidateAdapter.ToPkgLink(candidate);
            string hash = NormalizeSha256(candidate.ExpectedSha256, true);
            string id;
            lock (_lock)
            {
                // Keep a worker from claiming the row between the legacy-compatible enqueue and
                // completion of the candidate snapshot.
                id = Enqueue(game, link, candidate.SourceId, candidate.SourceVersion,
                    candidate.CandidateId, candidate.AccessType.ToString(), hash,
                    candidate.SourceAttribution, out message);
            lock (_lock) { var queued = Find(id); if (queued != null && string.IsNullOrEmpty(queued.PackageVersion) && !string.IsNullOrEmpty(candidate.PackageVersion)) queued.PackageVersion = candidate.PackageVersion ?? ""; }
            SaveManifest();
                var item = Find(id);
                if (item != null)
                {
                    if (mirrors != null) item.MirrorCandidates = PackageMirrorFallback.Encode(candidate, mirrors);
                    if (string.IsNullOrEmpty(item.ArchiveVolumes)) item.ArchiveVolumes = candidate.ArchiveVolumes ?? "";
                    item.ArchivePassword = candidate.ArchivePassword ?? "";
                    item.ArchivePasswords = candidate.ArchivePasswords ?? "";
                    // The candidate identity overload above either creates a row or fills only
                    // missing provenance. Apply the same immutable rule to the remaining fields.
                    if (item.ExpectedByteSize <= 0 && candidate.ExpectedByteSize.HasValue)
                        item.ExpectedByteSize = candidate.ExpectedByteSize.Value;
                    if (string.IsNullOrEmpty(item.ExpectedContentId))
                        item.ExpectedContentId = candidate.ExpectedContentId ?? "";
                    if (string.IsNullOrEmpty(item.SourcePageUrl))
                        item.SourcePageUrl = candidate.SourcePageUrl ?? "";
                    if (string.IsNullOrEmpty(item.ExpiresUtc) && candidate.ExpiresUtc.HasValue)
                        item.ExpiresUtc = candidate.ExpiresUtc.Value.ToUniversalTime().ToString("o");
                    if (item.State == DlState.Completed)
                    {
                        string integrityError;
                        if (!VerifyCandidateFile(item, out integrityError))
                        {
                            DeleteDownloadFiles(item);
                            item.State = DlState.Failed;
                            item.Error = integrityError;
                            item.StatusText = "Candidate integrity check failed";
                            item.Done = item.Total = 0;
                        }
                    }
                }
            }
            SaveManifest();
            return id;
        }

        bool TryParkProviderPreparation(DlItem job, int attempt, out HashSet<string> unavailableProviders)
        {
            unavailableProviders = null;
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
                { unavailableProviders = new HashSet<string>(StringComparer.Ordinal) { UnlockProviders.AllDebridId }; return false; }
                throw;
            }
            lock (_lock) if (job.AttemptId != attempt || job.CancelRequested || job.PauseRequested) return true;
            if (poll.Kind == AllDebridClient.PreparedPollKind.Ready) return false;
            if (poll.Kind == AllDebridClient.PreparedPollKind.Rejected || poll.Kind == AllDebridClient.PreparedPollKind.Terminal)
            {
                if (UnlockProviders.HasSupportedAlternative(_cfg, new[] { job.HosterUrl }, UnlockProviders.AllDebridId))
                { unavailableProviders = new HashSet<string>(StringComparer.Ordinal) { UnlockProviders.AllDebridId }; return false; }
                throw new Exception(string.IsNullOrEmpty(poll.Error) ? "AllDebrid preparation failed" : poll.Error);
            }
            long nowTicks = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (job.AttemptId != attempt || job.CancelRequested || job.PauseRequested) return true;
                job.State = DlState.Queued;job.ParkedForProvider = true;job.ParkProviderId = UnlockProviders.AllDebridId;
                job.ParkStartedUtcTicks = nowTicks;job.ParkPollCount = 0;job.ParkTransientFailures = 0;
                job.ParkLastState = poll.ProviderState ?? "";
                job.ParkPollDueUtcTicks = nowTicks + TimeSpan.FromMilliseconds(AllDebridClient.PollDelayMilliseconds).Ticks;
                job.BytesPerSec = 0;job.EtaSeconds = 0;
                job.StatusText = "Preparing in AllDebrid · next check in 5s · other downloads continue";
                _nextJobStartAt = TransferClockMs();
            }
            SaveManifest();EnsureParkPollTimer();return true;
        }

        static bool AllDebridIsFirstProvider(AppSettings cfg, string hosterUrl)
        {
            if (cfg == null || !cfg.UseUnlockProvider || !cfg.HasAllDebrid || string.IsNullOrEmpty(hosterUrl) ||
                !UnlockProviders.IsEnabled(cfg, UnlockProviders.AllDebridId)) return false;
            string[] ranked = UnlockProviders.RankedProviderIds(cfg, hosterUrl);
            return ranked.Length > 0 && string.Equals(ranked[0], UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Enqueue with an immutable Package Source provenance snapshot. The string parameters
        /// keep the legacy queue independent of the source-engine assembly during migration.
        /// </summary>
        public string Enqueue(GameHit game, PkgLink link, string sourceId, string sourceVersion,
            string candidateId, string accessType, string expectedSha256, string sourceAttribution,
            out string message)
        {
            if (game == null || link == null) throw new Exception("Bad download");
            if (accessType == "Personal")
            {
                string locator = CloudCatalog.ImportLocator(link.Url);
                if (locator != null) { link.Url = locator; accessType = "Cloud"; }
            }
            message = "Queued " + link.Kind;
            string existingId = null;
            bool revived = false;
            bool changed = false;
            lock (_lock)
            {
                foreach (var existing in _items)
                    if (((sourceId == "personal" && existing.SourceId == "personal") ||
                        (string.Equals(existing.TitleId, game.TitleId, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(existing.Kind, link.Kind, StringComparison.OrdinalIgnoreCase))) &&
                        string.Equals(existing.HosterUrl, link.Url, StringComparison.Ordinal))
                    {
                        existingId = existing.Id;
                        if (!string.IsNullOrEmpty(game.ImageUrl) &&
                            !string.Equals(existing.ImageUrl, game.ImageUrl, StringComparison.Ordinal))
                        {
                            existing.ImageUrl = game.ImageUrl;
                            changed = true;
                        }
                        if (!(sourceId == "personal" && string.IsNullOrEmpty(game.TitleId)) && !string.IsNullOrEmpty(game.Name) &&
                            !string.Equals(existing.Name, game.Name, StringComparison.Ordinal))
                        {
                            existing.Name = game.Name;
                            changed = true;
                        }
                        // A legacy row may be rediscovered through a Package Source. Fill only
                        // absent provenance so an existing queue snapshot is never silently
                        // rewritten by a later source version.
                        if (string.IsNullOrEmpty(existing.SourceId) && !string.IsNullOrEmpty(sourceId))
                        { existing.SourceId = sourceId; changed = true; }
                        if (string.IsNullOrEmpty(existing.SourceVersion) && !string.IsNullOrEmpty(sourceVersion))
                        { existing.SourceVersion = sourceVersion; changed = true; }
                        if (string.IsNullOrEmpty(existing.CandidateId) && !string.IsNullOrEmpty(candidateId))
                        { existing.CandidateId = candidateId; changed = true; }
                        if (string.IsNullOrEmpty(existing.AccessType) && !string.IsNullOrEmpty(accessType))
                        { existing.AccessType = accessType; changed = true; }
                        if (string.IsNullOrEmpty(existing.ExpectedSha256) && !string.IsNullOrEmpty(expectedSha256))
                        { existing.ExpectedSha256 = expectedSha256; changed = true; }
                        if (string.IsNullOrEmpty(existing.SourceAttribution) && !string.IsNullOrEmpty(sourceAttribution))
                        { existing.SourceAttribution = sourceAttribution; changed = true; }
                        if (existing.State == DlState.Canceled || existing.State == DlState.Failed ||
                            existing.State == DlState.Installed)
                        {
                            if (existing.Background && existing.ResidentArchive)
                            {
                                ResidentDownloadService.MarkFailed(existing.Id);
                                existing.CancelRequested = true;
                                existing.StatusText = "Stopping resident download before retry";
                                message = existing.StatusText;
                                changed = true;
                                break;
                            }
                            if (existing.Background)
                            {
                                int activeTask;
                                string cancelError;
                                if (!PkgInstaller.CancelBackground(existing.BgftTaskId, existing.BgftContentId,
                                    existing.BgftSubType, out activeTask, out cancelError))
                                {
                                    existing.BgftTaskId = activeTask;
                                    existing.StatusText = cancelError;
                                    changed = true;
                                    break;
                                }
                            }
                            ClearBackground(existing);
                            if (_activeIds.Contains(existing.Id)) existing.AttemptId++;
                            existing.PauseRequested = false;
                            existing.CancelRequested = false;
                            existing.State = DlState.Queued;
                            existing.TransientHttpRetries = 0;
                            existing.HttpRetryUrl = null;
                            existing.RetryAfterUtcTicks = 0;
                            existing.InstallConfirmed = false;
                            existing.InstallOrderReady = false;
                            existing.Error = null;
                            string partial = existing.DestPath + ".part";
                            if (File.Exists(partial))
                                existing.Done = new FileInfo(partial).Length;
                            else
                            {
                                existing.Done = 0;
                                existing.Total = 0;
                            }
                            existing.StatusText = existing.Done > 0
                                ? "Queued to resume " + Human(existing.Done) : "Queued";
                            message = existing.Done > 0 ? "Queued to resume" : "Queued for download";
                            revived = true;
                        }
                        else if (existing.State == DlState.Completed)
                        {
                            if (File.Exists(existing.DestPath) &&
                                PackageArchive.Detect(existing.DestPath) != PackageObjectKind.Pkg)
                            {
                                existing.State = DlState.Queued;
                                existing.StatusText = "Downloaded object ready for processing";
                                existing.Error = null;
                                revived = true;
                                message = existing.StatusText;
                                break;
                            }
                            string staleError;
                            PkgContentKind staleKind;
                            string staleKindName;
                            string staleContentId;
                            string staleTitleId;
                            long staleSize;
                            if (TryValidateLocalPackage(existing, out staleKind, out staleKindName,
                                out staleContentId, out staleTitleId, out staleSize, out staleError))
                                message = "Package already downloaded";
                            else
                            {
                                DiscardInvalidDownload(existing, staleError ?? "Previous file was invalid");
                                existing.State = DlState.Queued;
                                existing.StatusText = "Queued (replaced invalid file)";
                                existing.Error = null;
                                revived = true;
                                message = "Previous file was invalid — queued again";
                            }
                        }
                        else if (existing.State == DlState.Paused)
                            message = "Already paused — CROSS resumes";
                        else if (existing.State == DlState.Downloading || existing.State == DlState.Resolving)
                            message = "Already downloading";
                        else if (existing.State == DlState.Finalizing)
                            message = "Package is finalizing";
                        else if (existing.State == DlState.Installing)
                            message = "Package is installing";
                        else if (existing.State == DlState.Submitted)
                            message = "Already sent to PS4 — verification pending";
                        else if (existing.State == DlState.Queued)
                            message = "Already queued";
                        break;
                    }
            }
            if (existingId != null)
            {
                if (revived || changed) SaveManifest();
                return existingId;
            }

            string safe = game.TitleId + "_" + link.Kind + "_" + UrlTag(link.Url) + ".pkg";
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            string dest = Path.Combine(AppSettings.DownloadDir, safe);
            string id = game.TitleId + "_" + link.Kind + "_" + DateTime.UtcNow.Ticks.ToString("x");

            var item = new DlItem
            {
                Id = id,
                TitleId = game.TitleId,
                Name = game.Name,
                ImageUrl = game.ImageUrl,
                Kind = link.Kind,
                Label = link.Label ?? link.Kind,
                HosterUrl = link.Url,
                SourceId = sourceId ?? "",
                SourceVersion = sourceVersion ?? "",
                CandidateId = candidateId ?? "",
                AccessType = accessType ?? "",
                ExpectedSha256 = expectedSha256 ?? "",
                SourceAttribution = sourceAttribution ?? "",
                DestPath = dest,
                State = DlState.Queued,
                StatusText = "Queued",
                BgftTaskId = -1
            };
            if (File.Exists(dest))
            {
                PkgValResult vr;
                string vd;
                if (PackageArchive.Detect(dest) != PackageObjectKind.Pkg)
                {
                    item.Done = item.Total = new FileInfo(dest).Length;
                    item.StatusText = "Downloaded object ready for processing";
                }
                else if (PkgValidator.TryValidateDownload(dest, game.TitleId, link.Kind, out vr, out vd))
                {
                    item.State = DlState.Completed;
                    item.Done = item.Total = new FileInfo(dest).Length;
                    item.StatusText = "Valid PKG on disk";
                }
                else
                {
                    item.State = DlState.Failed;
                    item.Error = vd;
                    item.StatusText = "Corrupt on disk — re-queue";
                    DeleteDownloadFiles(dest);
                    item.State = DlState.Queued;
                    item.StatusText = "Queued (replaced bad file)";
                }
            }
            lock (_lock)
            {
                foreach (var existing in _items)
                {
                    if (!SamePath(existing.DestPath, dest)) continue;
                    message = "Package already queued";
                    return existing.Id;
                }
                _items.Insert(0, item);
            }
            SaveManifest();
            return id;
        }

        public int QueueLocalFiles(IList<string> paths, out string error)
        {
            error = null;
            if (paths == null || paths.Count == 0) { error = "Select a PKG, ZIP or first RAR volume"; return 0; }
            if (paths.Count > 256) { error = "Select up to 256 files at a time"; return 0; }
            var pending = new List<DlItem>();
            var problems = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                try
                {
                    string canonical;
                    if (!LocalInstallSource.TryNormalizePath(path, out canonical)) throw new IOException("USB paths cannot contain parent links, colons, or names longer than 240 UTF-8 bytes");
                    if (!seen.Add(canonical)) continue;
                    LocalInstallSource source = LocalInstallSource.Read(canonical, true);
                    pending.Add(new DlItem {
                        Id = "usb_" + Guid.NewGuid().ToString("N"), TitleId = source.TitleId,
                        Name = source.Name, ImageUrl = source.ImagePath, Kind = source.Kind,
                        Label = "USB · " + Path.GetFileName(canonical), DestPath = canonical,
                        HosterUrl = "local:" + source.Fingerprint, SourceId = "local-usb", AccessType = "Local",
                        CandidateId = source.Fingerprint, LocalSource = true, LocalSourceFingerprint = source.Fingerprint,
                        ExpectedContentId = source.ContentId, ExpectedByteSize = source.Size,
                        PackageVersion = source.Version, Total = source.Size, Done = source.Size,
                        State = DlState.Queued, StatusText = "Queued for USB installation; source file retained"
                    });
                }
                catch (Exception ex)
                {
                    string label;
                    try { label = Path.GetFileName(path ?? ""); } catch { label = "Selected file"; }
                    if (problems.Count < 5) problems.Add(label + ": " + ex.Message);
                }
            }
            pending.Sort((a, b) => {
                if ((a.Kind == "archive") != (b.Kind == "archive")) return a.Kind == "archive" ? 1 : -1;
                int title = string.Compare(a.TitleId, b.TitleId, StringComparison.OrdinalIgnoreCase);
                if (title != 0) return title;
                if (InstallPrecedes(a, b)) return -1;
                if (InstallPrecedes(b, a)) return 1;
                return string.Compare(a.DestPath, b.DestPath, StringComparison.Ordinal);
            });
            var added = new List<DlItem>(); int duplicates = 0;
            lock (_lock)
            {
                foreach (DlItem item in pending)
                {
                    bool duplicate = false;
                    foreach (DlItem existing in _items)
                        if (SamePath(existing.DestPath, item.DestPath) ||
                            (existing.LocalSource && !string.IsNullOrEmpty(item.ExpectedContentId) &&
                                existing.ExpectedContentId == item.ExpectedContentId && existing.LocalSourceFingerprint == item.LocalSourceFingerprint))
                        { duplicate = true; break; }
                    if (duplicate) { duplicates++; continue; }
                    DlItem previous = null;
                    foreach (DlItem other in _items)
                    {
                        if (other.State == DlState.Canceled || string.IsNullOrEmpty(item.TitleId) ||
                            !string.Equals(other.TitleId, item.TitleId, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!InstallPrecedes(other, item) && !(other.LocalSource && !InstallPrecedes(item, other) && InstallRank(other.Kind) == 1 &&
                            InstallRank(item.Kind) == 1 && other.PackageVersion == item.PackageVersion)) continue;
                        if (previous == null || InstallPrecedes(previous, other)) previous = other;
                    }
                    if (previous != null) { item.InstallAfterId = previous.Id; item.InstallAfterConfirmed = InstallPrerequisiteReady(previous); }
                    _items.Insert(0, item); added.Add(item);
                }
                if (added.Count > 0 && !SaveManifest())
                {
                    foreach (DlItem item in added) _items.Remove(item);
                    error = "USB queue could not be saved; original files are unchanged"; return 0;
                }
            }
            if (duplicates > 0) problems.Add(duplicates + " file(s) already in the queue");
            if (problems.Count > 0) error = string.Join("; ", problems.ToArray());
            return added.Count;
        }

        public bool CanChangeStaging()
        {
            lock (_lock)
            {
                foreach (var item in _items)
                    if ((item.State != DlState.Installed && item.State != DlState.Canceled) ||
                        item.ResidentRemovePending || ResidentDownloadService.HasJob(item.Id)) return false;
                return _activeIds.Count == 0 && !ResidentDownloadService.HasPendingOwnership;
            }
        }

        internal static string[] StorageRoots()
        { var roots = new List<string> { AppSettings.StagingRoot("ps4"), Path.Combine(AppSettings.DataDir, "resident/archives") };
            for (int i=0;i<8;i++) roots.Add(AppSettings.StagingRoot("/mnt/usb"+i)); return roots.ToArray(); }

        static bool IsPathClaimedByJob(DlItem job, string full)
        {
            if (job == null || string.IsNullOrEmpty(full)) return false;
            if (job.LocalSource && SamePath(job.DestPath, full)) return true;
            if (!string.IsNullOrEmpty(job.FanOutPendingPaths))
            {
                string[] paths = job.FanOutPendingPaths.Split(new[] { '\n' },
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (string p in paths)
                    if (SamePath(p, full)) return true;
                string extractRoot;
                try { extractRoot = Path.GetFullPath(Path.Combine(AppSettings.DownloadDir, ".extract")) + Path.DirectorySeparatorChar; }
                catch { extractRoot = null; }
                if (extractRoot != null && full.StartsWith(extractRoot, StringComparison.OrdinalIgnoreCase)) return true;
            }
            string[] family = { job.DestPath, job.DestPath + ".part", job.DestPath + ".resume", job.DestPath + ".ranges", job.DestPath + ".map", job.DestPath + ".map.tmp", job.DestPath + ".sha256-ok", job.DestPath + ".sha256-ok.tmp" };
            foreach (string p in family)
                if (!string.IsNullOrEmpty(p) && SamePath(p, full))
                    return job.Background || job.State == DlState.Queued || job.State == DlState.Downloading || job.State == DlState.Finalizing ||
                        job.State == DlState.Installing || job.State == DlState.Submitted ||
                        job.State == DlState.Resolving || job.State == DlState.Paused;
            if (job.Background)
            {
                string resident;
                try { resident = Path.GetFullPath(Path.Combine(AppSettings.DataDir, "resident", "archives")) + Path.DirectorySeparatorChar; }
                catch { resident = null; }
                if (resident != null && full.StartsWith(resident, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        public bool TryDeleteStorageFile(string path, out string detail)
        {
            detail = "File removed";
            lock (_lock)
            {
                try {
                    string root = null;
                    string full = Path.GetFullPath(path);
                    foreach (string candidate in StorageRoots()) {
                        string prefix = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                        if (full.StartsWith(prefix, StringComparison.Ordinal)) { root = prefix; break; }
                    }
                    if (root == null || !File.Exists(full))
                    { detail = "File is missing or outside downloaded storage"; return false; }
                    if (full.EndsWith(".xfer-lock", StringComparison.OrdinalIgnoreCase))
                    { detail = "Transfer ownership records are kept for safe resume"; return false; }
                    if (full.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    { detail = "Installation journals are kept for recovery"; return false; }
                    // Extraction/resident journals can own files not yet represented by a child row.
                    // Only the file's actual owner blocks deletion — an unrelated pending
                    // job must not veto cleanup of an orphan.
                    foreach (var job in _items)
                        if (IsPathClaimedByJob(job, full))
                        { detail = "File is in use by '" + (job.Name ?? job.TitleId) + "' (" + job.State + ")"; return false; }
                    for (string parent = full; parent != null && parent.Length >= root.Length - 1; parent = Path.GetDirectoryName(parent))
                        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                        { detail = "Linked files cannot be deleted here"; return false; }
                    File.Delete(full);
                    return true;
                } catch (Exception ex) { detail = "Delete failed: " + ex.Message; return false; }
            }
        }

        public List<DlItem> Snapshot()
        {
            lock (_lock) return CopySnapshot();
        }

        public bool TrySnapshot(out List<DlItem> snapshot)
        {
            snapshot = null;
            if (!Monitor.TryEnter(_lock)) return false;
            try { snapshot = CopySnapshot(); return true; }
            finally { Monitor.Exit(_lock); }
        }

        // Caller holds _lock. No filesystem or native calls belong in a UI snapshot.
        List<DlItem> CopySnapshot()
        {
            long now = TransferClockMs();
                var list = new List<DlItem>(_items.Count);
                foreach (var i in _items)
                {
                    bool freshStats = HasCurrentTransferStats(i, now);
                    list.Add(new DlItem
                    {
                        Id = i.Id,
                        TitleId = i.TitleId,
                        Name = i.Name,
                        ImageUrl = i.ImageUrl,
                        Kind = i.Kind,
                        Label = i.Label,
                        HosterUrl = i.HosterUrl,
                        SourceId = i.SourceId,
                        SourceVersion = i.SourceVersion,
                    PackageVersion = i.PackageVersion,
                        CandidateId = i.CandidateId,
                        AccessType = i.AccessType,
                        MirrorCandidates = i.MirrorCandidates,
                        ArchiveVolumes = i.ArchiveVolumes,
                        ArchivePassword = i.ArchivePassword,
                        ArchivePasswords = i.ArchivePasswords,
                        LocalSource = i.LocalSource,
                        LocalSourceFingerprint = i.LocalSourceFingerprint,
                        InstallAfterId = i.InstallAfterId,
                        InstallAfterConfirmed = i.InstallAfterConfirmed,
                        BgftLocalInstall = i.BgftLocalInstall,
                        ResidentArchive = i.ResidentArchive,
                        ContainerFormat = i.ContainerFormat,
                        ResidentStaged = i.ResidentStaged,
                        ResidentRemovePending = i.ResidentRemovePending,
                        RemoveRequested = i.RemoveRequested,
                        ResidentAutoInstall = i.ResidentAutoInstall,
                        ResidentGeneration = i.ResidentGeneration,
                        ResolvedProviderId = i.ResolvedProviderId,
                        InstallOrderReady = i.InstallOrderReady,
                        ExpectedSha256 = i.ExpectedSha256,
                        ExpectedByteSize = i.ExpectedByteSize,
                        ExpectedContentId = i.ExpectedContentId,
                        SourcePageUrl = i.SourcePageUrl,
                        ExpiresUtc = i.ExpiresUtc,
                        SourceAttribution = i.SourceAttribution,
                        DestPath = i.DestPath,
                        State = i.State,
                        Done = i.Done,
                        Total = i.Total,
                        BytesPerSec = freshStats ? i.BytesPerSec : 0,
                        RateSamples = i.LiveMeter == null ? null : i.LiveMeter.CopyRates(),
                        EtaSeconds = freshStats ? i.EtaSeconds : 0,
                        Error = i.Error,
                        StatusText = i.StatusText,
                        InstallConfirmed = i.InstallConfirmed,
                        InstallSubmitted = i.InstallSubmitted,
                        Background = i.Background,
                        ParkedForProvider = i.ParkedForProvider,
                        BgftLoopback = i.BgftLoopback,
                        BgftResident = i.BgftResident,
                        BgftLoopbackServed = i.BgftLoopbackServed,
                        ForceLocalInstall = i.ForceLocalInstall,
                        BgftTaskId = i.BgftTaskId,
                        BgftContentId = i.BgftContentId,
                        BgftSubType = i.BgftSubType
                    });
                }
                return list;
        }

        long _lastReconcileAt;

        /// <summary>Completed requires DestPath. Installed may have no PKG (deleted after install).</summary>
        public void ReconcileLocalFiles(bool force)
        {
            long now = TransferClockMs();
            if (!force && now - _lastReconcileAt < 2000) return;
            _lastReconcileAt = now;
            bool changed = false;
            lock (_lock)
            {
                foreach (var item in _items)
                {
                    // Rendering a snapshot must never hash, delete, or take ownership
                    // from a transfer/installer. Their worker owns reconciliation.
                    if (item.Background || item.ResidentArchive || item.ResidentRemovePending ||
                        _activeIds.Contains(item.Id) || item.State == DlState.Installing ||
                        item.State == DlState.Submitted) continue;
                    if (item.State == DlState.Installed && item.InstallConfirmed) continue;
                    if (item.State != DlState.Completed && item.State != DlState.Installed) continue;
                    if (string.IsNullOrEmpty(item.DestPath)) continue;
                    if (File.Exists(item.DestPath))
                    {
                        if (item.State == DlState.Installed)
                        {
                            item.State = DlState.Completed; item.InstallOrderReady = false;
                            item.StatusText = "File retained; verify before installation"; changed = true;
                        }
                        continue;
                    }
                    item.State = DlState.Failed; item.InstallConfirmed = item.InstallOrderReady = false;
                    item.Error = "PKG missing on disk";
                    item.StatusText = "PKG missing; reconnect the staging drive or retry";
                    changed = true;
                }
            }
            if (changed) SaveManifest();
        }


        public bool EnsureLocalPackage(string id, out string error)
        {
            error = null;
            ReconcileLocalFiles(true);
            lock (_lock)
            {
                var it = Find(id);
                if (it == null)
                {
                    error = "Download not found";
                    return false;
                }
                if (it.State == DlState.Installed)
                {
                    error = "Already installed — SQUARE remove, then re-queue to download again";
                    return false;
                }
                if (string.IsNullOrEmpty(it.DestPath) || !File.Exists(it.DestPath))
                {
                    if (it.State == DlState.Completed)
                    {
                        it.State = DlState.Failed;
                        it.Error = "PKG missing on disk";
                        it.StatusText = "PKG missing — re-download";
                        it.Done = 0;
                        SaveManifest();
                    }
                    error = "PKG file missing";
                    return false;
                }
                string localError;
                PkgContentKind localKind;
                string localKindName;
                string localContentId;
                string localTitleId;
                long localSize;
                if (!TryValidateLocalPackage(it, out localKind, out localKindName,
                    out localContentId, out localTitleId, out localSize, out localError))
                {
                    DiscardInvalidDownload(it, localError ?? "Invalid PKG on disk");
                    it.State = DlState.Failed;
                    it.StatusText = "Invalid file removed — CROSS re-downloads";
                    SaveManifest();
                    error = "PKG was incomplete or corrupt and was removed";
                    return false;
                }
                return true;
            }
        }

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

        public void TogglePause(string id)
        {
            lock (_lock)
            {
                var it = Find(id);
                if (it == null) return;
                if (it.Background && it.ResidentArchive)
                {
                    if (it.State == DlState.Failed)
                    {
                        if (it.ResidentStaged)
                        {
                            it.ResidentRetryPending = true;
                            it.ResidentLinkRenewals = 0; it.ResidentLastRenewalBytes = 0;
                            it.CancelRequested = it.PauseRequested = false;
                            it.State = DlState.Resolving;
                            it.StatusText = "Stopping previous attempt; verified files retained for retry";
                            if (SaveManifest()) ResidentDownloadService.Release(it.Id);
                            return;
                        }
                        ResidentDownloadService.MarkFailed(it.Id);
                        it.CancelRequested = true;
                        it.StatusText = "Stopping previous resident task before retry";
                        SaveManifest(); return;
                    }
                    bool pause = !(it.ResidentPauseDesired ?? (it.State == DlState.Paused));
                    if (!ResidentDownloadService.SetPaused(it.Id, pause))
                    {
                        it.StatusText = "Background command could not be saved: " + ResidentDownloadService.LastError;
                        SaveManifest(); return;
                    }
                    it.ResidentPauseDesired = pause;
                    it.PauseRequested = pause;
                    it.State = pause ? DlState.Paused : DlState.Downloading;
                    it.StatusText = pause ? "Background download pause requested" : "Background download resume requested";
                    SaveManifest(); return;
                }
                if (it.Background)
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
                            if (it.BgftResident) ResidentDownloadService.SetPaused(it.Id, true);
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
                            if (it.BgftResident) ResidentDownloadService.SetPaused(it.Id, false);
                            it.State = DlState.Downloading;
                            it.StatusText = "Background download resumed";
                        }
                        else
                            it.StatusText = error;
                    }
                    else if (it.State == DlState.Failed)
                    {
                        if (PkgInstaller.CancelBackground(it.BgftTaskId, it.BgftContentId, it.BgftSubType,
                            out activeTask, out error))
                        {
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
                    return;
                }
                if (it.State == DlState.Queued)
                {
                    it.State = DlState.Paused;
                    it.PauseRequested = true;
                    it.HttpRetryUrl = null;
                    it.RetryAfterUtcTicks = 0;
                    it.StatusText = "Paused · partial file kept";
                    SaveManifest();
                    return;
                }
                if (!it.PauseRequested && (it.State == DlState.Downloading || it.State == DlState.Resolving ||
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
                    it.Error = null;
                    it.PauseRequested = false;
                    it.CancelRequested = false;
                    it.TransientHttpRetries = 0;
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
        }

        public bool Cancel(string id, out string error)
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
                    if (!ResidentDownloadService.TryCancel(it.Id, out error))
                    {
                        it.StatusText = "Cancellation saved; waiting to reach the background worker";
                        error = null;
                    }
                    return true;
                }
                if (it.Background || (it.State == DlState.Submitted && it.BgftTaskId >= 0))
                {
                    bool previous = it.CancelRequested;
                    it.CancelRequested = true;
                    if (it.State == DlState.Submitted) it.InstallSubmitted = true;
                    if (!SaveManifest())
                    { it.CancelRequested = previous; error = "Could not save the cancellation request"; return false; }
                    int activeTask;
                    string cancelError;
                    if (PkgInstaller.CancelBackground(it.BgftTaskId, it.BgftContentId, it.BgftSubType,
                        out activeTask, out cancelError))
                    {
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
                if (_activeIds.Contains(it.Id) &&
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
                it.StatusText = it.InstallSubmitted ? "Stopped tracking installation; unconfirmed package retained" : "Canceled";
                DeleteDownloadFiles(it);
                SaveManifest();
                return true;
            }
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

        /// <summary>Drop a finished/failed/canceled row from the queue (history only; keeps installed PKGs).</summary>
        public bool Remove(string id, out string error)
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
                if (it.State == DlState.Submitted) it.InstallSubmitted = true;
                if (it.Background && (it.ResidentStaged || it.ResidentArchive))
                {
                    bool wasPending = it.ResidentRemovePending, wasCanceled = it.CancelRequested;
                    string previousStatus = it.StatusText;
                    it.ResidentRemovePending = true;
                    it.CancelRequested = true;
                    it.StatusText = "Removing background job; waiting for worker acknowledgement";
                    if (!SaveManifest())
                    {
                        it.ResidentRemovePending = wasPending; it.CancelRequested = wasCanceled;
                        it.StatusText = previousStatus; error = "Could not save the removal request"; return false;
                    }
                    ResidentDownloadService.Release(it.Id);
                    return true;
                }
                if ((it.Background || (it.State == DlState.Submitted && it.BgftTaskId >= 0)) &&
                    !_activeIds.Contains(it.Id))
                {
                    bool requested = it.RemoveRequested, canceled = it.CancelRequested;
                    it.RemoveRequested = it.CancelRequested = true;
                    if (!SaveManifest())
                    { it.RemoveRequested = requested; it.CancelRequested = canceled; error = "Could not save the removal request"; return false; }
                    int activeTask;
                    if (!PkgInstaller.CancelBackground(it.BgftTaskId, it.BgftContentId, it.BgftSubType,
                        out activeTask, out error))
                    {
                        it.BgftTaskId = activeTask;
                        it.StatusText = "Removal saved; waiting for PS4 acknowledgement";
                        error = null;
                        return true;
                    }
                    ClearBackground(it);
                    it.CancelRequested = false;
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
                // Queued/Paused: treat as remove-from-queue (cancel leftovers).
                if (it.State == DlState.Queued || it.State == DlState.Paused)
                {
                    if (it.Background || it.State == DlState.Submitted)
                    {
                        int activeTask;
                        string cancelError;
                        if (!PkgInstaller.CancelBackground(it.BgftTaskId, it.BgftContentId, it.BgftSubType,
                                out activeTask, out cancelError))
                        {
                            it.BgftTaskId = activeTask;
                            error = cancelError ?? "BGFT cancel failed";
                            return false;
                        }
                        ClearBackground(it);
                    }
                    if (!TryCleanupFanOutPending(it))
                    {
                        error = "Extracted package cleanup failed";
                        return false;
                    }
                    DeleteDownloadFiles(it);
                    _items.Remove(it);
                    SaveManifest();
                    return true;
                }
                if (it.Background && it.State == DlState.Failed)
                {
                    int activeTask;
                    string cancelError;
                    if (!PkgInstaller.CancelBackground(it.BgftTaskId, it.BgftContentId, it.BgftSubType,
                            out activeTask, out cancelError))
                    {
                        it.BgftTaskId = activeTask;
                        error = cancelError ?? "Cancel background first";
                        return false;
                    }
                    ClearBackground(it);
                }
                if (string.Equals(it.AccessType, "FanOutSource",
                    StringComparison.OrdinalIgnoreCase))
                {
                    DeleteDownloadFiles(it);
                    if (!it.LocalSource && File.Exists(it.DestPath))
                    {
                        error = "Downloaded source cleanup failed";
                        return false;
                    }
                    _items.Remove(it);
                    SaveManifest();
                    return true;
                }
                if (!TryCleanupFanOutPending(it))
                {
                    error = "Extracted package cleanup failed";
                    return false;
                }
                if (it.State == DlState.Failed || it.State == DlState.Canceled)
                    DeleteUnclaimedDownloadFiles(it);
                PreserveConfirmedDependency(it);
                _items.Remove(it);
                SaveManifest();
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
            var drop = new List<DlItem>();
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
                    if (it.Background)
                    {
                        skipped++;
                        continue;
                    }
                    if (!TryCleanupFanOutPending(it))
                    {
                        skipped++;
                        continue;
                    }
                    drop.Add(it);
                }
                foreach (var it in drop)
                {
                    if (it.State == DlState.Failed || it.State == DlState.Canceled)
                        DeleteUnclaimedDownloadFiles(it);
                    PreserveConfirmedDependency(it);
                    _items.Remove(it);
                }
                if (drop.Count > 0)
                    SaveManifest();
            }
            return drop.Count;
        }

        // Caller holds _lock until cleanup and removal finish, so a replacement
        // cannot claim the deterministic destination while old files are deleted.
        bool DeleteUnclaimedDownloadFiles(DlItem item)
        {
            if (item.LocalSource) return false;
            foreach (DlItem other in _items)
                if (!object.ReferenceEquals(other, item) && SamePath(other.DestPath, item.DestPath))
                    return false;
            DeleteDownloadFiles(item);
            return !File.Exists(item.DestPath) && !File.Exists(item.DestPath + ".part");
        }

        void PreserveConfirmedDependency(DlItem removed)
        {
            if (removed.State != DlState.Installed || !removed.InstallConfirmed) return;
            foreach (DlItem child in _items)
                if (!object.ReferenceEquals(child, removed) &&
                    string.Equals(child.InstallAfterId, removed.Id, StringComparison.Ordinal))
                    child.InstallAfterConfirmed = true;
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
        /// Confirmed install success. When deleteLocalPackage, removes DestPath so re-queue is clean.
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
                deleteLocalPackage = true; // Clean only this confirmed installation, never failed/pending jobs.
                bool deleted = false;
                // Save proof before deletion and retain ownership until cleanup finishes.
                // A retry cannot replace this attempt between the save and file deletion.
                bool proofSaved = !deleteLocalPackage || SaveManifest();
                if (deleteLocalPackage && proofSaved && IsOwnedDownloadPath(it.DestPath))
                    deleted = DeleteUnclaimedDownloadFiles(it);
                if (deleteLocalPackage && !proofSaved)
                    it.StatusText = (msg ?? "Installed") +
                        "; PKG retained because history could not be saved";
                else if (deleteLocalPackage && !deleted)
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

        DlItem Find(string id)
        {
            foreach (var i in _items)
                if (i.Id == id) return i;
            return null;
        }

        static bool InstallPrerequisiteReady(DlItem item)
        {
            return item != null &&
                ((item.State == DlState.Installed && item.InstallConfirmed) ||
                 (item.State == DlState.Completed && item.InstallOrderReady && string.IsNullOrEmpty(item.Error)));
        }

        static bool TryInitialTitlePresence(PkgContentKind kind, string titleId, out bool present)
        {
            present = false;
            return kind == PkgContentKind.BaseGame && !string.IsNullOrEmpty(titleId) &&
                PkgInstaller.TryIsTitleInstalled(titleId, out present);
        }

        bool InstallDependencyReady(DlItem item)
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(item.InstallAfterId))
                {
                    DlItem previous = Find(item.InstallAfterId);
                    bool replacedPatch = CanReplaceFailedPatch(previous, item);
                    if (!replacedPatch && !item.InstallAfterConfirmed && !InstallPrerequisiteReady(previous))
                    {
                        item.StatusText = previous != null && (previous.State == DlState.Failed || !string.IsNullOrEmpty(previous.Error))
                            ? "Waiting: previous package failed · retry it first" : "Waiting for previous package installation";
                        return false;
                    }
                    if (!replacedPatch) item.InstallAfterConfirmed = true;
                }
                foreach (var other in _items)
                {
                    if (other == item || !string.Equals(item.TitleId, other.TitleId, StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrEmpty(item.TitleId) || InstallPrerequisiteReady(other) ||
                        other.State == DlState.Canceled || CanReplaceFailedPatch(other, item)) continue;
                    if (InstallPrecedes(other, item))
                    {
                        item.StatusText = other.State == DlState.Failed || !string.IsNullOrEmpty(other.Error)
                            ? "Waiting: a required package failed" : "Waiting for this title's earlier package";
                        return false;
                    }
                }
                return true;
            }
        }

        static int InstallRank(string kind)
        {
            int sub = PkgValidator.BgftSubTypeForKind(kind);
            return sub == 6 ? 0 : sub == 8 ? 1 : 2;
        }

        static bool InstallPrecedes(DlItem previous, DlItem next)
        {
            int before = InstallRank(previous.Kind), after = InstallRank(next.Kind);
            if (before != after) return before < after;
            if (before != 1) return false;
            Version a, b;
            if (Version.TryParse((previous.PackageVersion ?? "").TrimStart('v', 'V'), out a) &&
                Version.TryParse((next.PackageVersion ?? "").TrimStart('v', 'V'), out b) && a != b)
                return a < b;
            // Unknown catalog versions still must not let an ordinary update
            // overwrite a backport that was queued before it.
            return !string.Equals(previous.Kind, "backport", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(next.Kind, "backport", StringComparison.OrdinalIgnoreCase);
        }

        // A failed ordinary full update is not a prerequisite for its matching
        // backport. The backport still passes actual base/content/key validation
        // before installation; no installed state is inferred for the failed row.
        bool CanReplaceFailedPatch(DlItem previous, DlItem next)
        {
            if (previous == null || next == null || previous.Background ||
                previous.InstallSubmitted || InstallRank(previous.Kind) != 1 ||
                string.Equals(previous.Kind, "backport", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(next.Kind, "backport", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(next.TitleId) ||
                !string.Equals(previous.TitleId, next.TitleId, StringComparison.OrdinalIgnoreCase) ||
                !(previous.State == DlState.Failed || (previous.State == DlState.Completed && !string.IsNullOrEmpty(previous.Error))))
                return false;
            if (_activeIds.Contains(previous.Id)) return false;
            Version a, b;
            if (!Version.TryParse((previous.PackageVersion ?? "").TrimStart('v', 'V'), out a) ||
                !Version.TryParse((next.PackageVersion ?? "").TrimStart('v', 'V'), out b) || a != b) return false;
            return string.IsNullOrEmpty(previous.ExpectedContentId) || string.IsNullOrEmpty(next.ExpectedContentId) ||
                string.Equals(previous.ExpectedContentId, next.ExpectedContentId, StringComparison.OrdinalIgnoreCase);
        }

        string ResidentDependencyId(DlItem item)
        {
            lock (_lock)
            {
                if (!item.InstallAfterConfirmed && !string.IsNullOrEmpty(item.InstallAfterId) &&
                    !CanReplaceFailedPatch(Find(item.InstallAfterId), item)) return item.InstallAfterId;
                DlItem latest = null;
                foreach (var other in _items)
                {
                    if (other == item || string.IsNullOrEmpty(item.TitleId) ||
                        !string.Equals(item.TitleId, other.TitleId, StringComparison.OrdinalIgnoreCase) ||
                        other.State == DlState.Canceled || InstallPrerequisiteReady(other) || CanReplaceFailedPatch(other, item)) continue;
                    if (!InstallPrecedes(other, item)) continue;
                    if (latest == null || InstallPrecedes(latest, other)) latest = other;
                }
                return latest == null ? "" : latest.Id;
            }
        }

        bool HasUnstartedPrerequisite(DlItem item)
        {
            lock (_lock)
            {
                string id = ResidentDependencyId(item);
                DlItem previous = string.IsNullOrEmpty(id) ? null : Find(id);
                return previous != null && previous.State == DlState.Queued && !previous.Background;
            }
        }

        bool CanInstallWithResidentDependency(string dependencyId)
        {
            lock (_lock)
            {
                if (string.IsNullOrEmpty(dependencyId)) return true;
                DlItem previous = Find(dependencyId);
                return previous != null && previous.ResidentAutoInstall && previous.Background;
            }
        }

        bool CanPublishResidentStaged(DlItem job, int attempt)
        {
            return object.ReferenceEquals(Find(job.Id), job) && job.AttemptId == attempt &&
                job.State == DlState.Downloading && job.Background && job.ResidentStaged &&
                !job.CancelRequested && !job.PauseRequested;
        }


        // Resident-owned automatic jobs may accept more durable metadata, but
        // local processing must wait until the resident gives up disk ownership.
        bool HasBlockingPipelineOwner(out bool residentBusy)
        {
            residentBusy = false;
            foreach (var item in _items)
            {
                if (item.Background && item.ResidentStaged && item.ResidentAutoInstall)
                { residentBusy = true; continue; }
                if ((item.Background && (item.State != DlState.Installed && item.State != DlState.Canceled && item.State != DlState.Paused)) ||
                    item.State == DlState.Installing || (item.State == DlState.Submitted && !item.InstallOrderReady)) return true;
            }
            return false;
        }

        internal static bool CanPrepareResidentHandoff(DlItem item, bool backgroundSelected)
        {
            return backgroundSelected && item != null && !item.ForceLocalInstall &&
                !item.ResidentRetryPending && string.IsNullOrEmpty(item.ArchiveVolumes) &&
                !string.Equals(item.AccessType, "FanOutSource", StringComparison.OrdinalIgnoreCase);
        }

        void WorkerLoop(int workerIndex)
        {
            long failureLoggedAt = -10000;
            while (_run)
            {
                try
                {
                if (Monitor.TryEnter(_bgftRefreshGate))
                {
                    try { RefreshBackgroundTasks(); }
                    finally { Monitor.Exit(_bgftRefreshGate); }
                }
                // Parked TorBox preparations are polled off-thread; the worker
                // itself never waits on a provider that has not published a file.
                PumpParkedPreparations();
                DlItem job = null;
                int attempt = 0;
                string activePath = null;
                lock (_lock)
                {
                    foreach (var pending in _items.ToArray())
                        if (pending.RemoveRequested && !pending.CancelRequested && pending.BgftTaskId < 0 &&
                            !pending.Background && !_activeIds.Contains(pending.Id) && pending.State != DlState.Installing)
                        {
                            if (!IsTerminal(pending.State) && pending.State != DlState.Submitted)
                                pending.State = DlState.Canceled;
                            string removalError;
                            if (!Remove(pending.Id, out removalError)) pending.StatusText = removalError;
                        }
                    bool residentBusy = false;
                    bool backgroundBusy = HasBlockingPipelineOwner(out residentBusy);
                    bool localPending = false;
                    foreach (var pending in _items)
                        if (pending.State == DlState.Queued && !pending.Background && File.Exists(pending.DestPath) &&
                            InstallDependencyReady(pending)) { localPending = true; break; }
                    for (int index = backgroundBusy || _activeIds.Count >= WorkerCount || TransferClockMs() < _nextJobStartAt
                        ? -1 : _items.Count - 1; index >= 0; index--)
                    {
                        var i = _items[index];
                        // Never claim a BGFT-owned row as a new foreground job.
                        string pathKey = NormalizePath(i.DestPath);
                        bool local = File.Exists(i.DestPath);
                        bool publishOnly = residentBusy && CanPrepareResidentHandoff(i, BackgroundSelected) &&
                            CanInstallWithResidentDependency(ResidentDependencyId(i));
                        bool dependencyAllowed = publishOnly ? !HasUnstartedPrerequisite(i) : InstallDependencyReady(i);
                        // One pure policy call, shared with the host regression, so a
                        // parked TorBox preparation is never claimed a second time and
                        // never blocks an independent game behind it.
                        if (QueueScheduler.CanClaim(i.ParkedForProvider, i.State == DlState.Queued, i.Background,
                            backgroundBusy, residentBusy, publishOnly, localPending, local, dependencyAllowed,
                            DateTime.UtcNow.Ticks, i.RetryAfterUtcTicks, TransferClockMs(), _nextJobStartAt,
                            _activeIds.Count, WorkerCount, _activePaths.Contains(pathKey)))
                        {
                            job = i;
                            _nextJobStartAt = TransferClockMs() + 3000;
                            _activeIds.Add(i.Id);
                            _activePaths.Add(pathKey);
                            activePath = pathKey;
                            i.PauseRequested = false;
                            i.CancelRequested = false;
                            i.AttemptId++;
                            attempt = i.AttemptId;
                            i.State = DlState.Resolving;
                            i.StatusText = "Preparing download; keep SSPI open until background handoff";
                            break;
                        }
                    }
                }
                if (job == null)
                {
                    ReconcileLocalFiles(false);
                    bool cleanup;
                    lock (_lock) cleanup = _startupCleanupPending && TransferClockMs() >= _startupCleanupAfter &&
                        _activeIds.Count == 0 && !_items.Exists(item => item.Background || item.State == DlState.Installing ||
                            item.State == DlState.Submitted);
                    if (cleanup) { _startupCleanupPending = false; CleanupFanOutStateOnStartup(); }
                    Thread.Sleep(500);
                    continue;
                }

                try { SaveManifest(); RunJob(job, attempt); }
                finally
                {
                    lock (_lock)
                    {
                        _activeIds.Remove(job.Id);
                        job.ForegroundTransfer = false;
                        if (activePath != null) _activePaths.Remove(activePath);
                    }
                }
                }
                catch (Exception ex)
                {
                    long now = TransferClockMs();
                    if (now - failureLoggedAt >= 10000) {
                        failureLoggedAt = now;
                        SspiLog.Write("download", "event=queue-worker-recovered exception=" + ex.GetType().Name);
                        Program.RecordFailure(ex);
                    }
                    Thread.Sleep(1000);
                }
            }
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
            unavailableProviders = null;
            DebridHostSupport.RefreshEnabled(_cfg);
            var urls = TorBoxPreparationUrls(job);
            if (urls.Count == 0 || urls.Exists(url => !TorBoxIsFirstProvider(_cfg, url))) return false;
            // A provider job is created at most once: TryParkOrPrepare reuses the
            // durable pending record and only polls when one already exists.
            TorBoxClient.PreparedPollResult poll;
            try
            {
                Func<bool> canceled = () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested || job.RemoveRequested;
                Action<string> progress = text => { lock (_lock) { if (!canceled()) job.StatusText = text; } };
                poll = string.IsNullOrEmpty(job.ArchiveVolumes)
                    ? TorBoxClient.TryParkOrPrepare(_cfg.TorBoxApiKey, job.HosterUrl, canceled, progress)
                    : TorBoxClient.PrepareMany(_cfg.TorBoxApiKey, urls, 0, canceled, progress);
            }
            catch (DebridResolutionError rejection)
            {
                // A definite TorBox rejection keeps the previous multi-provider
                // fallback: resolve again with TorBox excluded rather than failing
                // the job. Without another supported provider the rejection is
                // reported unchanged.
                if (rejection.CanTryProvider && UnlockProviders.HasSupportedAlternative(_cfg, urls, UnlockProviders.TorBoxId))
                {
                    unavailableProviders = new HashSet<string>(StringComparer.Ordinal) { UnlockProviders.TorBoxId };
                    return false;
                }
                throw;
            }
            lock (_lock)
            {
                if (job.AttemptId != attempt || job.CancelRequested || job.PauseRequested) return true;
            }
            if (poll.Kind == TorBoxClient.PreparedPollKind.Ready) return false;
            if (poll.Kind == TorBoxClient.PreparedPollKind.Rejected || poll.Kind == TorBoxClient.PreparedPollKind.Terminal)
            {
                var rejection = TorBoxClient.PreparationFailure(poll.FailedUrl ?? job.HosterUrl, poll);
                if (rejection.CanTryProvider && UnlockProviders.HasSupportedAlternative(_cfg, urls, UnlockProviders.TorBoxId))
                {
                    unavailableProviders = new HashSet<string>(StringComparer.Ordinal) { UnlockProviders.TorBoxId };
                    return false;
                }
                throw rejection;
            }
            long nowTicks = DateTime.UtcNow.Ticks;
            lock (_lock)
            {
                if (job.AttemptId != attempt || job.CancelRequested || job.PauseRequested) return true;
                job.State = DlState.Queued;
                job.ParkedForProvider = true;
                job.ParkProviderId = UnlockProviders.TorBoxId;
                job.ParkStartedUtcTicks = nowTicks;
                job.ParkPollCount = 1;
                job.ParkTransientFailures = poll.Kind == TorBoxClient.PreparedPollKind.Transient ? 1 : 0;
                job.ParkLastState = poll.ProviderState ?? "";
                job.ParkPollDueUtcTicks = nowTicks + TimeSpan.FromMilliseconds(Math.Max(
                    QueueScheduler.NextPollDelayMs(0), (long)poll.RetryAfterSeconds * 1000)).Ticks;
                job.BytesPerSec = 0;
                job.EtaSeconds = 0;
                job.StatusText = ParkedPreparationStatus(poll);
                // A park must free the queue immediately: the post-claim cooldown
                // exists to pace transfers, not to delay an unrelated game.
                _nextJobStartAt = TransferClockMs();
            }
            SaveManifest();
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

        void EnsureParkPollTimer()
        {
            if (Volatile.Read(ref _parkTimerStarted) == 1) return;
            if (Interlocked.CompareExchange(ref _parkTimerStarted, 1, 0) != 0) return;
            _parkPollTimer = new Timer(delegate { if (_run) PumpParkedPreparations(); }, null, 1000, 1000);
        }

        /// <summary>Only the first-ranked provider for this link parks. Later
        /// candidates keep their existing fallback behavior.</summary>
        static bool TorBoxIsFirstProvider(AppSettings cfg, string hosterUrl)
        {
            if (cfg == null || !cfg.UseUnlockProvider || !cfg.HasTorBox || string.IsNullOrEmpty(hosterUrl)) return false;
            if (!UnlockProviders.IsEnabled(cfg, UnlockProviders.TorBoxId)) return false;
            string[] ranked = UnlockProviders.RankedProviderIds(cfg, hosterUrl);
            return ranked.Length > 0 && string.Equals(ranked[0], UnlockProviders.TorBoxId, StringComparison.OrdinalIgnoreCase);
        }

        static string ParkedPreparationStatus(TorBoxClient.PreparedPollResult poll)
        {
            if (poll.Kind == TorBoxClient.PreparedPollKind.Transient)
                return "Preparing in TorBox · list request retry 1/" + TorBoxClient.TransientListAttempts +
                    " · other downloads continue";
            string metric = string.IsNullOrEmpty(poll.Metric) ? "" : " · " + poll.Metric;
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
        void PumpParkedPreparations()
        {
            if (_cfg == null || Interlocked.CompareExchange(ref _parkPollBusy, 1, 0) != 0) return;
            DlItem target = null;
            int attempt = 0;
            string token = null;
            bool rearmed = false;
            bool expired = false;
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
                        rearmed = true;
                        continue;
                    }
                    // The durable provider record is the park's only source of truth.
                    // If it vanished (restart, six hour expiry, provider cleanup) the
                    // row must be reclaimable so the next claim can create a fresh
                    // prepared download instead of waiting forever.
                    bool allDebrid = string.Equals(candidate.ParkProviderId, UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase);
                    bool hasPending = allDebrid
                        ? AllDebridClient.HasPreparedDownload(_cfg.AllDebridApiKey, candidate.HosterUrl)
                        : !string.IsNullOrEmpty(candidate.ArchiveVolumes) || TorBoxClient.HasPreparedDownload(_cfg.TorBoxApiKey, candidate.HosterUrl);
                    if (!hasPending)
                    {
                        candidate.ParkedForProvider = false;
                        candidate.RetryAfterUtcTicks = 0;
                        candidate.StatusText = (allDebrid ? "AllDebrid" : "TorBox") + " preparation restarted · queued";
                        rearmed = true;
                        continue;
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
                    expired = nowTicks - target.ParkStartedUtcTicks >
                        (allDebrid ? AllDebridClient.PreparationDeadline : TorBoxClient.PreparationDeadline).Ticks;
                }
            }
            if (rearmed) SaveManifest();
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
            string hostUrl = target.HosterUrl;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Exception failure = null;
                    bool allDebrid = string.Equals(target.ParkProviderId, UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase);
                    if (allDebrid)
                    {
                        AllDebridClient.PreparedPollResult poll = null;
                        try { poll = AllDebridClient.PollPrepared(token, hostUrl, () => !IsParkedPollCurrent(target, attempt)); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { failure = ex; }
                        if (poll != null || failure != null) ApplyAllDebridParkedPollResult(target, attempt, poll, failure);
                    }
                    else
                    {
                        TorBoxClient.PreparedPollResult poll = null;
                        try { poll = string.IsNullOrEmpty(target.ArchiveVolumes)
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
                    switch (poll.Kind)
                    {
                        case TorBoxClient.PreparedPollKind.Ready:
                            target.ParkedForProvider = false;
                            target.RetryAfterUtcTicks = 0;
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
                            target.StatusText = ParkedPreparationStatus(poll);
                            changed = true;
                            break;
                        case TorBoxClient.PreparedPollKind.Transient:
                            failMessage = ParkedTransientFailure(target, poll.Transport);
                            changed = true;
                            break;
                        default:
                            target.ParkedForProvider = false;
                            failMessage = TorBoxClient.PreparationFailure(poll.FailedUrl ?? target.HosterUrl, poll).Message;
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
            lock (_lock)
            {
                if (!object.ReferenceEquals(Find(target.Id), target) || target.AttemptId != attempt || !target.ParkedForProvider) return;
                if (target.PauseRequested || target.CancelRequested || target.RemoveRequested)
                { target.ParkedForProvider = false;changed = true; }
                else if (failure != null || poll == null)
                { ParkedTransientFailure(target, failure);changed = true; }
                else switch (poll.Kind)
                {
                    case AllDebridClient.PreparedPollKind.Ready:
                        target.ParkedForProvider = false;target.RetryAfterUtcTicks = 0;target.ParkLastState = "";
                        target.ResolvedProviderId = UnlockProviders.AllDebridId;
                        target.StatusText = "AllDebrid ready · queued";changed = true;break;
                    case AllDebridClient.PreparedPollKind.Preparing:
                        target.ParkPollCount++;target.ParkTransientFailures = 0;target.ParkLastState = poll.ProviderState ?? "";
                        target.ParkPollDueUtcTicks = DateTime.UtcNow.Ticks + TimeSpan.FromMilliseconds(ParkPollDelayMs(target)).Ticks;
                        target.StatusText = "Preparing in AllDebrid · next check in 5s · other downloads continue";changed = true;break;
                    case AllDebridClient.PreparedPollKind.Transient:
                        ParkedTransientFailure(target, poll.Transport);changed = true;break;
                    default:
                        target.ParkedForProvider = false;
                        failMessage = string.IsNullOrEmpty(poll.Error) ? "AllDebrid preparation failed" : poll.Error;
                        changed = true;break;
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
                delay = Math.Max(delay, Math.Min((long)int.MaxValue, (long)rejection.RetryAfterSeconds * 1000L));
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
            long seconds;
            string detail;
            lock (_lock)
            {
                if (!object.ReferenceEquals(Find(target.Id), target) || target.AttemptId != attempt ||
                    !target.ParkedForProvider) return;
                target.ParkedForProvider = false;
                seconds = Math.Max(1, (DateTime.UtcNow.Ticks - target.ParkStartedUtcTicks) / TimeSpan.TicksPerSecond);
                detail = string.IsNullOrEmpty(target.ParkLastState)
                    ? "the provider never listed the prepared download"
                    : "last provider state " + target.ParkLastState;
            }
            string provider = string.Equals(target.ParkProviderId, UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase) ? "AllDebrid" : "TorBox";
            SetFailureIfCurrent(target, attempt, provider + " is still preparing this file after " + seconds + "s (" +
                detail + "). The prepared download ID was kept; retry from Downloads shortly.");
        }

        void RunJob(DlItem job, int attempt)
        {
            try
            {
                if (CommitRequestedStop(job, attempt)) return;
                if (WaitForSelectedBackground(job, attempt)) return;
                if (job.LocalSource) { RunLocalSource(job, attempt); return; }
                bool metadataOnly;
                lock (_lock) { bool residentBusy; HasBlockingPipelineOwner(out residentBusy); metadataOnly = BackgroundSelected && residentBusy; }
                if (!CanPrepareNativeBgft(job, BackgroundSelected))
                    AppSettings.RequireStaging(Path.GetDirectoryName(job.DestPath));
                if (metadataOnly && File.Exists(job.DestPath))
                {
                    HandoffRetainedMetadata(job, attempt);
                    return;
                }
                if (!metadataOnly) TransferClient.TryPublishCompleted(job.DestPath, job.TitleId, job.Kind,
                    job.ExpectedContentId, job.ExpectedSha256, job.ExpectedByteSize,
                    () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested,
                    text => { lock (_lock) { if(job.AttemptId==attempt){job.State=DlState.Finalizing;job.StatusText=text;} } });
                if (File.Exists(job.DestPath) && string.IsNullOrEmpty(job.ArchiveVolumes))
                {
                    FinishDownloadedObject(job, attempt);
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
                    HashSet<string> unavailable;
                    if (TryParkTorBoxPreparation(job, attempt, out unavailable)) return;
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
                if (job.TransientHttpRetries > 0 && !string.IsNullOrEmpty(job.HttpRetryUrl))
                {
                    direct = job.HttpRetryUrl;
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
                    HashSet<string> torboxUnavailable;
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
                        direct = FreeHosterClient.ResolveDirect(job.HosterUrl,
                            string.Equals(job.AccessType, "HosterLanding", StringComparison.OrdinalIgnoreCase));
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
                bool linkExpired = !string.IsNullOrEmpty(job.ExpiresUtc) &&
                    DateTime.TryParse(job.ExpiresUtc, null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out expiresUtc) &&
                    expiresUtc.ToUniversalTime() < DateTime.UtcNow;
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
                    RequireTransferSpace(Math.Max(0, expected - existing), LikelyArchive(job, direct));
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
                                { job.State = DlState.Finalizing; job.StatusText = text; job.BytesPerSec = 0; job.EtaSeconds = 0; } } });
                    },
                    () => TransferClient.DurableBytes(job.DestPath),
                    () => CanRefreshUnlock(job),
                    () => RenewJobLink(job, attempt, cancel),
                    cancel,
                    count => { lock (_lock) { if (job.AttemptId == attempt) job.StatusText = "Refreshing rejected link..."; } });

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
            catch (Exception ex)
            {
                if (CommitRequestedStop(job, attempt)) return;
                if (TryScheduleHttpRetry(job, attempt, ex)) return;
                job.HttpRetryUrl = null;
                SetFailureIfCurrent(job, attempt, ex.Message);
            }
        }

        bool TryScheduleHttpRetry(DlItem job, int attempt, Exception error)
        {
            lock (_lock)
            {
                if (job.AttemptId != attempt || job.Background || job.PauseRequested || job.CancelRequested)
                    return false;
                long retryAt;
                if (!DownloadHttpException.TryGetRetry(error, job.TransientHttpRetries,
                    DateTime.UtcNow, out retryAt)) return false;
                long durable = TransferClient.DurableBytes(job.DestPath);
                if (durable > job.HttpRetryLastDurableBytes) job.TransientHttpRetries = 0;
                job.HttpRetryLastDurableBytes = Math.Max(job.HttpRetryLastDurableBytes, durable);
                if (job.TransientHttpRetries < int.MaxValue) job.TransientHttpRetries++;
                job.RetryAfterUtcTicks = retryAt;
                job.State = DlState.Queued;
                job.Error = null;
                job.BytesPerSec = 0;
                job.EtaSeconds = 0;
                long seconds = Math.Max(1, (retryAt - DateTime.UtcNow.Ticks + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond);
                job.StatusText = "Server temporarily unavailable · recovery retry " + job.TransientHttpRetries +
                    " in " + seconds + "s";
            }
            SaveManifest();
            return true;
        }

        internal static string PrepareResidentOnce(bool enabled, ref int attempted, Func<string> prepare)
        {
            if (!enabled || Interlocked.CompareExchange(ref attempted, -1, 0) != 0) return null;
            try
            {
                string error = prepare();
                // The service throttles failed activation attempts. A failed first
                // attempt must not suppress activation for the entire app session.
                Interlocked.Exchange(ref attempted, string.IsNullOrEmpty(error) ? 1 : 0);
                return error;
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref attempted, 0);
                return "Background downloader unavailable: " + ex.GetType().Name;
            }
        }

        static string PrepareResidentWorker()
        {
            string error;
            return ResidentDownloadService.EnsureAvailable(out error) ? null : error;
        }

        bool BackgroundSelected { get { return _cfg != null && _cfg.UseBgftDirect; } }

        internal static string SelectedModeWaitingReason(bool backgroundSelected, bool workerReady, string reason)
        {
            if (!backgroundSelected || (workerReady && string.IsNullOrEmpty(reason))) return null;
            return "Background mode waiting: " + (string.IsNullOrWhiteSpace(reason) ? "resident worker is not ready" : reason);
        }

        bool WaitForSelectedBackground(DlItem job, int attempt, string reason = null)
        {
            if (!BackgroundSelected) return false;
            bool logReason;
            if (string.IsNullOrEmpty(reason))
            {
                string error;
                if (ResidentDownloadService.EnsureAvailable(out error)) { job.BackgroundWaitLoggedReason = null; return false; }
                reason = string.IsNullOrEmpty(error) ? ResidentDownloadService.ReadinessDetail : error;
            }
            lock (_lock)
            {
                if (job.AttemptId != attempt || job.Background) return true;
                if (job.CancelRequested || job.PauseRequested) { CommitRequestedStop(job, attempt); return true; }
                job.State = DlState.Queued;
                job.StatusText = SelectedModeWaitingReason(true, false, reason);
                job.Error = null; job.BytesPerSec = 0; job.EtaSeconds = 0;
                job.ForegroundTransfer = false; job.ForceLocalInstall = false;
                job.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(15).Ticks;
                logReason = !string.Equals(job.BackgroundWaitLoggedReason, reason, StringComparison.Ordinal);
                job.BackgroundWaitLoggedReason = reason;
            }
            if (logReason) SspiLog.Write("resident", "background job waiting: " + reason);
            SaveManifest(); return true;
        }

        bool OtherForegroundTransfer(DlItem job)
        {
            lock (_lock) foreach (var other in _items)
                if (other != job && other.ForegroundTransfer) return true;
            return false;
        }

        bool DeferWhileResidentTransfers(DlItem job, int attempt)
        {
            bool busy = ResidentDownloadService.StagedTransfersActive;
            lock (_lock)
            {
                foreach (var other in _items)
                    if (other != job && other.Background && !other.BgftLocalInstall &&
                        other.State != DlState.Installed && other.State != DlState.Canceled) busy = true;
                if (!busy) return false;
                if (job.AttemptId != attempt) return true;
                job.State = DlState.Queued;
                job.StatusText = "Waiting for background transfer capacity";
                job.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(2).Ticks;
            }
            SaveManifest(); return true;
        }

        static void TraceBackgroundRoute(NetHttp.BackgroundRoute reason)
        { NetHttp.TraceBackgroundRoute(reason, ResidentDownloadService.ObservedWorkerVersion); }

        bool TryRunLoopbackBgftFeeder(DlItem job, int attempt, string direct)
        {
            if (_cfg == null || !_cfg.UseBgftDirect)
            { TraceBackgroundRoute(NetHttp.BackgroundRoute.Disabled); return false; }
            lock (_lock) foreach (var other in _items)
                if (other != job && other.Background && !other.ResidentStaged && !other.ResidentAutoInstall && !other.BgftLocalInstall)
                { TraceBackgroundRoute(NetHttp.BackgroundRoute.LegacyWorkerBusy); return WaitForSelectedBackground(job, attempt, "Waiting for the active resident archive or installation"); }
            // Foreground and native staging use different resume journals. Never transfer
            // ownership of an existing foreground file without converting its journal.
            if (File.Exists(job.DestPath))
            { TraceBackgroundRoute(NetHttp.BackgroundRoute.ExistingLocalFile); return false; }
            if (File.Exists(job.DestPath + ".part") && !File.Exists(job.DestPath + ".map"))
            { TraceBackgroundRoute(NetHttp.BackgroundRoute.ExistingPartial); return WaitForSelectedBackground(job, attempt, "Saved partial uses In-app resume; choose In-app mode and Retry to keep its downloaded bytes"); }
            string workerError;
            bool residentReady = ResidentDownloadService.EnsureAvailable(out workerError);
            TraceBackgroundRoute(residentReady ? NetHttp.BackgroundRoute.WorkerReady :
                ResidentDownloadService.RunningWorkerRequiresRestart ? NetHttp.BackgroundRoute.WorkerRestartRequired :
                NetHttp.BackgroundRoute.WorkerUnavailable);
            if (CommitRequestedStop(job, attempt)) return true;
            if (!residentReady) return WaitForSelectedBackground(job, attempt, workerError);

            HttpRangeResult header;
            try
            {
                lock (_lock)
                {
                    if (job.AttemptId != attempt) return true;
                    job.StatusText = "Checking download format...";
                }
                header = NetHttp.ReadRangeDirect(direct, 0, LoopbackPkgFeeder.HeaderBytes, 60000);
            }
            catch (Exception ex)
            {
                SetFeederForegroundFallback(job, attempt, ex.Message);
                return true;
            }
            if (CommitRequestedStop(job, attempt)) return true;

            if (header == null || header.Data == null || !LoopbackPkgFeeder.IsPkgHeader(header.Data))
            {
                if (IsResidentArchiveHeader(header))
                {
                    ApplySourceArchivePassword(job, header.Data);
                    string passwordError = ResidentArchivePasswordError(header.Data, job.ArchivePassword);
                    if (passwordError != null)
                    { SetFailureIfCurrent(job, attempt, passwordError); return true; }
                    if (job.ExpectedByteSize > 0 && job.ExpectedByteSize != header.Total)
                    { SetFailureIfCurrent(job, attempt, "Archive size mismatch"); return true; }
                    if (ResidentDownloadService.HasStagedDownloader)
                        return HandoffStagedPackage(job, attempt, direct, job.TitleId, job.Kind, "", header.Total, true);
                    SetFeederForegroundFallback(job, attempt, "Archive download requires SSPI to remain open");
                    return true;
                }
                SetFeederForegroundFallback(job, attempt, "Origin did not return a PKG header");
                return true;
            }

            string contentId;
            PkgContentKind actualKind;
            string kindDetail;
            long packageSize;
            string actualTitleId = job.TitleId;
            if (header.Data.Length != LoopbackPkgFeeder.HeaderBytes || header.Total <= header.Data.Length ||
                !PkgValidator.TryGetContentIdFromHeader(header.Data, out contentId) ||
                !PkgValidator.TryGetContentKindFromHeader(header.Data, out actualKind, out kindDetail) ||
                !PkgValidator.TryGetPackageSizeFromHeader(header.Data, out packageSize) ||
                packageSize != header.Total)
            {
                SetFeederForegroundFallback(job, attempt, "PKG preflight was incomplete");
                return true;
            }

            string parsedTitleId;
            if (PkgValidator.TryGetTitleIdFromContentId(contentId, out parsedTitleId))
                actualTitleId = parsedTitleId;
            if (!string.IsNullOrWhiteSpace(job.ExpectedContentId) &&
                !string.Equals(job.ExpectedContentId.Trim(), contentId,
                    StringComparison.OrdinalIgnoreCase))
            {
                SetFailureIfCurrent(job, attempt, "PKG content ID mismatch");
                return true;
            }
            if (job.ExpectedByteSize > 0 && job.ExpectedByteSize != packageSize)
            {
                SetFailureIfCurrent(job, attempt, "Package size mismatch: expected " +
                    job.ExpectedByteSize + ", got " + packageSize);
                return true;
            }
            if (!string.IsNullOrWhiteSpace(job.TitleId) &&
                !PkgValidator.ContentIdMatchesTitleId(contentId, job.TitleId))
            {
                SetFailureIfCurrent(job, attempt, "PKG title ID mismatch");
                return true;
            }

            string actualKindName = KindName(actualKind, job.Kind);
            string identityError;
            if (!PkgValidator.CheckRequestedIdentity(job.Kind, actualKind, job.TitleId, contentId, out identityError))
            { SetFailureIfCurrent(job, attempt, identityError); return true; }
            lock (_lock)
            {
                if (job.AttemptId != attempt) return true;
                // Retain the parsed identity for signed-link renewal and the final local
                // validation gate even when the source supplied only a title and host URL.
                if (string.IsNullOrEmpty(job.ExpectedContentId)) job.ExpectedContentId = contentId;
                if (job.ExpectedByteSize <= 0) job.ExpectedByteSize = packageSize;
            }
            // The durable resident owner validates and installs when its dependencies settle.
            if (ResidentDownloadService.HasStagedDownloader)
            {
                bool nativeBgft = (actualKind == PkgContentKind.BaseGame || actualKind == PkgContentKind.Patch) &&
                    CanPrepareNativeBgft(job, BackgroundSelected) &&
                    CanInstallWithResidentDependency(ResidentDependencyId(job));
                return HandoffStagedPackage(job, attempt, direct, actualTitleId, actualKindName, contentId, packageSize, false, nativeBgft);
            }
            // SSPI owns WAN transfers. BGFT receives a validated local file at install time;
            // a missing resident must not silently revert base games to an opaque single stream.
            if (NetHttp.CanUseParallelDownload(direct, actualTitleId))
                SelectForegroundParallelDownload(job, attempt);
            else
                SetFeederForegroundFallback(job, attempt, "Background downloader unavailable; keep SSPI open");
            return true;
        }

        static void ApplySourceArchivePassword(DlItem job, byte[] header)
        {
            job.ContainerFormat = ContainerFormatFromHeader(header);
            if (header != null && header.Length >= 4 && header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21)
            {
                var defaults = ArchivePasswordDefaults.Decode(job.ArchivePasswords);
                if (string.IsNullOrEmpty(job.ArchivePassword) && defaults.Length > 0) job.ArchivePassword = defaults[0];
                job.ArchivePassword = DistributionSettings.ArchivePassword(job.SourcePageUrl, job.ArchivePassword);
            }
        }

        string[] ArchiveFallbacks(DlItem job)
        {
            return _cfg.RetrySourceArchivePasswords
                ? DistributionSettings.ArchivePasswordFallbacks(job.SourcePageUrl, job.ArchivePassword, job.ArchivePasswords)
                : new string[0];
        }

        internal static string ContainerFormatFromHeader(byte[] header)
        {
            if (header == null || header.Length < 4) return "";
            if (header[0] == 0x7f && header[1] == 0x43 && header[2] == 0x4e && header[3] == 0x54) return "pkg";
            if (header[0] == 0x50 && header[1] == 0x4b &&
                ((header[2] == 3 && header[3] == 4) || (header[2] == 5 && header[3] == 6) || (header[2] == 7 && header[3] == 8))) return "zip";
            if (header.Length >= 7 && header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21 &&
                header[4] == 0x1a && header[5] == 7 && (header[6] == 0 || (header.Length >= 8 && header[6] == 1 && header[7] == 0))) return "rar";
            if (PackageArchive.IsSevenZipHeader(header)) return "7z";
            return "";
        }

        internal static void RestoreContainerFormat(DlItem item)
        {
            if (!string.IsNullOrEmpty(item.ContainerFormat)) return;
            if (!string.IsNullOrEmpty(item.ArchiveVolumes)) { item.ContainerFormat = "rar"; return; }
            if (string.IsNullOrEmpty(item.DestPath)) return;
            try {
                string path = File.Exists(item.DestPath) ? item.DestPath : item.DestPath + ".part";
                if (!File.Exists(path)) return;
                byte[] header = new byte[8];
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) {
                    int read = file.Read(header, 0, header.Length);
                    Array.Resize(ref header, read);
                }
                item.ContainerFormat = ContainerFormatFromHeader(header);
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        internal static string ResidentArchivePasswordError(byte[] header, string password)
        {
            if (PackageArchive.IsSevenZipHeader(header))
            {
                if (!string.IsNullOrEmpty(password)) return "Encrypted 7z archives are not supported; the archive is kept.";
                if (!ResidentDownloadService.SupportsSevenZipArchive)
                    return "Restart the PS4 and enable GoldHEN to load this build's 7z extractor; files are kept.";
                return null;
            }
            if (string.IsNullOrEmpty(password)) return null;
            if (!ResidentDownloadService.ValidArchivePassword(password))
                return "Archive password exceeds 256 UTF-8 bytes or is invalid";
            if (header != null && header.Length >= 4 && header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21)
                return null;
            return "Encrypted ZIP archives are not supported; the archive is kept. Choose an unencrypted ZIP or a supported RAR archive.";
        }

        void RunLocalSource(DlItem job, int attempt)
        {
            if (CommitRequestedStop(job, attempt)) return;
            if (!File.Exists(job.DestPath))
            {
                lock (_lock) if (job.AttemptId == attempt) {
                    job.State = DlState.Queued; job.Error = null; job.BytesPerSec = 0; job.EtaSeconds = 0;
                    job.StatusText = "Reconnect the USB drive containing " + Path.GetFileName(job.DestPath);
                    job.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(5).Ticks;
                }
                SaveManifest(); return;
            }
            LocalInstallSource source = LocalInstallSource.Read(job.DestPath, false);
            if (source.Fingerprint != job.LocalSourceFingerprint || source.Size != job.ExpectedByteSize)
                throw new IOException("USB file changed since it was queued; remove this row and select the file again");
            if (!BackgroundSelected)
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
                    job.State = DlState.Queued; job.StatusText = "Waiting for the required package to finish installing";
                    job.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(2).Ticks;
                }
                SaveManifest(); return;
            }
            HandoffStagedPackage(job, attempt, "", source.TitleId, source.Kind, source.ContentId,
                source.Size, source.ExpectedKind == 0, false, true);
        }

        void HandoffRetainedMetadata(DlItem job, int attempt)
        {
            var header = new byte[LoopbackPkgFeeder.HeaderBytes];
            long size;
            using (var file = new FileStream(job.DestPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                size = file.Length; int read = 0;
                while (read < header.Length) { int n = file.Read(header, read, header.Length - read); if (n == 0) break; read += n; }
            }
            if (IsResidentArchiveHeader(new HttpRangeResult { Data = header, Total = size }))
            {
                ApplySourceArchivePassword(job, header);
                string passwordError = ResidentArchivePasswordError(header, job.ArchivePassword);
                if (passwordError != null) { SetFailureIfCurrent(job, attempt, passwordError); return; }
                if (job.ExpectedByteSize > 0 && job.ExpectedByteSize != size)
                { SetFailureIfCurrent(job, attempt, "Retained archive size does not match; file kept"); return; }
                HandoffStagedPackage(job, attempt, job.HosterUrl, job.TitleId, job.Kind, "", size, true);
                return;
            }
            string content, error, title = job.TitleId; long declared; PkgContentKind kind;
            if (!PkgValidator.TryGetContentIdFromHeader(header, out content) ||
                !PkgValidator.TryGetContentKindFromHeader(header, out kind, out error) ||
                !PkgValidator.TryGetPackageSizeFromHeader(header, out declared) || declared != size ||
                !PkgValidator.CheckRequestedIdentity(job.Kind, kind, job.TitleId, content, out error) ||
                (!string.IsNullOrEmpty(job.ExpectedContentId) && job.ExpectedContentId != content) ||
                (job.ExpectedByteSize > 0 && job.ExpectedByteSize != size))
            { SetFailureIfCurrent(job, attempt, "Retained package metadata does not match; file kept"); return; }
            PkgValidator.TryGetTitleIdFromContentId(content, out title);
            // Header admission only; the resident checks package metadata and any
            // explicit publisher SHA in its serial slot before installation.
            HandoffStagedPackage(job, attempt, job.HosterUrl, title, KindName(kind, job.Kind), content, size);
        }

        internal static bool CanPrepareNativeBgft(DlItem job, bool backgroundSelected)
        {
            if (job == null || !backgroundSelected || !string.IsNullOrEmpty(job.ArchiveVolumes) ||
                !string.IsNullOrWhiteSpace(job.ExpectedSha256) || !string.IsNullOrEmpty(job.ArchivePassword)) return false;
            int subtype = PkgValidator.BgftSubTypeForKind(job.Kind);
            if (subtype != 6 && subtype != 8) return false;
            foreach (string suffix in new[] { "", ".part", ".map", ".ranges", ".resume" })
                if (File.Exists(job.DestPath + suffix)) return false;
            string destination = NativeBgftDestination(job.Id);
            foreach (string suffix in new[] { "", ".part", ".map", ".ranges", ".resume" })
                if (File.Exists(destination + suffix)) return false;
            return true;
        }

        static string NativeBgftDestination(string id)
        { return Path.Combine(AppSettings.StagingRoot("ps4"), "bgft-" + id + ".pkg").Replace('\\', '/'); }

        bool HandoffStagedPackage(DlItem job, int attempt, string url, string titleId,
            string kind, string contentId, long size, bool archive = false, bool nativeBgft = false, bool localSource = false)
        {
            if (!nativeBgft && !localSource)
            {
                AppSettings.RequireStaging(Path.GetDirectoryName(job.DestPath));
                if (!File.Exists(job.DestPath)) RequireTransferSpace(size, archive);
            }
            string dependencyId;
            bool autoInstall;
            lock (_lock)
            {
                if (!AcceptInstallCallback(job, attempt) || job.CancelRequested || job.PauseRequested)
                { CommitRequestedStop(job, attempt); return true; }
                if (nativeBgft) job.DestPath = NativeBgftDestination(job.Id);
                job.Kind = kind; job.TitleId = titleId;
                if (!archive) job.ContainerFormat = "pkg";
                else if (string.IsNullOrEmpty(job.ContainerFormat)) job.ContainerFormat = "archive";
                dependencyId = ResidentDependencyId(job);
                // Only native-owned predecessors publish native installed receipts. Other
                // routes can still stage concurrently, then use the app's install gate.
                autoInstall = CanInstallWithResidentDependency(dependencyId);
                job.ResidentArchive = true; job.ResidentStaged = true;
                job.ResidentGeneration = null;
                job.ResidentPauseDesired = null;
                job.ResidentAutoInstall = autoInstall;
                job.Background = true; job.BgftResident = true; job.BgftTaskId = -1;
                job.BgftContentId = contentId; job.BgftExpectedSize = size;
                job.State = DlState.Downloading; job.Total = size; job.StatsInitialized = false;
                job.StatusText = localSource ? "Preparing USB installation; source file retained" : "Publishing background download ownership";
            }
            if (!SaveManifest())
            {
                lock (_lock) { job.ResidentArchive = job.ResidentStaged = job.ResidentAutoInstall = job.Background = job.BgftResident = false; }
                throw new IOException("Could not save background transfer ownership");
            }
            string error; bool busy;
            int lanes = DownloadTransferSettings.ConnectionsFor(url, NetHttp.DownloadRangeCount);
            lock (_lock)
            {
                // Keep publication atomic with UI cancellation/removal. Before publication
                // there is no native slot for a control message to reach.
                if (!CanPublishResidentStaged(job, attempt))
                {
                    if (object.ReferenceEquals(Find(job.Id), job) && job.AttemptId == attempt)
                    {
                        ClearBackground(job); job.ResidentArchive = false;
                        if (job.CancelRequested) { job.State = DlState.Canceled; job.StatusText = "Canceled"; }
                        else if (job.PauseRequested) { job.State = DlState.Paused; job.StatusText = "Paused before background handoff"; }
                        job.CancelRequested = job.PauseRequested = false;
                        job.BytesPerSec = 0; job.EtaSeconds = 0;
                        SaveManifest();
                    }
                    return true;
                }
                bool published = localSource
                    ? ResidentDownloadService.TryStartLocalSource(job.Id, job.DestPath, titleId, contentId, size,
                        archive ? 0 : PkgValidator.BgftSubTypeForKind(kind), dependencyId,
                        archive ? job.ArchivePassword : null, out error, out busy, archive ? ArchiveFallbacks(job) : null)
                    : nativeBgft
                    ? ResidentDownloadService.TryStartNativeBgftPackage(job.Id, url, job.DestPath, titleId,
                        contentId, size, PkgValidator.BgftSubTypeForKind(kind), lanes, autoInstall,
                        autoInstall ? dependencyId : "", out error, out busy)
                    : ResidentDownloadService.TryStartStagedPackageWithPassword(job.Id, url, job.DestPath, titleId,
                        job.ExpectedSha256, contentId, size, archive ? 0 : PkgValidator.BgftSubTypeForKind(kind), lanes,
                        autoInstall, autoInstall ? dependencyId : "", archive ? job.ArchivePassword : null, out error, out busy, archive ? ArchiveFallbacks(job) : null);
                if (!published)
                {
                    job.ResidentStaged = job.ResidentAutoInstall = false;
                    TraceBackgroundRoute(busy ? NetHttp.BackgroundRoute.PublicationBusy : NetHttp.BackgroundRoute.PublicationFailed);
                    return ResidentPublicationFailed(job, busy ? "Another resident job is active" : error);
                }
            }
            TraceBackgroundRoute(NetHttp.BackgroundRoute.Published);
            NetHttp.TraceDownloadDecision(true, NetHttp.DownloadDecision.ResidentSelected, lanes);
            lock (_lock) if (job.AttemptId == attempt)
                job.StatusText = "Background queue saved; waiting for worker acknowledgement";
            return true;
        }

        internal static bool IsResidentArchiveHeader(HttpRangeResult header)
        {
            if (header == null || header.Data == null || header.Data.Length < 8 || header.Total <= 0) return false;
            byte[] data = header.Data;
            return (data[0] == 0x52 && data[1] == 0x61 && data[2] == 0x72 && data[3] == 0x21 &&
                data[4] == 0x1a && data[5] == 7 && (data[6] == 0 || (data[6] == 1 && data[7] == 0))) ||
                (data[0] == 0x50 && data[1] == 0x4b && data[2] == 3 && data[3] == 4) ||
                PackageArchive.IsSevenZipHeader(data);
        }

        bool HandoffResidentPackage(DlItem job, int attempt, string url, string titleId,
            string kind, string contentId, long size)
        {
            lock (_lock)
            {
                if (job.AttemptId != attempt || job.CancelRequested || job.PauseRequested)
                { CommitRequestedStop(job, attempt); return true; }
                job.Kind = kind; job.TitleId = titleId;
                job.ResidentArchive = true; job.Background = true; job.BgftResident = true;
                job.BgftContentId = contentId; job.BgftExpectedSize = size;
                job.State = DlState.Downloading; job.Total = size;
                job.StatusText = "Handing package to shell downloader";
            }
            if (!SaveManifest())
            {
                lock (_lock) { job.ResidentArchive = false; job.Background = false; job.BgftResident = false; }
                throw new IOException("Could not save resident package ownership");
            }
            string error;
            if (!ResidentDownloadService.TryStartPackage(job.Id, url, job.DestPath, titleId,
                job.ExpectedSha256, contentId, size, PkgValidator.BgftSubTypeForKind(kind), out error))
                return ResidentPublicationFailed(job, error);
            NetHttp.TraceDownloadDecision(true, NetHttp.DownloadDecision.ResidentSelected);
            return true;
        }

        bool ResidentPublicationFailed(DlItem job, string error)
        {
            bool busy = error == "Another resident job is active";
            lock (_lock)
            {
                job.ResidentArchive = false; job.Background = false; job.BgftResident = false;
                job.ResidentStaged = job.ResidentAutoInstall = false;
                if (busy)
                {
                    job.State = DlState.Queued;
                    job.StatusText = "Waiting for active PS4 download";
                    job.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(2).Ticks;
                }
            }
            if (BackgroundSelected) return WaitForSelectedBackground(job, job.AttemptId, error ?? "Resident handoff is not ready");
            SaveManifest();
            if (!busy) throw new IOException(error);
            return true;
        }

        static bool OwnsFeeder(DlItem job, int attempt, string contentId, int subType)
        {
            return job != null && job.AttemptId == attempt && job.Background &&
                job.BgftLoopback && job.BgftSubType == subType &&
                string.Equals(job.BgftContentId, contentId, StringComparison.OrdinalIgnoreCase);
        }

        bool FeederIsAlive(DlItem item)
        {
            return item != null && (item.BgftResident
                ? ResidentDownloadService.IsAlive(item.Id) : _loopback.IsAlive(item.Id));
        }

        string FeederLastError(DlItem item)
        {
            return item != null && item.BgftResident
                ? ResidentDownloadService.GetError(item.Id) : _loopback.LastError;
        }

        void FeederMarkFailed(DlItem item)
        {
            if (item == null) return;
            if (item.BgftResident) ResidentDownloadService.MarkFailed(item.Id);
            else _loopback.MarkFailed(item.Id);
        }

        void FeederRelease(DlItem item)
        {
            if (item == null) return;
            if (item.BgftResident) ResidentDownloadService.Release(item.Id);
            else _loopback.Release(item.Id);
        }

        bool FeederTryMarkComplete(DlItem item, out string error)
        {
            if (item != null && item.BgftResident)
                return ResidentDownloadService.TryMarkComplete(item.Id, out error);
            return _loopback.TryMarkComplete(item != null ? item.Id : "", out error);
        }

        bool FeederWasFullyServed(DlItem item)
        {
            return item != null && (item.BgftResident
                ? ResidentDownloadService.WasFullyServed(item.Id)
                : _loopback.WasFullyServed(item.Id));
        }

        bool FeederFallbackHandled(DlItem job, int attempt)
        {
            lock (_lock)
                return job.AttemptId != attempt || job.Background ||
                    job.State != DlState.Queued || !job.ForceLocalInstall;
        }

        void SelectForegroundParallelDownload(DlItem job, int attempt)
        {
            if (WaitForSelectedBackground(job, attempt, ResidentDownloadService.ReadinessDetail)) return;
            DiscardHeaderOnlyPartial(job);
            lock (_lock)
            {
                if (job.AttemptId != attempt) return;
                job.ForceLocalInstall = true;
                job.StatusText = "Parallel download · Keep SSPI open";
            }
            LogBgftEvent("transport-app-parallel", job, "Validated provider ranges; local verification before installation");
            User.NotifyToast("Downloading in SSPI: keep the app open");
            SaveManifest();
        }

        void SetFeederForegroundFallback(DlItem job, int attempt, string reason)
        {
            if (WaitForSelectedBackground(job, attempt, reason)) return;
            DiscardHeaderOnlyPartial(job);
            lock (_lock)
            {
                if (job.AttemptId != attempt) return;
                job.ForceLocalInstall = true;
                job.StatusText = "Using in-app download: " + ClipMsg(reason, 42);
            }
            if (!string.IsNullOrEmpty(reason)) LogBgftEvent("fallback-local", job, reason);
            SspiLog.Write("resident", reason ?? "");
            SaveManifest();
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
            ArchiveStorage.RequireFreeSpace(AppSettings.DownloadDir, required);
        }

        static bool LikelyArchive(DlItem job, string urlOrNull)
        {
            if (job != null && !string.IsNullOrEmpty(job.ArchiveVolumes)) return true;
            string[] candidates = { urlOrNull, job != null ? job.HosterUrl : null, job != null ? job.Label : null };
            foreach (string c in candidates)
            {
                if (string.IsNullOrEmpty(c)) continue;
                string l = c.ToLowerInvariant();
                if (l.EndsWith(".rar") || l.EndsWith(".zip") || l.EndsWith(".7z") || l.EndsWith(".7zip") || l.Contains(".part")) return true;
            }
            return false;
        }

        static List<string> ArchivePaths(DlItem job)
        {
            if (job.LocalSource) return LocalInstallSource.ArchivePaths(job.DestPath);
            var volumes = ArchiveVolumeSet.Decode(job.ArchiveVolumes);
            var paths = new List<string>();
            if (volumes.Count == 0) { paths.Add(job.DestPath); return paths; }
            // Keep volume files under the normal owned download directory; input streams
            // carry their sequence explicitly so hashed disk names do not lose RAR order.
            for (int i = 0; i < volumes.Count; i++)
                paths.Add(i == 0 ? job.DestPath : BuildDerivedDestination(job.TitleId, job.Kind,
                    job.HosterUrl + "#volume=" + (i + 1) + ":" + volumes[i].Name));
            return paths;
        }

        bool TryHandoffArchive(DlItem job, int attempt, List<ArchiveVolume> volumes, List<string> paths, ISet<string> unavailable = null)
        {
            ApplySourceArchivePassword(job, new byte[] { 0x52, 0x61, 0x72, 0x21 });
            job.ContainerFormat = "rar";
            if (!ResidentDownloadService.ValidArchivePassword(job.ArchivePassword))
            { SetFailureIfCurrent(job, attempt, "Archive password exceeds 256 UTF-8 bytes or is invalid"); return true; }
            if (!InstallDependencyReady(job)) return false;
            lock (_lock) foreach (var other in _items) if (other != job && other.Background) return false;
            if (_cfg == null || !_cfg.UseBgftDirect || !ResidentDownloadService.HasDownloader) return false;
            var resolved = new List<ArchiveVolume>();
            for (int volumeIndex = 0; volumeIndex < volumes.Count; volumeIndex++)
            {
                ArchiveVolume volume = volumes[volumeIndex];
                if (CommitRequestedStop(job, attempt)) return true;
                if (File.Exists(paths[volumeIndex]))
                {
                    PackageObjectKind localKind = PackageArchive.Detect(paths[volumeIndex]);
                    long localSize = new FileInfo(paths[volumeIndex]).Length;
                    if ((localKind != PackageObjectKind.Rar4 && localKind != PackageObjectKind.Rar5) ||
                        (volume.Size > 0 && volume.Size != localSize))
                        throw new InvalidDataException("Saved RAR volume type or size mismatch");
                    resolved.Add(new ArchiveVolume { Name = volume.Name, Url = volume.Url,
                        Size = localSize, Sha256 = volume.Sha256, AccessType = volume.AccessType });
                    continue;
                }
                string direct = ResolveVolume(volume, job, attempt, volumeIndex, volumes.Count, unavailable);
                lock (_lock) { if (job.AttemptId == attempt) job.StatusText = "Checking RAR part " + (volumeIndex + 1) + "/" + volumes.Count + " · file size and header"; }
                HttpRangeResult probe = NetHttp.ReadRangeDirect(direct, 0, 8, 60000);
                if (probe == null || probe.Total <= 0 || probe.Data == null || probe.Data.Length < 7 ||
                    probe.Data[0] != 0x52 || probe.Data[1] != 0x61 || probe.Data[2] != 0x72 || probe.Data[3] != 0x21 ||
                    probe.Data[4] != 0x1a || probe.Data[5] != 7)
                    throw new InvalidDataException("Archive volume did not return RAR bytes and a stable size");
                if (volume.Size > 0 && volume.Size != probe.Total) throw new InvalidDataException("Archive volume size mismatch");
                resolved.Add(new ArchiveVolume { Name = volume.Name, Url = probe.EffectiveUrl ?? direct,
                    Size = probe.Total, Sha256 = volume.Sha256, AccessType = "Direct" });
            }
            lock (_lock)
            {
                if (job.AttemptId != attempt || job.CancelRequested || job.PauseRequested)
                { CommitRequestedStop(job, attempt); return true; }
                // Persist ownership before publishing IPC, so restart cannot claim a second writer.
                job.ResidentArchive = true; job.Background = true; job.BgftResident = true;
                job.ResidentGeneration = Guid.NewGuid().ToString("N");
                job.ResidentPauseDesired = null;
                job.ResidentAutoInstall = true;
                job.State = DlState.Downloading; job.StatusText = "Handing RAR volumes to resident downloader";
            }
            if (!SaveManifest())
            {
                lock (_lock) { job.ResidentArchive = false; job.Background = false; job.BgftResident = false; job.ResidentAutoInstall = false; }
                throw new IOException("Could not save archive ownership");
            }
            string error;
            if (!ResidentDownloadService.TryStartArchiveWithPassword(job.Id, job.DestPath, job.TitleId, job.ExpectedContentId, resolved, paths, job.ResidentGeneration, job.ArchivePassword, out error, ArchiveFallbacks(job)))
                return ResidentPublicationFailed(job, error);
            NetHttp.TraceDownloadDecision(true, NetHttp.DownloadDecision.ResidentSelected);
            return true;
        }

        string ResolveVolume(ArchiveVolume volume, DlItem job, int attempt, int index, int count, ISet<string> unavailable)
        {
            Func<bool> canceled = () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested;
            if (canceled()) throw new OperationCanceledException();
            if (IsDirectAccess(volume.AccessType)) return volume.Url;
            if (_cfg.UseUnlockProvider && UnlockProviders.EnabledIds(_cfg).Length > 0)
                return UnlockProviders.Unrestrict(_cfg, volume.Url,
                    text => { lock (_lock) { if (!canceled()) job.StatusText = "RAR part " + (index + 1) + "/" + count + " · " + text; } },
                    canceled, id => { lock (_lock) { if (!canceled()) job.ResolvedProviderId = id; } }, null, unavailable);
            return FreeHosterClient.ResolveDirect(volume.Url,
                string.Equals(volume.AccessType, "HosterLanding", StringComparison.OrdinalIgnoreCase));
        }

        string ResolveJobHost(DlItem job, int attempt, ISet<string> unavailable = null, DebridResolutionError prepareFailure = null)
        {
            var unavailableProviders = unavailable ?? new HashSet<string>(StringComparer.Ordinal);
            Func<bool> cancelled = () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested;
            Action<string> progress = text => { lock (_lock) { if (job.AttemptId == attempt) job.StatusText = text; } };
            return PackageMirrorFallback.Resolve(job.HosterUrl, job.MirrorCandidates,
                url => {
                    if (prepareFailure != null && url == job.HosterUrl) throw prepareFailure;
                    return UnlockProviders.Unrestrict(_cfg, url, progress, cancelled,
                        provider => { lock (_lock) { if (!cancelled()) job.ResolvedProviderId = provider; } }, null, unavailableProviders);
                },
                () => !cancelled() && string.IsNullOrEmpty(job.ArchiveVolumes) &&
                    !File.Exists(job.DestPath) && !File.Exists(job.DestPath + ".part") &&
                    !File.Exists(job.DestPath + ".ranges") && !File.Exists(job.DestPath + ".resume"),
                mirror =>
                {
                    lock (_lock)
                    {
                        if (cancelled()) throw new OperationCanceledException();
                        job.HosterUrl = mirror.Url;
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
                    if (!SaveManifest()) throw new IOException("Could not save the selected package mirror");
                }, progress, cancelled);
        }

        void DownloadArchiveVolumes(DlItem job, int attempt, ISet<string> unavailable = null)
        {
            var volumes = ArchiveVolumeSet.Decode(job.ArchiveVolumes);
            var paths = ArchivePaths(job);
            lock (_transferRouteGate)
            {
                if (!OtherForegroundTransfer(job) && TryHandoffArchive(job, attempt, volumes, paths, unavailable)) return;
                if (WaitForSelectedBackground(job, attempt, "Archive handoff is waiting for resident capacity") ||
                    DeferWhileResidentTransfers(job, attempt)) return;
                lock (_lock) job.ForegroundTransfer = true;
            }
            {
                long missing = 0;
                for (int i = 0; i < volumes.Count; i++)
                    if (!File.Exists(paths[i])) missing = checked(missing + Math.Max(0, volumes[i].Size));
                RequireTransferSpace(missing, true);
            }
            for (int i = 0; i < volumes.Count; i++)
            {
                if (CommitRequestedStop(job, attempt)) return;
                if (WaitForSelectedBackground(job, attempt, "Background selected; remaining archive volumes are waiting for resident handoff")) return;
                ArchiveVolume volume = volumes[i];
                string path = paths[i];
                if (!File.Exists(path))
                {
                    lock (_lock) { job.State = DlState.Downloading; job.StatusText = "RAR volume " + (i + 1) + "/" + volumes.Count + " · Keep SSPI open"; }
                    string direct = ResolveVolume(volume, job, attempt, i, volumes.Count, unavailable);
                    string part = path + ".part";
                    long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
                    long baseBytes = 0;
                    for (int k = 0; k < volumes.Count; k++)
                    {
                        if (k < i)
                        {
                            try { if (File.Exists(paths[k])) baseBytes = checked(baseBytes + new FileInfo(paths[k]).Length); }
                            catch { }
                        }
                    }
                    string statsPhase = "volume:" + i;
                    lock (_lock)
                    {
                        if (job.AttemptId != attempt) return;
                        job.StatsInitialized = false;
                        UpdateTransferStats(job, checked(baseBytes + offset),
                            ArchiveTransferTotal(volumes, baseBytes, i, 0), statsPhase, TransferClockMs(), 0);
                    }
                    NetHttp.DownloadFileResumable(direct, path, offset,
                        null,
                        () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested,
                        60000, null, null, volume.Sha256, volume.Sha256,
                        (done, total, received) =>
                        {
                            lock (_lock)
                            {
                                if (job.AttemptId != attempt || job.PauseRequested || job.CancelRequested) return;
                                UpdateLiveTransferStats(job, checked(baseBytes + done),
                                    ArchiveTransferTotal(volumes, baseBytes, i, total), received, statsPhase, TransferClockMs());
                                job.StatusText = "RAR volume " + (i + 1) + "/" + volumes.Count + " · Keep SSPI open";
                            }
                        });
                }
                var probe = new DlItem { DestPath = path, ExpectedSha256 = volume.Sha256,
                    ExpectedByteSize = volume.Size };
                string error;
                if (!VerifyCandidateFile(probe, out error)) throw new InvalidDataException("Volume " + (i + 1) + ": " + error);
                PackageObjectKind kind = PackageArchive.Detect(path);
                bool singlePayload = volumes.Count == 1 && (kind == PackageObjectKind.Pkg || kind == PackageObjectKind.Zip || kind == PackageObjectKind.SevenZip);
                if (kind != PackageObjectKind.Rar4 && kind != PackageObjectKind.Rar5 && !singlePayload)
                    throw new InvalidDataException("Volume " + (i + 1) + " has an unsupported file header");
                if (singlePayload) {
                    lock (_lock) { job.DestPath = path; job.ArchiveVolumes = ""; }
                }
                SaveManifest();
            }
            FinishDownloadedObject(job, attempt);
        }

        internal static long ArchiveTransferTotal(IList<ArchiveVolume> volumes, long completedBytes, int index, long currentTotal)
        {
            if (volumes == null || index < 0 || index >= volumes.Count || completedBytes < 0) return 0;
            long total = completedBytes;
            for (int i = index; i < volumes.Count; i++)
            {
                long size = i == index && currentTotal > 0 ? currentTotal : volumes[i].Size;
                // A partial sum is not the complete archive size and cannot support its ETA.
                if (size <= 0 || total > long.MaxValue - size) return 0;
                total += size;
            }
            return total;
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

        /// <summary>Resume path for SSPI-06: if a previous run moved extracted PKGs
        /// to their final paths (marker kept because the stop was a pause), validate
        /// and adopt them without extracting again. Returns false when a fresh
        /// extraction is required.</summary>
        bool TryAdoptMovedFanOutChildren(DlItem job, int attempt, List<PendingLocalChild> pending)
        {
            string[] paths;
            lock (_lock)
            {
                if (string.IsNullOrEmpty(job.FanOutPendingPaths)) return false;
                paths = job.FanOutPendingPaths.Split(new[] { '\n' },
                    StringSplitOptions.RemoveEmptyEntries);
            }
            if (paths.Length == 0) return false;
            var adopted = new List<PendingLocalChild>();
            for (int i = 0; i < paths.Length; i++)
            {
                if (CommitRequestedStop(job, attempt)) return false;
                if (!IsOwnedDownloadPath(paths[i]) || !File.Exists(paths[i])) return false;
                var probe = new DlItem { DestPath = paths[i], TitleId = job.TitleId, Kind = job.Kind };
                PkgContentKind actualKind;
                string actualKindName;
                string contentId;
                string actualTitleId;
                long size;
                string error;
                if (!TryValidateLocalPackage(probe, out actualKind, out actualKindName,
                    out contentId, out actualTitleId, out size, out error))
                    return false;
                if (string.IsNullOrEmpty(actualTitleId)) actualTitleId = job.TitleId;
                var child = new DlItem
                {
                    Id = job.Id + "_entry_" + (i + 1),
                    TitleId = actualTitleId,
                    Name = job.Name,
                    ImageUrl = job.ImageUrl,
                    Kind = actualKindName,
                    Label = Path.GetFileName(paths[i]),
                    HosterUrl = (job.HosterUrl ?? "") +
                        ((job.HosterUrl ?? "").IndexOf('#') >= 0 ? "&" : "#") +
                        "adopted=" + (i + 1),
                    SourceId = job.SourceId,
                    SourceVersion = job.SourceVersion,
                    CandidateId = (string.IsNullOrEmpty(job.CandidateId) ? job.Id : job.CandidateId) +
                        ":adopted:" + (i + 1),
                    AccessType = "Extracted",
                    ExpectedContentId = contentId,
                    SourcePageUrl = job.SourcePageUrl,
                    SourceAttribution = job.SourceAttribution,
                    DestPath = paths[i],
                    State = DlState.Queued,
                    Done = size,
                    Total = size,
                    StatusText = "Recovered extracted package",
                    BgftTaskId = -1
                };
                if (job.LocalSource) ApplyLocalArchiveMetadata(child, paths[i]);
                adopted.Add(new PendingLocalChild
                {
                    Item = child,
                    ExtractedPath = paths[i],
                    Ordinal = i,
                    Priority = actualKind == PkgContentKind.BaseGame ? 0 :
                        actualKind == PkgContentKind.Patch ? 1 : 2
                });
            }
            pending.AddRange(adopted);
            return true;
        }

        void FanOutArchive(DlItem job, int attempt)
        {
            if (WaitForSelectedBackground(job, attempt, "Archive extraction requires a resident handoff; choose In-app mode to extract this archive here")) return;
            string staging = Path.Combine(AppSettings.DownloadDir, ".extract", UrlTag(job.Id));
            var pending = new List<PendingLocalChild>();
            bool committed = false;
            long downloadedBytes = job.Done, downloadTotal = job.Total;
            try
            {
                // Resume after pause: moved PKGs survive (never deleted on pause),
                // so adopt them directly instead of extracting again.
                if (TryAdoptMovedFanOutChildren(job, attempt, pending))
                {
                    lock (_lock) {
                        if (job.AttemptId != attempt || job.CancelRequested || job.PauseRequested) { CommitRequestedStop(job, attempt); return; }
                        job.State = DlState.Finalizing; job.StatusText = "Recovered extracted packages...";
                    }
                    goto AdoptedPlan;
                }
                var paths = ArchivePaths(job);
                // Older failed scans saved 0/0 or expanded-byte counters. Recover
                // the compressed size from retained inputs before callbacks run.
                long retainedBytes = 0;
                foreach (string path in paths) retainedBytes = checked(retainedBytes + new FileInfo(path).Length);
                downloadedBytes = downloadTotal = retainedBytes;
                PrepareExtractionDirectory(staging);
                lock (_lock) {
                    if (job.AttemptId != attempt || job.CancelRequested || job.PauseRequested) { CommitRequestedStop(job, attempt); return; }
                    job.State = DlState.Finalizing; job.StatusText = "Extracting packages...";
                }
                var volumes = ArchiveVolumeSet.Decode(job.ArchiveVolumes);
                List<string> volumeNames = null;
                if (volumes.Count == paths.Count) {
                    volumeNames = new List<string>();
                    foreach (var volume in volumes) volumeNames.Add(volume.Name);
                }
                LogBgftEvent("archive-extraction-start", job, "Archive extraction started; RAR uses native UnRAR");
                List<PackageArchiveEntry> entries = PackageArchive.ExtractPackages(paths, staging,
                    () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested,
                    (done, total) => { lock (_lock) {
                        if (job.AttemptId != attempt || job.CancelRequested || job.PauseRequested) return;
                        UpdateTransferStats(job, done, total, "extracting", TransferClockMs(), 0);
                        job.StatusText = "Extracting packages" + (total > 0 ? " - " + Math.Min(100, done * 100.0 / total).ToString("0") + "%" : "...");
                    } }, job.ArchivePassword, ArchiveFallbacks(job), volumeNames,
                    detail => LogBgftEvent("archive-decoder", job, detail));
                LogBgftEvent("archive-extraction-complete", job, "Extracted package count=" + entries.Count);
                for (int i = 0; i < entries.Count; i++)
                {
                    PackageArchiveEntry entry = entries[i];
                    var probe = new DlItem { DestPath = entry.ExtractedPath, TitleId = job.TitleId };
                    PkgContentKind actualKind;
                    string actualKindName;
                    string contentId;
                    string actualTitleId;
                    long size;
                    string error;
                    if (!TryValidateLocalPackage(probe, out actualKind, out actualKindName,
                        out contentId, out actualTitleId, out size, out error))
                        throw new InvalidDataException(entry.Name + ": " + error);
                    if (string.IsNullOrEmpty(actualTitleId)) actualTitleId = job.TitleId;
                    string discriminator = (job.HosterUrl ?? "") +
                        ((job.HosterUrl ?? "").IndexOf('#') >= 0 ? "&" : "#") +
                        "entry=" + (i + 1) + "-" + UrlTag(entry.Name);
                    var child = new DlItem
                    {
                        Id = job.Id + "_entry_" + (i + 1),
                        TitleId = actualTitleId,
                        Name = job.Name,
                        ImageUrl = job.ImageUrl,
                        Kind = actualKindName,
                        Label = entry.Name,
                        HosterUrl = discriminator,
                        SourceId = job.SourceId,
                        SourceVersion = job.SourceVersion,
                        CandidateId = (string.IsNullOrEmpty(job.CandidateId) ? job.Id : job.CandidateId) +
                            ":entry:" + (i + 1),
                        AccessType = "Extracted",
                        ExpectedByteSize = size,
                        ExpectedContentId = contentId,
                        SourcePageUrl = job.SourcePageUrl,
                        SourceAttribution = job.SourceAttribution,
                        DestPath = BuildDerivedDestination(actualTitleId, actualKindName, discriminator),
                        State = DlState.Queued,
                        Done = size,
                        Total = size,
                        StatusText = "Extracted and validated",
                        BgftTaskId = -1
                    };
                    if (job.LocalSource) ApplyLocalArchiveMetadata(child, entry.ExtractedPath);
                    pending.Add(new PendingLocalChild
                    {
                        Item = child,
                        ExtractedPath = entry.ExtractedPath,
                        Ordinal = i,
                        Priority = actualKind == PkgContentKind.BaseGame ? 0 :
                            actualKind == PkgContentKind.Patch ? 1 : 2
                    });
                }

            AdoptedPlan:
                if (!string.IsNullOrWhiteSpace(job.ExpectedContentId))
                {
                    if (pending.Count != 1)
                        throw new InvalidDataException("Archive content ID promise is ambiguous");
                    if (!string.Equals(job.ExpectedContentId.Trim(),
                        pending[0].Item.ExpectedContentId, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("PKG content ID mismatch");
                }

                // Match the resident planner: read each patch APP_VER, reject
                // competing same-version patches (update vs backport ambiguity),
                // and install patches in ascending version order.
                var patchVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in pending)
                {
                    if (p.Priority != 1) continue;
                    string v = "";
                    try { v = PkgIntegrity.SfoValue(PkgIntegrity.Entry(p.ExtractedPath, 0x1000), "APP_VER") ?? ""; } catch { }
                    p.Item.PackageVersion = v;
                    if (v.Length == 0) continue;
                    string first;
                    string identity = (p.Item.ExpectedContentId ?? "") + "|" + v;
                    if (patchVersions.TryGetValue(identity, out first))
                        throw new InvalidDataException("Archive contains competing updates for version " + v +
                            " (" + first + ", " + (p.Item.Label ?? "") + "); no packages installed");
                    patchVersions[identity] = p.Item.Label ?? "";
                }

                pending.Sort((left, right) =>
                {
                    int byKind = left.Priority.CompareTo(right.Priority);
                    if (byKind != 0) return byKind;
                    if (left.Priority == 1)
                    {
                        Version lv, rv;
                        bool lok = Version.TryParse(left.Item.PackageVersion ?? "", out lv);
                        bool rok = Version.TryParse(right.Item.PackageVersion ?? "", out rv);
                        if (lok && rok)
                        {
                            int c = lv.CompareTo(rv);
                            if (c != 0) return c;
                        }
                        else if (lok) return -1;
                        else if (rok) return 1;
                    }
                    return left.Ordinal.CompareTo(right.Ordinal);
                });

                for (int i = 1; i < pending.Count; i++)
                    pending[i].Item.InstallAfterId = pending[i - 1].Item.Id;

                if (CommitRequestedStop(job, attempt)) return;
                lock (_lock)
                {
                    var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < pending.Count; i++)
                    {
                        string path = NormalizePath(pending[i].Item.DestPath);
                        if (!claimed.Add(path))
                            throw new IOException("Duplicate extraction target");
                        foreach (DlItem existing in _items)
                            if (!object.ReferenceEquals(existing, job) &&
                                SamePath(existing.DestPath, pending[i].Item.DestPath))
                                throw new IOException("Extraction target is already queued");
                    }
                    job.FanOutPendingPaths = EncodePendingPaths(pending);
                }
                if (!SaveManifest())
                {
                    lock (_lock) job.FanOutPendingPaths = "";
                    throw new IOException("Could not save extraction recovery marker");
                }

                for (int i = 0; i < pending.Count; i++)
                {
                    PendingLocalChild item = pending[i];
                    if (File.Exists(item.Item.DestPath))
                    {
                        lock (_lock)
                            foreach (DlItem existing in _items)
                                if (!object.ReferenceEquals(existing, job) &&
                                    SamePath(existing.DestPath, item.Item.DestPath))
                                    throw new IOException("Extraction target is already queued");
                        PkgContentKind existingKind;
                        string existingKindName;
                        string existingContentId;
                        string existingTitleId;
                        long existingSize;
                        string existingError;
                        if (!TryValidateLocalPackage(item.Item, out existingKind, out existingKindName,
                            out existingContentId, out existingTitleId, out existingSize, out existingError))
                            throw new IOException("Extraction target collision: " + existingError);
                        // Adopted rows already live at DestPath; only delete a separate staging copy.
                        if (!SamePath(item.ExtractedPath, item.Item.DestPath))
                            File.Delete(item.ExtractedPath);
                    }
                    else
                    {
                        File.Move(item.ExtractedPath, item.Item.DestPath);
                    }
                }

                if (CommitRequestedStop(job, attempt))
                {
                    // Pause keeps moved PKGs (adopted on resume via FanOutPendingPaths);
                    // only cancel/terminal stops clean up.
                    DlState stopped;
                    lock (_lock) stopped = job.State;
                    if (stopped != DlState.Paused && TryCleanupFanOutPending(job)) SaveManifest();
                    return;
                }
                string commitError;
                bool stopRequested;
                var children = new List<DlItem>();
                for (int i = 0; i < pending.Count; i++) children.Add(pending[i].Item);
                if (!ReplaceClaimedJobWithChildren(job, attempt, children,
                    out commitError, out stopRequested))
                {
                    if (stopRequested)
                    {
                        TryCleanupFanOutPending(job);
                        CommitRequestedStop(job, attempt);
                        return;
                    }
                    throw new IOException(commitError);
                }
                committed = true;
                FinalizeFanOutSource(job);
                User.NotifyToast("Queued " + children.Count +
                    (children.Count == 1 ? " package" : " packages"));
            }
            catch (Exception ex)
            {
                if (!committed)
                {
                    TryCleanupFanOutPending(job);
                    if (!CommitRequestedStop(job, attempt))
                    {
                        lock (_lock)
                        {
                            if (job.AttemptId == attempt)
                                RestoreRetainedArchiveProgress(job, downloadedBytes, downloadTotal);
                        }
                        SetFailureIfCurrent(job, attempt, ex.Message);
                    }
                }
            }
            finally
            {
                try { DeleteExtractionDirectory(staging); } catch { }
            }
        }

        internal static void RestoreRetainedArchiveProgress(DlItem job, long downloadedBytes, long downloadTotal)
        {
            // Extraction counts expanded bytes and starts at zero. A failed scan
            // must not erase the completed compressed download from the queue.
            job.Done = Math.Max(0, downloadedBytes);
            job.Total = Math.Max(job.Done, downloadTotal);
            job.StatsInitialized = false;
            job.BytesPerSec = 0;
            job.EtaSeconds = 0;
        }

        void FanOutLinks(DlItem job, int attempt)
        {
            try
            {
                long length = new FileInfo(job.DestPath).Length;
                if (length > 2L * 1024 * 1024)
                {
                    RetainUnrecognizedDownload(job, attempt, "Not a PKG or archive");
                    return;
                }
                List<string> parsed = DownloadLinkParser.Extract(File.ReadAllText(job.DestPath));
                var links = new List<string>();
                for (int i = 0; i < parsed.Count; i++)
                    if (!string.Equals(parsed[i], job.HosterUrl, StringComparison.OrdinalIgnoreCase))
                        links.Add(parsed[i]);
                if (links.Count == 0)
                {
                    RetainUnrecognizedDownload(job, attempt, "Not a PKG or archive");
                    return;
                }
                bool promisedIntegrity = !string.IsNullOrWhiteSpace(job.ExpectedSha256) ||
                    job.ExpectedByteSize > 0 || !string.IsNullOrWhiteSpace(job.ExpectedContentId);
                if (links.Count > 1 && promisedIntegrity)
                {
                    SetFailureIfCurrent(job, attempt,
                        "Multiple package links cannot share one integrity promise");
                    return;
                }

                var children = new List<DlItem>();
                for (int i = 0; i < links.Count; i++)
                {
                    string url = links[i];
                    string label = LinkLabel(url, job.Label);
                    var child = new DlItem
                    {
                        Id = job.Id + "_link_" + (i + 1),
                        TitleId = job.TitleId,
                        Name = job.Name,
                        ImageUrl = job.ImageUrl,
                        Kind = job.Kind,
                        Label = label,
                        HosterUrl = url,
                        SourceId = job.SourceId,
                        SourceVersion = job.SourceVersion,
                        CandidateId = (string.IsNullOrEmpty(job.CandidateId) ? job.Id : job.CandidateId) +
                            ":link:" + (i + 1),
                        AccessType = LinkAccessType(url),
                        ExpectedSha256 = links.Count == 1 ? job.ExpectedSha256 : "",
                        ExpectedByteSize = links.Count == 1 ? job.ExpectedByteSize : 0,
                        ExpectedContentId = links.Count == 1 ? job.ExpectedContentId : "",
                        SourcePageUrl = job.SourcePageUrl,
                        ExpiresUtc = job.ExpiresUtc,
                        SourceAttribution = job.SourceAttribution,
                        DestPath = BuildDerivedDestination(job.TitleId, job.Kind, url),
                        State = DlState.Queued,
                        StatusText = "Queued from link response",
                        BgftTaskId = -1
                    };
                    children.Add(child);
                }

                if (children.Count > 1)
                {
                    var volumes = new List<ArchiveVolume>();
                    foreach (DlItem child in children)
                    {
                        string name = ArchiveVolumeSet.FileName(child.HosterUrl, child.Label);
                        if (string.IsNullOrEmpty(name)) { volumes.Clear(); break; }
                        volumes.Add(new ArchiveVolume { Name = name, Url = child.HosterUrl,
                            AccessType = child.AccessType });
                    }
                    if (volumes.Count == children.Count)
                    {
                        volumes = ArchiveVolumeSet.Validate(volumes);
                        DlItem first = children.Find(child => child.HosterUrl == volumes[0].Url);
                        first.ArchiveVolumes = ArchiveVolumeSet.Encode(volumes);
                        first.Label = volumes[0].Name + " (" + volumes.Count + " volumes)";
                        children.Clear(); children.Add(first);
                    }
                }

                string commitError;
                bool stopRequested;
                if (!ReplaceClaimedJobWithChildren(job, attempt, children,
                    out commitError, out stopRequested))
                {
                    if (stopRequested)
                    {
                        CommitRequestedStop(job, attempt);
                        return;
                    }
                    SetFailureIfCurrent(job, attempt, commitError);
                    return;
                }
                FinalizeFanOutSource(job);
                User.NotifyToast("Queued " + children.Count +
                    (children.Count == 1 ? " link" : " links"));
            }
            catch (Exception ex)
            {
                SetFailureIfCurrent(job, attempt, ex.Message);
            }
        }

        bool ReplaceClaimedJobWithChildren(DlItem parent, int attempt, List<DlItem> children,
            out string error, out bool stopRequested)
        {
            error = null;
            stopRequested = false;
            if (children == null || children.Count == 0)
            {
                error = "No package rows were produced";
                return false;
            }
            lock (_lock)
            {
                if (parent.AttemptId != attempt)
                {
                    error = "Queue item changed during extraction";
                    return false;
                }
                int parentIndex = _items.IndexOf(parent);
                if (parentIndex < 0)
                {
                    error = "Queue item was removed during extraction";
                    return false;
                }
                if (parent.CancelRequested || parent.PauseRequested)
                {
                    stopRequested = true;
                    return false;
                }
                var childIds = new HashSet<string>(StringComparer.Ordinal);
                var childPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < children.Count; i++)
                {
                    DlItem child = children[i];
                    string path = NormalizePath(child.DestPath);
                    if (!childIds.Add(child.Id) || !childPaths.Add(path))
                    {
                        error = "Duplicate package row in downloaded object";
                        return false;
                    }
                    foreach (DlItem existing in _items)
                    {
                        if (object.ReferenceEquals(existing, parent)) continue;
                        if (string.Equals(existing.Id, child.Id, StringComparison.Ordinal) ||
                            SamePath(existing.DestPath, child.DestPath) ||
                            (string.Equals(existing.TitleId, child.TitleId, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(existing.Kind, child.Kind, StringComparison.OrdinalIgnoreCase) &&
                             string.Equals(existing.HosterUrl, child.HosterUrl,
                                StringComparison.OrdinalIgnoreCase)))
                        {
                            error = "Package link is already queued";
                            return false;
                        }
                    }
                }

                DlState oldState = parent.State;
                string oldStatus = parent.StatusText;
                string oldAccessType = parent.AccessType;
                string oldError = parent.Error;
                parent.State = DlState.Completed;
                parent.StatusText = "Downloaded source cleanup pending";
                parent.AccessType = "FanOutSource";
                parent.Error = null;
                for (int i = 0; i < children.Count; i++)
                    _items.Insert(parentIndex, children[i]);
                if (SaveManifest()) return true;
                for (int i = 0; i < children.Count; i++) _items.Remove(children[i]);
                parent.State = oldState;
                parent.StatusText = oldStatus;
                parent.AccessType = oldAccessType;
                parent.Error = oldError;
                error = "Could not save extracted package rows";
                return false;
            }
        }

        void FinalizeFanOutSource(DlItem parent)
        {
            if (!parent.LocalSource)
                foreach (string path in ArchivePaths(parent)) DeleteDownloadFiles(path);
            DeleteDownloadFiles(parent);
            lock (_lock)
            {
                if (!File.Exists(parent.DestPath)) _items.Remove(parent);
                else parent.StatusText = parent.LocalSource ? "Packages queued; USB source retained" : "Packages queued; source cleanup pending";
            }
            SaveManifest();
        }

        static void ApplyLocalArchiveMetadata(DlItem child, string path)
        {
            LocalInstallSource source = LocalInstallSource.ReadMetadata(path, true);
            child.Name = source.Name; child.ImageUrl = source.ImagePath; child.PackageVersion = source.Version;
            if (source.ExpectedKind == 8 && System.Text.RegularExpressions.Regex.IsMatch(child.Label ?? "", @"(?:^|[^A-Za-z0-9])backport(?:[^A-Za-z0-9]|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                child.Kind = "backport";
        }

        static string BuildDerivedDestination(string titleId, string kind, string identity)
        {
            string safe = (titleId ?? "") + "_" + (kind ?? "game") + "_" +
                UrlTag(identity) + ".pkg";
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(AppSettings.DownloadDir, safe);
        }

        static string EncodePendingPaths(List<PendingLocalChild> pending)
        {
            var paths = new StringBuilder();
            for (int i = 0; i < pending.Count; i++)
            {
                if (i > 0) paths.Append('\n');
                paths.Append(pending[i].Item.DestPath);
            }
            return paths.ToString();
        }

        internal static string LinkAccessType(string url)
        {
            Uri uri;
            if (Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                string path = uri.AbsolutePath ?? "";
                if (path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".7zip", StringComparison.OrdinalIgnoreCase)) return "Direct";
            }
            return "HosterLanding";
        }

        static string LinkLabel(string url, string fallback)
        {
            try
            {
                string name = Path.GetFileName(new Uri(url).AbsolutePath);
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch { }
            return string.IsNullOrEmpty(fallback) ? "Package link" : fallback;
        }

        static void PrepareExtractionDirectory(string path)
        {
            if (!IsExtractionDirectory(path)) throw new IOException("Unsafe extraction path");
            if (Directory.Exists(path)) DeleteExtractionDirectory(path);
            Directory.CreateDirectory(path);
        }

        static bool IsExtractionDirectory(string path)
        {
            string root = NormalizePath(Path.Combine(AppSettings.DownloadDir, ".extract"));
            string full = NormalizePath(path);
            string leaf = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            if (leaf.Length != 12) return false;
            for (int i = 0; i < leaf.Length; i++)
            {
                char c = leaf[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') ||
                    (c >= 'A' && c <= 'F'))) return false;
            }
            return string.Equals(NormalizePath(Path.GetDirectoryName(full)), root,
                StringComparison.OrdinalIgnoreCase);
        }

        static void DeleteExtractionDirectory(string path)
        {
            if (!Directory.Exists(path)) return;
            if (!IsExtractionDirectory(path)) throw new IOException("Unsafe extraction path");
            foreach (string file in Directory.GetFiles(path)) File.Delete(file);
            Directory.Delete(path, false);
        }

        bool TryCleanupFanOutPending(DlItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FanOutPendingPaths)) return true;
            string[] paths = item.FanOutPendingPaths.Split(new[] { '\n' },
                StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < paths.Length; i++)
                if (!IsOwnedDownloadPath(paths[i])) return false;
            lock (_lock)
            {
                for (int i = 0; i < paths.Length; i++)
                {
                    bool claimedByOther = false;
                    foreach (DlItem existing in _items)
                        if (!object.ReferenceEquals(existing, item) &&
                            SamePath(existing.DestPath, paths[i]))
                        {
                            claimedByOther = true;
                            break;
                        }
                    if (claimedByOther) continue;
                    DeleteDownloadFiles(paths[i]);
                    if (File.Exists(paths[i])) return false;
                }
                item.FanOutPendingPaths = "";
            }
            return true;
        }

        void CleanupFanOutStateOnStartup()
        {
            var sources = new List<DlItem>();
            var pending = new List<DlItem>();
            lock (_lock)
                foreach (DlItem item in _items)
                {
                    bool source = string.Equals(item.AccessType, "FanOutSource",
                        StringComparison.OrdinalIgnoreCase);
                    if (source) sources.Add(item);
                    else if (!string.IsNullOrEmpty(item.FanOutPendingPaths)) pending.Add(item);
                }
            bool changed = false;
            for (int i = 0; i < pending.Count; i++)
            {
                DlItem item = pending[i];
                if (TryCleanupFanOutPending(item)) changed = true;
                else
                {
                    item.StatusText = "Extracted package cleanup pending";
                    changed = true;
                }
            }
            for (int i = 0; i < sources.Count; i++)
            {
                DlItem source = sources[i];
                DeleteDownloadFiles(source);
                lock (_lock)
                {
                    if (!File.Exists(source.DestPath))
                    {
                        _items.Remove(source);
                        changed = true;
                    }
                    else source.StatusText = source.LocalSource ? "Packages queued; USB source retained" : "Packages queued; source cleanup pending";
                }
            }
            try
            {
                string root = Path.Combine(AppSettings.DownloadDir, ".extract");
                if (Directory.Exists(root))
                    foreach (string directory in Directory.GetDirectories(root))
                        if (IsExtractionDirectory(directory)) DeleteExtractionDirectory(directory);
            }
            catch { }
            if (changed) SaveManifest();
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
            if (kind == PkgContentKind.Patch) return string.Equals(requested, "backport", StringComparison.OrdinalIgnoreCase) ? "backport" : "update";
            if (kind == PkgContentKind.AddOn) return "dlc";
            return "game";
        }

        void HandleValidatedLocalPackage(DlItem job, int attempt, PkgContentKind actualKind,
            string actualKindName, string contentId, string actualTitleId, long size)
        {
            if (BackgroundSelected)
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
                        (other.State == DlState.Submitted && !other.InstallOrderReady))) busy = true;
                if (busy) { RequeueValidatedLocalPackage(job, attempt, size); return; }
                try { HandleValidatedLocalPackageCore(job,attempt,actualKind,actualKindName,contentId,actualTitleId,size); }
                finally { _nextInstallHandoffAt=TransferClockMs()+3000; }
            }
        }

        void HandleValidatedLocalPackageCore(DlItem job, int attempt, PkgContentKind actualKind,
            string actualKindName, string contentId, string actualTitleId, long size)
        {
            lock (_packageInstallGate)
            {
                if (CommitRequestedStop(job, attempt)) return;
                lock (_lock)
                {
                    if (job.AttemptId != attempt) return;
                    job.Kind = actualKindName;
                    if (!string.IsNullOrEmpty(actualTitleId)) job.TitleId = actualTitleId;
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
                    actualKind == PkgContentKind.Patch ? "gp" : actualKind == PkgContentKind.AddOn ? "ac" : "gd", out metadataError))
                { MarkInstallFailed(job.Id, metadataError, attempt); return; }
                if (!InstallDependencyReady(job))
                {
                    lock (_lock) { job.State = DlState.Queued; job.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(2).Ticks; }
                    SaveManifest(); return;
                }
                if (actualKind == PkgContentKind.Patch && _loopback.IsOwnedByOther(job.Id))
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
                bool useLoopback = actualKind == PkgContentKind.Patch;
                bool baseReady = !PkgInstallPolicy.RequiresInstalledBase(actualKind) ||
                    (!string.IsNullOrEmpty(actualTitleId) && PkgInstaller.IsTitleInstalled(actualTitleId));
                string fallbackReason = null;
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
                                job.StatusText = "Registering verified update for installation...";
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
                        if (PkgInstaller.TryStartLoopbackBgftDownload(url, actualTitleId, contentId,
                            displayName, subType, size, out taskId, out startError, PkgIntegrity.PackageType(job.DestPath)))
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
                                    job.StatusText = "PS4 importing verified package";
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
                        bool unresolvedTask = taskId >= 0 || registrationError.StartsWith(
                            "BGFT_UNRESOLVED:", StringComparison.Ordinal);
                        if (unresolvedTask)
                        {
                            if (taskId >= 0)
                                lock (_lock) job.BgftTaskId = taskId;
                            if (!TryCancelBackgroundIdentity(job, contentId, subType,
                                registrationError))
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
                if (actualKind == PkgContentKind.Patch)
                {
                    string error = fallbackReason ?? "BGFT update registration failed; PKG retained";
                    if (!RetryLocalInstall(job, attempt, error)) MarkInstallFailed(job.Id, error, attempt);
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
                InstallValidatedLocalPackage(job, attempt, actualKind, actualKindName, actualTitleId);
            }
        }

        void RequeueValidatedLocalPackage(DlItem job, int attempt, long size)
        {
            lock (_lock)
            {
                if (job.AttemptId != attempt) return;
                job.State = DlState.Queued;
                job.Done = job.Total = size;
                job.Error = null;
                job.StatusText = "Waiting for PS4 installer; file retained";
                job.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(3).Ticks;
                _nerd.Finalizing = false;
            }
            SaveManifest();
        }

        void InstallValidatedLocalPackage(DlItem job, int attempt, PkgContentKind actualKind,
            string actualKindName, string actualTitleId)
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
            InstallOutcome outcome = PkgInstaller.InstallLocal(job.DestPath, actualTitleId,
                actualKindName, out installedTitleId, out error, out taskId, false);
            if (outcome == InstallOutcome.Started)
            {
                if (taskId < 0)
                {
                    MarkInstallAccepted(job.Id, "Sent to PS4 — verification pending · PKG kept", -1, attempt);
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
            if (CommitRequestedStop(job, attempt)) return;
            if (outcome == InstallOutcome.AlreadyInstalled)
            {
                if (actualKind == PkgContentKind.AddOn && PkgInstaller.IsAddonInstalled(job.DestPath, true))
                { MarkInstalled(job.Id, "Add-on installation confirmed by PS4 content", false, attempt); return; }
                MarkAlreadyInstalled(job.Id, "Already installed; local PKG kept", attempt);
                return;
            }
            if (outcome == InstallOutcome.NotReady && RetryLocalInstall(job, attempt, error)) return;
            MarkInstallFailed(job.Id, error ?? "Local PKG install failed", attempt);
        }

        public void TrackLocalInstallTask(string id, int attempt, int taskId)
        {
            lock (_lock)
            {
                var item = Find(id);
                if (item == null || item.AttemptId != attempt || taskId < 0) return;
                string content;
                if (PkgValidator.TryGetContentId(item.DestPath, out content)) item.BgftContentId = content;
                item.BgftTaskId = taskId;
                item.BgftSubType = PkgValidator.BgftSubTypeForKind(item.Kind);
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
                if (!PkgInstaller.CancelBackground(item.BgftTaskId, item.BgftContentId, item.BgftSubType, out activeTask, out error))
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
            lock (_lock)
            {
                if (job.AttemptId != attempt || job.CancelRequested || job.PauseRequested ||
                    job.Background || job.InstallRetries >= 3 || !File.Exists(job.DestPath)) return false;
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

        static string ClipMsg(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= n ? s : s.Substring(0, n);
        }

        internal static void UpdateLiveTransferStats(DlItem item, long done, long total, long received, string phase, long nowMs)
        {
            if (received < 0 || !IsTransferStatsPhase(phase))
            {
                item.LiveMeter = null;
                UpdateTransferStats(item, done, total, phase, nowMs, 0);
                return;
            }
            if (!item.StatsInitialized || item.LiveMeter == null) item.LiveMeter = new LiveTransferMeter();
            item.LiveMeter.Sample(received, done, total, phase, item.AttemptId, nowMs);
            item.Done = Math.Max(0, done);item.Total = Math.Max(0, total);
            item.BytesPerSec = item.LiveMeter.Rate;item.EtaSeconds = item.LiveMeter.Eta;
            item.StatsInitialized = true;item.StatsPhase = phase;item.StatsAttempt = item.AttemptId;
            item.StatsAt = nowMs;item.StatsAdvancedAt = item.LiveMeter.AdvancedAt;
        }

        internal static void UpdateTransferStats(DlItem item, long done, long total, string phase, long nowMs, int nativeEta)
        {
            done = Math.Max(0, done); total = Math.Max(0, total); nowMs = Math.Max(0, nowMs);
            item.Done = done; item.Total = total;
            bool transferring = IsTransferStatsPhase(phase) || phase == "extracting";
            long elapsed = nowMs - item.StatsAt;
            if (!transferring || !item.StatsInitialized || elapsed < 0 || elapsed > 15000 || done < item.StatsObservedDone ||
                item.StatsPhase != phase || item.StatsAttempt != item.AttemptId)
            {
                item.StatsAt = item.StatsAdvancedAt = nowMs; item.StatsDone = done; item.StatsTotal = total;
                item.StatsObservedDone = done; item.StatsInitialized = true;
                item.StatsPhase = phase; item.StatsAttempt = item.AttemptId;
                item.BytesPerSec = 0; item.EtaSeconds = 0;
                return;
            }
            if (done > item.StatsObservedDone) item.StatsAdvancedAt = nowMs;
            item.StatsObservedDone = done; item.StatsTotal = total;
            if ((total > 0 && done >= total) || nowMs - item.StatsAdvancedAt >= 5000)
            {
                item.StatsAt = nowMs; item.StatsDone = done;
                item.BytesPerSec = 0; item.EtaSeconds = 0;
                return;
            }
            // Warm up for two seconds, then smooth by elapsed time rather than callback frequency.
            if (elapsed >= (item.BytesPerSec > 0 ? 1000 : 2000))
            {
                double rate = (done - item.StatsDone) * 1000.0 / elapsed;
                double weight = 1.0 - Math.Exp(-elapsed / 5000.0);
                item.BytesPerSec = item.BytesPerSec > 0
                    ? item.BytesPerSec + (rate - item.BytesPerSec) * weight : rate;
                item.StatsAt = nowMs; item.StatsDone = done;
            }
            // BGFT's restSec values may describe a different phase or be stale. Only recent
            // observed byte movement can support the download estimate shown by SSPI.
            item.EtaSeconds = TransferEtaSeconds(done, total, item.BytesPerSec);
        }

        static bool IsTransferStatsPhase(string phase)
        {
            return phase == "downloading" || phase == "feeding" || phase == "bgft" ||
                phase == "foreground" || (phase != null && phase.StartsWith("volume:", StringComparison.Ordinal));
        }

        internal static bool HasCurrentTransferStats(DlItem item, long nowMs)
        {
            return item != null && item.StatsInitialized &&
                (item.State == DlState.Downloading || (item.State == DlState.Finalizing && item.StatsPhase == "extracting")) &&
                !item.PauseRequested && !item.CancelRequested && item.StatsAttempt == item.AttemptId &&
                (IsTransferStatsPhase(item.StatsPhase) || item.StatsPhase == "extracting") && nowMs >= item.StatsAdvancedAt &&
                nowMs - item.StatsAdvancedAt < 5000;
        }

        internal static int TransferEtaSeconds(long done, long total, double bytesPerSec)
        {
            if (done < 0 || total <= done || bytesPerSec <= 0 ||
                double.IsNaN(bytesPerSec) || double.IsInfinity(bytesPerSec)) return 0;
            double seconds = Math.Ceiling((total - done) / bytesPerSec);
            return seconds >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)seconds);
        }

        static long TransferClockMs()
        {
            return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000.0 / System.Diagnostics.Stopwatch.Frequency));
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
                if (item.ResidentArchive) { ResidentDownloadService.Release(item.Id); item.ResidentArchive = false; }
                ClearBackground(item);
                SspiLog.Write("download", "event=install-confirmed title=" + item.TitleId + " kind=" + item.Kind +
                    " job=" + item.Id + " version=" + version);
            }
            SaveManifest(); return true;
        }

        internal static bool RetainUnconfirmedAddonFiles(DlItem item)
        {
            return item.ResidentAutoInstall && !item.InstallConfirmed &&
                PkgValidator.BgftSubTypeForKind(item.Kind) == 7;
        }

        void RefreshResidentArchive(DlItem item)
        {
            int attempt = item.AttemptId;
            if (item.ResidentRemovePending)
            {
                lock (_lock)
                {
                    if (!object.ReferenceEquals(Find(item.Id), item)) return;
                    if (_activeIds.Contains(item.Id) || ResidentDownloadService.HasJob(item.Id))
                    {
                        ResidentDownloadService.Release(item.Id);
                        item.StatusText = "Removing background job; waiting for worker acknowledgement";
                        return;
                    }
                    // The native owner removes its durable job only after readers, transfer
                    // threads and any owned BGFT task have stopped. Until then keep the data.
                    if (!ResidentDownloadService.HasDownloader) return;
                    // Removing an unconfirmed add-on stops SSPI tracking, but must not
                    // delete input that the PS4 installer may still be consuming.
                    if (!RetainUnconfirmedAddonFiles(item))
                    {
                        if (!TryCleanupFanOutPending(item)) { item.StatusText = "Stored files could not be removed"; return; }
                        DeleteDownloadFiles(item);
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
                    if (ResidentDownloadService.HasStagedJob(item.Id))
                    {
                        ResidentDownloadService.Release(item.Id);
                        item.StatusText = "Waiting for background transfer to stop before retry";
                        return;
                    }
                    ClearBackground(item);
                    item.ResidentArchive = false;
                    item.ResidentRetryPending = false;
                    item.State = item.CancelRequested ? DlState.Canceled : item.PauseRequested ? DlState.Paused : DlState.Queued;
                    item.StatusText = item.CancelRequested ? "Canceled; partial download retained" :
                        item.PauseRequested ? "Paused; partial download retained" : "Preparing retry; verified files retained";
                    item.CancelRequested = item.PauseRequested = false;
                    item.Error = null; item.BytesPerSec = 0; item.EtaSeconds = 0;
                    item.StatsInitialized = false;
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
                ResidentDownloadService.TryCancel(item.Id, out cancelError);
            }
            if (!ResidentDownloadService.TryGetStatus(item.Id, item.ResidentGeneration, out status))
            {
                lock (_lock) {
                    if (!object.ReferenceEquals(Find(item.Id), item) || !AcceptInstallCallback(item, attempt)) return;
                    if (item.CancelRequested && !_activeIds.Contains(item.Id) &&
                        ResidentDownloadService.HasDownloader && !ResidentDownloadService.HasJob(item.Id))
                    {
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
            if (status.State == "awaiting-bgft" && !ResidentDownloadService.OwnsBgftLifetime(item.Id) &&
                ResidentDownloadService.TryAttachPendingBgft(item.Id, out int attachedTask))
            {
                lock (_lock)
                {
                    if (object.ReferenceEquals(Find(item.Id), item) && item.AttemptId == attempt)
                        item.BgftTaskId = attachedTask;
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
                    if (SaveManifest()) ResidentDownloadService.Release(item.Id);
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
                        if (SaveManifest()) ResidentDownloadService.Release(item.Id);
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
                    item.State = item.CancelRequested ? DlState.Canceled : DlState.Submitted;
                    item.CancelRequested = false; item.InstallConfirmed = false; item.InstallOrderReady = false;
                    item.InstallSubmitted = true;
                    item.Background = false; item.BgftResident = false;
                    item.StatusText = "Packages sent to PS4 in order; files retained";
                    release = true; changed = true;
                }
                else if (status.State == "failed" || status.State == "canceled")
                {
                    item.BytesPerSec = 0; item.EtaSeconds = 0;
                    item.State = status.State == "canceled" ? DlState.Canceled : DlState.Failed;
                    item.Error = status.Error;
                    if ((status.Error ?? "").StartsWith("DLC_UNCONFIRMED:", StringComparison.Ordinal))
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
                if (SaveManifest()) { if (release) ResidentDownloadService.Release(item.Id); }
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
                if (item.CancelRequested && !item.ResidentStaged && !item.ResidentArchive)
                {
                    lock (_lock)
                    {
                        if (!object.ReferenceEquals(Find(item.Id), item) || !item.CancelRequested) continue;
                        int activeTask;
                        string cancelError;
                        if (PkgInstaller.CancelBackground(item.BgftTaskId, item.BgftContentId, item.BgftSubType,
                            out activeTask, out cancelError))
                        {
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
                                    FallbackBgftToLocal(cur,
                                        FeederLastError(cur) ?? "loopback server stopped");
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
                    contentId, subType, out progress, out error))
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

                    if (progress.TaskId >= 0 && cur.BgftTaskId != progress.TaskId)
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
                                    FallbackBgftToLocal(cur,
                                        cur.BgftDirect
                                            ? (nearDone ? "direct transfer size mismatch"
                                                : "direct transfer stalled at " + Human(progress.Done))
                                            : (nearDone ? "loopback transfer was not fully served"
                                                : "stalled at " + Human(progress.Done)));
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
                                    if (cur.State != DlState.Submitted)
                                    {
                                        // LocalCopy is not final promotion: keep the task and source alive.
                                        cur.State = DlState.Submitted;
                                        cur.InstallOrderReady = false;
                                        cur.InstallConfirmed = false;
                                        cur.StatusText =
                                            "Sent to PS4 — verify in library · PKG kept";
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
            return basePayloadComplete || ((item.BgftLoopback || item.BgftDirect) &&
                (item.BgftDirect || item.BgftLoopbackServed));
        }

        bool TryCancelBackgroundIdentity(DlItem item, string contentId, int subType, string reason,
            bool failLoopback = true)
        {
            int activeTask = -1;
            string cancelError = null;
            bool canceled = PkgInstaller.CancelBackground(item != null ? item.BgftTaskId : -1,
                    contentId, subType,
                    out activeTask, out cancelError);
            if (canceled) return true;
            if (item == null) return false;
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

        string ManifestPath { get { return Path.Combine(AppSettings.DataDir, "downloads.json"); } }

        bool SaveManifest()
        {
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
                        sb.Append("\"remove_requested\":").Append(it.RemoveRequested ? "true" : "false").Append(',');
                        sb.Append("\"cancel_requested\":").Append(it.CancelRequested ? "true" : "false").Append(',');
                        sb.Append("\"install_submitted\":").Append(it.InstallSubmitted ? "true" : "false").Append(',');
                        sb.Append("\"resident_link_renewals\":").Append(it.ResidentLinkRenewals).Append(',');
                        sb.Append("\"resident_last_renewal_bytes\":").Append(it.ResidentLastRenewalBytes).Append(',');
                        sb.Append("\"resident_retry_pending\":").Append(it.ResidentRetryPending ? "true" : "false").Append(',');
                        sb.Append("\"http_retries\":").Append(it.TransientHttpRetries).Append(',');
                        sb.Append("\"http_retry_durable\":").Append(it.HttpRetryLastDurableBytes).Append(',');
                        sb.Append("\"install_retries\":").Append(it.InstallRetries).Append(',');
                        sb.Append("\"http_retry_at\":").Append(it.TransientHttpRetries > 0 || it.InstallRetries > 0 ? it.RetryAfterUtcTicks : 0).Append(',');
                        sb.Append("\"resident_auto_install\":").Append(it.ResidentAutoInstall ? "true" : "false").Append(',');
                        sb.Append("\"resident_generation\":\"").Append(JsonLite.Escape(it.ResidentGeneration ?? "")).Append("\",");
                        sb.Append("\"resident_pause\":\"").Append(it.ResidentPauseDesired == true ? "pause" : it.ResidentPauseDesired == false ? "resume" : "").Append("\",");
                        sb.Append("\"resolved_provider_id\":\"").Append(JsonLite.Escape(it.ResolvedProviderId)).Append("\",");
                        sb.Append("\"parked_provider\":").Append(it.ParkedForProvider ? "true" : "false").Append(',');
                        sb.Append("\"park_provider_id\":\"").Append(JsonLite.Escape(it.ParkProviderId ?? "")).Append("\",");
                        sb.Append("\"park_poll_due\":").Append(it.ParkPollDueUtcTicks).Append(',');
                        sb.Append("\"park_poll_count\":").Append(it.ParkPollCount).Append(',');
                        sb.Append("\"park_transient_failures\":").Append(it.ParkTransientFailures).Append(',');
                        sb.Append("\"park_last_state\":\"").Append(JsonLite.Escape(it.ParkLastState ?? "")).Append("\",");
                        sb.Append("\"park_started\":").Append(it.ParkStartedUtcTicks).Append(',');
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
                            string tmp = ManifestPath + ".tmp";
                            File.WriteAllText(tmp, sb.ToString());
                            ReplaceManifest(tmp, ManifestPath);
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
            if (item.ResidentRetryPending && item.ResidentStaged)
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
            long expectedSize = item.BgftExpectedSize > 0 ? item.BgftExpectedSize : item.Total;
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
                    out directProgress, out directProgressError) && directProgress != null &&
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
                        contentId, subType,
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
                    item.BgftSubType, out progress, out progressError) &&
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
                item.InstallOrderReady = false;
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
            try
            {
                RecoverManifest();
                if (!File.Exists(ManifestPath)) return;
                string json = File.ReadAllText(ManifestPath);
                var loadedIds = new HashSet<string>(StringComparer.Ordinal);
                var loadedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var loadedIdentities = new Dictionary<string, DlItem>(StringComparer.OrdinalIgnoreCase);

                foreach (var obj in JsonLite.ExtractObjectArray(json, "items"))
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
                        ParkPollDueUtcTicks = Math.Max(0, Math.Min(DateTime.MaxValue.Ticks, ParseLong(JsonLite.GetString(obj, "park_poll_due")))),
                        ParkPollCount = Math.Max(0, ParseInt(JsonLite.GetString(obj, "park_poll_count"), 0)),
                        ParkTransientFailures = Math.Max(0, ParseInt(JsonLite.GetString(obj, "park_transient_failures"), 0)),
                        ParkLastState = JsonLite.GetString(obj, "park_last_state") ?? "",
                        ParkStartedUtcTicks = Math.Max(0, Math.Min(DateTime.MaxValue.Ticks, ParseLong(JsonLite.GetString(obj, "park_started")))),
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
                        it.State = previous == DlState.Canceled || previous == DlState.Paused || previous == DlState.Failed ? previous : DlState.Queued;
                        it.StatusText = "Reconnect the USB drive containing " + Path.GetFileName(it.DestPath);
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
                    _items.Add(it);
                    migrated = true;
                }
                if (migrated) SaveManifest();
            }
            catch { }
        }

        void RecoverManifest()
        {
            if (File.Exists(ManifestPath)) return;
            string backup = ManifestPath + ".bak";
            string tmp = ManifestPath + ".tmp";
            if (IsManifestCandidate(tmp)) File.Move(tmp, ManifestPath);
            else if (File.Exists(backup)) File.Move(backup, ManifestPath);
        }

        static bool IsManifestCandidate(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                string json = File.ReadAllText(path).Trim();
                return json.StartsWith("{\"items\":[", StringComparison.Ordinal) &&
                    json.EndsWith("]}", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        static void ReplaceManifest(string tmp, string path)
        {
            if (!File.Exists(path))
            {
                File.Move(tmp, path);
                return;
            }

            string backup = path + ".bak";
            try
            {
                if (File.Exists(backup)) File.Delete(backup);
                File.Replace(tmp, path, backup);
                if (File.Exists(backup)) File.Delete(backup);
            }
            catch (NotSupportedException)
            {
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(path, backup);
                try
                {
                    File.Move(tmp, path);
                    File.Delete(backup);
                }
                catch
                {
                    if (!File.Exists(path) && File.Exists(backup)) File.Move(backup, path);
                    throw;
                }
            }
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

        void DiscardInvalidDownload(DlItem item, string reason)
        {
            if (item == null) return;
            try { if (File.Exists(item.DestPath)) item.Done = item.Total = new FileInfo(item.DestPath).Length;
                else item.Done = ParallelDownloadCheckpoint.DurableBytes(item.DestPath + ".part"); } catch { }
            item.InstallConfirmed = false;
            item.ForceLocalInstall = false;
            item.Error = reason;
            item.BytesPerSec = 0;
            item.EtaSeconds = 0;
            ClearBackground(item);
        }

        static void DeleteDownloadFiles(DlItem item)
        {
            if (item != null && !item.LocalSource && (!item.InstallSubmitted || item.InstallConfirmed))
                DeleteDownloadFiles(item.DestPath);
        }

        static void DeleteDownloadFiles(string finalPath)
        {
            if (!IsOwnedDownloadPath(finalPath)) return;
            try { DownloadResumeInfo.DeletePartial(finalPath + ".part"); } catch { }
            try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
            try { File.Delete(finalPath + ".map"); File.Delete(finalPath + ".map.tmp"); } catch { }
            try { File.Delete(finalPath + ".sha256-ok"); File.Delete(finalPath + ".sha256-ok.tmp"); } catch { }
        }

        static void DeletePartialOwned(string finalPath)
        {
            if (!IsOwnedDownloadPath(finalPath)) return;
            try { DownloadResumeInfo.DeletePartial(finalPath + ".part"); } catch { }
        }

        static bool IsOwnedDownloadPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string full = NormalizePath(path);
            var roots = new List<string>(StorageRoots());
            roots.Add(NormalizePath("/data/SSPI/downloads"));
            roots.Add(NormalizePath("/user/data/SSPI/downloads"));
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string prefix = NormalizePath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
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
                UnlockProviders.EnabledIds(cfg).Length > 0 &&
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
            return UnlockProviders.Unrestrict(_cfg, job.HosterUrl, progress, cancel,
                provider => { lock (_lock) { if (job.AttemptId == attempt) job.ResolvedProviderId = provider; } },
                job.ResolvedProviderId);
        }

        static bool IsAuthExpiry(Exception ex)
        {
            return DownloadLinkRecovery.IsExpired(ex);
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
            return PkgIntegrity.VerifyFile(item.DestPath, expected,
                text => item.StatusText = text, () => item.CancelRequested || item.PauseRequested, out error);
        }

        static string UrlTag(string value)
        {
            byte[] digest;
            using (var sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
            var tag = new StringBuilder(12);
            for (int i = 0; i < 6; i++) tag.Append(digest[i].ToString("x2"));
            return tag.ToString();
        }

        public static string Human(long bytes)
        {
            double v = bytes;
            string[] u = { "B", "KB", "MB", "GB" };
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? ((long)v).ToString() : v.ToString("0.0")) + " " + u[i];
        }

        public static string FormatProgress(AppSettings cfg, long done, long total, double bps, int etaSec)
        {
            int mode = cfg != null ? cfg.DownloadStatsMode : 0;
            string size = Human(done) + (total > 0 ? " / " + Human(total) : "");
            string speed = bps > 0 ? FormatSpeed(bps, mode) : "";
            string eta = etaSec > 0 ? FormatEta(etaSec) : "";
            if (mode == 1) // size only
                return size;
            if (mode == 2) // size + speed
                return string.IsNullOrEmpty(speed) ? size : size + "  ·  " + speed;
            if (mode == 3) // size + eta
                return string.IsNullOrEmpty(eta) ? size : size + "  ·  " + eta;
            // 0 = all
            var parts = new List<string>();
            parts.Add(size);
            if (!string.IsNullOrEmpty(speed)) parts.Add(speed);
            if (!string.IsNullOrEmpty(eta)) parts.Add(eta);
            return string.Join("  ·  ", parts.ToArray());
        }

        static string FormatSpeed(double bps, int mode)
        {
            // mode 0/2 use MB/s by default; Mbps if bit mode bit set via mode==4 later
            if (bps >= 1024 * 1024)
                return (bps / (1024.0 * 1024.0)).ToString("0.00") + " MB/s";
            if (bps >= 1024)
                return (bps / 1024.0).ToString("0.0") + " KB/s";
            return ((long)bps).ToString() + " B/s";
        }

        void BeginNerdSession(DlItem job, string directUrl)
        {
            lock (_lock)
            {
                _nerd.Reset();
                _nerd.TitleId = job.TitleId ?? "";
                try
                {
                    _nerd.FileName = string.IsNullOrEmpty(job.DestPath)
                        ? (job.Kind ?? "pkg")
                        : Path.GetFileName(job.DestPath);
                }
                catch { _nerd.FileName = job.Kind ?? "pkg"; }
                _nerd.CdnHost = SafeHost(directUrl);
                job.CdnHost = _nerd.CdnHost;
                _nerd.RegionHint = RegionFromHost(_nerd.CdnHost, _cfg != null ? _cfg.RealDebridLocation : "auto");
                _nerd.TransferMode = "in-app";
                job.TransferMode = _nerd.TransferMode;
                string provider = string.IsNullOrEmpty(job.ResolvedProviderId) ?
                    (job.AccessType == "Cloud" ? CloudCatalog.OwningProvider(job.HosterUrl) : "direct") : job.ResolvedProviderId;
                SspiLog.Write("download", "event=provider-selected job=" + (job.Id ?? "") +
                    " attempt=" + job.AttemptId + " provider=" + provider + " route=" + (job.AccessType ?? "") +
                    " cdn=" + _nerd.CdnHost + " allowance=" + DownloadTransferSettings.ConnectionsFor(directUrl, NetHttp.DownloadRangeCount));
            }
        }

        static string SafeHost(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return "—";
                var u = new Uri(url);
                string h = u.Host ?? "";
                if (h.Length > 48) h = h.Substring(0, 48);
                return h;
            }
            catch { return "—"; }
        }

        static string RegionFromHost(string host, string pref)
        {
            string h = (host ?? "").ToLowerInvariant();
            string[] us = { "lax", "sfo", "sea", "dal", "dfw", "chi", "ord", "nyc", "iad", "mia", "atl", ".us." };
            string[] eu = { "ams", "fra", "lhr", "lon", "par", "rbx", "waw", "hel", "sto", "mad", ".eu." };
            foreach (var t in us) if (h.Contains(t)) return "US-cdn";
            foreach (var t in eu) if (h.Contains(t)) return "EU-cdn";
            pref = AppSettings.NormalizeRdLocation(pref);
            if (pref == AppSettings.RdLocationUs) return "pref-US";
            if (pref == AppSettings.RdLocationEu) return "pref-EU";
            return "auto";
        }

        public static string FormatMbps(double mbps)
        {
            if (mbps >= 100) return mbps.ToString("0") + " Mbps";
            if (mbps >= 10) return mbps.ToString("0.0") + " Mbps";
            return mbps.ToString("0.00") + " Mbps";
        }

        public static string FormatMBpsFromMbps(double mbps)
        {
            double mib = (mbps * 1000.0 * 1000.0 / 8.0) / (1024.0 * 1024.0);
            return mib.ToString("0.00") + " MB/s";
        }

        static string FormatEta(int sec)
        {
            if (sec < 60) return sec + "s left";
            if (sec < 3600) return (sec / 60) + "m " + (sec % 60) + "s left";
            return (sec / 3600) + "h " + ((sec % 3600) / 60) + "m left";
        }

        static string Clip(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }

        static string Hex(int value)
        {
            return "0x" + unchecked((uint)value).ToString("X8");
        }
    }
}
