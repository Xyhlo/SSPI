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
        static int _nextDiagnosticId;

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

        // At most one pending snapshot; slow diagnostic storage cannot hold up network reads.
        sealed class DiagnosticWriter
        {
            readonly Action<string> _write;
            readonly object _gate = new object();
            string _latest;
            bool _running;
            public DiagnosticWriter(Action<string> write) { _write = write; }
            public void Submit(string snapshot)
            {
                if (_write == null) return;
                lock (_gate)
                {
                    _latest = snapshot;
                    if (_running) return;
                    _running = true;
                }
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    for (;;)
                    {
                        string value;
                        lock (_gate)
                        {
                            value = _latest; _latest = null;
                            if (value == null) { _running = false; return; }
                        }
                        try { _write(value); } catch (Exception) { }
                    }
                });
            }
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
                long written = 0, writeTicks = 0, readTicks = 0, readCalls = 0, flushTicks = 0;
                long bufferWaitTicks = 0;
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
                        long flushAt = Stopwatch.GetTimestamp();
                        output.Flush();
                        Interlocked.Add(ref flushTicks, Stopwatch.GetTimestamp() - flushAt);
                    }
                    catch (Exception ex) { writerError = ex; stopped.Set(); }
                }) { IsBackground = true, Name = "SSPI sequential disk writer" };
                writer.Start();
                var watch = Stopwatch.StartNew();
                long received = 0, lastProgress = -100, lastDiagnostic = 0;
                bool complete = false;
                string outcome = "failed";
                int diagnosticId = Interlocked.Increment(ref _nextDiagnosticId);
                var diagnostics = new DiagnosticWriter(diagnostic);
                Action<string> sample = phase =>
                {
                    if (diagnostic == null) return;
                    long committed = Interlocked.Read(ref written), elapsedMs = Math.Max(1, watch.ElapsedMilliseconds);
                    long readMs = readTicks * 1000 / Stopwatch.Frequency, writeMs = Interlocked.Read(ref writeTicks) * 1000 / Stopwatch.Frequency;
                    diagnostics.Submit("sample_utc=" + DateTime.UtcNow.ToString("O") + "\ncopy_id=" + diagnosticId + "\nphase=" + phase +
                        "\nelapsed_ms=" + elapsedMs + "\nresume_offset=" + offset + "\nbytes_written=" + committed +
                        "\nbytes_received=" + received + "\nthroughput_bytes_s=" + (long)(committed * 1000.0 / elapsedMs) +
                        "\nread_calls=" + readCalls + "\nread_ms=" + readMs + "\nwrite_ms=" + writeMs +
                        "\nflush_ms=" + Interlocked.Read(ref flushTicks) * 1000 / Stopwatch.Frequency +
                        "\nbuffer_wait_ms=" + bufferWaitTicks * 1000 / Stopwatch.Frequency +
                        "\nread_active_bytes_s=" + (readMs > 0 ? (long)(received * 1000.0 / readMs) : 0) +
                        "\nwrite_active_bytes_s=" + (writeMs > 0 ? (long)(committed * 1000.0 / writeMs) : 0) +
                        "\nblock_bytes=524288\nbuffer_count=4\n");
                };
                sample("starting");
                try
                {
                    for (;;)
                    {
                        if (writerError != null) throw new IOException("Download storage failed", writerError);
                        if (cancel != null && cancel()) throw new OperationCanceledException("paused");
                        Block block;
                        long waitAt = Stopwatch.GetTimestamp();
                        bool gotBlock = free.TryTake(out block, 25);
                        bufferWaitTicks += Stopwatch.GetTimestamp() - waitAt;
                        if (!gotBlock) continue;
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
                        if (progress != null && watch.ElapsedMilliseconds - lastProgress >= 100)
                        {
                            progress(offset + Interlocked.Read(ref written), total);
                            lastProgress = watch.ElapsedMilliseconds;
                        }
                        if (diagnostic != null && watch.ElapsedMilliseconds - lastDiagnostic >= 5000)
                        {
                            sample("running");
                            lastDiagnostic = watch.ElapsedMilliseconds;
                        }
                    }
                    pending.CompleteAdding();
                    writer.Join();
                    if (writerError != null) throw new IOException("Download storage failed", writerError);
                    if (cancel != null && cancel()) throw new OperationCanceledException("paused");
                    if (expectedBytes >= 0 && written != expectedBytes) throw new IOException("Truncated download response");
                    if (progress != null) progress(offset + written, total);
                    complete = true;
                    return written;
                }
                catch (OperationCanceledException) { outcome = "canceled"; throw; }
                finally
                {
                    stopped.Set();
                    if (!pending.IsAddingCompleted) pending.CompleteAdding();
                    // Never close the file or return buffers while the writer still owns them.
                    writer.Join();
                    sample(complete ? "complete" : outcome);
                }
            }
        }
    }
}
