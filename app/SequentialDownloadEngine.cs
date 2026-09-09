using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Orbis
{
    // One HTTP response, one sequential disk writer, a bounded 2 MiB handoff.
    // Read-ahead overlaps network latency with HDD writes without sparse files or seek storms.
    internal static class SequentialDownloadEngine
    {
        static long _limit;
        public static long LimitBytesPerSecond
        {
            get { return Interlocked.Read(ref _limit); }
            set { Interlocked.Exchange(ref _limit, Math.Max(0, value)); }
        }

        public static void LoadLimit(string settingsPath)
        {
            long limit = 0;
            try
            {
                foreach (string line in File.ReadAllLines(settingsPath))
                {
                    if (!line.StartsWith("download_limit_mb_s=", StringComparison.Ordinal)) continue;
                    int mb;
                    if (int.TryParse(line.Substring(20).Trim(), out mb)) limit = Math.Max(0, Math.Min(1000, mb)) * 1000000L;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            LimitBytesPerSecond = limit;
        }

        sealed class Block
        {
            public readonly byte[] Data = new byte[512 * 1024];
            public int Count;
        }

        public static long Copy(Func<byte[], int> read, Stream output, long expectedBytes,
            long offset, long total, Action<long, long> progress, Func<bool> cancel)
        {
            return CopyCore((buffer, start, count) => read(buffer), false, output,
                expectedBytes, offset, total, progress, cancel, null);
        }

        public static long Copy(Func<byte[], int, int, int> read, Stream output, long expectedBytes,
            long offset, long total, Action<long, long> progress, Func<bool> cancel, Action<string> diagnostic = null)
        {
            return CopyCore(read, true, output, expectedBytes, offset, total, progress, cancel, diagnostic);
        }

        static long CopyCore(Func<byte[], int, int, int> read, bool coalesce, Stream output, long expectedBytes,
            long offset, long total, Action<long, long> progress, Func<bool> cancel, Action<string> diagnostic)
        {
            using (var free = new BlockingCollection<Block>(4))
            using (var pending = new BlockingCollection<Block>(4))
            using (var stopped = new ManualResetEvent(false))
            {
                for (int i = 0; i < 4; i++) free.Add(new Block());
                Exception writerError = null;
                long written = 0, writeTicks = 0, readTicks = 0, readCalls = 0;
                var writer = new Thread(() =>
                {
                    try
                    {
                        foreach (var block in pending.GetConsumingEnumerable())
                        {
                            if (stopped.WaitOne(0)) break;
                            long writeAt = Stopwatch.GetTimestamp();
                            output.Write(block.Data, 0, block.Count);
                            Interlocked.Add(ref writeTicks, Stopwatch.GetTimestamp() - writeAt);
                            Interlocked.Add(ref written, block.Count);
                            free.Add(block);
                        }
                        output.Flush();
                    }
                    catch (Exception ex) { writerError = ex; stopped.Set(); }
                }) { IsBackground = true, Name = "SSPI sequential disk writer" };
                writer.Start();
                var watch = Stopwatch.StartNew();
                long received = 0, lastProgress = -100, lastDiagnostic = 0;
                double budget = 0;
                long budgetAt = 0;
                try
                {
                    for (;;)
                    {
                        if (writerError != null) throw new IOException("Download storage failed", writerError);
                        if (cancel != null && cancel()) throw new OperationCanceledException("paused");
                        Block block;
                        if (!free.TryTake(out block, 25)) continue;
                        int n = 0;
                        // Fill the bounded block: a TLS record is usually only 16 KiB.
                        // Publishing each record reduced the effective read-ahead to 64 KiB.
                        do
                        {
                            if (writerError != null) throw new IOException("Download storage failed", writerError);
                            if (cancel != null && cancel()) throw new OperationCanceledException("paused");
                            long readAt = Stopwatch.GetTimestamp();
                            int got = read(block.Data, n, block.Data.Length - n);
                            readTicks += Stopwatch.GetTimestamp() - readAt;
                            readCalls++;
                            if (got < 0 || got > block.Data.Length - n) throw new IOException("Invalid download read length");
                            if (got == 0) break;
                            n += got;
                            if (expectedBytes >= 0 && received + n > expectedBytes)
                                throw new IOException("Response exceeds its declared length");
                        } while (coalesce && n < block.Data.Length);
                        if (n == 0) { free.Add(block); break; }
                        received = checked(received + n);
                        if (expectedBytes >= 0 && received > expectedBytes)
                            throw new IOException("Response exceeds its declared length");
                        block.Count = n;
                        while (!pending.TryAdd(block, 25))
                        {
                            if (writerError != null) throw new IOException("Download storage failed", writerError);
                            if (cancel != null && cancel()) throw new OperationCanceledException("paused");
                        }
                        long limit = LimitBytesPerSecond;
                        if (limit > 0)
                        {
                            long now = watch.ElapsedMilliseconds;
                            budget = Math.Max(0, budget - (now - budgetAt) * limit / 1000.0) + n;
                            budgetAt = now;
                            while (budget > limit * .1)
                            {
                                if (cancel != null && cancel()) throw new OperationCanceledException("paused");
                                if (writerError != null) throw new IOException("Download storage failed", writerError);
                                if (LimitBytesPerSecond != limit) { budget = 0; break; }
                                Thread.Sleep((int)Math.Max(1, Math.Min(25, budget * 1000 / limit)));
                                now = watch.ElapsedMilliseconds;
                                budget = Math.Max(0, budget - (now - budgetAt) * limit / 1000.0);
                                budgetAt = now;
                            }
                        }
                        else { budget = 0; budgetAt = watch.ElapsedMilliseconds; }
                        if (progress != null && watch.ElapsedMilliseconds - lastProgress >= 100)
                        {
                            progress(offset + Interlocked.Read(ref written), total);
                            lastProgress = watch.ElapsedMilliseconds;
                        }
                        if (diagnostic != null && watch.ElapsedMilliseconds - lastDiagnostic >= 5000)
                        {
                            try { diagnostic("elapsed_ms=" + watch.ElapsedMilliseconds + "\nbytes_written=" + Interlocked.Read(ref written) +
                                "\nread_calls=" + readCalls + "\nread_ms=" + readTicks * 1000 / Stopwatch.Frequency +
                                "\nwrite_ms=" + Interlocked.Read(ref writeTicks) * 1000 / Stopwatch.Frequency +
                                "\nlimit_bytes_s=" + LimitBytesPerSecond + "\nblock_bytes=524288\nbuffer_count=4\n"); }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                            lastDiagnostic = watch.ElapsedMilliseconds;
                        }
                    }
                    pending.CompleteAdding();
                    writer.Join();
                    if (writerError != null) throw new IOException("Download storage failed", writerError);
                    if (cancel != null && cancel()) throw new OperationCanceledException("paused");
                    if (expectedBytes >= 0 && written != expectedBytes) throw new IOException("Truncated download response");
                    if (progress != null) progress(offset + written, total);
                    return written;
                }
                finally
                {
                    stopped.Set();
                    if (!pending.IsAddingCompleted) pending.CompleteAdding();
                    // Never close the file or return buffers while the writer still owns them.
                    writer.Join();
                }
            }
        }
    }
}
