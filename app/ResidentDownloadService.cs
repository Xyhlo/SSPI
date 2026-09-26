using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal sealed class LegacyBgftAttachCoordinator
    {
        readonly object _gate = new object();

        internal bool ExecuteSerialized(Func<bool> action)
        {
            lock (_gate) return action();
        }

        internal bool TryRetireForeignOwner(string requestedId, string ownerId, int taskId,
            Func<string, int, bool> cleanup, Action<string, int> retain)
        {
            if (taskId < 0 || string.IsNullOrEmpty(ownerId) ||
                string.Equals(requestedId, ownerId, StringComparison.Ordinal)) return true;
            lock (_gate)
            {
                bool cleaned = false;
                try { cleaned = cleanup(ownerId, taskId); } catch { }
                if (cleaned) return true;
                try { retain(ownerId, taskId); } catch { }
                return false;
            }
        }

        internal bool TryAttach(Func<bool> stillCurrent, Func<int> register,
            Func<int, bool> publish, Func<int, bool> cleanup, Action<int> retain,
            out int taskId)
        {
            taskId = -1;
            lock (_gate)
            {
                if (!stillCurrent()) return false;
                int created = register();
                if (created < 0) return false;
                taskId = created;
                if (!stillCurrent())
                {
                    bool cleaned = false;
                    try { cleaned = cleanup(created); } catch { }
                    if (cleaned) taskId = -1;
                    else retain(created);
                    return false;
                }
                bool published = false;
                try { published = publish(created); } catch { }
                if (!published)
                {
                    retain(created);
                    return false;
                }
                return true;
            }
        }
    }

    internal sealed class ResidentDownloadStatus
    {
        public string Id;
        public string State;
        public long Done;
        public long Total;
        public long NetworkBytes = -1;
        public bool FullyServed;
        public string Error;
        public int ActiveRanges;
        public bool AutoInstall;
        public string Generation;
        public string WorkerBuild;
        public string WorkerEpoch;

        public bool Terminal
        {
            get
            {
                return State == "complete" || State == "failed" ||
                    State == "canceled" || State == "idle" || State == "submitted" || State == "installed" ||
                    (State == "staged" && !AutoInstall) || State == "released";
            }
        }
    }

    internal static class ResidentDownloadService
    {
        const string Version = "5.11-api3";
        const string PreviousVersion = "5.10-api3";
        const string HostDaemonTitleId = "NPXS20119";
        const int LncAppNotFound = unchecked((int)0x80940005);
        const long SystemAuthId = 0x3800000000000010;
        const string DaemonTitleId = "SRCH00002";
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
        static DateTime _nextActivationAttemptUtc;
        static int _activationAttemptSerial, _activationAttemptInProgress, _manualActivationRetryQueued;
        static string _bgftAttachId;
        static int _bgftAttachTask = -1;
        static string _bgftAttachGeneration;
        static int _bgftAttachInFlight;
        static readonly LegacyBgftAttachCoordinator LegacyBgftAttach = new LegacyBgftAttachCoordinator();
        static DateTime _nextBgftAttachAttemptUtc;
        static int _launchMaintenanceStarted;
        static int _launchMaintenanceStopped;
        static int _launchSkipRecorded, _launchMaintenanceAttempts, _launchMaintenanceRepaired;
        static readonly ManualResetEvent LaunchMaintenanceStop = new ManualResetEvent(false);
        static string _launchMaintenanceStatus;

        const string SharedIpcRoot = "/user/data/SSPI/resident";
        static string CanonicalIpcRoot { get { return Path.Combine(AppSettings.DataDir, "resident"); } }
        static string IpcRoot { get { return _liveHeartbeatRoot == SharedIpcRoot ? SharedIpcRoot : CanonicalIpcRoot; } }
        internal static string ArchiveJournalDirectory(string id)
        {
            string current = Path.Combine(IpcRoot, "archives", id);
            if (File.Exists(Path.Combine(current, "packages.txt"))) return current;
            string alternate = Path.Combine(IpcRoot == SharedIpcRoot ? CanonicalIpcRoot : SharedIpcRoot, "archives", id);
            return File.Exists(Path.Combine(alternate, "packages.txt")) ? alternate : current;
        }
        static string HeartbeatPath { get { return Path.Combine(IpcRoot, "heartbeat.txt"); } }
        static string JobPath { get { return Path.Combine(IpcRoot, "job.txt"); } }
        static string StatusPath { get { return Path.Combine(IpcRoot, "status.txt"); } }
        static string ControlPath { get { return Path.Combine(IpcRoot, "control.txt"); } }
        static string BgftPath { get { return Path.Combine(IpcRoot, "bgft.txt"); } }
        static string BgftOwnerPath { get { return Path.Combine(IpcRoot, "bgft_owner.txt"); } }

        /// <summary>Maps an IPC file under the application's resident root to the
        /// equivalent path under the shell worker's shared root. Returns null for
        /// paths outside the application root so unrelated files are never
        /// mirrored.</summary>
        static string MapToSharedIpcRoot(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string root = CanonicalIpcRoot;
            if (path.Length <= root.Length) return null;
            if (!path.StartsWith(root, StringComparison.Ordinal)) return null;
            char separator = path[root.Length];
            if (separator != '/' && separator != '\\') return null;
            return SharedIpcRoot + path.Substring(root.Length).Replace('\\', '/');
        }

        public static string LastError { get { lock (Gate) return _lastError; } }

        public static string LaunchMaintenanceStatus
        { get { return Volatile.Read(ref _launchMaintenanceStopped) != 0 ? null : Volatile.Read(ref _launchMaintenanceStatus); } }
        public static string LaunchMaintenanceMessage
        {
            get
            {
                switch (LaunchMaintenanceStatus)
                {
                    case "ready-after-repair": return "Background downloader files updated and ready.";
                    case "restart-required": return "Background downloader updated. Let active jobs finish, then restart PS4 and enable GoldHEN.";
                    case "unavailable": return "Background mode is waiting for the resident worker. Open General settings for its status.";
                    default: return null;
                }
            }
        }

        public static void StartLaunchMaintenance(bool enabled)
        {
            if (!enabled)
            {
                if (Volatile.Read(ref _launchMaintenanceStarted) == 0 &&
                    Interlocked.CompareExchange(ref _launchSkipRecorded, 1, 0) == 0)
                    try
                    {
                        new Thread(() =>
                        {
                            if (Volatile.Read(ref _launchMaintenanceStarted) == 0)
                                WriteLaunchMaintenanceSnapshot("disabled");
                        }) { IsBackground = true, Name = "Resident maintenance status" }.Start();
                    }
                    catch { }
                return;
            }
            if (Interlocked.CompareExchange(ref _launchMaintenanceStarted, 1, 0) != 0) return;
            Volatile.Write(ref _launchMaintenanceStatus, "checking");
            try
            {
                new Thread(() =>
                {
                    string outcome = null;
                    string snapshot = "canceled";
                    bool isPs4 = false;
                    try
                    {
                        if (!LaunchMaintenanceStop.WaitOne(0))
                        {
                            WriteLaunchMaintenanceSnapshot("runtime-probe");
                            string detail;
                            isPs4 = ProbeConsoleRuntime(sceKernelGetProcessTime, out detail);
                            SspiLog.Write("resident", "launch runtime_probe=" + (isPs4 ? "ready" : "failed") + " detail=" + detail);
                            if (isPs4)
                            {
                                WriteLaunchMaintenanceSnapshot("checking", true);
                                outcome = RunLaunchMaintenance(AttemptLaunchMaintenance,
                                    milliseconds => LaunchMaintenanceStop.WaitOne(milliseconds),
                                    () => LaunchMaintenanceStop.WaitOne(0));
                                snapshot = outcome ?? "canceled";
                            }
                            else
                            {
                                outcome = "unavailable"; snapshot = "runtime-unavailable";
                                WriteLastError("Resident runtime probe failed: " + detail + ". See logs/resident.log");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        outcome = snapshot = "unavailable";
                        WriteLastError("Resident initialization: " + ex.GetType().Name + " 0x" + unchecked((uint)ex.HResult).ToString("X8"));
                        SspiLog.Write("resident", "launch exception=" + ex.GetType().FullName + " hresult=0x" + ex.HResult.ToString("X8") + " stack=" + ex.StackTrace);
                    }
                    if (LaunchMaintenanceStop.WaitOne(0)) { outcome = null; snapshot = "canceled"; }
                    WriteLaunchMaintenanceSnapshot(snapshot, isPs4);
                    Volatile.Write(ref _launchMaintenanceStatus, outcome);
                }) { IsBackground = true, Name = "Resident launch maintenance" }.Start();
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _launchMaintenanceStatus, "unavailable");
                WriteLastError("Resident thread: " + ex.GetType().Name + " 0x" + unchecked((uint)ex.HResult).ToString("X8"));
                SspiLog.Write("resident", "launch thread_start_failed exception=" + ex.GetType().FullName + " hresult=0x" + ex.HResult.ToString("X8"));
            }
        }

        [DllImport("libkernel", CallingConvention = CallingConvention.Cdecl)]
        static extern ulong sceKernelGetProcessTime();

        internal static bool ProbeConsoleRuntime(Func<ulong> probe, out string detail)
        {
            try
            {
                // Escaping the sandbox changes visible filesystem roots. A successful
                // read-only call establishes libkernel availability, not GoldHEN's
                // separate process API or permission to inject another process.
                probe();
                detail = "libkernel-call-ok";
                return true;
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + " 0x" + ex.HResult.ToString("X8");
                return false;
            }
        }

        public static void StopLaunchMaintenance()
        {
            Interlocked.Exchange(ref _launchMaintenanceStopped, 1);
            LaunchMaintenanceStop.Set();
            Volatile.Write(ref _launchMaintenanceStatus, null);
        }

        static void WriteLaunchMaintenanceSnapshot(string status, bool isPs4 = false)
        {
            SspiLog.Write("resident", "launch status=" + status + " build=" + BuildIdentity.Label +
                " attempts=" + Volatile.Read(ref _launchMaintenanceAttempts) +
                " repaired=" + Volatile.Read(ref _launchMaintenanceRepaired));
            string temporary = null;
            try
            {
                // A host/configuration skip must not create a new application data tree.
                if (!Directory.Exists(AppSettings.DataDir)) return;
                Directory.CreateDirectory(IpcRoot);
                string path = Path.Combine(IpcRoot, "launch-maintenance.txt");
                temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                byte[] bytes = new UTF8Encoding(false).GetBytes("version=1\nworker=" + Version + "\nstatus=" + status +
                    "\nutc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) +
                    "\nattempts=" + Volatile.Read(ref _launchMaintenanceAttempts).ToString(CultureInfo.InvariantCulture) +
                    "\nrepaired=" + Volatile.Read(ref _launchMaintenanceRepaired).ToString(CultureInfo.InvariantCulture) + "\n");
                if (isPs4) { AtomicFile.WriteText(path, Encoding.UTF8.GetString(bytes)); return; }
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                // Managed replacement also works for quiet host skips, without native calls.
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            catch (Exception ex)
            {
                SspiLog.Write("resident", "launch snapshot_failed status=" + status + " exception=" + ex.GetType().Name + " hresult=0x" + ex.HResult.ToString("X8"));
            }
            finally { try { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); } catch { } }
        }

        internal sealed class LaunchMaintenanceAttempt
        {
            public string Status;
            public bool Repaired, Retryable;
            public int RetryDelayMs;
        }

        internal static string RunLaunchMaintenance(Func<LaunchMaintenanceAttempt> attempt,
            Func<int, bool> wait, Func<bool> stopped)
        {
            bool repaired = false;
            for (int number = 0; number < 2 && !stopped(); number++)
            {
                LaunchMaintenanceAttempt result = attempt();
                if (result == null || stopped()) return null;
                repaired |= result.Repaired;
                if (result.Status == "ready") return repaired ? "ready-after-repair" : "ready";
                if (result.Status == "restart-required") return result.Status;
                if (!result.Retryable || number == 1) return "unavailable";
                if (wait(Math.Max(1, Math.Min(30000, result.RetryDelayMs)))) return null;
            }
            return null;
        }

        static LaunchMaintenanceAttempt AttemptLaunchMaintenance()
        {
            Interlocked.Increment(ref _launchMaintenanceAttempts);
            SspiLog.Write("resident", "launch attempt=" + Volatile.Read(ref _launchMaintenanceAttempts) + " begin");
            var waiting = System.Diagnostics.Stopwatch.StartNew();
            while (!LaunchMaintenanceStop.WaitOne(0) && waiting.ElapsedMilliseconds < 5000)
            {
                if (!Monitor.TryEnter(Gate, 100)) continue;
                bool activationAttempt = false;
                try
                {
                    if (LaunchMaintenanceStop.WaitOne(0)) return null;
                    bool allowActivation = DateTime.UtcNow >= _nextActivationAttemptUtc;
                    if (allowActivation) _nextActivationAttemptUtc = DateTime.UtcNow.AddSeconds(30);
                    if (allowActivation) { BeginActivationAttempt(); activationAttempt = true; }
                    string pluginStatus; bool repaired;
                    // Always reconcile disk files, including when a current worker is alive.
                    bool staged = GoldHenPluginInstaller.TryEnsureInstalled(allowActivation, out pluginStatus, out repaired);
                    SspiLog.Write("resident", "launch attempt_result=" + pluginStatus + " activation_allowed=" + allowActivation + " repaired=" + repaired);
                    if (repaired) Interlocked.Exchange(ref _launchMaintenanceRepaired, 1);
                    if (LaunchMaintenanceStop.WaitOne(0)) return null;
                    bool stageFailed = ActivationFailedBeforeAcknowledgement(pluginStatus);
                    if (staged && !stageFailed)
                    {
                        if (RunningWorkerRequiresRestart)
                            return new LaunchMaintenanceAttempt { Status = "restart-required", Repaired = repaired };
                        DateTime until = DateTime.UtcNow.AddSeconds(5);
                        do
                        {
                            if (FreshHeartbeat())
                            {
                                _nextActivationAttemptUtc = DateTime.MinValue;
                                return new LaunchMaintenanceAttempt { Status = "ready", Repaired = repaired };
                            }
                            if (!allowActivation || LaunchMaintenanceStop.WaitOne(100)) break;
                        } while (DateTime.UtcNow < until);
                    }
                    if (LaunchMaintenanceStop.WaitOne(0)) return null;
                    WorkerHeartbeat loaded;
                    WriteLastError(stageFailed ? ExplainLoaderStatus(pluginStatus) :
                        TryReadHeartbeat(out loaded) && loaded.Current ? loaded.NotReadyReason :
                        _heartbeatReadError ?? ExplainLoaderStatus(pluginStatus));
                    return new LaunchMaintenanceAttempt
                    {
                        Status = "unavailable", Repaired = repaired,
                        Retryable = pluginStatus != "plugin-not-bundled",
                        RetryDelayMs = Math.Max(1, (int)Math.Ceiling((_nextActivationAttemptUtc - DateTime.UtcNow).TotalMilliseconds))
                    };
                }
                finally
                {
                    if (activationAttempt) Volatile.Write(ref _activationAttemptInProgress, 0);
                    Monitor.Exit(Gate);
                }
            }
            return LaunchMaintenanceStop.WaitOne(0) ? null : new LaunchMaintenanceAttempt
            { Status = "unavailable", Retryable = true, RetryDelayMs = 30000 };
        }

        static string StagedPath(int slot, string extension)
        { return Path.Combine(IpcRoot, "transfer-" + slot.ToString(CultureInfo.InvariantCulture) + "." + extension); }
        const int StagedSlotCount = 16;

        static int FindStagedSlot(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < StagedSlotCount; i++)
                try
                {
                    string[] lines = File.ReadAllLines(StagedPath(i, "job"));
                    if (IsStagedRecord(lines) && Decode(lines[1]) == id) return i;
                }
                catch { }
            return -1;
        }

        public static bool HasStagedJob(string id) { return FindStagedSlot(id) >= 0; }
        internal static bool IsStagedRecord(string[] lines)
        {
            return lines != null && ((lines.Length == 10 && lines[0] == "6") ||
                (lines.Length == 11 && lines[0] == "7") ||
                (((lines.Length == 14 && (lines[0] == "10" || lines[0] == "16")) ||
                    (lines.Length >= 15 && (lines[0] == "13" || lines[0] == "18") && ValidEncodedPassword(lines[14]) && ValidPasswordTrailer(lines, 15))) && IsGeneration(lines[11]) &&
                    (lines[13] == "0" || lines[13] == "1")));
        }

        static bool IsGeneration(string value)
        { return value != null && System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-f0-9]{32}$"); }

        static string StagedGeneration(int slot)
        { return ReadStagedGeneration(StagedPath(slot, "job")); }

        internal static bool TryGetStagedJobIdentity(string id, string destination, out string generation)
        {
            generation = null;
            int slot = FindStagedSlot(id);
            if (slot < 0) return false;
            try
            {
                string[] record = File.ReadAllLines(StagedPath(slot, "job"));
                if (!IsStagedRecord(record) || Decode(record[1]) != id || Decode(record[3]) != destination)
                    return false;
                if ((record[0] == "10" || record[0] == "13" || record[0] == "16" || record[0] == "18") &&
                    record.Length > 11 && IsGeneration(record[11])) generation = record[11];
                return true;
            }
            catch { return false; }
        }

        internal static bool TryGetJobIdentity(string id, string destination, out string generation)
        {
            if (TryGetStagedJobIdentity(id, destination, out generation)) return true;
            generation = null;
            try
            {
                string[] record = File.ReadAllLines(JobPath);
                if (record.Length < 4 || Decode(record[1]) != id || Decode(record[3]) != destination) return false;
                if (record[0] == "12" || record[0] == "14")
                {
                    int count;
                    if (!int.TryParse(record[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 1 || count > 512) return false;
                    int index = 10 + count * 5;
                    bool password = record[0] == "14";
                    if ((password ? ValidPasswordTrailer(record, index + 3) : record.Length == index + 2) &&
                        IsGeneration(record[index]) && (!password || ValidEncodedPassword(record[index + 2])))
                        generation = record[index];
                    return generation != null;
                }
                if (record[0] == "9" || record[0] == "11" || record[0] == "15" || record[0] == "17" || record[0] == "19")
                {
                    int index = 10;
                    bool password = record[0] == "15" || record[0] == "19";
                    if ((password ? ValidPasswordTrailer(record, index + 3) : record.Length == index + 2) &&
                        IsGeneration(record[index]) && (!password || ValidEncodedPassword(record[index + 2])))
                        generation = record[index];
                    return generation != null;
                }
                return record[0] == "8";
            }
            catch { return false; }
        }

        static string ReadStagedGeneration(string jobPath)
        {
            try
            {
                string[] record = File.ReadAllLines(jobPath);
                return IsStagedRecord(record) && (record[0] == "10" || record[0] == "13" || record[0] == "16" || record[0] == "18") ? record[11] : null;
            }
            catch { return null; }
        }

        public static bool OwnsBgftLifetime(string id)
        { int slot = FindStagedSlot(id); return slot >= 0 ? StagedGeneration(slot) != null : ActiveGeneration(id) != null; }

        static string ActiveGeneration(string id)
        {
            try
            {
                string[] lines = File.ReadAllLines(JobPath);
                if (lines.Length < 12 || Decode(lines[1]) != id) return null;
                int index = 10;
                if (lines[0] == "12" || lines[0] == "14")
                {
                    int count;
                    if (!int.TryParse(lines[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out count) || count < 1 || count > 512) return null;
                    index += count * 5;
                }
                else if (lines[0] != "9" && lines[0] != "11" && lines[0] != "15" && lines[0] != "17" && lines[0] != "19") return null;
                bool password = lines[0] == "14" || lines[0] == "15" || lines[0] == "19";
                return (password ? ValidPasswordTrailer(lines, index + 3) : lines.Length == index + 2) && IsGeneration(lines[index]) &&
                    (!password || ValidEncodedPassword(lines[index + 2])) ? lines[index] : null;
            }
            catch { return null; }
        }

        internal static bool TryStagingRoot(string destination, out string root)
        {
            root = null;
            if (string.IsNullOrEmpty(destination) || destination.Length >= 1024 || destination.Contains("..") ||
                destination.IndexOf('\\') >= 0) return false;
            foreach (char character in destination) if (char.IsControl(character)) return false;
            const string internalRoot = "/data/SSPI/downloads/";
            if (destination.StartsWith(internalRoot, StringComparison.Ordinal)) root = internalRoot.TrimEnd('/');
            else if (destination.Length > 23 && destination.StartsWith("/mnt/usb", StringComparison.Ordinal) &&
                destination[8] >= '0' && destination[8] <= '7' &&
                destination.Substring(9).StartsWith("/SSPI/staging/", StringComparison.Ordinal))
                root = destination.Substring(0, 22);
            if (root == null) return false;
            string leaf = destination.Substring(root.Length + 1);
            return leaf.Length > 0 && leaf.IndexOf('/') < 0;
        }

        static string StorageToken(string root)
        {
            if (!root.StartsWith("/mnt/usb", StringComparison.Ordinal)) return "";
            // Do not create a mount or staging directory when a removable device is absent.
            string[] components = root.Split('/'); string current = "";
            for (int i = 1; i < components.Length; i++)
            {
                current += "/" + components[i];
                if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Reconnect the selected staging drive; files are retained");
            }
            string marker = root + "/.sspi-volume-id";
            if (File.Exists(marker) && (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked staging identity files are not supported");
            if (!File.Exists(marker))
            {
                string token = Guid.NewGuid().ToString("N");
                using (var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { byte[] bytes = Encoding.ASCII.GetBytes(token + "\n"); file.Write(bytes, 0, bytes.Length); file.Flush(true); }
            }
            string identity = File.ReadAllText(marker).Trim();
            if (!IsGeneration(identity)) throw new IOException("The selected staging drive identity is invalid");
            return identity;
        }

        internal static string CreateStagedRecord(string id, string url, string destination, string titleId,
            string sha256, string contentId, long size, int expectedKind, int rangeCount, bool autoInstall,
            string afterJobId, string generation, string storageToken)
        {
            return string.Join("\n", new[] { "10", Encode(id), Encode(url), Encode(destination), Encode(titleId),
                Encode(sha256), Encode(contentId), size.ToString(CultureInfo.InvariantCulture),
                DownloadTransferSettings.ConnectionsFor(url, rangeCount).ToString(CultureInfo.InvariantCulture),
                expectedKind.ToString(CultureInfo.InvariantCulture), Encode(afterJobId), generation,
                storageToken, autoInstall ? "1" : "0" }) + "\n";
        }
        internal static bool ValidArchivePassword(string password)
        {
            if (password == null) return true;
            try { return password.IndexOf('\0') < 0 && new UTF8Encoding(false, true).GetByteCount(password) <= 256; }
            catch (EncoderFallbackException) { return false; }
        }

        internal static string CreateNativeBgftRecord(string id, string url, string destination, string titleId,
            string contentId, long size, int expectedKind, int rangeCount, bool autoInstall,
            string afterJobId, string generation)
        {
            if (!autoInstall || (expectedKind != 6 && expectedKind != 8))
                throw new IOException("Native BGFT requires a base game or update PKG");
            string record = CreateStagedRecord(id, url, destination, titleId, "", contentId, size,
                expectedKind, rangeCount, autoInstall, afterJobId, generation, "");
            return "16" + record.Substring(2);
        }
        internal static string CreateLocalSourceRecord(string id, string source, string titleId, string contentId,
            long size, int expectedKind, string afterJobId, string generation, string storageToken, string password)
        {
            if (!ValidArchivePassword(password)) throw new IOException("Invalid archive password");
            string record = CreateStagedRecord(id, "", source, titleId, "", contentId, size, expectedKind,
                1, true, afterJobId, generation, storageToken);
            return "18" + record.Substring(2) + Encode(password) + "\n";
        }
        static bool ValidEncodedPassword(string value)
        {
            try { return ValidArchivePassword(new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value))); }
            catch { return false; }
        }

        static bool ValidPasswordTrailer(string[] lines, int start)
        {
            if (lines.Length == start) return true;
            int count;
            if (lines.Length < start + 2 || lines[start] != "password-fallbacks-v1" ||
                !int.TryParse(lines[start + 1], out count) || count < 1 || count > 3 || lines.Length != start + 2 + count) return false;
            for (int i = start + 2; i < lines.Length; i++)
                if (lines[i].Length == 0 || !ValidEncodedPassword(lines[i])) return false;
            return true;
        }

        internal static string AppendPasswordFallbacks(string record, string[] passwords)
        {
            if (passwords == null || passwords.Length == 0) return record;
            if (passwords.Length > 3) throw new IOException("Too many archive password fallbacks");
            var body = new StringBuilder(record).Append("password-fallbacks-v1\n").Append(passwords.Length).Append('\n');
            foreach (string password in passwords)
            {
                if (string.IsNullOrEmpty(password) || !ValidArchivePassword(password)) throw new IOException("Invalid archive password fallback");
                body.Append(Encode(password)).Append('\n');
            }
            return body.ToString();
        }
        internal static string CreatePasswordStagedRecord(string id, string url, string destination, string titleId,
            string sha256, string contentId, long size, int expectedKind, int rangeCount, bool autoInstall,
            string afterJobId, string generation, string storageToken, string password)
        {
            if (!ValidArchivePassword(password)) throw new IOException("Archive password exceeds 256 UTF-8 bytes or is invalid");
            string record = CreateStagedRecord(id, url, destination, titleId, sha256, contentId, size,
                expectedKind, rangeCount, autoInstall, afterJobId, generation, storageToken);
            return string.IsNullOrEmpty(password) ? record : "13" + record.Substring(2) + Encode(password) + "\n";
        }
        public static bool HasStagedDownloader { get { return FreshHeartbeat(); } }
        internal static bool HasPendingOwnership
        { get { return StagedTransfersActive || File.Exists(JobPath); } }
        public static bool StagedTransfersActive
        {
            get
            {
                for (int i = 0; i < StagedSlotCount; i++)
                    if (File.Exists(StagedPath(i, "job"))) return true;
                return false;
            }
        }

        public static bool TryStartStagedPackage(string id, string url, string destination, string titleId,
            string sha256, string contentId, long size, int expectedKind, int rangeCount,
            out string error, out bool busy)
        { return TryStartStagedPackage(id, url, destination, titleId, sha256, contentId, size, expectedKind,
            rangeCount, false, null, out error, out busy); }

        public static bool TryStartStagedPackage(string id, string url, string destination, string titleId,
            string sha256, string contentId, long size, int expectedKind, int rangeCount, bool autoInstall,
            string afterJobId, out string error, out bool busy)
        { return TryStartStagedPackageWithPassword(id, url, destination, titleId, sha256, contentId, size,
            expectedKind, rangeCount, autoInstall, afterJobId, null, out error, out busy); }

        public static bool TryStartStagedPackageWithPassword(string id, string url, string destination, string titleId,
            string sha256, string contentId, long size, int expectedKind, int rangeCount, bool autoInstall,
            string afterJobId, string password, out string error, out bool busy, string[] passwordFallbacks = null)
        {
            return TryPublishStagedPackage(id, url, destination, titleId, sha256, contentId, size,
                expectedKind, rangeCount, autoInstall, afterJobId, password, false, false,
                null, out error, out busy, passwordFallbacks);
        }

        internal static bool TryStartStagedPackageWithPasswordGeneration(string id, string url, string destination, string titleId,
            string sha256, string contentId, long size, int expectedKind, int rangeCount, bool autoInstall,
            string afterJobId, string password, string generation, out string error, out bool busy,
            string[] passwordFallbacks = null)
        {
            return TryPublishStagedPackage(id, url, destination, titleId, sha256, contentId, size,
                expectedKind, rangeCount, autoInstall, afterJobId, password, false, false,
                generation, out error, out busy, passwordFallbacks);
        }

        public static bool TryStartNativeBgftPackage(string id, string url, string destination, string titleId,
            string contentId, long size, int expectedKind, int rangeCount, bool autoInstall,
            string afterJobId, out string error, out bool busy)
        {
            return TryPublishStagedPackage(id, url, destination, titleId, "", contentId, size,
                expectedKind, rangeCount, autoInstall, afterJobId, null, true, false,
                null, out error, out busy);
        }

        internal static bool TryStartNativeBgftPackageWithGeneration(string id, string url, string destination, string titleId,
            string contentId, long size, int expectedKind, int rangeCount, bool autoInstall,
            string afterJobId, string generation, out string error, out bool busy)
        {
            return TryPublishStagedPackage(id, url, destination, titleId, "", contentId, size,
                expectedKind, rangeCount, autoInstall, afterJobId, null, true, false,
                generation, out error, out busy);
        }

        public static bool TryStartLocalSource(string id, string source, string titleId, string contentId,
            long size, int expectedKind, string afterJobId, string password, out string error, out bool busy, string[] passwordFallbacks = null)
        {
            return TryPublishStagedPackage(id, "", source, titleId, "", contentId, size,
                expectedKind, 1, true, afterJobId, password, false, true,
                null, out error, out busy, passwordFallbacks);
        }

        internal static bool TryStartLocalSourceWithGeneration(string id, string source, string titleId, string contentId,
            long size, int expectedKind, string afterJobId, string password, string generation,
            out string error, out bool busy, string[] passwordFallbacks = null)
        {
            return TryPublishStagedPackage(id, "", source, titleId, "", contentId, size,
                expectedKind, 1, true, afterJobId, password, false, true,
                generation, out error, out busy, passwordFallbacks);
        }

        static bool TryPublishStagedPackage(string id, string url, string destination, string titleId,
            string sha256, string contentId, long size, int expectedKind, int rangeCount, bool autoInstall,
            string afterJobId, string password, bool nativeBgft, bool localSource, string generation,
            out string error, out bool busy, string[] passwordFallbacks = null)
        {
            error = null; busy = false;
            if (!ValidArchivePassword(password) || (expectedKind != 0 && (!string.IsNullOrEmpty(password) || (passwordFallbacks != null && passwordFallbacks.Length > 0))))
            { error = "Resident archive password metadata is invalid"; return false; }
            if (!string.IsNullOrEmpty(generation) && !IsGeneration(generation))
            { error = "Resident staged generation is invalid"; return false; }
            Uri address; string storageRoot = null, canonical;
            bool sourceValid = localSource ? LocalInstallSource.TryNormalizePath(destination, out canonical)
                : TryStagingRoot(destination, out storageRoot);
            if (localSource && sourceValid) storageRoot = destination.Substring(0, 9) + "/SSPI/staging";
            if (string.IsNullOrWhiteSpace(id) || id.Length > 190 ||
                !System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]+$") ||
                (!localSource && (!Uri.TryCreate(url, UriKind.Absolute, out address) ||
                (address.Scheme != "http" && address.Scheme != "https") ||
                !string.IsNullOrEmpty(address.UserInfo) || url.Length >= 8192)) ||
                !sourceValid ||
                (expectedKind != 0 && (string.IsNullOrEmpty(contentId) || contentId.Length != 36)) ||
                size < (localSource && expectedKind == 0 ? 8 : LoopbackPkgFeeder.HeaderBytes) || size > 8L * 1024 * 1024 * 1024 * 1024 ||
                (expectedKind != 0 && (expectedKind < 6 || expectedKind > 8)))
            { error = "Resident staged package metadata is invalid"; return false; }
            if (nativeBgft && (!autoInstall || storageRoot != "/data/SSPI/downloads" ||
                (expectedKind != 6 && expectedKind != 8) || !string.IsNullOrEmpty(sha256) || !string.IsNullOrEmpty(password)))
            { error = "Native BGFT package metadata is invalid"; return false; }
            if ((!string.IsNullOrEmpty(afterJobId) &&
                (afterJobId == id || afterJobId.Length > 190 ||
                !System.Text.RegularExpressions.Regex.IsMatch(afterJobId, "^[A-Za-z0-9_-]+$"))))
            { error = "Resident installation dependency is invalid"; return false; }
            lock (Gate)
            {
                if (!EnsureAvailableLocked(out error) || !HasStagedDownloader) return false;
                if (File.Exists(JobPath) && !IsLocalStagedInstall())
                { busy = true; error = "The resident archive or installation worker is occupied"; return false; }
                int slot = FindStagedSlot(id);
                if (slot >= 0)
                {
                    string[] current;
                    try { current = File.ReadAllLines(StagedPath(slot, "job")); }
                    catch { busy = true; error = "Another resident job is active"; return false; }
                    string existingGeneration = (IsStagedRecord(current) && current.Length > 11 &&
                        (current[0] == "10" || current[0] == "13" || current[0] == "16" || current[0] == "18") &&
                        IsGeneration(current[11])) ? current[11] : null;
                    if (!IsStagedRecord(current) || Decode(current[1]) != id || Decode(current[3]) != destination ||
                        (!string.IsNullOrEmpty(generation) &&
                            !string.Equals(existingGeneration ?? "", generation, StringComparison.Ordinal)))
                    {
                        busy = true; error = "Another resident job is active"; return false;
                    }
                    ResidentDownloadStatus existing;
                    bool retiring = TryGetStagedStatus(id, out existing) && existing.Terminal;
                    try
                    {
                        string[] control = File.ReadAllLines(StagedPath(slot, "control"));
                        retiring |= control.Length >= 3 && (control[0] == "1" ||
                            (control[0] == "2" && control.Length == 4 && control[3] == StagedGeneration(slot))) && Decode(control[1]) == id &&
                            (control[2] == "cancel" || control[2] == "release");
                    }
                    catch { }
                    if (retiring) { busy = true; error = "The previous resident attempt is still retiring"; return false; }
                    return true;
                }
                for (int i = 0; i < StagedSlotCount; i++)
                    try
                    {
                        string[] other = File.ReadAllLines(StagedPath(i, "job"));
                        if (other.Length >= 4 && Decode(other[3]) == destination)
                        { busy = true; error = "Another resident job owns this destination"; return false; }
                    }
                    catch { }
                for (int i = 0; i < StagedSlotCount; i++)
                    if (!File.Exists(StagedPath(i, "job"))) { slot = i; break; }
                if (slot < 0) { busy = true; error = "The resident transfer queue is full"; return false; }
                try
                {
                    if (localSource)
                    {
                        LocalInstallSource.RequireAvailable(destination);
                        AppSettings.RequireStaging(storageRoot);
                    }
                    string storageToken = StorageToken(storageRoot);
                    if (string.IsNullOrEmpty(generation)) generation = Guid.NewGuid().ToString("N");
                    File.Delete(StagedPath(slot, "status"));
                    File.Delete(StagedPath(slot, "control"));
                    // A retry with the same job ID must earn a new installation receipt.
                    File.Delete(Path.Combine(IpcRoot, "installed-" + id + ".txt"));
                    string record = localSource
                        ? CreateLocalSourceRecord(id, destination, titleId, contentId, size, expectedKind,
                            afterJobId, generation, storageToken, password)
                        : nativeBgft
                        ? CreateNativeBgftRecord(id, url, destination, titleId, contentId, size,
                            expectedKind, rangeCount, autoInstall, afterJobId, generation)
                        : CreatePasswordStagedRecord(id, url, destination, titleId, sha256, contentId,
                            size, expectedKind, rangeCount, autoInstall, afterJobId, generation, storageToken, password);
                    if (passwordFallbacks != null && passwordFallbacks.Length > 0)
                    {
                        if (record.StartsWith("10\n", StringComparison.Ordinal)) record = "13" + record.Substring(2) + Encode(password) + "\n";
                        record = AppendPasswordFallbacks(record, passwordFallbacks);
                    }
                    WriteAtomic(StagedPath(slot, "job"), record);
                    return true; // Durable publication transfers ownership; do not fall back after this point.
                }
                catch (Exception ex) { error = "Resident staged handoff failed: " + ex.Message; return false; }
            }
        }

        static bool IsLocalStagedInstall()
        {
            try { using (var reader = new StreamReader(JobPath)) { string version = reader.ReadLine(); return version == "8" || version == "9" || version == "11" || version == "12" || version == "14" || version == "15" || version == "17" || version == "19"; } }
            catch { return false; }
        }

        public static bool TryGetStagedStatus(string id, out ResidentDownloadStatus status)
        {
            status = null;
            for (int i = 0; i < StagedSlotCount; i++)
            {
                string statusPath = StagedPath(i, "status");
                string jobPath = StagedPath(i, "job");
                if (TryGetStagedStatusAt(statusPath, jobPath, id, out status)) return true;
                string sharedStatusPath = MapToSharedIpcRoot(statusPath);
                if (sharedStatusPath != null &&
                    TryGetStagedStatusAt(sharedStatusPath, MapToSharedIpcRoot(jobPath) ?? jobPath, id, out status)) return true;
            }
            return false;
        }

        static bool TryGetStagedStatusAt(string statusPath, string jobPath, string id,
            out ResidentDownloadStatus status)
        {
            status = null;
            try
            {
                string[] lines = File.ReadAllLines(statusPath);
                long done, total; int lanes = 0;
                if (lines.Length < 7 || lines[0] != "1" || Decode(lines[1]) != id ||
                    !long.TryParse(lines[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out done) ||
                    !long.TryParse(lines[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out total) ||
                    done < 0 || total < 0 || (done > total && !(lines[2] == "extracting" && total == 0))) return false;
                if (lines.Length > 7) int.TryParse(lines[7], out lanes);
                string generation = ReadStagedGeneration(jobPath);
                if (generation != null && (lines.Length < 13 || lines[10] != generation)) return false;
                if (lines.Length > 10 && !string.IsNullOrEmpty(lines[10]) && lines[10] != generation) return false;
                status = new ResidentDownloadStatus { Id = id, State = lines[2], Done = done, Total = total,
                    FullyServed = false, Error = Decode(lines[6]), ActiveRanges = Math.Max(0, Math.Min(DownloadTransferSettings.MaxRangeCount, lanes)),
                    AutoInstall = lines.Length > 8 && lines[8] == "1", NetworkBytes = ReadNetworkBytes(lines, 9),
                    Generation = lines.Length > 10 ? lines[10] : null,
                    WorkerBuild = lines.Length > 11 ? lines[11] : null, WorkerEpoch = lines.Length > 12 ? lines[12] : null };
                return true;
            }
            catch { return false; }
        }

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

            ResidentDownloadStatus previousStatus;
            if (TryReadStatus(out previousStatus) &&
                (previousStatus.State == "failed" || previousStatus.State == "canceled"))
            {
                WriteControl(previousStatus.Id, "release");
                DateTime releasedBy = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < releasedBy)
                {
                    ResidentDownloadStatus released;
                    if (!TryReadStatus(out released) || released.State == "idle") break;
                    Thread.Sleep(100);
                }
            }

            string publishError = null;
            bool publishBusy = false;
            bool jobPublished = LegacyBgftAttach.ExecuteSerialized(() =>
            {
                string ownerError;
                if (!RetirePreviousLegacyBgftOwner(out ownerError))
                { publishError = ownerError; return false; }

                lock (Gate)
                {
                    if (!EnsureAvailableLocked(out publishError)) return false;

                    if (StagedTransfersActive)
                    { publishBusy = true; publishError = "Resident range transfers are active"; return false; }

                    ResidentDownloadStatus current;
                    if (TryReadStatus(out current) && current.State != "idle" &&
                        !string.Equals(current.Id, id, StringComparison.Ordinal))
                    {
                        publishBusy = true;
                        publishError = "Another PS4 system download owns the resident feeder";
                        return false;
                    }

                    try
                    {
                        Directory.CreateDirectory(IpcRoot);
                        // The serialized ownership check above retires any prior task before
                        // its durable receipt is cleared for this new attempt.
                        File.Delete(BgftPath);
                        File.Delete(BgftOwnerPath);
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
                            DownloadTransferSettings.ConnectionsFor(url, rangeCount)
                                .ToString(CultureInfo.InvariantCulture)
                        }));
                    }
                    catch (Exception ex)
                    {
                        publishError = "Resident job write failed: " + ex.Message;
                        _lastError = publishError;
                        return false;
                    }
                }
                return true;
            });
            error = publishError;
            busy = publishBusy;
            if (!jobPublished) return false;

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

        static bool RetirePreviousLegacyBgftOwner(out string error)
        {
            string failure = null;
            bool retired = LegacyBgftAttach.ExecuteSerialized(() =>
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    string owner = null, generation = null;
                    int task = -1;
                    lock (Gate)
                    {
                        if (_bgftAttachTask >= 0)
                        {
                            owner = _bgftAttachId;
                            generation = _bgftAttachGeneration;
                            task = _bgftAttachTask;
                        }
                    }
                    if (task < 0 && !TryReadAttachedBgft(out owner, out task))
                    {
                        if (!File.Exists(BgftPath) && !File.Exists(BgftOwnerPath)) return true;
                        failure = "The previous BGFT attachment receipt is invalid; refusing to replace it";
                        return false;
                    }
                    if (task < 0 || string.IsNullOrEmpty(owner))
                    {
                        failure = "The previous BGFT attachment owner is unknown; refusing to replace it";
                        return false;
                    }
                    if (!CleanupLegacyBgftTask(owner, task))
                    {
                        RetainLegacyBgftTask(owner, generation, task);
                        failure = "The previous BGFT task still owns the attachment receipt";
                        return false;
                    }
                }
                if (File.Exists(BgftPath) || File.Exists(BgftOwnerPath))
                {
                    failure = "The previous BGFT attachment receipt could not be retired";
                    return false;
                }
                return true;
            });
            error = failure;
            return retired;
        }

        public static bool TryStartArchive(string id, string destination, string titleId, string expectedContentId,
            System.Collections.Generic.IList<ArchiveVolume> volumes,
            System.Collections.Generic.IList<string> paths, out string error)
        { return TryStartArchive(id, destination, titleId, expectedContentId, volumes, paths, Guid.NewGuid().ToString("N"), out error); }

        public static bool TryStartArchive(string id, string destination, string titleId, string expectedContentId,
            System.Collections.Generic.IList<ArchiveVolume> volumes,
            System.Collections.Generic.IList<string> paths, string generation, out string error)
        { return TryStartArchiveWithPassword(id, destination, titleId, expectedContentId, volumes, paths, generation, null, out error); }

        public static bool TryStartArchiveWithPassword(string id, string destination, string titleId, string expectedContentId,
            System.Collections.Generic.IList<ArchiveVolume> volumes,
            System.Collections.Generic.IList<string> paths, string generation, string password, out string error, string[] passwordFallbacks = null)
        {
            lock (Gate)
            {
                if (!ValidArchivePassword(password)) { error = "Archive password exceeds 256 UTF-8 bytes or is invalid"; return false; }
                if (!IsGeneration(generation)) { error = "Resident ownership generation is invalid"; return false; }
                if (!EnsureAvailableLocked(out error)) return false;
                if (StagedTransfersActive) { error = "Resident range transfers are active"; return false; }
                if (File.Exists(JobPath))
                {
                    // Publication owns the singleton until its worker retires it. A retry
                    // cannot replace the generation of an existing writer or installer.
                    try
                    {
                        string[] existing = File.ReadAllLines(JobPath);
                        if (existing.Length > 1 && Decode(existing[1]) == id && ActiveGeneration(id) == generation)
                        { error = null; return true; }
                    }
                    catch { }
                    error = "Another resident job is active"; return false;
                }
                ResidentDownloadStatus current;
                if (TryReadStatus(out current) && current.State != "idle" && current.Id != id)
                { error = "Another resident job is active"; return false; }
                if (volumes == null || paths == null || volumes.Count == 0 || volumes.Count > 512 || volumes.Count != paths.Count)
                { error = "Archive volume metadata is incomplete"; return false; }
                try
                {
                    string root;
                    if (!TryStagingRoot(destination, out root) || paths[0] != destination)
                        throw new IOException("Resident archive destination is invalid");
                    foreach (string path in paths)
                    {
                        string volumeRoot;
                        if (!TryStagingRoot(path, out volumeRoot) || volumeRoot != root)
                            throw new IOException("Resident archive volumes must share the selected staging drive");
                    }
                    int rangeCount = DownloadTransferSettings.ClampRangeCount(NetHttp.DownloadRangeCount);
                    foreach (ArchiveVolume volume in volumes)
                        rangeCount = DownloadTransferSettings.ConnectionsFor(volume.Url, rangeCount);
                    string storageToken = StorageToken(root);
                    var body = new StringBuilder();
                    bool hasPassword = !string.IsNullOrEmpty(password) || (passwordFallbacks != null && passwordFallbacks.Length > 0);
                    body.Append(hasPassword ? "14\n" : "12\n").Append(Encode(id)).Append('\n').Append(Encode(volumes[0].Url)).Append('\n')
                        .Append(Encode(destination)).Append('\n').Append(Encode(titleId)).Append("\n\n").Append(Encode(expectedContentId)).Append('\n')
                        .Append(volumes[0].Size.ToString(CultureInfo.InvariantCulture)).Append("\n").Append(rangeCount).Append("\n")
                        .Append(volumes.Count.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    for (int i = 0; i < volumes.Count; i++)
                    {
                        ArchiveVolume volume = volumes[i];
                        body.Append(Encode(ArchiveVolumeSet.DecoderName(volume.Name))).Append('\n').Append(Encode(volume.Url)).Append('\n')
                            .Append(Encode(paths[i])).Append('\n').Append(Encode(volume.Sha256)).Append('\n')
                            .Append(volume.Size.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    }
                    body.Append(generation).Append('\n').Append(storageToken).Append('\n');
                    if (hasPassword) body.Append(Encode(password)).Append('\n');
                    WriteAtomic(JobPath, AppendPasswordFallbacks(body.ToString(), passwordFallbacks));
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
                if (StagedTransfersActive) { error = "Resident range transfers are active"; return false; }
                if (File.Exists(JobPath) && !HasJob(id))
                { error = "Another resident job is active"; return false; }
                ResidentDownloadStatus current;
                if (TryReadStatus(out current) && current.State != "idle" && current.Id != id)
                { error = "Another resident job is active"; return false; }
                try
                {
                    if (expectedKind < 6 || expectedKind > 8) throw new IOException("Package type is unknown");
                    WriteAtomic(JobPath, string.Join("\n", new[] { "5", Encode(id), Encode(url), Encode(destination),
                        Encode(titleId), Encode(sha256), Encode(contentId), size.ToString(CultureInfo.InvariantCulture), DownloadTransferSettings.ConnectionsFor(url, NetHttp.DownloadRangeCount).ToString(CultureInfo.InvariantCulture), expectedKind.ToString(CultureInfo.InvariantCulture) }));
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
            if (!AppSettings.DataDirWritable)
            {
                error = AppSettings.DataDirError ?? "SSPI data directory is not writable: " + AppSettings.DataDir;
                WriteLastError(error); return false;
            }
            if (AppSettings.DataMigrationPending)
            {
                string staged; bool repaired;
                GoldHenPluginInstaller.TryEnsureInstalled(false, out staged, out repaired);
                error = AppSettings.DataMigrationNotice;
                WriteLastError(error); return false;
            }
            try { Directory.CreateDirectory(IpcRoot); }
            catch (Exception ex)
            {
                error = "Resident coordination directory is unavailable (" + IpcRoot + "): " + ex.Message;
                SspiLog.Write("resident", "operation=create coordination directory path=" + IpcRoot +
                    " exception=" + ex.GetType().FullName + " hresult=0x" + ex.HResult.ToString("X8") + " " + ex);
                WriteLastError(error); return false;
            }
            if (FreshHeartbeat()) { _nextActivationAttemptUtc = DateTime.MinValue; return true; }
            if (DateTime.UtcNow < _nextActivationAttemptUtc) { error = _lastError; return false; }
            // Retry promptly like the 5.10 beta: one transient loader failure must not
            // suppress activation for the rest of the app session.
            _nextActivationAttemptUtc = DateTime.UtcNow.AddSeconds(2);
            BeginActivationAttempt();
            try
            {
                string pluginStatus;
                bool pluginStaged = GoldHenPluginInstaller.TryEnsureInstalled(out pluginStatus);
                if (pluginStaged)
                {
                    if (ActivationFailedBeforeAcknowledgement(pluginStatus))
                    {
                        error = ExplainLoaderStatus(pluginStatus);
                        WriteLastError(error); return false;
                    }
                    if (RunningWorkerRequiresRestart)
                    {
                        error = "Downloader updated: let active background jobs finish, then restart PS4 and re-enable GoldHEN to activate the new worker";
                        WriteLastError(error); return false;
                    }
                    // Only an acknowledged shell worker can own the handoff.
                    DateTime acknowledgeBy = DateTime.UtcNow.AddSeconds(5);
                    while (DateTime.UtcNow < acknowledgeBy)
                    {
                        if (FreshHeartbeat()) { _nextActivationAttemptUtc = DateTime.MinValue; return true; }
                        Thread.Sleep(100);
                    }
                    WorkerHeartbeat loaded;
                    error = TryReadHeartbeat(out loaded) && loaded.Current ? loaded.NotReadyReason :
                        _heartbeatReadError ?? ExplainLoaderStatus(pluginStatus);
                    _lastError = error;
                    WriteLastError(error);
                    return false;
                }
                error = ExplainLoaderStatus(pluginStatus);
                _lastError = error;
                WriteLastError(error);
                return false;
            }
            finally { Volatile.Write(ref _activationAttemptInProgress, 0); }
        }

        static void BeginActivationAttempt()
        {
            Volatile.Write(ref _activationAttemptInProgress, 1);
            Interlocked.Increment(ref _activationAttemptSerial);
        }

        internal static string ExplainLoaderStatus(string status)
        {
            if (!string.IsNullOrEmpty(status) && status.StartsWith("native-binding-failed:", StringComparison.Ordinal))
                return "SSPI native binding failed: " + status.Substring("native-binding-failed:".Length).Trim() + ". See logs/resident.log";
            if (!string.IsNullOrEmpty(status) && status.StartsWith("plugin-stage-failed:", StringComparison.Ordinal))
                return "Background worker staging failed: " + status.Substring("plugin-stage-failed:".Length).Trim() + ". See logs/resident.log";
            if (status == "no-goldhen") return "GoldHEN data directory is unavailable; SSPI could not verify filesystem access. See logs/startup.log";
            if (status == "plugin-not-bundled") return "Background worker files are missing. Reinstall this SSPI build";
            if (status == "shell-loader-busy") return "GoldHEN is busy loading a worker. The queue will retry";
            if (status == "shell-loader-lock-unavailable") return "Resident startup cannot access its loader lock. See logs/resident.log";
            if (status == "shell-data-directory-unavailable") return "Resident startup cannot use /data/SSPI or its resident directory. Check storage access; see logs/startup.log and logs/resident.log";
            if (status == "shell-canonical-storage-unavailable") return "Resident is loaded, but cannot access /data/SSPI from SceShellUI. Background jobs are waiting for shared storage access. See logs/combined.log";
            if (status == "shell-migration-pending") return "SSPI data migration must finish before background jobs can start. See logs/startup.log";
            if (status == "shell-capabilities-unavailable") return "Resident is loaded, but transfer or BGFT initialization is unavailable. See logs/resident.log";
            if (status == "shell-no-heartbeat") return "GoldHEN accepted the load request, but the worker did not start. Retry or restart PS4; see logs/resident.log";
            if (!string.IsNullOrEmpty(status) && status.StartsWith("shell-api-rejected:", StringComparison.Ordinal))
            {
                string detail = status.Substring("shell-api-rejected:".Length).Trim();
                // ENOSYS (78): the running jailbreak (for example HEN) has no GoldHEN
                // plugin loader, so background mode cannot start on this boot.
                if (detail.Contains("(-78)"))
                    return "Background mode needs GoldHEN's plugin loader, which this jailbreak does not provide (API query " + detail +
                        "). Use In-app download mode and keep SSPI open while downloading";
                return "Resident API query returned " + detail + ". Module load was not attempted. See logs/combined.log";
            }
            if (!string.IsNullOrEmpty(status) && status.StartsWith("shell-process-query-rejected:", StringComparison.Ordinal))
                return "Resident process query failed: " + status.Substring("shell-process-query-rejected:".Length).Trim() +
                    ". Module load was not attempted. See logs/combined.log";
            if (!string.IsNullOrEmpty(status) && status.StartsWith("shell-load-command-rejected:", StringComparison.Ordinal))
                return "Resident load command was rejected: " + status.Substring("shell-load-command-rejected:".Length).Trim() + ". See logs/combined.log";
            if (!string.IsNullOrEmpty(status) && status.StartsWith("shell-load-result-rejected:", StringComparison.Ordinal))
                return "Resident module load returned " + status.Substring("shell-load-result-rejected:".Length).Trim() + ". See logs/combined.log";
            if (!string.IsNullOrEmpty(status) && status.StartsWith("shell-restart-required", StringComparison.Ordinal))
                return "An older resident still owns background jobs. Finish them, restart PS4 and enable GoldHEN";
            if (!string.IsNullOrEmpty(status) && status.StartsWith("shell-load-failed", StringComparison.Ordinal))
            {
                string detail = status.Substring("shell-load-failed".Length).TrimStart(':', ' ');
                int code;
                if (int.TryParse(detail, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                    detail = "0x" + unchecked((uint)code).ToString("X8", CultureInfo.InvariantCulture) +
                        " (" + code.ToString(CultureInfo.InvariantCulture) + ")";
                return "Resident module load failed" + (detail.Length == 0 ? "" : ": " + detail) + ". See logs/combined.log";
            }
            return "The background worker has not acknowledged readiness. Retry or restart PS4 and enable GoldHEN; see logs/resident.log";
        }

        internal static bool ActivationFailedBeforeAcknowledgement(string status)
        {
            return status != null && (status.StartsWith("plugin-stage-failed:", StringComparison.Ordinal) ||
                status.StartsWith("native-binding-failed:", StringComparison.Ordinal) ||
                status.StartsWith("shell-load-failed:", StringComparison.Ordinal) ||
                status.StartsWith("shell-api-rejected:", StringComparison.Ordinal) ||
                status.StartsWith("shell-process-query-rejected:", StringComparison.Ordinal) ||
                status.StartsWith("shell-load-command-rejected:", StringComparison.Ordinal) ||
                status.StartsWith("shell-load-result-rejected:", StringComparison.Ordinal));
        }

        public static bool TryGetStatus(string id, out ResidentDownloadStatus status)
        {
            if (TryGetStagedStatus(id, out status)) return true;
            status = null;
            ResidentDownloadStatus current;
            if (!TryReadStatus(id, out current)) return false;
            string generation = ActiveGeneration(id);
            if (generation != null && current.Generation != generation) return false;
            if (!string.IsNullOrEmpty(current.Generation) && current.Generation != generation) return false;
            status = current;
            return true;
        }

        public static bool TryGetStatus(string id, string generation, out ResidentDownloadStatus status)
        {
            if (TryGetStatus(id, out status) && (string.IsNullOrEmpty(generation) || status.Generation == generation)) return true;
            status = null;
            if (!IsGeneration(generation) || string.IsNullOrEmpty(id) ||
                !System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9_-]+$")) return false;
            try
            {
                string[] receipt = File.ReadAllLines(Path.Combine(IpcRoot, "installed-" + id + ".txt"));
                long total;
                if (receipt.Length != 5 || receipt[0] != "1" || Decode(receipt[1]) != id || receipt[4] != generation ||
                    Decode(receipt[2]).Length != 36 || !long.TryParse(receipt[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out total) || total < 0) return false;
                status = new ResidentDownloadStatus { Id = id, Generation = generation, State = "installed", Done = total, Total = total, AutoInstall = true };
                return true;
            }
            catch { return false; }
        }

        public static bool IsAlive(string id)
        {
            string runningVersion;
            // The prior revision keeps its existing job until an orderly restart.
            if (!TryReadFreshHeartbeat(out runningVersion) ||
                (runningVersion != Version && runningVersion != PreviousVersion && runningVersion != "5.10-r15" &&
                 runningVersion != "5.10-r14" && runningVersion != "5.10-r13" && runningVersion != "5.10-r12" && runningVersion != "5.10-r11" && runningVersion != "5.10-r10" && runningVersion != "5.10-r9" && runningVersion != "5.10-r8" && runningVersion != "5.10-r7" && runningVersion != "5.10-r6")) return false;
            ResidentDownloadStatus current;
            if (TryGetStagedStatus(id, out current)) return current.State != "failed" && current.State != "canceled" && current.State != "released";
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

        public static bool SetPaused(string id, bool paused)
        {
            return WriteControl(id, paused ? "pause" : "resume");
        }

        public static bool SetPaused(string id, bool paused, string expectedGeneration, Func<Action, bool> publishIfCurrent)
        {
            return WriteControl(id, paused ? "pause" : "resume", -1, expectedGeneration, true, publishIfCurrent);
        }

        /// <summary>Durably publishes an app-registered BGFT task to the
        /// resident worker that is waiting in awaiting-bgft.</summary>
        public static bool AttachBgftTask(string id, int taskId, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(id) || taskId < 0)
            {
                error = "Resident BGFT task metadata is invalid";
                return false;
            }
            string contentId, generation;
            int subType;
            if (!TryGetLegacyBgftIdentity(id, taskId, out contentId, out subType, out generation))
            {
                error = "Resident BGFT owner identity is unavailable or changed";
                return false;
            }
            return AttachBgftTaskWithIdentity(id, taskId, contentId, subType, generation, out error);
        }

        static bool AttachBgftTaskWithIdentity(string id, int taskId, string contentId, int subType,
            string generation, out string error)
        {
            error = null;
            if (!PersistLegacyBgftIdentity(id, taskId, contentId, subType, generation))
            {
                error = "Resident BGFT owner identity could not be persisted";
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

        /// <summary>Registers and starts the BGFT install for the validated
        /// staged file of a resident job that is waiting in awaiting-bgft,
        /// then publishes the task through bgft.txt. Safe to call on every
        /// status poll: a published attachment and an in-memory receipt are
        /// reused, and failed registration attempts are throttled.</summary>
        public static bool TryAttachPendingBgft(string id, out int taskId)
        {
            taskId = -1;
            if (string.IsNullOrEmpty(id) || id.Length > 190 ||
                Interlocked.CompareExchange(ref _bgftAttachInFlight, 1, 0) != 0) return false;
            try
            {
                int attachedTask = -1;
                bool attached = LegacyBgftAttach.ExecuteSerialized(() =>
                    TryAttachPendingBgftSerialized(id, out attachedTask));
                taskId = attachedTask;
                return attached;
            }
            catch (Exception ex)
            {
                lock (Gate) _lastError = "Resident BGFT attachment failed: " + ex.Message;
                return false;
            }
            finally { Interlocked.Exchange(ref _bgftAttachInFlight, 0); }
        }

        static bool TryAttachPendingBgftSerialized(string id, out int taskId)
        {
            taskId = -1;
            if (OwnsBgftLifetime(id)) return false;
            ResidentDownloadStatus status;
            if (!TryGetStatus(id, out status) || status.State != "awaiting-bgft") return false;
            string generation = status.Generation ?? "";
            if (!IsLegacyBgftAttachCurrent(id, generation)) return false;

            int staleTask = -1;
            string staleId = null, staleGeneration = null;
            lock (Gate)
            {
                if (_bgftAttachTask >= 0 &&
                    (_bgftAttachId != id || !string.Equals(_bgftAttachGeneration ?? "", generation, StringComparison.Ordinal)))
                {
                    staleTask = _bgftAttachTask;
                    staleId = _bgftAttachId;
                    staleGeneration = _bgftAttachGeneration;
                }
            }
            if (staleTask >= 0)
            {
                if (!CleanupLegacyBgftTask(staleId, staleTask))
                {
                    RetainLegacyBgftTask(staleId, staleGeneration, staleTask);
                    return false;
                }
            }

            string receiptOwner;
            int receiptTask;
            if (TryReadAttachedBgft(out receiptOwner, out receiptTask) &&
                !LegacyBgftAttach.TryRetireForeignOwner(id, receiptOwner, receiptTask,
                    (owner, task) => CleanupLegacyBgftTask(owner, task),
                    (owner, task) => RetainLegacyBgftTask(owner, null, task)))
            {
                lock (Gate) _lastError = "A previous BGFT task still owns the attachment receipt";
                return false;
            }

            int registered;
            lock (Gate)
            {
                registered = _bgftAttachId == id && _bgftAttachTask >= 0 &&
                    string.Equals(_bgftAttachGeneration ?? "", generation, StringComparison.Ordinal)
                    ? _bgftAttachTask : AttachedBgftTask(id);
            }
            if (registered >= 0)
            {
                if (!IsLegacyBgftAttachCurrent(id, generation)) return false;
                RememberLegacyBgftTask(id, generation, registered);
                string publishError;
                if (AttachBgftTask(id, registered, out publishError))
                {
                    taskId = registered;
                    return true;
                }
                lock (Gate) _lastError = publishError;
                taskId = registered;
                return false;
            }

            string destination, titleId, contentId, jobError;
            long total;
            int subType;
            if (!TryReadActiveJob(id, out destination, out titleId, out contentId,
                out total, out subType, out jobError))
            { lock (Gate) _lastError = jobError; return false; }
            long stagedSize;
            try { stagedSize = File.Exists(destination) ? new FileInfo(destination).Length : -1; }
            catch (Exception ex)
            { lock (Gate) _lastError = "Resident staged package stat failed: " + ex.Message; return false; }
            if (stagedSize != total) return false;

            lock (Gate)
            {
                if (DateTime.UtcNow < _nextBgftAttachAttemptUtc) return false;
                _nextBgftAttachAttemptUtc = DateTime.UtcNow.AddSeconds(5);
            }

            string url = "http://127.0.0.1:" + LoopbackPkgServer.LegacyResidentPort +
                "/pkg/" + Uri.EscapeDataString(id) + ".pkg";
            string contentName = !string.IsNullOrEmpty(titleId)
                ? titleId + ".pkg" : Path.GetFileName(destination);
            string packageType = null;
            try { packageType = PkgIntegrity.PackageType(destination); } catch { }
            int created = -1;
            string registerError = null;
            bool started = false;
            bool attached = LegacyBgftAttach.TryAttach(
                () => IsLegacyBgftAttachCurrent(id, generation),
                () =>
                {
                    started = PkgInstaller.TryStartLoopbackBgftDownload(url, titleId,
                        contentId, contentName, subType, total, out created, out registerError, packageType);
                    if (created >= 0 && !PersistLegacyBgftIdentity(id, created, contentId, subType, generation))
                        WriteLastError("Resident BGFT owner binding could not be persisted after registration");
                    if (!started && created < 0)
                    {
                        lock (Gate) _nextBgftAttachAttemptUtc = DateTime.UtcNow.AddSeconds(5);
                        WriteLastError("Resident BGFT attachment pending: " + registerError);
                        return -1;
                    }
                    RememberLegacyBgftTask(id, generation, created);
                    if (!started)
                        WriteLastError("Resident BGFT start unconfirmed: " + registerError +
                            "; task and PKG retained for the resident worker");
                    return created;
                },
                task =>
                {
                    RememberLegacyBgftTask(id, generation, task);
                    string attachError;
                    if (AttachBgftTaskWithIdentity(id, task, contentId, subType, generation, out attachError)) return true;
                    lock (Gate) _lastError = attachError;
                    return false;
                },
                task => CleanupLegacyBgftTask(id, task),
                task => RetainLegacyBgftTask(id, generation, task, contentId, subType),
                out taskId);
            if (attached)
            {
                lock (Gate) _nextBgftAttachAttemptUtc = DateTime.MinValue;
                WriteLaunchLog("bgft attached id=" + id + " task=" + taskId);
            }
            else if (created >= 0 && taskId < 0)
            {
                // A generation change won and the new task was retired successfully.
                lock (Gate) _nextBgftAttachAttemptUtc = DateTime.UtcNow.AddSeconds(5);
            }
            return attached;
        }

        static bool IsLegacyBgftAttachCurrent(string id, string generation)
        {
            if (OwnsBgftLifetime(id) || HasPendingLegacyCancel(id, generation)) return false;
            ResidentDownloadStatus status;
            return TryGetStatus(id, out status) && status.State == "awaiting-bgft" &&
                string.Equals(status.Generation ?? "", generation ?? "", StringComparison.Ordinal);
        }

        static bool HasPendingLegacyCancel(string id, string generation)
        {
            try
            {
                string[] lines = File.ReadAllLines(ControlPath);
                if (lines.Length < 3 || Decode(lines[1]) != id ||
                    (lines[2] != "cancel" && lines[2] != "release")) return false;
                if (lines[0] == "1") return string.IsNullOrEmpty(generation) && lines.Length == 3;
                return lines[0] == "2" && lines.Length == 4 && lines[3] == (generation ?? "");
            }
            catch { return false; }
        }

        static void RememberLegacyBgftTask(string id, string generation, int taskId)
        {
            lock (Gate)
            {
                _bgftAttachId = id;
                _bgftAttachGeneration = generation ?? "";
                _bgftAttachTask = taskId;
            }
        }

        static void RetainLegacyBgftTask(string id, string generation, int taskId)
        {
            RememberLegacyBgftTask(id, generation, taskId);
            string attachError;
            if (!AttachBgftTask(id, taskId, out attachError))
                lock (Gate) _lastError = attachError;
            WriteLaunchLog("bgft attach retained id=" + id + " task=" + taskId);
        }

        static void RetainLegacyBgftTask(string id, string generation, int taskId, string contentId, int subType)
        {
            RememberLegacyBgftTask(id, generation, taskId);
            string attachError;
            if (!AttachBgftTaskWithIdentity(id, taskId, contentId, subType, generation, out attachError))
                lock (Gate) _lastError = attachError;
            WriteLaunchLog("bgft attach retained id=" + id + " task=" + taskId);
        }

        static bool CleanupLegacyBgftTask(string id, int taskId)
        {
            int activeTask;
            string error;
            string contentId;
            int subType;
            string generation;
            if (!TryGetLegacyBgftIdentity(id, taskId, out contentId, out subType, out generation))
            {
                lock (Gate) _lastError = "BGFT task identity does not match its resident owner; task ownership is retained";
                return false;
            }
            if (!PkgInstaller.CancelBackground(taskId, contentId, subType, out activeTask, out error))
            {
                lock (Gate) _lastError = error ?? "BGFT task cleanup is pending";
                return false;
            }
            lock (Gate)
            {
                if (_bgftAttachId == id && _bgftAttachTask == taskId)
                {
                    _bgftAttachId = null;
                    _bgftAttachGeneration = null;
                    _bgftAttachTask = -1;
                }
            }
            try
            {
                string receiptOwner; int receiptTask;
                bool hasReceipt = TryReadAttachedBgft(out receiptOwner, out receiptTask);
                if (hasReceipt && receiptOwner == id && receiptTask == taskId) File.Delete(BgftPath);
                if (!File.Exists(BgftPath) || (hasReceipt && receiptOwner == id && receiptTask == taskId))
                {
                    string[] owner = File.ReadAllLines(BgftOwnerPath);
                    int ownerTask;
                    if (owner.Length == 6 && owner[0] == "1" && Decode(owner[1]) == id &&
                        int.TryParse(owner[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ownerTask) && ownerTask == taskId)
                        File.Delete(BgftOwnerPath);
                }
            }
            catch { }
            return true;
        }

        static bool PersistLegacyBgftIdentity(string id, int taskId, string contentId, int subType, string generation)
        {
            if (string.IsNullOrEmpty(id) || taskId < 0 || string.IsNullOrWhiteSpace(contentId) || contentId.Length != 36 ||
                subType < 6 || subType > 8 || (!string.IsNullOrEmpty(generation) && !IsGeneration(generation))) return false;
            string journalContent; int journalType;
            if (!PkgInstaller.TryGetOwnedBackgroundIdentity(taskId, out journalContent, out journalType) ||
                !string.Equals(contentId, journalContent, StringComparison.OrdinalIgnoreCase) || journalType != subType) return false;
            if (File.Exists(BgftOwnerPath))
            {
                try
                {
                    string[] owner = File.ReadAllLines(BgftOwnerPath); int ownerTask, ownerType;
                    if (owner.Length != 6 || owner[0] != "1" || Decode(owner[1]) != id ||
                        !int.TryParse(owner[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ownerTask) || ownerTask != taskId ||
                        !int.TryParse(owner[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out ownerType) || ownerType != subType ||
                        !string.Equals(Decode(owner[3]), contentId, StringComparison.OrdinalIgnoreCase) || Decode(owner[5]) != (generation ?? "")) return false;
                    return true;
                }
                catch { return false; }
            }
            try
            {
                Directory.CreateDirectory(IpcRoot);
                WriteAtomic(BgftOwnerPath, "1\n" + Encode(id) + "\n" + taskId.ToString(CultureInfo.InvariantCulture) + "\n" +
                    Encode(contentId) + "\n" + subType.ToString(CultureInfo.InvariantCulture) + "\n" + Encode(generation));
                return true;
            }
            catch { return false; }
        }

        static bool TryGetLegacyBgftIdentity(string id, int taskId, out string contentId,
            out int subType, out string generation)
        {
            contentId = null; subType = 0; generation = "";
            if (string.IsNullOrEmpty(id) || taskId < 0) return false;
            bool hasOwner = File.Exists(BgftOwnerPath);
            if (hasOwner)
            {
                try
                {
                    string[] owner = File.ReadAllLines(BgftOwnerPath);
                    int ownerTask;
                    if (owner.Length != 6 || owner[0] != "1" || Decode(owner[1]) != id ||
                        !int.TryParse(owner[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out ownerTask) || ownerTask != taskId ||
                        !int.TryParse(owner[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out subType)) return false;
                    contentId = Decode(owner[3]);
                    generation = Decode(owner[5]);
                    if (string.IsNullOrWhiteSpace(contentId) || contentId.Length != 36 || subType < 6 || subType > 8 ||
                        (!string.IsNullOrEmpty(generation) && !IsGeneration(generation))) return false;
                }
                catch { return false; }
            }
            else
            {
                string receiptOwner; int receiptTask;
                if (File.Exists(BgftPath) && (!TryReadAttachedBgft(out receiptOwner, out receiptTask) || receiptOwner != id || receiptTask != taskId))
                    return false;
                string destination, titleId, jobContent, jobError; long total; int jobType;
                if (!TryReadActiveJob(id, out destination, out titleId, out jobContent, out total, out jobType, out jobError)) return false;
                contentId = jobContent; subType = jobType;
                generation = ActiveGeneration(id) ?? "";
                ResidentDownloadStatus status;
                if (TryGetStatus(id, out status) && !string.IsNullOrEmpty(status.Generation))
                {
                    if (!string.IsNullOrEmpty(generation) && generation != status.Generation) return false;
                    generation = status.Generation;
                }
            }

            string journalContent; int journalType;
            if (!PkgInstaller.TryGetOwnedBackgroundIdentity(taskId, out journalContent, out journalType) ||
                !string.Equals(contentId, journalContent, StringComparison.OrdinalIgnoreCase) || journalType != subType)
                return false;

            string activeGeneration = ActiveGeneration(id);
            if (!string.IsNullOrEmpty(activeGeneration) &&
                (string.IsNullOrEmpty(generation) || activeGeneration != generation)) return false;
            ResidentDownloadStatus currentStatus;
            if (TryGetStatus(id, out currentStatus) && !string.IsNullOrEmpty(currentStatus.Generation) &&
                currentStatus.Generation != generation) return false;

            if (!hasOwner)
            {
                if (!PersistLegacyBgftIdentity(id, taskId, contentId, subType, generation)) return false;
            }
            return true;
        }

        static int AttachedBgftTask(string id)
        {
            string owner;
            int taskId;
            return TryReadAttachedBgft(out owner, out taskId) && owner == id ? taskId : -1;
        }

        static bool TryReadAttachedBgft(out string id, out int taskId)
        {
            id = null;
            taskId = -1;
            if (!File.Exists(BgftPath)) return TryReadBgftOwner(out id, out taskId);
            try
            {
                string[] lines = File.ReadAllLines(BgftPath);
                if (lines.Length != 3 || lines[0] != "1" ||
                    !int.TryParse(lines[2], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out taskId) || taskId < 0) return false;
                id = Decode(lines[1]);
                return !string.IsNullOrEmpty(id);
            }
            catch { return false; }
        }

        static bool TryReadBgftOwner(out string id, out int taskId)
        {
            id = null; taskId = -1;
            try
            {
                string[] owner = File.ReadAllLines(BgftOwnerPath);
                int subType;
                if (owner.Length != 6 || owner[0] != "1" ||
                    !int.TryParse(owner[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out taskId) || taskId < 0 ||
                    !int.TryParse(owner[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out subType) || subType < 6 || subType > 8) return false;
                id = Decode(owner[1]);
                string contentId = Decode(owner[3]), generation = Decode(owner[5]);
                return !string.IsNullOrEmpty(id) && contentId.Length == 36 &&
                    (string.IsNullOrEmpty(generation) || IsGeneration(generation));
            }
            catch { id = null; taskId = -1; return false; }
        }

        static bool TryReadActiveJob(string id, out string destination, out string titleId,
            out string contentId, out long total, out int subType, out string error)
        {
            destination = null; titleId = ""; contentId = ""; total = 0; subType = 0; error = null;
            try
            {
                string[] lines = File.ReadAllLines(JobPath);
                if (lines.Length < 9 || Decode(lines[1]) != id ||
                    (lines[0] != "2" && lines[0] != "3" && lines[0] != "4" &&
                     lines[0] != "5" && lines[0] != "8"))
                { error = "Resident job metadata does not match the waiting job"; return false; }
                destination = Decode(lines[3]);
                titleId = Decode(lines[4]);
                contentId = Decode(lines[6]);
                if (!long.TryParse(lines[7], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out total) || total < LoopbackPkgFeeder.HeaderBytes)
                { error = "Resident job size is invalid"; return false; }
                if ((lines[0] == "5" || lines[0] == "8") && lines.Length > 9)
                {
                    int kind;
                    if (int.TryParse(lines[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out kind))
                        subType = kind;
                }
                if (string.IsNullOrEmpty(destination) || destination.Length <= "/data/SSPI/downloads/".Length ||
                    !destination.StartsWith("/data/SSPI/downloads/", StringComparison.Ordinal))
                { error = "Resident job destination is invalid"; return false; }
                if (subType < 6 || subType > 8)
                {
                    PkgContentKind kind;
                    string detail;
                    if (!PkgValidator.TryGetContentKind(destination, out kind, out detail))
                    { error = "Resident staged package type is invalid: " + detail; return false; }
                    subType = kind == PkgContentKind.AddOn ? 7 : kind == PkgContentKind.Patch ? 8 :
                        kind == PkgContentKind.BaseGame ? 6 : 0;
                }
                if (string.IsNullOrEmpty(contentId) || contentId.Length != 36)
                    PkgValidator.TryGetContentId(destination, out contentId);
                if (string.IsNullOrEmpty(contentId) || contentId.Length != 36 ||
                    (string.IsNullOrEmpty(titleId) &&
                     !PkgValidator.TryGetTitleIdFromContentId(contentId, out titleId)))
                { error = "Resident staged package identity is incomplete"; return false; }
                if (subType < 6 || subType > 8)
                { error = "Resident staged package type is unknown"; return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = "Resident job metadata is invalid: " + ex.Message;
                return false;
            }
        }

        public static void ResetQueueForFreshStart()
        {
            string id = "";
            lock (Gate)
            {
                Directory.CreateDirectory(IpcRoot);
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
            }
            // Release also cancels the worker, then clears its ownership after it stops.
            if (!string.IsNullOrEmpty(id)) WriteControl(id, "release");
        }

        public static void MarkFailed(string id)
        {
            WriteControl(id, "cancel");
        }

        /// <summary>MarkFailed for a caller that could not send it at once: the cancel is
        /// written only while publishIfCurrent confirms (under the caller's own lock) that
        /// the queue row still wants it, so a later release or resume is never replaced.</summary>
        public static bool MarkFailedIfCurrent(string id, Func<Action, bool> publishIfCurrent)
        {
            return WriteControl(id, "cancel", -1, null, false, publishIfCurrent);
        }

        public static bool TryCancel(string id, out string error)
        {
            bool written = WriteControl(id, "cancel");
            error = written ? null : "Could not send cancellation to the resident: " + LastError;
            return written;
        }

        public static bool TryCancel(string id, int legacyTaskId, out string error)
        {
            bool written = WriteControl(id, "cancel", legacyTaskId);
            error = written ? null : "Could not send cancellation to the resident: " + LastError;
            return written;
        }

        public static bool TryCancel(string id, int legacyTaskId, string expectedGeneration,
            Func<Action, bool> publishIfCurrent, out string error)
        {
            bool written = WriteControl(id, "cancel", legacyTaskId, expectedGeneration, true, publishIfCurrent);
            error = written ? null : "Could not send cancellation to the resident: " + LastError;
            return written;
        }

        public static bool TryCancelLegacyBgftTask(string id, int taskId, out string error)
        { return TryCancelLegacyBgftTask(id, taskId, null, null, out error); }

        public static bool TryCancelLegacyBgftTask(string id, int taskId, string expectedGeneration,
            Func<bool> stillCurrent, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(id) || taskId < 0) return true;
            string cleanupError = null;
            bool cleaned = LegacyBgftAttach.ExecuteSerialized(() =>
            {
                if (stillCurrent != null && !stillCurrent()) return true;
                if (OwnsBgftLifetime(id)) return true;
                string activeGeneration = ActiveGeneration(id);
                if (!string.Equals(activeGeneration ?? "", expectedGeneration ?? "", StringComparison.Ordinal)) return true;
                if (CleanupLegacyBgftTask(id, taskId)) return true;
                cleanupError = LastError;
                RetainLegacyBgftTask(id, expectedGeneration, taskId);
                return false;
            });
            if (!cleaned) error = string.IsNullOrEmpty(cleanupError)
                ? "BGFT task cleanup is pending" : cleanupError;
            return cleaned;
        }

        public static void Release(string id)
        {
            WriteControl(id, "release");
        }

        public static void Release(string id, string expectedGeneration, Func<Action, bool> publishIfCurrent)
        {
            WriteControl(id, "release", -1, expectedGeneration, true, publishIfCurrent);
        }

        public static string GetError(string id)
        {
            ResidentDownloadStatus status;
            return TryGetStatus(id, out status) ? status.Error : LastError;
        }

        static bool WriteControl(string id, string action, int fallbackTaskId = -1,
            string expectedGeneration = null, bool bindGeneration = false, Func<Action, bool> publishIfCurrent = null)
        {
            if (string.IsNullOrEmpty(id)) return false;
            return LegacyBgftAttach.ExecuteSerialized(() =>
                WriteControlSerialized(id, action, fallbackTaskId, expectedGeneration, bindGeneration, publishIfCurrent));
        }

        static bool WriteControlSerialized(string id, string action, int fallbackTaskId,
            string expectedGeneration, bool bindGeneration, Func<Action, bool> publishIfCurrent = null)
        {
            try
            {
                int slot = FindStagedSlot(id);
                string currentGeneration = slot >= 0 ? StagedGeneration(slot) : ActiveGeneration(id);
                string boundGeneration = string.IsNullOrEmpty(expectedGeneration) ? null : expectedGeneration;
                if (bindGeneration && !string.Equals(currentGeneration ?? "", boundGeneration ?? "", StringComparison.Ordinal))
                    return true;
                string generation = bindGeneration ? boundGeneration : currentGeneration;
                if (publishIfCurrent != null && !publishIfCurrent(null)) return true;
                if ((action == "cancel" || action == "release") && !OwnsBgftLifetime(id))
                {
                    int legacyTask;
                    string legacyGeneration;
                    lock (Gate)
                    {
                        bool sameOwner = _bgftAttachId == id &&
                            string.Equals(_bgftAttachGeneration ?? "", generation ?? "", StringComparison.Ordinal);
                        legacyTask = sameOwner ? _bgftAttachTask : -1;
                        legacyGeneration = sameOwner ? _bgftAttachGeneration : generation;
                    }
                    if (legacyTask < 0 && !bindGeneration) legacyTask = AttachedBgftTask(id);
                    if (legacyTask < 0) legacyTask = fallbackTaskId;
                    if (legacyTask >= 0 && !CleanupLegacyBgftTask(id, legacyTask))
                        RetainLegacyBgftTask(id, legacyGeneration, legacyTask);
                }
                Action publish = () =>
                {
                    Directory.CreateDirectory(IpcRoot);
                    WriteAtomic(slot >= 0 ? StagedPath(slot, "control") : ControlPath,
                        CreateControlRecord(id, action, generation));
                };
                if (publishIfCurrent != null) publishIfCurrent(publish);
                else publish();
                return true;
            }
            catch (Exception ex)
            {
                lock (Gate) _lastError = ex.Message;
                return false;
            }
        }

        public static bool HasJob(string id)
        {
            if (HasStagedJob(id)) return true;
            for (int slot = 0; slot < StagedSlotCount; slot++)
            {
                string path = StagedPath(slot, "job");
                if (!File.Exists(path)) continue;
                try
                {
                    string[] lines = File.ReadAllLines(path);
                    // A damaged record still has a native owner until the worker
                    // removes it. Only a valid record for another ID is excluded.
                    if (!IsStagedRecord(lines)) return true;
                }
                catch { return true; }
            }
            if (!File.Exists(JobPath)) return false;
            try
            {
                using (var reader = new StreamReader(JobPath))
                {
                    string version = reader.ReadLine();
                    bool known = version == "2" || version == "3" || version == "4" || version == "5" ||
                        version == "8" || version == "9" || version == "11" || version == "12" ||
                        version == "14" || version == "15" || version == "17" || version == "19";
                    if (!known) return true;
                    string owner = Decode(reader.ReadLine());
                    return string.IsNullOrEmpty(owner) || owner == id;
                }
            }
            catch { return File.Exists(JobPath); }
        }

        internal static string ObservedWorkerVersion
        {
            get
            {
                WorkerHeartbeat heartbeat;
                return TryReadHeartbeat(out heartbeat) ? heartbeat.Version + "/" + (heartbeat.Build ?? "unknown") : null;
            }
        }

        internal static string CreateControlRecord(string id, string action, string generation)
        {
            return (generation == null ? "1\n" : "2\n") + Encode(id) + "\n" + action +
                (generation == null ? "" : "\n" + generation);
        }

        public static bool HasDownloader { get { return FreshHeartbeat(); } }

        internal static bool SupportsSevenZipArchive
        {
            get
            {
                if (!AppSettings.DataDirWritable || AppSettings.DataMigrationPending) return false;
                WorkerHeartbeat heartbeat;
                return TryReadHeartbeat(out heartbeat) && heartbeat.Ready && heartbeat.Current && heartbeat.SevenZip;
            }
        }

        static bool FreshHeartbeat()
        {
            if (!AppSettings.DataDirWritable || AppSettings.DataMigrationPending) return false;
            WorkerHeartbeat heartbeat;
            return TryReadHeartbeat(out heartbeat) && heartbeat.Ready && heartbeat.Current;
        }

        static long _readinessRefreshAt;
        static string _readinessDetail = "Checking resident worker";
        internal static string ReadinessDetail
        {
            get
            {
                if (!AppSettings.DataDirWritable) return AppSettings.DataDirError ?? "SSPI data directory is not writable: " + AppSettings.DataDir;
                if (AppSettings.DataMigrationPending) return AppSettings.DataMigrationNotice;
                long now = DateTime.UtcNow.Ticks;
                if (now < Interlocked.Read(ref _readinessRefreshAt)) return _readinessDetail;
                Interlocked.Exchange(ref _readinessRefreshAt, now + TimeSpan.TicksPerSecond);
                WorkerHeartbeat heartbeat;
                if (!TryReadHeartbeat(out heartbeat))
                    return _readinessDetail = _heartbeatReadError ??
                        (string.IsNullOrEmpty(_lastError) || _lastError == "not started"
                        ? "Resident has not reported readiness. Choose Retry in General settings" : _lastError);
                if (!heartbeat.Current) return _readinessDetail = "Loaded worker " + (heartbeat.Build ?? "unknown build") + "; restart PS4 and enable GoldHEN for this build";
                return _readinessDetail = heartbeat.Ready ? "Resident ready · build " + heartbeat.Build : heartbeat.NotReadyReason;
            }
        }

        internal static string ModeStatus(bool backgroundSelected)
        { return backgroundSelected ? "Selected: Background · " + ReadinessDetail : "Selected: In-app · keep SSPI open"; }

        public static void RetryActivation()
        {
            QueueActivationRetry(action => ThreadPool.QueueUserWorkItem(_ => action()), () =>
            {
                if (!AppSettings.RetryDataMigration())
                {
                    WriteLastError(AppSettings.DataMigrationPending ? AppSettings.DataMigrationNotice : AppSettings.DataDirError);
                    Interlocked.Exchange(ref _readinessRefreshAt, 0);
                    return;
                }
                _nextActivationAttemptUtc = DateTime.MinValue;
                string error; EnsureAvailableLocked(out error);
                Interlocked.Exchange(ref _readinessRefreshAt, 0);
            });
        }

        internal static void QueueActivationRetry(Action<Action> schedule, Action retry)
        {
            if (Volatile.Read(ref _launchMaintenanceStopped) != 0 ||
                Interlocked.CompareExchange(ref _manualActivationRetryQueued, 1, 0) != 0) return;
            int serial = Volatile.Read(ref _activationAttemptSerial);
            if (Volatile.Read(ref _activationAttemptInProgress) != 0)
            { Volatile.Write(ref _manualActivationRetryQueued, 0); return; }
            try
            {
                schedule(() =>
                {
                    try
                    {
                        lock (Gate)
                        {
                            // Join maintenance already running or started since this
                            // request was queued; do not erase its cooldown afterwards.
                            if (Volatile.Read(ref _launchMaintenanceStopped) != 0 ||
                                serial != Volatile.Read(ref _activationAttemptSerial)) return;
                            retry();
                        }
                    }
                    catch (Exception ex) { WriteLastError("Resident retry failed: " + ex.GetType().Name); }
                    finally { Volatile.Write(ref _manualActivationRetryQueued, 0); }
                });
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _manualActivationRetryQueued, 0);
                WriteLastError("Resident retry could not be queued: " + ex.GetType().Name);
            }
        }

        internal static bool RunningWorkerRequiresRestart
        {
            get
            {
                WorkerHeartbeat heartbeat;
                return TryReadHeartbeat(out heartbeat) && !heartbeat.Current;
            }
        }

        internal sealed class WorkerHeartbeat
        {
            public string Version, Build, Epoch, NotReadyReason;
            public int ProcessId;
            public bool Current, Ready, SevenZip, UsbFilesystemContext;
        }

        internal static WorkerHeartbeat ParseHeartbeat(string[] lines, long nowTicks, string expectedBuild)
        {
            long ticks;
            if (lines == null || lines.Length < 3 || string.IsNullOrWhiteSpace(lines[0]) ||
                !long.TryParse(lines[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks) ||
                ticks < 0 || ticks > DateTime.MaxValue.Ticks ||
                Math.Abs(nowTicks - ticks) > TimeSpan.TicksPerSecond * 15) return null;
            var fields = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string field in lines[2].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = field.IndexOf('=');
                if (equals <= 0 || fields.ContainsKey(field.Substring(0, equals))) return null;
                fields.Add(field.Substring(0, equals), field.Substring(equals + 1));
            }
            Func<string, string> value = key => fields.ContainsKey(key) ? fields[key] : null;
            if (value("host") != "shell") return null;
            int pid; long epoch;
            bool identified = int.TryParse(value("pid"), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) && pid > 0 &&
                long.TryParse(value("epoch"), NumberStyles.Integer, CultureInfo.InvariantCulture, out epoch) && epoch > 0;
            bool current = identified && !string.IsNullOrEmpty(expectedBuild) &&
                string.Equals(value("build"), expectedBuild, StringComparison.Ordinal) &&
                value("v") == "2" && value("api") == "3" && value("staged") == "10";
            return new WorkerHeartbeat { Version = lines[0], Build = value("build"), Epoch = value("epoch"), ProcessId = pid,
                SevenZip = value("sevenzip") == "1",
                UsbFilesystemContext = value("usbcontext") == "1",
                NotReadyReason = value("migration") == "1" ? "Resident loaded; SSPI data migration is holding the background queue" :
                    value("storage") == "0" ? "Resident loaded; SceShellUI cannot access /data/SSPI. Background jobs are waiting for shared storage access" :
                    value("listener") != "1" ? "Resident loaded; local listener is not ready" :
                    value("transfer") != "1" || value("download") != "1" ? "Resident loaded; network transfer initialization is not ready" :
                    value("bgft") != "1" ? "Resident loaded; PS4 BGFT initialization is not ready" : "Resident loaded; required storage or archive capability is unavailable",
                Current = current, Ready = current && value("migration") != "1" && value("storage") != "0" && value("listener") == "1" && value("transfer") == "1" &&
                    value("download") == "1" && value("bgft") == "1" && value("usb") == "1" &&
                    value("archives") == "1" && value("zip") == "1" };
        }

        static string _heartbeatReadError;
        static string _liveHeartbeatRoot;
        static bool TryReadHeartbeat(out WorkerHeartbeat heartbeat)
        {
            heartbeat = null;
            string hash = BuildIdentity.SourceHash;
            string expectedBuild = hash != null && hash.Length >= 12 ? hash.Substring(0, 12) : null;
            string readError = null;
            string[] roots = HeartbeatRoots();
            for (int i = 0; i < roots.Length; i++)
            {
                string path = Path.Combine(roots[i], "heartbeat.txt");
                WorkerHeartbeat candidate = null;
                try
                {
                    candidate = ParseHeartbeat(File.ReadAllLines(path), DateTime.UtcNow.Ticks, expectedBuild);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex)
                {
                    string error = "Resident heartbeat cannot be read (" + path + "): " + ex.Message;
                    if (!string.Equals(_heartbeatReadError, error, StringComparison.Ordinal))
                        SspiLog.Write("resident", "operation=read heartbeat path=" + path +
                            " exception=" + ex.GetType().FullName + " hresult=0x" + ex.HResult.ToString("X8") + " " + ex);
                    if (readError == null) readError = error;
                }
                if (candidate != null)
                {
                    // Prefer whichever root supplied the live worker on later reads.
                    _liveHeartbeatRoot = roots[i];
                    _heartbeatReadError = null;
                    heartbeat = candidate;
                    return true;
                }
            }
            _heartbeatReadError = readError;
            return false;
        }

        static string[] HeartbeatRoots()
        {
            string primary = CanonicalIpcRoot;
            string shared = SharedIpcRoot;
            return string.Equals(_liveHeartbeatRoot, shared, StringComparison.Ordinal)
                ? new[] { shared, primary } : new[] { primary, shared };
        }

        static bool TryReadFreshHeartbeat(out string runningVersion)
        {
            runningVersion = null;
            WorkerHeartbeat heartbeat;
            if (!TryReadHeartbeat(out heartbeat)) return false;
            runningVersion = heartbeat.Current ? Version : heartbeat.Version;
            return true;
        }

        static long ReadNetworkBytes(string[] lines, int index)
        {
            long value;
            return lines.Length > index && long.TryParse(lines[index], NumberStyles.Integer,
                CultureInfo.InvariantCulture, out value) && value >= 0 ? value : -1;
        }

        static bool TryReadStatus(out ResidentDownloadStatus status)
        { return TryReadStatus(null, out status); }

        static bool TryReadStatus(string id, out ResidentDownloadStatus status)
        {
            status = null;
            string shared = MapToSharedIpcRoot(StatusPath);
            if (string.Equals(shared, StatusPath, StringComparison.Ordinal)) shared = null;
            for (int i = 0; i < 8; i++)
            {
                ResidentDownloadStatus candidate;
                if (TryReadStatusFile(StatusPath, out candidate) &&
                    (id == null || string.Equals(candidate.Id, id, StringComparison.Ordinal)))
                { status = candidate; return true; }
                if (shared != null && TryReadStatusFile(shared, out candidate) &&
                    (id == null || string.Equals(candidate.Id, id, StringComparison.Ordinal)))
                { status = candidate; return true; }
                Thread.Sleep(15);
            }
            return false;
        }

        static bool TryReadStatusFile(string path, out ResidentDownloadStatus status)
        {
            status = null;
            try
            {
                string[] lines = File.ReadAllLines(path);
                long done;
                long total;
                if (lines.Length < 7 || lines[0] != "1" ||
                    !long.TryParse(lines[3], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out done) ||
                    !long.TryParse(lines[4], NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out total)) return false;
                status = new ResidentDownloadStatus
                {
                    Id = Decode(lines[1]),
                    State = lines[2],
                    Done = done,
                    Total = total,
                    FullyServed = lines[5] == "1",
                    Error = Decode(lines[6]), NetworkBytes = ReadNetworkBytes(lines, 7),
                    Generation = lines.Length > 8 ? lines[8] : null,
                    WorkerBuild = lines.Length > 9 ? lines[9] : null, WorkerEpoch = lines.Length > 10 ? lines[10] : null
                };
                return true;
            }
            catch { return false; }
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
            MirrorSharedWrite(path, body);
        }

        /// <summary>Best-effort publication of an IPC file through the shell
        /// worker's shared root. The primary write already succeeded; a mirror
        /// failure never fails the caller, including when both roots name the
        /// same physical file and the second replacement is redundant.</summary>
        static void MirrorSharedWrite(string path, string body)
        {
            try
            {
                string shared = MapToSharedIpcRoot(path);
                if (shared == null || string.Equals(shared, path, StringComparison.Ordinal)) return;
                string directory = Path.GetDirectoryName(shared);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                AtomicFile.WriteText(shared, body);
            }
            catch (Exception ex)
            {
                SspiLog.Write("resident", "operation=mirror shared ipc path=" + path +
                    " exception=" + ex.GetType().Name + " hresult=0x" + ex.HResult.ToString("X8"));
            }
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
                "IV0000-SRCH00002_00-GAMESEARCHDAEMON", out sfoError);
        }

        static void StageDaemonPayload(string packageRoot)
        {
            string dataDaemon = "/data/SSPI/daemon";
            NativeMkdir("/data");
            NativeMkdir("/data/SSPI");
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
            string pkgSource = packageRoot + "/SRCH00002.pkg";
            if (!File.Exists(pkgSource))
            {
                WriteLaunchLog("daemon pkg missing");
                return false;
            }
            string pkgDest = Path.Combine(IpcRoot, "SRCH00002.pkg");
            string userPkg = "/user/data/SSPI/resident/SRCH00002.pkg";
            try
            {
                NativeMkdir("/user");
                NativeMkdir("/user/data");
                NativeMkdir("/user/data/SSPI");
                NativeMkdir("/user/data/SSPI/resident");
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
                "/data/SSPI/daemon/eboot.bin",
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
            SspiLog.Write("resident", body ?? "");
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
