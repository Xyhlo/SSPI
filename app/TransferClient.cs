using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Orbis
{
    internal static class TransferClient
    {
        internal const int ApiLevel = 3;
        static readonly object Gate = new object();
        static bool ready;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        struct Status
        {
            internal int State, Lanes, Retries, ErrorCode;
            internal long Done, Total, NetworkBytes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Error;
        }
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ApiFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitFn(int context);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        delegate int StartFn(string url, string bearer, string destination, string title, string content, string sha, int lanes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int PollFn(int handle, out Status status);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int ControlFn(int handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] delegate long DurableFn(string path);
        static StartFn start;
        static PollFn poll;
        static ControlFn pause, destroy;
        static DurableFn durable;
        static T Bind<T>(int module, string name) where T : class
        {
            IntPtr pointer;
            if (sceKernelDlsym(module, name, out pointer) != 0 || pointer == IntPtr.Zero)
                throw new IOException("Native transfer entry unavailable: " + name);
            return Marshal.GetDelegateForFunctionPointer(pointer, typeof(T)) as T;
        }
        static void EnsureReady()
        {
            lock (Gate)
            {
                if (ready) return;
                int context = NativeHttp.TransferContext;
                if (context < 0) throw new IOException("Firmware HTTP unavailable: " + NativeHttp.InitDetail);
                string root = Orbis.Internals.IO.GetAppBaseDirectory();
                if (string.IsNullOrEmpty(root)) root = "/app0";
                int module = Orbis.Internals.Kernel.TryLoadStartModule(Path.Combine(root, "sce_module", "libSspiTransfer.prx"));
                if (module < 0) throw new IOException("Native transfer module load failed: 0x" + unchecked((uint)module).ToString("X8"));
                if (Bind<ApiFn>(module, "sspi_xfer_api")() != ApiLevel) throw new IOException("Native transfer API mismatch");
                start = Bind<StartFn>(module, "sspi_xfer_start");
                poll = Bind<PollFn>(module, "sspi_xfer_poll");
                pause = Bind<ControlFn>(module, "sspi_xfer_pause");
                destroy = Bind<ControlFn>(module, "sspi_xfer_destroy");
                durable = Bind<DurableFn>(module, "sspi_xfer_durable");
                int result = Bind<InitFn>(module, "sspi_xfer_init")(context);
                if (result != 0) throw new IOException("Native transfer initialization failed: 0x" + unchecked((uint)result).ToString("X8"));
                ready = true;
            }
        }
        internal static long DurableBytes(string destination)
        {
            try
            {
                string path = destination + ".map";
                if (!File.Exists(path)) return 0;
                long length = new FileInfo(path).Length;
                if (length < 96 || length > 96 + 65536 * 40) return 0;
                byte[] bytes = File.ReadAllBytes(path);
                uint count = BitConverter.ToUInt32(bytes, 12);
                ulong total = BitConverter.ToUInt64(bytes, 16);
                const ulong chunk = 16777216;
                if (BitConverter.ToUInt32(bytes, 0) != 0x33585347 || BitConverter.ToUInt32(bytes, 4) != 3 ||
                    BitConverter.ToUInt32(bytes, 8) != chunk || count == 0 || count > 65536 ||
                    total == 0 || total > 1099511627776UL || count != (total + chunk - 1) / chunk ||
                    bytes.Length != 96 + count * 40) return 0;
                uint expected = BitConverter.ToUInt32(bytes, 92);
                Array.Clear(bytes, 92, 4);
                uint crc = 0xffffffff;
                foreach (byte value in bytes)
                {
                    crc ^= value;
                    for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320U : 0U);
                }
                if (~crc != expected) return 0;
                ulong done = 0;
                for (uint i = 0; i < count; i++)
                    if (bytes[96 + i * 40] != 0) done += Math.Min(chunk, total - i * chunk);
                return (long)done;
            }
            catch { return 0; }
        }
        internal static long Download(string url, string destination, Action<long,long> progress,
            Func<bool> canceled, string bearer, string title, string content, string sha, int lanes,
            Action<long,long,long> telemetry = null, Action<string> phase = null)
        {
            EnsureReady();
            int handle = start(url, bearer ?? "", destination, title ?? "", content ?? "", sha ?? "",
                DownloadTransferSettings.ConnectionsFor(url, lanes));
            if (handle < 0) throw new IOException("Native transfer could not start: " + handle);
            try
            {
                for (;;)
                {
                    Status state;
                    if (poll(handle, out state) != 0) throw new IOException("Native transfer status unavailable");
                    if (canceled != null && canceled()) { pause(handle); throw new OperationCanceledException(); }
                    if (telemetry != null) telemetry(state.Done, state.Total, state.NetworkBytes);
                    else if (progress != null) progress(state.Done, state.Total);
                    if (state.State == 4 && phase != null) phase(state.Error);
                    if (state.State == 5)
                    {
                        // Release native file/ownership handles before publishing the file.
                        if (destroy(handle) != 0) throw new IOException("Native transfer still owns the verified file");
                        handle = -1;
                        File.Move(destination + ".part", destination);
                        if (!string.IsNullOrEmpty(sha)) PkgIntegrity.RememberVerifiedSha256(destination, sha);
                        try { File.Delete(destination + ".map"); } catch { }
                        return state.Total;
                    }
                    if (state.State == 6)
                    {
                        if (state.ErrorCode >= 400 && state.ErrorCode <= 599)
                            throw new DownloadHttpException(state.ErrorCode, state.Error);
                        throw new IOException(state.Error);
                    }
                    if (state.State == 7) throw new OperationCanceledException();
                    Thread.Sleep(250);
                }
            }
            finally
            {
                if (handle >= 0)
                {
                    pause(handle);
                    // Abort wakes active reads. Never release ownership while a writer lives.
                    while (destroy(handle) == -2) Thread.Sleep(50);
                }
            }
        }
        internal static bool HasCompletedJournal(string destination)
        {
            try { return File.Exists(destination + ".part") && new FileInfo(destination + ".part").Length >= 4096 &&
                DurableBytes(destination) == new FileInfo(destination + ".part").Length; }
            catch { return false; }
        }

        internal static bool TryPublishCompleted(string destination, string title, string kind, string content,
            string sha, long expectedSize, Func<bool> cancel, Action<string> phase)
        {
            string part = destination + ".part", map = destination + ".map";
            if (File.Exists(destination) || !File.Exists(part) || !File.Exists(map)) return false;
            long size = new FileInfo(part).Length;
            if (size < 4096 || DurableBytes(destination) != size) return false;
            byte[] journal = File.ReadAllBytes(map);
            if (journal.Length < 96 || BitConverter.ToUInt64(journal, 16) != (ulong)size) return false;
            bool journalHasSha = false;
            for (int i = 56; i < 88; i++) journalHasSha |= journal[i] != 0;
            if (journalHasSha)
            {
                string savedSha = BitConverter.ToString(journal, 56, 32).Replace("-", "").ToLowerInvariant();
                if (!string.IsNullOrEmpty(sha) && !string.Equals(savedSha, sha, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Completed file retained: expected SHA-256 identity changed");
                sha = savedSha;
            }
            if (cancel != null && cancel()) throw new OperationCanceledException();
            if (phase != null) phase("Recovering completed file locally...");
            // A complete journal proves all chunks were committed. Bind that
            // journal to the retained header before any provider/API request.
            bool isPkg = false;
            using (var file = File.OpenRead(part))
            using (var hash = PkgIntegrity.CreateSha256())
            {
                var header = new byte[4096];int at = 0;
                while (at < header.Length) { int n=file.Read(header,at,header.Length-at);if(n<=0)throw new IOException("Completed file retained: header read failed");at+=n; }
                isPkg = header[0] == 0x7f && header[1] == 0x43 && header[2] == 0x4e && header[3] == 0x54;
                byte[] actual = hash.ComputeHash(header);
                for (int i=0;i<32;i++) if(actual[i]!=journal[24+i])
                    throw new IOException("Completed file retained: journal/header identity mismatch");
            }
            if (expectedSize > 0 && size != expectedSize) throw new IOException("Completed file retained: expected size mismatch");
            if (isPkg)
            {
                PkgValResult result;string error, actualContent;
                if (!PkgValidator.TryValidateDownload(part,title,kind,out result,out error))
                    throw new IOException("Completed PKG retained: " + error);
                if (!string.IsNullOrEmpty(content) && (!PkgValidator.TryGetContentId(part,out actualContent) ||
                    !string.Equals(content,actualContent,StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("Completed PKG retained: content ID mismatch");
            }
            if (!string.IsNullOrEmpty(sha))
            {
                string error;
                if(!PkgIntegrity.VerifyFile(part,sha,phase,cancel,out error))throw new IOException("Completed file retained: " + error);
            }
            if (cancel != null && cancel()) throw new OperationCanceledException();
            File.Move(part,destination);
            if(!string.IsNullOrEmpty(sha))PkgIntegrity.RememberVerifiedSha256(destination,sha);
            try { File.Delete(map); } catch { }
            return true;
        }
        [DllImport("libkernel", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceKernelDlsym(int module, string name, out IntPtr symbol);
    }
    // Allocated only for an active native transfer. No timer, task, file I/O or
    // per-sample allocation: 33 samples cover eight seconds at four updates/s.
    internal sealed class LiveTransferMeter
    {
        readonly long[] times = new long[33], wire = new long[33], useful = new long[33];
        readonly double[] rates = new double[64];
        int rateHead, rateCount;
        int head, count, attempt;
        string phase;
        long observedWire, observedDone, observedAt, advancedAt, rollbackAt;
        internal double Rate;
        internal int Eta;
        internal long AdvancedAt { get { return advancedAt; } }
        internal double[] CopyRates()
        {
            var result = new double[rateCount];
            for (int n = 0; n < rateCount; n++)
                result[n] = rates[(rateHead - rateCount + n + rates.Length) % rates.Length];
            return result;
        }
        internal void Sample(long bytes, long done, long total, string currentPhase, int currentAttempt, long now)
        {
            bytes = Math.Max(0, bytes);done = Math.Max(0, done);now = Math.Max(0, now);
            bool reset = count == 0 || bytes < observedWire || now < observedAt || now - observedAt > 5000 ||
                currentAttempt != attempt || currentPhase != phase;
            if (reset)
            {
                head = count = rateHead = rateCount = 0;Rate = 0;Eta = 0;advancedAt = rollbackAt = now;
                attempt = currentAttempt;phase = currentPhase;
            }
            else if (bytes > observedWire) advancedAt = now;
            if (!reset && done < observedDone) { rollbackAt = now;Eta = 0; }
            observedWire = bytes;observedDone = done;observedAt = now;
            if (count != 0 && now - times[head] < 200) return;
            if (count != 0) head = (head + 1) % times.Length;
            times[head] = now;wire[head] = bytes;useful[head] = done;
            if (count < times.Length) count++;
            int speed = head, eta = head;
            for (int n = 1; n < count; n++)
            {
                int i = (head - n + times.Length) % times.Length;
                if (now - times[i] <= 2000) speed = i;
                if (now - times[i] <= 8000 && times[i] >= rollbackAt) eta = i;
            }
            long elapsed = now - times[speed];
            Rate = elapsed >= 500 && now - advancedAt < 2000 ? (bytes - wire[speed]) * 1000.0 / elapsed : 0;
            long etaElapsed = now - times[eta], gained = done - useful[eta];
            Eta = 0;
            if (Rate > 0 && etaElapsed >= 2000 && gained > 0 && total > done)
            {
                double seconds = Math.Ceiling((total - done) / (gained * 1000.0 / etaElapsed));
                Eta = seconds >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)seconds);
            }
            if (total > 0 && done >= total) { Rate = 0;Eta = 0; }
            rates[rateHead] = Rate;rateHead = (rateHead + 1) % rates.Length;
            if (rateCount < rates.Length) rateCount++;
        }
    }

}
