using System;
using System.IO;

namespace Orbis
{
    internal static class DownloadTransferSettings
    {
        public const int MinRangeCount = 1;
        public const int MaxRangeCount = 7;
        public const int DefaultRangeCount = 1;

        public static int ClampRangeCount(int value)
        {
            if (value < MinRangeCount) return MinRangeCount;
            if (value > MaxRangeCount) return MaxRangeCount;
            return value;
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
                string[] lines = File.ReadAllLines(RangeMapPath(partPath));
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
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, body.ToString());
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch { }
        }

        public static long[] SpanNeeds(long total, int rangeCount)
        {
            int n = DownloadTransferSettings.ClampRangeCount(rangeCount);
            var needs = new long[n];
            for (int i = 0; i < n; i++)
            {
                long s = total * i / n, e = (total * (i + 1) / n) - 1;
                needs[i] = e - s + 1;
            }
            return needs;
        }

        public static bool TryPeekRangeMap(string partPath, out long total, out int n)
        {
            total = 0; n = 0;
            try
            {
                string[] lines = File.ReadAllLines(RangeMapPath(partPath));
                if (lines.Length < 3 || lines[0] != "1") return false;
                if (!long.TryParse(lines[1], out total) || total <= 0) return false;
                if (!int.TryParse(lines[2], out n) || n <= 0 || n > 64) return false;
                return true;
            }
            catch { return false; }
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
