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
            CompletedRecheck recheck;
            lock (_lock)
            {
                // Keep a worker from claiming the row between the legacy-compatible enqueue and
                // completion of the candidate snapshot.
                id = EnqueueCore(game, link, candidate.SourceId, candidate.SourceVersion,
                    candidate.CandidateId, candidate.AccessType.ToString(), hash,
                    candidate.SourceAttribution, out message, out recheck);
            lock (_lock) { var queued = Find(id); if (queued != null && string.IsNullOrEmpty(queued.PackageVersion) && !string.IsNullOrEmpty(candidate.PackageVersion)) queued.PackageVersion = candidate.PackageVersion ?? ""; }
            SaveManifest();
                var item = Find(id);
                if (item != null)
                {
                    if (mirrors != null) item.MirrorCandidates = PackageMirrorFallback.Encode(candidate, mirrors);
                    if (string.IsNullOrEmpty(item.ArchiveVolumes)) item.ArchiveVolumes = candidate.ArchiveVolumes ?? "";
                    if (!string.IsNullOrEmpty(item.ArchiveVolumes)) SetArchiveVolumesFormat(item);
                    else if (string.IsNullOrEmpty(item.ContainerFormat))
                        item.ContainerFormat = ContainerFormatFromFileName(link.Label);
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
                    // A completed package is checked against this candidate's size and
                    // SHA-256 after the queue lock is released (FinishCompletedRecheck).
                    if (item.State == DlState.Completed)
                    {
                        if (recheck == null || !object.ReferenceEquals(recheck.Row, item)) recheck = NewCompletedRecheck(item);
                        recheck.CandidateProbe = VerificationProbe(item);
                    }
                }
            }
            SaveManifest();
            if (recheck != null && FinishCompletedRecheck(recheck, ref message)) SaveManifest();
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
            CompletedRecheck recheck;
            string id = EnqueueCore(game, link, sourceId, sourceVersion, candidateId, accessType,
                expectedSha256, sourceAttribution, out message, out recheck);
            if (recheck != null && FinishCompletedRecheck(recheck, ref message)) SaveManifest();
            return id;
        }

        // A completed package that Enqueue finds is validated again. With a SHA-256 that
        // reads the whole file, minutes for a large game, so it never runs under the queue
        // lock: it runs on copies of the row's identity taken when the row was matched, and
        // its result is applied only while the row is unchanged.
        sealed class CompletedRecheck
        {
            public DlItem Row;
            public int Attempt;
            public string Path, Status;
            public DlItem PackageProbe, CandidateProbe;
            public bool StatusShown;
        }

        static CompletedRecheck NewCompletedRecheck(DlItem row)
        {
            return new CompletedRecheck { Row = row, Attempt = row.AttemptId, Path = row.DestPath, Status = row.StatusText };
        }

        static DlItem VerificationProbe(DlItem row)
        {
            return new DlItem { Id = row.Id, DestPath = row.DestPath, TitleId = row.TitleId, Kind = row.Kind,
                ExpectedSha256 = row.ExpectedSha256, ExpectedByteSize = row.ExpectedByteSize,
                ExpectedContentId = row.ExpectedContentId };
        }

        // Caller holds _lock.
        bool IsCurrentRecheck(CompletedRecheck check)
        {
            DlItem row = check.Row;
            return object.ReferenceEquals(Find(row.Id), row) && row.AttemptId == check.Attempt &&
                row.State == DlState.Completed && SamePath(row.DestPath, check.Path) &&
                !_activeIds.Contains(row.Id) && !row.RemoveRequested && !row.ResidentRemovePending;
        }

        bool VerifyRecheckProbe(CompletedRecheck check, DlItem probe, out string error)
        {
            DlItem row = check.Row;
            int attempt = check.Attempt;
            return VerifyCandidateFile(probe,
                text => { lock (_lock) if (IsCurrentRecheck(check)) { row.StatusText = text; check.StatusShown = true; } },
                () => row.AttemptId != attempt || row.CancelRequested || row.PauseRequested || row.RemoveRequested,
                out error);
        }

        // Runs a CompletedRecheck with no queue lock held. Returns true when it changed the
        // row, so the caller saves the manifest.
        bool FinishCompletedRecheck(CompletedRecheck check, ref string message)
        {
            DlItem row = check.Row;
            string error = null;
            bool packageValid = true, candidateValid = true;
            try
            {
                if (check.PackageProbe != null)
                {
                    // Hash first, bound to the row's own stop requests; the metadata checks
                    // after it read PkgIntegrity's verified-file cache instead of hashing again.
                    PkgContentKind kind;
                    string kindName, contentId, titleId;
                    long size;
                    packageValid = VerifyRecheckProbe(check, check.PackageProbe, out error) &&
                        TryValidateLocalPackage(check.PackageProbe, out kind, out kindName,
                            out contentId, out titleId, out size, out error);
                }
                if (packageValid && check.CandidateProbe != null)
                    candidateValid = VerifyRecheckProbe(check, check.CandidateProbe, out error);
            }
            catch (OperationCanceledException)
            {
                // Removed or restarted while checking: the newer request owns the row.
                packageValid = candidateValid = true;
            }
            lock (_lock)
            {
                if (!IsCurrentRecheck(check)) return false;
                if (!packageValid)
                {
                    bool removed = DiscardInvalidDownload(row, error ?? "Previous file was invalid");
                    row.State = removed ? DlState.Queued : DlState.Failed;
                    row.StatusText = removed ? "Queued (replaced invalid file)" : "Invalid file retained; remove it before retrying";
                    if (removed) row.Error = null;
                    message = removed ? "Previous file was invalid — queued again" : row.Error;
                    return true;
                }
                if (!candidateValid)
                {
                    DeleteDownloadFiles(row);
                    row.State = DlState.Failed;
                    row.Error = error;
                    row.StatusText = "Candidate integrity check failed";
                    row.Done = row.Total = 0;
                    return true;
                }
                if (!check.StatusShown) return false;
                row.StatusText = check.Status;
                return true;
            }
        }

        string EnqueueCore(GameHit game, PkgLink link, string sourceId, string sourceVersion,
            string candidateId, string accessType, string expectedSha256, string sourceAttribution,
            out string message, out CompletedRecheck recheck)
        {
            recheck = null;
            if (game == null || link == null) throw new Exception("Bad download");
            if (accessType == "Personal")
            {
                string locator = CloudCatalog.ImportLocator(link.Url);
                if (locator != null) { link.Url = locator; accessType = "Cloud"; }
            }
            // Personal and cloud files carry no catalog metadata. A "Name-CUSA00000.pkg"
            // file name supplies the title ID so the normal cover/backdrop lookup runs.
            if (sourceId == "personal" && string.IsNullOrEmpty(game.TitleId))
            {
                string derivedTitleId, derivedName;
                if (DownloadLinkParser.TryPackageIdentity(game.Name, link.Url, out derivedTitleId, out derivedName))
                    game = new GameHit { TitleId = derivedTitleId,
                        Name = derivedName.Length > 0 ? derivedName : (game.Name ?? derivedTitleId), ImageUrl = game.ImageUrl ?? "" };
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
                        // A removal that is still waiting for its writer or resident owner
                        // must finish first; reviving it would inherit the stop request.
                        if (existing.ResidentRemovePending || (existing.RemoveRequested &&
                            (_activeIds.Contains(existing.Id) || !IsTerminal(existing.State))))
                        {
                            message = "Previous attempt is still stopping; queue again shortly";
                            break;
                        }
                        // Legacy personal rows were queued without a title ID.
                        if (sourceId == "personal" && string.IsNullOrEmpty(existing.TitleId) &&
                            !string.IsNullOrEmpty(game.TitleId) && !existing.Background &&
                            !_activeIds.Contains(existing.Id) && (existing.State == DlState.Queued || IsTerminal(existing.State)))
                        {
                            existing.TitleId = game.TitleId;
                            changed = true;
                        }
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
                                existing.CancelRequested = true;
                                MarkResidentFailed(existing, row => row.CancelRequested);
                                existing.StatusText = "Stopping resident download before retry";
                                message = existing.StatusText;
                                changed = true;
                                break;
                            }
                            if (existing.Background)
                            {
                                int activeTask;
                                string cancelError;
                                if (!CancelRowBackground(existing, out activeTask, out cancelError))
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
                            // An explicit re-queue supersedes a terminal row's stale removal
                            // request; otherwise the new attempt aborts as soon as it starts.
                            existing.RemoveRequested = false;
                            existing.State = DlState.Queued;
                            existing.TransientHttpRetries = 0;
                            existing.HttpRetryRenewPending = false;
                            existing.HostSupportRetries = 0;
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
                            // Validation hashes the whole package when a SHA-256 is known.
                            // The caller runs it after this lock (FinishCompletedRecheck).
                            recheck = NewCompletedRecheck(existing);
                            recheck.PackageProbe = VerificationProbe(existing);
                            message = "Package already downloaded";
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
                ContainerFormat = ContainerFormatFromFileName(link.Url),
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
            if (string.IsNullOrEmpty(item.ContainerFormat))
                item.ContainerFormat = ContainerFormatFromFileName(item.Label);
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
                    bool ftp = LocalInstallSource.IsInboxPath(canonical);
                    // An FTP upload has no source row, so it gets the archive passwords a
                    // download receives from the host its file name is tagged with, if any.
                    string set; int volume;
                    string[] passwords = ftp && source.Kind == "archive" && ArchiveVolumeSet.TryIndex(Path.GetFileName(canonical), out set, out volume)
                        ? DistributionSettings.ArchivePasswordsForFileName(Path.GetFileName(canonical)) : new string[0];
                    pending.Add(new DlItem {
                        Id = "usb_" + Guid.NewGuid().ToString("N"), TitleId = source.TitleId,
                        Name = source.Name, ImageUrl = source.ImagePath, Kind = source.Kind,
                        Label = (ftp ? "FTP · " : "USB · ") + Path.GetFileName(canonical), DestPath = canonical,
                        HosterUrl = "local:" + source.Fingerprint, SourceId = "local-usb", AccessType = "Local",
                        CandidateId = source.Fingerprint, LocalSource = true, LocalSourceFingerprint = source.Fingerprint,
                        ExpectedContentId = source.ContentId, ExpectedByteSize = source.Size,
                        PackageVersion = source.Version, Total = source.Size, Done = source.Size,
                        ArchivePassword = passwords.Length > 0 ? passwords[0] : "",
                        ArchivePasswords = ArchivePasswordDefaults.EncodeLenient(passwords),
                        State = DlState.Queued, StatusText = ftp ? "Queued for installation; FTP file retained" :
                            "Queued for USB installation; source file retained"
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
            var replaced = new List<KeyValuePair<int, DlItem>>();
            lock (_lock)
            {
                foreach (DlItem item in pending)
                {
                    bool duplicate = false;
                    var stale = new List<DlItem>();
                    foreach (DlItem existing in _items)
                        if (SamePath(existing.DestPath, item.DestPath) ||
                            (existing.LocalSource && !string.IsNullOrEmpty(item.ExpectedContentId) &&
                                existing.ExpectedContentId == item.ExpectedContentId && existing.LocalSourceFingerprint == item.LocalSourceFingerprint))
                        {
                            // A finished row for the same file is history, not a live queue entry.
                            // Re-uploading after the game was deleted must queue again; the new row
                            // checks install state freshly instead of inheriting "Installed".
                            if (IsFinishedLocalRow(existing)) { stale.Add(existing); continue; }
                            duplicate = true; break;
                        }
                    if (duplicate) { duplicates++; continue; }
                    foreach (DlItem old in stale)
                    {
                        int index = _items.IndexOf(old);
                        if (index < 0) continue;
                        _items.RemoveAt(index); replaced.Add(new KeyValuePair<int, DlItem>(index, old));
                        PreserveConfirmedDependency(old);
                    }
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
                    for (int i = replaced.Count - 1; i >= 0; i--)
                        _items.Insert(Math.Min(replaced[i].Key, _items.Count), replaced[i].Value);
                    error = "USB queue could not be saved; original files are unchanged"; return 0;
                }
            }
            if (duplicates > 0) problems.Add(duplicates + " file(s) already in the queue");
            if (problems.Count > 0) error = string.Join("; ", problems.ToArray());
            return added.Count;
        }

        // Caller holds _lock. A local row that finished (installed, canceled or failed)
        // and that no worker, resident slot or PS4 task still owns can be replaced by a
        // new selection of the same file.
        bool IsFinishedLocalRow(DlItem item)
        {
            return item != null && item.LocalSource &&
                (item.State == DlState.Installed || item.State == DlState.Completed ||
                    item.State == DlState.Canceled || item.State == DlState.Failed) &&
                !item.Background && !item.ResidentRemovePending && !item.ResidentRetryPending &&
                !(item.InstallSubmitted && !item.InstallConfirmed) && item.BgftTaskId < 0 &&
                !_activeIds.Contains(item.Id) && !ResidentDownloadService.HasJob(item.Id);
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
                        ResidentDetachRequested = i.ResidentDetachRequested,
                        ResidentRemoveStuck = i.ResidentRemoveStuck,
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
                        StatsPhase = i.StatsPhase,
                        CancelRequested = i.CancelRequested,
                        PauseRequested = i.PauseRequested,
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
                        BaseStorageRefused = i.BaseStorageRefused,
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
            DlItem it, probe;
            int attempt;
            DlState state;
            string previousStatus;
            lock (_lock)
            {
                it = Find(id);
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
                probe = VerificationProbe(it);
                attempt = it.AttemptId;
                state = it.State;
                previousStatus = it.StatusText;
            }
            // A whole-file SHA-256 takes minutes for a large package, so the package is
            // checked with no queue lock held and the result applies only to an unchanged row.
            string localError;
            PkgContentKind localKind;
            string localKindName;
            string localContentId;
            string localTitleId;
            long localSize;
            bool valid, statusShown = false;
            DlItem row = it;
            try
            {
                valid = VerifyCandidateFile(probe,
                    text => { lock (_lock) if (object.ReferenceEquals(Find(id), row) && row.AttemptId == attempt) { row.StatusText = text; statusShown = true; } },
                    () => row.AttemptId != attempt || row.CancelRequested || row.PauseRequested || row.RemoveRequested,
                    out localError) &&
                    TryValidateLocalPackage(probe, out localKind, out localKindName,
                        out localContentId, out localTitleId, out localSize, out localError);
            }
            catch (OperationCanceledException)
            {
                error = "Package changed while it was being checked; try again";
                return false;
            }
            lock (_lock)
            {
                if (!object.ReferenceEquals(Find(id), it) || it.AttemptId != attempt || it.State != state ||
                    !SamePath(it.DestPath, probe.DestPath))
                {
                    error = "Package changed while it was being checked; try again";
                    return false;
                }
                if (!valid)
                {
                    bool removed = DiscardInvalidDownload(it, localError ?? "Invalid PKG on disk");
                    it.State = DlState.Failed;
                    it.StatusText = removed ? "Invalid file removed — CROSS re-downloads" : "Invalid file retained; remove it before retrying";
                    SaveManifest();
                    error = removed ? "PKG was incomplete or corrupt and was removed" : it.Error;
                    return false;
                }
                if (statusShown) it.StatusText = previousStatus;
                return true;
            }
        }

        static bool InstallPrerequisiteReady(DlItem item)
        {
            return item != null &&
                ((item.State == DlState.Installed && item.InstallConfirmed) ||
                 (item.State == DlState.Completed && item.InstallOrderReady && string.IsNullOrEmpty(item.Error)) ||
                 (item.State == DlState.Submitted && item.Kind == "theme-license" &&
                    item.InstallSubmitted && !item.InstallConfirmed && item.InstallOrderReady &&
                    item.BgftLocalInstall && item.BgftSubType == 9 && item.BgftLoopbackServed &&
                    string.IsNullOrEmpty(item.Error)));
        }

        static bool IsSameTitleDlcDependency(DlItem previous, DlItem next)
        {
            return previous != null && next != null &&
                string.Equals(previous.Kind, "dlc", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(next.Kind, "dlc", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(next.TitleId) &&
                string.Equals(previous.TitleId, next.TitleId, StringComparison.OrdinalIgnoreCase);
        }

        // Older manifests can carry an archive's DLC1 -> DLC2 chain. Walk through
        // those same-title sibling edges to the actual base/update prerequisite so a
        // failed sibling is ignored without losing an unresolved lower-rank dependency.
        bool ResolveInstallDependency(DlItem item, out string dependencyId, out bool dependencyConfirmed)
        {
            dependencyId = item == null ? "" : (item.InstallAfterId ?? "");
            dependencyConfirmed = item != null && item.InstallAfterConfirmed;
            if (item == null || !string.Equals(item.Kind, "dlc", StringComparison.OrdinalIgnoreCase))
                return !string.IsNullOrEmpty(dependencyId);

            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (!string.IsNullOrEmpty(dependencyId))
            {
                if (!visited.Add(dependencyId))
                {
                    dependencyConfirmed = false;
                    return true;
                }
                DlItem previous = Find(dependencyId);
                if (!IsSameTitleDlcDependency(previous, item)) return true;
                dependencyId = previous.InstallAfterId ?? "";
                dependencyConfirmed = previous.InstallAfterConfirmed;
            }
            dependencyConfirmed = false;
            return false;
        }

        internal static void MarkThemeLicenseCopyReady(DlItem item)
        {
            item.State = DlState.Submitted;
            item.InstallOrderReady = true;
            item.InstallConfirmed = false;
            item.InstallSubmitted = true;
            // This is the loopback copy receipt, not proof that the PS4 registered the license.
            item.StatusText = "Theme license sent to PS4; PKG retained for verification";
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
                string dependencyId;
                bool dependencyConfirmed;
                bool hasDependency = ResolveInstallDependency(item, out dependencyId, out dependencyConfirmed);
                if (string.Equals(item.Kind, "dlc", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(item.InstallAfterId ?? "", dependencyId, StringComparison.Ordinal))
                {
                    item.InstallAfterId = dependencyId;
                    item.InstallAfterConfirmed = dependencyConfirmed && hasDependency;
                }
                if (hasDependency)
                {
                    DlItem previous = Find(dependencyId);
                    bool replacedPatch = CanReplaceFailedPatch(previous, item);
                    bool prerequisiteReady = InstallPrerequisiteReady(previous);
                    if (!replacedPatch && (previous == null
                            ? !dependencyConfirmed
                            : !prerequisiteReady))
                    {
                        item.StatusText = previous != null && (previous.State == DlState.Failed || !string.IsNullOrEmpty(previous.Error))
                            ? "Waiting: previous package failed · retry it first" : "Waiting for previous package installation";
                        return false;
                    }
                    if (!replacedPatch) item.InstallAfterConfirmed = previous != null || dependencyConfirmed;
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
            if (PkgValidator.RequestedKind(previous.Kind) == PkgContentKind.SystemTheme ||
                PkgValidator.RequestedKind(next.Kind) == PkgContentKind.SystemTheme) return false;
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
                string dependencyId;
                bool dependencyConfirmed;
                if (ResolveInstallDependency(item, out dependencyId, out dependencyConfirmed) &&
                    !dependencyConfirmed && !InstallPrerequisiteReady(Find(dependencyId)) &&
                    !CanReplaceFailedPatch(Find(dependencyId), item)) return dependencyId;
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
        // Failed jobs retain their files without holding the queue. An unconfirmed
        // submission keeps its install dependency, but eventually releases the queue.
        // Says whose limit holds a TorBox create back: SSPI keeping to TorBox's hourly limit for
        // new web downloads, or a wait TorBox asked for. Players read the plain "waiting" as a fault.
        static string TorBoxCreateWaitText(Exception cause)
        {
            var limited = cause as DebridResolutionError;
            return limited != null && limited.Diagnostic == TorBoxClient.CreateWaitHourlyLimit
                ? TorBoxClient.CreateWaitHourlyLimit : TorBoxClient.CreateWaitRequested;
        }

        // A paused, canceled, installed or failed background job transfers nothing and holds no
        // installer. Like the general background check below, it must not hold every other
        // download behind "Waiting for the resident worker to release local files".
        internal static bool HoldsResidentForOthers(DlItem item)
        {
            return item.State != DlState.Paused && item.State != DlState.Canceled && item.State != DlState.Installed &&
                item.State != DlState.Failed;
        }

        bool HasBlockingPipelineOwner(out bool residentBusy, out string waitingReason)
        {
            residentBusy = false;
            waitingReason = null;
            bool changed = false;
            long now = TransferClockMs();
            foreach (var item in _items)
            {
                if ((item.State == DlState.Submitted || (item.State == DlState.Failed && item.SubmittedNeedsAttention)) &&
                    !item.InstallConfirmed)
                {
                    if (item.SubmittedWaitStartedMs < 0) item.SubmittedWaitStartedMs = now;
                    if (item.SubmittedNeedsAttention || now - item.SubmittedWaitStartedMs >= SubmittedConfirmationWaitMs)
                    {
                        changed |= !item.SubmittedNeedsAttention;
                        item.SubmittedNeedsAttention = true;
                        item.State = DlState.Failed;
                        item.Error = SubmittedConfirmationTimeoutError;
                        item.StatusText = "Needs attention: " + SubmittedConfirmationTimeoutError;
                    }
                }
                else
                {
                    item.SubmittedWaitStartedMs = -1;
                    item.SubmittedNeedsAttention = false;
                }
                // A removal unconfirmed for a minute waits on its own record or on the user's
                // Remove anyway; it must not hold new downloads behind it.
                if (item.ResidentRemovePending && (item.ResidentRemoveStuck || item.ResidentDetachRequested)) continue;
                if (item.State == DlState.Failed || item.SubmittedNeedsAttention) continue;
                if (item.Background && item.ResidentStaged && item.ResidentAutoInstall)
                { if (HoldsResidentForOthers(item)) residentBusy = true; continue; }
                if ((item.Background && (item.State != DlState.Installed && item.State != DlState.Canceled && item.State != DlState.Paused)) ||
                    item.State == DlState.Installing || (item.State == DlState.Submitted && !item.InstallOrderReady))
                    waitingReason = "Waiting for " + ClipMsg(item.Name ?? item.TitleId ?? "another package", 48) +
                        (item.State == DlState.Submitted ? " installation confirmation" :
                         item.State == DlState.Installing ? " to finish installing" : " background transfer");
            }
            if (changed) SaveManifest();
            return waitingReason != null;
        }

        internal static bool CanPrepareResidentHandoff(DlItem item, bool backgroundSelected)
        {
            return backgroundSelected && item != null && !item.ForceLocalInstall &&
                !item.ResidentRetryPending && string.IsNullOrEmpty(item.ArchiveVolumes) &&
                !string.Equals(item.AccessType, "FanOutSource", StringComparison.OrdinalIgnoreCase);
        }

        bool CanPublishQueuedResident(DlItem item, bool capacityAvailable)
        {
            return capacityAvailable && CanPrepareResidentHandoff(item, BackgroundSelected) &&
                CanInstallWithResidentDependency(ResidentDependencyId(item)) && !HasUnstartedPrerequisite(item);
        }

        internal static bool ResumeQueuedOnStartup(DlItem item, string storedState, bool backgroundSelected)
        {
            if (item == null || item.PauseRequested || item.CancelRequested || item.RemoveRequested ||
                item.ResidentRemovePending) return false;
            bool queued = string.Equals(storedState, "Queued", StringComparison.OrdinalIgnoreCase);
            // Enqueue/Resume persist Queued; an explicit pause persists Paused.
            return (item.ParkedForProvider && queued) || (backgroundSelected &&
                (queued || string.Equals(storedState, "Resolving", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(storedState, "Downloading", StringComparison.OrdinalIgnoreCase)));
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
                RetryConfirmedInstallCleanup();
                // Parked TorBox preparations are polled off-thread; the worker
                // itself never waits on a provider that has not published a file.
                PumpParkedPreparationsSafely();
                string capacityReason = null;
                bool residentCapacity = !BackgroundSelected || ResidentDownloadService.CanAcceptStagedJob(out capacityReason);
                DlItem job = null;
                int attempt = 0;
                string activePath = null;
                lock (_lock)
                {
                    foreach (var pending in _items.ToArray())
                        if (pending.RemoveRequested && (!pending.InstallSubmitted || pending.InstallConfirmed) &&
                            !pending.CancelRequested && pending.BgftTaskId < 0 &&
                            !pending.Background && !_activeIds.Contains(pending.Id) && pending.State != DlState.Installing)
                        {
                            if (!IsTerminal(pending.State) && pending.State != DlState.Submitted)
                                pending.State = DlState.Canceled;
                            string removalError;
                            if (!Remove(pending.Id, out removalError)) pending.StatusText = removalError;
                        }
                    foreach (var pending in _items)
                        if (pending.State == DlState.Queued && !pending.Background && !_activeIds.Contains(pending.Id) &&
                            (pending.PauseRequested || pending.CancelRequested))
                            CommitRequestedStop(pending, pending.AttemptId);
                    bool residentBusy = false;
                    string waitingReason;
                    bool backgroundBusy = HasBlockingPipelineOwner(out residentBusy, out waitingReason);
                    if (backgroundBusy)
                        foreach (var pending in _items)
                            if (pending.State == DlState.Queued && !pending.Background && !pending.ParkedForProvider)
                                pending.StatusText = waitingReason;
                    bool localPending = false;
                    long localNow = DateTime.UtcNow.Ticks;
                    foreach (var pending in _items)
                        if (QueueScheduler.CanHoldQueueForInstall(pending.State == DlState.Queued, pending.Background,
                                pending.ParkedForProvider, pending.ResidentRemovePending, pending.PauseRequested || pending.CancelRequested,
                                pending.RetryAfterUtcTicks, localNow, _activePaths.Contains(NormalizePath(pending.DestPath))) &&
                            File.Exists(pending.DestPath) && InstallDependencyReady(pending)) { localPending = true; break; }
                    for (int index = backgroundBusy || _activeIds.Count >= WorkerCount || TransferClockMs() < _nextJobStartAt
                        ? -1 : _items.Count - 1; index >= 0; index--)
                    {
                        var i = _items[index];
                        // Never claim a BGFT-owned row as a new foreground job.
                        string pathKey = NormalizePath(i.DestPath);
                        bool local = File.Exists(i.DestPath);
                        bool publishOnly = CanPublishQueuedResident(i, residentCapacity);
                        bool dependencyAllowed = publishOnly ? !HasUnstartedPrerequisite(i) : InstallDependencyReady(i);
                        if (BackgroundSelected && CanPrepareResidentHandoff(i, true) && !residentCapacity &&
                            i.State == DlState.Queued && !i.Background)
                        { i.StatusText = capacityReason; continue; }
                        if (i.State == DlState.Queued && !i.Background && !i.ParkedForProvider)
                        {
                            if (!dependencyAllowed) i.StatusText = "Waiting for the required package installation";
                            else if (residentBusy && !publishOnly) i.StatusText = "Waiting for the resident worker to release local files";
                            else if (localPending && !local) i.StatusText = "Waiting for a downloaded package to install";
                        }
                        // One pure policy call, shared with the host regression, so a
                        // parked TorBox preparation is never claimed a second time and
                        // never blocks an independent game behind it. A row waiting for
                        // its resident removal must never restart: claiming clears its stop.
                        if (!i.ResidentRemovePending && QueueScheduler.CanClaim(i.ParkedForProvider, i.State == DlState.Queued, i.Background,
                            backgroundBusy, residentBusy, publishOnly, localPending, local, dependencyAllowed,
                            DateTime.UtcNow.Ticks, i.RetryAfterUtcTicks, TransferClockMs(), _nextJobStartAt,
                            _activeIds.Count, WorkerCount, _activePaths.Contains(pathKey)))
                        {
                            if (CommitRequestedStop(i, i.AttemptId)) continue;
                            job = i;
                            _nextJobStartAt = TransferClockMs() + 3000;
                            _activeIds.Add(i.Id);
                            _activePaths.Add(pathKey);
                            activePath = pathKey;
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
                        // The writer returned without acknowledging a removal. No owner remains,
                        // so publish the stop; the removal sweep deletes the row next pass.
                        if (job.RemoveRequested && job.CancelRequested && !job.Background && job.BgftTaskId < 0 &&
                            (job.State == DlState.Resolving || job.State == DlState.Downloading ||
                             job.State == DlState.Finalizing) && !ResidentDownloadService.HasJob(job.Id))
                        {
                            job.CancelRequested = job.PauseRequested = false;
                            job.BytesPerSec = job.EtaSeconds = 0;
                            job.State = DlState.Canceled;
                            job.StatusText = "Canceled";
                        }
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

        // Local archive rows have no package identity until extraction. Their title
        // ID is taken from the archive or folder names so the cover and backdrop can
        // be shown while the archive is still waiting.
        public void AssignLocalArchiveTitles(IList<string> paths)
        {
            if (paths == null || paths.Count == 0) return;
            bool changed = false;
            lock (_lock)
            {
                foreach (DlItem item in _items)
                {
                    if (!item.LocalSource || item.Kind != "archive" || !string.IsNullOrEmpty(item.TitleId)) continue;
                    foreach (string path in paths)
                    {
                        if (!SamePath(item.DestPath, path)) continue;
                        string titleId = LocalInstallSource.TitleIdFromNames(path);
                        if (titleId.Length > 0) { item.TitleId = titleId; changed = true; }
                        break;
                    }
                }
            }
            if (changed) SaveManifest();
        }
    }
}
