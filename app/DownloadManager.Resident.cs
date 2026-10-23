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
        // The background service runs inside the PS4's shell process, where opening a USB
        // drive can keep failing while SSPI itself reads it (storage check -5, reported
        // 3 Oct on firmware 12.52). Downloads staged on such a drive then run in SSPI
        // instead of waiting for the service. Choosing a staging location clears the note.
        // A note is recorded only after the failure lasts a minute (a worker started right
        // after a PS4 restart can fail briefly), holds only for the worker instance that
        // reported it, and is cleared as soon as a background job writes to that drive.
        static readonly object BlockedUsbGate = new object();
        static Dictionary<string, string> _backgroundBlockedUsb; // mount -> reporting worker epoch
        static readonly Dictionary<string, long[]> _usbOpenFailing = new Dictionary<string, long[]>(StringComparer.Ordinal);
        static readonly long UsbOpenFailureConfirmTicks = TimeSpan.TicksPerMinute;
        static readonly long UsbOpenFailureGapTicks = 30 * TimeSpan.TicksPerSecond;
        static string BlockedUsbPath { get { return Path.Combine(AppSettings.DataDir, "usb-background-blocked"); } }

        static string UsbMountOf(string path)
        {
            string normalized = (path ?? "").Replace('\\', '/');
            return normalized.Length >= 9 && normalized.StartsWith("/mnt/usb", StringComparison.Ordinal) &&
                normalized[8] >= '0' && normalized[8] <= '7' && (normalized.Length == 9 || normalized[9] == '/')
                ? normalized.Substring(0, 9) : null;
        }

        // Lines are "mount<TAB>worker epoch". A bare mount (5.11.7 to 5.11.11) has no epoch,
        // so the drive gets one more try with the current worker.
        static Dictionary<string, string> BlockedUsb()
        {
            if (_backgroundBlockedUsb != null) return _backgroundBlockedUsb;
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (File.Exists(BlockedUsbPath) && new FileInfo(BlockedUsbPath).Length < 1024)
                    foreach (string line in File.ReadAllLines(BlockedUsbPath))
                    {
                        string[] fields = line.Trim().Split('\t');
                        string mount = UsbMountOf(fields[0].Trim());
                        if (mount != null) result[mount] = fields.Length > 1 ? fields[1].Trim() : "";
                    }
            }
            catch { }
            return _backgroundBlockedUsb = result;
        }

        static void SaveBlockedUsb(Dictionary<string, string> blocked)
        {
            try
            {
                if (blocked.Count == 0) { if (File.Exists(BlockedUsbPath)) File.Delete(BlockedUsbPath); return; }
                var lines = new List<string>();
                foreach (var entry in blocked) lines.Add(entry.Key + "\t" + entry.Value);
                File.WriteAllText(BlockedUsbPath, string.Join("\n", lines) + "\n");
            }
            catch { }
        }

        /// <summary>True when downloads staged at this path must not wait for the background service.</summary>
        internal static bool BackgroundCannotOpen(string path)
        {
            string mount = UsbMountOf(path);
            if (mount == null) return false;
            string recorded;
            lock (BlockedUsbGate) if (!BlockedUsb().TryGetValue(mount, out recorded)) return false;
            // Without a running worker nothing can be handed off anyway, so keep the note.
            string epoch = ResidentDownloadService.CurrentWorkerEpoch();
            return epoch == null || string.Equals(recorded, epoch, StringComparison.Ordinal);
        }

        /// <summary>Records a drive the service reported it could not open for a minute. Only a
        /// drive SSPI itself can open is recorded; a disconnected drive is a different problem.</summary>
        internal static bool NoteBackgroundCannotOpen(string path)
        {
            string mount = UsbMountOf(path);
            if (mount == null || !UsbVolumeLabel.IsConnected(mount)) return false;
            string epoch = ResidentDownloadService.CurrentWorkerEpoch() ?? "";
            long now = DateTime.UtcNow.Ticks;
            lock (BlockedUsbGate)
            {
                long[] seen;
                if (!_usbOpenFailing.TryGetValue(mount, out seen) || now - seen[1] > UsbOpenFailureGapTicks || now < seen[1])
                    _usbOpenFailing[mount] = seen = new[] { now, now };
                seen[1] = now;
                if (now - seen[0] < UsbOpenFailureConfirmTicks) return false;
                var blocked = BlockedUsb();
                string recorded;
                if (blocked.TryGetValue(mount, out recorded) && string.Equals(recorded, epoch, StringComparison.Ordinal)) return true;
                blocked[mount] = epoch;
                SaveBlockedUsb(blocked);
            }
            SspiLog.Write("download", "usb-storage background service cannot open " + mount + " worker_epoch=" +
                (epoch.Length == 0 ? "unknown" : epoch) + "; downloads staged there run in SSPI");
            return true;
        }

        /// <summary>A background job is writing to this drive, so the service can open it.</summary>
        internal static void NoteBackgroundOpened(string path)
        {
            string mount = UsbMountOf(path);
            if (mount == null) return;
            bool cleared;
            lock (BlockedUsbGate)
            {
                _usbOpenFailing.Remove(mount);
                var blocked = BlockedUsb();
                cleared = blocked.Remove(mount);
                if (cleared) SaveBlockedUsb(blocked);
            }
            if (cleared) SspiLog.Write("download", "usb-storage background service opened " + mount + "; background downloads use it again");
        }

        internal static void ClearBackgroundUsbBlocks()
        {
            lock (BlockedUsbGate)
            {
                _backgroundBlockedUsb = new Dictionary<string, string>(StringComparer.Ordinal);
                _usbOpenFailing.Clear();
                try { if (File.Exists(BlockedUsbPath)) File.Delete(BlockedUsbPath); } catch { }
            }
        }

        // The worker's storage-wait detail for check -5 from the shell process.
        internal static bool ResidentCannotOpenUsb(string error)
        {
            return error != null && error.IndexOf("storage check -5", StringComparison.Ordinal) >= 0 &&
                error.IndexOf("background service", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // A USB drive that is not connected holds nothing SSPI can reach, so removing a
        // row never waits for it; anything left there shows in Stored files once it is back.
        static bool OnDisconnectedUsb(string path)
        {
            string normalized = (path ?? "").Replace('\\', '/');
            return normalized.Length >= 9 && normalized.StartsWith("/mnt/usb", StringComparison.Ordinal) &&
                !UsbVolumeLabel.IsConnected(normalized.Substring(0, 9));
        }

        static bool TryDeleteResidentExtractionDirectory(DlItem item)
        {
            // Queue IDs contain the title, package kind and timestamp. Accept the
            // same filename-safe IDs as resident publication, not only GUIDs.
            if (item == null || !System.Text.RegularExpressions.Regex.IsMatch(item.Id ?? "", @"\A[A-Za-z0-9_-]{1,190}\z"))
                return false;
            string root = ResidentExtractionRoot(item);
            if (root == null) return false;
            string directory = Path.Combine(root, "extract-" + item.Id);
            try
            {
                if (root.Replace('\\', '/').StartsWith("/mnt/usb", StringComparison.Ordinal))
                    AppSettings.RequireStaging(root);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            if (!TryDeleteKnownResidentDirectory(directory, item.InstallConfirmed)) return false;
            if (item.LocalSource || !string.IsNullOrEmpty(item.ResidentGeneration)) return true;
            // Older archive jobs extracted beside the resident IPC journal.
            // Remove only the exact job directory after its native owner is gone.
            string legacy = Path.Combine(AppSettings.DataDir, "resident", "archives", item.Id);
            if (!TryDeleteKnownResidentDirectory(legacy, item.InstallConfirmed)) return false;
            string shared = Path.Combine("/user/data/SSPI/resident", "archives", item.Id);
            return SamePath(legacy, shared) || TryDeleteKnownResidentDirectory(shared, item.InstallConfirmed);
        }

        static bool TryDeleteKnownResidentDirectory(string directory, bool installConfirmed)
        {
            try
            {
                if (!Directory.Exists(directory)) return true;
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
                // An interrupted nested-archive extraction leaves the inner archive in
                // <directory>/nested. Only that exact, plain folder of plain files is
                // owned here; any other folder, a link or a deeper level keeps it all.
                string nested = null;
                string[] nestedFiles = new string[0];
                foreach (string subdirectory in Directory.GetDirectories(directory))
                {
                    if (nested != null || !string.Equals(Path.GetFileName(subdirectory), "nested", StringComparison.Ordinal) ||
                        (File.GetAttributes(subdirectory) & FileAttributes.ReparsePoint) != 0 ||
                        Directory.GetDirectories(subdirectory).Length != 0) return false;
                    nested = subdirectory;
                    nestedFiles = Directory.GetFiles(subdirectory);
                    foreach (string inner in nestedFiles)
                        if ((File.GetAttributes(inner) & FileAttributes.ReparsePoint) != 0) return false;
                }
                string[] files = Directory.GetFiles(directory);
                foreach (string file in files)
                {
                    if ((File.GetAttributes(file) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return false;
                    string leaf = Path.GetFileName(file);
                    // The resident also keeps BGFT markers beside an extracted package
                    // (.bgft-debug, .bgft-transfer-size) and a no-data license record
                    // beside its journal (.license); write_atomic stages each as .tmp.
                    bool known = System.Text.RegularExpressions.Regex.IsMatch(leaf,
                        @"^(?:packages|complete|extraction-in-progress)\.txt(?:\.tmp)?$|^pkg-\d{3}\.pkg(?:\.part|\.bgft-debug(?:\.tmp)?|\.bgft-transfer-size(?:\.tmp)?)?$|^install-\d{3}\.txt(?:\.addon|\.license)?(?:\.tmp)?$",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (!known) return false;
                    // The resident worker rewrites a canceled BGFT journal to
                    // this stopped record only after unregistering its task.
                    // Pending AppInstUtil submissions and incomplete writes
                    // provide no equivalent proof that their input is free.
                    if (!installConfirmed && leaf.StartsWith("install-", StringComparison.OrdinalIgnoreCase))
                    {
                        if (leaf.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) return false;
                        string journal = file;
                        foreach (string record in new[] { ".addon", ".license" })
                            if (leaf.EndsWith(record, StringComparison.OrdinalIgnoreCase))
                                journal = file.Substring(0, file.Length - record.Length);
                        if (!IsStoppedResidentInstallJournal(journal)) return false;
                    }
                }
                foreach (string inner in nestedFiles) File.Delete(inner);
                if (nested != null) Directory.Delete(nested, false);
                foreach (string file in files) File.Delete(file);
                Directory.Delete(directory, false);
                return !Directory.Exists(directory);
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        static bool IsStoppedResidentInstallJournal(string path)
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
                    new FileInfo(path).Length > 128) return false;
                // A canceled task leaves "<content> -1 0 0 6". A registration the PS4
                // rejected without creating a task appends the package kind (6-9).
                return System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(path).Trim(),
                    @"^[A-Za-z0-9_-]{36} -1 0 0 6(?: [6-9])?$");
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>True when a resident extraction directory of this row keeps an install
        /// journal that is not stopped (an add-on AppInstUtil accepted, or a finished child).</summary>
        static bool HasUnstoppedResidentJournal(DlItem item)
        {
            if (item == null || !item.ResidentArchive || item.InstallConfirmed ||
                !System.Text.RegularExpressions.Regex.IsMatch(item.Id ?? "", @"\A[A-Za-z0-9_-]{1,190}\z")) return false;
            var directories = new List<string>();
            string root = ResidentExtractionRoot(item);
            if (root != null) directories.Add(Path.Combine(root, "extract-" + item.Id));
            directories.Add(Path.Combine(AppSettings.DataDir, "resident", "archives", item.Id));
            directories.Add(Path.Combine("/user/data/SSPI/resident", "archives", item.Id));
            foreach (string directory in directories)
                try
                {
                    if (!Directory.Exists(directory)) continue;
                    foreach (string journal in Directory.GetFiles(directory, "install-*.txt"))
                        if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(journal), @"\Ainstall-\d{3}\.txt\z") &&
                            !IsStoppedResidentInstallJournal(journal)) return true;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            return false;
        }

        // Remove anyway: the worker released the job without stopping its PS4 task, or applies the
        // durable request when it next runs. That task may still read the downloaded files, so all
        // of them are kept for Stored files. Caller holds _lock.
        void FinishDetachedRemoval(DlItem item, bool acknowledged, string worker)
        {
            PreserveConfirmedDependency(item);
            _items.Remove(item);
            SaveManifest();
            SspiLog.Write("download", "event=remove-detached job=" + item.Id + " generation=" +
                (string.IsNullOrEmpty(item.ResidentGeneration) ? "none" : item.ResidentGeneration) +
                " acknowledged=" + (acknowledged ? "1" : "0") + " worker=" + worker + " files=retained");
        }

        internal static string ResidentExtractionRoot(DlItem item)
        {
            if (item == null) return null;
            string root;
            if (item.LocalSource)
            {
                string source;
                if (!LocalInstallSource.TryNormalizePath(item.DestPath, out source)) return null;
                // FTP inbox sources extract into internal staging, not a USB drive.
                root = LocalInstallSource.StagingRootFor(source);
            }
            else root = Path.GetDirectoryName(NormalizePath(item.DestPath));
            foreach (string candidate in StorageRoots())
                if (SamePath(root, candidate)) return root;
            if (!item.LocalSource && (SamePath(root, "/data/SSPI/downloads") ||
                SamePath(root, "/user/data/SSPI/downloads"))) return root;
            return null;
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

        internal static bool UseInAppThemeLicense(DlItem item)
        { return item != null && string.Equals(item.Kind, "theme-license", StringComparison.Ordinal); }

        internal static string SelectedModeWaitingReason(bool backgroundSelected, bool workerReady, string reason)
        {
            if (!backgroundSelected || (workerReady && string.IsNullOrEmpty(reason))) return null;
            return "Background mode waiting: " + (string.IsNullOrWhiteSpace(reason) ? "resident worker is not ready" : DescribeWaitingReason(reason));
        }

        // NativeHttp reports "sceHttp send 0x8095F00C host=... ssl=0x... verify=0x20 errno=0x...".
        // The card names the problem and keeps the codes; resident.log keeps the whole line.
        static readonly System.Text.RegularExpressions.Regex CertificateWait = new System.Text.RegularExpressions.Regex(
            @"\AsceHttp (?:send|read) 0x(?<rc>[0-9A-Fa-f]{1,8}) host=(?<host>[A-Za-z0-9.-]+) ssl=0x[0-9A-Fa-f]{1,8} verify=0x(?<verify>[0-9A-Fa-f]{1,8})",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        internal static string DescribeWaitingReason(string reason)
        {
            var match = CertificateWait.Match(reason ?? "");
            uint verify;
            if (!match.Success || !uint.TryParse(match.Groups["verify"].Value, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out verify) || verify == 0) return reason;
            // sceHttps verify flags: 0x04 host name, 0x08/0x10 validity dates, 0x20 unknown issuer.
            string advice = (verify & 0x18u) != 0 ? "Check the PS4's date and time"
                : (verify & 0x04u) != 0 ? "Check the PS4's DNS setting"
                : "Check the PS4's date, time and DNS, and turn off any VPN or network filter that inspects secure traffic";
            return "the PS4 did not trust the certificate from " + match.Groups["host"].Value + " (0x" +
                match.Groups["rc"].Value.ToUpperInvariant() + ", verify 0x" +
                verify.ToString("X", System.Globalization.CultureInfo.InvariantCulture) + "). " + advice;
        }

        bool WaitForSelectedBackground(DlItem job, int attempt, string reason = null)
        {
            if (!BackgroundSelected || UseInAppThemeLicense(job) || BackgroundCannotOpen(job.DestPath) ||
                InstalledTitleScan.IsHomebrew(job.TitleId)) return false;
            bool logReason;
            if (string.IsNullOrEmpty(reason))
            {
                string error;
                if (ResidentDownloadService.EnsureAvailable(out error)) { job.BackgroundWaitLoggedReason = null; return false; }
                reason = string.IsNullOrEmpty(error) ? ResidentDownloadService.ReadinessDetail : error;
            }
            // A dead link (HTTP 404 or 410) never recovers by waiting. Fail the row so the
            // user can replace the link, instead of requeueing it every 15 seconds.
            if (reason != null && (reason.Contains("HTTP 404") || reason.Contains("HTTP 410")))
            {
                lock (_lock)
                {
                    if (job.AttemptId != attempt || job.Background) return true;
                    job.State = DlState.Failed;
                    job.Error = "The download link no longer works (" + (reason.Contains("HTTP 410") ? "HTTP 410" : "HTTP 404") + "). Remove this file and add a working link.";
                    job.StatusText = job.Error;
                    job.BytesPerSec = 0; job.EtaSeconds = 0;
                    job.ForegroundTransfer = false; job.ForceLocalInstall = false;
                }
                SspiLog.Write("resident", "background job failed: dead link " + ClipMsg(reason, 80));
                SaveManifest();
                return true;
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
            string residentTransfer;
            bool busy = ResidentDownloadService.HasLiveStagedTransfer(job.Id, out residentTransfer);
            lock (_lock)
            {
                foreach (var other in _items)
                    if (other != job && other.Background && !other.BgftLocalInstall &&
                        other.State != DlState.Installed && other.State != DlState.Canceled &&
                        other.State != DlState.Failed && !other.SubmittedNeedsAttention) busy = true;
                if (CommitRequestedStop(job, attempt)) return true;
                if (!busy) return false;
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
                if (other != job && other.Background && !other.ResidentStaged && !other.ResidentAutoInstall && !other.BgftLocalInstall &&
                    other.State != DlState.Failed && !other.SubmittedNeedsAttention)
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
                return SetFeederForegroundFallback(job, attempt, ex.Message);
            }
            if (CommitRequestedStop(job, attempt)) return true;

            if (header != null)
                ApplyContainerFormatHint(job, header.Data, null, header.ContentDisposition, header.EffectiveUrl);

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
                    if (ResidentDownloadService.HasStagedDownloader && !BackgroundCannotOpen(job.DestPath))
                        return HandoffStagedPackage(job, attempt, direct, job.TitleId, job.Kind, "", header.Total, true);
                    return SetFeederForegroundFallback(job, attempt, BackgroundCannotOpen(job.DestPath)
                        ? "Background service can't open this USB drive; keep SSPI open" : "Archive download requires SSPI to remain open");
                }
                return SetFeederForegroundFallback(job, attempt, "Origin did not return a PKG header");
            }

            string contentId;
            PkgContentKind actualKind;
            string kindDetail;
            long packageSize;
            string actualTitleId = job.TitleId;
            if (header.Data.Length != LoopbackPkgFeeder.HeaderBytes || header.Total <= header.Data.Length ||
                !PkgValidator.TryGetContentIdFromHeader(header.Data, out contentId) ||
                !PkgValidator.TryGetContentKindFromHeader(header.Data, out actualKind, out kindDetail) ||
                !PkgValidator.TryGetTransferPackageSize(header.Data, header.Total, out packageSize) ||
                packageSize != header.Total)
            {
                return SetFeederForegroundFallback(job, attempt, "PKG preflight was incomplete");
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
                !PkgValidator.ContentIdMatchesCatalogTitleId(contentId, job.TitleId) &&
                !(actualKind == PkgContentKind.AddOn && PkgValidator.GameAcceptsAddon(job.TitleId, contentId)))
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
                if (!InstalledTitleScan.IsHomebrew(actualTitleId) && (nativeBgft || !BackgroundCannotOpen(job.DestPath)))
                    return HandoffStagedPackage(job, attempt, direct, actualTitleId, actualKindName, contentId, packageSize, false, nativeBgft);
            }
            // SSPI owns WAN transfers. BGFT receives a validated local file at install time;
            // a missing resident must not silently revert base games to an opaque single stream.
            if (NetHttp.CanUseParallelDownload(direct, actualTitleId))
                return SelectForegroundParallelDownload(job, attempt);
            return SetFeederForegroundFallback(job, attempt, InstalledTitleScan.IsHomebrew(actualTitleId)
                ? "Homebrew apps download in SSPI; keep SSPI open" : BackgroundCannotOpen(job.DestPath)
                ? "Background service can't open this USB drive; keep SSPI open" : "Background downloader unavailable; keep SSPI open");
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
                !PkgValidator.TryGetTransferPackageSize(header, size, out declared) || declared != size ||
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

        // BGFT, not SSPI, holds a native job's payload and a release unregisters it.
        // A fallback downloads to the same path with an SSPI map; that one resumes.
        internal static bool IsNativeBgftRow(DlItem item)
        {
            return item != null && !string.IsNullOrEmpty(item.Id) &&
                string.Equals(item.DestPath, NativeBgftDestination(item.Id), StringComparison.Ordinal) &&
                !File.Exists(item.DestPath) && !File.Exists(item.DestPath + ".map");
        }

        bool HandoffStagedPackage(DlItem job, int attempt, string url, string titleId,
            string kind, string contentId, long size, bool archive = false, bool nativeBgft = false, bool localSource = false)
        {
            if (!nativeBgft && !localSource)
            {
                AppSettings.RequireStaging(Path.GetDirectoryName(job.DestPath));
                // A retry or renewed link resumes the retained .part/.map of this destination.
                if (!File.Exists(job.DestPath))
                    RequireTransferSpace(Math.Max(0, size - RetainedTransferBytes(job.DestPath)), archive);
            }
            string dependencyId;
            bool autoInstall;
            string previousGeneration;
            // A sister-edition DLC keeps its row, and its install order, under the game that
            // declares it; the resident still gets the package's own title for its header check.
            string rowTitle = job.TitleId;
            bool sisterAddon = PkgValidator.BgftSubTypeForKind(kind) == 7 && !string.IsNullOrEmpty(rowTitle) &&
                !string.IsNullOrEmpty(contentId) && !PkgValidator.ContentIdMatchesTitleId(contentId, rowTitle) &&
                PkgValidator.GameAcceptsAddon(rowTitle, contentId);
            lock (_lock)
            {
                if (!AcceptInstallCallback(job, attempt) || job.CancelRequested || job.PauseRequested)
                { CommitRequestedStop(job, attempt); return true; }
                if (nativeBgft) job.DestPath = NativeBgftDestination(job.Id);
                job.Kind = kind; job.TitleId = sisterAddon ? rowTitle : titleId;
                if (!archive) job.ContainerFormat = "pkg";
                else if (string.IsNullOrEmpty(job.ContainerFormat)) job.ContainerFormat = "archive";
                dependencyId = ResidentDependencyId(job);
                // Only native-owned predecessors publish native installed receipts. Other
                // routes can still stage concurrently, then use the app's install gate.
                autoInstall = CanInstallWithResidentDependency(dependencyId);
                job.ResidentArchive = true; job.ResidentStaged = true;
                previousGeneration = job.ResidentGeneration;
                job.ResidentGeneration = Guid.NewGuid().ToString("N");
                job.ResidentPauseDesired = null;
                job.ResidentAutoInstall = autoInstall;
                job.Background = true; job.BgftResident = true; job.BgftTaskId = -1;
                job.BgftContentId = contentId; job.BgftExpectedSize = size;
                job.State = DlState.Downloading; job.Total = size; job.StatsInitialized = false;
                job.StatusText = localSource ? "Preparing USB installation; source file retained" : "Publishing background download ownership";
            }
            if (!SaveManifest())
            {
                lock (_lock) { job.ResidentArchive = job.ResidentStaged = job.ResidentAutoInstall = job.Background = job.BgftResident = false; job.ResidentGeneration = previousGeneration; }
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
                        job.ResidentGeneration = "";
                        if (job.CancelRequested) { job.State = DlState.Canceled; job.StatusText = "Canceled"; }
                        else if (job.PauseRequested) { job.State = DlState.Paused; job.StatusText = "Paused before background handoff"; }
                        job.CancelRequested = job.PauseRequested = false;
                        job.BytesPerSec = 0; job.EtaSeconds = 0;
                        SaveManifest();
                    }
                    return true;
                }
                bool published = localSource
                    ? ResidentDownloadService.TryStartLocalSourceWithGeneration(job.Id, job.DestPath, titleId, contentId, size,
                        archive ? 0 : PkgValidator.BgftSubTypeForKind(kind), dependencyId,
                        archive ? job.ArchivePassword : null, job.ResidentGeneration, out error, out busy, archive ? ArchiveFallbacks(job) : null)
                    : nativeBgft
                    ? ResidentDownloadService.TryStartNativeBgftPackageWithGeneration(job.Id, url, job.DestPath, titleId,
                        contentId, size, PkgValidator.BgftSubTypeForKind(kind), lanes, autoInstall,
                        autoInstall ? dependencyId : "", job.ResidentGeneration, out error, out busy)
                    : ResidentDownloadService.TryStartStagedPackageWithPasswordGeneration(job.Id, url, job.DestPath, titleId,
                        job.ExpectedSha256, contentId, size, archive ? 0 : PkgValidator.BgftSubTypeForKind(kind), lanes,
                        autoInstall, autoInstall ? dependencyId : "", archive ? job.ArchivePassword : null, job.ResidentGeneration,
                        out error, out busy, archive ? ArchiveFallbacks(job) : null);
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
            {
                job.StatusText = autoInstall ? "Background download and installation queued" :
                    "Background download queued; installation waits for SSPI";
                job.RetryAfterUtcTicks = 0;
                // There is no foreground I/O lease to cool down after a durable
                // handoff. Prepare the next row before the user suspends SSPI.
                _nextJobStartAt = TransferClockMs();
            }
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
                if (CommitRequestedStop(job, job.AttemptId)) return true;
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
            if (item == null) return null;
            string error = item.BgftResident ? ResidentDownloadService.GetError(item.Id) : _loopback.LastError;
            // The loopback server's LastError is process-wide ("ok" once it starts), never a row failure.
            return string.IsNullOrWhiteSpace(error) || error == "ok" || error == "stopped" ? null : error;
        }

        void FeederMarkFailed(DlItem item)
        {
            if (item == null) return;
            if (item.BgftResident) MarkResidentFailed(item, row => row.Background && row.BgftResident);
            else _loopback.MarkFailed(item.Id);
        }

        // ResidentDownloadService writes control records inside the BGFT attach gate, and
        // other callers' publish callbacks take _lock inside that gate. A thread holding
        // _lock must never enter the gate (lock-order inversion), so the cancel is sent from
        // the ThreadPool, as ReleaseResidentJob does with a release. It is still written only
        // while the row wants it: a later release, retry or resume must not be replaced.
        void MarkResidentFailed(DlItem item, Func<DlItem, bool> stillWanted)
        {
            if (item == null) return;
            if (!Monitor.IsEntered(_lock)) { ResidentDownloadService.MarkFailed(item.Id); return; }
            string id = item.Id;
            int attempt = item.AttemptId;
            string generation = item.ResidentGeneration;
            Func<Action, bool> publishIfCurrent = publish =>
            {
                lock (_lock)
                {
                    DlItem current = Find(id);
                    bool isCurrent = (current == null || object.ReferenceEquals(current, item)) &&
                        item.AttemptId == attempt &&
                        string.Equals(item.ResidentGeneration ?? "", generation ?? "", StringComparison.Ordinal) &&
                        stillWanted(item);
                    if (isCurrent && publish != null) publish();
                    return isCurrent;
                }
            };
            RunResidentCommandOffLock(() => ResidentDownloadService.MarkFailedIfCurrent(id, publishIfCurrent));
        }

        // Runs a resident control command now, or from the ThreadPool when this thread holds
        // _lock (see MarkResidentFailed). A pool thread must not end in an unhandled exception.
        void RunResidentCommandOffLock(Action command)
        {
            if (!Monitor.IsEntered(_lock)) { command(); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { command(); }
                catch (Exception ex) { SspiLog.Write("resident", "event=deferred-control-failed exception=" + ex.GetType().Name); }
            });
        }

        void FeederRelease(DlItem item)
        {
            if (item == null) return;
            if (item.BgftResident)
                ReleaseResidentJob(item, item.AttemptId, item.ResidentGeneration);
            else _loopback.Release(item.Id);
        }

        bool EnsureResidentGeneration(DlItem item, int attempt, out string generation)
        {
            generation = null;
            if (item == null) return false;
            lock (_lock)
            {
                DlItem current = Find(item.Id);
                if ((current != null && !object.ReferenceEquals(current, item)) || item.AttemptId != attempt) return false;
                if (!string.IsNullOrEmpty(item.ResidentGeneration))
                { generation = item.ResidentGeneration; return true; }
            }

            string resolved;
            if (!ResidentDownloadService.TryGetJobIdentity(item.Id, item.DestPath, out resolved)) return false;
            lock (_lock)
            {
                DlItem current = Find(item.Id);
                if ((current != null && !object.ReferenceEquals(current, item)) || item.AttemptId != attempt) return false;
                if (!string.IsNullOrEmpty(item.ResidentGeneration))
                { generation = item.ResidentGeneration; return true; }
                if (!string.IsNullOrEmpty(resolved) && current == item)
                {
                    item.ResidentGeneration = resolved;
                    if (!SaveManifest())
                    {
                        item.ResidentGeneration = null;
                        if (item.ResidentRemovePending)
                            item.StatusText = "Could not save resident ownership; removal will retry";
                        return false;
                    }
                }
                generation = item.ResidentGeneration;
                return true;
            }
        }

        void ReleaseResidentJob(DlItem item, int attempt, string generation, bool requireRemovePending = false)
        {
            if (item == null) return;
            string id = item.Id;
            string ownerGeneration;
            bool owned = EnsureResidentGeneration(item, attempt, out generation) &&
                ResidentDownloadService.TryGetJobIdentity(id, item.DestPath, out ownerGeneration) &&
                string.Equals(ownerGeneration ?? "", generation ?? "", StringComparison.Ordinal);
            // A removal ends the row, so it must reach whichever record still owns this
            // queue ID even when the saved generation or path is stale; otherwise no
            // release is ever written and the removal waits forever.
            if (!owned && !(requireRemovePending && AdoptResidentOwnerForRemoval(item, attempt, out generation)))
            {
                lock (_lock)
                    if (object.ReferenceEquals(Find(id), item) && item.AttemptId == attempt && item.ResidentRemovePending)
                        item.StatusText = "Waiting for resident job ownership before removal";
                return;
            }
            Func<Action, bool> publishIfCurrent = publish =>
            {
                lock (_lock)
                {
                    DlItem current = Find(id);
                    bool isCurrent = (current == null || object.ReferenceEquals(current, item)) &&
                        item.AttemptId == attempt &&
                        string.Equals(item.ResidentGeneration ?? "", generation ?? "", StringComparison.Ordinal) &&
                        (!requireRemovePending || (current == item && item.ResidentRemovePending));
                    if (isCurrent && publish != null) publish();
                    return isCurrent;
                }
            };
            bool detach = item.ResidentDetachRequested;
            Action release = () => ResidentDownloadService.Release(id, generation, publishIfCurrent, detach);
            if (System.Threading.Monitor.IsEntered(_lock)) ThreadPool.QueueUserWorkItem(_ => release());
            else release();
        }

        // Queue IDs are unique per row and a pending removal blocks any new attempt, so
        // the record still owning this ID is the one to stop. Persist its generation
        // first; the generation-bound release and its publish check then agree.
        bool AdoptResidentOwnerForRemoval(DlItem item, int attempt, out string generation)
        {
            generation = null;
            string owner;
            if (!ResidentDownloadService.TryGetOwnerGeneration(item.Id, out owner)) return false;
            lock (_lock)
            {
                if (!object.ReferenceEquals(Find(item.Id), item) || item.AttemptId != attempt || !item.ResidentRemovePending) return false;
                string previous = item.ResidentGeneration;
                if (!string.Equals(previous ?? "", owner ?? "", StringComparison.Ordinal))
                {
                    item.ResidentGeneration = owner;
                    if (!SaveManifest()) { item.ResidentGeneration = previous; return false; }
                    SspiLog.Write("resident", "event=remove-owner-adopted job=" + item.Id +
                        " saved=" + (string.IsNullOrEmpty(previous) ? "none" : previous) + " owner=" + (owner ?? "legacy"));
                }
                generation = owner;
                return true;
            }
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

        bool SelectForegroundParallelDownload(DlItem job, int attempt)
        {
            if (WaitForSelectedBackground(job, attempt, ResidentDownloadService.ReadinessDetail)) return true;
            if (CommitRequestedStop(job, attempt)) return true;
            DiscardHeaderOnlyPartial(job);
            lock (_lock)
            {
                if (job.AttemptId != attempt) return true;
                job.ForceLocalInstall = true;
                job.StatusText = "Parallel download · Keep SSPI open";
            }
            LogBgftEvent("transport-app-parallel", job, "Validated provider ranges; local verification before installation");
            User.NotifyToast("Downloading in SSPI: keep the app open");
            SaveManifest();
            return false;
        }

        bool SetFeederForegroundFallback(DlItem job, int attempt, string reason)
        {
            if (WaitForSelectedBackground(job, attempt, reason)) return true;
            if (CommitRequestedStop(job, attempt)) return true;
            DiscardHeaderOnlyPartial(job);
            lock (_lock)
            {
                if (job.AttemptId != attempt) return true;
                job.ForceLocalInstall = true;
                job.StatusText = "Using in-app download: " + ClipMsg(reason, 42);
            }
            if (!string.IsNullOrEmpty(reason)) LogBgftEvent("fallback-local", job, reason);
            SspiLog.Write("resident", reason ?? "");
            SaveManifest();
            return false;
        }
    }
}
