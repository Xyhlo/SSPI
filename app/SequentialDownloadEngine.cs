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
            using (var free = new BlockingCollection<Block>(4))
            using (var pending = new BlockingCollection<Block>(4))
            using (var stopped = new ManualResetEvent(false))
            {
                for (int i = 0; i < 4; i++) free.Add(new Block());
                Exception writerError = null;
                long written = 0;
                var writer = new Thread(() =>
                {
                    try
                    {
                        foreach (var block in pending.GetConsumingEnumerable())
                        {
                            if (stopped.WaitOne(0)) break;
                            output.Write(block.Data, 0, block.Count);
                            Interlocked.Add(ref written, block.Count);
                            free.Add(block);
                        }
                        output.Flush();
                    }
                    catch (Exception ex) { writerError = ex; stopped.Set(); }
                }) { IsBackground = true, Name = "SSPI sequential disk writer" };
                writer.Start();
                var watch = Stopwatch.StartNew();
                long received = 0, lastProgress = -100;
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
                        int n = read(block.Data);
                        if (n < 0 || n > block.Data.Length) throw new IOException("Invalid download read length");
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
