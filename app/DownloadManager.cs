using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Orbis
{
    /// <summary>Bounded transfer queue with independent, ordered package installation.</summary>
    internal sealed partial class DownloadManager
    {
        readonly object _lock = new object();
        readonly object _saveLock = new object();
        readonly object _packageInstallGate = new object();
        long _nextInstallHandoffAt, _nextJobStartAt;
        readonly object _bgftRefreshGate = new object();
        readonly object _transferRouteGate = new object();
        readonly LoopbackPkgServer _loopback = new LoopbackPkgServer();
        readonly List<DlItem> _items = new List<DlItem>();
        readonly AppSettings _cfg;
        const int WorkerCount = 1;
        const int MaximumTorBoxThrottleRenewalsWithoutProgress = 2;
        const int MaximumHttpFailuresPerLink = 3;
        const long SubmittedConfirmationWaitMs = 15 * 60 * 1000;
        const string SubmittedConfirmationTimeoutError = "Installation not confirmed after 15 minutes; check PS4 notifications. Package retained.";
        bool _startupCleanupPending = true;
        long _startupCleanupAfter;
        long _nextOwnedCleanupAt;
        readonly Thread[] _workers = new Thread[WorkerCount];
        readonly HashSet<string> _activeIds = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> _activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        volatile bool _run = true;
        long _saveVersion;
        long _savedVersion;
        bool _manifestLoadFailed;
        int _installRevision;
        // UI refreshes must not wait for removal's file cleanup or manifest writes.
        public int InstallRevision { get { return Volatile.Read(ref _installRevision); } }
        int _residentPreparationAttempted;
        int _parkPollBusy;
        long _parkPollFailureLoggedAt = -60000;
        int _parkTimerStarted;
        Timer _parkPollTimer;
        readonly NerdTelemetry _nerd = new NerdTelemetry();
        public NerdTelemetry Nerd { get { return _nerd; } }

        public DownloadManager(AppSettings cfg)
        {
            NetHttp.ConfigureDownloadTrace(BuildIdentity.SourceHash);
            _cfg = cfg;
            if (_cfg != null) _cfg.ApplyToNetHttp();
            LoadManifest();
            _startupCleanupAfter = TransferClockMs() + 5000;
            for (int i = 0; i < _workers.Length; i++)
            {
                int workerIndex = i;
                _workers[i] = new Thread(() => WorkerLoop(workerIndex))
                    { IsBackground = true, Name = "DlMgr-" + (i + 1) };
                try { _workers[i].Priority = ThreadPriority.BelowNormal; } catch (PlatformNotSupportedException) { }
                _workers[i].Start();
            }
            if (_items.Exists(item => item.ParkedForProvider)) EnsureParkPollTimer();
        }

        public bool IsBusyDownloading
        {
            get
            {
                lock (_lock)
                {
                    foreach (var i in _items)
                        if (i.State == DlState.Downloading || i.State == DlState.Resolving ||
                            i.State == DlState.Finalizing)
                            return true;
                }
                return false;
            }
        }

        public bool IsPipelineActive
        {
            get
            {
                // UI metadata refreshes must defer instead of waiting for disk/BGFT work.
                if (!Monitor.TryEnter(_lock)) return true;
                try
                {
                    return _activeIds.Count != 0 || _items.Exists(item => item.Background ||
                        item.State == DlState.Installing || item.State == DlState.Submitted);
                }
                finally { Monitor.Exit(_lock); }
            }
        }

        DlItem Find(string id)
        {
            foreach (var i in _items)
                if (i.Id == id) return i;
            return null;
        }
    }
}
