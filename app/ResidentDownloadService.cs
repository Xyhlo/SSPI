using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal sealed class ResidentDownloadStatus
    {
        public string Id;
        public string State;
        public long Done;
        public long Total;
        public bool FullyServed;
        public string Error;

        public bool Terminal
        {
            get
            {
                return State == "complete" || State == "failed" ||
                    State == "canceled" || State == "idle" || State == "submitted" || State == "installed";
            }
        }
    }

    internal static class ResidentDownloadService
    {
        const string Version = "5.10-r6";
        const string HostDaemonTitleId = "NPXS20119";
        const int LncAppNotFound = unchecked((int)0x80940005);
        const long SystemAuthId = 0x3800000000000010;
        const string DaemonTitleId = "SRCHD0001";
        const string DaemonRoot = "/system/vsh/app/" + DaemonTitleId;
        static string _packageRoot;
        static bool _bridgeBound;
        static ResidentIntFn _mountFn;
        static ResidentIntFn _initFn;
        static ResidentIntFn _getIdFn;
        static ResidentIntFn _stopFn;
        static ResidentLaunchFn _launchFn;
        static ResidentPathFn _mkdirFn;
        static ResidentPathFn _unlinkFn;
        static ResidentChmodFn _chmodFn;
        static ResidentCopyFn _copyFn;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int ResidentIntFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int ResidentLaunchFn(uint userId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        delegate int ResidentPathFn(string path);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        delegate int ResidentChmodFn(string path, int mode);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        delegate int ResidentCopyFn(string src, string dst);
        static readonly string[] SystemDaemonSfoCandidates =
        {
            "/system/vsh/app/NPXS20119/sce_sys/param.sfo",
            "/system/vsh/app/NPXS21007/sce_sys/param.sfo"
        };
        static readonly object Gate = new object();
        static string _lastError = "not started";

        static string IpcRoot { get { return Path.Combine(AppSettings.DataDir, "resident"); } }
        static string HeartbeatPath { get { return Path.Combine(IpcRoot, "heartbeat.txt"); } }
        static string JobPath { get { return Path.Combine(IpcRoot, "job.txt"); } }
        static string StatusPath { get { return Path.Combine(IpcRoot, "status.txt"); } }
        static string ControlPath { get { return Path.Combine(IpcRoot, "control.txt"); } }
        static string BgftPath { get { return Path.Combine(IpcRoot, "bgft.txt"); } }

        public static string LastError { get { lock (Gate) return _lastError; } }

        public static bool TryStart(string id, string url, string destination, string titleId,
            string expectedSha256, string contentId, long total, int rangeCount, out string routeUrl,
            out string error, out bool busy)
        {
            routeUrl = null;
            error = null;
            busy = false;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url) ||
                string.IsNullOrWhiteSpace(destination) || total < LoopbackPkgFeeder.HeaderBytes)
            {
                error = "Resident download metadata is invalid";
                return false;
            }

            lock (Gate)
            {
                if (!EnsureAvailableLocked(out error)) return false;

                ResidentDownloadStatus current;
                if (TryReadStatus(out current) &&
                    (current.State == "failed" || current.State == "canceled"))
                {
                    WriteControl(current.Id, "release");
                    DateTime releasedBy = DateTime.UtcNow.AddSeconds(3);
                    while (DateTime.UtcNow < releasedBy)
                    {
                        ResidentDownloadStatus released;
                        if (!TryReadStatus(out released) || released.State == "idle") break;
                        Thread.Sleep(100);
                    }
                }
                if (TryReadStatus(out current) && current.State != "idle" &&
                    !string.Equals(current.Id, id, StringComparison.Ordinal))
                {
                    busy = true;
                    error = "Another PS4 system download owns the resident feeder";
                    return false;
                }

                try
                {
                    Directory.CreateDirectory(IpcRoot);
                    WriteAtomic(JobPath, string.Join("\n", new[]
                    {
                        "2",
                        Encode(id),
                        Encode(url),
                        Encode(destination),
                        Encode(titleId),
                        Encode(expectedSha256),
                        Encode(contentId),
                        total.ToString(CultureInfo.InvariantCulture),
                        DownloadTransferSettings.ClampRangeCount(rangeCount)
                            .ToString(CultureInfo.InvariantCulture)
                    }));
                }
                catch (Exception ex)
                {
                    error = "Resident job write failed: " + ex.Message;
                    _lastError = error;
                    return false;
                }

                DateTime until = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < until)
                {
                    ResidentDownloadStatus status;
                    if (TryReadStatus(out status) && string.Equals(status.Id, id,
                        StringComparison.Ordinal))
                    {
                        if (status.State == "failed" || status.State == "canceled")
                        {
                            error = string.IsNullOrEmpty(status.Error)
                                ? "Resident feeder failed to start" : status.Error;
                            _lastError = error;
                            return false;
                        }
                        if (status.State == "ready" || status.State == "feeding" ||
                            status.State == "paused" || status.State == "validating" ||
                            status.State == "awaiting-bgft" || status.State == "complete")
                        {
                            routeUrl = "http://127.0.0.1:" + LoopbackPkgServer.Port +
                                "/pkg/" + Uri.EscapeDataString(id) + ".pkg";
                            _lastError = "ok";
                            return true;
                        }
                    }
                    Thread.Sleep(100);
                }
                error = "Resident feeder did not accept the job";
                _lastError = error;
                return false;
            }
        }

        public static bool TryStartArchive(string id, string destination, string titleId, string expectedContentId,
            System.Collections.Generic.IList<ArchiveVolume> volumes,
            System.Collections.Generic.IList<string> paths, out string error)
        {
            lock (Gate)
            {
                if (!EnsureAvailableLocked(out error)) return false;
                if (File.Exists(JobPath) && !HasJob(id))
                { error = "Another resident job is active"; return false; }
                ResidentDownloadStatus current;
                if (TryReadStatus(out current) && current.State != "idle" && current.Id != id)
                { error = "Another resident job is active"; return false; }
                if (volumes == null || paths == null || volumes.Count == 0 || volumes.Count != paths.Count)
                { error = "Archive volume metadata is incomplete"; return false; }
                try
                {
                    var body = new StringBuilder();
                    body.Append("3\n").Append(Encode(id)).Append('\n').Append(Encode(volumes[0].Url)).Append('\n')
                        .Append(Encode(destination)).Append('\n').Append(Encode(titleId)).Append("\n\n").Append(Encode(expectedContentId)).Append('\n')
                        .Append(volumes[0].Size.ToString(CultureInfo.InvariantCulture)).Append("\n").Append(DownloadTransferSettings.ClampRangeCount(NetHttp.DownloadRangeCount)).Append("\n")
                        .Append(volumes.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    for (int i = 0; i < volumes.Count; i++)
                    {
                        ArchiveVolume volume = volumes[i];
                        body.Append(Encode(volume.Name)).Append('\n').Append(Encode(volume.Url)).Append('\n')
                            .Append(Encode(paths[i])).Append('\n').Append(Encode(volume.Sha256)).Append('\n')
                            .Append(volume.Size.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    }
                    WriteAtomic(JobPath, body.ToString());
                    // Durable publication transfers ownership even if the process switches
                    // before the plugin can acknowledge it. Never fall back after this write.
                    error = null;
                    return true;
                }
                catch (Exception ex) { error = "Archive handoff failed: " + ex.Message; return false; }
            }
        }

        public static bool TryStartPackage(string id, string url, string destination, string titleId,
            string sha256, string contentId, long size, int expectedKind, out string error)
        {
            lock (Gate)
            {
                if (!EnsureAvailableLocked(out error)) return false;
                if (File.Exists(JobPath) && !HasJob(id))
                { error = "Another resident job is active"; return false; }
                ResidentDownloadStatus current;
                if (TryReadStatus(out current) && current.State != "idle" && current.Id != id)
                { error = "Another resident job is active"; return false; }
                try
                {
                    if (expectedKind < 6 || expectedKind > 8) throw new IOException("Package type is unknown");
                    WriteAtomic(JobPath, string.Join("\n", new[] { "5", Encode(id), Encode(url), Encode(destination),
                        Encode(titleId), Encode(sha256), Encode(contentId), size.ToString(CultureInfo.InvariantCulture), DownloadTransferSettings.ClampRangeCount(NetHttp.DownloadRangeCount).ToString(CultureInfo.InvariantCulture), expectedKind.ToString(CultureInfo.InvariantCulture) }));
                    error = null;
                    return true;
                }
                catch (Exception ex) { error = "Package handoff failed: " + ex.Message; return false; }
            }
        }

        public static bool EnsureAvailable(out string error)
        {
            lock (Gate) return EnsureAvailableLocked(out error);
        }

        static bool EnsureAvailableLocked(out string error)
        {
            error = null;
            try { Directory.CreateDirectory(IpcRoot); } catch { }
            if (FreshHeartbeat()) return true;
            string pluginStatus;
            bool pluginStaged = GoldHenPluginInstaller.TryEnsureInstalled(out pluginStatus);
            if (pluginStaged)
            {
                try
                {
                    string[] heartbeat = File.ReadAllLines(HeartbeatPath);
                    long ticks;
                    if (heartbeat.Length >= 2 && heartbeat[0] != Version &&
                        long.TryParse(heartbeat[1], out ticks) && Math.Abs(DateTime.UtcNow.Ticks - ticks) < TimeSpan.FromSeconds(10).Ticks)
                    {
                        error = "Downloader updated: restart PS4 and re-enable GoldHEN to activate the new download worker";
                        _lastError = error; return false;
                    }
                }
                catch { }
                // Only an acknowledged shell worker can own the handoff.
                for (int i = 0; i < 50; i++)
                {
                    if (FreshHeartbeat()) return true;
                    Thread.Sleep(100);
                }
                error = "GoldHEN shell downloader has not acknowledged startup: " + pluginStatus;
                _lastError = error;
                WriteLastError(error);
                return false;
            }
            error = "GoldHEN resident plugin unavailable: " + pluginStatus;
            _lastError = error;
            WriteLastError(error);
            return false;
        }

        public static bool TryGetStatus(string id, out ResidentDownloadStatus status)
        {
            status = null;
            ResidentDownloadStatus current;
            if (!TryReadStatus(out current) ||
                !string.Equals(current.Id, id, StringComparison.Ordinal)) return false;
            status = current;
            return true;
        }

        public static bool IsAlive(string id)
        {
            if (!FreshHeartbeat()) return false;
            ResidentDownloadStatus current;
            if (!TryReadStatus(out current)) return true;
            if (!string.Equals(current.Id, id, StringComparison.Ordinal)) return false;
            return current.State != "failed" && current.State != "canceled" &&
                current.State != "idle";
        }

        public static bool TryMarkComplete(string id, out string error)
        {
            ResidentDownloadStatus status;
            if (TryGetStatus(id, out status) && status.State == "complete")
            {
                error = null;
                return true;
            }
            error = status != null && !string.IsNullOrEmpty(status.Error)
                ? status.Error : "Resident feeder has not validated the PKG";
            return false;
        }

        public static bool WasFullyServed(string id)
        {
            ResidentDownloadStatus status;
            return TryGetStatus(id, out status) && status.FullyServed;
        }

        public static void SetPaused(string id, bool paused)
        {
            WriteControl(id, paused ? "pause" : "resume");
        }

        public static bool AttachBgftTask(string id, int taskId, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(id) || taskId < 0)
            {
                error = "Resident BGFT task metadata is invalid";
                return false;
            }
            try
            {
                Directory.CreateDirectory(IpcRoot);
                WriteAtomic(BgftPath, "1\n" + Encode(id) + "\n" +
                    taskId.ToString(CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception ex)
            {
                error = "Resident BGFT task write failed: " + ex.Message;
                return false;
            }
        }

        public static void ResetQueueForFreshStart()
        {
            lock (Gate)
            {
                Directory.CreateDirectory(IpcRoot);
                string id = "";
                ResidentDownloadStatus status;
                if (TryReadStatus(out status)) id = status.Id;
                if (File.Exists(JobPath))
                {
                    string[] lines = File.ReadAllLines(JobPath);
                    if (lines.Length > 1 && string.IsNullOrEmpty(id)) id = Decode(lines[1]);
                    string backup = JobPath + ".before-5.01";
                    if (!File.Exists(backup)) File.Copy(JobPath, backup);
                    File.Delete(JobPath);
                }
                // Release also cancels the worker, then clears its ownership after it stops.
                if (!string.IsNullOrEmpty(id)) WriteControl(id, "release");
            }
        }

        public static void MarkFailed(string id)
        {
            WriteControl(id, "cancel");
        }

        public static void Release(string id)
        {
            WriteControl(id, "release");
        }

        public static string GetError(string id)
        {
            ResidentDownloadStatus status;
            return TryGetStatus(id, out status) ? status.Error : LastError;
        }

        static void WriteControl(string id, string action)
        {
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                Directory.CreateDirectory(IpcRoot);
                WriteAtomic(ControlPath, "1\n" + Encode(id) + "\n" + action);
            }
            catch (Exception ex)
            {
                lock (Gate) _lastError = ex.Message;
            }
        }

        public static bool HasJob(string id)
        {
            try
            {
                using (var reader = new StreamReader(JobPath))
                {
                    string version = reader.ReadLine();
                    return (version == "2" || version == "3" || version == "4" || version == "5") && Decode(reader.ReadLine()) == id;
                }
            }
            catch { return false; }
        }

        public static bool HasDownloader { get { return FreshHeartbeat(); } }

        static bool FreshHeartbeat()
        {
            for (int i = 0; i < 8; i++)
            {
                try
                {
                    string[] lines = File.ReadAllLines(HeartbeatPath);
                    long ticks;
                    if (lines.Length >= 3 && lines[0] == Version &&
                        lines[2].Contains("host=shell ") &&
                        lines[2].Contains("listener=1") && lines[2].Contains("download=1") &&
                        long.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out ticks) && Math.Abs(DateTime.UtcNow.Ticks - ticks) <=
                        TimeSpan.TicksPerSecond * 30)
                        return true;
                }
                catch { }
                Thread.Sleep(15);
            }
            return false;
        }

        static bool TryReadStatus(out ResidentDownloadStatus status)
        {
            status = null;
            for (int i = 0; i < 8; i++)
            {
                try
                {
                    string[] lines = File.ReadAllLines(StatusPath);
                    long done;
                    long total;
                    if (lines.Length != 7 || lines[0] != "1" ||
                        !long.TryParse(lines[3], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out done) ||
                        !long.TryParse(lines[4], NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out total))
                    {
                        Thread.Sleep(15);
                        continue;
                    }
                    status = new ResidentDownloadStatus
                    {
                        Id = Decode(lines[1]),
                        State = lines[2],
                        Done = done,
                        Total = total,
                        FullyServed = lines[5] == "1",
                        Error = Decode(lines[6])
                    };
                    return true;
                }
                catch { }
                Thread.Sleep(15);
            }
            return false;
        }

        static void RestoreHostDaemonIfNeeded()
        {
            string backup = Path.Combine(IpcRoot, "npxs20119.eboot.bak");
            string hostEboot = "/system/vsh/app/" + HostDaemonTitleId + "/eboot.bin";
            if (!File.Exists(backup) || !File.Exists(hostEboot)) return;
            try
            {
                NativeCopyFile(backup, hostEboot);
                WriteLaunchLog("host eboot restored");
            }
            catch (Exception ex)
            {
                WriteLaunchLog("host restore " + ex.Message);
            }
        }

        static void TakeSystemCreds()
        {
            try
            {
                bool ok = Orbis.Internals.Kernel.Jailbreak(SystemAuthId);
                WriteLaunchLog("systemCreds=" + (ok ? "1" : "0"));
            }
            catch (Exception ex)
            {
                WriteLaunchLog("systemCreds ex=" + ex.GetType().Name);
            }
        }

        static void CopyTree(string source, string destination)
        {
            if (!Directory.Exists(source)) return;
            NativeMkdir(destination);
            NativeChmod(destination, 511);
            foreach (string directory in Directory.GetDirectories(source, "*",
                SearchOption.AllDirectories))
            {
                string targetDir = destination + directory.Substring(source.Length);
                NativeMkdir(targetDir);
                NativeChmod(targetDir, 511);
            }
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = destination + file.Substring(source.Length);
                string parent = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(parent)) NativeMkdir(parent);
                NativeUnlink(target);
                NativeCopyFile(file, target);
            }
        }

        static void NativeMkdir(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            int rc = _mkdirFn != null ? _mkdirFn(path) : gs_resident_mkdir(path);
            if (rc == 0) return;
            WriteLaunchLog("mkdir " + path + " " + DescribeNativeIo(rc));
            try { Directory.CreateDirectory(path); }
            catch (Exception ex)
            {
                WriteLaunchLog("mkdir managed " + path + " " + ex.Message);
            }
        }

        static void NativeChmod(string path, int mode)
        {
            if (string.IsNullOrEmpty(path)) return;
            int rc = _chmodFn != null ? _chmodFn(path, mode) : gs_resident_chmod(path, mode);
            if (rc != 0) WriteLaunchLog("chmod " + path + " " + DescribeNativeIo(rc));
        }

        static void NativeUnlink(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (_unlinkFn != null) _unlinkFn(path);
            else gs_resident_unlink(path);
        }

        static void NativeCopyFile(string source, string destination)
        {
            int rc = _copyFn != null
                ? _copyFn(source, destination)
                : gs_resident_copy_file(source, destination);
            if (rc == 0) return;
            WriteLaunchLog("copy " + Path.GetFileName(source) + " " + DescribeNativeIo(rc));
            throw new IOException("native copy " + Path.GetFileName(source) + " " +
                DescribeNativeIo(rc));
        }

        static string DescribeNativeIo(int rc)
        {
            if (rc >= 0) return "0x" + rc.ToString("X");
            int errno = -rc;
            string stage = "io";
            if (errno >= 10000 && errno < 20000) { stage = "src"; errno -= 10000; }
            else if (errno >= 20000 && errno < 30000) { stage = "dst"; errno -= 20000; }
            else if (errno >= 30000 && errno < 40000) { stage = "write"; errno -= 30000; }
            else if (errno >= 40000 && errno < 50000) { stage = "read"; errno -= 40000; }
            return stage + " errno=" + errno;
        }

        static void WriteAtomic(string path, string body)
        {
            AtomicFile.WriteText(path, body);
        }

        [DllImport("libkernel", EntryPoint = "sceKernelRename", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceKernelRename(string source, string destination);

        static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        static string Decode(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? ""));
        }

        static bool IsDaemonAppId(int appId)
        {
            return (unchecked((uint)appId) & 0xFF000000U) == 0x60000000U;
        }

        static void InstallDaemonSfo()
        {
            string daemonSfo = Path.Combine(DaemonRoot, "sce_sys/param.sfo");
            Directory.CreateDirectory(Path.GetDirectoryName(daemonSfo));
            string donor = null;
            for (int i = 0; i < SystemDaemonSfoCandidates.Length; i++)
            {
                if (File.Exists(SystemDaemonSfoCandidates[i]))
                {
                    donor = SystemDaemonSfoCandidates[i];
                    break;
                }
            }
            if (donor != null)
            {
                File.Copy(donor, daemonSfo, true);
                WriteLaunchLog("sfo donor=" + donor);
            }
            else WriteLaunchLog("sfo donor=bundled");
            if (!File.Exists(daemonSfo))
                throw new FileNotFoundException("daemon SFO is unavailable", daemonSfo);
            string sfoError;
            if (!ResidentSfo.TrySetUtf8(daemonSfo, "TITLE_ID", DaemonTitleId, out sfoError))
                WriteLaunchLog("sfo TITLE_ID patch failed: " + sfoError);
            else WriteLaunchLog("sfo TITLE_ID=" + DaemonTitleId);
            ResidentSfo.TrySetUtf8(daemonSfo, "TITLE", "Game Search Service", out sfoError);
            ResidentSfo.TrySetUtf8(daemonSfo, "CATEGORY", "gdd", out sfoError);
            ResidentSfo.TrySetUtf8(daemonSfo, "CONTENT_ID",
                "IV0000-SRCHD0001_00-GAMESEARCHDAEMON", out sfoError);
        }

        static void StageDaemonPayload(string packageRoot)
        {
            string dataDaemon = "/data/GameSearch/daemon";
            NativeMkdir("/data");
            NativeMkdir("/data/GameSearch");
            NativeMkdir(IpcRoot);
            NativeMkdir(dataDaemon);
            bool stale = true;
            try
            {
                string marker = Path.Combine(dataDaemon, "version.txt");
                if (File.Exists(marker) && File.ReadAllText(marker).Trim() == Version &&
                    File.Exists(Path.Combine(dataDaemon, "eboot.bin")) &&
                    File.Exists(Path.Combine(dataDaemon, "main.exe")))
                    stale = false;
            }
            catch { }
            if (stale)
            {
                try
                {
                    CopyTree(packageRoot + "/daemon", dataDaemon);
                    CopyTree(packageRoot + "/runtime", dataDaemon);
                    CopyTree(packageRoot + "/worker", dataDaemon);
                    File.WriteAllText(Path.Combine(dataDaemon, "version.txt"), Version);
                }
                catch (Exception ex)
                {
                    WriteLaunchLog("data daemon copy " + ex.Message);
                }
            }
            string[] sources =
            {
                packageRoot + "/daemon/spawn.eboot.bin",
                Path.Combine(dataDaemon, "spawn.eboot.bin"),
                Path.Combine(DaemonRoot, "eboot.bin"),
                Path.Combine(dataDaemon, "eboot.bin"),
                packageRoot + "/daemon/eboot.bin"
            };
            string eboot = null;
            for (int i = 0; i < sources.Length; i++)
            {
                if (File.Exists(sources[i]))
                {
                    eboot = sources[i];
                    break;
                }
            }
            if (eboot == null)
            {
                WriteLaunchLog("stage eboot missing");
                return;
            }
            TryStageFile(eboot, "/data/eboot.bin");
            TryStageFile(eboot, Path.Combine(IpcRoot, "eboot.bin"));
            TryStageFile(eboot, Path.Combine(dataDaemon, "eboot.bin"));
        }

        static void TryStageFile(string source, string destination)
        {
            try
            {
                NativeUnlink(destination);
                NativeCopyFile(source, destination);
                NativeChmod(destination, 511);
                WriteLaunchLog("staged " + destination + " " + DescribeManagedFile(destination));
            }
            catch (Exception ex)
            {
                WriteLaunchLog("stage " + destination + " " + ex.Message);
            }
        }

        static string DescribeManagedFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return "missing";
                return "exists size=" + new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        static bool TryRegisterDaemonTitle(string packageRoot, uint launchUser, ref int launchResult)
        {
            string pkgSource = packageRoot + "/SRCHD0001.pkg";
            if (!File.Exists(pkgSource))
            {
                WriteLaunchLog("daemon pkg missing");
                return false;
            }
            string pkgDest = Path.Combine(IpcRoot, "SRCHD0001.pkg");
            string userPkg = "/user/data/GameSearch/resident/SRCHD0001.pkg";
            try
            {
                NativeMkdir("/user");
                NativeMkdir("/user/data");
                NativeMkdir("/user/data/GameSearch");
                NativeMkdir("/user/data/GameSearch/resident");
                TryStageFile(pkgSource, pkgDest);
                TryStageFile(pkgSource, userPkg);
                string installError;
                string installPath = File.Exists(userPkg) ? userPkg : pkgDest;
                bool accepted = PkgInstaller.TryInstallWithAppInstUtil(installPath, out installError);
                WriteLaunchLog("daemon pkg AppInstUtil=" + (accepted ? "1" : "0") +
                    (string.IsNullOrEmpty(installError) ? "" : " " + installError));
                if (!accepted) return false;
                DateTime until = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < until)
                {
                    if (PkgInstaller.IsTitleInstalled(DaemonTitleId) ||
                        IsDaemonAppId(ResidentGetAppId()))
                        break;
                    Thread.Sleep(250);
                }
                WriteLaunchLog("daemon pkg exists=" +
                    (PkgInstaller.IsTitleInstalled(DaemonTitleId) ? "1" : "0") +
                    " appId=0x" + unchecked((uint)ResidentGetAppId()).ToString("X"));
                int retry = ResidentLaunch(launchUser);
                WriteLaunchLog("launch after register=0x" + unchecked((uint)retry).ToString("X"));
                if (IsDaemonAppId(retry) || ((unchecked((uint)retry) & 0x80000000U) == 0))
                {
                    launchResult = retry;
                    return true;
                }
                retry = RetryIndependentLaunch(launchUser, retry);
                launchResult = retry;
                return IsDaemonAppId(retry);
            }
            catch (Exception ex)
            {
                WriteLaunchLog("daemon pkg " + ex.GetType().Name + " " + ex.Message);
                return false;
            }
        }

        static int RetryIndependentLaunch(uint launchUser, int previous)
        {
            int skip = previous;
            try
            {
                skip = gs_resident_launch_skip(launchUser);
                WriteLaunchLog("launchSkip=0x" + unchecked((uint)skip).ToString("X"));
                if (IsDaemonAppId(skip)) return skip;
            }
            catch (Exception ex)
            {
                WriteLaunchLog("launchSkip ex=" + ex.GetType().Name + " " + ex.Message);
            }
            try
            {
                int system = gs_resident_system_launch(launchUser);
                WriteLaunchLog("systemLaunch=0x" + unchecked((uint)system).ToString("X"));
                if (IsDaemonAppId(system)) return system;
                if (((unchecked((uint)system) & 0x80000000U) == 0)) return system;
                if (IsDaemonAppId(skip) || ((unchecked((uint)skip) & 0x80000000U) == 0))
                    return skip;
                return system;
            }
            catch (Exception ex)
            {
                WriteLaunchLog("systemLaunch ex=" + ex.GetType().Name + " " + ex.Message);
                return skip;
            }
        }

        static int SpawnLocalDaemon(int previous)
        {
            string[] paths =
            {
                "/data/eboot.bin",
                Path.Combine(IpcRoot, "eboot.bin"),
                "/data/GameSearch/daemon/eboot.bin",
                Path.Combine(DaemonRoot, "eboot.bin")
            };
            int result = previous;
            for (int i = 0; i < paths.Length; i++)
            {
                WriteLaunchLog("spawn try " + paths[i] + " " + DescribeManagedFile(paths[i]));
                if (!File.Exists(paths[i])) continue;
                try
                {
                    result = gs_resident_spawn_local(paths[i]);
                }
                catch (Exception ex)
                {
                    WriteLaunchLog("local spawn ex=" + ex.GetType().Name + " " + ex.Message);
                    continue;
                }
                WriteLaunchLog("local spawn " + paths[i] + "=0x" +
                    unchecked((uint)result).ToString("X"));
                if (IsDaemonAppId(result) || ((unchecked((uint)result) & 0x80000000U) == 0))
                    return result;
            }
            return result;
        }

        static void WriteLaunchLog(string body)
        {
            try
            {
                Directory.CreateDirectory(IpcRoot);
                File.AppendAllText(Path.Combine(IpcRoot, "launch.txt"),
                    DateTime.UtcNow.ToString("o") + " " + (body ?? "") + "\n");
            }
            catch { }
        }

        static void TryLoadLncUtil()
        {
            try
            {
                Orbis.Internals.Kernel.TryLoadStartModule(
                    "/system/common/lib/libSceLncUtil.sprx");
            }
            catch { }
        }

        static string ResidentAppRoot()
        {
            try
            {
                string dir = Orbis.Internals.IO.GetAppBaseDirectory();
                if (!string.IsNullOrEmpty(dir)) return dir.TrimEnd('/', '\\');
            }
            catch { }
            return "/app0";
        }

        static string ResolvePackageRoot()
        {
            if (!string.IsNullOrEmpty(_packageRoot)) return _packageRoot;
            string[] roots =
            {
                ResidentAppRoot() + "/resident",
                "/app0/resident"
            };
            for (int i = 0; i < roots.Length; i++)
            {
                if (File.Exists(roots[i] + "/daemon/eboot.bin") &&
                    File.Exists(roots[i] + "/worker/main.exe"))
                {
                    _packageRoot = roots[i];
                    return _packageRoot;
                }
            }
            return null;
        }

        static bool BindResidentBridge(out string error)
        {
            error = null;
            if (_bridgeBound) return true;
            string baseDir = ResidentAppRoot();
            string[] paths =
            {
                Path.Combine(baseDir, "sce_module", "libGameSearchResident.prx"),
                "/app0/sce_module/libGameSearchResident.prx",
                "libGameSearchResident.prx"
            };
            WriteLaunchLog("prx exists=" + File.Exists(paths[0]));
            int handle = int.MinValue;
            bool haveHandle = false;
            for (int i = 0; i < paths.Length; i++)
            {
                int loaded = LoadResidentPrx(paths[i]);
                WriteLaunchLog("prx " + paths[i] + " handle=0x" +
                    unchecked((uint)loaded).ToString("X"));
                if ((loaded & unchecked((int)0x80000000)) == 0)
                {
                    handle = loaded;
                    haveHandle = true;
                    break;
                }
            }
            if (!haveHandle)
            {
                error = "could not load libGameSearchResident.prx";
                return false;
            }

            if (!BindFn(handle, "gs_resident_mount_system", out _mountFn, out error) ||
                !BindFn(handle, "gs_resident_initialize", out _initFn, out error) ||
                !BindFn(handle, "gs_resident_get_app_id", out _getIdFn, out error) ||
                !BindFn(handle, "gs_resident_stop", out _stopFn, out error))
                return false;
            Delegate launch;
            if (!BindSymbol(handle, "gs_resident_launch", typeof(ResidentLaunchFn),
                out launch, out error))
                return false;
            _launchFn = (ResidentLaunchFn)launch;
            Delegate mkdir;
            Delegate unlink;
            Delegate chmod;
            Delegate copy;
            if (!BindSymbol(handle, "gs_resident_mkdir", typeof(ResidentPathFn),
                out mkdir, out error) ||
                !BindSymbol(handle, "gs_resident_unlink", typeof(ResidentPathFn),
                    out unlink, out error) ||
                !BindSymbol(handle, "gs_resident_chmod", typeof(ResidentChmodFn),
                    out chmod, out error) ||
                !BindSymbol(handle, "gs_resident_copy_file", typeof(ResidentCopyFn),
                    out copy, out error))
                return false;
            _mkdirFn = (ResidentPathFn)mkdir;
            _unlinkFn = (ResidentPathFn)unlink;
            _chmodFn = (ResidentChmodFn)chmod;
            _copyFn = (ResidentCopyFn)copy;
            _bridgeBound = true;
            WriteLaunchLog("prx bound handle=0x" + unchecked((uint)handle).ToString("X"));
            return true;
        }

        static int LoadResidentPrx(string path)
        {
            int loaded = unchecked((int)0x80000001);
            try { loaded = sceKernelLoadStartModule(path, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception ex)
            {
                WriteLaunchLog("kload " + path + " " + ex.GetType().Name);
            }
            if ((loaded & unchecked((int)0x80000000)) == 0) return loaded;
            try
            {
                int hinted = Orbis.Internals.Kernel.TryLoadStartModule(path);
                if ((hinted & unchecked((int)0x80000000)) == 0) return hinted;
            }
            catch { }
            return loaded;
        }

        static bool BindFn(int handle, string name, out ResidentIntFn fn, out string error)
        {
            Delegate value;
            fn = null;
            if (!BindSymbol(handle, name, typeof(ResidentIntFn), out value, out error))
                return false;
            fn = (ResidentIntFn)value;
            return true;
        }

        static bool BindSymbol(int handle, string name, Type type, out Delegate fn,
            out string error)
        {
            fn = null;
            error = null;
            IntPtr symbol = IntPtr.Zero;
            int rc;
            try { rc = sceKernelDlsym(handle, name, out symbol); }
            catch (Exception ex)
            {
                error = "dlsym " + name + " " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            if (rc != 0 || symbol == IntPtr.Zero)
            {
                error = "dlsym " + name + " 0x" + unchecked((uint)rc).ToString("X");
                return false;
            }
            try
            {
                fn = Marshal.GetDelegateForFunctionPointer(symbol, type);
                return fn != null;
            }
            catch (Exception ex)
            {
                error = "bind " + name + " " + ex.GetType().Name;
                return false;
            }
        }

        static int ResidentMountSystem()
        {
            if (_mountFn != null) return _mountFn();
            return gs_resident_mount_system();
        }

        static int ResidentInitialize()
        {
            if (_initFn != null) return _initFn();
            return gs_resident_initialize();
        }

        static int ResidentLaunch(uint userId)
        {
            if (_launchFn != null) return _launchFn(userId);
            return gs_resident_launch(userId);
        }

        static int ResidentGetAppId()
        {
            if (_getIdFn != null) return _getIdFn();
            return gs_resident_get_app_id();
        }

        static int ResidentStop()
        {
            if (_stopFn != null) return _stopFn();
            return gs_resident_stop();
        }

        static void WriteLastError(string body)
        {
            _lastError = body;
            WriteLaunchLog("error=" + (body ?? ""));
            try
            {
                Directory.CreateDirectory(AppSettings.DataDir);
                File.WriteAllText(Path.Combine(AppSettings.DataDir, "resident-error.txt"),
                    DateTime.UtcNow.ToString("o") + " " + (body ?? "") + "\n");
            }
            catch { }
        }

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_mount_system",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_resident_mount_system();

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_mount_system_data",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_resident_mount_system_data();

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_register_in_appdb",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_resident_register_in_appdb();

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_initialize",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_resident_initialize();

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_launch",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_resident_launch(uint userId);

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_launch_skip",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_resident_launch_skip(uint userId);

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_system_launch",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_resident_system_launch(uint userId);

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_launch_title",
            CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern int gs_resident_launch_title(string titleId, uint userId);

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_get_user_id",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_resident_get_user_id();

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_stop_title",
            CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern int gs_resident_stop_title(string titleId);

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_spawn_local",
            CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern int gs_resident_spawn_local(string ebootPath);

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_get_app_id",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_resident_get_app_id();

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_stop",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_resident_stop();

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_mkdir",
            CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern int gs_resident_mkdir(string path);

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_chmod",
            CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern int gs_resident_chmod(string path, int mode);

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_unlink",
            CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern int gs_resident_unlink(string path);

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_copy_file",
            CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern int gs_resident_copy_file(string src, string dst);

        [DllImport("libkernel", EntryPoint = "sceKernelLoadStartModule",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int sceKernelLoadStartModule(string path, uint argc, IntPtr argv,
            uint flags, IntPtr option, IntPtr result);

        [DllImport("libkernel", EntryPoint = "sceKernelDlsym",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int sceKernelDlsym(int handle, string name, out IntPtr result);
    }
}
