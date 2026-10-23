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
            bool owned = job.Background || job.ResidentRemovePending || job.ResidentRetryPending ||
                (job.InstallSubmitted && !job.InstallConfirmed) || job.State == DlState.Queued ||
                job.State == DlState.Downloading || job.State == DlState.Finalizing ||
                job.State == DlState.Installing || job.State == DlState.Submitted ||
                job.State == DlState.Resolving || job.State == DlState.Paused;
            if (!owned && (job.ResidentArchive || job.ResidentStaged || !string.IsNullOrEmpty(job.ResidentGeneration)))
                owned = ResidentDownloadService.HasJob(job.Id);
            if (!string.IsNullOrEmpty(job.FanOutPendingPaths))
            {
                string[] paths = job.FanOutPendingPaths.Split(new[] { '\n' },
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (string p in paths)
                    if (SamePath(p, full)) return true;
            }
            if (owned || !string.IsNullOrEmpty(job.FanOutPendingPaths))
            {
                string extractRoot;
                try { extractRoot = Path.GetFullPath(Path.Combine(AppSettings.DownloadDir, ".extract", UrlTag(job.Id))) + Path.DirectorySeparatorChar; }
                catch { extractRoot = null; }
                if (extractRoot != null && full.StartsWith(extractRoot, StringComparison.OrdinalIgnoreCase)) return true;
            }
            if (owned)
            {
                var inputs = job.LocalSource ? new List<string> { job.DestPath } : ArchivePaths(job);
                foreach (string input in inputs)
                {
                    if (string.IsNullOrEmpty(input)) continue;
                    if (SamePath(input, full)) return true;
                    foreach (string suffix in DownloadSidecarSuffixes)
                        if (SamePath(input + suffix, full)) return true;
                }
                // Resident extraction has no child queue rows. Its inputs remain owned
                // until the worker and any accepted installer release this exact job.
                string staging = ResidentExtractionRoot(job);
                if (staging != null && System.Text.RegularExpressions.Regex.IsMatch(job.Id ?? "", @"\A[A-Za-z0-9_-]{1,190}\z"))
                {
                    string extracted = Path.GetFullPath(Path.Combine(staging, "extract-" + job.Id)) + Path.DirectorySeparatorChar;
                    if (full.StartsWith(extracted, StringComparison.OrdinalIgnoreCase)) return true;
                }
                string resident;
                try { resident = Path.GetFullPath(Path.Combine(AppSettings.DataDir, "resident", "archives", job.Id)) + Path.DirectorySeparatorChar; }
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
                    if (string.Equals(Path.GetFileName(full), ".sspi-volume-id", StringComparison.OrdinalIgnoreCase))
                    { detail = "USB identity records are kept for safe resume"; return false; }
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
                    // A deleted package or partial leaves its resume map, checksum
                    // receipt and hidden transfer lock behind. No job owns this
                    // download (checked above), so remove those sidecars too.
                    string basePath = DownloadBasePath(full);
                    bool baseClaimed = false;
                    foreach (var job in _items)
                        if (IsPathClaimedByJob(job, basePath)) { baseClaimed = true; break; }
                    // Only once no payload remains: deleting a small receipt or map must
                    // never delete the package or partial download it belongs to.
                    bool payloadRemains = File.Exists(basePath) || File.Exists(basePath + ".part") ||
                        File.Exists(basePath + ".resident.part") || File.Exists(basePath + ".parallel.part");
                    for (int piece = 0; !payloadRemains && piece < DownloadTransferSettings.MaxRangeCount; piece++)
                        payloadRemains = File.Exists(basePath + ".part.p" + piece);
                    if (!baseClaimed && !payloadRemains && !DeleteDownloadFiles(basePath)) detail = "File removed; some transfer records remain";
                    return true;
                } catch (Exception ex) { detail = "Delete failed: " + ex.Message; return false; }
            }
        }

        // Caller holds _lock until cleanup and removal finish, so a replacement
        // cannot claim the deterministic destination while old files are deleted.
        bool DeleteUnclaimedDownloadFiles(DlItem item)
        {
            if (item.LocalSource || ((item.ResidentArchive || item.ResidentStaged ||
                !string.IsNullOrEmpty(item.ResidentGeneration)) &&
                ResidentDownloadService.HasJob(item.Id))) return false;
            foreach (DlItem other in _items)
                if (!object.ReferenceEquals(other, item) && SamePath(other.DestPath, item.DestPath))
                    return false;
            return DeleteDownloadFiles(item);
        }

        bool TryCleanupOwnedFiles(DlItem item, out string error)
        {
            error = null;
            if (item.InstallSubmitted && !item.InstallConfirmed)
            {
                error = "PS4 installation is unconfirmed; package retained";
                return false;
            }
            if ((item.ResidentArchive || item.ResidentStaged || !string.IsNullOrEmpty(item.ResidentGeneration)) &&
                ResidentDownloadService.HasJob(item.Id))
            {
                error = "Waiting for the background worker to release its files";
                return false;
            }
            if (!item.LocalSource)
            {
                foreach (string path in ArchivePaths(item))
                {
                    bool claimed = false;
                    foreach (DlItem other in _items)
                    {
                        if (object.ReferenceEquals(other, item)) continue;
                        if (SamePath(other.DestPath, path)) { claimed = true; break; }
                        if (other.LocalSource) continue;
                        foreach (string otherPath in ArchivePaths(other))
                            if (SamePath(otherPath, path)) { claimed = true; break; }
                        if (claimed) break;
                    }
                    if (!claimed && OnDisconnectedUsb(path))
                    {
                        SspiLog.Write("download", "event=remove-drive-absent job=" + item.Id + " files-left-for-stored-files=1");
                        continue;
                    }
                    if (!claimed && !DeleteDownloadFiles(path))
                    {
                        error = "Downloaded files could not be removed; reconnect storage and retry";
                        return false;
                    }
                }
            }
            if (item.ResidentArchive && !OnDisconnectedUsb(ResidentExtractionRoot(item)) && !TryDeleteResidentExtractionDirectory(item))
            {
                error = "Extracted package cleanup pending; reconnect storage and retry";
                return false;
            }
            return true;
        }

        bool DiscardInvalidDownload(DlItem item, string reason)
        {
            if (item == null) return false;
            bool removed = DeleteDownloadFiles(item);
            if (removed) item.Done = item.Total = 0;
            item.InstallConfirmed = false;
            item.ForceLocalInstall = false;
            item.Error = removed ? reason : reason + "; file could not be removed or is still owned by the installer";
            item.BytesPerSec = 0;
            item.EtaSeconds = 0;
            if (removed) ClearBackground(item);
            return removed;
        }

        static bool DeleteDownloadFiles(DlItem item)
        {
            return item != null && !item.LocalSource &&
                (!item.InstallSubmitted || item.InstallConfirmed) && DeleteDownloadFiles(item.DestPath);
        }

        static bool DeleteDownloadFiles(string finalPath)
        {
            if (!IsOwnedDownloadPath(finalPath)) return false;
            string normalized = finalPath.Replace('\\', '/');
            if (normalized.StartsWith("/mnt/usb", StringComparison.Ordinal))
            {
                string root;
                if (!ResidentDownloadService.TryStagingRoot(normalized, out root)) return false;
                try { AppSettings.RequireStaging(root); }
                catch (IOException) { return false; }
                catch (UnauthorizedAccessException) { return false; }
            }
            var files = new List<string> { finalPath };
            foreach (string suffix in DownloadSidecarSuffixes) files.Add(finalPath + suffix);
            for (int i = 0; i < DownloadTransferSettings.MaxRangeCount; i++)
                files.Add(finalPath + ".part.p" + i);
            bool cleaned = true;
            foreach (string file in files)
            {
                try
                {
                    if (File.Exists(file)) File.Delete(file);
                    if (File.Exists(file) || Directory.Exists(file)) cleaned = false;
                }
                catch (IOException) { cleaned = false; }
                catch (UnauthorizedAccessException) { cleaned = false; }
            }
            return cleaned;
        }

        // Every file a transfer, checksum receipt or installer handoff keeps beside
        // a download. The native engine's .xfer-lock stays while it owns the file
        // and was previously never removed, so installs and cancellations left one
        // behind per package (hidden from Stored Files).
        static readonly string[] DownloadSidecarSuffixes = {
            ".part", ".part.resume", ".part.resume.tmp", ".part.ranges", ".part.ranges.tmp", ".part.checkpoint.tmp",
            ".resume", ".ranges", ".map", ".map.tmp", ".sha256-ok", ".sha256-ok.tmp", ".xfer-lock",
            ".resident.part", ".resident.map", ".resident.map.tmp", ".resident.xfer-lock",
            ".resident.sha256-ok", ".resident.sha256-ok.tmp",
            ".parallel.part", ".parallel.part.ranges", ".parallel.part.ranges.tmp",
            ".bgft-meta", ".bgft-meta.tail", ".bgft-meta.tmp", ".bgft-fallback", ".bgft-debug"
        };

        // The download a stored file belongs to: its own path for a package or
        // archive, or the path with a partial/sidecar suffix removed.
        internal static string DownloadBasePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            string best = null;
            foreach (string suffix in DownloadSidecarSuffixes)
                if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && path.Length > suffix.Length &&
                    (best == null || suffix.Length > best.Length)) best = suffix;
            return best == null ? path : path.Substring(0, path.Length - best.Length);
        }

        // Startup only runs this when no job is active, background-owned, installing
        // or submitted. A lock whose download, partial and resume map are all gone
        // cannot protect anything, so it is removed. Recent locks are left alone.
        void RemoveOrphanTransferLocks()
        {
            var locks = new List<string>();
            try
            {
                foreach (string root in StorageRoots())
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                    foreach (string path in Directory.GetFiles(root, "*.xfer-lock"))
                    {
                        if (locks.Count >= 4096) break;
                        locks.Add(path);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            int removed = 0;
            foreach (string path in locks)
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(1)) continue;
                    string owner = path.Substring(0, path.Length - ".xfer-lock".Length);
                    if (File.Exists(owner) || File.Exists(owner + ".part") || File.Exists(owner + ".map")) continue;
                    string basePath = DownloadBasePath(owner);
                    bool claimed = false;
                    lock (_lock)
                        foreach (var job in _items)
                            if (IsPathClaimedByJob(job, basePath) || IsPathClaimedByJob(job, owner)) { claimed = true; break; }
                    if (claimed) continue;
                    File.Delete(path);
                    removed++;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            if (removed > 0) SspiLog.Write("download", "event=orphan-transfer-locks removed=" + removed);
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
    }
}
