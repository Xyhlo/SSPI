using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Orbis.Internals;

namespace Orbis
{
    internal enum InstallOutcome
    {
        Started,
        InvalidPackage,
        AlreadyInstalled,
        InstallFailed,
        UninstallFailed,
        NotReady
    }

    internal sealed class BgftProgress
    {
        public int TaskId;
        public long Done;
        public long Total;
        public int ErrorResult;
        public bool Finished;
        public bool DownloadComplete;
        public bool InstallComplete;
        public int LocalCopyPercent;
        public int EtaSeconds;
        public ulong RawLength;
        public ulong RawTransferred;
        public ulong RawLengthTotal;
        public ulong RawTransferredTotal;
        public uint NumIndex;
        public uint NumTotal;
        public uint Bits;
        public int PreparingPercent;
    }

    /// <summary>Validated local PKG install through BGFT with an AppInstUtil fallback.</summary>
    internal static class PkgInstaller
    {
        static readonly object Gate = new object();
        static readonly object InstallRegisterGate = new object();
        static readonly Dictionary<int, string> OwnedWebTasks = new Dictionary<int, string>();
        static bool _ownedJournalLoaded;
        const string OwnedJournalName = "bgft_owned.tsv";

        static string OwnedJournalPath()
        {
            try { return Path.Combine(AppSettings.DataDir, OwnedJournalName); }
            catch { return null; }
        }

        static void LoadOwnedJournal()
        {
            lock (InstallRegisterGate)
            {
                if (_ownedJournalLoaded) return;
                _ownedJournalLoaded = true;
                try
                {
                    string path = OwnedJournalPath();
                    if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                    foreach (var task in BgftOwnershipJournal.Read(File.ReadAllText(path)))
                        OwnedWebTasks[task.Key] = task.Value;
                }
                catch { }
            }
        }

        static void SaveOwnedJournal()
        {
            try
            {
                string path = OwnedJournalPath();
                if (string.IsNullOrEmpty(path)) return;
                AtomicFile.WriteText(path, BgftOwnershipJournal.Write(OwnedWebTasks));
            }
            catch { }
        }

        static void ClaimWebTask(int taskId, string contentId, int subType)
        {
            lock (InstallRegisterGate)
            {
                LoadOwnedJournal();
                OwnedWebTasks[taskId] = BackgroundIdentity(contentId, subType);
                SaveOwnedJournal();
            }
        }

        static void ReleaseWebTask(int taskId)
        {
            lock (InstallRegisterGate)
            {
                LoadOwnedJournal();
                if (OwnedWebTasks.Remove(taskId)) SaveOwnedJournal();
            }
        }

        /// <summary>True only for tasks this app registered (this run or a
        /// previous one via the durable journal). Never control anything else.</summary>
        static bool IsOwnedBackgroundTask(int taskId, string contentId, int subType)
        {
            if (taskId < 0 || string.IsNullOrEmpty(contentId) || subType <= 0) return false;
            lock (InstallRegisterGate)
            {
                LoadOwnedJournal();
                string identity;
                return OwnedWebTasks.TryGetValue(taskId, out identity) &&
                    string.Equals(identity, BackgroundIdentity(contentId, subType),
                        StringComparison.OrdinalIgnoreCase);
            }
        }
        static bool _init;
        static string _lastError = "not init";
        static bool _uninstallAvailable = true;
        static bool _bgftReady;
        static IntPtr _bgftHeap;
        const int BgftHeapSize = 1024 * 1024;
        const uint BgftDisableCdnQueryParam = 0x10000;
        const int BgftAlreadyInitialized = unchecked((int)0x80990001);
        const int BgftTaskDuplicated = unchecked((int)0x80990015);
        const int BgftTaskNotFound = unchecked((int)0x80990019);
        const int BgftContentAlreadyDownloading = unchecked((int)0x80990086);
        const int BgftSameApplicationInstalled = unchecked((int)0x80990088);
        const int AppSlotNotFound = unchecked((int)0x80A3000E);

        public static string LastError { get { return _lastError; } }

        public static bool EnsureReady()
        {
            lock (Gate)
            {
                if (_init) return true;
                try
                {
                    LoadMod("/system/common/lib/libSceSystemService.sprx");
                    LoadMod("/system/common/lib/libSceAppInstUtil.sprx");
                    LoadMod("/system/common/lib/libSceSysUtil.sprx");
                    int rc = sceAppInstUtilInitialize();
                    if (rc != 0)
                    {
                        _lastError = "AppInstUtilInit 0x" + rc.ToString("X");
                        return false;
                    }
                    _init = true;
                    _lastError = "ok";
                    return true;
                }
                catch (Exception ex)
                {
                    _lastError = ex.GetType().Name + ": " + ex.Message;
                    return false;
                }
            }
        }

        static void LoadMod(string path)
        {
            try
            {
                int h = Kernel.TryLoadStartModule(path);
                if (h != 0) return;
            }
            catch { }
            try { sceKernelLoadStartModule(path, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }

        public static bool TryGetTitleId(string pkgPath, out string titleId, out string error)
        {
            titleId = "";
            error = null;
            if (!EnsureReady())
            {
                error = _lastError;
                return false;
            }
            try
            {
                string nativePath;
                string nativeState;
                if (!TryResolveAppInstPath(pkgPath, out nativePath, out titleId, out nativeState))
                {
                    error = "PKG unavailable to AppInstUtil; checked " + nativeState;
                    return false;
                }
                return !string.IsNullOrEmpty(titleId);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static InstallOutcome InstallLocal(string pkgPath, string expectedTitleId, string requestedKind,
            out string titleId, out string error, out int taskId, bool uninstallFirst = false)
        {
            titleId = "";
            error = null;
            taskId = -1;

            PkgValResult vr;
            string vdetail;
            if (!PkgValidator.TryValidateDownload(pkgPath, expectedTitleId, requestedKind, out vr, out vdetail))
            {
                error = "Invalid PKG: " + vdetail;
                return vr == PkgValResult.NativeParseFailed
                    ? InstallOutcome.NotReady : InstallOutcome.InvalidPackage;
            }
            try
            {
                long pkgSize = new FileInfo(pkgPath).Length;
                ArchiveStorage.RequireFreeSpace(AppSettings.DataDir,
                    checked(pkgSize + (512L * 1024 * 1024)));
            }
            catch (Exception spaceEx)
            {
                error = "Not enough free space to install: " + spaceEx.Message;
                return InstallOutcome.NotReady;
            }

            if (!EnsureReady())
            {
                error = _lastError;
                return InstallOutcome.NotReady;
            }

            try
            {
                string appInstPath;
                string nativeState;
                if (!TryResolveAppInstPath(pkgPath, out appInstPath, out titleId, out nativeState))
                {
                    error = "PKG unavailable to AppInstUtil; checked " + nativeState;
                    return InstallOutcome.NotReady;
                }
                string headerContentId;
                if (!string.IsNullOrEmpty(expectedTitleId) &&
                    PkgValidator.TryGetContentId(pkgPath, out headerContentId) &&
                    PkgValidator.ContentIdMatchesTitleId(headerContentId, expectedTitleId))
                    titleId = expectedTitleId;

                PkgContentKind contentKind;
                string kindDetail;
                if (!PkgValidator.TryGetContentKind(pkgPath, out contentKind, out kindDetail))
                {
                    error = kindDetail;
                    return InstallOutcome.InstallFailed;
                }
                string packageContentId;
                PkgValidator.TryGetContentId(pkgPath, out packageContentId);
                if (!PkgIntegrity.CheckMetadata(pkgPath, titleId,
                    contentKind == PkgContentKind.Patch ? "gp" : contentKind == PkgContentKind.AddOn ? "ac" : "gd", out error))
                    return InstallOutcome.InvalidPackage;
                if (!PkgValidator.CheckRequestedIdentity(requestedKind, contentKind, expectedTitleId,
                    packageContentId, out error)) return InstallOutcome.InstallFailed;
                LogInstall("identity title=" + titleId + " requested=" + (requestedKind ?? "") +
                    " actual=" + kindDetail + " content=" + (packageContentId ?? "") +
                    " appInstPath=" + appInstPath + " file={" + nativeState + "}");

                int exists = 0;
                int rc = sceAppInstUtilAppExists(titleId, out exists);
                if (rc != 0)
                {
                    error = "AppExists 0x" + rc.ToString("X");
                    return InstallOutcome.NotReady;
                }
                if (contentKind == PkgContentKind.BaseGame && exists != 0)
                {
                    // AppExists includes incomplete dashboard placeholders. Only
                    // the owned task's completion path may certify installation.
                    if (uninstallFirst && !UninstallAndWait(titleId, out error))
                        return InstallOutcome.UninstallFailed;
                }
                else if (PkgInstallPolicy.RequiresInstalledBase(contentKind) && exists == 0)
                {
                    error = (contentKind == PkgContentKind.Patch ? "Update" : "DLC") +
                            " requires installed base game " + titleId;
                    return InstallOutcome.InstallFailed;
                }

                lock (InstallRegisterGate)
                {
                    if (contentKind == PkgContentKind.AddOn)
                    {
                        if (IsAddonInstalled(pkgPath, true)) return InstallOutcome.AlreadyInstalled;
                        return TryInstallWithAppInstUtil(pkgPath, out error) ? InstallOutcome.Started : InstallOutcome.InstallFailed;
                    }
                    bool bgftAttempted;
                    if (contentKind == PkgContentKind.Patch && !PkgIntegrity.WaitForInstalledBase(pkgPath, titleId, null, null, out error))
                        return PkgIntegrity.IsInstalledBaseUnavailable(error) ? InstallOutcome.NotReady : InstallOutcome.InvalidPackage;
                    string bgftError;
                    if (TryStartBgftInstall(pkgPath, titleId, contentKind,
                        out bgftAttempted, out bgftError, out taskId))
                        return InstallOutcome.Started;
                    if (bgftAttempted && !string.IsNullOrEmpty(bgftError) &&
                        bgftError.StartsWith("ALREADY_INSTALLED:", StringComparison.Ordinal))
                    {
                        error = bgftError;
                        return InstallOutcome.AlreadyInstalled;
                    }
                    if (bgftAttempted && !string.IsNullOrEmpty(bgftError) &&
                        bgftError.StartsWith("BGFT_UNRESOLVED:", StringComparison.Ordinal))
                    {
                        error = bgftError.Substring("BGFT_UNRESOLVED:".Length);
                        return InstallOutcome.InstallFailed;
                    }

                    // AppInstUtil only when BGFT never created/owns a live task.
                    // Never fallback on duplicate/unresolved ownership (could double-install).
                    bool allowFallback = !bgftAttempted && taskId < 0;

                    string fallbackError = null;
                    if (allowFallback && TryInstallWithAppInstUtil(pkgPath, out fallbackError))
                    {
                        taskId = -1;
                        error = null;
                        LogInstall("BGFT failed (" + (bgftError ?? "?") + "); AppInstUtil accepted " + pkgPath);
                        return InstallOutcome.Started;
                    }

                    error = string.IsNullOrEmpty(bgftError) ? "BGFT install registration failed" : bgftError;
                    if (!string.IsNullOrEmpty(fallbackError))
                        error = error + " | fallback " + fallbackError;
                    LogInstall(error);
                    return InstallOutcome.InstallFailed;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return InstallOutcome.InstallFailed;
            }
        }

        public static bool WaitForInstall(int taskId, Action<int> progress, out string error)
        {
            bool localCopyComplete;
            return WaitForInstall(taskId, null, PkgContentKind.Unknown, progress,
                out localCopyComplete, out error);
        }

        /// <summary>
        /// Poll BGFT install task. Does not fail on a single transient progress error
        /// (user can open PS4 home while install continues). Optional titleId confirms success.
        /// </summary>
        public static bool WaitForInstall(int taskId, string titleId, Action<int> progress, out string error)
        {
            bool localCopyComplete;
            return WaitForInstall(taskId, titleId, PkgContentKind.Unknown, progress,
                out localCopyComplete, out error);
        }

        public static bool WaitForInstall(int taskId, string titleId, PkgContentKind contentKind,
            Action<int> progress, out bool localCopyComplete, out string error, Func<bool> interrupt = null,
            string sourcePath = null)
        {
            error = null;
            localCopyComplete = false;
            if (taskId < 0) return true;
            int lastPercent = -2;
            int consecutivePollFails = 0;
            int completedTransferStallPolls = 0;
            int lastInstallPhase = -1;
            int stableCopyPolls = 0;
            int titlePresentPolls = 0;
            long expectedBytes = 0;
            try { if (!string.IsNullOrEmpty(sourcePath)) expectedBytes = new FileInfo(sourcePath).Length; } catch { }
            // Completed local bytes are valid even when copy percentage stays at
            // zero. Also confirm the promoted base before releasing its input.
            const int MinStableCopyPolls = 8; // ~4s
            const int MinTitlePresentPolls = 4; // ~2s after title appears
            try
            {
                // Local BGFT installs can take a long time for large games.
                for (int i = 0; i < 28800; i++)
                {
                    if (interrupt != null && interrupt()) { error = "Installation stopped by queue"; return false; }
                    BgftTaskProgress state;
                    int rc = sceBgftServiceDownloadGetProgress(taskId, out state);
                    if (i % 20 == 0 || (rc != 0 && consecutivePollFails == 0) || state.ErrorResult != 0)
                        LogInstall("local-progress title=" + (titleId ?? "") + " expected=" + expectedBytes +
                            " installed_proof_polls=" + titlePresentPolls + " rc=" + Hex(rc) + " " +
                            FormatBackgroundProgress(MapBackgroundProgress(taskId, state)));
                    if (rc != 0)
                    {
                        consecutivePollFails++;
                        // AppExists also reports incomplete dashboard placeholders.
                        // Losing the task cannot prove that its source was installed.
                        if (consecutivePollFails >= 120)
                        {
                            error = "BGFT progress 0x" + rc.ToString("X");
                            return false;
                        }
                        try { sceSystemServicePowerTick(); } catch { }
                        Thread.Sleep(500);
                        continue;
                    }
                    consecutivePollFails = 0;

                    if (state.ErrorResult != 0)
                    {
                        error = "BGFT install " + PkgInstallPolicy.DescribeBgftError(state.ErrorResult);
                        return false;
                    }

                    ulong total = state.LengthTotal != 0 ? state.LengthTotal : state.Length;
                    ulong done = state.TransferredTotal != 0 ? state.TransferredTotal : state.Transferred;
                    // Never treat total==0 as transfer complete (local install may leave lengths 0).
                    bool transferComplete = total > 0 && done >= total;
                    bool copySignal = state.LocalCopyPercent >= 100 ||
                                      (state.PreparingPercent >= 100 && state.LocalCopyPercent >= 50);
                    int installPhase = Math.Max(state.PreparingPercent, state.LocalCopyPercent);
                    int percent = (transferComplete || copySignal)
                        ? Math.Min(100, Math.Max(installPhase, 1))
                        : (total > 0 ? Math.Min(99, (int)(done * 100UL / total)) : installPhase);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        if (progress != null) progress(percent);
                    }

                    bool copyDone = PkgInstallPolicy.LocalInstallCopyComplete(expectedBytes,
                        state.Length, state.Transferred, state.LengthTotal, state.TransferredTotal, state.LocalCopyPercent);
                    if (copyDone)
                        stableCopyPolls++;
                    else
                        stableCopyPolls = 0;

                    bool titleOk = true;
                    if (!string.IsNullOrEmpty(titleId) && contentKind == PkgContentKind.BaseGame)
                    {
                        if (copyDone && (!string.IsNullOrEmpty(sourcePath)
                            ? IsBasePackageInstalled(sourcePath, titleId) : IsTitleInstalled(titleId)))
                            titlePresentPolls++;
                        else
                            titlePresentPolls = 0;
                        // Base game: do not claim complete until the title exists.
                        titleOk = titlePresentPolls >= MinTitlePresentPolls;
                    }
                    else if (contentKind == PkgContentKind.Patch || contentKind == PkgContentKind.AddOn)
                    {
                        // Patch/DLC: AppExists only proves base is present — never treat as
                        // install confirmation. Require stable LocalCopyPercent only.
                        titleOk = true;
                    }

                    if (stableCopyPolls >= MinStableCopyPolls && titleOk)
                    {
                        LogInstall("local-copy-confirmed title=" + (titleId ?? "") + " task=" + taskId +
                            " " + FormatBackgroundProgress(MapBackgroundProgress(taskId, state)));
                        localCopyComplete = true;
                        return true;
                    }

                    if (transferComplete || copyDone)
                    {
                        if (installPhase != lastInstallPhase)
                        {
                            lastInstallPhase = installPhase;
                            completedTransferStallPolls = 0;
                        }
                        else if (++completedTransferStallPolls >= 240)
                        {
                            // Stalled: if base title is installed, succeed without
                            // claiming localCopyComplete (caller keeps PKG).
                            if (!string.IsNullOrEmpty(titleId) &&
                                contentKind == PkgContentKind.BaseGame &&
                                IsTitleInstalled(titleId))
                            {
                                localCopyComplete = false;
                                return true;
                            }
                            error = "BGFT transfer completed but PS4 install did not advance";
                            return false;
                        }
                    }
                    try { sceSystemServicePowerTick(); } catch { }
                    Thread.Sleep(500);
                }
                error = "BGFT install timeout";
                return false;
            }
            catch (Exception ex)
            {
                error = "BGFT progress " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        public static bool TryGetBackgroundProgress(int taskId, string contentId, int subType,
            out BgftProgress progress, out string error)
        {
            progress = null;
            error = null;
            string initError;
            if (!EnsureBgftReady(out initError))
            {
                error = initError;
                return false;
            }
            try
            {
                BgftTaskProgress state = new BgftTaskProgress();
                int activeTask;
                if (!BgftCancellation.ResolveOwned(contentId, subType,
                    sceBgftServiceDownloadFindTaskByContentId,
                    id => IsOwnedBackgroundTask(id, contentId, subType), out activeTask, out error)) return false;
                int rc = activeTask >= 0 ? sceBgftServiceDownloadGetProgress(activeTask, out state) : BgftTaskNotFound;
                if (rc != 0 || state.ErrorResult != 0)
                    LogInstall("api=sceBgftServiceDownloadGetProgress rc=" + Hex(rc) + " error=" + Hex(state.ErrorResult) +
                        " task=" + activeTask + " content=" + contentId + " subtype=" + subType);
                if (rc != 0)
                {
                    error = "sceBgftServiceDownloadGetProgress " + PkgInstallPolicy.DescribeBgftError(rc);
                    return false;
                }

                progress = MapBackgroundProgress(activeTask, state);
                return true;
            }
            catch (Exception ex)
            {
                error = "BGFT progress " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        static BgftProgress MapBackgroundProgress(int taskId, BgftTaskProgress state)
        {
            ulong total = state.LengthTotal != 0 ? state.LengthTotal : state.Length;
            ulong done = state.TransferredTotal != 0 ? state.TransferredTotal : state.Transferred;
            // Candidate flags only — callers must require stable polls + expected size.
            // Never treat LocalCopyPercent==100 alone as terminal (fires early on web BGFT).
            bool downloadComplete = total > 0 && done >= total && done > 0;
            bool copyInRange = state.LocalCopyPercent >= 0 && state.LocalCopyPercent <= 100;
            bool installComplete = downloadComplete && copyInRange && state.LocalCopyPercent >= 100;
            return new BgftProgress
            {
                TaskId = taskId,
                Done = ToLong(done),
                Total = ToLong(total),
                ErrorResult = state.ErrorResult,
                DownloadComplete = downloadComplete,
                InstallComplete = installComplete,
                LocalCopyPercent = state.LocalCopyPercent,
                EtaSeconds = ValidBgftEta(state.RestSecTotal, state.RestSec),
                Finished = installComplete,
                RawLength = state.Length,
                RawTransferred = state.Transferred,
                RawLengthTotal = state.LengthTotal,
                RawTransferredTotal = state.TransferredTotal,
                NumIndex = state.NumIndex,
                NumTotal = state.NumTotal,
                Bits = state.Bits,
                PreparingPercent = state.PreparingPercent
            };
        }

        internal static string FormatBackgroundProgress(BgftProgress progress)
        {
            if (progress == null) return "";
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "bgft_raw task={0} length={1} transferred={2} length_total={3} transferred_total={4}" +
                " index={5} count={6} bits=0x{7:X8} preparing={8} copy={9} error=0x{10:X8}",
                progress.TaskId, progress.RawLength, progress.RawTransferred,
                progress.RawLengthTotal, progress.RawTransferredTotal,
                progress.NumIndex, progress.NumTotal, progress.Bits,
                progress.PreparingPercent, progress.LocalCopyPercent,
                unchecked((uint)progress.ErrorResult));
        }

        internal static int ValidBgftEta(uint total, uint current)
        {
            // BGFT uses sentinel values while calculating. Never display those as an ETA.
            if (total > 0 && total <= 30 * 24 * 60 * 60) return (int)total;
            return current > 0 && current <= 30 * 24 * 60 * 60 ? (int)current : 0;
        }

        public static bool PauseBackground(int taskId, string contentId, int subType,
            out int activeTaskId, out string error)
        {
            return ControlBackground(taskId, contentId, subType, sceBgftServiceDownloadPauseTask,
                "pause", out activeTaskId, out error);
        }

        public static bool ResumeBackground(int taskId, string contentId, int subType,
            out int activeTaskId, out string error)
        {
            return ControlBackground(taskId, contentId, subType, sceBgftServiceDownloadResumeTask,
                "resume", out activeTaskId, out error);
        }

        public static bool CancelBackground(int taskId, string contentId, int subType,
            out int activeTaskId, out string error)
        {
            activeTaskId = taskId;
            error = null;
            if (taskId < 0 && (string.IsNullOrWhiteSpace(contentId) || subType <= 0))
            {
                lock (InstallRegisterGate)
                {
                    LoadOwnedJournal();
                    if (OwnedWebTasks.Count == 0) { activeTaskId = -1; return true; }
                }
            }
            string initError;
            if (!EnsureBgftReady(out initError))
            {
                error = initError;
                return false;
            }
            try
            {
                return BgftCancellation.Cancel(taskId, contentId, subType,
                    sceBgftServiceDownloadFindTaskByContentId,
                    id => IsOwnedBackgroundTask(id, contentId, subType),
                    sceBgftServiceDownloadStopTask, sceBgftServiceIntDownloadUnregisterTask,
                    ReleaseWebTask, out activeTaskId, out error);
            }
            catch (Exception ex)
            {
                error = "BGFT unregister " + ex.GetType().Name;
                return false;
            }
        }

        public static bool IsTitleInstalled(string titleId)
        {
            bool exists;
            return TryIsTitleInstalled(titleId, out exists) && exists;
        }

        public static bool TryIsTitleInstalled(string titleId, out bool installed)
        {
            installed = false;
            try
            {
                if (!EnsureReady()) return false;
                int exists;
                if (sceAppInstUtilAppExists(titleId ?? "", out exists) != 0) return false;
                installed = exists != 0;
                return true;
            }
            catch { return false; }
        }

        internal static bool IsAddonInstalled(string source, bool full)
        {
            string content;
            if (!PkgValidator.TryGetContentId(source, out content) || content == null || content.Length != 36) return false;
            string title = content.Substring(7, 9), label = content.Substring(20);
            if (!System.Text.RegularExpressions.Regex.IsMatch(title, "^CUSA[0-9]{5}$") ||
                !System.Text.RegularExpressions.Regex.IsMatch(label, "^[A-Za-z0-9_-]{16}$")) return false;
            foreach (string root in new[] { "/user/addcont/", "/mnt/ext0/user/addcont/" })
            {
                string path = root + title + "/" + label + "/ac.pkg";
                if (!PkgInstallPolicy.MatchesInstalledContainer(source, path)) continue;
                if (!full) return true;
                try {
                    using (var a = File.OpenRead(source))
                    using (var b = File.OpenRead(path)) {
                        byte[] left = new byte[65536], right = new byte[65536]; long remaining = a.Length;
                        while (remaining > 0) {
                            int n = (int)Math.Min(remaining, left.Length);
                            if (a.Read(left, 0, n) != n || b.Read(right, 0, n) != n) return false;
                            for (int i = 0; i < n; i++) if (left[i] != right[i]) return false;
                            remaining -= n;
                        }
                        return true;
                    }
                } catch { }
            }
            return false;
        }

        internal static bool IsBasePackageInstalled(string source, string titleId)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(titleId ?? "", "^[A-Z]{4}[0-9]{5}$")) return false;
            foreach (string root in new[] { "/user/app/", "/mnt/ext0/user/app/" })
            {
                string path = root + titleId + "/app.pkg";
                if (!PkgInstallPolicy.MatchesInstalledContainer(source, path)) continue;
                string error;
                if (PkgIntegrity.CheckMetadata(path, titleId, "gd", out error)) return true;
            }
            return false;
        }

        public static bool TryInstallWithAppInstUtil(string pkgPath, out string error)
        {
            error = null;
            PkgContentKind kind; string kindError;
            if (!PkgValidator.TryGetContentKind(pkgPath, out kind, out kindError)) { error = kindError; return false; }
            if (kind == PkgContentKind.Patch) { error = "Updates require the verified BGFT download queue; local fallback is disabled"; return false; }
            if (!EnsureReady())
            {
                error = _lastError;
                return false;
            }
            try
            {
                string nativePath;
                string nativeTitleId;
                string nativeState;
                if (!TryResolveAppInstPath(pkgPath, out nativePath, out nativeTitleId,
                    out nativeState))
                {
                    error = "PKG unavailable to AppInstUtil; checked " + nativeState;
                    LogInstall(error);
                    return false;
                }
                lock (InstallRegisterGate)
                {
                    int rc = sceAppInstUtilAppInstallPkg(nativePath, IntPtr.Zero);
                    LogInstall("api=sceAppInstUtilAppInstallPkg rc=" + Hex(rc) + " task=-1 title=" + nativeTitleId +
                        " subtype=" + (kind == PkgContentKind.AddOn ? 7 : 6) + " path=" + nativePath + " file={" + nativeState + "}");
                    if (rc != 0)
                    {
                        error = DescribeInstallError("sceAppInstUtilAppInstallPkg", rc) +
                            " path=" + nativePath + " file={" + nativeState + "}";
                        LogInstall(error);
                        return false;
                    }
                }
                // rc==0 is asynchronous acceptance, not proof that the service has finished
                // reading the source. The caller keeps this exact PKG until user verification.
                LogInstall("AppInstUtil fallback accepted path=" + nativePath +
                    " title=" + nativeTitleId + " file={" + nativeState + "}");
                return true;
            }
            catch (Exception ex)
            {
                error = "InstallPkg fallback " + ex.GetType().Name + ": " + ex.Message;
                LogInstall(error);
                return false;
            }
        }

        delegate int BgftControl(int taskId);

        static bool ControlBackground(int taskId, string contentId, int subType, BgftControl control,
            string operation, out int activeTaskId, out string error)
        {
            activeTaskId = taskId;
            error = null;
            string initError;
            if (!EnsureBgftReady(out initError))
            {
                error = initError;
                return false;
            }
            try
            {
                if (!BgftCancellation.ResolveOwned(contentId, subType,
                    sceBgftServiceDownloadFindTaskByContentId,
                    id => IsOwnedBackgroundTask(id, contentId, subType), out activeTaskId, out error)) return false;
                int rc = activeTaskId >= 0 ? control(activeTaskId) : BgftTaskNotFound;
                if (rc != 0)
                {
                    error = "BGFT " + operation + " " + Hex(rc);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "BGFT " + operation + " " + ex.GetType().Name;
                return false;
            }
        }

        static bool TryFindBackgroundTask(string contentId, int subType, out int taskId)
        {
            taskId = -1;
            return !string.IsNullOrEmpty(contentId) && subType > 0 &&
                sceBgftServiceDownloadFindTaskByContentId(contentId, subType, out taskId) == 0;
        }

        public static bool TryFindBackgroundTaskIdentity(string contentId, int subType,
            out bool found, out int taskId, out string error)
        {
            found = false;
            taskId = -1;
            error = null;
            string initError;
            if (string.IsNullOrEmpty(contentId) || subType <= 0)
            {
                error = "BGFT lookup requires content identity";
                return false;
            }
            if (!EnsureBgftReady(out initError))
            {
                error = initError;
                return false;
            }
            try
            {
                int rc = sceBgftServiceDownloadFindTaskByContentId(contentId, subType, out taskId);
                if (rc == 0 && taskId >= 0)
                {
                    found = true;
                    return true;
                }
                taskId = -1;
                if (rc == BgftTaskNotFound || (rc == 0 && taskId < 0)) return true;
                error = "BGFT find " + Hex(rc);
                return false;
            }
            catch (Exception ex)
            {
                error = "BGFT find " + ex.GetType().Name;
                return false;
            }
        }

        static bool UninstallAndWait(string titleId, out string error)
        {
            error = null;
            if (!_uninstallAvailable)
            {
                error = "UnInstall export unavailable on this FW";
                return false;
            }
            try
            {
                int rc = sceAppInstUtilAppUnInstall(titleId);
                if (rc != 0)
                {
                    error = "UnInstall 0x" + rc.ToString("X");
                    return false;
                }
            }
            catch (EntryPointNotFoundException)
            {
                _uninstallAvailable = false;
                error = "UnInstall not found — delete app from PS4 home screen";
                return false;
            }
            catch (Exception ex)
            {
                error = "UnInstall: " + ex.Message;
                return false;
            }

            // Poll until gone (max ~90s)
            for (int i = 0; i < 180; i++)
            {
                Thread.Sleep(500);
                int exists = 1;
                try
                {
                    int rc = sceAppInstUtilAppExists(titleId, out exists);
                    if (rc == 0 && exists == 0) return true;
                }
                catch { }
            }
            error = "Uninstall timeout — delete from PS4 Storage manually";
            return false;
        }

        static bool TryResolveAppInstPath(string pkgPath, out string nativePath,
            out string titleId, out string state)
        {
            nativePath = "";
            titleId = "";
            var detail = new StringBuilder();
            string[] candidates = PkgInstallPath.AppInstCandidates(pkgPath);
            foreach (string candidate in candidates)
            {
                if (detail.Length > 0) detail.Append("; ");
                detail.Append("path=").Append(candidate).Append(' ')
                    .Append(DescribeManagedFile(candidate));
                try
                {
                    byte[] titleBuffer = new byte[16];
                    int isApp;
                    int rc = sceAppInstUtilGetTitleIdFromPkg(candidate, titleBuffer, out isApp);
                    detail.Append(" nativeGetTitleId=").Append(Hex(rc));
                    if (rc != 0) continue;
                    string parsed = Encoding.ASCII.GetString(titleBuffer).TrimEnd('\0');
                    if (string.IsNullOrEmpty(parsed))
                    {
                        detail.Append(" title=<empty>");
                        continue;
                    }
                    nativePath = candidate;
                    titleId = parsed;
                    detail.Append(" title=").Append(parsed);
                    state = detail.ToString();
                    return true;
                }
                catch (Exception ex)
                {
                    detail.Append(" nativeGetTitleId=").Append(ex.GetType().Name)
                        .Append(':').Append(ex.Message);
                }
            }
            state = detail.Length > 0 ? detail.ToString() :
                "path=<empty> managed=missing nativeGetTitleId=not-called";
            return false;
        }

        static string DescribeManagedFile(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return "managed=missing";
                if (!File.Exists(path)) return "managed=missing";
                return "managed=present size=" + new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                return "managed=stat-error:" + ex.GetType().Name + ":" + ex.Message;
            }
        }

        static long ToLong(ulong value)
        {
            return value > long.MaxValue ? long.MaxValue : (long)value;
        }

        static string Hex(int value)
        {
            return "0x" + unchecked((uint)value).ToString("X8");
        }

        static bool TryStartBgftInstall(string pkgPath, string titleId, PkgContentKind contentKind,
            out bool attempted, out string error, out int taskId)
        {
            attempted = false;
            error = null;
            taskId = -1;
            if (!PkgInstallPolicy.UseStorageBgft(contentKind))
            {
                error = contentKind + " uses AppInstUtil local install";
                return false;
            }
            string initError;
            if (!EnsureBgftReady(out initError))
            {
                error = initError;
                return false;
            }

            string pathError;
            long packageSize;
            if (!TryValidateLocalPkgPath(pkgPath, out packageSize, out pathError))
            {
                error = pathError;
                return false;
            }

            var allocated = new List<IntPtr>();
            bool registered = false;
            try
            {
                string contentId = ReadContentId(pkgPath);
                if (string.IsNullOrEmpty(contentId))
                {
                    error = "BGFT: missing content ID";
                    return false;
                }

                int userId;
                if (!TryGetForegroundUser(out userId, out error))
                    return false;

                uint slot;
                int slotRc = sceAppInstUtilGetPrimaryAppSlot(titleId, out slot);
                if (slotRc == AppSlotNotFound)
                    slot = 0;
                else if (slotRc != 0)
                {
                    error = "GetPrimaryAppSlot " + Hex(slotRc);
                    return false;
                }

                // Always /user + /data/... — do not dual-try bare /data (can create two tasks).
                string contentUrl = PkgInstallPath.ForBgftStorage(pkgPath);
                var p = new BgftDownloadParam
                {
                    UserId = userId,
                    EntitlementType = 5,
                    Id = Ansi("", allocated),
                    ContentUrl = Ansi(contentUrl, allocated),
                    ContentExUrl = IntPtr.Zero,
                    ContentName = Ansi(Path.GetFileName(pkgPath) ?? "package.pkg", allocated),
                    IconPath = Ansi("", allocated),
                    SkuId = IntPtr.Zero,
                    Option = BgftDisableCdnQueryParam,
                    PlaygoScenarioId = Ansi("0", allocated),
                    ReleaseDate = IntPtr.Zero,
                    // Empty non-null C strings (storage path). Do not invent PS4GD/PS4AC labels.
                    PackageType = Ansi("", allocated),
                    PackageSubType = Ansi("", allocated),
                    PackageSize = packageSize > 0 ? (ulong)packageSize : 0UL
                };
                var ex = new BgftDownloadParamEx { Params = p, Slot = slot };
                attempted = true; // register mutates system state
                int rc = sceBgftServiceIntDownloadRegisterTaskByStorageEx(ref ex, out taskId);
                LogInstall("api=sceBgftServiceIntDownloadRegisterTaskByStorageEx rc=" + Hex(rc) + " task=" + taskId +
                    " content=" + contentId + " subtype=6 bytes=" + packageSize + " path=" + contentUrl);
                string recovery;
                if (taskId < 0 && PkgInstallPolicy.IsBgftDuplicate(rc) &&
                    TryRetireFailedDuplicate(contentId, 6, out recovery))
                {
                    rc = sceBgftServiceIntDownloadRegisterTaskByStorageEx(ref ex, out taskId);
                    LogInstall("BGFT storage retry after owned recovery rc=" + Hex(rc) + " task=" + taskId);
                }
                if (rc != 0 || taskId < 0)
                {
                    if (taskId >= 0)
                    {
                        registered = true;
                        ClaimWebTask(taskId, contentId, 6);
                        error = "BGFT_UNRESOLVED:Registration returned " + Hex(rc) +
                            " with task " + taskId + "; task and PKG retained";
                        LogInstall(error);
                        return false;
                    }
                    if (rc == BgftTaskDuplicated || rc == BgftContentAlreadyDownloading)
                    {
                        taskId = -1;
                        error = "BGFT_UNRESOLVED:An existing PS4 download conflicts with this package. Open PS4 Notifications > Downloads, finish or cancel the matching title, then retry. PKG retained.";
                        LogInstall("BGFT duplicate ambiguous content=" + contentId + " rc=" + Hex(rc));
                        return false;
                    }
                    if (rc == BgftSameApplicationInstalled)
                    {
                        error = "BGFT_UNRESOLVED:PS4 reports existing content, but this installation is not confirmed. Check PS4 Downloads; PKG retained.";
                        LogInstall(error);
                        return false;
                    }
                    error = (rc == 0 ? "BGFT register returned no task" : "BGFT register " + PkgInstallPolicy.DescribeBgftError(rc)) + " path=" + contentUrl +
                        " source={path=" + pkgPath + " " + DescribeManagedFile(pkgPath) + "}";
                    LogInstall(error);
                    return false;
                }
                registered = true;
                ClaimWebTask(taskId, contentId, 6);
                string startDetail;
                if (!TryStartOwnedBgftTask(taskId, out startDetail))
                {
                    int stopRc = -1, unregisterRc = -1;
                    try { stopRc = sceBgftServiceDownloadStopTask(taskId); } catch { }
                    try { unregisterRc = sceBgftServiceIntDownloadUnregisterTask(taskId); }
                    catch { unregisterRc = -1; }
                    bool stillOwned = unregisterRc != 0;
                    registered = stillOwned;
                    if (!stillOwned) ReleaseWebTask(taskId);
                    taskId = stillOwned ? taskId : -1;
                    error = (stillOwned ? "BGFT_UNRESOLVED:" : "") + "BGFT " +
                        startDetail + " stop " + Hex(stopRc) + " unregister " + Hex(unregisterRc);
                    LogInstall(error);
                    return false;
                }
                LogInstall("BGFT local started task=" + taskId + " " + startDetail + " content=" + contentId +
                    " path=" + contentUrl + " size=" + packageSize);
                return true;
            }
            catch (Exception ex)
            {
                error = (registered ? "BGFT_UNRESOLVED:" : "") + "BGFT " +
                    ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                foreach (IntPtr p in allocated)
                    if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            }
        }

        static bool TryRetireFailedDuplicate(string contentId, int subType, out string detail)
        {
            bool recovered = PkgInstallPolicy.TryRetireFailedBgftTask(
                () => { int found; return TryFindBackgroundTask(contentId, subType, out found) ? found : -1; },
                id => IsOwnedBackgroundTask(id, contentId, subType),
                id => { BgftTaskProgress state; int rc = sceBgftServiceDownloadGetProgress(id, out state);
                    return new PkgInstallPolicy.BgftStartProgress { Readable = rc == 0, Error = state.ErrorResult }; },
                sceBgftServiceDownloadStopTask, sceBgftServiceIntDownloadUnregisterTask, ReleaseWebTask, out detail);
            LogInstall("BGFT duplicate recovery content=" + contentId + " subtype=" + subType + " " + detail);
            return recovered;
        }

        static bool TryRegisterBgftWebDownload(string contentUrl, string titleId, string contentId,
            string contentName, int subType, long expectedSize, out int taskId,
            out string error, string packageType = null)
        {
            taskId = -1;
            error = null;
            if (string.IsNullOrEmpty(contentUrl))
            {
                error = "BGFT web: empty URL";
                return false;
            }
            // contentUrl max ~0x800 including NUL
            if (Encoding.UTF8.GetByteCount(contentUrl) + 1 > 0x800)
            {
                error = "BGFT web: URL too long for BGFT";
                return false;
            }

            if (!EnsureReady())
            {
                error = _lastError;
                return false;
            }

            string initError;
            if (!EnsureBgftReady(out initError))
            {
                error = initError;
                return false;
            }

            var allocated = new List<IntPtr>();
            bool registered = false;
            try
            {
                int userId;
                if (!TryGetForegroundUser(out userId, out error))
                    return false;

                // DLC/patch packages require an installed matching base title.
                // Transport errors must be diagnosed separately from this check.
                if ((subType == 7 || subType == 8) && !string.IsNullOrEmpty(titleId))
                {
                    try
                    {
                        int exists = 0;
                        int existsRc = sceAppInstUtilAppExists(titleId, out exists);
                        if (existsRc == 0 && exists == 0)
                        {
                            error = (subType == 7 ? "DLC" : "Update") +
                                " requires installed base game " + titleId + "; install the matching base first";
                            LogInstall("BGFT web preflight refused sub=" + subType + " title=" + titleId + " content=" + (contentId ?? ""));
                            return false;
                        }
                    }
                    catch { }
                }

                string name = string.IsNullOrEmpty(contentName)
                    ? (!string.IsNullOrEmpty(titleId) ? titleId + ".pkg" : "package.pkg")
                    : contentName;
                if (name.Length > 64) name = name.Substring(0, 64);

                var p = new BgftDownloadParam
                {
                    UserId = userId,
                    EntitlementType = 5,
                    Id = Ansi(contentId ?? "", allocated),
                    ContentUrl = Ansi(contentUrl, allocated),
                    ContentExUrl = IntPtr.Zero,
                    ContentName = Ansi(name, allocated),
                    IconPath = Ansi("", allocated),
                    SkuId = IntPtr.Zero,
                    Option = BgftDisableCdnQueryParam,
                    PlaygoScenarioId = Ansi("0", allocated),
                    ReleaseDate = IntPtr.Zero,
                    PackageType = Ansi(packageType ?? (subType == 7 ? "PS4AC" : "PS4GD"), allocated),
                    PackageSubType = Ansi("", allocated),
                    PackageSize = expectedSize > 0 ? (ulong)expectedSize : 0UL
                };

                bool debugRegistration = !PkgInstallPolicy.IsBaseBgftSubType(subType);
                string registerApi = debugRegistration ? "sceBgftServiceIntDebugDownloadRegisterPkg" : "sceBgftServiceIntDownloadRegisterTask";
                lock (InstallRegisterGate)
                {
                    int rc = debugRegistration
                        ? sceBgftServiceIntDebugDownloadRegisterPkg(ref p, out taskId)
                        : sceBgftServiceIntDownloadRegisterTask(ref p, out taskId);
                    LogBgftRegistration(registerApi, rc, taskId, contentId, subType, expectedSize, contentUrl);
                    if (!debugRegistration && taskId < 0 && rc == unchecked((int)0x80F00633))
                    {
                        registerApi = "sceBgftServiceIntDebugDownloadRegisterPkg";
                        taskId = -1;
                        rc = sceBgftServiceIntDebugDownloadRegisterPkg(ref p, out taskId);
                        LogBgftRegistration(registerApi, rc, taskId, contentId, subType, expectedSize, contentUrl);
                    }
                    string recovery;
                    if (taskId < 0 && PkgInstallPolicy.IsBgftDuplicate(rc) &&
                        TryRetireFailedDuplicate(contentId, subType, out recovery))
                    {
                        rc = registerApi == "sceBgftServiceIntDebugDownloadRegisterPkg"
                            ? sceBgftServiceIntDebugDownloadRegisterPkg(ref p, out taskId)
                            : sceBgftServiceIntDownloadRegisterTask(ref p, out taskId);
                        LogBgftRegistration(registerApi, rc, taskId, contentId, subType, expectedSize, contentUrl);
                    }
                    if (rc != 0 || taskId < 0)
                    {
                        if (taskId >= 0)
                        {
                            registered = true;
                            ClaimWebTask(taskId, contentId, subType);
                            error = "BGFT_UNRESOLVED:Registration returned " + Hex(rc) + " with task " + taskId + "; task and PKG retained";
                            LogInstall(error); return false;
                        }
                        if (rc == BgftTaskDuplicated || rc == BgftContentAlreadyDownloading)
                        {
                            error = "BGFT_UNRESOLVED:An existing PS4 download conflicts with this package. Open PS4 Notifications > Downloads, finish or cancel the matching title, then retry. PKG retained.";
                            LogInstall(error + " content=" + (contentId ?? ""));
                            return false;
                        }
                        if (rc == BgftSameApplicationInstalled)
                        {
                            error = PkgInstallPolicy.IsBaseBgftSubType(subType)
                                ? "BGFT_UNRESOLVED:PS4 reports existing content, but this installation is not confirmed. Check PS4 Downloads; PKG retained."
                                : "PS4 rejected this update/add-on identity; matching package required";
                            return false;
                        }
                        error = rc != 0 ? registerApi + " " + PkgInstallPolicy.DescribeBgftError(rc) :
                            "BGFT web register returned no task";
                        LogInstall(error);
                        return false;
                    }
                    registered = true;
                    ClaimWebTask(taskId, contentId, subType);

                    string startDetail;
                    if (!TryStartOwnedBgftTask(taskId, out startDetail))
                    {
                        int stopRc = -1, unregRc = -1;
                        try { stopRc = sceBgftServiceDownloadStopTask(taskId); } catch { }
                        try { unregRc = sceBgftServiceIntDownloadUnregisterTask(taskId); } catch { }
                        registered = unregRc != 0;
                        if (!registered) ReleaseWebTask(taskId);
                        taskId = registered ? taskId : -1;
                        error = (registered ? "BGFT_UNRESOLVED:" : "") +
                            "BGFT web " + startDetail +
                            " stop " + Hex(stopRc) + " unreg " + Hex(unregRc);
                        LogInstall(error);
                        return false;
                    }
                    LogInstall("BGFT web task=" + taskId + " " + startDetail);
                }

                LogInstall("BGFT web started task=" + taskId + " register=" + registerApi + " title=" + titleId +
                    " content=" + (contentId ?? "") + " sub=" + subType +
                    " sizeHint=" + expectedSize);
                return true;
            }
            catch (Exception ex)
            {
                error = (registered ? "BGFT_UNRESOLVED:" : "") + "BGFT web " +
                    ex.GetType().Name + ": " + ex.Message;
                LogInstall(error);
                return false;
            }
            finally
            {
                foreach (IntPtr ptr in allocated)
                    if (ptr != IntPtr.Zero) Marshal.FreeHGlobal(ptr);
            }
        }

        static bool IsAppLoopbackUrl(Uri uri)
        {
            if (uri == null) return false;
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)) return false;
            if (!uri.IsLoopback && !string.Equals(uri.Host, "127.0.0.1", StringComparison.Ordinal)) return false;
            // Current app port plus legacy 8742 for tasks registered before the
            // resident/app port split. New registrations always use Port (8743).
            if (uri.Port != LoopbackPkgServer.Port && uri.Port != LoopbackPkgServer.LegacyResidentPort) return false;
            if (!uri.AbsolutePath.StartsWith("/pkg/", StringComparison.Ordinal)) return false;
            if (!uri.AbsolutePath.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        public static bool TryStartLoopbackBgftDownload(string contentUrl, string titleId, string contentId,
            string contentName, int subType, long expectedSize, out int taskId, out string error, string packageType = null)
        {
            taskId = -1;
            error = null;
            Uri uri;
            if (!Uri.TryCreate(contentUrl, UriKind.Absolute, out uri) || !IsAppLoopbackUrl(uri))
            {
                error = "BGFT loopback: invalid stable URL";
                return false;
            }
            return TryRegisterBgftWebDownload(contentUrl + ".json", titleId, contentId, contentName,
                subType, expectedSize, out taskId, out error, packageType);
        }

        static string BackgroundIdentity(string contentId, int subType)
        {
            return (contentId ?? "") + "\n" + subType;
        }

        static bool TryValidateLocalPkgPath(string pkgPath, out long size, out string error)
        {
            size = 0;
            error = null;
            if (string.IsNullOrEmpty(pkgPath))
            {
                error = "BGFT: empty package path; checked path=<empty> managed=missing";
                return false;
            }
            if (pkgPath.IndexOf('\0') >= 0 || pkgPath.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                error = "BGFT: unsafe package path";
                return false;
            }
            // App-owned downloads live under /data/SSPI/...
            string norm = pkgPath.Replace('\\', '/');
            if (!norm.StartsWith("/data/", StringComparison.OrdinalIgnoreCase) &&
                !norm.StartsWith("/user/data/", StringComparison.OrdinalIgnoreCase) &&
                !norm.StartsWith("/mnt/usb", StringComparison.OrdinalIgnoreCase) &&
                !norm.StartsWith("/mnt/ext", StringComparison.OrdinalIgnoreCase))
            {
                // Still allow if file exists (USB etc.) but prefer /data.
            }
            if (!File.Exists(pkgPath))
            {
                error = "BGFT: package file missing; checked path=" + pkgPath + " " +
                    DescribeManagedFile(pkgPath) + " nativePath=" +
                    PkgInstallPath.ForBgftStorage(pkgPath);
                return false;
            }
            try
            {
                size = new FileInfo(pkgPath).Length;
            }
            catch (Exception ex)
            {
                error = "BGFT: package stat " + ex.Message;
                return false;
            }
            if (size <= 0)
            {
                error = "BGFT: package is empty";
                return false;
            }
            return true;
        }

        static bool TryStartOwnedBgftTask(int taskId, out string detail)
        {
            return PkgInstallPolicy.TryStartBgftTask(
                () => { int rc = sceBgftServiceDownloadStartTask(taskId); LogInstall("api=sceBgftServiceDownloadStartTask rc=" + Hex(rc) + " task=" + taskId); return rc; },
                () => { int rc = sceBgftServiceIntDownloadStartTask(taskId); LogInstall("api=sceBgftServiceIntDownloadStartTask rc=" + Hex(rc) + " task=" + taskId); return rc; },
                () => {
                    BgftTaskProgress state;
                    int rc = sceBgftServiceDownloadGetProgress(taskId, out state);
                    LogInstall("api=sceBgftServiceDownloadGetProgress rc=" + Hex(rc) + " task=" + taskId + " error=" + Hex(state.ErrorResult));
                    return new PkgInstallPolicy.BgftStartProgress {
                        Readable = rc == 0, Error = state.ErrorResult,
                        Preparing = state.PreparingPercent, Copy = state.LocalCopyPercent,
                        Total = state.LengthTotal != 0 ? state.LengthTotal : state.Length,
                        Done = state.TransferredTotal != 0 ? state.TransferredTotal : state.Transferred
                    };
                }, Thread.Sleep, out detail);
        }

        static bool EnsureBgftReady(out string error)
        {
            lock (Gate)
            {
                error = null;
                if (_bgftReady) return true;
                try
                {
                    if (Marshal.SizeOf(typeof(BgftDownloadParam)) != 104 ||
                        Marshal.SizeOf(typeof(BgftDownloadParamEx)) != 112 ||
                        Marshal.SizeOf(typeof(BgftTaskProgress)) != 64 ||
                        (int)Marshal.OffsetOf(typeof(BgftDownloadParam), "PackageSize") != 96)
                    {
                        error = "BGFT ABI mismatch param=" +
                            Marshal.SizeOf(typeof(BgftDownloadParam)) +
                            " ex=" + Marshal.SizeOf(typeof(BgftDownloadParamEx)) +
                            " prog=" + Marshal.SizeOf(typeof(BgftTaskProgress)) +
                            " pkgSizeOff=" + (int)Marshal.OffsetOf(typeof(BgftDownloadParam), "PackageSize");
                        return false;
                    }
                    LoadMod("/system/common/lib/libSceBgft.sprx");
                    _bgftHeap = Marshal.AllocHGlobal(BgftHeapSize);
                    Marshal.Copy(new byte[BgftHeapSize], 0, _bgftHeap, BgftHeapSize);
                    var init = new BgftInitParams
                    {
                        Heap = _bgftHeap,
                        HeapSize = (UIntPtr)BgftHeapSize
                    };
                    int rc = sceBgftServiceIntInit(ref init);
                    if (rc != 0 && rc != BgftAlreadyInitialized)
                    {
                        error = "BGFT init " + PkgInstallPolicy.DescribeBgftError(rc);
                        Marshal.FreeHGlobal(_bgftHeap);
                        _bgftHeap = IntPtr.Zero;
                        return false;
                    }
                    _bgftReady = true;
                    return true;
                }
                catch (Exception ex)
                {
                    if (_bgftHeap != IntPtr.Zero)
                    {
                        Marshal.FreeHGlobal(_bgftHeap);
                        _bgftHeap = IntPtr.Zero;
                    }
                    error = "BGFT init " + ex.GetType().Name;
                    return false;
                }
            }
        }

        static bool TryGetForegroundUser(out int userId, out string error)
        {
            // DllImport sceUserService* fails on this Mono host (0x80960002 / 0x80020001).
            // Use eboot InternalCall path proven by splash/IME.
            userId = 0;
            error = null;
            try
            {
                if (!Orbis.Internals.UserService.TryGetUserId(out userId, out error))
                {
                    if (string.IsNullOrEmpty(error)) error = "BGFT user lookup failed";
                    return false;
                }
                if (userId <= 0)
                {
                    error = "BGFT user id invalid";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "BGFT user " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        static string ReadContentId(string pkgPath)
        {
            using (var fs = new FileStream(pkgPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Position = 0x40;
                byte[] data = new byte[0x30];
                int read = fs.Read(data, 0, data.Length);
                return Encoding.ASCII.GetString(data, 0, read).TrimEnd('\0');
            }
        }

        static IntPtr Ansi(string value, List<IntPtr> allocated)
        {
            IntPtr p = Marshal.StringToHGlobalAnsi(value ?? "");
            allocated.Add(p);
            return p;
        }

        static string DescribeInstallError(string operation, int rc)
        {
            string reason = null;
            switch (unchecked((uint)rc)) {
                case 0x80A3000B: reason = "The PS4 rejected the add-on package (ADDCONT_BROKEN). Local metadata passed; retry with a matching add-on. File retained."; break;
                case 0x80A30006: reason = "PS4 rejected the package DRM type. Use a compatible package; file retained."; break;
                case 0x80A30004: case 0x80A3000A: reason = "Required base game is not fully installed. Finish the base, then retry; file retained."; break;
                case 0x80A3000C: reason = "Close the running game before installing this package; file retained."; break;
            }
            if (reason != null) return reason + " API=" + operation + " rc=" + Hex(rc);
            if (rc == unchecked((int)0x80020012))
                return operation + " EXDEV 0x80020012 (storage path rejected)";
            return operation + " 0x" + rc.ToString("X");
        }

        static void LogInstall(string message)
        {
            try
            {
                SspiLog.Write("download", "install " + (message ?? ""));
            }
            catch { }
        }

        static void LogBgftRegistration(string api, int rc, int task, string content, int subtype, long size, string url)
        {
            Uri parsed; string endpoint = "remote-url-redacted";
            if (Uri.TryCreate(url, UriKind.Absolute, out parsed) && parsed.IsLoopback)
                endpoint = parsed.Authority + parsed.AbsolutePath;
            LogInstall("api=" + api + " rc=" + Hex(rc) + " task=" + task + " content=" + content +
                " subtype=" + subtype + " bytes=" + size + " local_http_endpoint=" + endpoint);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BgftInitParams
        {
            public IntPtr Heap;
            public UIntPtr HeapSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BgftDownloadParam
        {
            public int UserId;
            public int EntitlementType;
            public IntPtr Id;
            public IntPtr ContentUrl;
            public IntPtr ContentExUrl;
            public IntPtr ContentName;
            public IntPtr IconPath;
            public IntPtr SkuId;
            public uint Option;
            public IntPtr PlaygoScenarioId;
            public IntPtr ReleaseDate;
            public IntPtr PackageType;
            public IntPtr PackageSubType;
            public ulong PackageSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BgftDownloadParamEx
        {
            public BgftDownloadParam Params;
            public uint Slot;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BgftTaskProgress
        {
            public uint Bits;
            public int ErrorResult;
            public ulong Length;
            public ulong Transferred;
            public ulong LengthTotal;
            public ulong TransferredTotal;
            public uint NumIndex;
            public uint NumTotal;
            public uint RestSec;
            public uint RestSecTotal;
            public int PreparingPercent;
            public int LocalCopyPercent;
        }

        [DllImport("libkernel", EntryPoint = "sceKernelLoadStartModule", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceKernelLoadStartModule(
            [MarshalAs(UnmanagedType.LPStr)] string path,
            int argc, IntPtr argv, int flags, IntPtr pOpt, IntPtr pRes);

        [DllImport("libSceAppInstUtil", EntryPoint = "sceAppInstUtilInitialize", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceAppInstUtilInitialize();

        [DllImport("libSceAppInstUtil", EntryPoint = "sceAppInstUtilGetTitleIdFromPkg", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceAppInstUtilGetTitleIdFromPkg(
            [MarshalAs(UnmanagedType.LPStr)] string pkgPath,
            byte[] titleId, out int isApp);

        [DllImport("libSceAppInstUtil", EntryPoint = "sceAppInstUtilAppExists", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceAppInstUtilAppExists(
            [MarshalAs(UnmanagedType.LPStr)] string titleId, out int exists);

        [DllImport("libSceAppInstUtil", EntryPoint = "sceAppInstUtilAppInstallPkg", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceAppInstUtilAppInstallPkg(
            [MarshalAs(UnmanagedType.LPStr)] string pkgPath, IntPtr reserved);

        [DllImport("libSceAppInstUtil", EntryPoint = "sceAppInstUtilGetPrimaryAppSlot", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceAppInstUtilGetPrimaryAppSlot(
            [MarshalAs(UnmanagedType.LPStr)] string titleId, out uint slot);

        [DllImport("libSceAppInstUtil", EntryPoint = "sceAppInstUtilAppUnInstall", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceAppInstUtilAppUnInstall(
            [MarshalAs(UnmanagedType.LPStr)] string titleId);

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceIntInit", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceIntInit(ref BgftInitParams initParams);

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceIntDownloadRegisterTaskByStorageEx", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceIntDownloadRegisterTaskByStorageEx(
            ref BgftDownloadParamEx downloadParams, out int taskId);

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceIntDownloadRegisterTask", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceIntDownloadRegisterTask(ref BgftDownloadParam downloadParams, out int taskId);

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceIntDebugDownloadRegisterPkg", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceIntDebugDownloadRegisterPkg(ref BgftDownloadParam downloadParams, out int taskId);

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceDownloadStartTask", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceDownloadStartTask(int taskId);

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceIntDownloadStartTask", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceIntDownloadStartTask(int taskId);

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceDownloadGetProgress", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceDownloadGetProgress(int taskId, out BgftTaskProgress progress);

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceDownloadPauseTask", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceDownloadPauseTask(int taskId);

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceDownloadResumeTask", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceDownloadResumeTask(int taskId);

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceDownloadStopTask", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceDownloadStopTask(int taskId);

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceIntDownloadUnregisterTask", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceIntDownloadUnregisterTask(int taskId);

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceDownloadFindTaskByContentId", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceDownloadFindTaskByContentId(
            [MarshalAs(UnmanagedType.LPStr)] string contentId, int subType, out int taskId);

        [DllImport("libSceSystemService", EntryPoint = "sceSystemServicePowerTick", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceSystemServicePowerTick();
    }
}
