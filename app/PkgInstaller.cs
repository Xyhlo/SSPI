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
        const uint BgftForceUpdate = 0x8;
        const uint BgftDisableCdnQueryParam = 0x10000;
        const int BgftAlreadyInitialized = unchecked((int)0x80990001);
        const int BgftTaskDuplicated = unchecked((int)0x80990015);
        const int BgftTaskNotFound = unchecked((int)0x80990019);
        const int BgftContentAlreadyDownloading = unchecked((int)0x80990086);
        const int BgftSameApplicationInstalled = unchecked((int)0x80990088);
        const int AppSlotNotFound = unchecked((int)0x80A3000E);
        const int UserServiceNotInitialized = unchecked((int)0x80960002);

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
                ArchiveStorage.RequireFreeSpace(AppSettings.DownloadDir,
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
                    if (!uninstallFirst)
                    {
                        error = "ALREADY_INSTALLED:" + titleId;
                        return InstallOutcome.AlreadyInstalled;
                    }
                    if (!UninstallAndWait(titleId, out error))
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
                    bool bgftAttempted;
                    if (contentKind == PkgContentKind.Patch && !PkgIntegrity.CheckInstalledBase(pkgPath, titleId, out error))
                        return InstallOutcome.InvalidPackage;
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
            Action<int> progress, out bool localCopyComplete, out string error)
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
            // LocalCopyPercent can report 100 before BGFT finishes reading the source PKG.
            // Require a stable 100% window and (for base games) title presence before success.
            const int MinStableCopyPolls = 8; // ~4s
            const int MinTitlePresentPolls = 4; // ~2s after title appears
            try
            {
                // Local BGFT installs can take a long time for large games.
                for (int i = 0; i < 28800; i++)
                {
                    BgftTaskProgress state;
                    int rc = sceBgftServiceDownloadGetProgress(taskId, out state);
                    if (rc != 0)
                    {
                        consecutivePollFails++;
                        // If we already saw a durable install, treat poll loss as success.
                        if (!string.IsNullOrEmpty(titleId) &&
                            contentKind == PkgContentKind.BaseGame &&
                            IsTitleInstalled(titleId) &&
                            ++titlePresentPolls >= MinTitlePresentPolls)
                        {
                            localCopyComplete = true;
                            return true;
                        }
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
                        error = "BGFT install 0x" + state.ErrorResult.ToString("X");
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

                    bool copyDone = state.LocalCopyPercent >= 100;
                    if (copyDone)
                        stableCopyPolls++;
                    else
                        stableCopyPolls = 0;

                    bool titleOk = true;
                    if (!string.IsNullOrEmpty(titleId) && contentKind == PkgContentKind.BaseGame)
                    {
                        if (IsTitleInstalled(titleId))
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

                    // Local storage installs often leave LengthTotal=0; allow completion via
                    // stable LocalCopyPercent (+ base title for games).
                    bool transferOk = transferComplete || total == 0;
                    if (stableCopyPolls >= MinStableCopyPolls && titleOk && transferOk)
                    {
                        localCopyComplete = true;
                        return true;
                    }

                    if ((transferComplete || copyDone) && stableCopyPolls < MinStableCopyPolls)
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
                int activeTask = taskId;
                int rc = activeTask >= 0 ? sceBgftServiceDownloadGetProgress(activeTask, out state) : -1;
                if (rc != 0 && !string.IsNullOrEmpty(contentId) && subType > 0)
                {
                    rc = sceBgftServiceDownloadFindTaskByContentId(contentId, subType, out activeTask);
                    if (rc == 0)
                        rc = sceBgftServiceDownloadGetProgress(activeTask, out state);
                }
                if (rc != 0)
                {
                    error = "BGFT progress " + Hex(rc);
                    return false;
                }

                ulong total = state.LengthTotal != 0 ? state.LengthTotal : state.Length;
                ulong done = state.TransferredTotal != 0 ? state.TransferredTotal : state.Transferred;
                // Candidate flags only — callers must require stable polls + expected size.
                // Never treat LocalCopyPercent==100 alone as terminal (fires early on web BGFT).
                bool downloadComplete = total > 0 && done >= total && done > 0;
                bool copyInRange = state.LocalCopyPercent >= 0 && state.LocalCopyPercent <= 100;
                bool installComplete = downloadComplete && copyInRange && state.LocalCopyPercent >= 100;
                progress = new BgftProgress
                {
                    TaskId = activeTask,
                    Done = ToLong(done),
                    Total = ToLong(total),
                    ErrorResult = state.ErrorResult,
                    DownloadComplete = downloadComplete,
                    InstallComplete = installComplete,
                    LocalCopyPercent = state.LocalCopyPercent,
                    EtaSeconds = ValidBgftEta(state.RestSecTotal, state.RestSec),
                    Finished = installComplete
                };
                return true;
            }
            catch (Exception ex)
            {
                error = "BGFT progress " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
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
            try
            {
                if (!EnsureReady()) return false;
                int exists;
                return sceAppInstUtilAppExists(titleId ?? "", out exists) == 0 && exists != 0;
            }
            catch { return false; }
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
                    if (rc != 0)
                    {
                        error = DescribeInstallError("InstallPkg fallback", rc) +
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
                int rc = activeTaskId >= 0 ? control(activeTaskId) : -1;
                if (rc != 0 && TryFindBackgroundTask(contentId, subType, out activeTaskId))
                {
                    if (!IsOwnedBackgroundTask(activeTaskId, contentId, subType))
                    {
                        error = "BGFT task " + activeTaskId + " is not owned by Game Search; " + operation + " refused";
                        return false;
                    }
                    rc = control(activeTaskId);
                }
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
                if (rc == BgftTaskNotFound) return true;
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
                if (rc != 0)
                {
                    if (taskId >= 0)
                    {
                        registered = true;
                        error = "BGFT_UNRESOLVED:Registration returned " + Hex(rc) +
                            " with task " + taskId + "; task and PKG retained";
                        LogInstall(error);
                        return false;
                    }
                    if (rc == BgftTaskDuplicated || rc == BgftContentAlreadyDownloading)
                    {
                        // Do not FindTaskByContentId → resume/start: contentId+subtype is not
                        // proof of ownership. Keep PKG; user retries or clears PS4 notification.
                        taskId = -1;
                        error = "duplicate BGFT task already active for this content — finish or cancel it on PS4, PKG kept";
                        LogInstall("BGFT duplicate ambiguous content=" + contentId + " rc=" + Hex(rc));
                        return false;
                    }
                    if (rc == BgftSameApplicationInstalled)
                    {
                        error = "ALREADY_INSTALLED:" + titleId;
                        LogInstall(error);
                        return false;
                    }
                    error = "BGFT register 0x" + rc.ToString("X") + " path=" + contentUrl +
                        " source={path=" + pkgPath + " " + DescribeManagedFile(pkgPath) + "}";
                    LogInstall(error);
                    return false;
                }
                registered = true;
                rc = sceBgftServiceDownloadStartTask(taskId);
                if (rc != 0)
                {
                    // Accept auto-start only if progress is readable AND (error-free) with
                    // either completion signals or advancing counters on a second probe.
                    BgftTaskProgress probe1;
                    if (sceBgftServiceDownloadGetProgress(taskId, out probe1) == 0 &&
                        probe1.ErrorResult == 0)
                    {
                        Thread.Sleep(400);
                        BgftTaskProgress probe2;
                        if (sceBgftServiceDownloadGetProgress(taskId, out probe2) == 0 &&
                            probe2.ErrorResult == 0 &&
                            (IsBgftProgressComplete(probe2) || IsBgftProgressAdvanced(probe1, probe2)))
                        {
                            LogInstall("BGFT local already active task=" + taskId +
                                " content=" + contentId + " size=" + packageSize);
                            return true;
                        }
                    }
                    int stopRc = -1, unregisterRc = -1;
                    try { stopRc = sceBgftServiceDownloadStopTask(taskId); } catch { }
                    try { unregisterRc = sceBgftServiceIntDownloadUnregisterTask(taskId); }
                    catch { unregisterRc = -1; }
                    bool stillOwned = unregisterRc != 0;
                    registered = stillOwned;
                    taskId = stillOwned ? taskId : -1;
                    error = (stillOwned ? "BGFT_UNRESOLVED:" : "") + "BGFT start 0x" +
                        rc.ToString("X") + " stop " + Hex(stopRc) + " unregister " + Hex(unregisterRc);
                    LogInstall(error);
                    return false;
                }
                LogInstall("BGFT local started task=" + taskId + " content=" + contentId +
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

        static bool TryRegisterBgftWebDownload(string contentUrl, string titleId, string contentId,
            string contentName, int subType, long expectedSize, bool startTask, out int taskId,
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
                string registerApi = debugRegistration ? "debug" : "notification";
                lock (InstallRegisterGate)
                {
                    int rc = debugRegistration
                        ? sceBgftServiceIntDebugDownloadRegisterPkg(ref p, out taskId)
                        : sceBgftServiceDownloadRegisterTask(ref p, out taskId);
                    if (!debugRegistration && taskId < 0 && rc != BgftTaskDuplicated && rc != BgftContentAlreadyDownloading && rc != BgftSameApplicationInstalled)
                    {
                        registerApi = "debug-fallback";
                        taskId = -1;
                        rc = sceBgftServiceIntDebugDownloadRegisterPkg(ref p, out taskId);
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
                            error = "BGFT_UNRESOLVED:BGFT web: package already downloading on PS4";
                            LogInstall(error + " content=" + (contentId ?? ""));
                            return false;
                        }
                        if (rc == BgftSameApplicationInstalled)
                        {
                            error = PkgInstallPolicy.IsBaseBgftSubType(subType)
                                ? ("ALREADY_INSTALLED:" + titleId)
                                : "PS4 rejected this update/add-on identity; matching package required";
                            return false;
                        }
                        error = rc != 0 ? "BGFT web register 0x" + rc.ToString("X") :
                            "BGFT web register returned no task";
                        LogInstall(error);
                        return false;
                    }
                    registered = true;
                    ClaimWebTask(taskId, contentId, subType);

                    if (!startTask)
                    {
                        LogInstall("BGFT web registered task=" + taskId + " register=" + registerApi +
                            " title=" + titleId + " content=" + (contentId ?? "") +
                            " sub=" + subType + " sizeHint=" + expectedSize);
                        return true;
                    }

                    int publicStartRc = sceBgftServiceDownloadStartTask(taskId);
                    rc = publicStartRc;
                    if (rc != 0)
                    {
                        try { rc = sceBgftServiceIntDownloadStartTask(taskId); }
                        catch { rc = publicStartRc; }
                    }
                    if (rc != 0)
                    {
                        BgftTaskProgress probe1;
                        if (sceBgftServiceDownloadGetProgress(taskId, out probe1) == 0 &&
                            probe1.ErrorResult == 0)
                        {
                            Thread.Sleep(400);
                            BgftTaskProgress probe2;
                            if (sceBgftServiceDownloadGetProgress(taskId, out probe2) == 0 &&
                                probe2.ErrorResult == 0 &&
                                (IsBgftProgressComplete(probe2) || IsBgftProgressAdvanced(probe1, probe2) ||
                                 probe2.Transferred > 0 || probe2.TransferredTotal > 0 ||
                                 probe2.PreparingPercent > 0 || probe2.LocalCopyPercent > 0))
                            {
                                LogInstall("BGFT web already active task=" + taskId +
                                    " title=" + titleId + " content=" + (contentId ?? ""));
                                return true;
                            }
                        }
                        int stopRc = -1, unregRc = -1;
                        try { stopRc = sceBgftServiceDownloadStopTask(taskId); } catch { }
                        try { unregRc = sceBgftServiceIntDownloadUnregisterTask(taskId); } catch { }
                        registered = unregRc != 0;
                        if (!registered) ReleaseWebTask(taskId);
                        taskId = registered ? taskId : -1;
                        error = (registered ? "BGFT_UNRESOLVED:" : "") +
                            "BGFT web start public " + Hex(publicStartRc) + " internal " + Hex(rc) +
                            " stop " + Hex(stopRc) + " unreg " + Hex(unregRc);
                        LogInstall(error);
                        return false;
                    }
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
                subType, expectedSize, true, out taskId, out error, packageType);
        }

        public static bool TryRegisterLoopbackBgftDownload(string contentUrl, string titleId,
            string contentId, string contentName, int subType, long expectedSize, out int taskId,
            out string error)
        {
            taskId = -1;
            error = null;
            Uri route;
            if (!Uri.TryCreate(contentUrl, UriKind.Absolute, out route) || !IsAppLoopbackUrl(route))
            {
                error = "BGFT loopback: invalid stable URL";
                return false;
            }
            return TryRegisterBgftWebDownload(contentUrl + ".json", titleId, contentId, contentName,
                subType, expectedSize, false, out taskId, out error);
        }

        public static bool TryStartDirectBgftWebDownload(string contentUrl, string titleId,
            string contentId, string contentName, int subType, long expectedSize,
            out int taskId, out string error)
        {
            taskId = -1;
            error = null;
            Uri uri;
            if (!Uri.TryCreate(contentUrl ?? "", UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                error = "BGFT direct: invalid URL";
                return false;
            }
            if (Encoding.UTF8.GetByteCount(contentUrl) + 1 > 0x800)
            {
                error = "BGFT direct: URL too long for BGFT";
                return false;
            }
            return TryRegisterBgftWebDownload(contentUrl, titleId, contentId, contentName,
                subType, expectedSize, true, out taskId, out error);
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
            // App-owned downloads live under /data/GameSearch/...
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

        static bool IsBgftProgressComplete(BgftTaskProgress state)
        {
            if (state.LocalCopyPercent >= 100) return true;
            if (state.PreparingPercent >= 100 && state.LocalCopyPercent >= 100) return true;
            ulong total = state.LengthTotal != 0 ? state.LengthTotal : state.Length;
            ulong done = state.TransferredTotal != 0 ? state.TransferredTotal : state.Transferred;
            return total > 0 && done >= total && state.LocalCopyPercent >= 100;
        }

        static bool IsBgftProgressAdvanced(BgftTaskProgress a, BgftTaskProgress b)
        {
            if (b.LocalCopyPercent > a.LocalCopyPercent) return true;
            if (b.PreparingPercent > a.PreparingPercent) return true;
            ulong aDone = a.TransferredTotal != 0 ? a.TransferredTotal : a.Transferred;
            ulong bDone = b.TransferredTotal != 0 ? b.TransferredTotal : b.Transferred;
            return bDone > aDone;
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
                        error = "BGFT init 0x" + rc.ToString("X");
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
            if (rc == unchecked((int)0x80020012))
                return operation + " EXDEV 0x80020012 (storage path rejected)";
            return operation + " 0x" + rc.ToString("X");
        }

        static void LogInstall(string message)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppSettings.DataDir, "download-install.log"),
                    DateTime.UtcNow.ToString("o") + " " + (message ?? "") + "\n");
            }
            catch { }
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

        [DllImport("libSceBgft", EntryPoint = "sceBgftServiceDownloadRegisterTask", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceBgftServiceDownloadRegisterTask(ref BgftDownloadParam downloadParams, out int taskId);

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

        [DllImport("libSceUserService", EntryPoint = "sceUserServiceGetForegroundUser", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceUserServiceGetForegroundUser(out int userId);

        [DllImport("libSceUserService", EntryPoint = "sceUserServiceInitialize", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceUserServiceInitialize(IntPtr priority);

        [DllImport("libSceSystemService", EntryPoint = "sceSystemServicePowerTick", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceSystemServicePowerTick();
    }
}
