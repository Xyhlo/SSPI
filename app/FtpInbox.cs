using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Orbis
{
    // Watches <data>/pkg-rars for PKG files, RAR volumes and single-file ZIP or 7z
    // archives uploaded over FTP. An upload is handed to the local install pipeline
    // once every tracked file has stopped changing for the grace period. Source files
    // are never moved or deleted.
    //
    // Ownership handshake with the resident worker, which watches the same folder
    // while it runs (also with SSPI closed): every upload identity has one claim
    // file, <data>/ftp-inbox-claims/<fnv1a64(name)>-<size>-<mtime seconds>.claim.
    // Whoever creates the claim of the PKG, ZIP, 7z or first RAR volume
    // (FileMode.CreateNew here, O_CREAT|O_EXCL in the worker) owns the upload, and an
    // existing claim marks the upload as processed. A claim body holds owner, name,
    // size, mtime, job and state lines; the worker may append its set's file count
    // and bytes. While a ready worker advertises ftpinbox=1 in its heartbeat, the app
    // only shows progress and leaves the upload to it.
    internal static class FtpInbox
    {
        internal sealed class Snapshot
        {
            public int Files;
            public long ReceivedBytes;
            public double BytesPerSecond;
            public bool Background;
            public string[] Names = new string[0];
        }

        sealed class Entry
        {
            public string Path, Name;
            public long Size, MtimeSeconds;
            public DateTime ChangedUtc, ReadyUtc;
        }

        // The grace period also gives a PS4 package installer started on the same
        // file time to register its task before SSPI checks for it. Archive sets
        // arrive one volume at a time and FTP clients can pause between volumes, so
        // they wait longer. Must match gs_ftp_inbox.inc.
        const int PollMilliseconds = 2000, QuietSeconds = 10, ArchiveQuietSeconds = 60, ScanLimit = 1024, DeferSeconds = 120;
        // DownloadManager.QueueLocalFiles refuses more than 256 files per call, so a
        // larger burst is claimed and handed off in batches on consecutive polls.
        const int HandoffBatch = 256;
        static readonly object Gate = new object();
        static readonly Dictionary<string, Entry> Tracked = new Dictionary<string, Entry>(StringComparer.Ordinal);
        static Snapshot _snapshot = new Snapshot();
        static Thread _thread;
        static DateTime _retryUtc = DateTime.MinValue, _pruneUtc = DateTime.MinValue;

        internal static string Root { get { return LocalInstallSource.InboxRoot; } }
        static string ClaimRoot { get { return Path.Combine(AppSettings.DataDir, "ftp-inbox-claims"); } }
        static string LegacyLedgerPath { get { return Path.Combine(AppSettings.DataDir, "ftp-inbox-processed.txt"); } }

        internal static Snapshot Current { get { lock (Gate) return _snapshot; } }

        // 1: PKG, ZIP, 7z or first RAR volume (decides ownership), 2: continuation volume,
        // 0: other. ZIP and 7z are single-file archives, as in USB installation.
        static int Kind(string name)
        {
            if (name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".7zip", StringComparison.OrdinalIgnoreCase)) return 1;
            string set; int index;
            if (!ArchiveVolumeSet.TryIndex(name, out set, out index)) return 0;
            return index == 1 ? 1 : 2;
        }

        static int QuietSecondsFor(string name)
        { return name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) ? QuietSeconds : ArchiveQuietSeconds; }

        internal static long UnixSeconds(DateTime utc)
        { return (utc.Ticks - 621355968000000000L) / TimeSpan.TicksPerSecond; }

        // Must match gs_inbox_claim_path in resident/plugin/gs_ftp_inbox.inc.
        internal static string ClaimFileName(string name, long size, long mtimeSeconds)
        {
            ulong hash = 0xcbf29ce484222325UL;
            unchecked { foreach (byte value in Encoding.UTF8.GetBytes(name ?? "")) { hash ^= value; hash *= 0x100000001b3UL; } }
            return hash.ToString("x16", CultureInfo.InvariantCulture) + "-" + size.ToString(CultureInfo.InvariantCulture) + "-" +
                mtimeSeconds.ToString(CultureInfo.InvariantCulture) + ".claim";
        }

        static string ClaimPath(string name, long size, long mtime)
        { return Path.Combine(ClaimRoot, ClaimFileName(name, size, mtime)); }

        static bool IsClaimed(Entry entry)
        {
            try { return File.Exists(ClaimPath(entry.Name, entry.Size, entry.MtimeSeconds)); }
            catch { return false; }
        }

        // 1: this app now owns the upload, 0: another owner claimed it, -1: retry later.
        static int TryClaim(string name, long size, long mtime, string state)
        {
            string path = ClaimPath(name, size, mtime);
            try
            {
                Directory.CreateDirectory(ClaimRoot);
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] body = Encoding.UTF8.GetBytes("app\n" + name + "\n" + size.ToString(CultureInfo.InvariantCulture) + "\n" +
                        mtime.ToString(CultureInfo.InvariantCulture) + "\n\n" + state + "\n");
                    stream.Write(body, 0, body.Length);
                }
                return 1;
            }
            catch (Exception ex)
            {
                bool exists;
                try { exists = File.Exists(path); } catch { exists = false; }
                if (!exists) SspiLog.Write("download", "ftp-inbox claim " + SspiLog.Clean(ex.Message));
                return exists ? 0 : -1;
            }
        }

        static void Unclaim(Entry entry)
        {
            try { File.Delete(ClaimPath(entry.Name, entry.Size, entry.MtimeSeconds)); }
            catch (Exception ex) { SspiLog.Write("download", "ftp-inbox unclaim " + SspiLog.Clean(ex.Message)); }
        }

        // State line of a claim body. An incomplete or unreadable claim counts as
        // "queued", the state an owner writes when it takes an upload.
        static string ClaimState(Entry entry)
        {
            try
            {
                string[] lines = File.ReadAllLines(ClaimPath(entry.Name, entry.Size, entry.MtimeSeconds));
                return lines.Length >= 6 ? lines[5] : "queued";
            }
            catch { return "queued"; }
        }

        // Earlier builds recorded path|size|ticks lines. Uploads that still match are
        // claimed so neither side processes them again; the ledger is then retired.
        static void MigrateLegacyLedger()
        {
            string ledger = LegacyLedgerPath;
            if (!File.Exists(ledger)) return;
            foreach (string line in File.ReadAllLines(ledger))
            {
                int last = line.LastIndexOf('|'), middle = last > 0 ? line.LastIndexOf('|', last - 1) : -1;
                long size, ticks;
                if (middle <= 0 || !long.TryParse(line.Substring(middle + 1, last - middle - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out size) ||
                    !long.TryParse(line.Substring(last + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks)) continue;
                string path = line.Substring(0, middle);
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length != size || info.LastWriteTimeUtc.Ticks != ticks) continue;
                    TryClaim(info.Name, size, UnixSeconds(info.LastWriteTimeUtc), "legacy");
                }
                catch { }
            }
            string retired = ledger + ".migrated";
            if (File.Exists(retired)) File.Delete(retired);
            File.Move(ledger, retired);
        }

        // A claim whose upload is gone or has changed no longer describes a file; drop
        // it so a later upload with the same name is processed again.
        static void PruneClaims()
        {
            if (!Directory.Exists(ClaimRoot)) return;
            int scanned = 0;
            foreach (string claim in Directory.EnumerateFiles(ClaimRoot, "*.claim"))
            {
                if (++scanned > 4096) break;
                try
                {
                    string[] lines = File.ReadAllLines(claim);
                    long size, mtime;
                    if (lines.Length < 4 || lines[1].Length == 0 || lines[1].IndexOfAny(new[] { '/', '\\' }) >= 0 ||
                        !long.TryParse(lines[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out size) ||
                        !long.TryParse(lines[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out mtime)) continue;
                    var info = new FileInfo(Path.Combine(Root, lines[1]));
                    if (info.Exists && info.Length == size && UnixSeconds(info.LastWriteTimeUtc) == mtime) continue;
                    File.Delete(claim);
                }
                catch { }
            }
        }

        // handoff receives the complete PKGs and first RAR volumes this app owns and
        // returns false when the pipeline cannot accept them yet. background reports
        // uploads that the resident worker claimed.
        internal static void Start(Action<string> arrival, Func<List<string>, bool> handoff, Action changed, Action<string> background)
        {
            lock (Gate)
            {
                if (_thread != null) return;
                _thread = new Thread(() => Run(arrival, handoff, changed, background)) { IsBackground = true, Name = "sspi-ftp-inbox" };
            }
            _thread.Start();
        }

        static void Run(Action<string> arrival, Func<List<string>, bool> handoff, Action changed, Action<string> background)
        {
            try
            {
                Directory.CreateDirectory(Root);
                MigrateLegacyLedger();
            }
            catch (Exception ex) { SspiLog.Write("download", "ftp-inbox unavailable " + SspiLog.Clean(ex.Message)); }
            while (true)
            {
                try { Poll(arrival, handoff, changed, background); }
                catch (Exception ex) { SspiLog.Write("download", "ftp-inbox poll " + SspiLog.Clean(ex.Message)); }
                Thread.Sleep(PollMilliseconds);
            }
        }

        static void Poll(Action<string> arrival, Func<List<string>, bool> handoff, Action changed, Action<string> background)
        {
            if (!Directory.Exists(Root)) { Directory.CreateDirectory(Root); return; }
            DateTime now = DateTime.UtcNow;
            if (now >= _pruneUtc) { _pruneUtc = now.AddSeconds(60); PruneClaims(); }
            bool resident = ResidentDownloadService.WatchesFtpInbox;
            var present = new HashSet<string>(StringComparer.Ordinal);
            var takenByWorker = new List<string>();
            long grown = 0; string firstArrival = null; bool wasIdle, filling = false;
            lock (Gate) wasIdle = Tracked.Count == 0;
            int scanned = 0;
            foreach (string file in Directory.EnumerateFiles(Root))
            {
                if (++scanned > ScanLimit) break;
                string path = file.Replace('\\', '/'), name = Path.GetFileName(path);
                if (Kind(name) == 0) continue;
                FileInfo info;
                try
                {
                    info = new FileInfo(path);
                    if (!info.Exists) continue;
                    if (info.Length <= 0)
                    {
                        // A client creates each upload (often the next RAR volume) empty
                        // and can pause before sending data: a recent one is activity.
                        if (Math.Abs((now - info.LastWriteTimeUtc).TotalSeconds) < QuietSecondsFor(name)) filling = true;
                        continue;
                    }
                }
                catch { continue; }
                var probe = new Entry { Path = path, Name = name, Size = info.Length, MtimeSeconds = UnixSeconds(info.LastWriteTimeUtc), ChangedUtc = now };
                if (IsClaimed(probe))
                {
                    bool tracked;
                    lock (Gate) tracked = Tracked.Remove(path);
                    // Only uploads the app was still tracking reach here; its own hand-offs
                    // leave the tracked set before their claim is seen. The worker posts its
                    // own PS4 notification when it skips an upload (invalid, installed, busy).
                    if (tracked && Kind(name) == 1 && ClaimState(probe) == "queued") takenByWorker.Add(name);
                    continue;
                }
                present.Add(path);
                lock (Gate)
                {
                    Entry entry;
                    if (!Tracked.TryGetValue(path, out entry))
                    {
                        Tracked.Add(path, probe);
                        if (firstArrival == null) firstArrival = name;
                    }
                    else if (entry.Size != probe.Size || entry.MtimeSeconds != probe.MtimeSeconds)
                    {
                        if (probe.Size > entry.Size) grown += probe.Size - entry.Size;
                        entry.Size = probe.Size; entry.MtimeSeconds = probe.MtimeSeconds;
                        entry.ChangedUtc = now; entry.ReadyUtc = DateTime.MinValue;
                    }
                }
            }
            List<Entry> ready = null; bool defer = false;
            lock (Gate)
            {
                foreach (string path in new List<string>(Tracked.Keys))
                    if (!present.Contains(path)) Tracked.Remove(path);
                bool quiet = Tracked.Count > 0 && !filling;
                int window = QuietSeconds;
                foreach (Entry entry in Tracked.Values) window = Math.Max(window, QuietSecondsFor(entry.Name));
                foreach (Entry entry in Tracked.Values)
                    if ((now - entry.ChangedUtc).TotalSeconds < window) { quiet = false; break; }
                // RAR sets arrive one volume at a time, so hand-off waits until the
                // whole inbox is quiet instead of releasing a partly uploaded set.
                if (quiet && now >= _retryUtc)
                {
                    ready = new List<Entry>(Tracked.Values);
                    foreach (Entry entry in ready)
                    {
                        if (entry.ReadyUtc == DateTime.MinValue) entry.ReadyUtc = now;
                        // A ready worker claims the upload within a poll or two. If it has
                        // not after DeferSeconds, the app takes over; claims prevent overlap.
                        if (resident && (now - entry.ReadyUtc).TotalSeconds < DeferSeconds) defer = true;
                    }
                }
                long received = 0; var names = new List<string>();
                foreach (Entry entry in Tracked.Values) { received += entry.Size; if (names.Count < 3) names.Add(entry.Name); }
                names.Sort(StringComparer.OrdinalIgnoreCase);
                _snapshot = new Snapshot { Files = Tracked.Count, ReceivedBytes = received, Background = resident,
                    BytesPerSecond = grown / (PollMilliseconds / 1000.0), Names = names.ToArray() };
            }
            // The worker posts its own PS4 notification for a new upload.
            if (firstArrival != null && wasIdle && arrival != null && !resident) arrival(firstArrival);
            if (background != null) foreach (string name in takenByWorker) background(name);
            // Redraw only while uploads change; an idle inbox costs one folder scan.
            if (changed != null && (firstArrival != null || grown > 0 || ready != null || takenByWorker.Count > 0 ||
                (!wasIdle && present.Count == 0))) changed();
            if (ready == null || ready.Count == 0 || defer) return;
            var owned = new List<Entry>();
            var paths = new List<string>();
            bool retry = false;
            var primaries = new List<Entry>();
            foreach (Entry entry in ready) if (Kind(entry.Name) == 1) primaries.Add(entry);
            primaries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
            foreach (Entry entry in primaries)
            {
                // Uploads beyond one batch stay tracked and ready for the next poll.
                if (owned.Count == HandoffBatch) break;
                int claim = TryClaim(entry.Name, entry.Size, entry.MtimeSeconds, "queued");
                if (claim > 0) { owned.Add(entry); paths.Add(entry.Path); }
                else if (claim < 0) retry = true;
            }
            // Continuation volumes travel with their set's claim.
            foreach (Entry entry in ready)
                if (Kind(entry.Name) == 2) TryClaim(entry.Name, entry.Size, entry.MtimeSeconds, "volume");
            paths.Sort(StringComparer.Ordinal);
            bool accepted = paths.Count == 0;
            if (paths.Count > 0)
            {
                try { accepted = handoff != null && handoff(paths); }
                catch (Exception ex) { SspiLog.Write("download", "ftp-inbox handoff " + SspiLog.Clean(ex.Message)); }
            }
            if (!accepted)
            {
                // Nothing was queued: release the claims so either side can retry.
                foreach (Entry entry in owned) Unclaim(entry);
                retry = true;
            }
            lock (Gate)
            {
                if (retry) _retryUtc = DateTime.UtcNow.AddSeconds(30);
                if (accepted)
                    foreach (Entry entry in ready)
                        if (Kind(entry.Name) == 2 || owned.Contains(entry)) Tracked.Remove(entry.Path);
                if (Tracked.Count == 0) _snapshot = new Snapshot();
            }
            if (changed != null) changed();
        }
    }
}
