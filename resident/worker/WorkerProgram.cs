using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal sealed class ResidentJob
    {
        public string Id;
        public string Url;
        public string Destination;
        public string TitleId;
        public string ExpectedSha256;
        public string ContentId;
        public long Total;
        public int RangeCount;

        public static ResidentJob Parse(string[] lines)
        {
            long total;
            int rangeCount;
            if (lines == null || lines.Length != 9 || lines[0] != "2" ||
                !long.TryParse(lines[7], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out total) ||
                !int.TryParse(lines[8], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out rangeCount)) return null;
            return new ResidentJob
            {
                Id = Decode(lines[1]),
                Url = Decode(lines[2]),
                Destination = Decode(lines[3]),
                TitleId = Decode(lines[4]),
                ExpectedSha256 = Decode(lines[5]),
                ContentId = Decode(lines[6]),
                Total = total,
                RangeCount = DownloadTransferSettings.ClampRangeCount(rangeCount)
            };
        }

        static string Decode(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? ""));
        }
    }

    internal sealed class ResidentBgftAttachment
    {
        public string Id;
        public int TaskId;

        public static ResidentBgftAttachment Parse(string[] lines)
        {
            int taskId;
            if (lines == null || lines.Length != 3 || lines[0] != "1" ||
                !int.TryParse(lines[2], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out taskId) || taskId < 0) return null;
            try
            {
                return new ResidentBgftAttachment
                {
                    Id = Encoding.UTF8.GetString(Convert.FromBase64String(lines[1] ?? "")),
                    TaskId = taskId
                };
            }
            catch { return null; }
        }
    }

    public static class Program
    {
        const string Version = "4.43";
        const string DataRoot = "/data/GameSearch";
        static readonly string IpcRoot = Path.Combine(DataRoot, "resident");
        static readonly string JobPath = Path.Combine(IpcRoot, "job.txt");
        static readonly string StatusPath = Path.Combine(IpcRoot, "status.txt");
        static readonly string ControlPath = Path.Combine(IpcRoot, "control.txt");
        static readonly string HeartbeatPath = Path.Combine(IpcRoot, "heartbeat.txt");
        static readonly string BgftPath = Path.Combine(IpcRoot, "bgft.txt");
        static readonly object Gate = new object();
        static readonly LoopbackPkgServer Server = new LoopbackPkgServer();
        static ResidentJob _job;
        static Thread _feeder;
        static volatile bool _stop;
        static volatile bool _paused;
        static volatile bool _release;
        static Exception _feederError;
        static long _done;
        static string _state = "idle";
        static string _error = "";
        static int _bgftTaskId = -1;
        static long _lastStatusTicks;
        static long _lastHeartbeatTicks;

        public static void Main()
        {
            Directory.CreateDirectory(IpcRoot);
            try
            {
                int registered = sceSystemServiceRegisterDaemon();
                try
                {
                    File.WriteAllText(Path.Combine(IpcRoot, "boot-managed.txt"),
                        Version + "\nregister=0x" +
                        unchecked((uint)registered).ToString("X") + "\n");
                }
                catch { }
            }
            catch { }
            WriteHeartbeat();
            while (true)
            {
                try
                {
                    WriteHeartbeat();
                    if (_job == null)
                    {
                        ResidentJob next = ReadJob();
                        if (next != null) Start(next);
                    }
                    else
                    {
                        ReadControl();
                        ReadBgftTask();
                        if (_feeder != null && !_feeder.IsAlive) FinishFeeder();
                        else if (_feeder == null && _state == "awaiting-bgft") StartBgftIfReady();
                        WriteStatus(false);
                    }
                }
                catch (Exception ex)
                {
                    Fail(ex.Message);
                }
                Thread.Sleep(100);
            }
        }

        public static int SelfTest()
        {
            string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes("queue one"));
            ResidentJob parsed = ResidentJob.Parse(new[]
            {
                "2", encoded,
                Convert.ToBase64String(Encoding.UTF8.GetBytes("https://example.invalid/a.pkg")),
                Convert.ToBase64String(Encoding.UTF8.GetBytes("/data/GameSearch/downloads/a.pkg")),
                Convert.ToBase64String(Encoding.UTF8.GetBytes("CUSA00001")), "", "", "1072", "7"
            });
            ResidentBgftAttachment attachment = ResidentBgftAttachment.Parse(new[]
            {
                "1", encoded, "42"
            });
            return parsed != null && parsed.Id == "queue one" && parsed.Total == 1072 &&
                parsed.RangeCount == 7 && attachment != null && attachment.TaskId == 42 &&
                OwnedDestination(parsed.Destination) ? 0 : 1;
        }

        static void Start(ResidentJob job)
        {
            if (string.IsNullOrWhiteSpace(job.Id) || string.IsNullOrWhiteSpace(job.Url) ||
                !OwnedDestination(job.Destination) || job.Total < LoopbackPkgFeeder.HeaderBytes)
            {
                _job = job;
                Fail("Resident job metadata is invalid");
                return;
            }

            _job = job;
            _stop = false;
            _paused = false;
            _release = false;
            _feederError = null;
            _error = "";
            _done = 0;
            _bgftTaskId = -1;

            string route;
            string error;
            bool busy;
            if (!Server.TryRegister(job.Id, job.Destination, job.Total,
                out route, out error, out busy))
            {
                Fail(error);
                return;
            }

            if (File.Exists(job.Destination))
            {
                if (!Validate(job.Destination, job, out error) ||
                    !Server.TryMarkComplete(job.Id, out error))
                {
                    Fail(error);
                    return;
                }
                _done = job.Total;
                _state = "awaiting-bgft";
                WriteStatus(true);
                StartBgftIfReady();
                return;
            }

            string part = job.Destination + ".part";
            if (!File.Exists(part))
            {
                Fail("Resident feeder partial is missing");
                return;
            }
            long offset = new FileInfo(part).Length;
            if (offset < LoopbackPkgFeeder.HeaderBytes || offset > job.Total)
            {
                Fail("Resident feeder partial has an invalid length");
                return;
            }

            _done = offset;
            _state = "ready";
            WriteStatus(true);
            _feeder = new Thread(() => Feed(job, offset))
                { IsBackground = true, Name = "Resident PKG feeder" };
            _feeder.Start();
        }

        static void Feed(ResidentJob job, long offset)
        {
            try
            {
                _state = "feeding";
                NetHttp.DownloadRangeCount = 1;
                SequentialDownloadEngine.LoadLimit(Path.Combine(AppSettings.DataDir, "settings.ini"));
                string part = job.Destination + ".part";
                if (offset == LoopbackPkgFeeder.HeaderBytes)
                {
                    ParallelDownloadCheckpoint.DeleteAll(part);
                    offset = 0;
                    _done = 0;
                }
                while (!_stop)
                {
                    while (_paused && !_stop) Thread.Sleep(100);
                    if (_stop) throw new OperationCanceledException("canceled");
                    try
                    {
                        _done = NetHttp.DownloadFileResumable(job.Url, job.Destination, offset,
                            (done, total) =>
                            {
                                Interlocked.Exchange(ref _done, done);
                            }, () => _stop || _paused, 60000, null, job.TitleId);
                        return;
                    }
                    catch (OperationCanceledException)
                    {
                        if (_stop) throw;
                        offset = File.Exists(part) ? new FileInfo(part).Length : 0;
                        _done = offset;
                        if (!_paused) throw;
                    }
                }
            }
            catch (Exception ex) { _feederError = ex; }
        }

        static void FinishFeeder()
        {
            _feeder = null;
            if (_stop || _release)
            {
                Server.MarkFailed(_job.Id);
                Server.Release(_job.Id);
                ClearJobFile(_job.Id);
                _job = null;
                _state = "idle";
                _error = "";
                _done = 0;
                WriteStatus(true);
                return;
            }
            if (_feederError != null || _done != _job.Total)
            {
                Fail(_feederError != null ? _feederError.Message :
                    "Resident feeder stopped before completion");
                return;
            }

            _state = "validating";
            WriteStatus(true);
            string part = _job.Destination + ".part";
            string completed = File.Exists(_job.Destination) ? _job.Destination : part;
            string error;
            if (!Validate(completed, _job, out error))
            {
                Fail(error);
                return;
            }
            try
            {
                if (completed == part)
                {
                    if (File.Exists(_job.Destination))
                        throw new IOException("Package destination already exists");
                    File.Move(part, _job.Destination);
                }
                DownloadResumeInfo.Delete(part);
            }
            catch (Exception ex)
            {
                Fail(ex.Message);
                return;
            }
            if (!Server.TryMarkComplete(_job.Id, out error))
            {
                Fail(error);
                return;
            }
            _state = "awaiting-bgft";
            WriteStatus(true);
            StartBgftIfReady();
        }

        static void StartBgftIfReady()
        {
            if (_job == null || _bgftTaskId < 0 || _state != "awaiting-bgft") return;
            string error;
            if (!ResidentBgft.Start(_bgftTaskId, out error))
            {
                Fail(error);
                return;
            }
            _state = "complete";
            _done = _job.Total;
            WriteStatus(true);
        }

        static bool Validate(string path, ResidentJob job, out string error)
        {
            error = null;
            PkgValResult result;
            string detail;
            if (!PkgValidator.TryValidateStructure(path, out result, out detail))
            {
                error = detail;
                return false;
            }
            if (new FileInfo(path).Length != job.Total)
            {
                error = "Resident PKG size changed";
                return false;
            }
            string contentId;
            if (!PkgValidator.TryGetContentId(path, out contentId) ||
                (!string.IsNullOrEmpty(job.ContentId) && !string.Equals(contentId,
                    job.ContentId, StringComparison.OrdinalIgnoreCase)))
            {
                error = "Resident PKG content ID changed";
                return false;
            }
            if (!string.IsNullOrEmpty(job.TitleId) &&
                !PkgValidator.ContentIdMatchesTitleId(contentId, job.TitleId))
            {
                error = "Resident PKG title ID changed";
                return false;
            }
            string expected = NormalizeSha(job.ExpectedSha256);
            if (expected.Length > 0)
            {
                string actual;
                using (var sha = SHA256.Create())
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 1024 * 1024))
                    actual = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    error = "Package SHA-256 mismatch";
                    return false;
                }
            }
            return true;
        }

        static void ReadControl()
        {
            if (!File.Exists(ControlPath)) return;
            string[] lines;
            try
            {
                lines = File.ReadAllLines(ControlPath);
                File.Delete(ControlPath);
            }
            catch { return; }
            if (lines.Length != 3 || lines[0] != "1" || _job == null) return;
            string id;
            try { id = Encoding.UTF8.GetString(Convert.FromBase64String(lines[1])); }
            catch { return; }
            if (!string.Equals(id, _job.Id, StringComparison.Ordinal)) return;
            if (lines[2] == "pause")
            {
                _paused = true;
                _state = "paused";
            }
            else if (lines[2] == "resume")
            {
                _paused = false;
                if (_feeder != null && _feeder.IsAlive) _state = "feeding";
            }
            else if (lines[2] == "cancel" || lines[2] == "release")
            {
                _release = lines[2] == "release";
                _stop = true;
                if (_feeder == null || !_feeder.IsAlive) FinishFeeder();
            }
            WriteStatus(true);
        }

        static ResidentJob ReadJob()
        {
            try { return File.Exists(JobPath) ? ResidentJob.Parse(File.ReadAllLines(JobPath)) : null; }
            catch { return null; }
        }

        static void ReadBgftTask()
        {
            try
            {
                ResidentBgftAttachment attachment = File.Exists(BgftPath)
                    ? ResidentBgftAttachment.Parse(File.ReadAllLines(BgftPath)) : null;
                if (attachment == null || _job == null ||
                    !string.Equals(attachment.Id, _job.Id, StringComparison.Ordinal)) return;
                _bgftTaskId = attachment.TaskId;
            }
            catch { }
        }

        static void Fail(string error)
        {
            lock (Gate)
            {
                _error = string.IsNullOrEmpty(error) ? "Resident feeder failed" : error;
                _state = "failed";
                if (_job != null) Server.MarkFailed(_job.Id);
            }
            WriteStatus(true);
        }

        static void WriteHeartbeat()
        {
            long now = DateTime.UtcNow.Ticks;
            if (now - Interlocked.Read(ref _lastHeartbeatTicks) < TimeSpan.TicksPerSecond) return;
            Interlocked.Exchange(ref _lastHeartbeatTicks, now);
            WriteAtomic(HeartbeatPath, Version + "\n" +
                now.ToString(CultureInfo.InvariantCulture));
        }

        static void WriteStatus(bool force)
        {
            long now = DateTime.UtcNow.Ticks;
            if (!force && now - Interlocked.Read(ref _lastStatusTicks) <
                TimeSpan.TicksPerMillisecond * 500) return;
            Interlocked.Exchange(ref _lastStatusTicks, now);
            ResidentJob job = _job;
            string id = job != null ? job.Id : "";
            long total = job != null ? job.Total : 0;
            bool served = job != null && Server.WasFullyServed(job.Id);
            string body = "1\n" + Encode(id) + "\n" + _state + "\n" +
                Interlocked.Read(ref _done).ToString(CultureInfo.InvariantCulture) + "\n" +
                total.ToString(CultureInfo.InvariantCulture) + "\n" +
                (served ? "1" : "0") + "\n" + Encode(_error);
            try { WriteAtomic(StatusPath, body); } catch { }
        }

        static void ClearJobFile(string id)
        {
            try
            {
                ResidentJob stored = ReadJob();
                if (stored != null && string.Equals(stored.Id, id, StringComparison.Ordinal))
                    File.Delete(JobPath);
                ResidentBgftAttachment attachment = File.Exists(BgftPath)
                    ? ResidentBgftAttachment.Parse(File.ReadAllLines(BgftPath)) : null;
                if (attachment != null && string.Equals(attachment.Id, id, StringComparison.Ordinal))
                    File.Delete(BgftPath);
            }
            catch { }
        }

        static bool OwnedDestination(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try
            {
                string full = Path.GetFullPath(path).Replace('\\', '/');
                string root = Path.GetFullPath(Path.Combine(DataRoot, "downloads"))
                    .Replace('\\', '/').TrimEnd('/');
                return full.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        static string NormalizeSha(string value)
        {
            string hash = (value ?? "").Replace("-", "").Replace(" ", "").Trim();
            if (hash.Length == 0) return "";
            if (hash.Length != 64) return "invalid";
            for (int i = 0; i < hash.Length; i++)
                if (!Uri.IsHexDigit(hash[i])) return "invalid";
            return hash.ToUpperInvariant();
        }

        static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        static void WriteAtomic(string path, string body)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, body);
            try
            {
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
            catch
            {
                File.Copy(tmp, path, true);
                try { File.Delete(tmp); } catch { }
            }
        }

        [DllImport("libSceSystemService", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceSystemServiceRegisterDaemon();
    }

    internal static class ResidentBgft
    {
        const int HeapSize = 1024 * 1024;
        const int AlreadyInitialized = unchecked((int)0x80990001);
        static IntPtr _heap;
        static bool _ready;

        public static bool Start(int taskId, out string error)
        {
            error = null;
            try
            {
                if (!_ready)
                {
                    try
                    {
                        Orbis.Internals.Kernel.TryLoadStartModule(
                            "/system/common/lib/libSceBgft.sprx");
                    }
                    catch { }
                    try
                    {
                        sceKernelLoadStartModule("/system/common/lib/libSceBgft.sprx",
                            0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
                    }
                    catch { }
                    _heap = Marshal.AllocHGlobal(HeapSize);
                    var init = new BgftInitParams
                    {
                        Heap = _heap,
                        HeapSize = (UIntPtr)HeapSize
                    };
                    int initResult = sceBgftServiceIntInit(ref init);
                    if (initResult != 0 && initResult != AlreadyInitialized)
                    {
                        error = "Resident BGFT init 0x" +
                            unchecked((uint)initResult).ToString("X");
                        return false;
                    }
                    _ready = true;
                }
                int publicStart = sceBgftServiceDownloadStartTask(taskId);
                int result = publicStart;
                if (result != 0)
                {
                    try { result = sceBgftServiceIntDownloadStartTask(taskId); }
                    catch { result = publicStart; }
                }
                if (result == 0) return true;
                error = "Resident BGFT start 0x" + unchecked((uint)result).ToString("X");
                return false;
            }
            catch (Exception ex)
            {
                error = "Resident BGFT " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BgftInitParams
        {
            public IntPtr Heap;
            public UIntPtr HeapSize;
        }

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceIntInit",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceIntInit(ref BgftInitParams initParams);

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceDownloadStartTask",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceDownloadStartTask(int taskId);

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceIntDownloadStartTask",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceIntDownloadStartTask(int taskId);

        [DllImport("libkernel", EntryPoint = "sceKernelLoadStartModule",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int sceKernelLoadStartModule(string name, uint argc, IntPtr argv,
            uint flags, IntPtr option, IntPtr result);
    }
}
