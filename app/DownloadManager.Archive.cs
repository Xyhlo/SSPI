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
        sealed class PendingLocalChild
        {
            public DlItem Item;
            public string ExtractedPath;
            public int Ordinal;
            public int Priority;
        }

        static void ApplySourceArchivePassword(DlItem job, byte[] header)
        {
            ApplyContainerFormatHint(job, header, null, null, null);
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

        internal static string ContainerFormatFromFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string name = value.Trim().Trim('"', '\'');
            Uri uri;
            if (Uri.TryCreate(name, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile))
                name = uri.AbsolutePath;
            int query = name.IndexOfAny(new[] { '?', '#' });
            if (query >= 0) name = name.Substring(0, query);
            int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            if (slash >= 0) name = name.Substring(slash + 1);
            try { name = Uri.UnescapeDataString(name); } catch { }
            string lower = name.ToLowerInvariant();
            if (lower.EndsWith(".rar", StringComparison.Ordinal) ||
                (lower.Length >= 4 && lower[lower.Length - 4] == '.' && lower[lower.Length - 3] == 'r' &&
                    char.IsDigit(lower[lower.Length - 2]) && char.IsDigit(lower[lower.Length - 1]))) return "rar";
            if (lower.EndsWith(".zip", StringComparison.Ordinal)) return "zip";
            if (lower.EndsWith(".7z", StringComparison.Ordinal)) return "7z";
            if (lower.EndsWith(".pkg", StringComparison.Ordinal)) return "pkg";
            return "";
        }

        static string ContainerFormatFromContentDisposition(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string ordinary = "";
            foreach (string raw in value.Split(';'))
            {
                int equals = raw.IndexOf('=');
                if (equals <= 0) continue;
                string key = raw.Substring(0, equals).Trim();
                if (!key.Equals("filename*", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("filename", StringComparison.OrdinalIgnoreCase)) continue;
                string filename = raw.Substring(equals + 1).Trim().Trim('"', '\'');
                if (key.Equals("filename*", StringComparison.OrdinalIgnoreCase))
                {
                    int first = filename.IndexOf('\'');
                    int second = first < 0 ? -1 : filename.IndexOf('\'', first + 1);
                    if (second >= 0) filename = filename.Substring(second + 1);
                    try { filename = Uri.UnescapeDataString(filename); } catch { }
                    string format = ContainerFormatFromFileName(filename);
                    if (!string.IsNullOrEmpty(format)) return format;
                }
                else ordinary = filename;
            }
            return ContainerFormatFromFileName(ordinary);
        }

        static string ExistingContainerFormat(DlItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.DestPath)) return "";
            try
            {
                string path = File.Exists(item.DestPath) ? item.DestPath : item.DestPath + ".part";
                if (!File.Exists(path)) return "";
                byte[] header = new byte[8];
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int read = file.Read(header, 0, header.Length);
                    Array.Resize(ref header, read);
                }
                return ContainerFormatFromHeader(header);
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        internal static void ApplyContainerFormatHint(DlItem item, byte[] header,
            string providerFileName, string contentDisposition, string effectiveUrl)
        {
            if (item == null) return;
            string detected = ContainerFormatFromHeader(header);
            if (!string.IsNullOrEmpty(detected)) { item.ContainerFormat = detected; return; }
            if (!string.IsNullOrEmpty(item.ArchiveVolumes)) { item.ContainerFormat = "rar"; return; }
            string[] hints = {
                ContainerFormatFromFileName(providerFileName),
                ContainerFormatFromContentDisposition(contentDisposition),
                ContainerFormatFromFileName(effectiveUrl),
                ContainerFormatFromFileName(item.HosterUrl),
                ContainerFormatFromFileName(item.Label),
                item.ContainerFormat
            };
            foreach (string hint in hints)
                if (hint == "pkg" || hint == "rar" || hint == "zip" || hint == "7z")
                { item.ContainerFormat = hint; return; }
        }

        static void SetArchiveVolumesFormat(DlItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.ArchiveVolumes)) return;
            string detected = ExistingContainerFormat(item);
            item.ContainerFormat = string.IsNullOrEmpty(detected) ? "rar" : detected;
        }

        internal static string IncomingContainerFileName(DlItem item, string name)
        {
            if (item == null) return name ?? "";
            if (string.IsNullOrEmpty(name)) name = item.Name ?? item.TitleId ?? "download";
            bool generatedName = !string.IsNullOrEmpty(item.DestPath) &&
                string.Equals(Path.GetFileName(name), Path.GetFileName(item.DestPath), StringComparison.OrdinalIgnoreCase);
            string format = item.ContainerFormat ?? "";
            if (format != "pkg" && format != "rar" && format != "zip" && format != "7z")
            {
                if (!string.IsNullOrEmpty(item.ArchiveVolumes)) format = "rar";
                else
                {
                    if (!generatedName) format = ContainerFormatFromFileName(name);
                    if (string.IsNullOrEmpty(format)) format = ContainerFormatFromFileName(item.HosterUrl);
                    if (string.IsNullOrEmpty(format)) format = ContainerFormatFromFileName(item.Label);
                }
            }
            string extension = Path.GetExtension(name).ToLowerInvariant();
            bool rarVolume = extension.Length == 4 && extension[1] == 'r' &&
                char.IsDigit(extension[2]) && char.IsDigit(extension[3]);
            if (format == "rar" && (extension == ".rar" || rarVolume)) return name;
            if (format == "rar" || format == "zip" || format == "7z" || format == "pkg")
                return Path.ChangeExtension(name, "." + format);
            if (generatedName && extension == ".pkg")
                return string.IsNullOrEmpty(item.Name) ? Path.GetFileNameWithoutExtension(name) : item.Name;
            return name;
        }

        internal static void RestoreContainerFormat(DlItem item)
        {
            if (item == null) return;
            string detected = ExistingContainerFormat(item);
            if (!string.IsNullOrEmpty(detected)) { item.ContainerFormat = detected; return; }
            if (!string.IsNullOrEmpty(item.ArchiveVolumes)) { item.ContainerFormat = "rar"; return; }
            if (!string.IsNullOrEmpty(item.ContainerFormat)) return;
            ApplyContainerFormatHint(item, null, null, null, null);
        }

        internal static string ResidentArchivePasswordError(byte[] header, string password)
        {
            // Only RAR uses a password. Sources give every link on a page the same
            // passwords, so one on a ZIP or 7z row is no sign of encryption; the
            // extractors report real encryption themselves and keep the archive.
            if (PackageArchive.IsSevenZipHeader(header))
                return ResidentDownloadService.SupportsSevenZipArchive ? null
                    : "Restart the PS4 and enable GoldHEN to load this build's 7z extractor; files are kept.";
            bool rar = header != null && header.Length >= 4 && header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 && header[3] == 0x21;
            if (!rar || string.IsNullOrEmpty(password)) return null;
            return ResidentDownloadService.ValidArchivePassword(password) ? null
                : "Archive password exceeds 256 UTF-8 bytes or is invalid";
        }

        internal static bool HasRetainedArchiveVolumes(DlItem job)
        {
            if (job == null || string.IsNullOrEmpty(job.ArchiveVolumes)) return false;
            var paths = ArchivePaths(job);
            foreach (string path in paths) if (!File.Exists(path)) return false;
            // The download/extraction path still verifies every input. This only
            // skips cloud preparation when a password retry needs no more bytes.
            return paths.Count > 0;
        }

        // A complete local volume set needs no provider link. Verify each local
        // input before archive dispatch; the decoder then checks member CRCs.
        internal static bool CheckRetainedArchiveVolumes(DlItem job, out string error)
        {
            error = null;
            if (!HasRetainedArchiveVolumes(job)) return false;
            var volumes = ArchiveVolumeSet.Decode(job.ArchiveVolumes);
            var paths = ArchivePaths(job);
            if (volumes.Count != paths.Count) { error = "Retained archive volume list is inconsistent"; return true; }
            for (int i = 0; i < paths.Count; i++)
            {
                var probe = new DlItem { DestPath = paths[i], ExpectedByteSize = volumes[i].Size,
                    ExpectedSha256 = volumes[i].Sha256 };
                string detail;
                if (!VerifyCandidateFile(probe, out detail))
                { error = "Volume " + (i + 1) + ": " + detail; return true; }
            }
            PackageObjectKind first = PackageArchive.Detect(paths[0]);
            if (first != PackageObjectKind.Rar4 && first != PackageObjectKind.Rar5 &&
                !(volumes.Count == 1 && (first == PackageObjectKind.Pkg || first == PackageObjectKind.Zip ||
                    first == PackageObjectKind.SevenZip)))
                error = "Retained archive has an unsupported first-volume header";
            return true;
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
            lock (_lock) foreach (var other in _items)
                if (other != job && other.Background && other.State != DlState.Failed && !other.SubmittedNeedsAttention &&
                    HoldsResidentForOthers(other) &&
                    !(other.ResidentRemovePending && (other.ResidentRemoveStuck || other.ResidentDetachRequested))) return false;
            if (_cfg == null || !_cfg.UseBgftDirect || !ResidentDownloadService.HasDownloader) return false;
            if (ResidentDownloadService.SingletonHeldByOther(job.Id)) return false;
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
            {
                string selectedProvider;
                HashSet<string> volumeRejected;
                GetArchiveProviderState(job, volume.Url, out selectedProvider, out volumeRejected);
                if (unavailable != null) foreach (string id in unavailable) volumeRejected.Add(id);
                // A refusal with no other service supporting this part would leave nothing to ask.
                if (volumeRejected.Count > 0)
                {
                    DebridHostSupport.RefreshEnabled(_cfg);
                    volumeRejected.RemoveWhere(id => !UnlockProviders.HasSupportedAlternative(_cfg, new[] { volume.Url }, id));
                }
                return ResolveArchiveProviderSlot(() => UnlockProviders.Unrestrict(_cfg, volume.Url,
                    text => { lock (_lock) { if (!canceled()) job.StatusText = "RAR part " + (index + 1) + "/" + count + " · " + text; } },
                    canceled, id =>
                    {
                        lock (_lock)
                        {
                            if (canceled()) throw new OperationCanceledException();
                            SetArchiveProviderState(job, volume.Url, id);
                            job.ResolvedProviderId = id;
                            job.ProviderTransientRetries = 0;
                        }
                        if (!SaveManifest()) throw new IOException("Could not save the selected provider for archive part " + (index + 1));
                    },
                    selectedProvider, volumeRejected, true, !string.IsNullOrEmpty(selectedProvider),
                    rejected =>
                    {
                        // Kept only to steer this part to another service; with none, the
                        // refusal would block the only one.
                        if (!UnlockProviders.HasSupportedAlternative(_cfg, new[] { volume.Url }, rejected)) return;
                        lock (_lock)
                        {
                            if (canceled()) throw new OperationCanceledException();
                            SetArchiveProviderState(job, volume.Url, "", rejected, true);
                        }
                        if (!SaveManifest()) throw new IOException("Could not save the archive provider rejection");
                    }), canceled);
            }
            return FreeHosterClient.ResolveDirect(volume.Url,
                string.Equals(volume.AccessType, "HosterLanding", StringComparison.OrdinalIgnoreCase));
        }

        internal static string ResolveArchiveProviderSlot(Func<string> resolve, Func<bool> canceled)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            for (;;)
            {
                if (canceled != null && canceled()) throw new OperationCanceledException();
                try { return resolve(); }
                catch (ProviderPreparationWaitException wait)
                {
                    // Adjacent ready archive parts can hit our one-second request
                    // spacing. Parking here restarts preflight at part 1 forever.
                    // No provider request was sent for this local admission wait.
                    if (wait.State != "provider request slot" || wait.RetryAfterSeconds != 1 ||
                        elapsed.ElapsedMilliseconds >= 2000) throw;
                    for (int n = 0; n < 10; n++)
                    {
                        if (canceled != null && canceled()) throw new OperationCanceledException();
                        Thread.Sleep(100);
                    }
                }
            }
        }

        bool CanRefreshArchiveVolume(DlItem job, ArchiveVolume volume)
        {
            if (job == null || volume == null || IsDirectAccess(volume.AccessType) ||
                _cfg == null || !_cfg.UseUnlockProvider) return false;
            string provider;
            HashSet<string> ignored;
            GetArchiveProviderState(job, volume.Url, out provider, out ignored);
            return UnlockProviders.IsEnabled(_cfg, provider);
        }

        string RenewArchiveVolumeLink(ArchiveVolume volume, DlItem job, int attempt, int index, int count, Func<bool> cancel)
        {
            string provider;
            HashSet<string> ignored;
            GetArchiveProviderState(job, volume.Url, out provider, out ignored);
            if (string.IsNullOrEmpty(provider))
                throw new InvalidOperationException("The original provider for archive part " + (index + 1) +
                    " is unknown; refusing to mix providers during link renewal");
            string fresh = UnlockProviders.RefreshSameProvider(_cfg, volume.Url, provider,
                text => { lock (_lock) { if (!cancel()) job.StatusText = "RAR part " + (index + 1) + "/" + count + " · " + text; } },
                cancel);
            if (cancel()) throw new OperationCanceledException();
            if (!SaveManifest()) throw new IOException("Could not save the renewed provider lease for archive part " + (index + 1));
            return fresh;
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
                    if (!File.Exists(paths[i]))
                        missing = checked(missing + Math.Max(0, volumes[i].Size - RetainedTransferBytes(paths[i])));
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
                    string archiveProvider;
                    HashSet<string> archiveRejected;
                    GetArchiveProviderState(job, volume.Url, out archiveProvider, out archiveRejected);
                    bool renewThrottledUrl = job.ArchiveRetryVolume == i && (job.HttpRetryRenewPending ||
                        (job.TorBoxThrottleRenewPending && string.Equals(archiveProvider, UnlockProviders.TorBoxId, StringComparison.OrdinalIgnoreCase)));
                    bool reuseThrottledUrl = job.ArchiveRetryVolume == i && !renewThrottledUrl &&
                        !string.IsNullOrEmpty(job.ArchiveRetryUrl);
                    string direct;
                    if (renewThrottledUrl)
                    {
                        direct = RenewArchiveVolumeLink(volume, job, attempt, i, volumes.Count,
                            () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested);
                        if (string.IsNullOrWhiteSpace(direct)) throw new IOException("Link service returned an empty archive link");
                        job.HttpRetryRenewPending = false;
                        job.TorBoxThrottleRenewPending = false;
                        job.TorBoxThrottleNoProgress = 0;
                        if (job.TorBoxThrottleRenewalsWithoutProgress < int.MaxValue)
                            job.TorBoxThrottleRenewalsWithoutProgress++;
                        job.ArchiveRetryUrl = direct;
                        if (!SaveManifest()) throw new IOException("Could not save the refreshed archive link state");
                    }
                    else direct = reuseThrottledUrl
                        ? job.ArchiveRetryUrl
                        : ResolveVolume(volume, job, attempt, i, volumes.Count, unavailable);
                    if (!reuseThrottledUrl && !renewThrottledUrl)
                    {
                        job.ArchiveRetryVolume = -1;
                        job.ArchiveRetryUrl = "";
                        job.TorBoxThrottleRenewPending = false;
                    }
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
                    Func<bool> canceled = () => job.AttemptId != attempt || job.CancelRequested || job.PauseRequested;
                    string lastAttemptUrl = direct;
                    try
                    {
                        DownloadLinkRecovery.Run(direct,
                            (url, durable) =>
                            {
                                lastAttemptUrl = url;
                                // A volume is an archive part, not a PKG: it has no package ID,
                                // and the engine refuses any ID longer than 48 characters.
                                return NetHttp.DownloadFileResumable(url, path, durable,
                                    null, canceled, 60000, null, null, null, volume.Sha256,
                                    (done, total, received) =>
                                    {
                                        lock (_lock)
                                        {
                                            if (job.AttemptId != attempt || job.PauseRequested || job.CancelRequested) return;
                                            UpdateLiveTransferStats(job, checked(baseBytes + done),
                                                ArchiveTransferTotal(volumes, baseBytes, i, total), received, statsPhase, TransferClockMs());
                                            job.StatusText = "RAR volume " + (i + 1) + "/" + volumes.Count + " · Keep SSPI open";
                                        }
                                    }, null, text => ApplyTransferActivity(job, attempt,
                                        "RAR volume " + (i + 1) + "/" + volumes.Count + " · " + text));
                            },
                            () => TransferClient.DurableBytes(path),
                            () => CanRefreshArchiveVolume(job, volume),
                            () => RenewArchiveVolumeLink(volume, job, attempt, i, volumes.Count, canceled),
                            canceled,
                            countRenewal => { lock (_lock) { if (job.AttemptId == attempt) job.StatusText = "Refreshing rejected RAR volume link " + countRenewal; } });
                    }
                    catch (Exception ex)
                    {
                        long retryAt;
                        if (DownloadHttpException.TryGetRetry(ex, job.TransientHttpRetries, DateTime.UtcNow, out retryAt))
                        {
                            job.ArchiveRetryVolume = i;
                            job.ArchiveRetryUrl = lastAttemptUrl;
                        }
                        throw;
                    }
                    job.ArchiveRetryVolume = -1;
                    job.ArchiveRetryUrl = "";
                    job.TransientHttpRetries = 0;
                    job.HttpRetryRenewPending = false;
                    job.TorBoxThrottleNoProgress = 0;
                    job.TorBoxThrottleRenewalsWithoutProgress = 0;
                    job.TorBoxThrottleRenewPending = false;
                    job.TorBoxThrottleDurableBytes = 0;
                }
                var probe = new DlItem { DestPath = path, ExpectedSha256 = volume.Sha256,
                    ExpectedByteSize = volume.Size };
                string error;
                if (!VerifyCandidateFile(probe,
                    text => { lock (_lock) { if (job.AttemptId == attempt) job.StatusText = text; } },
                    () => !_run || job.AttemptId != attempt || job.CancelRequested || job.PauseRequested || job.RemoveRequested,
                    out error)) throw new InvalidDataException("Volume " + (i + 1) + ": " + error);
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

        static void AssignArchiveInstallDependencies(List<PendingLocalChild> pending)
        {
            // Bases and updates chain per title: in an archive holding several games, one
            // game's unconfirmed or removed base must not hold another game's packages.
            var lastByTitle = new Dictionary<string, DlItem>(StringComparer.OrdinalIgnoreCase);
            DlItem lastBaseOrPatch = null;
            foreach (PendingLocalChild child in pending)
            {
                child.Item.InstallAfterId = "";
                child.Item.InstallAfterConfirmed = false;
                string title = child.Item.TitleId ?? "";
                DlItem sameTitle;
                lastByTitle.TryGetValue(title, out sameTitle);
                if (child.Priority <= 1)
                {
                    if (sameTitle != null) child.Item.InstallAfterId = sameTitle.Id;
                    lastByTitle[title] = child.Item;
                    lastBaseOrPatch = child.Item;
                    continue;
                }

                if (string.Equals(child.Item.Kind, "dlc", StringComparison.OrdinalIgnoreCase))
                {
                    // A DLC follows its own game; with none in the archive (a sister
                    // edition's DLC) it follows the archive's latest base or update.
                    DlItem after = sameTitle ?? lastBaseOrPatch;
                    if (after != null) child.Item.InstallAfterId = after.Id;
                    continue;
                }

                if (!string.Equals(child.Item.Kind, "theme", StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrEmpty(child.Item.ExpectedContentId)) continue;
                foreach (PendingLocalChild candidate in pending)
                {
                    if (string.Equals(candidate.Item.Kind, "theme-license", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(child.Item.ExpectedContentId, candidate.Item.ExpectedContentId, StringComparison.Ordinal))
                    {
                        child.Item.InstallAfterId = candidate.Item.Id;
                        break;
                    }
                }
            }
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
                    // A local archive's title ID is read from its file or folder names for
                    // artwork only. The extracted PKG's own identity decides its title.
                    var probe = new DlItem { DestPath = entry.ExtractedPath, TitleId = job.LocalSource ? "" : job.TitleId };
                    PkgContentKind entryKind; string entryDetail;
                    if (PkgValidator.TryGetContentKind(entry.ExtractedPath, out entryKind, out entryDetail) && entryKind == PkgContentKind.SystemTheme)
                        probe.TitleId = null;
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
                ClassifyThemeLicenseCompanions(pending);
                if (!string.IsNullOrWhiteSpace(job.ExpectedContentId))
                {
                    if (pending.Count != 1 && !(pending.Count == 2 && pending.Exists(p => p.Item.Kind == "theme-license") && pending.Exists(p => p.Item.Kind == "theme")))
                        throw new InvalidDataException("Archive content ID promise is ambiguous");
                    if (pending.Exists(p => !string.Equals(job.ExpectedContentId.Trim(),
                        p.Item.ExpectedContentId, StringComparison.OrdinalIgnoreCase)))
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

                AssignArchiveInstallDependencies(pending);

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
            bool cleaned = true;
            if (!parent.LocalSource)
                foreach (string path in ArchivePaths(parent))
                    if (!DeleteDownloadFiles(path)) cleaned = false;
            lock (_lock)
            {
                if (cleaned) _items.Remove(parent);
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
                    if (!DeleteDownloadFiles(paths[i])) return false;
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
                // TryCleanupOwnedFiles scans every other row's paths. Hold the queue lock,
                // as its other callers do, so an enqueue or removal cannot change _items
                // during the scan; a row already removed needs no cleanup here.
                lock (_lock)
                {
                    if (!object.ReferenceEquals(Find(source.Id), source)) continue;
                    string cleanupError;
                    if (TryCleanupOwnedFiles(source, out cleanupError))
                    {
                        _items.Remove(source);
                        changed = true;
                    }
                    else source.StatusText = cleanupError;
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
            RemoveOrphanTransferLocks();
            if (changed) SaveManifest();
        }

        static void ClassifyThemeLicenseCompanions(List<PendingLocalChild> pending)
        {
            foreach (var license in pending)
            {
                if (license.Item.Kind != "dlc" || !PkgIntegrity.IsNoDataLicense(license.ExtractedPath)) continue;
                foreach (var theme in pending)
                {
                    if (theme.Item.Kind != "theme" || string.IsNullOrEmpty(theme.Item.ExpectedContentId) ||
                        !string.Equals(theme.Item.ExpectedContentId, license.Item.ExpectedContentId, StringComparison.Ordinal)) continue;
                    license.Item.Kind = "theme-license";
                    license.Priority = 2;
                    theme.Priority = 3;
                    break;
                }
            }
        }
    }
}
