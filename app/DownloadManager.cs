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
            if (mbps > 800) mbps = 800;
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
        public string FanOutPendingPaths = "";
        public string ArchiveVolumes = "";
        public string ArchivePassword = "";
        public string InstallAfterId = "";
        public bool BgftLocalInstall;
        // Persisted as resident_archive for compatibility; V4 PKGs also let the
        // resident own installation through to BGFT acknowledgement.
        public bool ResidentArchive;
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
        internal long StatsAt, StatsDone, StatsTotal, StatsAdvancedAt;
        internal string StatsPhase;
        internal int StatsAttempt;
        public string Error;
        public string StatusText;
        /// <summary>CDN hostname only (no path/query).</summary>
        public string CdnHost;
        public string TransferMode;
        public bool InstallConfirmed;
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
        /// <summary>Incremented each transfer claim; Refresh must not stomp a newer attempt.</summary>
        public int AttemptId;
        public bool PauseRequested;
        public bool CancelRequested;
    }

    /// <summary>Sequential PKG download queue with pause/resume and manifest persistence.</summary>
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
        readonly object _bgftRefreshGate = new object();
        readonly LoopbackPkgServer _loopback = new LoopbackPkgServer();
        readonly List<DlItem> _items = new List<DlItem>();
        readonly AppSettings _cfg;
        const int WorkerCount = 1;
        readonly Thread[] _workers = new Thread[WorkerCount];
        readonly HashSet<string> _activeIds = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> _activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        volatile bool _run = true;
        long _saveVersion;
        long _savedVersion;
        readonly NerdTelemetry _nerd = new NerdTelemetry();
        public NerdTelemetry Nerd { get { return _nerd; } }

        public DownloadManager(AppSettings cfg)
        {
            _cfg = cfg;
            if (_cfg != null) _cfg.ApplyToNetHttp();
            LoadManifest();
            CleanupFanOutStateOnStartup();
            for (int i = 0; i < _workers.Length; i++)
            {
                int workerIndex = i;
                _workers[i] = new Thread(() => WorkerLoop(workerIndex))
                    { IsBackground = true, Name = "DlMgr-" + (i + 1) };
                _workers[i].Start();
            }
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
                    if (string.IsNullOrEmpty(item.ArchiveVolumes)) item.ArchiveVolumes = candidate.ArchiveVolumes ?? "";
                    item.ArchivePassword = candidate.ArchivePassword ?? "";
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
                            DeleteDownloadFiles(item.DestPath);
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

        /// <summary>
        /// Enqueue with an immutable Package Source provenance snapshot. The string parameters
        /// keep the legacy queue independent of the source-engine assembly during migration.
        /// </summary>
        public string Enqueue(GameHit game, PkgLink link, string sourceId, string sourceVersion,
            string candidateId, string accessType, string expectedSha256, string sourceAttribution,
            out string message)
        {
            if (game == null || link == null) throw new Exception("Bad download");
            message = "Queued " + link.Kind;
            string existingId = null;
            bool revived = false;
            bool changed = false;
            lock (_lock)
            {
                foreach (var existing in _items)
                    if (string.Equals(existing.TitleId, game.TitleId, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existing.Kind, link.Kind, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(existing.HosterUrl, link.Url, StringComparison.OrdinalIgnoreCase))
                    {
                        existingId = existing.Id;
                        if (!string.IsNullOrEmpty(game.ImageUrl) &&
                            !string.Equals(existing.ImageUrl, game.ImageUrl, StringComparison.Ordinal))
                        {
                            existing.ImageUrl = game.ImageUrl;
                            changed = true;
                        }
                        if (!string.IsNullOrEmpty(game.Name) &&
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
                            existing.InstallConfirmed = false;
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

        internal static string[] StorageRoots()
        { return new[] { AppSettings.DownloadDir, Path.Combine(AppSettings.DataDir, "resident/archives") }; }

        static bool IsPathClaimedByJob(DlItem job, string full)
        {
            if (job == null || string.IsNullOrEmpty(full)) return false;
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
            string[] family = { job.DestPath, job.DestPath + ".part", job.DestPath + ".resume", job.DestPath + ".ranges" };
            foreach (string p in family)
                if (!string.IsNullOrEmpty(p) && SamePath(p, full))
                    return job.Background || job.State == DlState.Downloading || job.State == DlState.Finalizing ||
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
            ReconcileLocalFiles(false);
            lock (_lock)
            {
                var list = new List<DlItem>(_items.Count);
                foreach (var i in _items)
                {
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
                        ArchiveVolumes = i.ArchiveVolumes,
                        ArchivePassword = i.ArchivePassword,
                        InstallAfterId = i.InstallAfterId,
                        BgftLocalInstall = i.BgftLocalInstall,
                        ResidentArchive = i.ResidentArchive,
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
                        BytesPerSec = i.BytesPerSec,
                        EtaSeconds = i.EtaSeconds,
                        Error = i.Error,
                        StatusText = i.StatusText,
                        InstallConfirmed = i.InstallConfirmed,
                        Background = i.Background,
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
        }

        long _lastReconcileTicks;

        /// <summary>Completed requires DestPath. Installed may have no PKG (deleted after install).</summary>
        public void ReconcileLocalFiles(bool force)
        {
            long now = DateTime.UtcNow.Ticks;
            if (!force && now - _lastReconcileTicks < TimeSpan.TicksPerSecond * 2)
                return;
            _lastReconcileTicks = now;
            bool changed = false;
            lock (_lock)
            {
                foreach (var it in _items)
                {
                    if (it.ResidentArchive || (it.Background && it.State != DlState.Installed)) continue;
                    if (it.State == DlState.Installed)
                    {
                        string localError = null;
                        PkgContentKind localKind;
                        string localKindName;
                        string localContentId;
                        string localTitleId;
                        long localSize = 0;
                        bool destValid = !string.IsNullOrEmpty(it.DestPath) && File.Exists(it.DestPath) &&
                            TryValidateLocalPackage(it, out localKind, out localKindName,
                                out localContentId, out localTitleId, out localSize, out localError);
                        if (!destValid && !string.IsNullOrEmpty(it.DestPath) && File.Exists(it.DestPath))
                            DiscardInvalidDownload(it, localError ?? "Invalid PKG on disk");

                        // AppExists(CUSA) only proves the base game. DLC/update must not
                        // stay Installed just because the base is present or a leftover
                        // dest file exists.
                        if (PkgInstallPolicy.IsAddonOrPatchName(it.Kind))
                        {
                            if (destValid)
                            {
                                it.State = DlState.Completed;
                                it.InstallConfirmed = false;
                                it.StatusText = "PKG on disk — CROSS installs";
                                it.Done = it.Total = localSize;
                            }
                            else
                            {
                                it.State = DlState.Failed;
                                it.InstallConfirmed = false;
                                it.Error = localError ?? "DLC/update not on disk";
                                it.StatusText = "Not on disk — CROSS re-downloads";
                                it.Done = 0;
                                it.Total = 0;
                            }
                            changed = true;
                            continue;
                        }
                        if (!string.IsNullOrEmpty(it.TitleId) &&
                            !PkgInstaller.IsTitleInstalled(it.TitleId))
                        {
                            it.State = DlState.Failed;
                            it.InstallConfirmed = false;
                            it.Error = destValid ? "Removed on PS4" : (localError ?? "Removed on PS4");
                            it.StatusText = destValid
                                ? "Uninstalled on PS4 — CROSS re-downloads"
                                : "Invalid leftover removed — CROSS re-downloads";
                            if (!destValid)
                            {
                                it.Done = 0;
                                it.Total = 0;
                            }
                            changed = true;
                        }
                        continue;
                    }
                    if (it.State != DlState.Completed && it.State != DlState.Installing &&
                        it.State != DlState.Submitted)
                        continue;
                    if (string.IsNullOrEmpty(it.DestPath)) continue;
                    if (File.Exists(it.DestPath))
                    {
                        if (it.State == DlState.Completed)
                        {
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
                                changed = true;
                            }
                        }
                        continue;
                    }
                    if (it.State == DlState.Installing || it.State == DlState.Submitted)
                    {
                        it.State = DlState.Failed;
                        it.Error = "PKG missing during install";
                        it.StatusText = "PKG missing — re-download";
                        it.Done = 0;
                        changed = true;
                        continue;
                    }
                    it.State = DlState.Failed;
                    it.Error = "PKG missing on disk";
                    it.StatusText = "PKG missing — CROSS to re-queue";
                    it.Done = 0;
                    it.Total = 0;
                    it.BytesPerSec = 0;
                    it.EtaSeconds = 0;
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
                        ResidentDownloadService.MarkFailed(it.Id);
                        it.CancelRequested = true;
                        it.StatusText = "Stopping previous resident task before retry";
                        SaveManifest(); return;
                    }
                    bool pause = it.State != DlState.Paused;
                    ResidentDownloadService.SetPaused(it.Id, pause);
                    it.State = pause ? DlState.Paused : DlState.Downloading;
                    it.StatusText = pause ? "Resident archive pause requested" : "Resident archive resumed";
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
                if (it.State == DlState.Downloading || it.State == DlState.Resolving ||
                    it.State == DlState.Finalizing)
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
                            it.Done = new FileInfo(part).Length;
                    }
                    catch { }
                }
                else if (it.State == DlState.Paused || it.State == DlState.Failed ||
                         it.State == DlState.Canceled)
                {
                    bool restartRequired = it.State == DlState.Failed &&
                        DownloadResumeInfo.IsRestartRequired(it.Error);
                    if (restartRequired)
                    {
                        DeletePartialOwned(it.DestPath);
                        it.Done = 0;
                        it.Total = 0;
                    }
                    try
                    {
                        string part = it.DestPath + ".part";
                        if (File.Exists(part))
                            it.Done = new FileInfo(part).Length;
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
                if (IsTerminal(it.State))
                {
                    error = "Already finished — use Remove";
                    return false;
                }
                if (!it.Background && it.State == DlState.Installing && _activeIds.Contains(it.Id))
                {
                    error = "Local install is already in progress";
                    return false;
                }
                if (it.Background && it.ResidentArchive)
                {
                    ResidentDownloadService.MarkFailed(it.Id);
                    it.CancelRequested = true;
                    it.StatusText = "Canceling resident archive and its BGFT task...";
                    SaveManifest(); return true;
                }
                if (it.Background)
                {
                    int activeTask;
                    string cancelError;
                    if (PkgInstaller.CancelBackground(it.BgftTaskId, it.BgftContentId, it.BgftSubType,
                        out activeTask, out cancelError))
                    {
                        ClearBackground(it);
                        it.State = DlState.Canceled;
                        it.StatusText = "Canceled";
                    }
                    else
                    {
                        it.BgftTaskId = activeTask;
                        it.StatusText = cancelError;
                        error = cancelError;
                    }
                    SaveManifest();
                    return error == null;
                }
                if (_activeIds.Contains(it.Id) &&
                    (it.State == DlState.Resolving || it.State == DlState.Downloading ||
                     it.State == DlState.Finalizing))
                {
                    it.CancelRequested = true;
                    it.PauseRequested = false;
                    it.StatusText = "Canceling...";
                    SaveManifest();
                    return true;
                }
                if (_activeIds.Contains(it.Id)) it.AttemptId++;
                if (!TryCleanupFanOutPending(it))
                {
                    error = "Extracted package cleanup failed";
                    return false;
                }
                it.State = DlState.Canceled;
                it.CancelRequested = false;
                it.PauseRequested = false;
                it.StatusText = "Canceled";
                DeleteDownloadFiles(it.DestPath);
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
            DlItem removed = null;
            lock (_lock)
            {
                var it = Find(id);
                if (it == null)
                {
                    error = "Download not found";
                    return false;
                }
                if (_activeIds.Contains(it.Id) || it.State == DlState.Resolving ||
                    it.State == DlState.Downloading || it.State == DlState.Finalizing ||
                    it.State == DlState.Installing)
                {
                    error = "Still active";
                    return false;
                }
                if (!IsTerminal(it.State) && it.State != DlState.Queued && it.State != DlState.Paused)
                {
                    error = "Cannot remove now";
                    return false;
                }
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
                    DeleteDownloadFiles(it.DestPath);
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
                    DeleteDownloadFiles(it.DestPath);
                    if (File.Exists(it.DestPath))
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
                removed = it;
                _items.Remove(it);
                SaveManifest();
            }
            if (removed != null &&
                (removed.State == DlState.Failed || removed.State == DlState.Canceled))
            {
                DeleteDownloadFiles(removed.DestPath);
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
                    _items.Remove(it);
                if (drop.Count > 0)
                    SaveManifest();
            }
            foreach (var it in drop)
            {
                if (it.State == DlState.Failed || it.State == DlState.Canceled)
                {
                    DeleteDownloadFiles(it.DestPath);
                }
            }
            return drop.Count;
        }

        public void QueueLocalInstall(string id)
        {
            lock (_lock) {
                var it = Find(id); if (it == null || it.Background || it.State == DlState.Installing || it.State == DlState.Submitted) return;
                it.State = DlState.Queued; it.ForceLocalInstall = true; it.Error = null;
                it.PauseRequested = it.CancelRequested = false; it.RetryAfterUtcTicks = 0;
                it.StatusText = "Queued for update verification";
            }
            SaveManifest();
        }

        static void CompleteByteCounters(DlItem item)
        {
            long size = item.Total > 100 ? item.Total : Math.Max(item.ExpectedByteSize, item.BgftExpectedSize);
            try { if (File.Exists(item.DestPath)) size = new FileInfo(item.DestPath).Length; } catch { }
            item.Done = item.Total = Math.Max(0, size);
        }

        public void MarkInstalling(string id, string msg)
        {
            lock (_lock)
            {
                var it = Find(id);
                if (it == null) return;
                it.State = DlState.Installing;
                it.InstallConfirmed = false;
                it.StatusText = msg ?? "Installing...";
                SaveManifest();
            }
        }

        public void MarkInstalled(string id, string msg)
        {
            MarkInstalled(id, msg, false);
        }

        public void MarkInstallAccepted(string id, string msg)
        {
            MarkInstallAccepted(id, msg, -1);
        }

        public void MarkInstallAccepted(string id, string msg, int bgftTaskId)
        {
            lock (_lock)
            {
                var it = Find(id);
                if (it == null) return;
                it.State = DlState.Submitted;
                it.InstallConfirmed = false;
                it.Error = null;
                it.StatusText = msg ?? "Sent to PS4 — verify · PKG kept";
                it.BytesPerSec = 0;
                it.EtaSeconds = 0;
                if (bgftTaskId >= 0)
                    it.BgftTaskId = bgftTaskId;
                if (!string.IsNullOrEmpty(it.DestPath) && File.Exists(it.DestPath))
                    it.Done = it.Total = new FileInfo(it.DestPath).Length;
                SaveManifest();
            }
        }

        public void MarkAlreadyInstalled(string id, string msg)
        {
            lock (_lock)
            {
                var it = Find(id);
                if (it == null) return;
                it.State = DlState.Completed;
                it.InstallConfirmed = false;
                it.Error = null;
                it.StatusText = msg ?? "Already installed; PKG kept";
                it.InstallOrderReady = true;
                if (!string.IsNullOrEmpty(it.DestPath) && File.Exists(it.DestPath))
                    it.Done = it.Total = new FileInfo(it.DestPath).Length;
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
                it.InstallConfirmed = false;
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
        public bool MarkInstalled(string id, string msg, bool deleteLocalPackage)
        {
            string path = null;
            lock (_lock)
            {
                var it = Find(id);
                if (it == null) return false;
                path = it.DestPath;
                it.State = DlState.Installed;
                it.InstallConfirmed = true;
                it.Error = null;
                it.StatusText = msg ?? "Installed";
                it.BytesPerSec = 0;
                it.EtaSeconds = 0;
                if (File.Exists(it.DestPath)) it.Total = new FileInfo(it.DestPath).Length;
                it.Done = it.Total;
                ClearBackground(it);
                it.Background = false;
            }
            bool deleted = !deleteLocalPackage;
            // Persist proof before cleanup so a power loss cannot turn a confirmed install
            // into an unverified legacy row with a missing PKG.
            bool proofSaved = !deleteLocalPackage || SaveManifest();
            if (deleteLocalPackage && IsOwnedDownloadPath(path))
            {
                if (proofSaved) try
                {
                    if (File.Exists(path)) File.Delete(path);
                    deleted = !File.Exists(path);
                }
                catch { deleted = false; }
                if (proofSaved) try { DownloadResumeInfo.DeletePartial(path + ".part"); } catch { }
            }
            if (deleteLocalPackage && !proofSaved)
            {
                deleted = false;
                lock (_lock)
                {
                    var it = Find(id);
                    if (it != null) it.StatusText = (msg ?? "Installed") +
                        "; PKG retained because history could not be saved";
                }
            }
            if (deleteLocalPackage && proofSaved && !deleted)
            {
                lock (_lock)
                {
                    var it = Find(id);
                    if (it != null) it.StatusText = (msg ?? "Installed") + "; PKG retained";
                }
            }
            SaveManifest();
            return deleted;
        }

        public void UpdateInstallProgress(string id, int percent)
        {
            lock (_lock)
            {
                var it = Find(id);
                if (it == null) return;
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

        public void MarkInstallFailed(string id, string err)
        {
            string path;
            string titleId;
            lock (_lock)
            {
                var it = Find(id);
                if (it == null) return;
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
                if (it == null) return;
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

        public void MarkPackageInvalid(string id, string err)
        {
            string path;
            lock (_lock)
            {
                var it = Find(id);
                if (it == null) return;
                path = it.DestPath;
                it.State = DlState.Failed;
                it.InstallConfirmed = false;
                it.Error = err;
                it.StatusText = "Bad PKG — CROSS to re-download";
            }
            DeleteDownloadFiles(path);
            SaveManifest();
        }

        DlItem Find(string id)
        {
            foreach (var i in _items)
                if (i.Id == id) return i;
            return null;
        }

        bool InstallDependencyReady(DlItem item)
        {
            lock (_lock)
            {
                if (!string.IsNullOrEmpty(item.InstallAfterId))
                {
                    DlItem previous = Find(item.InstallAfterId);
                    if (previous == null || !(previous.InstallOrderReady || previous.InstallConfirmed))
                    {
                        item.StatusText = previous != null && previous.State == DlState.Failed
                            ? "Waiting: previous package failed · retry it first" : "Waiting for previous package installation";
                        return false;
                    }
                }
                int rank = InstallRank(item.Kind);
                foreach (var other in _items)
                {
                    if (other == item || !string.Equals(item.TitleId, other.TitleId, StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrEmpty(item.TitleId) || other.InstallConfirmed || other.InstallOrderReady ||
                        other.State == DlState.Canceled) continue;
                    bool earlier = InstallRank(other.Kind) < rank;
                    Version a, b;
                    if (rank == 1 && InstallRank(other.Kind) == 1 &&
                        Version.TryParse((other.PackageVersion ?? "").TrimStart('v','V'), out a) &&
                        Version.TryParse((item.PackageVersion ?? "").TrimStart('v','V'), out b))
                        earlier = a < b || (a == b && !string.Equals(other.Kind, "backport", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(item.Kind, "backport", StringComparison.OrdinalIgnoreCase));
                    if (earlier || other.Background || other.State == DlState.Installing)
                    {
                        item.StatusText = other.State == DlState.Failed ? "Waiting: a required package failed" : "Waiting for this title's earlier package";
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


        void WorkerLoop(int workerIndex)
        {
            while (_run)
            {
                if (Monitor.TryEnter(_bgftRefreshGate))
                {
                    try { RefreshBackgroundTasks(); }
                    finally { Monitor.Exit(_bgftRefreshGate); }
                }
                DlItem job = null;
                int attempt = 0;
                string activePath = null;
                lock (_lock)
                {
                    bool backgroundBusy = false;
                    foreach (var item in _items)
                        if (item.Background && (item.State == DlState.Downloading || item.State == DlState.Resolving ||
                            item.State == DlState.Finalizing || item.State == DlState.Paused))
                        {
                            backgroundBusy = true;
                            break;
                        }
                    for (int index = backgroundBusy ? -1 : _items.Count - 1; index >= 0; index--)
                    {
                        var i = _items[index];
                        // Never claim a BGFT-owned row as a new foreground job.
                        string pathKey = NormalizePath(i.DestPath);
                        if (i.State == DlState.Queued && !i.Background && (!File.Exists(i.DestPath) || InstallDependencyReady(i)) &&
                            i.RetryAfterUtcTicks <= DateTime.UtcNow.Ticks && !_activeIds.Contains(i.Id) &&
                            !_activePaths.Contains(pathKey))
                        {
                            job = i;
                            _activeIds.Add(i.Id);
                            _activePaths.Add(pathKey);
                            activePath = pathKey;
                            i.PauseRequested = false;
                            i.CancelRequested = false;
                            i.AttemptId++;
                            attempt = i.AttemptId;
                            i.State = DlState.Resolving;
                            i.StatusText = "Unlocking link...";
                            break;
                        }
                    }
                }
                if (job == null)
                {
                    Thread.Sleep(500);
                    continue;
                }

                SaveManifest();
                try { RunJob(job, attempt); }
                finally
                {
                    lock (_lock)
                    {
                        _activeIds.Remove(job.Id);
                        if (activePath != null) _activePaths.Remove(activePath);
                    }
                }
            }
        }

        void RunJob(DlItem job, int attempt)
        {
            try
            {
                if (CommitRequestedStop(job, attempt)) return;
                if (!string.IsNullOrEmpty(job.ArchiveVolumes))
                {
                    DownloadArchiveVolumes(job, attempt);
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
                        DiscardInvalidDownload(job, existingError ?? "Invalid file on disk");
                        job.StatusText = "Re-downloading (invalid file removed)";
                    }
                    SaveManifest();
                }
                if (string.Equals(job.AccessType, "Extracted", StringComparison.OrdinalIgnoreCase))
                {
                    SetFailureIfCurrent(job, attempt, "Extracted PKG is missing; requeue the archive");
                    return;
                }
                string direct = job.HosterUrl;
                string foregroundStatus = "Downloading in Game Search...";
                // A source-declared Direct candidate is already a byte-stream URL. It must not
                // be sent to a Link Service or interpreted as a free-hoster landing page.
                if (IsDirectAccess(job.AccessType))
                {
                    foregroundStatus = "Downloading direct...";
                }
                // Unknown (including legacy empty values) preserves the old routing behavior.
                else if (_cfg.UseUnlockProvider &&
                    !string.Equals(_cfg.UnlockProviderId, UnlockProviders.NoneId, StringComparison.OrdinalIgnoreCase))
                {
                    lock (_lock)
                    {
                        if (job.AttemptId != attempt) return;
                        job.StatusText = "Unlocking (" + UnlockProviders.DisplayName(_cfg.UnlockProviderId) + ")...";
                    }
                    direct = UnlockProviders.Unrestrict(_cfg, job.HosterUrl,
                        text => { lock (_lock) { if (job.AttemptId == attempt) job.StatusText = text; } },
                        () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested);
                    foregroundStatus = "Downloading (" + UnlockProviders.DisplayName(_cfg.UnlockProviderId) + ")...";
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
                        direct = FreeHosterClient.ResolveDirect(job.HosterUrl);
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
                if (File.Exists(part)) existing = new FileInfo(part).Length;

                if (existing == 0)
                    foregroundStatus = "Downloading in Game Search...";

                BeginNerdSession(job, direct);

                if (TryRunLoopbackBgftFeeder(job, attempt, direct)) return;
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
                        DiscardInvalidDownload(job, afterError ?? "Invalid file on disk");
                    }
                    SaveManifest();
                }
                existing = File.Exists(part) ? new FileInfo(part).Length : 0;

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
                    ClearBackground(job);
                    job.Background = false;
                    if (!stopBeforeTransfer)
                    {
                        job.State = DlState.Downloading;
                        job.StatusText = foregroundStatus;
                    }
                }
                if (stopBeforeTransfer && CommitRequestedStop(job, attempt)) return;
                if (!string.IsNullOrEmpty(staleContent) && staleSub > 0)
                {
                    int activeTask;
                    string cerr;
                    PkgInstaller.CancelBackground(-1, staleContent, staleSub, out activeTask, out cerr);
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
                long speedWindowStart = DateTime.UtcNow.Ticks;
                long speedWindowBytes = existing;
                double emaBps = 0;
                string resumeTitleId = existing > 0 &&
                    PackageArchive.Detect(part) == PackageObjectKind.Pkg ? job.TitleId : null;
                string resumePackageId = !string.IsNullOrEmpty(job.ExpectedContentId) ? job.ExpectedContentId :
                    (!string.IsNullOrEmpty(job.ExpectedSha256) ? "sha256:" + job.ExpectedSha256 : null);
                Action<long, long> report =
                    (done, total) =>
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
                            long now = DateTime.UtcNow.Ticks;
                            long bps = 0;
                            // Parallel→single fallback can drop done; reset EMA so speed isn't nonsense.
                            if (lastPublishedBytes >= 0 && done < lastPublishedBytes)
                            {
                                emaBps = 0;
                                speedWindowStart = now;
                                speedWindowBytes = done;
                                lastPublishedAt = 0;
                                lastPublishedBytes = -1;
                            }

                            if (done != total && lastPublishedBytes >= 0 &&
                                done - lastPublishedBytes < 256 * 1024 &&
                                now - lastPublishedAt < TimeSpan.TicksPerMillisecond * 250)
                                return;

                            long elapsedTicks = now - speedWindowStart;
                            if (elapsedTicks > TimeSpan.TicksPerMillisecond * 400)
                            {
                                double secs = elapsedTicks / (double)TimeSpan.TicksPerSecond;
                                if (secs > 0.05)
                                {
                                    double inst = (done - speedWindowBytes) / secs;
                                    emaBps = emaBps <= 0 ? inst : (emaBps * 0.7 + inst * 0.3);
                                    bps = (long)emaBps;
                                }
                                speedWindowStart = now;
                                speedWindowBytes = done;
                            }
                            else if (emaBps > 0) bps = (long)emaBps;

                            int eta = 0;
                            if (bps > 1024 && total > done)
                                eta = (int)Math.Min(int.MaxValue, (total - done) / bps);

                            lock (_lock)
                            {
                                if (job.AttemptId != attempt || job.Background ||
                                    job.PauseRequested || job.CancelRequested) return;
                                if (lastPublishedBytes >= 0 && done < lastPublishedBytes)
                                    _nerd.DropEvents++;
                                if (bps < 1024 && lastPublishedBytes >= 0 &&
                                    done == lastPublishedBytes)
                                    _nerd.StallEvents++;
                                job.Done = done;
                                // Monotonic total — never shrink (DLC size flicker)
                                if (total > 0 && (job.Total <= 0 || total >= job.Done))
                                    job.Total = total;
                                job.BytesPerSec = bps;
                                job.EtaSeconds = eta;
                                bool transferComplete = total > 0 && done >= total;
                                if (transferComplete)
                                {
                                    job.State = DlState.Finalizing;
                                    job.StatusText = "Finalizing transfer...";
                                    job.BytesPerSec = 0;
                                    job.EtaSeconds = 0;
                                    _nerd.Finalizing = true;
                                }
                                else if (job.State != DlState.Finalizing)
                                {
                                    job.State = DlState.Downloading;
                                    job.StatusText = "Downloading";
                                }
                                _nerd.Done = done;
                                _nerd.Total = job.Total;
                                if (bps > 0)
                                    _nerd.PushMbps((bps * 8.0) / (1000.0 * 1000.0));
                            }
                            lastPublishedAt = now;
                            lastPublishedBytes = done;
                        }
                        finally { Monitor.Exit(progSync); }
                    };
                long size;
                try
                {
                    size = NetHttp.DownloadFileResumable(direct, job.DestPath, existing,
                        report, cancel, 0, null, resumeTitleId, resumePackageId);
                }
                catch (Exception transferEx) when (IsAuthExpiry(transferEx) && CanRefreshUnlock(job))
                {
                    // One bounded in-attempt refresh: fresh signed URL, retained
                    // partial bytes resume under the package-identity rule.
                    lock (_lock)
                    {
                        if (job.AttemptId != attempt) return;
                        job.StatusText = "Refreshing expired link...";
                    }
                    direct = UnlockProviders.Unrestrict(_cfg, job.HosterUrl,
                        text => { lock (_lock) { if (job.AttemptId == attempt) job.StatusText = text; } },
                        () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested);
                    size = NetHttp.DownloadFileResumable(direct, job.DestPath, existing,
                        report, cancel, 0, null, resumeTitleId, resumePackageId);
                }

                lock (_lock)
                {
                    if (job.AttemptId != attempt) return;
                    job.State = DlState.Finalizing;
                    job.BytesPerSec = 0;
                    job.EtaSeconds = 0;
                    job.StatusText = string.IsNullOrEmpty(job.ExpectedSha256)
                        ? "Verifying package..." : "Verifying package SHA-256...";
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
                SetFailureIfCurrent(job, attempt, ex.Message);
            }
        }

        bool TryRunLoopbackBgftFeeder(DlItem job, int attempt, string direct)
        {
            if (!InstallDependencyReady(job)) return false;
            lock (_lock) foreach (var other in _items)
                if (other != job && other.Background) return false;
            if (_cfg == null || !_cfg.UseBgftDirect ||
                (job.ForceLocalInstall && !ResidentDownloadService.HasDownloader)) return false;

            HttpRangeResult header;
            try
            {
                lock (_lock)
                {
                    if (job.AttemptId != attempt) return true;
                    job.StatusText = "Checking PS4 system download...";
                }
                header = NetHttp.ReadRangeDirect(direct, 0, LoopbackPkgFeeder.HeaderBytes, 60000);
            }
            catch (Exception ex)
            {
                SetFeederForegroundFallback(job, attempt, ex.Message);
                return false;
            }
            if (CommitRequestedStop(job, attempt)) return true;

            if (header == null || header.Data == null || !LoopbackPkgFeeder.IsPkgHeader(header.Data))
            {
                if (header != null && header.Data != null && header.Data.Length >= 8 && header.Total > 0 &&
                    header.Data[0] == 0x52 && header.Data[1] == 0x61 && header.Data[2] == 0x72 &&
                    header.Data[3] == 0x21 && header.Data[4] == 0x1a && header.Data[5] == 7)
                {
                    if (job.ExpectedByteSize > 0 && job.ExpectedByteSize != header.Total)
                    { SetFailureIfCurrent(job, attempt, "Archive size mismatch"); return true; }
                    string name = ArchiveVolumeSet.FileName(job.HosterUrl, job.Label);
                    if (string.IsNullOrEmpty(name)) name = "archive.rar";
                    var volumes = new List<ArchiveVolume> { new ArchiveVolume { Name = name,
                        Url = header.EffectiveUrl ?? direct, AccessType = "Direct", Size = header.Total,
                        Sha256 = job.ExpectedSha256 } };
                    if (TryHandoffArchive(job, attempt, volumes, new List<string> { job.DestPath })) return true;
                }
                SetFeederForegroundFallback(job, attempt, "Origin did not return a PKG header");
                return false;
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
                return false;
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
            if (PkgInstallPolicy.RequiresInstalledBase(actualKind) &&
                (string.IsNullOrEmpty(actualTitleId) || !PkgInstaller.IsTitleInstalled(actualTitleId)))
            {
                SetFeederForegroundFallback(job, attempt,
                    actualKind == PkgContentKind.Patch
                        ? "Update requires installed base game"
                        : "DLC requires installed base game");
                return false;
            }

            try
            {
                HttpRangeResult rangeProbe = NetHttp.ReadRangeDirect(direct,
                    LoopbackPkgFeeder.HeaderBytes, 1, 60000);
                if (rangeProbe == null || rangeProbe.Data == null || rangeProbe.Data.Length != 1 ||
                    rangeProbe.Total != packageSize)
                    throw new IOException("Origin does not provide stable byte ranges");
            }
            catch (Exception ex)
            {
                SetFeederForegroundFallback(job, attempt, ex.Message);
                return false;
            }
            if (CommitRequestedStop(job, attempt)) return true;

            string bgftUrl = !string.IsNullOrEmpty(header.EffectiveUrl)
                ? header.EffectiveUrl : direct;
            if (ResidentDownloadService.HasDownloader && actualKind != PkgContentKind.BaseGame)
                return HandoffResidentPackage(job, attempt, bgftUrl, actualTitleId, actualKindName, contentId, packageSize);
            // Only the version-acknowledged resident verifies the complete patch
            // before BGFT. Older/direct feeders must use the local verified path.
            if (actualKind == PkgContentKind.Patch) { SetFeederForegroundFallback(job, attempt, "Update requires full integrity verification"); return false; }
            if (_cfg != null && _cfg.UseBgftDirect && _cfg.DownloadLimitMBps <= 0 &&
                actualKind == PkgContentKind.BaseGame &&
                !job.ForceLocalInstall && !string.IsNullOrEmpty(bgftUrl) &&
                Encoding.UTF8.GetByteCount(bgftUrl) + 1 <= 0x800)
            {
                lock (_lock)
                {
                    if (job.AttemptId != attempt) return true;
                    job.StatusText = "Trying direct PS4 system download...";
                }

                string directDisplayName = !string.IsNullOrEmpty(job.Name)
                    ? job.Name : (!string.IsNullOrEmpty(actualTitleId) ? actualTitleId : "package");
                int directSubType = PkgValidator.BgftSubTypeForKind(actualKindName);
                bool directTitleKnown = actualKind == PkgContentKind.BaseGame &&
                    !string.IsNullOrEmpty(actualTitleId);
                bool directTitleAtStart = directTitleKnown &&
                    PkgInstaller.IsTitleInstalled(actualTitleId);
                int directTaskId;
                string directError;
                bool directStarted = PkgInstaller.TryStartDirectBgftWebDownload(bgftUrl,
                    actualTitleId, contentId, directDisplayName, directSubType, packageSize,
                    out directTaskId, out directError);
                string directHost = "";
                try { directHost = new Uri(bgftUrl).Host; }
                catch { }

                if (directStarted)
                {
                    bool attemptChanged;
                    lock (_lock)
                    {
                        attemptChanged = job.AttemptId != attempt;
                        if (attemptChanged)
                        {
                            job.BgftTaskId = directTaskId;
                            job.BgftContentId = contentId;
                            job.BgftSubType = directSubType;
                        }
                        else
                        {
                            job.Kind = actualKindName;
                            if (!string.IsNullOrEmpty(actualTitleId)) job.TitleId = actualTitleId;
                            job.Background = true;
                            job.BgftLoopback = false;
                            job.BgftResident = false;
                            job.BgftDirect = true;
                            job.BgftLoopbackServed = true;
                            job.BgftContentId = contentId;
                            job.BgftSubType = directSubType;
                            job.BgftExpectedSize = packageSize;
                            job.BgftTaskId = directTaskId;
                            job.BgftTitlePresenceKnown = directTitleKnown;
                            job.BgftTitlePresentAtStart = directTitleAtStart;
                            job.BgftDownloadPhaseConfirmed = false;
                            ResetBgftTransientPolls(job);
                            job.State = DlState.Downloading;
                            job.Done = 0;
                            job.Total = packageSize;
                            job.StatusText = "PS4 system download started";
                        }
                    }
                    if (attemptChanged)
                    {
                        TryCancelBackgroundIdentity(job, contentId, directSubType,
                            "direct task registered after attempt change");
                        return true;
                    }
                    SaveManifest();
                    User.NotifyToast("PS4 download started");
                    LogBgftEvent("direct-start", job, "task=" + directTaskId +
                        " host=" + directHost + " sub=" + directSubType + " size=" + packageSize);
                    return true;
                }

                string directFailure = directError ?? "BGFT direct registration failed";
                if (directFailure.StartsWith("ALREADY_INSTALLED:", StringComparison.Ordinal) &&
                    PkgInstaller.IsTitleInstalled(actualTitleId))
                {
                    lock (_lock)
                    {
                        if (job.AttemptId != attempt) return true;
                        ClearBackground(job);
                        job.State = DlState.Installed;
                        job.InstallConfirmed = true;
                        CompleteByteCounters(job);
                        job.Error = null;
                        job.StatusText = "Already installed";
                    }
                    SaveManifest();
                    return true;
                }
                if (directTaskId >= 0 || directFailure.StartsWith(
                    "BGFT_UNRESOLVED:", StringComparison.Ordinal))
                {
                    if (directTaskId >= 0) lock (_lock) job.BgftTaskId = directTaskId;
                    if (!TryCancelBackgroundIdentity(job, contentId, directSubType, directFailure))
                    {
                        SaveManifest();
                        return true;
                    }
                }
                else
                {
                    LogBgftEvent("direct-failed", job, "host=" + directHost +
                        " sub=" + directSubType + " size=" + packageSize + " " + directFailure);
                }
            }

            if (ResidentDownloadService.HasDownloader)
                return HandoffResidentPackage(job, attempt, bgftUrl, actualTitleId, actualKindName, contentId, packageSize);
            // Only the acknowledged shell worker owns new native handoffs.
            SetFeederForegroundFallback(job, attempt, "Shell downloader is unavailable; keep Game Search open");
            return false;
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
            return true;
        }

        bool ResidentPublicationFailed(DlItem job, string error)
        {
            bool busy = error == "Another resident job is active";
            lock (_lock)
            {
                job.ResidentArchive = false; job.Background = false; job.BgftResident = false;
                if (busy)
                {
                    job.State = DlState.Queued;
                    job.StatusText = "Waiting for active PS4 download";
                    job.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(2).Ticks;
                }
            }
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

        void SetFeederForegroundFallback(DlItem job, int attempt, string reason)
        {
            DiscardHeaderOnlyPartial(job);
            lock (_lock)
            {
                if (job.AttemptId != attempt) return;
                job.ForceLocalInstall = true;
                job.StatusText = "Using in-app download: " + ClipMsg(reason, 42);
            }
            if (!string.IsNullOrEmpty(reason)) LogBgftEvent("fallback-local", job, reason);
            try
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                File.WriteAllText(Path.Combine(AppSettings.DataDir, "resident-error.txt"),
                    DateTime.UtcNow.ToString("o") + " " + (reason ?? "") + "\n");
            }
            catch { }
            SaveManifest();
        }

        static void DiscardHeaderOnlyPartial(DlItem job)
        {
            if (job == null || string.IsNullOrEmpty(job.DestPath)) return;
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
                    DeleteDownloadFiles(job.DestPath);
                    changed = true;
                }
                else if (job.PauseRequested)
                {
                    try
                    {
                        string part = job.DestPath + ".part";
                        if (File.Exists(part)) job.Done = new FileInfo(part).Length;
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
                User.NotifyToast("DL fail");
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
                if (l.EndsWith(".rar") || l.EndsWith(".zip") || l.EndsWith(".7z") || l.Contains(".part")) return true;
            }
            return false;
        }

        static List<string> ArchivePaths(DlItem job)
        {
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

        bool TryHandoffArchive(DlItem job, int attempt, List<ArchiveVolume> volumes, List<string> paths)
        {
            // The resident protocol has no archive-password field. Keep encrypted
            // archives in the managed extractor, which validates size and CRC.
            if (!string.IsNullOrEmpty(job.ArchivePassword)) return false;
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
                string direct = ResolveVolume(volume);
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
                job.State = DlState.Downloading; job.StatusText = "Handing RAR volumes to resident downloader";
            }
            if (!SaveManifest())
            {
                lock (_lock) { job.ResidentArchive = false; job.Background = false; job.BgftResident = false; }
                throw new IOException("Could not save archive ownership");
            }
            string error;
            if (!ResidentDownloadService.TryStartArchive(job.Id, job.DestPath, job.TitleId, job.ExpectedContentId, resolved, paths, out error))
                return ResidentPublicationFailed(job, error);
            return true;
        }

        string ResolveVolume(ArchiveVolume volume)
        {
            if (IsDirectAccess(volume.AccessType)) return volume.Url;
            if (_cfg.UseUnlockProvider && !string.Equals(_cfg.UnlockProviderId,
                UnlockProviders.NoneId, StringComparison.OrdinalIgnoreCase))
                return UnlockProviders.Unrestrict(_cfg, volume.Url);
            return FreeHosterClient.ResolveDirect(volume.Url);
        }

        void DownloadArchiveVolumes(DlItem job, int attempt)
        {
            var volumes = ArchiveVolumeSet.Decode(job.ArchiveVolumes);
            var paths = ArchivePaths(job);
            if (TryHandoffArchive(job, attempt, volumes, paths)) return;
            {
                long missing = 0;
                for (int i = 0; i < volumes.Count; i++)
                    if (!File.Exists(paths[i])) missing = checked(missing + Math.Max(0, volumes[i].Size));
                RequireTransferSpace(missing, true);
            }
            for (int i = 0; i < volumes.Count; i++)
            {
                if (CommitRequestedStop(job, attempt)) return;
                ArchiveVolume volume = volumes[i];
                string path = paths[i];
                if (!File.Exists(path))
                {
                    lock (_lock) { job.State = DlState.Downloading; job.StatusText = "Downloading RAR volume " + (i + 1) + "/" + volumes.Count; }
                    string direct = ResolveVolume(volume);
                    string part = path + ".part";
                    long offset = File.Exists(part) ? new FileInfo(part).Length : 0;
                    long baseBytes = 0;
                    long setKnown = 0;
                    int unknownVolumes = 0;
                    for (int k = 0; k < volumes.Count; k++)
                    {
                        if (volumes[k].Size > 0) setKnown = checked(setKnown + volumes[k].Size);
                        else unknownVolumes++;
                        if (k < i)
                        {
                            try { if (File.Exists(paths[k])) baseBytes = checked(baseBytes + new FileInfo(paths[k]).Length); }
                            catch { }
                        }
                    }
                    long volumeStartTicks = DateTime.UtcNow.Ticks;
                    NetHttp.DownloadFileResumable(direct, path, offset,
                        (done, total) =>
                        {
                            lock (_lock)
                            {
                                if (job.AttemptId != attempt) return;
                                job.Done = checked(baseBytes + done);
                                long volumeTotal = Math.Max(total, volume.Size);
                                long setTotal = checked(baseBytes + volumeTotal);
                                for (int k = i + 1; k < volumes.Count; k++)
                                    setTotal = checked(setTotal + Math.Max(0, volumes[k].Size));
                                if (setKnown <= 0 || unknownVolumes > 0)
                                    setTotal = Math.Max(setTotal, job.Done);
                                job.Total = setTotal;
                                long elapsed = DateTime.UtcNow.Ticks - volumeStartTicks;
                                if (elapsed > TimeSpan.TicksPerMillisecond * 400)
                                {
                                    double secs = elapsed / (double)TimeSpan.TicksPerSecond;
                                    if (secs > 0.05)
                                    {
                                        long bps = (long)((job.Done - baseBytes) / secs);
                                        if (bps > 0)
                                        {
                                            job.BytesPerSec = bps;
                                            job.EtaSeconds = job.Total > job.Done
                                                ? (int)Math.Min(int.MaxValue, (job.Total - job.Done) / bps) : 0;
                                        }
                                    }
                                }
                                job.StatusText = "Downloading RAR volume " + (i + 1) + "/" + volumes.Count;
                            }
                        },
                        () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested,
                        60000, null, null, volume.Sha256);
                }
                var probe = new DlItem { DestPath = path, ExpectedSha256 = volume.Sha256,
                    ExpectedByteSize = volume.Size };
                string error;
                if (!VerifyCandidateFile(probe, out error)) throw new InvalidDataException("Volume " + (i + 1) + ": " + error);
                PackageObjectKind kind = PackageArchive.Detect(path);
                if (kind != PackageObjectKind.Rar4 && kind != PackageObjectKind.Rar5)
                    throw new InvalidDataException("Volume " + (i + 1) + " is not RAR data");
                SaveManifest();
            }
            FinishDownloadedObject(job, attempt);
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
                objectKind == PackageObjectKind.Rar5)
            {
                string error;
                if (!VerifyCandidateFile(job, out error))
                {
                    RetainUnrecognizedDownload(job, attempt, error);
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
            string staging = Path.Combine(AppSettings.DownloadDir, ".extract", UrlTag(job.Id));
            var pending = new List<PendingLocalChild>();
            bool committed = false;
            try
            {
                // Resume after pause: moved PKGs survive (never deleted on pause),
                // so adopt them directly instead of extracting again.
                if (TryAdoptMovedFanOutChildren(job, attempt, pending))
                {
                    lock (_lock) { job.State = DlState.Finalizing; job.StatusText = "Recovered extracted packages..."; }
                    goto AdoptedPlan;
                }
                PrepareExtractionDirectory(staging);
                var paths = ArchivePaths(job);
                lock (_lock) { job.State = DlState.Finalizing; job.StatusText = "Extracting packages..."; }
                List<PackageArchiveEntry> entries = PackageArchive.ExtractPackages(paths, staging,
                    () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested,
                    (done, total) => { lock (_lock) { job.Done = done; job.Total = total; job.StatusText = "Extracting packages..."; } }, job.ArchivePassword);
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
                    if (patchVersions.TryGetValue(v, out first))
                        throw new InvalidDataException("Archive contains competing updates for version " + v +
                            " (" + first + ", " + (p.Item.Label ?? "") + "); no packages installed");
                    patchVersions[v] = p.Item.Label ?? "";
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
                    if (!CommitRequestedStop(job, attempt)) SetFailureIfCurrent(job, attempt, ex.Message);
                }
            }
            finally
            {
                try { DeleteExtractionDirectory(staging); } catch { }
            }
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
            foreach (string path in ArchivePaths(parent)) DeleteDownloadFiles(path);
            DeleteDownloadFiles(parent.DestPath);
            lock (_lock)
            {
                if (!File.Exists(parent.DestPath)) _items.Remove(parent);
                else parent.StatusText = "Packages queued; source cleanup pending";
            }
            SaveManifest();
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

        static string LinkAccessType(string url)
        {
            Uri uri;
            if (Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                string path = uri.AbsolutePath ?? "";
                if (path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)) return "Direct";
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
                DeleteDownloadFiles(source.DestPath);
                lock (_lock)
                {
                    if (!File.Exists(source.DestPath))
                    {
                        _items.Remove(source);
                        changed = true;
                    }
                    else source.StatusText = "Packages queued; source cleanup pending";
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
                { MarkInstallFailed(job.Id, metadataError); return; }
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
                    if (!PkgIntegrity.CheckInstalledBase(job.DestPath, actualTitleId, out integrityError))
                    { MarkInstallFailed(job.Id, integrityError); return; }
                    if (!PkgIntegrity.ValidatePatch(job.DestPath, actualTitleId, FirmwareInfo.Probe(),
                        text => { lock (_lock) job.StatusText = text; },
                        () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested, out integrityError))
                    { MarkInstallFailed(job.Id, integrityError); return; }
                    string identityError;
                    if (!PkgValidator.CheckRequestedIdentity(actualKindName, actualKind, actualTitleId, contentId, out identityError))
                    { MarkInstallFailed(job.Id, identityError); return; }
                    try { job.PackageVersion = PkgIntegrity.SfoValue(PkgIntegrity.Entry(job.DestPath, 0x1000), "APP_VER"); } catch { }
                    string installedName, installedVersion, installedIcon, versionError;
                    InstalledTitleScan.ReadMeta(actualTitleId, out installedName, out installedVersion, out installedIcon);
                    if (!PkgIntegrity.CheckUpdateVersion(job.PackageVersion, installedVersion, out versionError))
                    { MarkInstallFailed(job.Id, versionError); return; }
                }
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
                        bool titleKnown = actualKind == PkgContentKind.BaseGame &&
                            !string.IsNullOrEmpty(actualTitleId);
                        bool titleAtStart = titleKnown && PkgInstaller.IsTitleInstalled(actualTitleId);
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
                { MarkInstallFailed(job.Id, fallbackReason ?? "BGFT update registration failed; PKG retained"); return; }
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
                job.StatusText = "Waiting for active PS4 system download";
                job.RetryAfterUtcTicks = DateTime.UtcNow.AddSeconds(2).Ticks;
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
            InstallOutcome outcome = PkgInstaller.InstallLocal(job.DestPath, actualTitleId,
                actualKindName, out installedTitleId, out error, out taskId, false);
            if (outcome == InstallOutcome.Started)
            {
                if (taskId < 0)
                {
                    MarkInstallAccepted(job.Id, "Sent to PS4 — verification pending · PKG kept");
                    return;
                }
                string checkTitleId = !string.IsNullOrEmpty(installedTitleId)
                    ? installedTitleId : actualTitleId;
                bool localCopyComplete;
                string waitError;
                bool completed = PkgInstaller.WaitForInstall(taskId, checkTitleId, actualKind,
                    percent =>
                    {
                        lock (_lock)
                        {
                            if (job.AttemptId != attempt) return;
                            job.State = DlState.Installing;
                            job.StatusText = percent >= 0
                                ? "Installing " + percent + "%" : "Installing...";
                            if (percent >= 0)
                            {
                                job.Done = job.Total;
                            }
                        }
                    }, out localCopyComplete, out waitError);
                if (completed)
                {
                    lock (_lock) job.InstallOrderReady = localCopyComplete;
                    bool confirmedBase = actualKind == PkgContentKind.BaseGame &&
                        localCopyComplete && !string.IsNullOrEmpty(checkTitleId) &&
                        PkgInstaller.IsTitleInstalled(checkTitleId);
                    if (confirmedBase)
                        MarkInstalled(job.Id, "Installed " + checkTitleId + " — local PKG kept", false);
                    else
                        MarkInstallAccepted(job.Id,
                            actualKind == PkgContentKind.Patch || actualKind == PkgContentKind.AddOn
                                ? "Sent to PS4 — verify update/DLC · PKG kept"
                                : "Sent to PS4 — verification pending · PKG kept",
                            taskId);
                    return;
                }
                if ((waitError ?? "").StartsWith("BGFT progress") || (waitError ?? "").IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                    MarkInstallAccepted(job.Id, "PS4 install status unavailable · task and PKG retained", taskId);
                else MarkInstallFailed(job.Id, waitError ?? "Local PKG install failed");
                return;
            }
            if (outcome == InstallOutcome.AlreadyInstalled)
            {
                MarkAlreadyInstalled(job.Id, "Already installed; local PKG kept");
                return;
            }
            MarkInstallFailed(job.Id, error ?? "Local PKG install failed");
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
                    kind == PackageObjectKind.Rar5)
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

        internal static void UpdateTransferStats(DlItem item, long done, long total, string phase, long nowMs, int nativeEta)
        {
            item.Done = done; item.Total = total;
            bool transferring = phase == "downloading" || phase == "feeding" || phase == "extracting" || phase == "bgft";
            long elapsed = nowMs - item.StatsAt;
            if (!transferring || item.StatsAt == 0 || elapsed < 0 || elapsed > 15000 || done < item.StatsDone ||
                item.StatsPhase != phase || item.StatsTotal != total || item.StatsAttempt != item.AttemptId)
            {
                item.StatsAt = item.StatsAdvancedAt = nowMs; item.StatsDone = done; item.StatsTotal = total;
                item.StatsPhase = phase; item.StatsAttempt = item.AttemptId;
                item.BytesPerSec = 0; item.EtaSeconds = transferring ? nativeEta : 0;
                return;
            }
            if (elapsed < 1000) return;
            long delta = done - item.StatsDone;
            if (delta > 0)
            {
                double rate = delta * 1000.0 / elapsed;
                item.BytesPerSec = item.BytesPerSec > 0 ? item.BytesPerSec * .65 + rate * .35 : rate;
                item.StatsAdvancedAt = nowMs;
            }
            else if (nowMs - item.StatsAdvancedAt >= 5000) item.BytesPerSec = 0;
            item.StatsAt = nowMs; item.StatsDone = done;
            item.EtaSeconds = nativeEta > 0 ? nativeEta : total > done && item.BytesPerSec > 0
                ? (int)Math.Min(int.MaxValue, Math.Ceiling((total - done) / item.BytesPerSec)) : 0;
            if (total > 0 && done >= total) { item.BytesPerSec = 0; item.EtaSeconds = 0; }
        }

        static long TransferClockMs()
        {
            return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000.0 / System.Diagnostics.Stopwatch.Frequency));
        }

        bool ConfirmInstalledUpdate(DlItem item)
        {
            if (item == null || !item.Background || item.State == DlState.Canceled ||
                !string.IsNullOrEmpty(item.ArchiveVolumes) || PkgValidator.BgftSubTypeForKind(item.Kind) != 8) return false;
            int attempt = item.AttemptId;
            if (string.IsNullOrEmpty(item.PackageVersion))
            {
                try { item.PackageVersion = PkgIntegrity.SfoValue(PkgIntegrity.Entry(item.DestPath, 0x1000), "APP_VER"); }
                catch { return false; }
                if (string.IsNullOrEmpty(item.PackageVersion)) return false;
            }
            string version = null, installedPath = null;
            foreach (string root in new[] { "/user/patch/", "/mnt/ext0/user/patch/" })
            {
                string installed = root + item.TitleId + "/patch.pkg";
                if (!PkgInstallPolicy.MatchesInstalledContainer(item.DestPath, installed)) continue;
                try
                {
                    byte[] sfo = PkgIntegrity.Entry(installed, 0x1000);
                    Version wanted, actual;
                    string candidate = PkgIntegrity.SfoValue(sfo, "APP_VER");
                    if (PkgIntegrity.SfoValue(sfo, "CATEGORY") != "gp" ||
                        !Version.TryParse(item.PackageVersion.TrimStart('v', 'V'), out wanted) ||
                        !Version.TryParse(candidate.TrimStart('v', 'V'), out actual) || actual < wanted) continue;
                    version = candidate; installedPath = installed; break;
                }
                catch { }
            }
            if (version == null) { item.BgftInstalledProofPolls = 0; return false; }
            // BGFT reference-package totals can omit metadata. The promoted patch
            // is stronger evidence than byte counters, feeder lifetime or an old error.
            if (++item.BgftInstalledProofPolls < 8) return false;
            string verificationError;
            if (!PkgIntegrity.ValidatePatch(installedPath, item.TitleId, "", null, null, out verificationError))
            { item.BgftInstalledProofPolls = 0; return false; }
            lock (_lock) {
                if (!item.Background || item.AttemptId != attempt || item.State == DlState.Canceled) return false;
                item.State = DlState.Installed; item.InstallConfirmed = true; item.InstallOrderReady = true;
                item.Error = null;
                item.StatusText = "Installed update v" + version; item.Done = item.Total; item.BytesPerSec = 0; item.EtaSeconds = 0;
                if (item.ResidentArchive) { ResidentDownloadService.Release(item.Id); item.ResidentArchive = false; }
                ClearBackground(item);
            }
            SaveManifest(); return true;
        }

        void RefreshResidentArchive(DlItem item)
        {
            ResidentDownloadStatus status;
            if (!ResidentDownloadService.TryGetStatus(item.Id, out status))
            {
                lock (_lock) item.StatusText = ResidentDownloadService.HasDownloader
                    ? "Waiting for shell download acknowledgement" : "Waiting for GoldHEN shell downloader";
                return;
            }
            bool changed = false, release = false;
            lock (_lock)
            {
                if (!item.ResidentArchive || !item.Background) return;
                if (item.State == DlState.Failed && status.State == "failed" && item.Error == status.Error) return;
                // Extraction counts output bytes; the resident total still describes compressed input.
                UpdateTransferStats(item, status.Done, status.State == "extracting" ? 0 : status.Total,
                    status.State, TransferClockMs(), 0);
                item.StatusText = status.State == "extracting" ? "Extracting RAR packages in resident worker" :
                    status.State == "installing" ? "Installing packages in order" :
                    status.State == "validating" ? "Verifying package integrity · " + Human(status.Done) + " checked" : "Resident download: " + status.State;
                if (status.State == "installing") item.State = DlState.Installing;
                else if (status.State == "validating" || status.State == "extracting")
                { item.State = DlState.Finalizing; item.BytesPerSec = 0; item.EtaSeconds = 0; }
                else if (status.State == "downloading" || status.State == "feeding" || status.State == "ready") item.State = DlState.Downloading;
                if (status.State == "installed")
                {
                    item.State = DlState.Installed; item.InstallConfirmed = true; item.InstallOrderReady = true;
                    item.Background = false; item.BgftResident = false; item.Error = null;
                    item.BytesPerSec = 0; item.EtaSeconds = 0; CompleteByteCounters(item);
                    item.StatusText = "Installed · verified by PS4 · local files retained";
                    release = true; changed = true;
                }
                else if (status.State == "submitted")
                {
                    item.State = DlState.Submitted; item.InstallConfirmed = false; item.InstallOrderReady = true;
                    item.Background = false; item.BgftResident = false;
                    item.StatusText = "Packages sent to PS4 in order; files retained";
                    release = true; changed = true;
                }
                else if (status.State == "failed" || status.State == "canceled")
                {
                    item.State = status.State == "canceled" ? DlState.Canceled : DlState.Failed;
                    item.Error = status.Error;
                    item.StatusText = !string.IsNullOrEmpty(status.Error) ? status.Error :
                        status.State == "canceled" ? "Resident download canceled; files retained" : "Resident download failed; files retained";
                    if (status.State == "canceled")
                    {
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

        void RefreshBackgroundTasks()
        {
            var active = new List<DlItem>();
            lock (_lock)
            {
                foreach (var item in _items)
                {
                    if (!item.Background) continue;
                    if (item.ResidentArchive || item.State == DlState.Downloading || item.State == DlState.Resolving ||
                        item.State == DlState.Installing || item.State == DlState.Submitted || item.State == DlState.Failed)
                        active.Add(item);
                }
            }

            foreach (var item in active)
            {
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
                                UpdateTransferStats(cur, Math.Min(residentStatus.Done, Math.Max(0, residentStatus.Total - 1)),
                                    residentStatus.Total, residentStatus.State, TransferClockMs(), 0);
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
                        // Root cause for 0x80991404 (HTTP 404): BGFT fetched a URL
                        // the local server no longer owns. Before 5.10 the app and
                        // resident shell shared port 8742 with different /pkg routes,
                        // so a task could 404 against the other owner's listener.
                        // App loopback is now 8743; resident stays on 8742.
                        string code = "BGFT 0x" + unchecked((uint)progress.ErrorResult).ToString("X");
                        string diag = code + " task=" + cur.BgftTaskId +
                            " sub=" + cur.BgftSubType + " kind=" + (kind ?? cur.Kind) +
                            " title=" + (titleId ?? cur.TitleId) +
                            " content=" + (contentId ?? cur.BgftContentId ?? "") +
                            " done=" + progress.Done + "/" + progress.Total +
                            " copy=" + progress.LocalCopyPercent + "%";
                        if (unchecked((uint)progress.ErrorResult) == 0x80991404u)
                            diag += " · HTTP 404: local package URL not found — retry uses a fresh local URL, file kept";
                        LogBgftEvent("task-error-retained", cur, diag);
                        // Release the loopback route so a fresh retry (or the next
                        // queued package) does not reuse a stale 404 route.
                        if (cur.BgftLoopback) FeederMarkFailed(cur);
                        FallbackBgftToLocal(cur, diag);
                        stateChanged = true;
                    }
                    else
                    {
                        const int StallPolls = 150;
                        bool basePayloadComplete = PkgValidator.BgftSubTypeForKind(cur.Kind) == 6 &&
                            progress.DownloadComplete && progress.Total > 0 && progress.LocalCopyPercent == 100;
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

                        // Credible download-complete candidate: totals match preflight size.
                        bool totalCredible = expected > 0 && progress.Total > 0 &&
                            progress.Total >= expected - tol && progress.Total <= expected + tol;
                        bool dlCandidate = basePayloadComplete || ((cur.BgftLoopback || cur.BgftDirect) &&
                            (cur.BgftDirect || cur.BgftLoopbackServed) &&
                            totalCredible && progress.Done >= progress.Total &&
                            progress.Done >= expected - tol && progress.Done > 0);
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
            if (item.BgftLoopback) FeederRelease(item);
            item.Background = false;
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
        }

        bool TryCancelBackgroundIdentity(DlItem item, string contentId, int subType, string reason,
            bool failLoopback = true)
        {
            int activeTask = -1;
            string cancelError = null;
            bool canceled = !string.IsNullOrEmpty(contentId) && subType > 0 &&
                PkgInstaller.CancelBackground(item != null ? item.BgftTaskId : -1,
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


        static void LogBgftEvent(string kind, DlItem it, string detail)
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
                    " " + ClipMsg(detail, 120) +
                    Environment.NewLine;
                File.AppendAllText(Path.Combine(dir, "bgft-download.log"), line);
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
                        sb.Append("\"install_order_ready\":").Append(it.InstallOrderReady ? "true" : "false").Append(',');
                        sb.Append("\"archive_volumes\":\"").Append(JsonLite.Escape(it.ArchiveVolumes)).Append("\",");
                        sb.Append("\"archive_password\":\"").Append(JsonLite.Escape(it.ArchivePassword)).Append("\",");
                        sb.Append("\"bgft_local_install\":").Append(it.BgftLocalInstall ? "true" : "false").Append(",");
                        sb.Append("\"install_after_id\":\"").Append(JsonLite.Escape(it.InstallAfterId)).Append("\",");
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
            if (string.Equals(storedState, "Failed", StringComparison.OrdinalIgnoreCase))
            {
                item.State = DlState.Failed;
                item.StatusText = "PS4 task failed · file retained · CROSS retries";
                return;
            }
            if (item.ResidentArchive)
            {
                ResidentDownloadStatus resident;
                if (!ResidentDownloadService.HasJob(item.Id) && !ResidentDownloadService.TryGetStatus(item.Id, out resident))
                {
                    item.ResidentArchive = false; item.Background = false; item.BgftResident = false;
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
                string residentError;
                ResidentDownloadStatus residentStatus;
                if (ResidentDownloadService.EnsureAvailable(out residentError) &&
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
                if (!string.IsNullOrEmpty(residentError))
                    LogBgftEvent("resident-recovery", item, residentError);
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

        void ResetQueueFor501()
        {
            string marker = Path.Combine(AppSettings.DataDir, "fresh-5.01.done");
            if (File.Exists(marker)) return;
            try
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                RecoverManifest();
                string backup = ManifestPath + ".before-5.01";
                if (File.Exists(ManifestPath))
                {
                    string json = File.ReadAllText(ManifestPath);
                    if (!File.Exists(backup)) File.Copy(ManifestPath, backup);
                    foreach (string obj in JsonLite.ExtractObjectArray(json, "items"))
                    {
                        string id = JsonLite.GetString(obj, "id") ?? "";
                        if (JsonLite.GetBool(obj, "background") || JsonLite.GetBool(obj, "bgft_loopback"))
                        {
                            int task;
                            string error;
                            bool canceled = PkgInstaller.CancelBackground(
                                ParseInt(JsonLite.GetString(obj, "bgft_task"), -1),
                                JsonLite.GetString(obj, "bgft_content_id") ?? "",
                                ParseInt(JsonLite.GetString(obj, "bgft_sub_type"), 0), out task, out error);
                            if (!canceled) File.AppendAllText(Path.Combine(AppSettings.DataDir, "fresh-5.01.log"),
                                id + " | " + (error ?? "PS4 task could not be canceled") + Environment.NewLine);
                        }
                    }
                }
                ResidentDownloadService.ResetQueueForFreshStart();
                File.WriteAllText(ManifestPath + ".tmp", "{\"items\":[]}");
                ReplaceManifest(ManifestPath + ".tmp", ManifestPath);
                if (File.Exists(ManifestPath + ".bak")) File.Delete(ManifestPath + ".bak");
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            }
            catch (Exception ex)
            {
                try { Orbis.Internals.Kernel.Log("Fresh queue reset: " + ex.Message); } catch { }
            }
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
                        ArchiveVolumes = JsonLite.GetString(obj, "archive_volumes") ?? "",
                        ArchivePassword = JsonLite.GetString(obj, "archive_password") ?? "",
                        InstallAfterId = JsonLite.GetString(obj, "install_after_id") ?? "",
                        BgftLocalInstall = JsonLite.GetBool(obj, "bgft_local_install"),
                        ResidentArchive = JsonLite.GetBool(obj, "resident_archive"),
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
                    string identity = (it.TitleId ?? "") + "\n" + (it.Kind ?? "") + "\n" +
                        (it.HosterUrl ?? "");
                    if (it.Background || it.BgftLoopback)
                        ResetBgftTransientPolls(it);
                    string uniqueName = it.TitleId + "_" + it.Kind + "_" + UrlTag(it.HosterUrl) + ".pkg";
                    foreach (char c in Path.GetInvalidFileNameChars()) uniqueName = uniqueName.Replace(c, '_');
                    string uniquePath = Path.Combine(AppSettings.DownloadDir, uniqueName);
                    if (!IsOwnedDownloadPath(it.DestPath))
                    {
                        it.DestPath = uniquePath;
                        migrated = true;
                    }
                    else if (!string.Equals(it.DestPath, uniquePath, StringComparison.OrdinalIgnoreCase))
                    {
                        bool hasLegacyFiles = File.Exists(it.DestPath) ||
                            File.Exists(it.DestPath + ".part") || File.Exists(it.DestPath + ".part.resume");
                        if (!hasLegacyFiles) it.DestPath = uniquePath;
                        migrated = true;
                    }
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
                        PackageObjectKind diskObject;
                        try { diskObject = PackageArchive.Detect(it.DestPath); }
                        catch { diskObject = PackageObjectKind.Unknown; }
                        if (diskObject != PackageObjectKind.Pkg)
                        {
                            ClearBackground(it);
                            it.Done = it.Total = new FileInfo(it.DestPath).Length;
                            if (string.Equals(it.AccessType, "FanOutSource",
                                StringComparison.OrdinalIgnoreCase))
                            {
                                it.State = DlState.Completed;
                                it.StatusText = "Downloaded source cleanup pending";
                            }
                            else if (string.Equals(st, "Failed", StringComparison.OrdinalIgnoreCase))
                                it.State = DlState.Failed;
                            else if (string.Equals(st, "Canceled", StringComparison.OrdinalIgnoreCase))
                                it.State = DlState.Canceled;
                            else if (string.Equals(st, "Paused", StringComparison.OrdinalIgnoreCase))
                            {
                                it.State = DlState.Paused;
                                it.StatusText = "Downloaded object paused";
                            }
                            else
                            {
                                it.State = DlState.Queued;
                                it.Error = null;
                                it.StatusText = "Downloaded object queued for processing";
                            }
                            _items.Add(it);
                            migrated = true;
                            continue;
                        }
                        PkgContentKind diskKind;
                        string diskKindName;
                        string diskContentId;
                        string diskTitleId;
                        long diskSize;
                        string vd;
                        if (TryValidateLocalPackage(it, out diskKind, out diskKindName,
                            out diskContentId, out diskTitleId, out diskSize, out vd))
                        {
                            it.Kind = diskKindName;
                            if (!string.IsNullOrEmpty(diskTitleId)) it.TitleId = diskTitleId;
                            bool queuedLocal = string.Equals(st, "Queued", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(st, "Resolving", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(st, "Downloading", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(st, "Finalizing", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(st, "Installing", StringComparison.OrdinalIgnoreCase);
                            if (string.Equals(st, "Installed", StringComparison.OrdinalIgnoreCase) &&
                                it.InstallConfirmed)
                            {
                                if (PkgInstallPolicy.IsAddonOrPatchName(it.Kind))
                                {
                                    it.State = DlState.Completed;
                                    it.InstallConfirmed = false;
                                    it.StatusText = "PKG on disk — CROSS installs";
                                    migrated = true;
                                }
                                else
                                    it.State = DlState.Installed;
                            }
                            else if (string.Equals(st, "Submitted", StringComparison.OrdinalIgnoreCase))
                            {
                                it.State = DlState.Completed;
                                it.InstallConfirmed = false;
                                it.StatusText = "Previous install request unverified — CROSS retries";
                            }
                            else if (queuedLocal)
                            {
                                it.State = DlState.Queued;
                                it.InstallConfirmed = false;
                                if (string.Equals(st, "Installing", StringComparison.OrdinalIgnoreCase))
                                    it.ForceLocalInstall = true;
                                it.StatusText = "Validated local PKG queued";
                            }
                            else
                            {
                                it.State = DlState.Completed;
                                it.InstallConfirmed = false;
                            }
                            ClearBackground(it);
                            it.Done = it.Total = diskSize;
                            if (!queuedLocal && (it.State != DlState.Completed ||
                                !string.Equals(st, "Submitted", StringComparison.OrdinalIgnoreCase))
                            )
                                it.StatusText = it.State == DlState.Installed
                                ? "Installed; local PKG retained"
                                : it.State == DlState.Submitted
                                    ? "Sent to PS4 — verify · PKG kept"
                                    : "Complete+valid";
                        }
                        else
                        {
                            DiscardInvalidDownload(it, vd ?? "Invalid PKG on disk");
                            it.State = DlState.Failed;
                            it.StatusText = "Invalid file removed — CROSS re-downloads";
                        }
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
                                if (File.Exists(part)) it.Done = new FileInfo(part).Length;
                                it.StatusText = string.IsNullOrEmpty(it.StatusText)
                                    ? "Failed — CROSS retries" : it.StatusText;
                            }
                            else
                            {
                                it.State = DlState.Paused;
                                string part = it.DestPath + ".part";
                                if (File.Exists(part))
                                    it.Done = new FileInfo(part).Length;
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
                        Math.Min(999999999999L, new FileInfo(part).Length);
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
            if (!string.IsNullOrEmpty(item.DestPath))
                DeleteDownloadFiles(item.DestPath);
            item.Done = 0;
            item.Total = 0;
            item.InstallConfirmed = false;
            item.ForceLocalInstall = false;
            item.Error = reason;
            item.BytesPerSec = 0;
            item.EtaSeconds = 0;
            ClearBackground(item);
        }

        static void DeleteDownloadFiles(string finalPath)
        {
            if (!IsOwnedDownloadPath(finalPath)) return;
            try { DownloadResumeInfo.DeletePartial(finalPath + ".part"); } catch { }
            try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
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
            string[] roots =
            {
                NormalizePath(AppSettings.DownloadDir),
                NormalizePath("/data/GameSearch/downloads"),
                NormalizePath("/user/data/GameSearch/downloads")
            };
            foreach (string root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
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
            AppSettings cfg = _cfg;
            return cfg != null && cfg.UseUnlockProvider &&
                !string.Equals(cfg.UnlockProviderId, UnlockProviders.NoneId,
                    StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(job.HosterUrl);
        }

        static bool IsAuthExpiry(Exception ex)
        {
            string message = ex != null && ex.Message != null ? ex.Message : "";
            return message.IndexOf("HTTP 401", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("HTTP 403", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf(" 401 ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf(" 403 ", StringComparison.OrdinalIgnoreCase) >= 0;
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
