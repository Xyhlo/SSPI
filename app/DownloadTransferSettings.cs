using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Net;
using System.Globalization;

namespace Orbis
{
    internal static class DownloadTransferSettings
    {
        public const int MinRangeCount = 1;
        public const int MaxRangeCount = 5;
        public const int DefaultRangeCount = 5;

        public static int ClampRangeCount(int value)
        {
            if (value < MinRangeCount) return MinRangeCount;
            if (value > MaxRangeCount) return MaxRangeCount;
            return value;
        }

        sealed class Permit { internal int Count; }
        const int MaximumPermits = 512;
        static readonly Dictionary<string, Permit> Permits = new Dictionary<string, Permit>(StringComparer.Ordinal);
        static readonly Queue<string> PermitOrder = new Queue<string>();
        static int overflowLimit = MaxRangeCount;
        internal sealed class RangeProbe { internal long Total; internal string ETag, Effective, VerifiedTitle; internal byte[] Header; internal DateTime Until; }
        static readonly Dictionary<string, RangeProbe> Probes = new Dictionary<string, RangeProbe>(StringComparer.Ordinal);
        internal static void RememberProbe(string url, long total, string etag, string effective, byte[] header = null, string verifiedTitle = null)
        {
            lock (Probes)
            {
                if (Probes.Count >= 32) Probes.Clear();
                Probes[url] = new RangeProbe { Total = total, ETag = etag, Effective = effective, Header = header,
                    VerifiedTitle = header == null ? null : verifiedTitle, Until = DateTime.UtcNow.AddSeconds(30) };
            }
        }
        internal static bool HasProbe(string url, string expectedTitle)
        {
            lock (Probes)
            {
                RangeProbe probe;
                return url != null && Probes.TryGetValue(url, out probe) && probe.Until > DateTime.UtcNow &&
                    probe.Total >= 8L * 1024 * 1024 && (StrongEtag(probe.ETag) ||
                    (probe.Header != null && !string.IsNullOrEmpty(probe.VerifiedTitle) &&
                    string.Equals(probe.VerifiedTitle, expectedTitle, StringComparison.OrdinalIgnoreCase)));
            }
        }
        internal static bool TakeProbe(string url, out long total, out string etag, out string effective)
        { byte[] header; string title; return TakeProbe(url, out total, out etag, out effective, out header, out title); }
        internal static bool TakeProbe(string url, out long total, out string etag, out string effective, out byte[] header, out string verifiedTitle)
        {
            total = -1; etag = effective = verifiedTitle = null; header = null;
            lock (Probes)
            {
                RangeProbe probe;
                if (!Probes.TryGetValue(url, out probe)) return false;
                Probes.Remove(url);
                if (probe.Until <= DateTime.UtcNow) return false;
                total = probe.Total; etag = probe.ETag; effective = probe.Effective;
                header = probe.Header; verifiedTitle = probe.VerifiedTitle; return true;
            }
        }
        public static void RememberProviderLimit(string url, int maximum)
        {
            if (string.IsNullOrEmpty(url)) return;
            lock (Permits)
            {
                // A live signed URL keeps its restriction for this process. Cache
                // pressure or elapsed time must never raise its connection count.
                Permit known;
                int count = ClampRangeCount(maximum);
                if (Permits.TryGetValue(url, out known)) known.Count = Math.Min(known.Count, count);
                else
                {
                    if (Permits.Count >= MaximumPermits)
                    {
                        string oldest = PermitOrder.Dequeue();
                        overflowLimit = Math.Min(overflowLimit, Permits[oldest].Count);
                        Permits.Remove(oldest);
                    }
                    Permits[url] = new Permit { Count = count };
                    PermitOrder.Enqueue(url);
                }
            }
        }
        public static int ConnectionsFor(string url, int requested)
        {
            int count = ClampRangeCount(requested);
            lock (Permits)
            {
                Permit permit;
                return Math.Min(count, url != null && Permits.TryGetValue(url, out permit) ? permit.Count : overflowLimit);
            }
        }
        internal static bool StrongEtag(string tag)
        {
            return !string.IsNullOrEmpty(tag) && tag.Length >= 2 && tag.Length <= 1024 &&
                tag[0] == '"' && tag[tag.Length - 1] == '"' && tag.IndexOf('\r') < 0 && tag.IndexOf('\n') < 0;
        }
        internal static bool IdentityEncoding(string value)
        { return string.IsNullOrEmpty(value) || value.Equals("identity", StringComparison.OrdinalIgnoreCase); }
        internal static bool SameOrigin(string first, string second)
        {
            Uri a, b;
            return Uri.TryCreate(first, UriKind.Absolute, out a) && Uri.TryCreate(second, UriKind.Absolute, out b) &&
                a.Scheme.Equals(b.Scheme, StringComparison.OrdinalIgnoreCase) &&
                a.Host.Equals(b.Host, StringComparison.OrdinalIgnoreCase) && a.Port == b.Port;
        }
    }

    internal sealed class DownloadRangeRejectedException : Exception
    { internal DownloadRangeRejectedException(string reason) : base(reason) { } }

    internal sealed class DownloadHttpException : IOException
    {
        internal readonly int StatusCode;
        internal readonly int RetryAfterSeconds;
        internal DownloadHttpException(int status, string retryAfter = null, string operation = "downloading")
            : base("HTTP " + status + " " + operation)
        {
            StatusCode = status;
            RetryAfterSeconds = ParseRetryAfter(retryAfter, DateTime.UtcNow);
        }
        internal static int ParseRetryAfter(string value, DateTime now)
        {
            long seconds;
            if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out seconds))
                return (int)Math.Max(1, Math.Min(300, seconds));
            DateTimeOffset date;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date))
                return (int)Math.Max(1, Math.Min(300, Math.Ceiling((date.UtcDateTime - now).TotalSeconds)));
            return 0;
        }
        internal static DownloadHttpException Find(Exception error)
        {
            for (int depth = 0; error != null && depth < 8; depth++, error = error.InnerException)
            {
                var typed = error as DownloadHttpException;
                if (typed != null) return typed;
                var web = error as WebException;
                var response = web == null ? null : web.Response as HttpWebResponse;
                if (response != null)
                {
                    try { return new DownloadHttpException((int)response.StatusCode, response.Headers["Retry-After"]); }
                    catch (ObjectDisposedException) { }
                }
            }
            return null;
        }
        // attempt is the number of scheduled HTTP retries, independent of link renewal.
        internal static bool TryGetRetry(Exception error, int attempt, DateTime now, out long retryAtUtcTicks)
        {
            retryAtUtcTicks = 0;
            if (error is OperationCanceledException || attempt < 0 || attempt >= 3) return false;
            var http = Find(error);
            if (http == null) return false;
            switch (http.StatusCode)
            {
                case 408: case 425: case 429: case 500: case 502: case 503: case 504:
                    retryAtUtcTicks = now.AddSeconds(Math.Max(http.RetryAfterSeconds, 2 << attempt)).Ticks;
                    return true;
                default: return false;
            }
        }
    }

    internal sealed class TransferCancellation : IDisposable
    {
        readonly ManualResetEvent done;
        readonly Thread watcher;
        readonly object callbackGate = new object();
        bool disposed;
        internal TransferCancellation(Func<bool> canceled, Action abort)
        {
            if (canceled == null) return;
            done = new ManualResetEvent(false);
            watcher = new Thread(() =>
            {
                while (!done.WaitOne(100))
                {
                    bool stop = false;
                    try { stop = canceled(); } catch { }
                    if (!stop) continue;
                    lock (callbackGate)
                    {
                        if (disposed) return;
                        try { abort(); } catch { }
                    }
                    return;
                }
            }) { IsBackground = true, Name = "HTTP cancel" };
            try { watcher.Start(); } catch { done.Close(); throw; }
        }
        public void Dispose()
        {
            if (done == null) return;
            // Returning authorizes the caller to close/reuse the native request.
            // A timed watcher join alone cannot prove an in-flight abort has ended.
            lock (callbackGate)
            {
                if (disposed) return;
                disposed = true;
                done.Set();
            }
            if (watcher.Join(1000)) done.Close();
        }
    }

    // One process owns bulk transfers at a time (app or resident). Inside the app,
    // every single stream and range shares this budget; provider limits remain per URL.
    internal static class TransferLaneBudget
    {
        internal const int Maximum = 5;
        static readonly object Gate = new object();
        static readonly List<Job> Jobs = new List<Job>();
        [ThreadStatic] static Job current;
        internal static Job Current { get { return current; } }
        internal static int Active { get { lock (Gate) { int n = 0; foreach (Job j in Jobs) n += j.Active; return n; } } }

        internal sealed class Scope : IDisposable
        {
            readonly Job previous;
            internal readonly Job Job;
            bool disposed;
            internal Scope(int maximum)
            {
                previous = current;
                Job = new Job { Maximum = DownloadTransferSettings.ClampRangeCount(maximum) };
                lock (Gate) { Jobs.Add(Job); Monitor.PulseAll(Gate); }
                current = Job;
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                lock (Gate) { Jobs.Remove(Job); Monitor.PulseAll(Gate); }
                current = previous;
            }
        }
        internal sealed class Job
        {
            internal int Maximum, Active;
            internal IDisposable Acquire(Func<bool> cancel)
            {
                for (;;)
                {
                    // Cancellation may take the queue lock; never invoke it inside Gate.
                    if (cancel != null && cancel()) throw new OperationCanceledException();
                    lock (Gate)
                    {
                        int total = 0, reserved = 0;
                        foreach (Job other in Jobs)
                        {
                            total += other.Active;
                            if (other != this) reserved += Math.Max(0, Math.Min(2, other.Maximum) - other.Active);
                        }
                        if (Active < Maximum && total < TransferLaneBudget.Maximum &&
                            (Active < 2 || total + reserved < TransferLaneBudget.Maximum))
                        { Active++; return new Lane(this); }
                        Monitor.Wait(Gate, 100);
                    }
                }
            }
        }
        sealed class Lane : IDisposable
        {
            Job job;
            internal Lane(Job owner) { job = owner; }
            public void Dispose()
            {
                lock (Gate)
                {
                    if (job == null) return;
                    job.Active--; job = null; Monitor.PulseAll(Gate);
                }
            }
        }
        sealed class Single : IDisposable
        {
            readonly Scope scope;
            readonly IDisposable lane;
            internal Single(Func<bool> cancel)
            {
                if (current == null) scope = Begin(1);
                try { lane = current.Acquire(cancel); }
                catch { if (scope != null) scope.Dispose(); throw; }
            }
            public void Dispose() { lane.Dispose(); if (scope != null) scope.Dispose(); }
        }
        internal static Scope Begin(int maximum) { return new Scope(maximum); }
        internal static IDisposable AcquireSingle(Func<bool> cancel) { return new Single(cancel); }
    }

    internal static class ExactRangeTransfer
    {
        // Coalesce short TLS records before crossing the managed file/progress boundary.
        // A range owns at most 256 KiB; the caller validates headers and flushes before
        // publishing a durable checkpoint.
        internal static long Copy(Func<byte[], int, int, int> read, Stream output, long expected,
            Func<bool> cancel, Action<int> progress)
        {
            if (expected < 0) throw new ArgumentOutOfRangeException("expected");
            var buffer = new byte[256 * 1024];
            long received = 0;
            for (;;)
            {
                int count = 0;
                bool ended = false;
                while (count < buffer.Length)
                {
                    if (cancel != null && cancel()) throw new OperationCanceledException("paused");
                    int got = read(buffer, count, buffer.Length - count);
                    if (got < 0 || got > buffer.Length - count)
                        throw new IOException("Invalid range read length");
                    if (got == 0) { ended = true; break; }
                    if (got > expected - received - count)
                        throw new DownloadRangeRejectedException("Range body exceeded requested span");
                    count += got;
                }
                if (count > 0)
                {
                    if (cancel != null && cancel()) throw new OperationCanceledException("paused");
                    output.Write(buffer, 0, count);
                    received += count;
                    if (progress != null) progress(count);
                }
                if (ended) break;
            }
            if (received != expected) throw new IOException("Truncated range response");
            return received;
        }
    }

    internal static class ValidatedParallelDownload
    {
        // Called after exact range and ETag/PKG integrity checks, including resume identity.
        // Each transport checks its response before writing, and returns only after disk flush.
        internal static long Run(string source, string effective, string part, long total, string etag,
            int ranges, string titleId, string packageId, Action<long, long> progress, Func<bool> cancel,
            Action<long, long, Func<bool>, Action<int>> transfer, long existingBytes = 0, string headerIdentity = null)
        {
            using (var scope = TransferLaneBudget.Current == null ? TransferLaneBudget.Begin(ranges) : null)
            {
            var owner = TransferLaneBudget.Current;
            int n = DownloadTransferSettings.ClampRangeCount(ranges);
            if (n < 2 || total < 8L * 1024 * 1024 ||
                (!DownloadTransferSettings.StrongEtag(etag) && string.IsNullOrEmpty(headerIdentity)))
                throw new ArgumentException("Parallel range prerequisites missing");
            if (existingBytes < 0 || existingBytes > total) throw new ArgumentOutOfRangeException("existingBytes");
            var metadata = DownloadResumeInfo.Create(source, effective, etag, null, total, titleId, packageId);
            metadata.StrictIdentity = true;
            metadata.IntegrityHeader = headerIdentity ?? "";
            var needs = ParallelDownloadCheckpoint.SpanNeeds(total, n);
            var durable = new long[n]; var claimed = new long[n]; var errors = new Exception[n];
            var activeSpan = new int[n]; var activeStart = new long[n];
            long next = existingBytes, received = existingBytes; int nextSpan = 0;
            var gate = new object(); int failed = 0;
            var watch = Stopwatch.StartNew(); long reportedAt = -1000;
            Func<bool> stop = () => (cancel != null && cancel()) || Interlocked.CompareExchange(ref failed, 0, 0) != 0;
            if (existingBytes == 0) ParallelDownloadCheckpoint.DeleteAll(part);
            else if (!File.Exists(part) || new FileInfo(part).Length != existingBytes)
                throw new IOException("Partial file changed before range resume");
            for (int i = 0; i < n; i++)
            {
                durable[i] = claimed[i] = Math.Max(0, Math.Min(needs[i], existingBytes - ParallelDownloadCheckpoint.Boundary(total, i, n)));
                activeSpan[i] = -1;
            }
            metadata.Save(part);
            ParallelDownloadCheckpoint.SaveRangeMap(part, total, durable);
            if (!File.Exists(ParallelDownloadCheckpoint.RangeMapPath(part))) throw new IOException("Cannot save range checkpoint");
            using (var output = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) output.SetLength(total);
            var threads = new Thread[n];
            for (int i = 0; i < n; i++)
            {
                int index = i;
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        for (;;)
                        {
                            int span; long start, length;
                            using (owner.Acquire(stop))
                            {
                                lock (gate)
                                {
                                    if (next == total || failed != 0) break;
                                    while (next >= ParallelDownloadCheckpoint.Boundary(total, nextSpan + 1, n)) nextSpan++;
                                    span = nextSpan; start = next;
                                    length = Math.Min(16L * 1024 * 1024,
                                        ParallelDownloadCheckpoint.Boundary(total, span + 1, n) - start);
                                    activeSpan[index] = span; activeStart[index] = claimed[span];
                                    next += length; claimed[span] += length;
                                }
                                transfer(start, start + length - 1, stop, count =>
                                {
                                    lock (gate)
                                    {
                                        received += count;
                                        if (progress == null || watch.ElapsedMilliseconds - reportedAt < 100) return;
                                        reportedAt = watch.ElapsedMilliseconds;
                                        progress(Math.Min(received, total - 1), total);
                                    }
                                });
                            }
                            lock (gate)
                            {
                                activeSpan[index] = -1;
                                // Claims are ordered; only an outstanding chunk can leave
                                // a hole below claimed[span]. Failed chunks stay outstanding.
                                long prefix = claimed[span];
                                for (int lane = 0; lane < n; lane++)
                                    if (activeSpan[lane] == span) prefix = Math.Min(prefix, activeStart[lane]);
                                if (prefix > durable[span])
                                {
                                    durable[span] = prefix;
                                    ParallelDownloadCheckpoint.SaveRangeMap(part, total, durable);
                                }
                            }
                        }
                    }
                    catch (Exception ex) { errors[index] = ex; Interlocked.Exchange(ref failed, 1); }
                }) { IsBackground = true, Name = "validated-download-" + i };
                try { threads[i].Start(); }
                catch (Exception ex) { errors[i] = ex; threads[i] = null; Interlocked.Exchange(ref failed, 1); break; }
            }
            foreach (Thread thread in threads) if (thread != null) thread.Join();
            Exception failure = null;
            foreach (Exception error in errors)
                if (error != null && (failure == null || failure is OperationCanceledException || error is DownloadRangeRejectedException)) failure = error;
            // A rejected validator/range invalidates the complete attempt. Never retain mixed bytes.
            if (failure is DownloadRangeRejectedException)
            { ParallelDownloadCheckpoint.DeleteAll(part); throw failure; }
            long keep = ParallelDownloadCheckpoint.RetainedBytes(durable, needs);
            using (var output = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.None)) { output.SetLength(keep); output.Flush(true); }
            ParallelDownloadCheckpoint.DeleteRangeMap(part);
            metadata.Save(part);
            if (progress != null) progress(failure == null && (cancel == null || !cancel()) ? keep : Math.Min(keep, total - 1), total);
            if (cancel != null && cancel()) throw new OperationCanceledException("paused at " + keep + " bytes");
            if (failure != null) throw failure;
            if (keep != total) throw new IOException("Incomplete parallel transfer");
            return keep;
            }
        }
    }

    /// <summary>
    /// Converts completed range files into the largest durable contiguous prefix. The normal
    /// single-stream resume path can then continue from exactly that byte without trusting gaps.
    /// </summary>
    internal static class ParallelDownloadCheckpoint
    {
        public static string RangePath(string partPath, int index)
        {
            return partPath + ".p" + index;
        }

        public static void DeleteRangeFiles(string partPath)
        {
            for (int i = 0; i < DownloadTransferSettings.MaxRangeCount; i++)
            {
                try
                {
                    string path = RangePath(partPath, i);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch { }
            }
            try
            {
                string temp = partPath + ".checkpoint.tmp";
                if (File.Exists(temp)) File.Delete(temp);
            }
            catch { }
        }

        public static void DeleteAll(string partPath)
        {
            DeleteRangeFiles(partPath);
            DeleteRangeMap(partPath);
            DownloadResumeInfo.DeletePartial(partPath);
        }

        /// <summary>Range-completion sidecar for positioned parallel writes
        /// (SSPI-11/12). Completed spans survive pause/restart; interrupted spans
        /// are never marked complete and restart within their own span.</summary>
        public static string RangeMapPath(string partPath)
        {
            return partPath + ".ranges";
        }

        internal static long DurableBytes(string partPath)
        {
            if (!File.Exists(partPath)) return 0;
            if (partPath.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            {
                string destination = partPath.Substring(0, partPath.Length - 5);
                if (File.Exists(destination + ".map") || File.Exists(destination + ".xfer-lock"))
                    return TransferClient.DurableBytes(destination);
            }
            long length = new FileInfo(partPath).Length;
            if (!File.Exists(RangeMapPath(partPath))) return length;
            long total; int count;
            if (!TryPeekRangeMap(partPath, out total, out count) || count > DownloadTransferSettings.MaxRangeCount)
                return 0;
            long[] done = LoadRangeMap(partPath, total, count), needs = SpanNeeds(total, count);
            long verified = 0;
            for (int i = 0; i < count; i++)
            {
                if (done[i] > needs[i]) return 0;
                verified += done[i];
            }
            return Math.Min(length, verified);
        }

        public static void DeleteRangeMap(string partPath)
        {
            try
            {
                string path = RangeMapPath(partPath);
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            }
            catch { }
        }

        public static long[] LoadRangeMap(string partPath, long total, int n)
        {
            var empty = new long[Math.Max(0, n)];
            try
            {
                string[] lines = ReadRangeMapLines(partPath);
                if (lines.Length != 3 + n || lines[0] != "1") return empty;
                long savedTotal;
                int savedN;
                if (!long.TryParse(lines[1], out savedTotal) || savedTotal != total ||
                    !int.TryParse(lines[2], out savedN) || savedN != n)
                    return empty;
                for (int i = 0; i < n; i++)
                    if (!long.TryParse(lines[3 + i], out empty[i]) || empty[i] < 0) return new long[n];
                return empty;
            }
            catch { return new long[Math.Max(0, n)]; }
        }

        public static void SaveRangeMap(string partPath, long total, long[] done)
        {
            try
            {
                string path = RangeMapPath(partPath);
                var body = new System.Text.StringBuilder();
                body.Append("1\n").Append(total).Append('\n').Append(done.Length).Append('\n');
                foreach (long d in done) body.Append(Math.Max(0, d)).Append('\n');
                AtomicFile.WriteText(path, body.ToString());
            }
            catch { }
        }

        public static long[] SpanNeeds(long total, int rangeCount)
        {
            int n = DownloadTransferSettings.ClampRangeCount(rangeCount);
            var needs = new long[n];
            for (int i = 0; i < n; i++)
            {
                long s = Boundary(total, i, n), e = Boundary(total, i + 1, n) - 1;
                needs[i] = e - s + 1;
            }
            return needs;
        }

        internal static long Boundary(long total, int index, int count)
        { return (total / count) * index + ((total % count) * index) / count; }

        internal static long RecoverPositionedPart(string partPath, DownloadResumeInfo resume)
        {
            long total; int n;
            if (!TryPeekRangeMap(partPath, out total, out n) || n > DownloadTransferSettings.MaxRangeCount ||
                (resume != null && resume.Total != total))
                throw new IOException(DownloadResumeInfo.RestartRequired("saved range checkpoint is invalid"));
            long keep = RetainedBytes(LoadRangeMap(partPath, total, n), SpanNeeds(total, n));
            using (var output = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                if (keep > output.Length) throw new IOException(DownloadResumeInfo.RestartRequired("saved range data is incomplete"));
                output.SetLength(keep);
                output.Flush(true);
            }
            // Keep the map if a required identity sidecar is missing. Never adopt unknown bytes.
            if (keep > 0 && resume == null) throw new IOException(DownloadResumeInfo.RestartRequired("saved partial metadata is missing"));
            DeleteRangeMap(partPath);
            return keep;
        }

        public static bool TryPeekRangeMap(string partPath, out long total, out int n)
        {
            total = 0; n = 0;
            try
            {
                string[] lines = ReadRangeMapLines(partPath);
                if (lines.Length < 3 || lines[0] != "1") return false;
                if (!long.TryParse(lines[1], out total) || total <= 0) return false;
                if (!int.TryParse(lines[2], out n) || n <= 0 || n > 64) return false;
                return true;
            }
            catch { return false; }
        }

        static string[] ReadRangeMapLines(string partPath)
        {
            // A reader must not prevent the writer from atomically replacing its checkpoint.
            using (var input = new FileStream(RangeMapPath(partPath), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(input))
            {
                var buffer = new char[4097];
                int count = 0, read;
                while (count < buffer.Length && (read = reader.Read(buffer, count, buffer.Length - count)) > 0)
                    count += read;
                if (count == buffer.Length) throw new IOException("Range checkpoint exceeds size limit");
                var lines = new List<string>();
                using (var text = new StringReader(new string(buffer, 0, count)))
                {
                    string line;
                    while ((line = text.ReadLine()) != null) lines.Add(line);
                }
                return lines.ToArray();
            }
        }

        public static long ContiguousPrefix(long[] done, long[] needs)
        {
            long prefix = 0;
            for (int i = 0; i < Math.Min(done.Length, needs.Length); i++)
            {
                if (done[i] < needs[i]) break;
                prefix += needs[i];
            }
            return prefix;
        }

        /// <summary>Bytes safe to keep on pause/error: the contiguous prefix plus
        /// the first incomplete span's partial bytes (still contiguous on disk).
        /// Interrupted spans are never marked complete and restart whole.</summary>
        public static long RetainedBytes(long[] done, long[] needs)
        {
            long keep = 0;
            int n = Math.Min(done.Length, needs.Length);
            for (int i = 0; i < n; i++)
            {
                if (done[i] >= needs[i]) keep += needs[i];
                else
                {
                    keep += Math.Max(0, Math.Min(done[i], needs[i]));
                    break;
                }
            }
            return keep;
        }

        /// <summary>Truncates a positioned .part to its largest contiguous completed
        /// prefix so single-stream resume never appends after holes. Returns prefix.</summary>
        public static long TruncateToContiguousPrefix(string partPath, long total, int n, long[] needs)
        {
            long[] done = LoadRangeMap(partPath, total, n);
            long prefix = ContiguousPrefix(done, needs);
            try
            {
                if (File.Exists(partPath))
                {
                    using (var fs = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.None))
                        if (fs.Length != prefix) fs.SetLength(prefix);
                }
            }
            catch { }
            DeleteRangeMap(partPath);
            return prefix;
        }

        public static long PreserveContiguousPrefix(string partPath, long[] expectedRangeBytes,
            string sourceUrl, long total, string expectedTitleId, string expectedPackageId = null)
        {
            if (expectedRangeBytes == null || expectedRangeBytes.Length == 0 || total <= 0)
                return 0;

            string temp = partPath + ".checkpoint.tmp";
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }

            long prefix = 0;
            byte[] buffer = new byte[1024 * 1024];
            using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write,
                FileShare.None, buffer.Length))
            {
                for (int i = 0; i < expectedRangeBytes.Length; i++)
                {
                    string rangePath = RangePath(partPath, i);
                    if (!File.Exists(rangePath)) break;
                    long available = new FileInfo(rangePath).Length;
                    long expected = expectedRangeBytes[i];
                    if (available < 0 || available > expected)
                        throw new IOException("parallel range " + i + " has invalid size " +
                            available + " (expected at most " + expected + ")");
                    if (available == 0) break;

                    using (var input = new FileStream(rangePath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, buffer.Length))
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            output.Write(buffer, 0, read);
                            prefix += read;
                        }
                    }
                    if (available != expected) break;
                }
                output.Flush();
            }

            if (prefix <= 0)
            {
                try { File.Delete(temp); } catch { }
                DeleteRangeFiles(partPath);
                return 0;
            }
            if (prefix > total)
                throw new IOException("parallel checkpoint " + prefix + " exceeds total " + total);

            if (File.Exists(partPath)) File.Delete(partPath);
            File.Move(temp, partPath);
            DownloadResumeInfo.Create(sourceUrl, sourceUrl, null, null, total,
                expectedTitleId, expectedPackageId).Save(partPath);
            DeleteRangeFiles(partPath);
            return prefix;
        }
    }
}
