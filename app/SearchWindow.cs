using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using Orbis.Internals;
using SDL2.Object;
using SDL2.Types;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using static SDL2.SDL;

namespace Orbis
{
    /// <summary>
    /// Search + Downloads (L1/R1). Native HTTPS, unlock pair, covers, install.
    /// </summary>
    public partial class SearchWindow : Window
    {
        private const int W = 1920;
        private const int H = 1080;
        private const int MaxQuery = 28;
        private const int VisibleRows = 8;
        // Keep first-frame rendering on APIs proven by the PS4 SDL port.
        private static readonly bool AdvancedBlendEffects = false;
        private enum TopTab { Search, Downloads }
        private enum BrowseScreen { Search, Results, Detail }

        private TopTab _tab = TopTab.Search;
        private BrowseScreen _screen = BrowseScreen.Search;
        private string _query = "";
        private int _focus;
        private int _listScroll;
        private int _linkScroll;
        private int _dlFocus;
        private int _dlScroll;
        private bool _splashHidden;
        private uint _frameTime;
        private uint _lastUiRefresh;
        private enum BusyKind { None, Searching, Resolving }
        private BusyKind _busyKind;
        private IntPtr _logoTex;
        private int _logoW, _logoH;
        private bool _logoTried;
        private IntPtr _wordTex;
        private int _wordW, _wordH;
        private bool _wordTried;
        private IntPtr _wallpaperTex;
        private string _wallpaperLoadedPath = "";
        private int _searchGeneration;
        private int _resolveGeneration;
        private bool _kbLower;
        private string _searchError;
        private string _resolveError;

        private bool _settingsOpen;
        private int _settingsFocus;
        private int _settingsPage; // 0=General 1=Link Services 2=Package Sources 3=Appearance
        private int _settingsScroll;
        private string _proxyDraft = "";
        private string _deepbridDraft = "";
        private string _allDebridDraft = "";
        private string _torBoxDraft = "";
        private bool _softKbOpen;
        private bool _softKbForProxy;
        private bool _softKbForDeepbrid;
        private bool _softKbForAllDebrid;
        private bool _softKbForTorBox;
        private bool _softKbForSourceUrl;
        private string _sourceUrlDraft = "";
        private List<SourceUiEntry> _sourceUi = new List<SourceUiEntry>();
        private string _sourceUiError = "";
        private readonly PackageSourceRuntimeBridge _packageSources = new PackageSourceRuntimeBridge();
        private enum SourceInstallStage { Idle, Connecting, Downloading, Validating, Installing, Complete, Failed }
        private SourceInstallStage _sourceInstallStage;
        private long _sourceInstallDone;
        private long _sourceInstallTotal;
        private string _sourceInstallTarget = "";
        private string _sourceInstallDetail = "";
        private uint _sourceInstallShownAt;
        private uint _sourceInstallTerminalAt;
        private int _kbRow, _kbCol;
        private int _kbPressedRow = -1, _kbPressedCol = -1;
        private uint _kbPressedAt;
        private uint _typingPulseAt;
        private int _searchLandingFocus;
        private int _kbSuggestionFocus = -1;
        private readonly List<string> _recentQueries = new List<string>(6);
        private List<GameHit> _suggestionPool = new List<GameHit>();
        private readonly GameHit[] _kbSuggestions = new GameHit[4];
        private int _kbSuggestionCount;
        private readonly List<DlItem> _landingContinue = new List<DlItem>(3);
        private DlItem _landingPebble;
        private readonly List<GameHit> _libraryGames = new List<GameHit>(8);
        private readonly HashSet<string> _libraryHasUpdate =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _libraryScanGen;
        private bool _libraryScanBusy;
        private uint _consoleScanAt;
        private readonly List<GameHit> _consoleInstalled = new List<GameHit>(12);
        private readonly List<GameHit> _cloudPool = new List<GameHit>(18);
        private readonly HashSet<string> _landingCoverRequested =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private uint _landingModelAt;
        private string _firmwareVersion = "unknown";
        private string _freeStorageLabel = "— GB";
        private uint _lastInputAt;
        private uint _cloudFrozenAt;
        private bool _cloudWasFrozen;
        private static readonly string[] KbRows =
        {
            "1234567890",
            "QWERTYUIOP",
            "ASDFGHJKL",
            "ZXCVBNM-.:/?=&_%",
            "  < > az OK"
        };

        private readonly AppSettings _cfg = new AppSettings();
        private readonly PairServer _pair = new PairServer();
        private DownloadManager _dlMgr;
        private CoverCache _covers;
        private bool[,] _qr;
        private int _qrSize;
        private string _pairUrlShown = "";
        private bool _pairSessionActive;
        private bool _pairSuccessConsumed;
        private enum PairUiStage { Idle, Waiting, Complete, Failed, TimedOut }
        private PairUiStage _pairUiStage;
        private string _pairUiDetail = "";
        private enum UiOverlay { None, DownloadActions, ConfirmRemove, ConfirmCancel, Install, ConfirmClearToken, ConfirmClearHistory, QrPair, ConfirmSaveSettings }
        private UiOverlay _uiOverlay;
        private int _overlayFocus;
        private string _overlayDownloadId = "";
        private string _overlayTitle = "";
        private bool _appearanceDirty;
        private bool _settingsDirty;
        private int _libraryScroll;
        private byte[] _wallpaperPixels;
        private int _wallpaperPw, _wallpaperPh;
        private string _wallpaperPendingKey = "";
        private bool _wallpaperLoadBusy;
        private IntPtr _dimTex;
        private string _appearanceAccent = "";
        private string _appearanceAccentName = "";
        private string _appearanceBgMode = "";
        private string _appearanceBgImage = "";
        private bool _appearanceCloud;
        private string _appearanceCloudDensity = "";
        private string _appearanceStyle = "";
        private bool _appearanceReduceMotion;
        private bool _appearanceRecents;
        private bool _appearanceContinue;
        private string _appearanceEta = "";

        private readonly object _lock = new object();
        private readonly HashSet<string> _artworkResolving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _artworkRetryAfter = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _collapsedDownloadGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _installingTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _browseBusy;
        private string _status = "Ready";
        private string _toastText = "";
        private uint _toastStartedAt;
        private bool _toastActive;
        private const uint ToastEnterMs = 160;
        private const uint ToastHoldMs = 4400;
        private const uint ToastExitMs = 160;

        private List<GameHit> _results = new List<GameHit>();
        private GameHit _selected;
        private List<PkgLink> _links = new List<PkgLink>();
        // Kept index-aligned with _links so a queue row receives the immutable source snapshot.
        private List<PackageCandidate> _linkCandidates = new List<PackageCandidate>();
        private List<PackageCandidatePresentation> _linkPresentation =
            new List<PackageCandidatePresentation>();
        // Built only when resolve data or mirror expansion changes; avoids grouping every frame.
        private readonly List<int> _detailRows = new List<int>();
        private readonly HashSet<string> _expandedPackageGroups =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _detailFocus;
        private int _detailScroll;
        private readonly Dictionary<string, uint> _resultCoverFirstSeen =
            new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        private List<DlItem> _downloadView = new List<DlItem>();
        private readonly SDL_Rect[] _softRects = new SDL_Rect[3];
        private readonly SDL_Rect[] _strokeRects = new SDL_Rect[4];

        private sealed class DownloadGroup
        {
            public string TitleId;
            public string Name;
            public string ImageUrl;
            public readonly List<DlItem> Items = new List<DlItem>();
        }

        private sealed class DownloadTreeRow
        {
            public DownloadGroup Group;
            public DlItem Item;
            public bool IsRoot;
            public bool HasChildren;
            public bool IsLastChild;
            public int ChildCount;
        }

        private sealed class SourceInstallView
        {
            public SourceInstallStage Stage;
            public long Done;
            public long Total;
            public string Target;
            public string Detail;
            public uint ShownAt;
            public uint TerminalAt;
        }

        private bool DarkSurfaces { get { return BackdropPattern.IsDarkGradient(_cfg.BackgroundMode); } }
        private SDL_Color Bg { get { return DarkSurfaces ? C(8, 8, 8) : C(20, 20, 20); } }
        private SDL_Color Panel { get { return DarkSurfaces ? C(16, 16, 16) : C(29, 29, 29); } }
        private SDL_Color Row { get { return DarkSurfaces ? C(16, 16, 16) : C(29, 29, 29); } }
        private SDL_Color Raised { get { return DarkSurfaces ? C(27, 27, 27) : C(43, 43, 43); } }
        private SDL_Color Focused { get { return DarkSurfaces ? C(27, 27, 27) : C(43, 43, 43); } }
        private SDL_Color Border { get { return DarkSurfaces ? C(46, 46, 46) : C(53, 53, 53); } }
        // Single live V2 chrome accent. Semantic health/destructive colors are separate.
        private SDL_Color Accent
        {
            get
            {
                ThemeColor color = ThemePalette.Accent(_cfg);
                return C(color.R, color.G, color.B);
            }
        }
        private SDL_Color FocusCyan { get { return Accent; } }
        private static readonly SDL_Color White = C(243, 243, 241);
        private static readonly SDL_Color Muted = C(184, 184, 180);
        private static readonly SDL_Color Dim = C(142, 142, 138);
        private static readonly SDL_Color Danger = C(244, 149, 161);
        private static readonly SDL_Color Ok = C(131, 215, 163);
        private static readonly SDL_Color Warning = C(240, 198, 116);
        private static readonly SDL_Color Violet = C(177, 140, 255);
        private static readonly SDL_Color Shadow = C(0, 0, 0);

        public SearchWindow() : base(W, H)
        {
            FPS = 60;
            ClearR = Bg.r; ClearG = Bg.g; ClearB = Bg.b;
            // Continue the branded PS4 launch screen while managed services initialize.
            try
            {
                if (Renderer != null && Renderer.Handler != IntPtr.Zero)
                {
                    PrepareLaunchBranding(Renderer.Handler);
                    PaintLaunchBranding(Renderer.Handler, 0);
                    if (SDL_UpdateWindowSurface(Handler) == 0)
                    {
                        try { UserService.HideSplashScreen(); _splashHidden = true; } catch { }
                    }
                }
            }
            catch { }
            _firmwareVersion = FirmwareInfo.Probe();

            _cfg.Load();
            if (_cfg.UnlockProviderId == UnlockProviders.DeepbridId || _cfg.UnlockProviderId == UnlockProviders.AllDebridId) { _cfg.UnlockProviderId = _cfg.HasTorBox ? UnlockProviders.TorBoxId : UnlockProviders.RealDebridId; _cfg.DeepbridApiKey = ""; _cfg.AllDebridApiKey = ""; _cfg.Save(); }
            _dlMgr = new DownloadManager(_cfg);
            _covers = new CoverCache();
            _proxyDraft = _cfg.ProxyBaseUrl ?? "";
            _deepbridDraft = _cfg.DeepbridApiKey ?? "";
            _allDebridDraft = _cfg.AllDebridApiKey ?? "";
            _torBoxDraft = _cfg.TorBoxApiKey ?? "";
            RefreshSourceUi();
            LoadRecentQueries();
            RefreshLandingModel();
            StartLibraryUpdateScan();
            _freeStorageLabel = ReadFreeStorageLabel();
            _lastInputAt = UiTick();
            if (Joystick.Online > 0) Joystick.Open(0);

            try { UiFont.Ensure(); } catch { }

            // NativeHttp init is background/lazy — don't block first paint.
            string https = NativeHttp.Available ? "HTTPS OK" : "HTTPS INIT...";
            if (_cfg.HasActiveUnlock)
                SetStatus(https + " | " + UnlockProviders.DisplayName(_cfg.UnlockProviderId) + " ready");
            else
                SetStatus(https + " | Options = pair Real-Debrid");
            Invalidated = true;
        }

        void EnsureSplashHidden()
        {
            if (_splashHidden) return;
            try { UserService.HideSplashScreen(); _splashHidden = true; } catch { }
        }

        void SetStatus(string s) { lock (_lock) { _status = s ?? ""; } }

        static uint UiTick()
        {
            return unchecked((uint)Environment.TickCount);
        }

        static uint UiElapsed(uint startedAt)
        {
            return startedAt == 0 ? 0 : unchecked(UiTick() - startedAt);
        }

        static string ReadFreeStorageLabel()
        {
            // DriveInfo can invoke an unsupported Mono icall on PS4 payloads.
            return "Storage";
        }

        void LoadRecentQueries()
        {
            try
            {
                string path = Path.Combine(AppSettings.DataDir, "search-recents.txt");
                if (!File.Exists(path)) return;
                foreach (string raw in File.ReadAllLines(path))
                {
                    string value = (raw ?? "").Trim();
                    if (value.Length == 0 || _recentQueries.Contains(value)) continue;
                    _recentQueries.Add(value);
                    if (_recentQueries.Count == 6) break;
                }
            }
            catch { }
        }

        void SaveRecentQueries()
        {
            try
            {
                File.WriteAllLines(Path.Combine(AppSettings.DataDir, "search-recents.txt"),
                    _recentQueries.ToArray());
            }
            catch { }
        }

        void RememberQuery(string query)
        {
            query = (query ?? "").Trim();
            if (query.Length == 0) return;
            for (int i = _recentQueries.Count - 1; i >= 0; i--)
                if (string.Equals(_recentQueries[i], query, StringComparison.OrdinalIgnoreCase))
                    _recentQueries.RemoveAt(i);
            _recentQueries.Insert(0, query);
            while (_recentQueries.Count > 6) _recentQueries.RemoveAt(_recentQueries.Count - 1);
            SaveRecentQueries();
        }

        void RefreshKeyboardSuggestions()
        {
            for (int i = 0; i < _kbSuggestions.Length; i++) _kbSuggestions[i] = null;
            _kbSuggestionCount = 0;
            string needle = (_query ?? "").Trim();
            if (needle.Length < 3) { _kbSuggestionFocus = -1; return; }
            for (int i = 0; i < _suggestionPool.Count && _kbSuggestionCount < 4; i++)
            {
                GameHit hit = _suggestionPool[i];
                if (hit == null) continue;
                bool matches = (!string.IsNullOrEmpty(hit.Name) &&
                        hit.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) ||
                    (!string.IsNullOrEmpty(hit.TitleId) &&
                        hit.TitleId.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);
                if (matches) _kbSuggestions[_kbSuggestionCount++] = hit;
            }
            if (_kbSuggestionFocus >= _kbSuggestionCount) _kbSuggestionFocus = -1;
        }

        void RefreshLandingModel()
        {
            _landingContinue.Clear();
            _landingPebble = null;
            List<DlItem> items = _dlMgr == null ? null : _dlMgr.Snapshot();
            if (items != null)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < items.Count; i++)
                {
                    DlItem item = items[i];
                    if (item == null || item.State == DlState.Canceled || item.State == DlState.Installed)
                        continue;
                    if (_landingPebble == null &&
                        (item.State == DlState.Downloading || item.State == DlState.Resolving ||
                         item.State == DlState.Finalizing))
                        _landingPebble = item;
                    string key = string.IsNullOrEmpty(item.TitleId) ? item.Name : item.TitleId;
                    if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;
                    _landingContinue.Add(item);
                    if (_landingContinue.Count == 3) break;
                }
            }

            _cloudPool.Clear();
            var cloudSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _suggestionPool.Count && _cloudPool.Count < 18; i++)
            {
                GameHit hit = _suggestionPool[i];
                if (hit != null && !string.IsNullOrEmpty(hit.TitleId) && cloudSeen.Add(hit.TitleId))
                    _cloudPool.Add(hit);
            }
            for (int i = 0; i < _landingContinue.Count && _cloudPool.Count < 18; i++)
            {
                DlItem item = _landingContinue[i];
                if (item == null || string.IsNullOrEmpty(item.TitleId) || !cloudSeen.Add(item.TitleId)) continue;
                _cloudPool.Add(new GameHit
                {
                    TitleId = item.TitleId,
                    Name = item.Name,
                    ImageUrl = item.ImageUrl,
                    Region = "?",
                    Source = "queue"
                });
            }
            RebuildLibraryGames(items);
            for (int i = 0; i < _libraryGames.Count && _cloudPool.Count < 18; i++)
            {
                GameHit hit = _libraryGames[i];
                if (hit != null && !string.IsNullOrEmpty(hit.TitleId) && cloudSeen.Add(hit.TitleId))
                    _cloudPool.Add(hit);
            }
            _landingModelAt = UiTick();
            int maxFocus = _libraryGames.Count +
                (_cfg.SearchContinue ? _landingContinue.Count : 0) +
                0;
            if (_searchLandingFocus > maxFocus) _searchLandingFocus = maxFocus;
            StartLibraryUpdateScan();
        }

        void RebuildLibraryGames(List<DlItem> items)
        {
            var scanned = _pendingConsoleInstalled;
            if (scanned != null)
            {
                _pendingConsoleInstalled = null;
                _consoleInstalled.Clear();
                _consoleInstalled.AddRange(scanned);
            }
            if (!_consoleScanBusy && (_consoleScanAt == 0 || UiElapsed(_consoleScanAt) > 20000))
            {
                _consoleScanAt = UiTick();
                _consoleScanBusy = true;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try { _pendingConsoleInstalled = InstalledTitleScan.Scan(256); }
                    catch { }
                    finally { _consoleScanBusy = false; }
                });
            }
            _libraryGames.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            // Full library: cover textures stay bounded inside CoverCache (32
            // with eviction); update scans run over the whole list.
            for (int i = 0; i < _consoleInstalled.Count && _libraryGames.Count < 256; i++)
            {
                GameHit hit = _consoleInstalled[i];
                if (hit == null || string.IsNullOrEmpty(hit.TitleId) || !seen.Add(hit.TitleId)) continue;
                if (string.IsNullOrWhiteSpace(hit.Name) || string.Equals(hit.Name, hit.TitleId, StringComparison.OrdinalIgnoreCase))
                {
                    if (items != null) foreach (var saved in items)
                        if (string.Equals(saved.TitleId, hit.TitleId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(saved.Name) && !string.Equals(saved.Name, hit.TitleId, StringComparison.OrdinalIgnoreCase)) { hit.Name = saved.Name; break; }
                    foreach (var saved in _suggestionPool)
                        if (string.Equals(saved.TitleId, hit.TitleId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(saved.Name) && !string.Equals(saved.Name, hit.TitleId, StringComparison.OrdinalIgnoreCase)) { hit.Name = saved.Name; break; }
                }
                _libraryGames.Add(hit);
                if (!string.IsNullOrEmpty(hit.ImageUrl))
                    _covers.Request(hit.TitleId, hit.ImageUrl);
            }
            if (items == null) return;
            for (int i = 0; i < items.Count && _libraryGames.Count < 256; i++)
            {
                DlItem item = items[i];
                if (item == null || string.IsNullOrEmpty(item.TitleId)) continue;
                if (PkgInstallPolicy.IsAddonOrPatchName(item.Kind)) continue;
                if (item.State != DlState.Installed && item.State != DlState.Completed &&
                    item.State != DlState.Submitted)
                    continue;
                if (!seen.Add(item.TitleId)) continue;
                bool onConsole = false;
                try { onConsole = PkgInstaller.IsTitleInstalled(item.TitleId); } catch { }
                if (!onConsole && item.State != DlState.Installed) continue;
                var hit = new GameHit
                {
                    TitleId = item.TitleId,
                    Name = item.Name,
                    ImageUrl = item.ImageUrl,
                    Region = "?",
                    Source = item.SourceAttribution
                };
                _libraryGames.Add(hit);
                if (!string.IsNullOrEmpty(hit.ImageUrl))
                    _covers.Request(hit.TitleId, hit.ImageUrl);
            }
        }

        string _libraryScanKey = "";
        readonly Dictionary<string, string> _libraryUpdateInfo = new Dictionary<string, string>();
        void StartLibraryUpdateScan()
        {
            if (_libraryScanBusy || _libraryGames.Count == 0) return;
            var keys = new List<string>();
            foreach (var game in _libraryGames) keys.Add(game.TitleId + ":" + game.Version);
            keys.Sort(StringComparer.Ordinal);
            string scanKey = string.Join("|", keys);
            if (scanKey == _libraryScanKey && DateTime.UtcNow.Ticks < _nextUpdateScan) return;
            _libraryScanKey = scanKey;
            _nextUpdateScan = DateTime.UtcNow.AddMinutes(15).Ticks;
            GameHit[] copy = _libraryGames.ToArray();
            int gen = ++_libraryScanGen;
            _libraryScanBusy = true;
            new Thread(() =>
            {
                try
                {
                    string err;
                    if (!_packageSources.HasEnabled(out err)) { _nextUpdateScan = DateTime.UtcNow.AddSeconds(30).Ticks; return; }
                    for (int i = 0; i < copy.Length; i++)
                    {
                        if (gen != _libraryScanGen) return;
                        GameHit hit = copy[i];
                        if (hit == null) continue;
                        bool cachedUpdate; string cachedInfo;
                        string updateCacheKey = QueryCache.UpdateKey(hit.TitleId, hit.Version);
                        if (QueryCache.TryUpdateKey(updateCacheKey, out cachedUpdate, out cachedInfo))
                        { lock (_lock) { if (gen != _libraryScanGen) return; if (cachedUpdate) { _libraryHasUpdate.Add(hit.TitleId); _libraryUpdateInfo[hit.TitleId] = cachedInfo; } else { _libraryHasUpdate.Remove(hit.TitleId); _libraryUpdateInfo.Remove(hit.TitleId); } } continue; }
                        List<PackageCandidate> cands = _packageSources.Resolve(hit.TitleId, hit.Name, hit.Region, out err);
                        if (gen != _libraryScanGen) return;
                        bool update = false; string updateInfo = "", newestVersion = hit.Version;
                        if (cands != null)
                        {
                            for (int c = 0; c < cands.Count; c++)
                            {
                                PackageCandidate cand = cands[c];
                                if (cand == null) continue;
                                // Same classifier as the results page (hint + label fallback),
                                // so a badge shown in results always matches library detection.
                                string kind = PackageCandidatePresentation.EffectiveKind(cand);
                                if (kind != "update" && kind != "backport")
                                    continue;
                                bool remoteKnown = HasVersionDigits(cand.PackageVersion);
                                if (remoteKnown && VersionNewer(cand.PackageVersion, newestVersion))
                                {
                                    update = true;
                                    newestVersion = cand.PackageVersion;
                                    updateInfo = (kind == "backport" ? "Backport" : "Update") + " v" + cand.PackageVersion + " · " + (string.IsNullOrEmpty(cand.SourceAttribution) ? cand.SourceId : cand.SourceAttribution);
                                }
                                else if (!remoteKnown && !update)
                                {
                                    // Unparseable remote version is never a confirmed newer
                                    // badge; surface it as inspect-only without moving the baseline.
                                    update = true;
                                    updateInfo = "Update available to inspect · " + (string.IsNullOrEmpty(cand.SourceAttribution) ? cand.SourceId : cand.SourceAttribution);
                                }
                            }
                        }
                        if (!string.IsNullOrEmpty(err)) { _nextUpdateScan = DateTime.UtcNow.AddSeconds(30).Ticks; continue; } // Retry transient failures without caching a negative result.
                        string partial = _packageSources.LastPartialWarning;
                        lock (_lock)
                        {
                            if (gen != _libraryScanGen) return;
                            // Retain the source configuration from before the network request.
                            if (!update && !string.IsNullOrEmpty(partial))
                            {
                                // Partial coverage: useful results stand, but never cache
                                // a negative result — the failed source may hold the update.
                                _libraryHasUpdate.Remove(hit.TitleId);
                                _libraryUpdateInfo.Remove(hit.TitleId);
                            }
                            else
                            {
                                QueryCache.PutUpdateKey(updateCacheKey, update, updateInfo);
                                if (update) { _libraryHasUpdate.Add(hit.TitleId); _libraryUpdateInfo[hit.TitleId] = updateInfo; }
                                else { _libraryHasUpdate.Remove(hit.TitleId); _libraryUpdateInfo.Remove(hit.TitleId); }
                            }
                        }
                    }
                }
                catch { }
                finally
                {
                    _libraryScanBusy = false;
                    Invalidated = true;
                }
            }) { IsBackground = true, Name = "SSPI update scanner" }.Start();
        }

        int _pairRevision;
        volatile bool _pairSourcesChanged;
        void PollPairState()
        {
            if (!_pairSessionActive) return;
            if (_pairSourcesChanged) { _pairSourcesChanged = false; SourcesChanged(); }
            if (_pairRevision != _pair.Revision)
            {
                _pairRevision = _pair.Revision;
                _torBoxDraft = _cfg.TorBoxApiKey ?? "";
                CaptureAppearanceDraft(); _settingsDirty = false;
                SetStatus("Configuration saved · " + UnlockProviders.DisplayName(_cfg.UnlockProviderId));
                Invalidated = true;
            }
            if (!string.IsNullOrEmpty(_pair.PendingSource) && !SourceInstallActive(GetSourceInstallView().Stage))
            { _sourceUrlDraft = _pair.PendingSource; _pair.PendingSource = null; StartSourceInstall(); }
            _pair.InstalledSources = string.Join("\n", _sourceUi.ConvertAll(x => x.Name + " · v" + x.Version + (x.Enabled ? " · Enabled" : " · Disabled")).ToArray());
            var sourceView = GetSourceInstallView();
            _pair.SourceStatus = sourceView.Stage + " · " + sourceView.Detail;
        }

        #region Input
        public void HandleButton(DS4Button button)
        {
            lock (_lock) HandleButtonCore(button);
        }

        void HandleButtonCore(DS4Button button)
        {
            _lastInputAt = UiTick();
            if (!_launchFinished) { FinishLaunchBranding(); Invalidated = true; return; }
            if (_uiOverlay != UiOverlay.None)
            {
                HandleUiOverlay(button);
                Invalidated = true;
                return;
            }
            if (button == DS4Button.SCE_PAD_BUTTON_OPTIONS)
            {
                if (_settingsOpen) TryCloseSettings();
                else OpenSettings();
                return;
            }

            if (_settingsOpen && !_softKbOpen)
            {
                // L1/R1 switch settings pages while overlay is open
                if (button == DS4Button.SCE_PAD_BUTTON_L1)
                {
                    _settingsPage = (_settingsPage + 4) % 5; if (_settingsPage == 2) _settingsPage = 1; if (_settingsPage == 1) StartPairSession();
                    _settingsFocus = 0;
                    _settingsScroll = 0;
                    Invalidated = true;
                    return;
                }
                if (button == DS4Button.SCE_PAD_BUTTON_R1)
                {
                    _settingsPage = (_settingsPage + 1) % 5; if (_settingsPage == 2) _settingsPage = 3; if (_settingsPage == 1) StartPairSession();
                    _settingsFocus = 0;
                    _settingsScroll = 0;
                    Invalidated = true;
                    return;
                }
                HandleSettings(button);
                Invalidated = true;
                return;
            }

            if (_softKbOpen)
            {
                HandleSoftKeyboard(button);
                Invalidated = true;
                return;
            }

            // L1/R1 cycle Search ↔ Downloads
            if (button == DS4Button.SCE_PAD_BUTTON_L1)
            {
                CycleTab(-1);
                Invalidated = true;
                return;
            }
            if (button == DS4Button.SCE_PAD_BUTTON_R1)
            {
                CycleTab(1);
                Invalidated = true;
                return;
            }

            if (button == DS4Button.SCE_PAD_BUTTON_TRIANGLE &&
                _tab == TopTab.Search && _screen != BrowseScreen.Search &&
                _screen != BrowseScreen.Detail)
            {
                _screen = BrowseScreen.Search;
                SetStatus("Search");
                return;
            }

            bool busy;
            lock (_lock) busy = _browseBusy;
            if (busy && _tab == TopTab.Search && button != DS4Button.SCE_PAD_BUTTON_CIRCLE &&
                button != DS4Button.SCE_PAD_BUTTON_L1 && button != DS4Button.SCE_PAD_BUTTON_R1)
            {
                SetStatus("Busy...");
                return;
            }

            if (_tab == TopTab.Downloads)
                HandleDownloads(button);
            else if (_screen == BrowseScreen.Detail)
                HandleDetail(button);
            else
            {
                switch (_screen)
                {
                    case BrowseScreen.Search: HandleSearch(button); break;
                    case BrowseScreen.Results: HandleResults(button); break;
                }
            }
            Invalidated = true;
        }

        void OpenSettings()
        {
            if (!_pair.Running) _cfg.Load();
            _proxyDraft = string.IsNullOrEmpty(_cfg.ProxyBaseUrl) ? _proxyDraft : _cfg.ProxyBaseUrl;
            _deepbridDraft = _cfg.DeepbridApiKey ?? "";
            _allDebridDraft = _cfg.AllDebridApiKey ?? "";
            _torBoxDraft = _cfg.TorBoxApiKey ?? "";
            _settingsOpen = true;
            _settingsPage = 0; // General first — do not auto-start RD pair server
            _settingsFocus = 0;
            _settingsScroll = 0;
            CaptureAppearanceDraft();
            _settingsDirty = false;
            RefreshSourceUi();
            // Pairing starts only from explicit Real-Debrid Connect on Link Services page.
            SetStatus("Settings · L1/R1 pages · Link Services / Package Sources / General");
        }

        void TryCloseSettings()
        {
            if (_settingsDirty || _appearanceDirty)
            {
                _uiOverlay = UiOverlay.ConfirmSaveSettings;
                _overlayTitle = "Save settings?";
                _overlayFocus = 0;
                return;
            }
            CloseSettings();
        }

        void DiscardSettingsAndClose()
        {
            try { _cfg.Load();
            } catch { }
            if (_appearanceDirty) RevertAppearanceDraft();
            _settingsDirty = false;
            _appearanceDirty = false;
            CloseSettings();
        }

        void SaveSettingsAndClose()
        {
            _cfg.ProxyBaseUrl = (_proxyDraft ?? "").Trim();
            _cfg.DeepbridApiKey = (_deepbridDraft ?? "").Trim();
            _cfg.AllDebridApiKey = (_allDebridDraft ?? "").Trim();
            _cfg.TorBoxApiKey = (_torBoxDraft ?? "").Trim();
            _cfg.ValidateAppearance(true);
            _cfg.Save();
            CaptureAppearanceDraft();
            _settingsDirty = false;
            CloseSettings();
            User.NotifyToast("Settings saved");
        }

        void MarkSettingsDirty()
        {
            _settingsDirty = true;
            Invalidated = true;
        }

        void CloseSettings()
        {
            _settingsOpen = false;
            _softKbOpen = false;
            _softKbForDeepbrid = false;
            _softKbForAllDebrid = false;
            _softKbForTorBox = false;
            _softKbForSourceUrl = false;
            SetStatus(_cfg.HasActiveUnlock
                ? (UnlockProviders.DisplayName(_cfg.UnlockProviderId) + " ready | " + NetHttp.TransportLabel)
                : "Link Service not set");
        }

        void StartPairSession()
        {
            try
            {
                _pair.Settings = _cfg;
                _pair.SetSourceEnabled = (id, enabled) => {
                    string error;
                    if (!_packageSources.SetEnabled(id, enabled, out error)) return error ?? "Source update failed";
                    lock (_lock) { ++_libraryScanGen; _nextUpdateScan = 0; _libraryHasUpdate.Clear(); _libraryUpdateInfo.Clear(); }
                    _pairSourcesChanged = true; return null;
                };
                _pair.Start(1440);
                if (!_pair.Running)
                {
                    _pairSessionActive = false;
                    _pairUiStage = PairUiStage.Failed;
                    _pairUiDetail = string.IsNullOrEmpty(_pair.Status)
                        ? "Could not start pairing." : _pair.Status;
                    SetStatus("Pair server fail: " + _pairUiDetail);
                    _pairUrlShown = "";
                    _qr = null;
                    _qrSize = 0;
                    return;
                }
                _pairSessionActive = true;
                _pairSuccessConsumed = false;
                _pairUiStage = PairUiStage.Waiting;
                _pairUiDetail = "Waiting for an API key from the pairing page.";
                _pairUrlShown = _pair.PairUrl;
                try { _qr = QrCode.Encode(_pairUrlShown, out _qrSize); }
                catch { _qr = null; _qrSize = 0; }
                SetStatus("Pair: " + _pairUrlShown);
            }
            catch (Exception ex)
            {
                _pairSessionActive = false;
                _pairUiStage = PairUiStage.Failed;
                _pairUiDetail = "Could not start pairing: " + ex.Message;
                SetStatus("Pair server fail: " + ex.Message);
                User.NotifyToast("Pairing failed");
                _pairUrlShown = "";
                _qr = null;
            }
        }

        void HandleSettings(DS4Button b)
        {
            if (b == DS4Button.SCE_PAD_BUTTON_CIRCLE)
            {
                if (_settingsPage == 2) { _settingsPage = 1; _settingsFocus = 3; return; }
                TryCloseSettings();
                return;
            }
            if (_settingsPage == 0) HandleSettingsGeneral(b);
            else if (_settingsPage == 1) HandleSettingsUnlock(b);
            else if (_settingsPage == 2) HandleSettingsPackageSources(b);
            else if (_settingsPage == 4) HandleStorage(b);
            else HandleSettingsAppearance(b);
        }

        void HandleSettingsGeneral(DS4Button b)
        {
            const int n = 6;
            if (b == DS4Button.SCE_PAD_BUTTON_UP) { _settingsFocus = (_settingsFocus + n - 1) % n; return; }
            if (b == DS4Button.SCE_PAD_BUTTON_DOWN) { _settingsFocus = (_settingsFocus + 1) % n; return; }
            if (_settingsFocus == 0 && (b == DS4Button.SCE_PAD_BUTTON_LEFT || b == DS4Button.SCE_PAD_BUTTON_RIGHT))
            { SetDownloadRangeCount(_cfg.DownloadLimitMBps + (b == DS4Button.SCE_PAD_BUTTON_LEFT ? -5 : 5)); return; }
            if (b != DS4Button.SCE_PAD_BUTTON_CROSS) return;
            if (_settingsFocus == 0) SetDownloadRangeCount(_cfg.DownloadLimitMBps >= 100 ? 0 : _cfg.DownloadLimitMBps + 5);
            else if (_settingsFocus == 1) { _cfg.DownloadStatsMode = (_cfg.DownloadStatsMode + 1) % 4; MarkSettingsDirty(); }
            else if (_settingsFocus == 2) { _cfg.NerdStats = !_cfg.NerdStats; MarkSettingsDirty(); }
            else if (_settingsFocus == 3) { _cfg.ShowFirmwareHints = !_cfg.ShowFirmwareHints; MarkSettingsDirty(); }
            else if (_settingsFocus == 4) { _uiOverlay = UiOverlay.ConfirmClearHistory; _overlayTitle = "Clear removable history?"; }
            else SaveSettingsAndClose();
        }

        void SetDownloadRangeCount(int value)
        {
            _cfg.DownloadLimitMBps = Math.Max(0, Math.Min(1000, value));
            MarkSettingsDirty();
            SetStatus(_cfg.DownloadLimitMBps == 0 ? "Download bandwidth: unlimited" : "Download limit: " + _cfg.DownloadLimitMBps + " MB/s");
        }

        void HandleSettingsUnlock(DS4Button b)
        {
            if (b == DS4Button.SCE_PAD_BUTTON_UP) { _settingsFocus = (_settingsFocus + 3) % 4; return; }
            if (b == DS4Button.SCE_PAD_BUTTON_DOWN) { _settingsFocus = (_settingsFocus + 1) % 4; return; }
            if (b == DS4Button.SCE_PAD_BUTTON_SQUARE) { StartPairSession(); _uiOverlay = UiOverlay.QrPair; return; }
            if (b != DS4Button.SCE_PAD_BUTTON_CROSS && b != DS4Button.SCE_PAD_BUTTON_LEFT && b != DS4Button.SCE_PAD_BUTTON_RIGHT) return;
            if (_settingsFocus == 3) { _settingsPage = 2; _settingsFocus = 0; RefreshSourceUi(); return; }
            string id = _settingsFocus == 0 ? UnlockProviders.RealDebridId : _settingsFocus == 1 ? UnlockProviders.TorBoxId : UnlockProviders.NoneId;
            string error;
            if (_cfg.TrySelectDownloadService(id, out error)) SetStatus(UnlockProviders.DisplayName(id) + " selected for new downloads");
            else {
                if (!UnlockProviders.IsConfigured(_cfg, id)) { StartPairSession(); _uiOverlay = UiOverlay.QrPair; }
                SetStatus(error);
            }
            Invalidated = true;
        }

        void SourcesChanged()
        {
            lock (_lock) { ++_libraryScanGen; _nextUpdateScan = 0; _libraryHasUpdate.Clear(); _libraryUpdateInfo.Clear(); }
            RefreshSourceUi(); Invalidated = true;
        }

        void HandleSettingsPackageSources(DS4Button b)
        {
            RefreshSourceUi();
            int n = 1 + _sourceUi.Count; // Install, then one row per installed source.
            if (b == DS4Button.SCE_PAD_BUTTON_UP) { _settingsFocus = (_settingsFocus + n - 1) % n; return; }
            if (b == DS4Button.SCE_PAD_BUTTON_DOWN) { _settingsFocus = (_settingsFocus + 1) % n; return; }
            if (SourceInstallActive(GetSourceInstallView().Stage) &&
                (b == DS4Button.SCE_PAD_BUTTON_CROSS || b == DS4Button.SCE_PAD_BUTTON_SQUARE))
            {
                SetStatus("Package Source install is still working");
                return;
            }
            if (b == DS4Button.SCE_PAD_BUTTON_SQUARE && _settingsFocus > 0)
            {
                SourceUiEntry remove = _sourceUi[_settingsFocus - 1];
                string error;
                if (_packageSources.Remove(remove.Id, out error))
                {
                    SetStatus("Removed Package Source " + remove.Name);
                    SourcesChanged();
                }
                else SetStatus("Remove failed: " + Clip(error, 52));
                return;
            }
            if (b != DS4Button.SCE_PAD_BUTTON_CROSS) return;
            if (_settingsFocus == 0) { StartPairSession(); _uiOverlay = UiOverlay.QrPair; return; }
            SourceUiEntry item = _sourceUi[_settingsFocus - 1];
            string toggleError;
            if (_packageSources.SetEnabled(item.Id, !item.Enabled, out toggleError))
            {
                SetStatus(item.Name + (!item.Enabled ? " enabled" : " disabled"));
                SourcesChanged();
            }
            else SetStatus("Source update failed: " + Clip(toggleError, 48));
        }

        void CaptureAppearanceDraft()
        {
            _appearanceAccent = _cfg.Accent; _appearanceAccentName = _cfg.AccentName;
            _appearanceBgMode = _cfg.BackgroundMode; _appearanceBgImage = _cfg.BackgroundImagePath;
            _appearanceCloud = _cfg.CoverCloud; _appearanceCloudDensity = _cfg.CoverCloudDensity;
            _appearanceStyle = _cfg.UiStyle; _appearanceReduceMotion = _cfg.ReduceMotion;
            _appearanceRecents = _cfg.SearchRecents; _appearanceContinue = _cfg.SearchContinue;
            _appearanceEta = _cfg.EtaFormat; _appearanceDirty = false;
        }

        void RevertAppearanceDraft()
        {
            _cfg.Accent = _appearanceAccent; _cfg.AccentName = _appearanceAccentName;
            _cfg.BackgroundMode = _appearanceBgMode; _cfg.BackgroundImagePath = _appearanceBgImage;
            _cfg.CoverCloud = _appearanceCloud; _cfg.CoverCloudDensity = _appearanceCloudDensity;
            _cfg.UiStyle = _appearanceStyle; _cfg.ReduceMotion = _appearanceReduceMotion;
            _cfg.SearchRecents = _appearanceRecents; _cfg.SearchContinue = _appearanceContinue;
            _cfg.EtaFormat = _appearanceEta; _appearanceDirty = false;
            User.NotifyToast("Appearance reverted");
        }

        void MarkAppearanceChanged()
        {
            _cfg.ValidateAppearance(false);
            _appearanceDirty = true;
            _settingsDirty = true;
            Invalidated = true;
        }

        void HandleSettingsAppearance(DS4Button b)
        {
            int n = ThemePalette.Presets.Count + 3;
            if (b == DS4Button.SCE_PAD_BUTTON_UP) { _settingsFocus = _settingsFocus == 0 ? n - 1 : _settingsFocus == n - 1 ? n - 2 : _settingsFocus >= 6 ? _settingsFocus - 4 : _settingsFocus >= 2 ? 1 : 0; return; }
            if (b == DS4Button.SCE_PAD_BUTTON_DOWN) { _settingsFocus = _settingsFocus == n - 1 ? 0 : _settingsFocus < 2 ? _settingsFocus + 1 : Math.Min(n - 1, _settingsFocus + 4); return; }
            if (b != DS4Button.SCE_PAD_BUTTON_CROSS && b != DS4Button.SCE_PAD_BUTTON_LEFT && b != DS4Button.SCE_PAD_BUTTON_RIGHT && b != DS4Button.SCE_PAD_BUTTON_TRIANGLE) return;
            if (_settingsFocus >= 2 && _settingsFocus < n - 1 && (b == DS4Button.SCE_PAD_BUTTON_LEFT || b == DS4Button.SCE_PAD_BUTTON_RIGHT)) { _settingsFocus = Math.Max(2, Math.Min(n - 2, _settingsFocus + (b == DS4Button.SCE_PAD_BUTTON_LEFT ? -1 : 1))); return; }
            string oldMode = _cfg.BackgroundMode, oldAccent = _cfg.Accent, oldName = _cfg.AccentName; bool oldMotion = _cfg.ReduceMotion;
            if (_settingsFocus == n - 1 || b == DS4Button.SCE_PAD_BUTTON_TRIANGLE) { _cfg.BackgroundMode = "solid"; _cfg.Accent = ThemePalette.DefaultAccentHex; _cfg.AccentName = ThemePalette.DefaultAccentName; _cfg.ReduceMotion = false; }
            else if (_settingsFocus == 0) { int direction = b == DS4Button.SCE_PAD_BUTTON_LEFT ? -1 : 1; _cfg.BackgroundMode = BackdropPattern.Modes[(BackdropPattern.Index(_cfg.BackgroundMode) + direction + BackdropPattern.Modes.Length) % BackdropPattern.Modes.Length]; }
            else if (_settingsFocus == 1) _cfg.ReduceMotion = !_cfg.ReduceMotion;
            else { var preset = ThemePalette.Presets[_settingsFocus - 2]; _cfg.Accent = preset.Hex; _cfg.AccentName = preset.Name; }
            if (_cfg.Save()) { CaptureAppearanceDraft(); SetStatus("Appearance saved"); }
            else { _cfg.BackgroundMode = oldMode; _cfg.Accent = oldAccent; _cfg.AccentName = oldName; _cfg.ReduceMotion = oldMotion; User.NotifyToast("Could not save appearance"); }
            Invalidated = true;
        }

        void OpenAppearanceText(bool accentHex)
        {
            StartPairSession(); _uiOverlay = UiOverlay.QrPair;
        }

        void RefreshSourceUi()
        {
            string error;
            _sourceUi = _packageSources.List(out error);
            _sourceUiError = error ?? "";
            var choices = new System.Text.StringBuilder("[");
            for (int i = 0; i < _sourceUi.Count; i++) {
                var source = _sourceUi[i]; if (i > 0) choices.Append(',');
                choices.Append("{\"id\":\"").Append(JsonLite.Escape(source.Id)).Append("\",\"name\":\"")
                    .Append(JsonLite.Escape(source.Name)).Append("\",\"version\":\"").Append(JsonLite.Escape(source.Version))
                    .Append("\",\"enabled\":").Append(source.Enabled ? "true" : "false").Append('}');
            }
            _pair.SourceChoicesJson = choices.Append(']').ToString();
            int max = Math.Max(0, _sourceUi.Count);
            if (_settingsPage == 2 && _settingsFocus > max) _settingsFocus = max;
        }

        void OpenSourceUrlKeyboard()
        {
            if (!_imeDisabled)
            {
                string value, error;
                if (NativeImeDialog.Show(_sourceUrlDraft, "Install Package Source",
                    "https://example.org/source.gssource", 256, true, out value, out error))
                {
                    _sourceUrlDraft = (value ?? "").Trim();
                    StartSourceInstall();
                    return;
                }
                _imeDisabled = true;
            }
            _softKbForProxy = false;
            _softKbForDeepbrid = false;
            _softKbForAllDebrid = false;
            _softKbForTorBox = false;
            _softKbForSourceUrl = true;
            _softKbOpen = true;
            _kbRow = _kbCol = 0;
            SetStatus("Package Source URL · OK/R2 installs");
        }

        void StartSourceInstall()
        {
            if (SourceInstallActive(GetSourceInstallView().Stage))
            {
                SetStatus("Package Source install is already running");
                return;
            }
            string text = (_sourceUrlDraft ?? "").Trim();
            Uri uri;
            if (!Uri.TryCreate(text, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                SetStatus("Enter an absolute HTTP/HTTPS source URL without user info");
                return;
            }
            _softKbOpen = false;
            _softKbForSourceUrl = false;
            BeginSourceInstall(uri);
            SetStatus("Connecting to Package Source...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string temp = null;
                bool tooLarge = false;
                try
                {
                    string staging = Path.Combine(AppSettings.DataDir, "sources", "staging");
                    Directory.CreateDirectory(staging);
                    temp = Path.Combine(staging, "url-" + DateTime.UtcNow.Ticks.ToString("x") + ".gssource");
                    int timeoutMs = SourceInstallTimeoutMs(uri);
                    NetHttp.DownloadSourceFile(uri.AbsoluteUri, temp,
                        (done, total) =>
                        {
                            if (done > 4L * 1024 * 1024 || total > 4L * 1024 * 1024) tooLarge = true;
                            UpdateSourceInstall(SourceInstallStage.Downloading, done, total,
                                total > 0 ? "Receiving source package" : "Receiving source package · size unknown");
                        },
                        () => tooLarge, timeoutMs);
                    if (tooLarge || !File.Exists(temp) || new FileInfo(temp).Length > 4L * 1024 * 1024)
                        throw new Exception("source package exceeds 4 MiB");
                    long sourceBytes = new FileInfo(temp).Length;
                    UpdateSourceInstall(SourceInstallStage.Validating, sourceBytes, sourceBytes,
                        "Checking package structure, manifest, and limits");
                    string installError;
                    UpdateSourceInstall(SourceInstallStage.Installing, sourceBytes, sourceBytes,
                        "Activating source and updating the registry");
                    if (!_packageSources.Install(temp, out installError))
                        throw new Exception(installError);
                    UpdateSourceInstall(SourceInstallStage.Complete, sourceBytes, sourceBytes,
                        "Installed and ready to use");
                    SetStatus("Package Source installed");
                    User.NotifyToast("Package Source installed");
                }
                catch (OperationCanceledException)
                {
                    string detail = tooLarge
                        ? "Package exceeds the 4 MiB safety limit"
                        : "Package download was canceled";
                    UpdateSourceInstall(SourceInstallStage.Failed, 0, 0, detail);
                    SetStatus(detail);
                    User.NotifyToast("Source install failed");
                }
                catch (Exception ex)
                {
                    string detail = FriendlySourceInstallError(ex, uri);
                    UpdateSourceInstall(SourceInstallStage.Failed, 0, 0, detail);
                    SetStatus("Source install failed: " + Clip(detail, 52));
                    User.NotifyToast("Source install failed");
                }
                finally
                {
                    if (!string.IsNullOrEmpty(temp)) try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                    Invalidated = true;
                }
            });
        }

        void BeginSourceInstall(Uri uri)
        {
            lock (_lock)
            {
                _sourceInstallStage = SourceInstallStage.Connecting;
                _sourceInstallDone = 0;
                _sourceInstallTotal = 0;
                _sourceInstallTarget = SourceInstallTarget(uri);
                _sourceInstallDetail = "Waiting for the host to respond";
                _sourceInstallShownAt = UiTick();
                _sourceInstallTerminalAt = 0;
            }
            Invalidated = true;
        }

        void UpdateSourceInstall(SourceInstallStage stage, long done, long total, string detail)
        {
            lock (_lock)
            {
                _sourceInstallStage = stage;
                _sourceInstallDone = Math.Max(0, done);
                _sourceInstallTotal = Math.Max(0, total);
                if (detail != null) _sourceInstallDetail = detail;
                if ((stage == SourceInstallStage.Complete || stage == SourceInstallStage.Failed) &&
                    _sourceInstallTerminalAt == 0)
                    _sourceInstallTerminalAt = UiTick();
            }
            Invalidated = true;
        }

        SourceInstallView GetSourceInstallView()
        {
            lock (_lock)
            {
                return new SourceInstallView
                {
                    Stage = _sourceInstallStage,
                    Done = _sourceInstallDone,
                    Total = _sourceInstallTotal,
                    Target = _sourceInstallTarget,
                    Detail = _sourceInstallDetail,
                    ShownAt = _sourceInstallShownAt,
                    TerminalAt = _sourceInstallTerminalAt
                };
            }
        }

        static bool SourceInstallActive(SourceInstallStage stage)
        {
            return stage == SourceInstallStage.Connecting ||
                   stage == SourceInstallStage.Downloading ||
                   stage == SourceInstallStage.Validating ||
                   stage == SourceInstallStage.Installing;
        }

        static int SourceInstallTimeoutMs(Uri uri)
        {
            // A LAN host should answer quickly. Public HTTPS hosts get enough time for
            // DNS, TLS, redirects, and slower firmware networking.
            return IsPrivateLanHost(uri) ? 15000 : 90000;
        }

        static bool IsPrivateLanHost(Uri uri)
        {
            if (uri == null) return false;
            IPAddress ip;
            if (!IPAddress.TryParse(uri.Host, out ip)) return false;
            byte[] bytes = ip.GetAddressBytes();
            if (bytes.Length != 4) return false;
            return bytes[0] == 10 || bytes[0] == 127 ||
                   (bytes[0] == 192 && bytes[1] == 168) ||
                   (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31);
        }

        static string SourceInstallTarget(Uri uri)
        {
            if (uri == null) return "Package Source";
            string port = uri.IsDefaultPort ? "" : ":" + uri.Port;
            return uri.Host + port + (string.IsNullOrEmpty(uri.AbsolutePath) ? "/" : uri.AbsolutePath);
        }

        static string FriendlySourceInstallError(Exception ex, Uri uri)
        {
            string message = ex == null ? "Unknown source install error" : (ex.Message ?? "Source install error");
            WebException web = ex as WebException;
            bool connectionFailure = web != null &&
                (web.Status == WebExceptionStatus.Timeout ||
                 web.Status == WebExceptionStatus.ConnectFailure ||
                 web.Status == WebExceptionStatus.NameResolutionFailure ||
                 web.Status == WebExceptionStatus.ProxyNameResolutionFailure);
            string lower = message.ToLowerInvariant();
            connectionFailure = connectionFailure || lower.Contains("timed out") || lower.Contains("timeout");
            if (connectionFailure && IsPrivateLanHost(uri))
            {
                string port = uri.IsDefaultPort ? (uri.Scheme == Uri.UriSchemeHttps ? "443" : "80") : uri.Port.ToString();
                return "No response from " + uri.Host + ":" + port +
                    " · check the same LAN and allow TCP " + port + " through the PC firewall";
            }
            return Clip(message, 120);
        }

        void OpenDeepbridKeyboard()
        {
            _softKbOpen = true;
            _softKbForProxy = false;
            _softKbForDeepbrid = true;
            _softKbForAllDebrid = false;
            _softKbForTorBox = false;
            _softKbForSourceUrl = false;
            _kbRow = 0;
            _kbCol = 0;
            SetStatus("Deepbrid API key · OK/R2 save");
        }

        void HandleSearch(DS4Button b)
        {
            int libraryCount = _libraryGames.Count;
            int continueCount = _cfg.SearchContinue ? _landingContinue.Count : 0;
            int pebbleCount = 0;
            int total = 1 + libraryCount + continueCount + pebbleCount;
            if (b == DS4Button.SCE_PAD_BUTTON_LEFT || b == DS4Button.SCE_PAD_BUTTON_UP)
            {
                _searchLandingFocus = (_searchLandingFocus + total - 1) % Math.Max(1, total);
                return;
            }
            if (b == DS4Button.SCE_PAD_BUTTON_RIGHT || b == DS4Button.SCE_PAD_BUTTON_DOWN)
            {
                _searchLandingFocus = (_searchLandingFocus + 1) % Math.Max(1, total);
                return;
            }
            if (b != DS4Button.SCE_PAD_BUTTON_CROSS) return;
            if (_searchLandingFocus == 0)
            {
                OpenSearchKeyboard();
                return;
            }
            if (_searchLandingFocus <= libraryCount)
            {
                _selected = _libraryGames[_searchLandingFocus - 1];
                bool focusUpdate;
                lock (_lock) focusUpdate = _libraryHasUpdate.Contains(_selected.TitleId ?? "");
                StartResolveJob(focusUpdate);
                return;
            }
            int continueIndex = _searchLandingFocus - 1 - libraryCount;
            if (continueIndex >= 0 && continueIndex < continueCount)
            {
                _tab = TopTab.Downloads;
                _screen = BrowseScreen.Search;
                SetStatus("Downloads");
                return;
            }
            if (pebbleCount > 0 && continueIndex == continueCount)
                _tab = TopTab.Downloads;
        }

        void OpenAllDebridKeyboard()
        {
            _softKbOpen = true;
            _softKbForProxy = false;
            _softKbForDeepbrid = false;
            _softKbForAllDebrid = true;
            _softKbForTorBox = false;
            _softKbForSourceUrl = false;
            _kbRow = 0;
            _kbCol = 0;
            SetStatus("AllDebrid API key - OK/R2 save");
        }

        void OpenTorBoxKeyboard()
        {
            _softKbOpen = true;
            _softKbForProxy = false;
            _softKbForDeepbrid = false;
            _softKbForAllDebrid = false;
            _softKbForTorBox = true;
            _softKbForSourceUrl = false;
            _kbRow = 0;
            _kbCol = 0;
            SetStatus("TorBox API token - OK/R2 save");
        }

        static bool _imeDisabled;

        void OpenSearchKeyboard()
        {
            // Search owns a stable controller-first grid; the session query remains intact.
            _softKbForProxy = false;
            _softKbForDeepbrid = false;
            _softKbForAllDebrid = false;
            _softKbForTorBox = false;
            _softKbForSourceUrl = false;
            _softKbOpen = true;
            _kbRow = 0;
            _kbCol = 0;
            _kbSuggestionFocus = -1;
            RefreshKeyboardSuggestions();
            _cloudFrozenAt = UiTick();
            SetStatus("On-screen keyboard");
        }

        void OpenProxyKeyboard()
        {
            _softKbForDeepbrid = false;
            _softKbForAllDebrid = false;
            _softKbForTorBox = false;
            _softKbForSourceUrl = false;
            if (!_imeDisabled)
            {
                string value;
                string error;
                if (NativeImeDialog.Show(_proxyDraft, "Emergency LAN proxy", "http://192.168.0.2:8788",
                    96, true, out value, out error))
                {
                    _proxyDraft = (value ?? "").Trim();
                    SetStatus("Proxy URL updated - choose Save and close");
                    return;
                }
                _imeDisabled = true;
            }
            _softKbForProxy = true;
            _softKbOpen = true;
            _kbRow = 0;
            _kbCol = 0;
            SetStatus("On-screen keyboard for proxy URL");
        }

        void HandleSoftKeyboard(DS4Button b)
        {
            switch (b)
            {
                case DS4Button.SCE_PAD_BUTTON_UP:
                    if (!_softKbForProxy && !_softKbForDeepbrid && !_softKbForAllDebrid &&
                        !_softKbForTorBox && !_softKbForSourceUrl &&
                        _kbRow == 0 && _kbSuggestionCount > 0)
                    {
                        _kbSuggestionFocus = _kbSuggestionFocus < 0 ? 0 :
                            (_kbSuggestionFocus + _kbSuggestionCount - 1) % _kbSuggestionCount;
                    }
                    else if (_kbSuggestionFocus >= 0)
                        _kbSuggestionFocus = (_kbSuggestionFocus + _kbSuggestionCount - 1) % _kbSuggestionCount;
                    else if (_kbRow > 0) { _kbRow--; ClampKb(); }
                    break;
                case DS4Button.SCE_PAD_BUTTON_DOWN:
                    if (_kbSuggestionFocus >= 0) { _kbSuggestionFocus = -1; _kbRow = 0; ClampKb(); }
                    else if (_kbRow < KbRows.Length - 1) { _kbRow++; ClampKb(); }
                    break;
                case DS4Button.SCE_PAD_BUTTON_LEFT:
                    if (_kbSuggestionFocus >= 0)
                        _kbSuggestionFocus = (_kbSuggestionFocus + _kbSuggestionCount - 1) % _kbSuggestionCount;
                    else _kbCol = (_kbCol + KeysInRow(_kbRow) - 1) % KeysInRow(_kbRow);
                    break;
                case DS4Button.SCE_PAD_BUTTON_RIGHT:
                    if (_kbSuggestionFocus >= 0)
                        _kbSuggestionFocus = (_kbSuggestionFocus + 1) % _kbSuggestionCount;
                    else _kbCol = (_kbCol + 1) % KeysInRow(_kbRow);
                    break;
                case DS4Button.SCE_PAD_BUTTON_CROSS:
                    if (_kbSuggestionFocus >= 0 && _kbSuggestionFocus < _kbSuggestionCount)
                    {
                        GameHit suggestion = _kbSuggestions[_kbSuggestionFocus];
                        _query = suggestion == null ? _query :
                            (!string.IsNullOrEmpty(suggestion.TitleId) ? suggestion.TitleId : suggestion.Name);
                        _kbSuggestionFocus = -1;
                        RefreshKeyboardSuggestions();
                    }
                    else
                    {
                        MarkSoftKeyPressed(_kbRow, _kbCol);
                        SoftKbPress();
                    }
                    break;
                case DS4Button.SCE_PAD_BUTTON_SQUARE:
                    MarkSoftKeyPressed(4, 1);
                    SoftKbBackspace(); break;
                case DS4Button.SCE_PAD_BUTTON_L2:
                    MarkSoftKeyPressed(4, 3);
                    _kbLower = !_kbLower;
                    SetStatus(_kbLower ? "Keyboard: lowercase" : "Keyboard: uppercase");
                    break;
                case DS4Button.SCE_PAD_BUTTON_TRIANGLE:
                    MarkSoftKeyPressed(4, 0);
                    SoftKbAppend(' ');
                    break;
                case DS4Button.SCE_PAD_BUTTON_R2:
                    // R2 = quick search / submit (like system keyboard Done)
                    MarkSoftKeyPressed(4, 4);
                    SoftKbSubmit(); break;
                case DS4Button.SCE_PAD_BUTTON_CIRCLE:
                    _softKbOpen = false;
                    _kbSuggestionFocus = -1;
                    SetStatus("Keyboard closed");
                    break;
            }
        }

        void MarkSoftKeyPressed(int row, int col)
        {
            _kbPressedRow = row;
            _kbPressedCol = col;
            _kbPressedAt = UiTick();
        }

        int KeysInRow(int r) { return r == 4 ? 5 : KbRows[r].Length; }
        void ClampKb()
        {
            int n = KeysInRow(_kbRow);
            if (_kbCol >= n) _kbCol = n - 1;
            if (_kbCol < 0) _kbCol = 0;
        }

        void SoftKbBackspace()
        {
            _typingPulseAt = UiTick();
            if (_softKbForSourceUrl)
            {
                if (_sourceUrlDraft.Length > 0)
                    _sourceUrlDraft = _sourceUrlDraft.Substring(0, _sourceUrlDraft.Length - 1);
            }
            else if (_softKbForDeepbrid)
            {
                if (_deepbridDraft.Length > 0)
                    _deepbridDraft = _deepbridDraft.Substring(0, _deepbridDraft.Length - 1);
            }
            else if (_softKbForAllDebrid)
            {
                if (_allDebridDraft.Length > 0)
                    _allDebridDraft = _allDebridDraft.Substring(0, _allDebridDraft.Length - 1);
            }
            else if (_softKbForTorBox)
            {
                if (_torBoxDraft.Length > 0)
                    _torBoxDraft = _torBoxDraft.Substring(0, _torBoxDraft.Length - 1);
            }
            else if (_softKbForProxy)
            {
                if (_proxyDraft.Length > 0)
                    _proxyDraft = _proxyDraft.Substring(0, _proxyDraft.Length - 1);
            }
            else if (_query.Length > 0)
                _query = _query.Substring(0, _query.Length - 1);
            if (!_softKbForSourceUrl && !_softKbForDeepbrid && !_softKbForAllDebrid &&
                !_softKbForTorBox && !_softKbForProxy)
                RefreshKeyboardSuggestions();
        }

        void SoftKbSubmit()
        {
            _softKbOpen = false;
            if (_softKbForSourceUrl)
            {
                _softKbForSourceUrl = false;
                StartSourceInstall();
                return;
            }
            if (_softKbForDeepbrid)
            {
                _cfg.DeepbridApiKey = (_deepbridDraft ?? "").Trim();
                _cfg.UnlockProviderId = UnlockProviders.DeepbridId;
                _cfg.UseUnlockProvider = true;
                _cfg.UseRealDebrid = false;
                _cfg.Save();
                _softKbForDeepbrid = false;
                SetStatus(_cfg.HasDeepbrid ? "Deepbrid key saved" : "Deepbrid key empty");
                return;
            }
            if (_softKbForAllDebrid)
            {
                _cfg.AllDebridApiKey = (_allDebridDraft ?? "").Trim();
                _cfg.UnlockProviderId = UnlockProviders.AllDebridId;
                _cfg.UseUnlockProvider = true;
                _cfg.UseRealDebrid = false;
                _cfg.Save();
                _softKbForAllDebrid = false;
                SetStatus(_cfg.HasAllDebrid ? "AllDebrid key saved" : "AllDebrid key empty");
                return;
            }
            if (_softKbForTorBox)
            {
                _cfg.TorBoxApiKey = (_torBoxDraft ?? "").Trim();
                _cfg.UnlockProviderId = UnlockProviders.TorBoxId;
                _cfg.UseUnlockProvider = true;
                _cfg.UseRealDebrid = false;
                _cfg.Save();
                _softKbForTorBox = false;
                SetStatus(_cfg.HasTorBox ? "TorBox token saved" : "TorBox token empty");
                return;
            }
            if (_softKbForProxy)
            {
                _cfg.ProxyBaseUrl = (_proxyDraft ?? "").Trim();
                bool saved = _cfg.Save();
                _softKbForProxy = false;
                SetStatus(saved ? "Proxy URL saved" : "Proxy URL save failed");
                return;
            }
            _query = (_query ?? "").Trim();
            if (_query.Length >= 1) StartSearchJob();
            else
            {
                _screen = BrowseScreen.Search;
                SetStatus("Search");
            }
        }

        void SoftKbPress()
        {
            if (_kbRow < 4)
            {
                char ch = KbRows[_kbRow][_kbCol];
                if (_kbLower && char.IsLetter(ch))
                    ch = char.ToLowerInvariant(ch);
                SoftKbAppend(ch);
                return;
            }
            // special row: SPACE BACK CLR aA OK
            switch (_kbCol)
            {
                case 0:
                    SoftKbAppend(' ');
                    break;
                case 1: SoftKbBackspace(); break;
                case 2:
                    _typingPulseAt = UiTick();
                    if (_softKbForSourceUrl) _sourceUrlDraft = "";
                    else if (_softKbForDeepbrid) _deepbridDraft = "";
                    else if (_softKbForAllDebrid) _allDebridDraft = "";
                    else if (_softKbForTorBox) _torBoxDraft = "";
                    else if (_softKbForProxy) _proxyDraft = "";
                    else _query = "";
                    RefreshKeyboardSuggestions();
                    break;
                case 3:
                    _kbLower = !_kbLower;
                    break;
                case 4:
                    SoftKbSubmit();
                    break;
            }
        }

        void HandleResults(DS4Button b)
        {
            if (b == DS4Button.SCE_PAD_BUTTON_CIRCLE)
            {
                // Invalidate the pending result so it cannot reopen a canceled search.
                lock (_lock)
                {
                    if (_browseBusy)
                    {
                        if (_busyKind == BusyKind.Searching) _searchGeneration++;
                        else if (_busyKind == BusyKind.Resolving) _resolveGeneration++;
                        _browseBusy = false; _busyKind = BusyKind.None;
                    }
                }
                _screen = BrowseScreen.Search; _searchError = null; return;
            }
            if (b == DS4Button.SCE_PAD_BUTTON_LEFT || b == DS4Button.SCE_PAD_BUTTON_RIGHT)
            {
                _regionFilter = (_regionFilter + (b == DS4Button.SCE_PAD_BUTTON_LEFT ? 4 : 1)) % RegionFilters.Length;
                FilteredResults(); return;
            }
            var hits = FilteredResults();
            if (hits.Count == 0)
            {
                if (b == DS4Button.SCE_PAD_BUTTON_CROSS)
                {
                    if (!string.IsNullOrEmpty(_searchError) &&
                        _searchError.IndexOf("fail", StringComparison.OrdinalIgnoreCase) >= 0)
                        StartSearchJob();
                    else
                        _screen = BrowseScreen.Search;
                    return;
                }
                if (b == DS4Button.SCE_PAD_BUTTON_TRIANGLE || b == DS4Button.SCE_PAD_BUTTON_CIRCLE)
                {
                    _screen = BrowseScreen.Search;
                    _searchError = null;
                }
                return;
            }
            _focus = Math.Max(0, Math.Min(_focus, hits.Count - 1));
            switch (b)
            {
                case DS4Button.SCE_PAD_BUTTON_UP:
                    _focus = (_focus + hits.Count - 1) % hits.Count; break;
                case DS4Button.SCE_PAD_BUTTON_DOWN:
                    _focus = (_focus + 1) % hits.Count; break;
                case DS4Button.SCE_PAD_BUTTON_CROSS:
                    _selected = hits[_focus];
                    StartResolveJob();
                    break;
                case DS4Button.SCE_PAD_BUTTON_SQUARE:
                    StartRecommendedResolve(hits[_focus]);
                    break;
                case DS4Button.SCE_PAD_BUTTON_CIRCLE:
                    lock (_lock)
                    {
                        if (_browseBusy && _busyKind == BusyKind.Resolving)
                        {
                            _resolveGeneration++;
                            _browseBusy = false;
                            _busyKind = BusyKind.None;
                        }
                    }
                    _screen = BrowseScreen.Search; break;
            }
        }

        void HandleDetail(DS4Button b)
        {
            bool busy;
            lock (_lock) busy = _browseBusy;
            if (busy)
            {
                if (b == DS4Button.SCE_PAD_BUTTON_CIRCLE) LeaveDetail();
                return;
            }
            if (_links.Count == 0)
            {
                if (b == DS4Button.SCE_PAD_BUTTON_CROSS ||
                    b == DS4Button.SCE_PAD_BUTTON_SQUARE)
                {
                    _resolveError = null;
                    StartResolveJob();
                    return;
                }
                if (b == DS4Button.SCE_PAD_BUTTON_CIRCLE) LeaveDetail();
                return;
            }
            if (b == DS4Button.SCE_PAD_BUTTON_LEFT || b == DS4Button.SCE_PAD_BUTTON_RIGHT || b == DS4Button.SCE_PAD_BUTTON_L2 || b == DS4Button.SCE_PAD_BUTTON_R2)
            {
                if (b == DS4Button.SCE_PAD_BUTTON_LEFT) _packageFilter = (_packageFilter + 4) % 5;
                if (b == DS4Button.SCE_PAD_BUTTON_RIGHT) _packageFilter = (_packageFilter + 1) % 5;
                if (b == DS4Button.SCE_PAD_BUTTON_L2) _hostFilter = (_hostFilter + 1) % (_detailHosts.Count + 1);
                if (b == DS4Button.SCE_PAD_BUTTON_R2) _latestUpdateOnly = !_latestUpdateOnly;
                _detailFocus = _detailScroll = 0; RebuildDetailRows(); return;
            }
            switch (b)
            {
                case DS4Button.SCE_PAD_BUTTON_UP:
                    if (_detailRows.Count == 0) break;
                    _detailFocus = (_detailFocus + _detailRows.Count - 1) % _detailRows.Count;
                    break;
                case DS4Button.SCE_PAD_BUTTON_DOWN:
                    if (_detailRows.Count == 0) break;
                    _detailFocus = (_detailFocus + 1) % _detailRows.Count;
                    break;
                case DS4Button.SCE_PAD_BUTTON_CROSS:
                    if (_detailFocus >= 0 && _detailFocus < _detailRows.Count)
                        QueueDownload(_links[_detailRows[_detailFocus]]);
                    break;
                case DS4Button.SCE_PAD_BUTTON_SQUARE:
                    QueueRecommendedResolved(_selected, true);
                    break;
                case DS4Button.SCE_PAD_BUTTON_TRIANGLE:
                    if (_detailFocus >= 0 && _detailFocus < _detailRows.Count)
                    {
                        int linkIndex = _detailRows[_detailFocus];
                        if (linkIndex < _linkPresentation.Count)
                        {
                            string group = DetailGroupKey(_linkPresentation[linkIndex]);
                            if (!_expandedPackageGroups.Add(group)) _expandedPackageGroups.Remove(group);
                            RebuildDetailRows();
                            if (_detailRows.Count == 0) _detailFocus = 0;
                            else if (_detailFocus >= _detailRows.Count) _detailFocus = _detailRows.Count - 1;
                        }
                    }
                    break;
                case DS4Button.SCE_PAD_BUTTON_CIRCLE:
                    LeaveDetail(); break;
            }
        }

        void LeaveDetail()
        {
            lock (_lock) { _resolveGeneration++; _browseBusy = false; _busyKind = BusyKind.None; }
            _resolveError = null;
            ResetPackageFilters();
            _detailRows.Clear();
            _detailFocus = _detailScroll = 0;
            _screen = BrowseScreen.Results;
            _tab = TopTab.Search;
        }

        void CycleTab(int delta)
        {
            int n = 2;
            int i = ((int)_tab + delta) % n;
            if (i < 0) i += n;
            _tab = (TopTab)i;
            if (_screen == BrowseScreen.Detail && _tab != TopTab.Search)
                _screen = BrowseScreen.Search;
            SetStatus(_tab == TopTab.Search ? "Search" : "Downloads");
        }

        void HandleDownloads(DS4Button b)
        {
            if (_fileDetailId == null && _downloadFilesTitle != null && b == DS4Button.SCE_PAD_BUTTON_CIRCLE) { _downloadFilesTitle = null; _dlFocus = _dlScroll = 0; _queueModelReady = false; return; }
            if (!string.IsNullOrEmpty(_fileDetailId))
            {
                if (b == DS4Button.SCE_PAD_BUTTON_CIRCLE || b == DS4Button.SCE_PAD_BUTTON_R2) _fileDetailId = null;
                else if (b == DS4Button.SCE_PAD_BUTTON_UP) _fileScroll = Math.Max(0, _fileScroll - 1);
                else if (b == DS4Button.SCE_PAD_BUTTON_DOWN) _fileScroll++;
                return;
            }
            if (b == DS4Button.SCE_PAD_BUTTON_L2) { _queueFilter = (_queueFilter + 1) % 3; _dlFocus = _dlScroll = 0; }
            RefreshQueueModel(true);
            var rows = _queueRows;
            _queueModelReady = false;
            if (rows.Count == 0)
            {
                if (b == DS4Button.SCE_PAD_BUTTON_CIRCLE) { _tab = TopTab.Search; SetStatus("Search"); }
                else if (b == DS4Button.SCE_PAD_BUTTON_SQUARE)
                {
                    int skipped;
                    string err;
                    int n = _dlMgr.ClearTerminal(out skipped, out err);
                    SetStatus(n > 0 ? ("Cleared " + n + " finished") : "Nothing to clear");
                }
                return;
            }
            ClampDownloadFocus(rows);
            DownloadTreeRow current = rows[_dlFocus];
            string titleId = current.Group.TitleId ?? "";
            if (b == DS4Button.SCE_PAD_BUTTON_R2)
            {
                if (_downloadFilesTitle == null) { _downloadFilesTitle = current.Group.TitleId; _dlFocus = _dlScroll = 0; _queueModelReady = false; }
                else OpenFileDetails(current.Item);
                return;
            }

            switch (b)
            {
                case DS4Button.SCE_PAD_BUTTON_LEFT:
                    if (!current.IsRoot)
                        _dlFocus = FindDownloadRoot(rows, titleId);
                    else if (current.HasChildren)
                        _collapsedDownloadGroups.Add(titleId);
                    return;
                case DS4Button.SCE_PAD_BUTTON_RIGHT:
                    if (current.IsRoot && current.HasChildren)
                    {
                        if (_collapsedDownloadGroups.Remove(titleId)) return;
                        if (_dlFocus + 1 < rows.Count && !rows[_dlFocus + 1].IsRoot)
                            _dlFocus++;
                    }
                    return;
                case DS4Button.SCE_PAD_BUTTON_UP:
                    _dlFocus = (_dlFocus + rows.Count - 1) % rows.Count;
                    return;
                case DS4Button.SCE_PAD_BUTTON_DOWN:
                    _dlFocus = (_dlFocus + 1) % rows.Count;
                    return;
                case DS4Button.SCE_PAD_BUTTON_CROSS:
                    if (current.Item != null)
                    {
                        if (current.Item.State == DlState.Completed) { _overlayDownloadId = current.Item.Id; _uiOverlay = UiOverlay.Install; }
                        else ActOnDownload(current.Item);
                    }
                    else if (current.HasChildren)
                    {
                        if (_collapsedDownloadGroups.Remove(titleId)) return;
                        if (_dlFocus + 1 < rows.Count && !rows[_dlFocus + 1].IsRoot)
                            _dlFocus++;
                    }
                    break;
                case DS4Button.SCE_PAD_BUTTON_TRIANGLE:
                    var cur = current.Item;
                    if (cur == null)
                    {
                        int skipped;
                        string cerr;
                        int n = _dlMgr.ClearTerminal(out skipped, out cerr);
                        SetStatus(n > 0
                            ? ("Cleared " + n + (skipped > 0 ? ("; skipped " + skipped) : ""))
                            : "Nothing finished to clear");
                        return;
                    }
                    if ((cur.State == DlState.Completed || cur.State == DlState.Installed) && IsBasePackage(cur))
                        StartInstall(cur, true);
                    else
                        SetStatus("TRIANGLE on game row = clear finished; on PKG = force reinstall");
                    break;
                case DS4Button.SCE_PAD_BUTTON_SQUARE:
                    cur = current.Item;
                    if (cur == null) return;
                    if (cur.State == DlState.Installing)
                    {
                        SetStatus("Install is already running");
                        return;
                    }
                    _overlayDownloadId = cur.Id;
                    _overlayTitle = "Remove " + PackageTitle(cur.Kind) + "?";
                    _uiOverlay = (cur.State == DlState.Downloading || cur.State == DlState.Resolving || cur.State == DlState.Finalizing)
                        ? UiOverlay.ConfirmCancel : UiOverlay.ConfirmRemove;
                    break;
                case DS4Button.SCE_PAD_BUTTON_CIRCLE:
                    _tab = TopTab.Search; SetStatus("Search"); break;
            }
        }

        DlItem OverlayDownload()
        {
            foreach (DlItem item in _dlMgr.Snapshot())
                if (string.Equals(item.Id, _overlayDownloadId, StringComparison.Ordinal)) return item;
            return null;
        }

        void HandleUiOverlay(DS4Button b)
        {
            if (_uiOverlay == UiOverlay.QrPair)
            {
                if (b == DS4Button.SCE_PAD_BUTTON_CIRCLE ||
                    (b == DS4Button.SCE_PAD_BUTTON_CROSS && _pairUiStage == PairUiStage.Complete))
                    _uiOverlay = UiOverlay.None;
                return;
            }
            if (_uiOverlay == UiOverlay.ConfirmSaveSettings)
            {
                if (b == DS4Button.SCE_PAD_BUTTON_UP || b == DS4Button.SCE_PAD_BUTTON_DOWN)
                {
                    _overlayFocus = _overlayFocus == 0 ? 1 : 0;
                    return;
                }
                if (b == DS4Button.SCE_PAD_BUTTON_CIRCLE)
                {
                    _uiOverlay = UiOverlay.None;
                    DiscardSettingsAndClose();
                    return;
                }
                if (b != DS4Button.SCE_PAD_BUTTON_CROSS) return;
                _uiOverlay = UiOverlay.None;
                if (_overlayFocus == 0) SaveSettingsAndClose();
                else DiscardSettingsAndClose();
                return;
            }
            if (b == DS4Button.SCE_PAD_BUTTON_CIRCLE) { _uiOverlay = UiOverlay.None; return; }
            if (_uiOverlay == UiOverlay.DownloadActions)
            {
                const int n = 4;
                if (b == DS4Button.SCE_PAD_BUTTON_UP) { _overlayFocus = (_overlayFocus + n - 1) % n; return; }
                if (b == DS4Button.SCE_PAD_BUTTON_DOWN) { _overlayFocus = (_overlayFocus + 1) % n; return; }
                if (b != DS4Button.SCE_PAD_BUTTON_CROSS) return;
                DlItem item = OverlayDownload();
                if (item == null) { _uiOverlay = UiOverlay.None; return; }
                if (_overlayFocus == 0)
                {
                    ActOnDownload(item); _uiOverlay = UiOverlay.None;
                    User.NotifyToast(item.State == DlState.Paused ? "Resume requested" : "Action requested");
                }
                else if (_overlayFocus == 1)
                {
                    string error;
                    User.NotifyToast(_dlMgr.MoveUp(item.Id, out error) ? "Moved up" : Clip(error, 42));
                    _uiOverlay = UiOverlay.None;
                }
                else if (_overlayFocus == 2)
                {
                    _uiOverlay = (item.Background || item.State == DlState.Queued || item.State == DlState.Installing ||
                        item.State == DlState.Downloading || item.State == DlState.Resolving || item.State == DlState.Finalizing)
                        ? UiOverlay.ConfirmCancel : UiOverlay.ConfirmRemove;
                    _overlayTitle = "Remove " + PackageTitle(item.Kind) + "?";
                }
                else _uiOverlay = UiOverlay.None;
                return;
            }
            if (b != DS4Button.SCE_PAD_BUTTON_CROSS) return;
            if (_uiOverlay == UiOverlay.Install)
            {
                DlItem item = OverlayDownload(); _uiOverlay = UiOverlay.None;
                if (item != null) StartInstall(item, false);
            }
            else if (_uiOverlay == UiOverlay.ConfirmRemove || _uiOverlay == UiOverlay.ConfirmCancel)
            {
                DlItem item = OverlayDownload(); string error = null; bool ok = false;
                if (item != null) ok = _uiOverlay == UiOverlay.ConfirmCancel
                    ? _dlMgr.Cancel(item.Id, out error) : _dlMgr.Remove(item.Id, out error);
                _uiOverlay = UiOverlay.None;
                User.NotifyToast(ok ? "Removed " + (item == null ? "package" : item.TitleId) : Clip(error ?? "Remove failed", 48));
            }
            else if (_uiOverlay == UiOverlay.ConfirmClearToken)
            {
                if (string.Equals(_cfg.UnlockProviderId, UnlockProviders.DeepbridId, StringComparison.OrdinalIgnoreCase))
                { _cfg.DeepbridApiKey = ""; _deepbridDraft = ""; }
                else if (string.Equals(_cfg.UnlockProviderId, UnlockProviders.AllDebridId, StringComparison.OrdinalIgnoreCase))
                { _cfg.AllDebridApiKey = ""; _allDebridDraft = ""; }
                else if (string.Equals(_cfg.UnlockProviderId, UnlockProviders.TorBoxId, StringComparison.OrdinalIgnoreCase))
                { _cfg.TorBoxApiKey = ""; _torBoxDraft = ""; }
                else _cfg.RealDebridToken = "";
                _cfg.Save(); _pairUiStage = PairUiStage.Idle; _pairUiDetail = "";
                _uiOverlay = UiOverlay.None; User.NotifyToast("Token cleared");
            }
            else if (_uiOverlay == UiOverlay.ConfirmClearHistory)
            {
                int skipped; string error; int count = _dlMgr.ClearTerminal(out skipped, out error);
                _uiOverlay = UiOverlay.None; User.NotifyToast(count > 0 ? "History cleared" : "Nothing to clear");
            }
        }

        void DrawUiOverlay(IntPtr r)
        {
            if (_uiOverlay == UiOverlay.QrPair) { DrawCharcoalPair(r); return; }
            var panel = new SDL_Rect { x = 570, y = 290, w = 780, h = _uiOverlay == UiOverlay.DownloadActions ? 480 :
                (_uiOverlay == UiOverlay.ConfirmSaveSettings ? 360 : 310) };
            Fill(r, panel.x + 8, panel.y + 10, panel.w, panel.h, Shadow); Fill(r, panel.x, panel.y, panel.w, panel.h, Panel);
            StrokeRect(r, panel, Border, 2); Fill(r, panel.x, panel.y + 14, 6, panel.h - 28, Accent);
            DlItem item = OverlayDownload();
            string title = _uiOverlay == UiOverlay.Install ? "Install package?" :
                (_uiOverlay == UiOverlay.DownloadActions ? (item == null ? "Package actions" : PackageTitle(item.Kind)) :
                (_uiOverlay == UiOverlay.ConfirmSaveSettings ? "Save settings?" : _overlayTitle));
            TextFit(r, panel.x + 36, panel.y + 30, 30, panel.w - 72, title, White);
            if (_uiOverlay == UiOverlay.DownloadActions)
            {
                string primary = item != null && item.State == DlState.Paused ? "Resume" :
                    (item != null && (item.State == DlState.Failed || item.State == DlState.Canceled) ? "Retry" : "Pause");
                string[] actions = { primary, "Move up", "Remove", "Cancel" };
                for (int i = 0; i < actions.Length; i++)
                    DrawSettingsRow(r, panel.x + 36, panel.y + 92 + i * 78, panel.w - 72, 66, _overlayFocus == i ? _settingsFocus : -2,
                        actions[i], "", i == 2 ? "REMOVE" : "");
                // DrawSettingsRow keys from settings focus; add the actual action focus explicitly.
                int fy = panel.y + 92 + _overlayFocus * 78;
                StrokeRect(r, new SDL_Rect { x = panel.x + 36, y = fy, w = panel.w - 72, h = 66 }, Accent, 3);
            }
            else if (_uiOverlay == UiOverlay.ConfirmSaveSettings)
            {
                TextFit(r, panel.x + 36, panel.y + 92, 20, panel.w - 72,
                    "Unsaved changes will be lost if you skip save.", Muted);
                DrawSettingsRow(r, panel.x + 36, panel.y + 150, panel.w - 72, 66, -2,
                    "Save changes", "Write settings.ini", "SAVE");
                DrawSettingsRow(r, panel.x + 36, panel.y + 226, panel.w - 72, 66, -2,
                    "Don't save", "Discard and close", "SKIP");
                int fy = panel.y + (_overlayFocus == 0 ? 150 : 226);
                StrokeRect(r, new SDL_Rect { x = panel.x + 36, y = fy, w = panel.w - 72, h = 66 }, Accent, 3);
            }
            else
            {
                string consequence = _uiOverlay == UiOverlay.Install ?
                    ((item == null ? "PKG" : PackageTitle(item.Kind)) + " · install from local storage") :
                    (_uiOverlay == UiOverlay.ConfirmClearToken ? "Downloads will use direct links until paired again." :
                    (_uiOverlay == UiOverlay.ConfirmClearHistory ? "Finished and removable rows will be cleared." : "This action cannot be undone."));
                TextFit(r, panel.x + 36, panel.y + 100, 20, panel.w - 72, consequence, Muted);
                TextPx(r, panel.x + 36, panel.y + panel.h - 68, 19, "× Confirm     ○ Cancel", White);
            }
        }

        void ActOnDownload(DlItem item)
        {
            if (item.State == DlState.Installed)
            {
                SetStatus("Already installed — SQUARE remove row, then queue again to re-download");
                return;
            }
            if (item.State == DlState.Submitted)
            {
                SetStatus("Sent to PS4 — verification pending · PKG kept");
                return;
            }
            if (item.Background && item.State == DlState.Installing)
            {
                SetStatus("PS4 is still installing in background");
                return;
            }
            if (item.State == DlState.Finalizing)
            {
                SetStatus(item.StatusText ?? "Finalizing package — please wait");
                return;
            }
            if (item.State == DlState.Completed)
            {
                string miss;
                if (!_dlMgr.EnsureLocalPackage(item.Id, out miss))
                {
                    SetStatus(miss);
                    return;
                }
                StartInstall(item, false);
            }
            else if (item.State == DlState.Failed &&
                     (item.Error ?? "").IndexOf("missing", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _dlMgr.TogglePause(item.Id); // re-queue
                SetStatus("Re-queued missing PKG");
            }
            else
                _dlMgr.TogglePause(item.Id);
        }

        void ClampDownloadFocus(List<DownloadTreeRow> rows)
        {
            if (_dlFocus >= rows.Count) _dlFocus = rows.Count - 1;
            if (_dlFocus < 0) _dlFocus = 0;
        }

        static List<DownloadGroup> BuildDownloadGroups(List<DlItem> items)
        {
            var groups = new List<DownloadGroup>();
            var byTitle = new Dictionary<string, DownloadGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                string key = item.TitleId ?? "";
                DownloadGroup group;
                if (!byTitle.TryGetValue(key, out group))
                {
                    group = new DownloadGroup
                    {
                        TitleId = key,
                        Name = item.Name ?? key,
                        ImageUrl = item.ImageUrl ?? ""
                    };
                    byTitle[key] = group;
                    groups.Add(group);
                }
                if (string.IsNullOrEmpty(group.ImageUrl) && !string.IsNullOrEmpty(item.ImageUrl))
                    group.ImageUrl = item.ImageUrl;
                group.Items.Add(item);
            }
            foreach (var group in groups)
                group.Items.Sort((a, b) => KindOrder(a.Kind).CompareTo(KindOrder(b.Kind)));
            return groups;
        }

        List<DownloadTreeRow> BuildDownloadRows(List<DownloadGroup> groups)
        {
            var rows = new List<DownloadTreeRow>();
            foreach (var group in groups)
            {
                if (_downloadFilesTitle != null)
                {
                    if (group.TitleId != _downloadFilesTitle) continue;
                    foreach (var item in group.Items) rows.Add(new DownloadTreeRow { Group = group, Item = item });
                }
                else if (QueueGroupMatches(group))
                    rows.Add(new DownloadTreeRow { Group = group, Item = PrimaryTransfer(group), IsRoot = true, ChildCount = group.Items.Count });
            }
            return rows;
        }

        static int FindDownloadRoot(List<DownloadTreeRow> rows, string titleId)
        {
            for (int i = 0; i < rows.Count; i++)
                if (rows[i].IsRoot && string.Equals(rows[i].Group.TitleId, titleId, StringComparison.OrdinalIgnoreCase))
                    return i;
            return 0;
        }

        static int KindOrder(string kind)
        {
            if (string.Equals(kind, "game", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(kind, "update", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(kind, "dlc", StringComparison.OrdinalIgnoreCase)) return 2;
            return 3;
        }

        #endregion

        #region Jobs

        bool ApplyPairedWallpaper(byte[] photo)
        {
            return false;
        }

        void StartSearchJob()
        {
            string q = (_query ?? "").Trim();
            if (q.Length < 1) { SetStatus("Search"); return; }
            RememberQuery(q);
            string sourceStamp = _packageSources.CatalogFingerprint();
            List<GameHit> cached;
            if (QueryCache.TrySearch(q, out cached, sourceStamp) && cached != null && cached.Count > 0)
            {
                _covers.BumpGeneration();
                _results = cached;
                _focus = 0;
                _listScroll = 0;
                _suggestionPool = new List<GameHit>(cached);
                _searchError = null;
                _screen = BrowseScreen.Results;
                _tab = TopTab.Search;
                PrefetchSearchCovers(cached);
                SetStatus("Found " + cached.Count + " titles");
                return;
            }
            int gen;
            lock (_lock)
            {
                if (_browseBusy) return;
                _browseBusy = true;
                _busyKind = BusyKind.Searching;
                _searchError = null;
                _results = new List<GameHit>();
                _focus = 0;
                _listScroll = 0;
                gen = ++_searchGeneration;
            }
            // Cancel queued cover work from the previous results before this view can request art.
            _covers.BumpGeneration();
            _screen = BrowseScreen.Results; // skeleton results while searching
            _tab = TopTab.Search;
            SetStatus("Searching titles");
            var qCopy = q;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string sourceError;
                    if (!_packageSources.HasEnabled(out sourceError))
                    {
                        throw new Exception(string.IsNullOrEmpty(sourceError)
                            ? "Install and enable a Package Source in Settings"
                            : sourceError);
                    }
                    var sourceHits = _packageSources.Search(qCopy, out sourceError);
                    if (sourceHits == null)
                        throw new Exception(string.IsNullOrEmpty(sourceError) ? "Package Source search failed" : sourceError);
                    var hits = new List<GameHit>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var result in sourceHits)
                    {
                        string key = (result.TitleId ?? "") + "\n" + (result.Region ?? "");
                        if (string.IsNullOrEmpty(result.TitleId) || !seen.Add(key)) continue;
                        hits.Add(new GameHit
                        {
                            TitleId = result.TitleId,
                            Name = string.IsNullOrEmpty(result.DisplayName) ? result.TitleId : result.DisplayName,
                            Region = string.IsNullOrEmpty(result.Region) ? "?" : result.Region,
                            ImageUrl = result.ImageUrl,
                            Source = result.SourceId,
                            Rating = result.Rating,
                            Genres = result.Genres,
                            Backport = result.Backport
                        });
                        if (hits.Count >= 100) break;
                    }
                    lock (_lock)
                    {
                        if (gen != _searchGeneration) return;
                        _results = hits; _focus = 0; _listScroll = 0;
                        _suggestionPool = new List<GameHit>(hits);
                        _browseBusy = false; _busyKind = BusyKind.None;
                        // An empty successful response is a result state, not a source error.
                        _searchError = null;
                        _status = "Found " + hits.Count + " titles";
                    }
                    PrefetchSearchCovers(hits);
                    if (hits.Count > 0) QueryCache.PutSearch(qCopy, hits, sourceStamp);
                }
                catch (Exception ex)
                {
                    lock (_lock)
                    {
                        if (gen != _searchGeneration) return;
                        _browseBusy = false; _busyKind = BusyKind.None;
                        _searchError = "Package Source search failed: " + Clip(ex.Message, 60);
                        _status = _searchError;
                    }
                }
            });
        }

        void StartResolveJob(bool focusFirstUpdate = false)
        {
            if (_selected == null) return;
            int gen;
            string selId;
            lock (_lock)
            {
                if (_browseBusy) return;
                _browseBusy = true;
                _busyKind = BusyKind.Resolving;
                _resolveError = null;
                gen = ++_resolveGeneration;
                selId = _selected.TitleId;
                _links = new List<PkgLink>();
                _linkCandidates = new List<PackageCandidate>();
                _linkPresentation = new List<PackageCandidatePresentation>();
                _expandedPackageGroups.Clear();
                RebuildDetailRows();
                _detailFocus = _detailScroll = 0;
            }
            string enabledError;
            if (!_packageSources.HasEnabled(out enabledError))
            {
                lock (_lock)
                {
                    _browseBusy = false; _busyKind = BusyKind.None;
                    _resolveError = string.IsNullOrEmpty(enabledError)
                        ? "Install and enable a Package Source in Settings"
                        : enabledError;
                    _status = _resolveError;
                }
                _screen = BrowseScreen.Detail;
                return;
            }
            List<PackageCandidate> cachedCands;
            string sourceStamp = _packageSources.CatalogFingerprint();
            bool hostListReady = !_cfg.UseUnlockProvider || _cfg.UnlockProviderId == UnlockProviders.NoneId ||
                DebridHostSupport.Load(_cfg, _cfg.UnlockProviderId, false) != null;
            if (hostListReady && QueryCache.TryResolve(_selected.TitleId, out cachedCands, sourceStamp) && cachedCands != null &&
                cachedCands.Count > 0)
            {
                string hostMessage;
                cachedCands = DebridHostSupport.Filter(_cfg, cachedCands, false, out hostMessage);
                var cachedLinks = new List<PkgLink>();
                for (int i = 0; i < cachedCands.Count; i++)
                    cachedLinks.Add(PackageCandidateAdapter.ToPkgLink(cachedCands[i]));
                _links = cachedLinks;
                _linkCandidates = cachedCands;
                _linkPresentation = PackageCandidatePresentation.Build(
                    cachedCands, _selected.TitleId, _selected.Region);
                // Mirrors start collapsed; Triangle expands deliberately.
                _expandedPackageGroups.Clear();
                RebuildDetailRows();
                _detailFocus = _detailScroll = 0;
                if (focusFirstUpdate) FocusFirstUpdateRow();
                _browseBusy = false;
                _busyKind = BusyKind.None;
                _screen = BrowseScreen.Detail;
                if (cachedCands.Count == 0) _resolveError = hostMessage;
                SetStatus(cachedCands.Count + " links (cached)");
                return;
            }
            SetStatus("Resolving Package Sources " + _selected.TitleId);
            if (!string.IsNullOrEmpty(_selected.ImageUrl))
                _covers.Request(_selected.TitleId, _selected.ImageUrl);
            // Enter detail immediately for resolving chrome (worker must not force-nav later).
            _screen = BrowseScreen.Detail;
            var sel = _selected;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string sourceError;
                    var candidates = _packageSources.Resolve(sel.TitleId, sel.Name, sel.Region, out sourceError);
                    if (candidates == null)
                        throw new Exception(string.IsNullOrEmpty(sourceError) ? "Package Source resolve failed" : sourceError);
                    if (candidates.Count > 0) QueryCache.PutResolve(sel.TitleId, candidates, sourceStamp);
                    string hostMessage;
                    candidates = DebridHostSupport.Filter(_cfg, candidates, true, out hostMessage);
                    var links = new List<PkgLink>();
                    foreach (var candidate in candidates)
                        links.Add(PackageCandidateAdapter.ToPkgLink(candidate));
                    var presentation = PackageCandidatePresentation.Build(
                        candidates, sel.TitleId, sel.Region);
                    lock (_lock)
                    {
                        if (gen != _resolveGeneration) return;
                        if (_selected == null ||
                            !string.Equals(_selected.TitleId, selId, StringComparison.OrdinalIgnoreCase))
                            return;
                        _links = links; _linkCandidates = candidates;
                        _linkPresentation = presentation; _focus = 0; _linkScroll = 0;
                        // Mirrors start collapsed; Triangle expands deliberately.
                        _expandedPackageGroups.Clear();
                        RebuildDetailRows();
                        _detailFocus = _detailScroll = 0;
                        if (focusFirstUpdate) FocusFirstUpdateRow();
                        _browseBusy = false; _busyKind = BusyKind.None;
                        _resolveError = links.Count == 0 ? (hostMessage ?? "No matching package links") : null;
                        _status = links.Count + " links (Package Sources)";
                    }
                }
                catch (Exception ex)
                {
                    lock (_lock)
                    {
                        if (gen != _resolveGeneration) return;
                        _browseBusy = false; _busyKind = BusyKind.None;
                        _links = new List<PkgLink>();
                        _linkCandidates = new List<PackageCandidate>();
                        _linkPresentation = new List<PackageCandidatePresentation>();
                        _expandedPackageGroups.Clear();
                        RebuildDetailRows();
                        _detailFocus = _detailScroll = 0;
                        _resolveError = "Resolve failed: " + Clip(ex.Message, 60);
                        _status = _resolveError;
                    }
                }
            });
        }

        void StartRecommendedResolve(GameHit game)
        {
            if (game == null) return;
            int gen;
            lock (_lock)
            {
                if (_browseBusy) return;
                _browseBusy = true;
                _busyKind = BusyKind.Resolving;
                gen = ++_resolveGeneration;
            }
            SetStatus("Resolving recommended packages");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string sourceError;
                    var candidates = _packageSources.Resolve(game.TitleId, game.Name, game.Region, out sourceError);
                    if (candidates == null)
                        throw new Exception(string.IsNullOrEmpty(sourceError)
                            ? "Package Source resolve failed" : sourceError);
                    string hostMessage;
                    candidates = DebridHostSupport.Filter(_cfg, candidates, true, out hostMessage);
                    if (candidates.Count == 0 && hostMessage != null) throw new Exception(hostMessage);
                    lock (_lock) { if (gen != _resolveGeneration) return; }
                    int queued = QueueRecommendedCandidates(game, candidates);
                    lock (_lock)
                    {
                        if (gen != _resolveGeneration) return;
                        _browseBusy = false;
                        _busyKind = BusyKind.None;
                        _status = queued > 0 ? "Recommended queued" : "No recommended packages found";
                    }
                    if (queued > 0)
                        User.NotifyToast(game.Name + " · " + RecommendedToastSuffix(candidates) + " queued");
                    else
                        User.NotifyToast("No recommended packages");
                }
                catch (Exception ex)
                {
                    lock (_lock)
                    {
                        if (gen != _resolveGeneration) return;
                        _browseBusy = false;
                        _busyKind = BusyKind.None;
                        _status = "Queue failed: " + Clip(ex.Message, 48);
                    }
                    User.NotifyToast("Recommended queue failed");
                }
            });
        }

        void QueueRecommendedResolved(GameHit game, bool notify)
        {
            int queued = QueueRecommendedCandidates(game, _linkCandidates);
            if (queued <= 0)
            {
                SetStatus("No base or update package is available");
                if (notify) User.NotifyToast("No recommended packages");
                return;
            }
            string suffix = RecommendedToastSuffix(_linkCandidates);
            SetStatus("Recommended queued · R1 Downloads");
            if (notify) User.NotifyToast((game == null ? "Title" : game.Name) + " · " + suffix + " queued");
        }

        int QueueRecommendedCandidates(GameHit game, IList<PackageCandidate> candidates)
        {
            if (game == null || candidates == null || candidates.Count == 0) return 0;
            int baseIndex = PreferredCandidate(candidates, "base", false);
            int updateIndex = PreferredCandidate(candidates, "update", true);
            int queued = 0;
            if (baseIndex >= 0 && QueueCandidate(game, candidates[baseIndex])) queued++;
            if (updateIndex >= 0 && updateIndex != baseIndex && QueueCandidate(game, candidates[updateIndex])) queued++;
            return queued;
        }

        bool QueueCandidate(GameHit game, PackageCandidate candidate)
        {
            if (candidate == null) return false;
            bool needsLinkService = candidate.AccessType != PackageAccessType.Direct;
            if (needsLinkService && _cfg.UseUnlockProvider &&
                !string.Equals(_cfg.UnlockProviderId, UnlockProviders.NoneId, StringComparison.OrdinalIgnoreCase) &&
                !UnlockProviders.IsConfigured(_cfg, _cfg.UnlockProviderId))
            {
                SetStatus("Configure " + UnlockProviders.DisplayName(_cfg.UnlockProviderId) + " in Settings");
                return false;
            }
            try
            {
                string message;
                _dlMgr.Enqueue(game, candidate, out message);
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("Queue fail: " + Clip(ex.Message, 40));
                return false;
            }
        }

        static int PreferredCandidate(IList<PackageCandidate> candidates, string kind, bool latestVersion)
        {
            int best = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                PackageCandidate candidate = candidates[i];
                if (candidate == null || !SamePackageKind(candidate.PackageKindHint, kind)) continue;
                if (best < 0) { best = i; continue; }
                int version = ComparePackageVersions(candidate.PackageVersion, candidates[best].PackageVersion);
                if (latestVersion && version > 0) { best = i; continue; }
                if ((!latestVersion || version == 0) && IsPreferredHost(candidate) &&
                    !IsPreferredHost(candidates[best])) best = i;
            }
            return best;
        }

        static bool SamePackageKind(string value, string kind)
        {
            string normalized = (value ?? "").Trim().ToLowerInvariant();
            if (normalized == "game") normalized = "base";
            return normalized == kind;
        }

        static bool IsPreferredHost(PackageCandidate candidate)
        {
            string host = candidate == null ? "" : (candidate.HosterName ?? candidate.Label ?? "");
            return host.IndexOf("onefile", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static int ComparePackageVersions(string left, string right)
        {
            string[] a = (left ?? "").TrimStart('v', 'V').Split('.');
            string[] b = (right ?? "").TrimStart('v', 'V').Split('.');
            int n = Math.Max(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int av = 0, bv = 0;
                if (i < a.Length) int.TryParse(a[i], out av);
                if (i < b.Length) int.TryParse(b[i], out bv);
                if (av != bv) return av.CompareTo(bv);
            }
            return 0;
        }

        static string RecommendedToastSuffix(IList<PackageCandidate> candidates)
        {
            int update = PreferredCandidate(candidates, "update", true);
            if (update >= 0 && !string.IsNullOrWhiteSpace(candidates[update].PackageVersion))
                return "base + v" + candidates[update].PackageVersion.TrimStart('v', 'V');
            return update >= 0 ? "base + update" : "base";
        }

        static string DetailGroupKey(PackageCandidatePresentation meta)
        {
            if (meta == null) return "";
            if (!string.IsNullOrEmpty(meta.GroupId) &&
                meta.GroupId.IndexOf("single:", StringComparison.OrdinalIgnoreCase) != 0)
                return meta.GroupId;
            if (string.Equals(meta.Kind, "update", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(meta.Version))
                return "display:update:" + meta.Version;
            if (string.Equals(meta.Kind, "base", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(meta.Kind, "game", StringComparison.OrdinalIgnoreCase))
                return "display:base";
            return meta.GroupId ?? "";
        }

        void RebuildDetailRows()
        {
            _detailRows.Clear();
            _detailHosts.Clear();
            Array.Clear(_detailTypeCounts, 0, _detailTypeCounts.Length);
            var summaryGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var meta in _linkPresentation)
            {
                if (summaryGroups.Add(DetailGroupKey(meta)))
                {
                    _detailTypeCounts[0]++;
                    _detailTypeCounts[Math.Min(4, PackageKindOrder(meta.Kind) + 1)]++;
                }
                if (!string.IsNullOrWhiteSpace(meta.Hoster) && !_detailHosts.Exists(h => string.Equals(h, meta.Hoster, StringComparison.OrdinalIgnoreCase))) _detailHosts.Add(meta.Hoster);
            }
            if (_hostFilter > _detailHosts.Count) _hostFilter = 0;
            for (int kindOrder = 0; kindOrder < 4; kindOrder++)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _linkPresentation.Count; i++)
                {
                    var meta = _linkPresentation[i]; string group = DetailGroupKey(meta);
                    if (PackageKindOrder(meta.Kind) != kindOrder || !PackageMatches(i) || !seen.Add(group)) continue;
                    int preferred = i;
                    for (int j = i + 1; j < _linkPresentation.Count; j++)
                        if (PackageMatches(j) && string.Equals(DetailGroupKey(_linkPresentation[j]), group, StringComparison.OrdinalIgnoreCase) && j < _linkCandidates.Count && IsPreferredHost(_linkCandidates[j])) preferred = j;
                    _detailRows.Add(preferred);
                    if (!_expandedPackageGroups.Contains(group)) continue;
                    for (int j = 0; j < _linkPresentation.Count; j++)
                        if (j != preferred && PackageMatches(j) && string.Equals(DetailGroupKey(_linkPresentation[j]), group, StringComparison.OrdinalIgnoreCase)) _detailRows.Add(j);
                }
            }
            _detailFocus = Math.Max(0, Math.Min(_detailFocus, _detailRows.Count - 1));
        }

        void FocusFirstUpdateRow()
        {
            for (int row = 0; row < _detailRows.Count; row++)
            {
                int index = _detailRows[row];
                if (index < 0 || index >= _linkPresentation.Count ||
                    !string.Equals(_linkPresentation[index].Kind, "update",
                        StringComparison.OrdinalIgnoreCase)) continue;
                _detailFocus = row;
                return;
            }
        }

        bool DetailRowIsChild(int detailIndex)
        {
            if (detailIndex <= 0 || detailIndex >= _detailRows.Count) return false;
            int index = _detailRows[detailIndex];
            int prev = _detailRows[detailIndex - 1];
            if (index < 0 || prev < 0 || index >= _linkPresentation.Count ||
                prev >= _linkPresentation.Count) return false;
            return string.Equals(
                DetailGroupKey(_linkPresentation[index]),
                DetailGroupKey(_linkPresentation[prev]),
                StringComparison.OrdinalIgnoreCase);
        }

        bool DetailRowIsLastChild(int detailIndex)
        {
            if (!DetailRowIsChild(detailIndex)) return false;
            if (detailIndex + 1 >= _detailRows.Count) return true;
            return !DetailRowIsChild(detailIndex + 1);
        }

        static int PackageKindOrder(string kind)
        {
            if (string.Equals(kind, "base", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, "game", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(kind, "update", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(kind, "dlc", StringComparison.OrdinalIgnoreCase)) return 2;
            return 3;
        }

        void QueueDownload(PkgLink link)
        {
            if (link == null || _selected == null) return;
            PackageCandidate candidate = null;
            int linkIndex = _links.IndexOf(link);
            RefreshQueueModel(true);
            if (PackageGroupQueued(linkIndex, _queueSnapshot))
            {
                SetStatus("This package is already queued through another mirror · R1 Downloads");
                return;
            }
            if (linkIndex >= 0 && linkIndex < _linkCandidates.Count)
                candidate = _linkCandidates[linkIndex];
            if (candidate == null)
            {
                SetStatus("Queue failed: Package Source provenance is missing");
                return;
            }
            bool needsLinkService = candidate.AccessType != PackageAccessType.Direct;
            if (needsLinkService && _cfg.UseUnlockProvider &&
                !string.Equals(_cfg.UnlockProviderId, UnlockProviders.NoneId, StringComparison.OrdinalIgnoreCase) &&
                !UnlockProviders.IsConfigured(_cfg, _cfg.UnlockProviderId))
            {
                SetStatus("Configure " + UnlockProviders.DisplayName(_cfg.UnlockProviderId) + " in Settings");
                OpenSettings();
                return;
            }
            try
            {
                string queueMessage;
                _dlMgr.Enqueue(_selected, candidate, out queueMessage);
                SetStatus(queueMessage + " · R1 Downloads");
                User.NotifyToast(PackageTitle(candidate.PackageKindHint) + " added · R1 Downloads");
                Invalidated = true;
            }
            catch (Exception ex)
            {
                SetStatus("Queue fail: " + Clip(ex.Message, 40));
            }
        }

        void StartInstall(DlItem item, bool uninstallFirst)
        {
            if (item == null || string.IsNullOrEmpty(item.DestPath)) return;
            if (uninstallFirst && !IsBasePackage(item))
            {
                SetStatus("Force reinstall is only available for base games");
                return;
            }
            string missing;
            if (!_dlMgr.EnsureLocalPackage(item.Id, out missing))
            {
                SetStatus(missing + " — remove row or re-download");
                return;
            }
            if (PkgValidator.RequestedKind(item.Kind) == PkgContentKind.Patch)
            { _dlMgr.QueueLocalInstall(item.Id); SetStatus("Verifying update before BGFT installation"); return; }
            string exp = item.TitleId;
            if (_dlMgr.HasPendingInstall(exp))
            {
                SetStatus("An install for " + exp + " is already pending");
                return;
            }
            lock (_lock)
            {
                if (_installingTitles.Contains(exp))
                {
                    _status = "An install for " + exp + " is already running";
                    return;
                }
                _installingTitles.Add(exp);
            }
            _dlMgr.MarkInstalling(item.Id, uninstallFirst ? "Uninstall+install..." : "Installing...");
            SetStatus((uninstallFirst ? "Reinstall " : "Install ") + item.TitleId + "...");
            string path = item.DestPath;
            string id = item.Id;
            string requestedKind = item.Kind;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string tid, err;
                    int taskId;
                    PkgContentKind packageKind;
                    string kindDetail;
                    if (!PkgValidator.TryGetContentKind(path, out packageKind, out kindDetail))
                        packageKind = PkgContentKind.Unknown;
                    var outcome = PkgInstaller.InstallLocal(path, exp, requestedKind,
                        out tid, out err, out taskId, uninstallFirst);
                    switch (outcome)
                    {
                        case InstallOutcome.Started:
                            if (taskId < 0)
                            {
                                // AppInstUtil/duplicate BGFT acceptance exposes no task to verify.
                                _dlMgr.MarkInstallAccepted(id,
                                    "Sent to PS4 — verification pending · PKG kept; SQUARE resets status");
                                SetStatus("Sent to PS4 — verification pending · PKG kept");
                                User.NotifyToast("Sent to PS4");
                                break;
                            }
                            SetStatus("Installing " + tid + "...");
                            User.NotifyToast("Install started");
                            string waitError;
                            bool localCopyComplete;
                            string checkId = !string.IsNullOrEmpty(tid) ? tid : exp;
                            bool completed = PkgInstaller.WaitForInstall(taskId, checkId, packageKind, percent =>
                            {
                                _dlMgr.UpdateInstallProgress(id, percent);
                                if (percent >= 0) SetStatus("Installing " + checkId + "  " + percent + "%");
                            }, out localCopyComplete, out waitError);
                            if (completed)
                            {
                                // Never delete the source PKG automatically. BGFT can still need
                                // the file after LocalCopyPercent hits 100, and early delete was
                                // failing installs. User clears finished rows with SQUARE/TRIANGLE.
                                // Only base-game title presence can confirm install.
                                // Patch/DLC LocalCopy 100% is not proof — keep Submitted + PKG.
                                bool confirmedBase = packageKind == PkgContentKind.BaseGame &&
                                    localCopyComplete &&
                                    !string.IsNullOrEmpty(checkId) &&
                                    PkgInstaller.IsTitleInstalled(checkId);
                                if (confirmedBase)
                                {
                                    _dlMgr.MarkInstalled(id,
                                        "Installed " + checkId + " — PKG kept (SQUARE removes)", false);
                                    SetStatus("Install complete: " + checkId + " — PKG kept");
                                    User.NotifyToast("Install complete");
                                }
                                else
                                {
                                    string msg = packageKind == PkgContentKind.Patch ||
                                                 packageKind == PkgContentKind.AddOn
                                        ? "Sent to PS4 — verify update/DLC · PKG kept"
                                        : "Sent to PS4 — verification pending · PKG kept";
                                    _dlMgr.MarkInstallAccepted(id, msg, taskId);
                                    SetStatus("Sent to PS4: " + checkId + " — verification pending · PKG kept");
                                    User.NotifyToast("Sent to PS4");
                                }
                            }
                            else
                            {
                                string combined = waitError ?? "BGFT install failed";
                                if (combined.StartsWith("BGFT progress") || combined.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0)
                                { _dlMgr.MarkInstallAccepted(id, "PS4 install status unavailable · task and PKG retained", taskId); break; }
                                _dlMgr.MarkInstallFailed(id, combined);
                                SetStatus("Install failed: " + Clip(combined, 44) + " — PKG kept");
                                User.NotifyToast("Install failed; PKG kept");
                            }
                            break;
                        case InstallOutcome.AlreadyInstalled:
                            _dlMgr.MarkAlreadyInstalled(id,
                                "Already installed; TRIANGLE force-reinstalls base games");
                            SetStatus("Already installed " + exp + " — PKG kept for force reinstall");
                            User.NotifyToast("Already installed");
                            break;
                        case InstallOutcome.InvalidPackage:
                            _dlMgr.MarkPackageInvalid(id, err);
                            SetStatus("Bad PKG: " + Clip(err, 48) + " — re-download");
                            User.NotifyToast("Bad PKG");
                            break;
                        case InstallOutcome.UninstallFailed:
                            _dlMgr.MarkInstallFailed(id, err);
                            SetStatus("Uninstall fail: " + Clip(err, 40));
                            User.NotifyToast("Uninstall fail");
                            break;
                        default:
                            _dlMgr.MarkInstallFailed(id, err);
                            SetStatus("Install fail: " + Clip(err, 48));
                            User.NotifyToast("Install fail");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    _dlMgr.MarkInstallFailed(id, ex.Message);
                    SetStatus("Install fail: " + Clip(ex.Message, 48));
                    User.NotifyToast("Install fail");
                }
                finally
                {
                    lock (_lock) _installingTitles.Remove(exp);
                }
            });
        }

        #endregion

        #region Draw

        static bool IsBasePackage(DlItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.DestPath)) return false;
            PkgContentKind kind;
            string detail;
            return PkgValidator.TryGetContentKind(item.DestPath, out kind, out detail) &&
                   kind == PkgContentKind.BaseGame;
        }

        public override void OnCycleBegin(uint FrameTime, uint NextFrameTime)
        {
            _frameTime = FrameTime;
            string nextStatus;
            if (User.TryTakeStatus(out nextStatus))
                SetStatus(nextStatus);
            // Pair completion/expiry is network-driven; never wait for another button press.
            PollPairState();
            if (UiElapsed(_landingModelAt) >= 1000)
                RefreshLandingModel();
            uint toastAge = _toastActive ? UiElapsed(_toastStartedAt) : 0;
            if (_toastActive && toastAge >= ToastEnterMs + ToastHoldMs + ToastExitMs)
            {
                _toastActive = false;
                _toastText = "";
            }
            if (!_toastActive)
            {
                string nextToast;
                if (User.TryTakeToast(out nextToast))
                {
                    _toastText = nextToast;
                    _toastStartedAt = UiTick();
                    _toastActive = true;
                }
            }
            bool busyAnim;
            bool sourceInstallAnim;
            lock (_lock)
            {
                busyAnim = _browseBusy;
                sourceInstallAnim = _sourceInstallStage != SourceInstallStage.Idle;
            }
            // The window owns the 16/17 ms presentation cadence. A second 100 ms
            // throttle previously reduced normal navigation and downloads to 10 FPS.
            Invalidated = true;
            _lastUiRefresh = FrameTime;
            _covers.PumpReady(Renderer.Handler, 1);
            RecordFrameTiming(FrameTime);
        }

        public override void OnBackground(uint FrameTime)
        {
            // Source workers publish several related lists under this gate. Keep
            // one consistent model for the entire frame, including footer hints.
            lock (_lock) DrawFrame(FrameTime);
        }

        void DrawFrame(uint FrameTime)
        {
            IntPtr r = Renderer.Handler;
            UiFont.BindRenderer(r);
            GamepadIcons.Ensure(r);
            if (DrawLaunchBranding(r, FrameTime)) return;

            UiBackgroundSurface surface = _settingsOpen
                ? UiBackgroundSurface.Settings
                : (_tab == TopTab.Search && _screen == BrowseScreen.Search
                    ? UiBackgroundSurface.SearchLanding : UiBackgroundSurface.Content);
            DrawAppBackground(r, surface);

            if (_settingsOpen)
            {
                // Exclusive full-screen settings — never draw Search/Downloads under it.
                DrawSettingsOverlay(r);
                DrawFooter(r);
                DrawToast(r);
                if (_uiOverlay != UiOverlay.None) DrawUiOverlay(r);
                return;
            }

            DrawHeader(r);
            if (_tab == TopTab.Downloads)
                DrawDownloads(r);
            else if (_screen == BrowseScreen.Detail)
                DrawDetail(r);
            else
            {
                switch (_screen)
                {
                    case BrowseScreen.Search: DrawSearch(r); break;
                    case BrowseScreen.Results: DrawResults(r); break;
                }
            }

            // No center modal — busy state is footer text + skeleton/panel animation.
            DrawFooter(r);
            DrawToast(r);
            if (_uiOverlay != UiOverlay.None) DrawUiOverlay(r);
        }

        bool UsesImageBackground()
        {
            return false;
        }

        static string ShowcaseWallpaperPath()
        {
            try
            {
                string bas = (Orbis.Internals.IO.GetAppBaseDirectory() ?? ".").TrimEnd('/', '\\');
                string[] paths =
                {
                    "/app0/assets/images/showcase-bg.jpg",
                    bas + "/assets/images/showcase-bg.jpg",
                    "assets/images/showcase-bg.jpg",
                    "/app0/assets/images/showcase-bg.png",
                    bas + "/assets/images/showcase-bg.png"
                };
                for (int i = 0; i < paths.Length; i++)
                    if (File.Exists(paths[i])) return paths[i];
            }
            catch { }
            return "";
        }

        void DrawAppBackground(IntPtr r, UiBackgroundSurface surface)
        {
            if (!DrawPattern(r)) Fill(r, 0, 0, W, H, Bg);
        }

        void DrawContentShadow(IntPtr r)
        {
            if (_dimTex == IntPtr.Zero)
            {
                _dimTex = SDL_CreateTexture(r, SDL_PIXELFORMAT_ABGR8888,
                    (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STATIC, 1, 1);
                if (_dimTex == IntPtr.Zero) return;
                byte[] px = { 0, 0, 0, 180 };
                var pin = System.Runtime.InteropServices.GCHandle.Alloc(px,
                    System.Runtime.InteropServices.GCHandleType.Pinned);
                try { SDL_UpdateTexture(_dimTex, IntPtr.Zero, pin.AddrOfPinnedObject(), 4); }
                finally { pin.Free(); }
                SDL_SetTextureBlendMode(_dimTex, SDL_BlendMode.SDL_BLENDMODE_BLEND);
            }
            var dst = new SDL_Rect { x = 0, y = 100, w = W, h = 872 };
            SDL_SetTextureAlphaMod(_dimTex, 160);
            SDL_RenderCopy(r, _dimTex, IntPtr.Zero, ref dst);
            SDL_SetTextureAlphaMod(_dimTex, 255);
        }

        void DrawHeader(IntPtr r)
        {
            Fill(r, 0, 0, W, 106, C(25, 25, 25));
            DrawHeaderBrand(r);
            if (!_settingsOpen)
            {
                DrawTab(r, 709, 25, 220, "L1", "Search", _tab == TopTab.Search);
                DrawTab(r, 975, 25, 246, "R1", "Downloads", _tab == TopTab.Downloads);
                float target = _tab == TopTab.Search ? 709 : 975;
                uint now = UiTick();
                if (_navUnderlineX < 0 || _cfg.ReduceMotion) _navUnderlineX = target;
                else _navUnderlineX += (target - _navUnderlineX) * Math.Min(1, unchecked(now - _navUnderlineAt) / 75f);
                _navUnderlineAt = now;
                Fill(r, (int)Math.Round(_navUnderlineX), 103, _tab == TopTab.Search ? 220 : 246, 3, Accent);
            }
            Fill(r, 0, 105, W, 1, C(46, 46, 46));
            DrawHeaderStatusChips(r);
        }

        void DrawHeaderStatusChips(IntPtr r)
        {
            int enabledSources = 0;
            for (int i = 0; i < _sourceUi.Count; i++)
                if (_sourceUi[i] != null && _sourceUi[i].Enabled) enabledSources++;
            bool linkActive = _cfg.HasActiveUnlock;
            bool linkSaved = _cfg.HasRealDebrid || _cfg.HasDeepbrid || _cfg.HasAllDebrid || _cfg.HasTorBox;

            string link = linkActive ? UnlockProviders.DisplayName(_cfg.UnlockProviderId) :
                (!_cfg.UseUnlockProvider || _cfg.UnlockProviderId == UnlockProviders.NoneId ? "Direct links" : linkSaved ? "Link service saved" : "No link service");
            string sources = enabledSources + (enabledSources == 1 ? " source" : " sources");
            string line = link + " · " + sources;
            int statusWidth = Math.Min(520, UiFont.MeasurePx(16, line));
            TextFit(r, W - 142 - statusWidth, 43, 16, statusWidth + 1, line, C(132, 132, 132));
        }

        string EnabledSourceName()
        {
            for (int i = 0; i < _sourceUi.Count; i++)
            {
                SourceUiEntry source = _sourceUi[i];
                if (source != null && source.Enabled && !string.IsNullOrEmpty(source.Name))
                    return source.Name;
            }
            return "enabled source";
        }

        void DrawTab(IntPtr r, int x, int y, int w, string shoulder, string label, bool active)
        {
            if (!GamepadIcons.Draw(r, shoulder.ToLowerInvariant(), x + 12, y + 12, 32))
            {
                var key = new SDL_Rect { x = x + 12, y = y + 15, w = 34, h = 26 };
                StrokeRect(r, key, C(104, 104, 104), 1);
                TextCentered(r, key, 16, shoulder, active ? White : Muted);
            }
            TextPx(r, x + 62, y + 11, 24, label, active ? White : Muted);
        }

        void DrawFooter(IntPtr r)
        {
            Fill(r, 0, 986, W, 94, C(25, 25, 25));
            Fill(r, 0, 986, W, 1, Border);
            string status;
            bool busy;
            BusyKind kind;
            SourceInstallStage sourceStage;
            lock (_lock)
            {
                status = _status;
                sourceStage = _sourceInstallStage;
                busy = _browseBusy || SourceInstallActive(sourceStage);
                kind = _busyKind;
            }
            string context = FooterContextLabel(status, busy, kind, sourceStage);
            // Context stays left; transient outcomes belong in the toast above this row.
            if (busy)
            {
                int dots = 0;
                string anim = new string('.', dots);
                string label = SourceInstallActive(sourceStage)
                    ? SourceInstallTitle(sourceStage)
                    : (kind == BusyKind.Searching
                        ? "Searching"
                        : (kind == BusyKind.Resolving ? "Resolving" : "Working"));
                string core = string.IsNullOrEmpty(context) ? label : context;
                while (core.Length > 0 && core[core.Length - 1] == '.')
                    core = core.Substring(0, core.Length - 1);
                core = core.TrimEnd();
                string line = core + anim;
                SDL_Color col = Muted;
                TextFit(r, 72, 1014, 20, 310, line, col);
            }
            else
                TextFit(r, 72, 1014, 20, 310, context, White);
            DrawFooterHints(r, 1017);
            // Product version v5.10: footer displays BuildIdentity.Label (beta/hash) bottom-right.
            string buildLabel = BuildIdentity.Label;
            int buildW = UiFont.MeasurePx(15, buildLabel);
            TextPx(r, W - 48 - buildW, 1016, 15, buildLabel, Muted);
        }

        string FooterContextLabel(string status, bool busy, BusyKind kind,
            SourceInstallStage sourceStage)
        {
            if (_settingsOpen) return "Settings";
            if (SourceInstallActive(sourceStage)) return SourceInstallTitle(sourceStage);
            if (busy && kind == BusyKind.Searching) return "Searching";
            if (busy && kind == BusyKind.Resolving)
                return _selected == null ? "Resolving" : "Resolving " + _selected.TitleId;
            if (_tab == TopTab.Downloads) return "Downloads";
            if (_screen == BrowseScreen.Results)
            {
                string q = string.IsNullOrEmpty(_query) ? "Results" : ("\"" + _query + "\"");
                return _results.Count > 0 ? q + "  ·  " + _results.Count + " titles" : q;
            }
            if (_screen == BrowseScreen.Detail)
                return _selected == null ? "Packages" :
                    (_selected.Name ?? _selected.TitleId) + "  ·  " + _selected.TitleId;
            return "Search";
        }

        void DrawToast(IntPtr r)
        {
            if (!_toastActive || string.IsNullOrEmpty(_toastText)) return;
            if (_settingsOpen && _sourceInstallStage != SourceInstallStage.Idle) return;
            uint age = UiElapsed(_toastStartedAt), total = ToastEnterMs + ToastHoldMs + ToastExitMs;
            if (age >= total) return;
            int y = 800;
            if (!_cfg.ReduceMotion && age < ToastEnterMs) { double t = age / (double)ToastEnterMs; y += (int)(18 * Math.Pow(1 - t, 3)); }
            else if (!_cfg.ReduceMotion && age > ToastEnterMs + ToastHoldMs) { double t = (age - ToastEnterMs - ToastHoldMs) / (double)ToastExitMs; y += (int)(30 * t * t); }
            string lower = _toastText.ToLowerInvariant();
            bool bad = lower.Contains("fail") || lower.Contains("error") || lower.Contains("corrupt") || lower.Contains("could not");
            bool queued = lower.Contains("queued") || lower.Contains("added");
            bool good = queued || lower.Contains("complete") || lower.Contains("installed") || lower.Contains("saved") || lower.Contains("ready");
            string heading = bad ? "Needs attention" : queued ? "Added to downloads" : lower.Contains("saved") ? "Settings saved" : good ? "Complete" : "SSPI";
            SDL_Color tone = bad ? Danger : good ? Ok : White;
            var box = new SDL_Rect { x = W - 72 - 540, y = y, w = 540, h = 146 };
            SoftRect(r, new SDL_Rect { x = box.x, y = box.y + 5, w = box.w, h = box.h }, C(10, 10, 10));
            SoftRect(r, box, C(39, 39, 39)); StrokeRect(r, box, C(85, 85, 85), 1);
            var symbol = new SDL_Rect { x = box.x + 21, y = box.y + 24, w = 44, h = 44 };
            SoftRect(r, symbol, Raised); DesignIcon(r, bad ? "error" : good ? "check" : "download", symbol.x + 11, symbol.y + 10, 23, tone);
            TextPx(r, box.x + 84, box.y + 18, 22, heading, White);
            TextWrapped(r, box.x + 84, box.y + 59, 17, box.w - 110, _toastText, Muted);
            int remaining = (int)((box.w - 16) * (1 - age / (double)total));
            Fill(r, box.x + 8, box.y + box.h - 2, Math.Max(0, remaining), 2, tone);
        }

        void DrawFooterHints(IntPtr r, int y)
        {
            // Build hint pairs first, measure total width, center on viewport X=960.
            if (!_settingsOpen && _tab == TopTab.Downloads && !string.IsNullOrEmpty(_fileDetailId))
            {
                TextCentered(r, new SDL_Rect { x = 470, y = y, w = 980, h = 36 }, 22, "↑ / ↓  Scroll files      ○  Back to downloads", Muted); return;
            }
            var pairs = new List<KeyValuePair<string, string>>();
            Action<string, string> add = (icon, label) =>
                pairs.Add(new KeyValuePair<string, string>(icon, label));

            if (_softKbOpen)
            {
                add("cross", "Type");
                add("square", "Del");
                add("triangle", "Space");
                add("l2", "Caps");
                add("r2", _softKbForSourceUrl ? "Install" :
                    (_softKbForDeepbrid || _softKbForAllDebrid || _softKbForTorBox ||
                    _softKbForProxy ? "Save" : "Search"));
                add("circle", "Close");
            }
            else if (_settingsOpen)
            {
                if (_settingsPage == 4) { if (_storageDelete != null) { add("cross", "Confirm delete"); add("square", "Keep file"); } else { add("triangle", "Refresh"); if (_storageFiles.Count > 0) add("square", "Delete file"); } add("circle", "Back"); }
                else if (_settingsPage == 2)
                {
                    add("cross", _settingsFocus == 0 ? "Add source" : "Toggle");
                    if (_settingsFocus > 0) add("square", "Remove");
                    add("circle", "Back");
                }
                else if (_settingsPage == 1) { add("cross", "Select"); add("square", "QR setup"); add("circle", "Close"); }
                else if (_settingsPage == 3) { add("cross", "Select"); add("triangle", "Restore"); add("circle", "Close"); }
                else if (_settingsPage == 0 && _settingsFocus == 0)
                {
                    add("cross", "Bandwidth limit");
                    add("circle", "Close");
                }
                else
                {
                    add("cross", "Select");
                    add("square", "Edit");
                    add("circle", "Close");
                }
            }
            else if (_tab == TopTab.Downloads)
            {
                // Contextual hints from focused download row (matches actual PKG UX).
                RefreshQueueModel();
                var rows = _queueRows;
                if (rows.Count == 0)
                {
                    add("circle", "Search");
                    add("l1", "Tabs");
                    add("options", "Settings");
                }
                else
                {
                    DlItem focused = null;
                    if (_dlFocus >= 0 && _dlFocus < rows.Count)
                        focused = rows[_dlFocus].Item;
                    if (focused != null)
                    {
                        if (focused.State == DlState.Downloading || focused.State == DlState.Resolving)
                            add("cross", "Pause");
                        else if (focused.State == DlState.Finalizing)
                            add("cross", "Wait");
                        else if (focused.State == DlState.Paused || focused.State == DlState.Queued)
                            add("cross", "Resume");
                        else if (focused.State == DlState.Completed)
                            add("cross", "Install");
                        else if (focused.State == DlState.Failed)
                            add("cross", "Retry");
                        else if (focused.State == DlState.Submitted)
                            add("cross", "Verify");
                        else
                            add("cross", "Action");

                        if (focused.State == DlState.Downloading || focused.State == DlState.Resolving ||
                            focused.State == DlState.Finalizing ||
                            focused.State == DlState.Installing)
                            add("square", "Cancel");
                        else if (focused.State == DlState.Submitted)
                            add("square", "Reset status");
                        else
                            add("square", "Remove");
                    }
                    else
                    {
                        add("cross", "Expand / collapse");
                    }
                    add("r2", _downloadFilesTitle == null ? "Files" : "File details");
                    add("circle", "Back");
                    add("l1", "Tabs");
                    add("options", "Settings");
                }
            }
            else if (_screen == BrowseScreen.Detail)
            {
                bool busyR;
                lock (_lock) busyR = _browseBusy;
                if (busyR)
                    add("circle", "Back");
                else if (!string.IsNullOrEmpty(_resolveError) || (_links != null && _links.Count == 0))
                {
                    add("square", "Retry");
                    add("circle", "Back");
                }
                else
                {
                    add("cross", "Queue");
                    add("triangle", "Mirrors");
                    add("square", "Recommended");
                    add("circle", "Back");
                    add("l1", "Tabs");
                }
            }
            else
            {
                switch (_screen)
                {
                    case BrowseScreen.Search:
                        add("cross", _searchLandingFocus == 0 ? "Type" : _searchLandingFocus <= _libraryGames.Count ? "Open" : "Downloads");
                        add("options", "Settings");
                        break;
                    case BrowseScreen.Results:
                        if (_browseBusy) { add("circle", "Cancel search"); break; }
                        if (FilteredResults().Count == 0)
                            add("cross", string.IsNullOrEmpty(_searchError) ? "Edit search" : "Retry");
                        else
                        {
                            add("cross", "Open");
                            add("square", "Queue");
                        }
                        add("circle", "Back");
                        add("l1", "Tabs");
                        break;
                }
            }

            if (pairs.Count == 0) return;
            int totalW = 0;
            for (int i = 0; i < pairs.Count; i++)
            {
                totalW += 42; // icon slot
                totalW += UiFont.MeasurePx(20, pairs[i].Value) + 18;
            }
            int cx = (W - totalW) / 2;
            for (int i = 0; i < pairs.Count; i++)
            {
                string icon = pairs[i].Key;
                string label = pairs[i].Value;
                if (!GamepadIcons.Draw(r, icon, cx, y - 6, 36))
                    TextPx(r, cx, y, 14, icon.ToUpperInvariant(), White);
                cx += 42;
                TextPx(r, cx, y - FooterLift(label), 20, label, _tab == TopTab.Downloads && (label == "Pause" || label == "Resume" || label == "Cancel" || label == "Files") ? White : Muted);
                cx += UiFont.MeasurePx(20, label) + 18;
            }
        }

        void DrawSettingsOverlay(IntPtr r)
        {
            DrawHeader(r);
            string[] pages = { "General", "Connections", "Appearance", "Storage" };
            int[] ids = { 0, 1, 3, 4 };
            var tabGroup = new SDL_Rect { x = (W - 904) / 2, y = 140, w = 904, h = 56 };
            SoftRect(r, tabGroup, C(29, 29, 29)); StrokeRect(r, tabGroup, Border, 1);
            GamepadIcons.Draw(r, "l1", tabGroup.x - 60, tabGroup.y + 10, 36);
            GamepadIcons.Draw(r, "r1", tabGroup.x + tabGroup.w + 24, tabGroup.y + 10, 36);
            for (int i = 0; i < pages.Length; i++)
            {
                bool on = _settingsPage == ids[i] || (_settingsPage == 2 && i == 1);
                FilterChip(r, tabGroup.x + 4 + i * 224, tabGroup.y + 4, 220, pages[i], on);
            }
            var sheet = new SDL_Rect { x = 210, y = 222, w = 1500, h = 714 };
            if (_settingsPage == 1) DrawSettingsUnlockPage(r, sheet);
            else if (_settingsPage == 2) DrawSettingsPackageSourcesPage(r, sheet);
            else if (_settingsPage == 3) DrawSettingsAppearancePage(r, sheet);
            else if (_settingsPage == 4) DrawStorage(r, sheet);
            else DrawSettingsGeneralPage(r, sheet);
            DrawSourceInstallNotification(r, sheet);
            if (_softKbOpen && (_softKbForProxy || _softKbForDeepbrid || _softKbForAllDebrid || _softKbForTorBox || _softKbForSourceUrl))
            {
                string draft = _softKbForSourceUrl ? _sourceUrlDraft : (_softKbForDeepbrid ? _deepbridDraft : (_softKbForAllDebrid ? _allDebridDraft : (_softKbForTorBox ? _torBoxDraft : _proxyDraft)));
                DrawSoftKeyboardModal(r, _softKbForSourceUrl ? "Install package source" : "Connection settings", draft, "R2 SAVE");
            }
        }

        void DrawSettingsUnlockPage(IntPtr r, SDL_Rect sheet)
        {
            var left = new SDL_Rect { x = sheet.x, y = sheet.y + 16, w = 480, h = 640 };
            SoftRect(r, left, C(29, 29, 29)); StrokeRect(r, left, Border, 1);
            TextCentered(r, new SDL_Rect { x = left.x, y = left.y + 30, w = left.w, h = 44 }, 28, "Connect your phone", White);
            TextCentered(r, new SDL_Rect { x = left.x, y = left.y + 86, w = left.w, h = 34 }, 18, "Services · Sources · Appearance", Muted);
            if (_qr != null && _qrSize > 0)
            {
                int module = Math.Max(2, Math.Min(8, 296 / (_qrSize + 8))), total = (_qrSize + 8) * module;
                int x = left.x + (left.w - total) / 2, y = left.y + 163;
                Fill(r, x, y, total, total, White);
                QrCode.Draw(r, _qr, _qrSize, x + 4 * module, y + 4 * module, module, 0, 0, 0);
            }
            else
            {
                DesignIcon(r, "phone", left.x + 214, left.y + 232, 52, Muted);
                TextCentered(r, new SDL_Rect { x = left.x + 20, y = left.y + 330, w = left.w - 40, h = 60 }, 18, _pairUiStage == PairUiStage.Waiting ? "Starting local pairing…" : "Press SQUARE to start pairing", Muted);
            }
            TextCentered(r, new SDL_Rect { x = left.x + 20, y = left.y + 500, w = left.w - 40, h = 34 }, 18, "Scan on the same Wi-Fi or wired network.", Muted);
            TextCentered(r, new SDL_Rect { x = left.x + 20, y = left.y + 540, w = left.w - 40, h = 34 }, 17, "Your settings stay on this PS4.", Dim);
            int rx = sheet.x + 524, rw = sheet.w - 524;
            TextPx(r, rx, left.y + 8, 28, "Your connections", White);
            DrawSettingsRow(r, rx, left.y + 72, rw, 88, 0, "Real-Debrid", _cfg.HasRealDebrid ? "API key saved" : "Link from your phone", _cfg.UnlockProviderId == UnlockProviders.RealDebridId && _cfg.HasActiveUnlock ? "Active" : _cfg.HasRealDebrid ? "Select" : "Set up");
            DrawSettingsRow(r, rx, left.y + 174, rw, 88, 1, "TorBox", _cfg.HasTorBox ? "API key saved" : "Link from your phone", _cfg.UnlockProviderId == UnlockProviders.TorBoxId && _cfg.HasActiveUnlock ? "Active" : _cfg.HasTorBox ? "Select" : "Set up");
            DrawSettingsRow(r, rx, left.y + 276, rw, 88, 2, "Direct links", "Use the package source URL directly", !_cfg.UseUnlockProvider || _cfg.UnlockProviderId == UnlockProviders.NoneId ? "Active" : "Select");
            TextPx(r, rx, left.y + 405, 24, "Package sources", White);
            int enabled = 0; foreach (var source in _sourceUi) if (source.Enabled) enabled++;
            DrawSettingsRow(r, rx, left.y + 457, rw, 88, 3, "Manage sources", enabled + " of " + _sourceUi.Count + " enabled", "Open");
            TextFit(r, rx, left.y + 587, 18, rw, "Both service keys stay saved when you switch.", Dim);
        }

        void DrawSettingsPackageSourcesPage(IntPtr r, SDL_Rect sheet)
        {
            int x = sheet.x, w = sheet.w;
            TextPx(r, x, sheet.y + 15, 28, "Package sources", White);
            TextPx(r, x, sheet.y + 57, 18, "Choose which catalogs appear in search.", Muted);
            DrawSettingsRow(r, x, sheet.y + 112, w, 86, 0, "Add a source", "Scan the QR code and add a source link from your phone", "Connect");
            int start = Math.Max(0, _settingsFocus - 5);
            for (int i = 0; i < 5 && start + i < _sourceUi.Count; i++)
            {
                int n = start + i; var source = _sourceUi[n];
                DrawSettingsRow(r, x, sheet.y + 218 + i * 92, w, 80, n + 1, source.Name,
                    "Version " + source.Version + " · " + (source.Enabled ? "Included in search" : "Paused"), source.Enabled ? "ON" : "OFF");
            }
            if (_sourceUi.Count == 0)
            {
                DesignIcon(r, "library", x + w / 2 - 20, sheet.y + 300, 40, Dim);
                TextCentered(r, new SDL_Rect { x = x, y = sheet.y + 368, w = w, h = 48 }, 28, "No sources yet", White);
                TextCentered(r, new SDL_Rect { x = x, y = sheet.y + 425, w = w, h = 34 }, 18, "Add your first source to search its catalog.", Muted);
            }
        }

        void DrawSettingsGeneralPage(IntPtr r, SDL_Rect sheet)
        {
            int x = sheet.x, w = sheet.w, y = sheet.y + 18;
            TextPx(r, x, y, 27, "Downloads and compatibility", White);
            TextPx(r, x, y + 45, 19, "PS4 " + _firmwareVersion + " · System background downloads enabled", Muted);
            y += 100;
            DrawSettingsRow(r, x, y, w, 83, 0, "Bandwidth limit", "LEFT / RIGHT adjusts the limit", _cfg.DownloadLimitMBps == 0 ? "Unlimited" : _cfg.DownloadLimitMBps + " MB/s");
            DrawSettingsRow(r, x, y + 97, w, 83, 1, "Download statistics", "Size, speed and estimated time", DlStatsLabel(_cfg.DownloadStatsMode));
            DrawSettingsRow(r, x, y + 194, w, 83, 2, "Stats for nerds", "Show a speed graph on the selected download", _cfg.NerdStats ? "ON" : "OFF");
            DrawSettingsRow(r, x, y + 291, w, 83, 3, "Firmware and backport hints", "Show package requirements when available", _cfg.ShowFirmwareHints ? "ON" : "OFF");
            DrawSettingsRow(r, x, y + 388, w, 83, 4, "Clear removable history", "Installed content and retry files stay protected", "CLEAR");
            DrawSettingsSave(r, sheet, 5);
        }

        void DrawSettingsAppearancePage(IntPtr r, SDL_Rect sheet)
        {
            int x = sheet.x, w = sheet.w, y = sheet.y + 18;
            TextPx(r, x, y, 27, "Appearance", White);
            TextPx(r, x, y + 45, 19, "Personalize your console. Changes save automatically.", Muted);
            DrawSettingsRow(r, x, y + 100, w, 83, 0, "Background pattern", "LEFT / RIGHT to change", BackdropPattern.Names[BackdropPattern.Index(_cfg.BackgroundMode)]);
            DrawSettingsRow(r, x, y + 197, w, 83, 1, "Reduced motion", "Static focus and immediate transitions", _cfg.ReduceMotion ? "ON" : "OFF");
            TextPx(r, x, y + 311, 22, "Accent", White);
            int n = ThemePalette.Presets.Count, columns = 4, cw = (w - 42) / columns;
            for (int i = 0; i < n; i++)
            {
                var preset = ThemePalette.Presets[i]; int cy = y + 359 + (i / columns) * 80, cx = x + (i % columns) * (cw + 14);
                DesignCard(r, new SDL_Rect { x = cx, y = cy, w = cw, h = 64 }, _settingsFocus == i + 2);
                Fill(r, cx + 20, cy + 21, 22, 22, C(preset.Color.R, preset.Color.G, preset.Color.B));
                TextFit(r, cx + 57, cy + 18, 19, cw - 95, preset.Name, White);
                if (string.Equals(_cfg.Accent, preset.Hex, StringComparison.OrdinalIgnoreCase)) DesignIcon(r, "check", cx + cw - 33, cy + 24, 17, White);
            }
            DrawSettingsRow(r, x, sheet.y + 632, w, 66, n + 2, "Restore charcoal", "Restore visual defaults", "Restore");
        }

        void DrawSettingsRow(IntPtr r, int x, int y, int w, int h, int index, string title, string body, string value)
        {
            var rect = new SDL_Rect { x = x, y = y, w = w, h = h };
            bool focused = index >= 0 && _settingsFocus == index;
            bool danger = string.Equals(value, "CLEAR", StringComparison.OrdinalIgnoreCase);
            DesignCard(r, rect, focused);
            bool toggle = value == "ON" || value == "OFF";
            int valueWidth = toggle ? 90 : Math.Min(w / 3, UiFont.MeasurePx(19, value ?? "") + 65);
            int available = Math.Max(30, w - 54 - valueWidth);
            int titlePx = h < 76 ? 21 : 23;
            TextFit(r, x + 22, y + (string.IsNullOrEmpty(body) ? (h - 30) / 2 : 11), titlePx, available, title, danger ? Danger : White);
            if (!string.IsNullOrEmpty(body)) TextFit(r, x + 22, y + h - 33, 17, available, body, Muted);
            if (toggle) DrawSwitch(r, x + w - 76, y + (h - 28) / 2, value == "ON");
            else
            {
                string shown = UiFont.EllipsizePx(value ?? "", 19, valueWidth - 34);
                TextPx(r, x + w - 24 - UiFont.MeasurePx(19, shown), y + (h - 27) / 2, 19, shown, string.Equals(value, "Active", StringComparison.OrdinalIgnoreCase) ? Ok : focused ? White : Muted);
            }
        }

        void SoftKbAppend(char ch)
        {
            _typingPulseAt = UiTick();
            int maxLen = _softKbForSourceUrl ? 256 :
                ((_softKbForProxy || _softKbForDeepbrid || _softKbForAllDebrid || _softKbForTorBox) ? 96 : MaxQuery);
            if (_softKbForSourceUrl)
            {
                if (_sourceUrlDraft.Length < maxLen) _sourceUrlDraft += ch;
            }
            else if (_softKbForDeepbrid)
            {
                if (_deepbridDraft.Length < maxLen) _deepbridDraft += ch;
            }
            else if (_softKbForAllDebrid)
            {
                if (_allDebridDraft.Length < maxLen) _allDebridDraft += ch;
            }
            else if (_softKbForTorBox)
            {
                if (_torBoxDraft.Length < maxLen) _torBoxDraft += ch;
            }
            else if (_softKbForProxy)
            {
                if (_proxyDraft.Length < maxLen) _proxyDraft += ch;
            }
            else if (_query.Length < maxLen)
            {
                _query += ch;
                RefreshKeyboardSuggestions();
            }
        }

        void DrawSourceInstallNotification(IntPtr r, SDL_Rect sheet)
        {
            var view = GetSourceInstallView(); if (view.Stage == SourceInstallStage.Idle) return;
            uint terminalAge = view.TerminalAt == 0 ? 0 : UiElapsed(view.TerminalAt);
            const uint hold = 4400, exit = 160;
            if (view.TerminalAt != 0 && terminalAge >= hold + exit)
            {
                lock (_lock) { if (_sourceInstallTerminalAt == view.TerminalAt) { _sourceInstallStage = SourceInstallStage.Idle; _sourceInstallTerminalAt = 0; } }
                return;
            }
            bool active = SourceInstallActive(view.Stage);
            SDL_Color tone = view.Stage == SourceInstallStage.Failed ? Danger : view.Stage == SourceInstallStage.Complete ? Ok : Accent;
            int offset = 0;
            if (!_cfg.ReduceMotion)
            {
                uint age = UiElapsed(view.ShownAt);
                if (age < 160) offset = (int)(35 * Math.Pow(1 - age / 160.0, 3));
                if (terminalAge > hold) offset = (int)(600 * Math.Pow((terminalAge - hold) / (double)exit, 2));
            }
            var card = new SDL_Rect { x = W - 588 + offset, y = 800, w = 540, h = 148 };
            SoftRect(r, new SDL_Rect { x = card.x + 4, y = card.y + 6, w = card.w, h = card.h }, Shadow);
            DesignCard(r, card, false);
            DesignIcon(r, active ? "download" : view.Stage == SourceInstallStage.Complete ? "check" : "error", card.x + 24, card.y + 30, 30, tone);
            TextFit(r, card.x + 76, card.y + 22, 23, card.w - 100, active ? "Adding source" : view.Stage == SourceInstallStage.Complete ? "Source installed" : "Source couldn't be added", White);
            TextWrapped(r, card.x + 76, card.y + 62, 17, card.w - 100, view.Detail ?? view.Target ?? "", Muted);
            var track = new SDL_Rect { x = card.x + 12, y = card.y + card.h - 5, w = card.w - 24, h = 2 };
            if (active && view.Total <= 0) DrawActivityRail(r, track);
            else {
                Fill(r, track.x, track.y, track.w, track.h, Border);
                double fraction = active ? Math.Min(1, view.Done / (double)Math.Max(1, view.Total)) : Math.Max(0, 1 - terminalAge / (double)hold);
                Fill(r, track.x, track.y, (int)(track.w * fraction), track.h, tone);
            }
        }

        static string SourceInstallTitle(SourceInstallStage stage)
        {
            switch (stage)
            {
                case SourceInstallStage.Connecting: return "Connecting to Package Source";
                case SourceInstallStage.Downloading: return "Downloading Package Source";
                case SourceInstallStage.Validating: return "Validating Package Source";
                case SourceInstallStage.Installing: return "Installing Package Source";
                case SourceInstallStage.Complete: return "Package Source installed";
                case SourceInstallStage.Failed: return "Package Source install failed";
                default: return "Package Source";
            }
        }

        static string SourceInstallPill(SourceInstallView view)
        {
            if (view == null) return "SOURCE";
            switch (view.Stage)
            {
                case SourceInstallStage.Connecting: return "CONNECT";
                case SourceInstallStage.Downloading:
                    if (view.Total > 0)
                        return Math.Min(100, (int)(view.Done * 100L / Math.Max(1L, view.Total))) + "%";
                    return "RECEIVE";
                case SourceInstallStage.Validating: return "CHECK";
                case SourceInstallStage.Installing: return "INSTALL";
                case SourceInstallStage.Complete: return "READY";
                case SourceInstallStage.Failed: return "FAILED";
                default: return "SOURCE";
            }
        }

        void DrawSearch(IntPtr r)
        {
            DrawCharcoalSearch(r);
        }

        void DrawSearchHero(IntPtr r, int x, int y, int w, int h, int continueFocusBase)
        {
            var frame = new SDL_Rect { x = x, y = y, w = w, h = h };
            bool heroFocused = _cfg.SearchContinue && _landingContinue.Count > 0 &&
                _searchLandingFocus == continueFocusBase;
            Fill(r, frame.x + 8, frame.y + 10, frame.w, frame.h, Shadow);
            Fill(r, frame.x, frame.y, frame.w, frame.h, heroFocused ? Focused : Row);
            StrokeRect(r, frame, heroFocused ? Accent : Border, heroFocused ? 3 : 1);

            DlItem hero = null;
            if (_cfg.SearchContinue && _landingContinue.Count > 0)
                hero = _landingContinue[0];
            else
                hero = _landingPebble;

            int artX = x + 36;
            int artY = y + 36;
            int artW = w - 72;
            int artH = h - 220;
            var art = new SDL_Rect { x = artX, y = artY, w = artW, h = artH };
            if (hero != null)
            {
                DrawLandingCover(r, hero, art);
                FillAlpha(r, art.x, art.y + art.h - 160, art.w, 160, C(8, 8, 10), 180);
                TextFit(r, art.x + 20, art.y + art.h - 132, 28, art.w - 40, hero.Name ?? hero.TitleId, White);
                TextPx(r, art.x + 20, art.y + art.h - 90, 18, hero.TitleId ?? "", Muted);
                string state = StateLabel(hero);
                if (hero.Total > 0 && hero.State != DlState.Completed && hero.State != DlState.Installed)
                    state = Math.Min(100, (int)(hero.Done * 100.0 / Math.Max(1.0, hero.Total))) + "%  ·  " + state;
                DrawPill(r, art.x + 20, art.y + art.h - 56, state, Accent);
            }
            else
            {
                SoftRect(r, art, Raised);
                TextCentered(r, new SDL_Rect { x = art.x, y = art.y + art.h / 2 - 40, w = art.w, h = 36 },
                    24, "Ready to search", White);
                TextCentered(r, new SDL_Rect { x = art.x + 24, y = art.y + art.h / 2 + 4, w = art.w - 48, h = 28 },
                    17, EnabledSourceName(), Muted);
            }

            TextPx(r, x + 36, y + h - 56, 15, "FEATURED", Dim);
            TextFit(r, x + 36, y + h - 32, 18, w - 72,
                hero == null ? "Nothing in progress" : (hero.Name ?? "Now playing"), Muted);
        }

        void DrawSearchAtmosphere(IntPtr r)
        {
            if (!UsesImageBackground() &&
                !string.Equals(_cfg.BackgroundMode, AppSettings.BackgroundBlack,
                    StringComparison.OrdinalIgnoreCase))
            {
                ThemeColor theme = ThemePalette.Accent(_cfg);
                FillAlpha(r, 0, 100, W, 872, C(theme.R, theme.G, theme.B),
                    string.Equals(_cfg.BackgroundMode, AppSettings.BackgroundAdaptive,
                        StringComparison.OrdinalIgnoreCase) ? (byte)24 : (byte)18);
            }
        }

        void DrawLandingCover(IntPtr r, DlItem item, SDL_Rect destination)
        {
            SoftRect(r, new SDL_Rect { x = destination.x + 3, y = destination.y + 4,
                w = destination.w, h = destination.h }, Shadow);
            SoftRect(r, destination, Raised);
            IntPtr texture;
            int tw, th;
            string titleId = item == null ? "" : item.TitleId;
            if (_covers.TryGet(titleId, out texture, out tw, out th) && tw > 0 && th > 0)
            {
                var dest = destination;
                SDL_RenderCopy(r, texture, IntPtr.Zero, ref dest);
            }
            else
            {
                if (item != null && !string.IsNullOrEmpty(item.ImageUrl))
                {
                    // Bounded request ledger: a full clear only risks re-requesting
                    // a cover, which CoverCache deduplicates.
                    if (_landingCoverRequested.Count >= 2048) _landingCoverRequested.Clear();
                    if (_landingCoverRequested.Add((item.TitleId ?? "") + "\n" + item.ImageUrl))
                        _covers.Request(item.TitleId, item.ImageUrl);
                }
                TextCentered(r, destination, 11, titleId, Dim);
            }
        }

        void DrawDownloadPebble(IntPtr r, int colX, int colW, int y)
        {
            DlItem item = _landingPebble;
            if (item == null) return;
            if (y + 72 > 960) y = 888;
            int pct = item.Total > 0
                ? Math.Min(100, (int)(item.Done * 100.0 / Math.Max(1.0, item.Total))) : 0;
            var pebble = new SDL_Rect { x = colX, y = y, w = colW, h = 68 };
            int pebbleFocus = 1 + _libraryGames.Count +
                (_cfg.SearchContinue ? _landingContinue.Count : 0);
            bool focused = _searchLandingFocus == pebbleFocus;
            Fill(r, pebble.x, pebble.y, pebble.w, pebble.h, focused ? Focused : Row);
            StrokeRect(r, pebble, focused ? Accent : Border, focused ? 2 : 1);
            DrawLandingCover(r, item, new SDL_Rect { x = pebble.x + 14, y = pebble.y + 10, w = 36, h = 48 });
            TextFit(r, pebble.x + 64, pebble.y + 10, 18, colW - 280,
                item.Name ?? item.TitleId, White);
            string rate = item.BytesPerSec > 0
                ? (item.BytesPerSec / (1024.0 * 1024.0)).ToString("0.0") + " MB/s"
                : StateLabel(item);
            TextPx(r, pebble.x + 64, pebble.y + 36, 15, rate + "  ·  " + pct + "%", Muted);
            int trackX = pebble.x + colW - 220;
            Fill(r, trackX, pebble.y + 30, 188, 6, Raised);
            if (pct > 0) Fill(r, trackX, pebble.y + 30, 188 * pct / 100, 6, Accent);
        }

        void DrawSoftKeyboardModal(IntPtr r, string label, string value, string action)
        {
            Fill(r, 0, 106, W, 880, Bg);
            TextPx(r, ContentX, 152, 32, label, White);
            TextPx(r, ContentX + ContentWidth - 220, 162, 18, _kbLower ? "L2  Lowercase" : "L2  Uppercase", Muted);
            var input = new SDL_Rect { x = ContentX, y = 217, w = ContentWidth, h = 82 };
            DesignCard(r, input, true);
            DesignIcon(r, "search", input.x + 26, input.y + 28, 26, Muted);
            string cursor = _cfg.ReduceMotion || ((_frameTime / 440) % 2) == 0 ? "|" : "";
            TextFit(r, input.x + 72, input.y + 20, 28, input.w - 100, value + cursor, White);
            if (_typingPulseAt != 0 && UiElapsed(_typingPulseAt) < 160 && !_cfg.ReduceMotion)
                Fill(r, input.x + 18, input.y + input.h - 3, input.w - 36, 2, Accent);
            if (_kbSuggestionCount > 0)
            {
                int sw = (ContentWidth - 36) / 4;
                for (int i = 0; i < _kbSuggestionCount; i++)
                {
                    var box = new SDL_Rect { x = ContentX + i * (sw + 12), y = 321, w = sw, h = 56 };
                    bool on = _kbSuggestionFocus == i; DesignCard(r, box, on);
                    TextCentered(r, box, 18, UiFont.EllipsizePx(_kbSuggestions[i].Name, 18, sw - 30), on ? White : Muted);
                }
            }
            DrawSoftKeyboard(r, 395);
        }

        void DrawSoftKeyboard(IntPtr r, int top)
        {
            const int keyH = 64, gap = 12;
            for (int row = 0; row < 4; row++)
            {
                string keys = KbRows[row]; int keyW = row == 3 ? 84 : 112;
                int rowW = keys.Length * (keyW + gap) - gap, startX = (W - rowW) / 2;
                for (int col = 0; col < keys.Length; col++)
                {
                    bool on = _kbSuggestionFocus < 0 && _kbRow == row && _kbCol == col;
                    bool pressed = _kbPressedRow == row && _kbPressedCol == col && UiElapsed(_kbPressedAt) < 140;
                    int lift = _cfg.ReduceMotion ? 0 : pressed ? -2 : FocusLift(on);
                    var box = new SDL_Rect { x = startX + col * (keyW + gap), y = top + row * (keyH + gap) - lift, w = keyW, h = keyH };
                    SoftRect(r, box, on || pressed ? PrimaryFill : Row);
                    if (on) StrokeRect(r, new SDL_Rect { x = box.x - 4, y = box.y - 4, w = box.w + 8, h = box.h + 8 }, White, 1);
                    char ch = keys[col]; if (_kbLower && char.IsLetter(ch)) ch = char.ToLowerInvariant(ch);
                    TextCentered(r, box, 29, ch.ToString(), on || pressed ? PrimaryInk : White);
                }
            }
            string[] special = { "Space", "Delete", "Clear", _kbLower ? "ABC" : "abc", (_softKbForProxy || _softKbForDeepbrid || _softKbForAllDebrid || _softKbForTorBox) ? "Save" : _softKbForSourceUrl ? "Install" : "Search" };
            const int sw = 244; int sx = (W - (5 * (sw + gap) - gap)) / 2;
            for (int i = 0; i < 5; i++)
            {
                bool on = _kbSuggestionFocus < 0 && _kbRow == 4 && _kbCol == i;
                var box = new SDL_Rect { x = sx + i * (sw + gap), y = top + 322 - FocusLift(on), w = sw, h = 70 };
                SoftRect(r, box, on || i == 4 ? PrimaryFill : Row);
                if (on) StrokeRect(r, new SDL_Rect { x = box.x - 4, y = box.y - 4, w = box.w + 8, h = box.h + 8 }, White, 1);
                TextCentered(r, box, 22, special[i], on || i == 4 ? PrimaryInk : White);
            }
        }

        void DrawResults(IntPtr r)
        {
            DrawCharcoalResults(r);
        }

        static string SourceName(string source)
        {
            return string.IsNullOrWhiteSpace(source) ? "Package Source" : source;
        }

        static string SearchSubtitle(GameHit hit)
        {
            var parts = new List<string>
            {
                hit.TitleId ?? "",
                hit.Region ?? "?",
                SourceName(hit.Source)
            };
            if (!string.IsNullOrWhiteSpace(hit.Rating)) parts.Add("Rating " + hit.Rating.Trim());
            if (!string.IsNullOrWhiteSpace(hit.Genres)) parts.Add(hit.Genres.Trim());
            string backport = BackportLabel(hit.Backport);
            if (backport.Length > 0) parts.Add(backport);
            return string.Join("  ·  ", parts.ToArray());
        }

        static string BackportLabel(string value)
        {
            string text = (value ?? "").Trim();
            if (text.Length == 0 || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "no", StringComparison.OrdinalIgnoreCase) || text == "0") return "";
            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "yes", StringComparison.OrdinalIgnoreCase) || text == "1") return "BP";
            return text.StartsWith("BP", StringComparison.OrdinalIgnoreCase) ? text : "BP " + text;
        }

        void PrefetchSearchCovers(IList<GameHit> hits)
        {
            if (hits == null) return;
            int count = Math.Min(6, hits.Count);
            for (int i = 0; i < count; i++)
            {
                GameHit hit = hits[i];
                if (hit != null && !string.IsNullOrEmpty(hit.ImageUrl))
                    _covers.Request(hit.TitleId, hit.ImageUrl);
            }
        }

        void DrawSearchSkeleton(IntPtr r)
        {
            const int contentW = 1600;
            const int contentX = (W - contentW) / 2;
            const int rowH = 100;
            int listTop = 200;
            for (int row = 0; row < VisibleRows; row++)
            {
                int y = listTop + row * rowH;
                var card = new SDL_Rect { x = contentX, y = y, w = contentW, h = rowH - 8 };
                Card(r, card, row == 0, Row, row == 0 ? Accent : Border);
                var cover = new SDL_Rect { x = contentX + 16, y = y + 8, w = 76, h = 76 };
                SoftRect(r, cover, Raised);
                DrawShimmer(r, cover);
                DrawShimmerBar(r, contentX + 110, y + 18, 520 + (row % 3) * 40, 22);
                DrawShimmerBar(r, contentX + 110, y + 52, 220, 16);
            }
            TextCentered(r, new SDL_Rect { x = 0, y = 130, w = W, h = 36 }, 22,
                "Searching" + new string('.', (int)((_frameTime / 350) % 4)), White);
        }

        void DrawSearchingPanel(IntPtr r, SDL_Rect panel, string title, string subtitle)
        {
            // No logo here — header already has branding. Right panel = pure activity chrome.
            int cx = panel.x + panel.w / 2;
            int cy = panel.y + 300;

            // Concentric pulse rings
            int pulse = (int)((_frameTime / 6) % 48);
            for (int i = 0; i < 3; i++)
            {
                int ring = 90 + i * 48 + pulse / (i + 1);
                var rr = new SDL_Rect { x = cx - ring / 2, y = cy - ring / 2, w = ring, h = ring };
                StrokeRect(r, rr, i == 0 ? Accent : (i == 1 ? Border : Raised), i == 0 ? 3 : 2);
            }

            // Center spinner ticks
            SoftRect(r, new SDL_Rect { x = cx - 28, y = cy - 28, w = 56, h = 56 }, Raised);
            int tick = (int)((_frameTime / 80) % 8);
            for (int i = 0; i < 8; i++)
            {
                double ang = (i * 45 + tick * 12) * Math.PI / 180.0;
                int tx = cx + (int)(Math.Cos(ang) * 18) - 3;
                int ty = cy + (int)(Math.Sin(ang) * 18) - 3;
                Fill(r, tx, ty, 6, 6, i == tick ? FocusCyan : Dim);
            }

            int dots = 0;
            string anim = new string('.', dots);
            TextCentered(r, new SDL_Rect { x = panel.x + 24, y = panel.y + 480, w = panel.w - 48, h = 36 },
                26, (title ?? "Working") + anim, White);
            if (!string.IsNullOrEmpty(subtitle))
                TextCentered(r, new SDL_Rect { x = panel.x + 24, y = panel.y + 528, w = panel.w - 48, h = 28 },
                    18, Clip(subtitle, 36), Muted);

            // Indeterminate bar
            var track = new SDL_Rect { x = panel.x + 80, y = panel.y + 600, w = panel.w - 160, h = 10 };
            SoftRect(r, track, Raised);
            int seg = 140;
            int bx = track.x + (int)((_frameTime / 4) % (uint)Math.Max(1, track.w + seg)) - seg;
            int x0 = Math.Max(track.x, bx);
            int x1 = Math.Min(track.x + track.w, bx + seg);
            if (x1 > x0) Fill(r, x0, track.y, x1 - x0, track.h, Accent);
        }

        void DrawShimmer(IntPtr r, SDL_Rect rect)
        {
            SoftRect(r, rect, Raised);
        }

        void DrawShimmerBar(IntPtr r, int x, int y, int w, int h)
        {
            var rect = new SDL_Rect { x = x, y = y, w = w, h = h };
            SoftRect(r, rect, Raised);
            DrawShimmer(r, rect);
        }

        void EnsureWordmark(IntPtr renderer)
        {
            if (_wordTried || renderer == IntPtr.Zero) return;
            _wordTried = true;
            try
            {
                string bas;
                try { bas = (Orbis.Internals.IO.GetAppBaseDirectory() ?? ".").TrimEnd('/', '\\'); }
                catch { bas = "."; }
                string[] paths =
                {
                    "/app0/assets/images/sspi-wordmark.png",
                    bas + "/assets/images/sspi-wordmark.png",
                    bas + "\\assets\\images\\sspi-wordmark.png",
                    "assets/images/sspi-wordmark.png"
                };
                foreach (string path in paths)
                {
                    if (!File.Exists(path)) continue;
                    byte[] bytes = File.ReadAllBytes(path);
                    if (bytes.Length < 32) continue;
                    using (var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(bytes))
                    {
                        if (image.Width < 8 || image.Height < 8) continue;
                        // Trim empty margins so aspect matches the real text, then scale crisply.
                        int x0, y0, x1, y1;
                        if (!TryContentBounds(image, out x0, out y0, out x1, out y1))
                        {
                            x0 = 0; y0 = 0; x1 = image.Width - 1; y1 = image.Height - 1;
                        }
                        int cw = Math.Max(8, x1 - x0 + 1);
                        int ch = Math.Max(8, y1 - y0 + 1);
                        // Target ~height 128 for sharp downscale to ~52px on screen.
                        int th = 128;
                        int tw = Math.Max(64, cw * th / ch);
                        if (tw > 2048) { tw = 2048; th = Math.Max(48, ch * tw / cw); }
                        using (var cropped = image.Clone(ctx =>
                        {
                            ctx.Crop(new SixLabors.ImageSharp.Rectangle(x0, y0, cw, ch));
                            ctx.Resize(tw, th, SixLabors.ImageSharp.Processing.KnownResamplers.Lanczos3);
                        }))
                        {
                            byte[] rgba = new byte[checked(cropped.Width * cropped.Height * 4)];
                            cropped.CopyPixelDataTo(rgba);
                            IntPtr tex = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ABGR8888,
                                (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STATIC, cropped.Width, cropped.Height);
                            if (tex == IntPtr.Zero) continue;
                            var pin = System.Runtime.InteropServices.GCHandle.Alloc(rgba,
                                System.Runtime.InteropServices.GCHandleType.Pinned);
                            try
                            {
                                if (SDL_UpdateTexture(tex, IntPtr.Zero, pin.AddrOfPinnedObject(), cropped.Width * 4) != 0)
                                {
                                    SDL_DestroyTexture(tex);
                                    continue;
                                }
                            }
                            finally { pin.Free(); }
                            SDL_SetTextureBlendMode(tex, SDL_BlendMode.SDL_BLENDMODE_BLEND);
                            try { SDL_SetHint(SDL_HINT_RENDER_SCALE_QUALITY, "1"); } catch { }
                            _wordTex = tex;
                            _wordW = cropped.Width;
                            _wordH = cropped.Height;
                            return;
                        }
                    }
                }
            }
            catch { }
        }

        static bool TryContentBounds(Image<Rgba32> image,
            out int x0, out int y0, out int x1, out int y1)
        {
            int minX = image.Width, minY = image.Height, maxX = 0, maxY = 0;
            bool any = false;
            for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                Rgba32 p = image[x, y];
                if (p.A < 24) continue;
                if (p.R < 18 && p.G < 18 && p.B < 18) continue;
                any = true;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
            if (!any)
            {
                x0 = y0 = x1 = y1 = 0;
                return false;
            }
            x0 = Math.Max(0, minX - 4);
            y0 = Math.Max(0, minY - 4);
            x1 = Math.Min(image.Width - 1, maxX + 4);
            y1 = Math.Min(image.Height - 1, maxY + 4);
            return true;
        }

        void DrawDetail(IntPtr r)
        {
            DrawCharcoalDetail(r);
        }

        void DrawResolveSkeleton(IntPtr r, SDL_Rect panel)
        {
            var track = new SDL_Rect { x = panel.x + 32, y = panel.y + 64, w = panel.w - 64, h = 6 };
            SoftRect(r, track, Raised);
            int segment = 180;
            int bx = track.x + (int)((_frameTime / 4) % (uint)(track.w + segment)) - segment;
            Fill(r, Math.Max(track.x, bx), track.y,
                Math.Max(0, Math.Min(track.x + track.w, bx + segment) - Math.Max(track.x, bx)), track.h, Accent);
            TextPx(r, panel.x + 32, panel.y + 88, 18,
                "Checking " + SourceName(_selected.Source) + " · mirrors", Muted);
            TextPx(r, panel.x + 32, panel.y + 116, 16, _selected.TitleId, Dim);
            for (int i = 0; i < 5; i++)
            {
                int y = panel.y + 160 + i * 102;
                var row = new SDL_Rect { x = panel.x + 24, y = y, w = panel.w - 48, h = 84 };
                SoftRect(r, row, Raised);
                DrawShimmerBar(r, row.x + 24, row.y + 18, 320 + i * 34, 18);
                DrawShimmerBar(r, row.x + 24, row.y + 50, 210, 13);
            }
        }

        string PackageSizeSummary()
        {
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int bases = 0, updates = 0, dlc = 0, backports = 0;
            foreach (var meta in _linkPresentation)
            {
                if (!groups.Add(DetailGroupKey(meta))) continue;
                int kind = PackageKindOrder(meta.Kind);
                if (kind == 0) bases++; else if (kind == 1) updates++; else if (kind == 2) dlc++; else backports++;
            }
            return bases + " base · " + updates + " updates · " + dlc + " DLC" + (backports > 0 ? " · " + backports + " other" : "");
        }

        bool RecommendedAlreadyQueued()
        {
            List<DlItem> items = _dlMgr.Snapshot();
            int baseIndex = PreferredCandidate(_linkCandidates, "base", false);
            int updateIndex = PreferredCandidate(_linkCandidates, "update", true);
            if (baseIndex < 0 && updateIndex < 0) return false;
            return (baseIndex < 0 || IsCandidateQueued(_linkCandidates[baseIndex], items)) &&
                (updateIndex < 0 || IsCandidateQueued(_linkCandidates[updateIndex], items));
        }

        static bool IsCandidateQueued(PackageCandidate candidate, IList<DlItem> items)
        {
            if (candidate == null || items == null) return false;
            for (int i = 0; i < items.Count; i++)
                if (string.Equals(items[i].CandidateId, candidate.CandidateId, StringComparison.Ordinal) &&
                    string.Equals(items[i].SourceId, candidate.SourceId, StringComparison.Ordinal)) return true;
            return false;
        }

        void DrawDownloads(IntPtr r)
        {
            DrawRefinedDownloads(r);
        }

        void DrawFocusedSparkline(IntPtr r, SDL_Rect rect)
        {
            Fill(r, rect.x, rect.y, rect.w, rect.h, C(12, 13, 16));
            NerdTelemetry n = _dlMgr.Nerd;
            int samples = Math.Min(n.Count, 64);
            double peak = 1;
            for (int i = 0; i < samples; i++) peak = Math.Max(peak, n.SampleAt(i));
            int step = Math.Max(2, (rect.w - 150) / Math.Max(1, samples));
            for (int i = 0; i < samples; i++)
            {
                int h = (int)Math.Max(1, n.SampleAt(samples - i - 1) * (rect.h - 6) / peak);
                Fill(r, rect.x + i * step, rect.y + rect.h - h, Math.Max(1, step - 1), h, Accent);
            }
            string speed = n.MbpsInstant.ToString("0.0") + " Mbps";
            TextPx(r, rect.x + rect.w - UiFont.MeasurePx(15, speed) - 12, rect.y + 10, 15, speed, Muted);
        }

        void DrawNerdTerminal(IntPtr r, int top, int height)
        {
            if (height < 140) return;
            // Clamp so nothing draws into the footer band.
            if (top + height > 960) height = 960 - top;
            if (height < 140) return;

            var panel = new SDL_Rect { x = 96, y = top, w = 1728, h = height };
            Fill(r, panel.x + 4, panel.y + 5, panel.w, panel.h, Shadow);
            Fill(r, panel.x, panel.y, panel.w, panel.h, C(8, 10, 12));
            StrokeRect(r, panel, Border, 2);
            Fill(r, panel.x, panel.y, 6, panel.h, Accent);

            var n = _dlMgr.Nerd;
            DrawSectionLabel(r, panel.x + 24, panel.y + 12, "STATS FOR NERDS", Accent);
            TextPx(r, panel.x + 280, panel.y + 14, 15, "no tokens · hostnames only", Dim);

            // Graph left — height scales with panel so bars never clip.
            int pad = 16;
            int gx = panel.x + 24;
            int gy = panel.y + 44;
            int gw = 1020;
            int gh = Math.Max(72, height - 68);
            Fill(r, gx, gy, gw, gh, C(14, 16, 20));
            StrokeRect(r, new SDL_Rect { x = gx, y = gy, w = gw, h = gh }, Border, 1);
            double peak = 1;
            for (int i = 0; i < n.Count; i++)
            {
                double v = n.SampleAt(n.Count - 1 - i);
                if (v > peak) peak = v;
            }
            peak = Math.Max(peak, 8);
            int samples = Math.Min(n.Count, Math.Max(8, gw / 4));
            for (int i = 0; i < samples; i++)
            {
                double mbps = n.SampleAt(samples - 1 - i);
                int barH = (int)Math.Max(1, (mbps / peak) * (gh - 8));
                SDL_Color col = mbps >= n.MbpsAvg * 0.85 ? Ok :
                    (mbps >= n.MbpsAvg * 0.5 ? Warning : Danger);
                if (n.StabilityLabel == "unstable") col = Danger;
                else if (n.StabilityLabel == "jitter") col = Warning;
                int step = Math.Max(2, (gw - 8) / Math.Max(1, samples));
                int bx = gx + 4 + i * step;
                int bw = Math.Max(2, step - 1);
                Fill(r, bx, gy + gh - 4 - barH, bw, barH, col);
            }

            // Right readout — compact lines fully inside panel.
            int tx = gx + gw + 24;
            int ty = panel.y + 40;
            int line = 22;
            int maxY = panel.y + height - pad;
            SDL_Color stabCol = n.StabilityLabel == "stable" ? Ok :
                (n.StabilityLabel == "jitter" ? Warning :
                (n.StabilityLabel == "unstable" ? Danger : Dim));
            Action<string, SDL_Color, int> lineAt = (text, col, px) =>
            {
                if (ty + px > maxY) return;
                TextFit(r, tx, ty, px, panel.x + panel.w - tx - 20, text ?? "", col);
                ty += line;
            };
            lineAt(DownloadManager.FormatMbps(n.MbpsInstant) + "   " +
                   DownloadManager.FormatMBpsFromMbps(n.MbpsInstant), White, 20);
            lineAt("avg " + DownloadManager.FormatMbps(n.MbpsAvg), Muted, 16);
            lineAt("stability  " + n.StabilityLabel + "  " + ((int)(n.Stability01 * 100)) + "%", stabCol, 16);
            lineAt("stalls " + n.StallEvents + "   drops " + n.DropEvents, Muted, 16);
            lineAt("region  " + (string.IsNullOrEmpty(n.RegionHint) ? "—" : n.RegionHint), White, 16);
            lineAt("cdn  " + (string.IsNullOrEmpty(n.CdnHost) ? "—" : n.CdnHost), Muted, 15);
            lineAt("file  " + (string.IsNullOrEmpty(n.FileName) ? "—" : n.FileName), Muted, 15);
            lineAt("title  " + (string.IsNullOrEmpty(n.TitleId) ? "—" : n.TitleId), Dim, 15);
            if (n.Total > 0)
            {
                if (n.Finalizing)
                    lineAt("xfer complete  ·  finalizing", Violet, 15);
                else
                {
                    int pct = (int)Math.Min(100, n.Done * 100.0 / Math.Max(1, n.Total));
                    lineAt("xfer  " + pct + "%  ·  " + n.TransferMode, Dim, 15);
                }
            }
            else
                lineAt("xfer  " + n.TransferMode, Dim, 15);
        }

        static string PackageMix(DownloadGroup group)
        {
            int games = 0, updates = 0, dlc = 0, other = 0;
            foreach (var item in group.Items)
            {
                int order = KindOrder(item.Kind);
                if (order == 0) games++;
                else if (order == 1) updates++;
                else if (order == 2) dlc++;
                else other++;
            }
            var parts = new List<string>();
            if (games > 0) parts.Add(games + " game");
            if (updates > 0) parts.Add(updates + " update" + (updates == 1 ? "" : "s"));
            if (dlc > 0) parts.Add(dlc + " DLC");
            if (other > 0) parts.Add(other + " other");
            return string.Join("  -  ", parts.ToArray());
        }

        static void DownloadGroupProgress(DownloadGroup group, out long done, out long total)
        {
            done = 0; total = 0;
            foreach (DlItem item in group.Items)
            {
                done += Math.Max(0, item.Done);
                total += Math.Max(Math.Max(0, item.Total), Math.Max(0, item.Done));
            }
        }

        static string DownloadGroupStatus(DownloadGroup group)
        {
            int submitted = 0, installed = 0, paused = 0;
            foreach (DlItem item in group.Items)
            {
                if (item.State == DlState.Failed) return "Needs attention";
                if (Extracting(item)) return "Extracting";
                if (item.State == DlState.Installing || (item.StatusText ?? "").StartsWith("Installing packages")) return "Installing in order";
                if (item.State == DlState.Downloading) return "Downloading";
                if (item.State == DlState.Finalizing) return "Verifying files";
                if (item.State == DlState.Resolving) return "Resolving links";
                if (item.State == DlState.Installed) installed++;
                if (item.State == DlState.Submitted) submitted++;
                if (item.State == DlState.Paused) paused++;
            }
            if (installed == group.Items.Count) return "Installed";
            if (submitted > 0) return "Submitted to BGFT";
            if (paused > 0) return "Paused";
            foreach (DlItem item in group.Items)
                if (!string.IsNullOrEmpty(item.InstallAfterId)) return "Waiting for dependency";
            foreach (DlItem item in group.Items) if (item.State == DlState.Completed) return "Ready to install";
            return "Queued";
        }

        SDL_Color DownloadGroupColor(DownloadGroup group)
        {
            string state = DownloadGroupStatus(group);
            if (state.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                state.IndexOf("attention", StringComparison.OrdinalIgnoreCase) >= 0) return Danger;
            if (state == "Waiting for dependency" || state == "Waiting for base") return Warning;
            if (state == "Installed") return Ok;
            return Accent;
        }

        string GroupProgressLine(DownloadGroup group, long done, long total)
        {
            double speed = 0; int eta = 0;
            foreach (DlItem item in group.Items) { speed += Math.Max(0, item.BytesPerSec); eta = Math.Max(eta, item.EtaSeconds); }
            string sizes = total > 0 ? (DownloadManager.Human(done) + " / " + DownloadManager.Human(total)) : group.Items.Count + " packages";
            string rate = speed > 0 ? "   " + DownloadManager.Human((long)speed) + "/s" : "";
            string etaText = "";
            if (eta > 0)
            {
                etaText = _cfg.EtaFormat == AppSettings.EtaClock
                    ? string.Format("{0}:{1:00}", eta / 60, eta % 60)
                    : Math.Max(1, eta / 60) + "m";
            }
            return sizes + rate + (etaText.Length > 0 ? "   " + etaText : "");
        }

        static string DownloadPackageMetadata(DlItem item)
        {
            var parts = new List<string>();
            parts.Add(string.IsNullOrEmpty(item.Label) ? "Package" : item.Label);
            string source = !string.IsNullOrEmpty(item.SourceAttribution)
                ? item.SourceAttribution : item.SourceId;
            if (!string.IsNullOrEmpty(source))
                parts.Add("Source " + Clip(source, 30));
            if (!string.IsNullOrEmpty(item.SourceVersion))
                parts.Add("v" + Clip(item.SourceVersion, 14));
            if (!string.IsNullOrEmpty(item.StatusText))
                parts.Add(item.StatusText);
            return string.Join("  ·  ", parts.ToArray());
        }

        void EnsureDownloadArtwork(DownloadGroup group)
        {
            if (group == null || string.IsNullOrEmpty(group.TitleId) || !string.IsNullOrEmpty(group.ImageUrl)) return;
            lock (_lock)
            {
                long retryAt;
                if (_artworkRetryAfter.TryGetValue(group.TitleId, out retryAt) &&
                    retryAt > DateTime.UtcNow.Ticks) return;
                if (!_artworkResolving.Add(group.TitleId)) return;
            }
            string titleId = group.TitleId;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var hits = OrbisClient.Search(titleId, 8);
                    GameHit match = null;
                    foreach (var hit in hits)
                    {
                        if (string.Equals(hit.TitleId, titleId, StringComparison.OrdinalIgnoreCase))
                        {
                            match = hit;
                            break;
                        }
                    }
                    if (match != null && !string.IsNullOrEmpty(match.ImageUrl))
                    {
                        _dlMgr.SetTitleArtwork(titleId, match.Name, match.ImageUrl);
                        _covers.Request(titleId, match.ImageUrl);
                        Invalidated = true;
                    }
                    else
                    {
                        lock (_lock) _artworkRetryAfter[titleId] = DateTime.UtcNow.AddSeconds(30).Ticks;
                    }
                }
                catch
                {
                    lock (_lock) _artworkRetryAfter[titleId] = DateTime.UtcNow.AddSeconds(30).Ticks;
                }
                finally
                {
                    lock (_lock) _artworkResolving.Remove(titleId);
                }
            });
        }

        void DrawPageTitle(IntPtr r, string title, string subtitle)
        {
            int titleW = UiFont.MeasurePx(36, title ?? "");
            int tx = (W - titleW) / 2;
            Fill(r, tx, 128, Math.Min(36, Math.Max(18, titleW)), 4, Accent);
            TextPx(r, tx, 142, 36, title ?? "", White);
            TextCentered(r, new SDL_Rect { x = 160, y = 186, w = 1600, h = 28 }, 20, subtitle ?? "", Muted);
        }

        void DrawSectionLabel(IntPtr r, int x, int y, string label, SDL_Color color)
        {
            Fill(r, x, y + 6, 18, 3, Accent);
            TextPx(r, x + 28, y, 15, label ?? "", color);
        }

        void DrawCover(IntPtr r, GameHit game, SDL_Rect destination)
        {
            Fill(r, destination.x, destination.y, destination.w, destination.h, Panel);

            IntPtr texture;
            int tw, th;
            if (game != null && _covers.TryGet(game.TitleId, out texture, out tw, out th) &&
                tw > 0 && th > 0)
            {
                int inset = 0;
                int boxW = Math.Max(1, destination.w - inset * 2);
                int boxH = Math.Max(1, destination.h - inset * 2);
                // Aspect-fit inside matte (no stretch).
                float scale = Math.Min(boxW / (float)tw, boxH / (float)th);
                int dw = Math.Max(1, (int)(tw * scale));
                int dh = Math.Max(1, (int)(th * scale));
                var dest = new SDL_Rect
                {
                    x = destination.x + inset + (boxW - dw) / 2,
                    y = destination.y + inset + (boxH - dh) / 2,
                    w = dw,
                    h = dh
                };
                SDL_RenderCopy(r, texture, IntPtr.Zero, ref dest);
                return;
            }
            if (game != null && !string.IsNullOrEmpty(game.ImageUrl))
                _covers.Request(game.TitleId, game.ImageUrl);
            var inner = new SDL_Rect
            {
                x = destination.x + 8,
                y = destination.y + 8,
                w = destination.w - 16,
                h = destination.h - 16
            };
            StrokeRect(r, inner, Border, 2);
            string id = game == null || string.IsNullOrEmpty(game.TitleId) ? "Game Search" : game.TitleId;
            TextCentered(r, destination, destination.w > 200 ? 22 : 13, id, Dim);
            if (destination.h > 120)
                TextCentered(r, new SDL_Rect { x = destination.x, y = destination.y + destination.h / 2 + 18,
                    w = destination.w, h = 24 }, 16, "no cover", Muted);
        }

        static string MaskCredential(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            string t = value.Trim();
            if (t.Length <= 4) return new string('•', Math.Max(4, t.Length));
            return new string('•', Math.Min(12, t.Length - 4)) + t.Substring(t.Length - 4);
        }

        void DrawProgress(IntPtr r, SDL_Rect track, DlItem item)
        {
            if (item == null || item.State == DlState.Installed) return;
            Fill(r, track.x, track.y, track.w, track.h, Raised);
            bool determinate = item.Total > 0 && (item.State == DlState.Downloading || Extracting(item) || item.State == DlState.Paused || item.State == DlState.Completed);
            if (determinate)
            {
                int width = (int)Math.Max(0, Math.Min(track.w, item.Done * (double)track.w / item.Total));
                if (width > 0) Fill(r, track.x, track.y, width, track.h, Accent);
            }
            else if (item.State == DlState.Resolving || item.State == DlState.Finalizing || item.State == DlState.Installing || item.State == DlState.Downloading)
            {
                int segment = Math.Max(1, Math.Min(180, track.w / 4));
                int x = _cfg.ReduceMotion ? 0 : (int)((_frameTime / 5) % (uint)Math.Max(1, track.w + segment)) - segment;
                int first = Math.Max(0, x), last = Math.Min(track.w, x + segment);
                if (last > first) Fill(r, track.x + first, track.y, last - first, track.h, Accent);
            }
        }

        void DrawEmptyState(IntPtr r, string title, string body)
        {
            var rect = new SDL_Rect { x = 360, y = 280, w = 1200, h = 460 };
            Fill(r, rect.x + 8, rect.y + 10, rect.w, rect.h, Shadow);
            Fill(r, rect.x, rect.y, rect.w, rect.h, Row);
            StrokeRect(r, rect, Border, 1);
            Fill(r, rect.x, rect.y, 6, rect.h, Accent);
            Fill(r, rect.x + 80, rect.y + 80, 48, 4, Accent);
            TextCentered(r, new SDL_Rect { x = 360, y = 420, w = 1200, h = 52 }, 34, title, White);
            TextCentered(r, new SDL_Rect { x = 420, y = 490, w = 1080, h = 40 }, 20, body, Muted);
        }



        void DrawDiagnostic(IntPtr r, int x, int y, string label, string value, SDL_Color color)
        {
            TextPx(r, x, y, 18, label, Dim);
            TextPx(r, x + 132, y - 2, 20, value, color);
        }

        void DrawScrollBar(IntPtr r, SDL_Rect track, int total, int visible, int first)
        {
            if (total <= visible) return;
            Fill(r, track.x, track.y, track.w, track.h, C(38, 38, 43));
            int thumb = Math.Max(48, track.h * visible / total);
            int travel = track.h - thumb;
            int maxFirst = Math.Max(1, total - visible);
            int y = track.y + travel * first / maxFirst;
            Fill(r, track.x, y, track.w, thumb, Accent);
        }

        static void EnsureVisible(ref int scroll, int focus, int count, int visible)
        {
            if (focus < scroll) scroll = focus;
            if (focus >= scroll + visible) scroll = focus - visible + 1;
            int max = Math.Max(0, count - visible);
            if (scroll > max) scroll = max;
            if (scroll < 0) scroll = 0;
        }

        void Card(IntPtr r, SDL_Rect rect, bool focused, SDL_Color fill, SDL_Color rail)
        {
            SoftRect(r, rect, focused ? Focused : fill);
            if (focused) StrokeRect(r, rect, Accent, 2);
        }

        void SquareCard(IntPtr r, SDL_Rect rect, bool focused, SDL_Color fill, SDL_Color rail)
        {
            Card(r, rect, focused, fill, rail);
        }

        void SoftRect(IntPtr r, SDL_Rect rect, SDL_Color color)
        {
            Rounded(r, rect, color, 0);
        }

        void FillCircle(IntPtr r, int cx, int cy, int rad, SDL_Color color)
        {
            SDL_SetRenderDrawColor(r, color.r, color.g, color.b, 255);
            for (int dy = -rad; dy <= rad; dy++)
            {
                int dx = (int)Math.Sqrt(rad * rad - dy * dy);
                var line = new SDL_Rect { x = cx - dx, y = cy + dy, w = dx * 2 + 1, h = 1 };
                SDL_RenderFillRect(r, ref line);
            }
        }

        void StrokeRect(IntPtr r, SDL_Rect rect, SDL_Color color, int thickness)
        {
            Rounded(r, rect, color, thickness);
        }

        void DrawPill(IntPtr r, int x, int y, string text, SDL_Color color)
        {
            text = text ?? "";
            int textWidth = UiFont.MeasurePx(16, text);
            var rect = new SDL_Rect { x = x, y = y, w = Math.Max(72, textWidth + 36), h = 34 };
            // modern capsule: dark track + accent fill, white label
            var bg = new SDL_Color { r = 28, g = 28, b = 32, a = 255 };
            SoftRect(r, rect, bg);
            StrokeRect(r, rect, color, 2);
            TextPx(r, rect.x + Math.Max(10, (rect.w - textWidth) / 2), rect.y + 6, 16, text, color);
        }

        void DrawPillRight(IntPtr r, int right, int y, string text, SDL_Color color)
        {
            text = text ?? "";
            int textWidth = UiFont.MeasurePx(16, text);
            int width = Math.Max(72, textWidth + 36);
            DrawPill(r, right - width, y, text, color);
        }

        void TextPx(IntPtr r, int x, int y, int px, string text, SDL_Color color)
        {
            UiFont.DrawPx(r, x, y, px, text ?? "", color);
        }

        void TextFit(IntPtr r, int x, int y, int px, int maxWidth, string text, SDL_Color color)
        {
            UiFont.DrawPx(r, x, y, px, UiFont.EllipsizePx(text ?? "", px, maxWidth), color);
        }

        void TextCentered(IntPtr r, SDL_Rect rect, int px, string text, SDL_Color color)
        {
            string fitted = UiFont.EllipsizePx(text ?? "", px, Math.Max(1, rect.w - 16));
            int width = UiFont.MeasurePx(px, fitted);
            int height = px + 8;
            UiFont.DrawPx(r, rect.x + Math.Max(8, (rect.w - width) / 2),
                rect.y + Math.Max(2, (rect.h - height) / 2), px, fitted, color);
        }

        void DrawResultCover(IntPtr r, GameHit game, SDL_Rect destination)
        {
            IntPtr texture;
            int tw, th;
            if (game == null || !_covers.TryGet(game.TitleId, out texture, out tw, out th) ||
                texture == IntPtr.Zero || tw <= 0 || th <= 0)
            {
                DrawCover(r, game, destination);
                return;
            }
            uint first;
            if (!_resultCoverFirstSeen.TryGetValue(game.TitleId ?? "", out first))
            {
                first = UiTick();
                if (_resultCoverFirstSeen.Count >= 1024)
                {
                    // Bounded fade-in bookkeeping: evict oldest first-seen ticks.
                    // Evicted titles simply fade in again; visible covers are untouched.
                    var victims = new List<KeyValuePair<string, uint>>(_resultCoverFirstSeen);
                    victims.Sort((a, b) => a.Value.CompareTo(b.Value));
                    for (int i = 0; i + 256 < victims.Count; i++)
                        _resultCoverFirstSeen.Remove(victims[i].Key);
                }
                _resultCoverFirstSeen[game.TitleId ?? ""] = first;
            }
            uint age = UiElapsed(first);
            byte alpha = age >= 200 ? (byte)255 : (byte)Math.Max(24, age * 255 / 200);
            if (AdvancedBlendEffects) SDL_SetTextureAlphaMod(texture, alpha);
            DrawCover(r, game, destination);
            if (AdvancedBlendEffects) SDL_SetTextureAlphaMod(texture, 255);
        }

        void DrawFullTitle(IntPtr r, int x, int y, int width, string title, SDL_Color color)
        {
            title = title ?? "";
            int px = 30;
            while (px > 18 && UiFont.MeasurePx(px, title) > width) px--;
            if (UiFont.MeasurePx(px, title) <= width)
            {
                TextPx(r, x, y, px, title, color);
                return;
            }
            int split = title.LastIndexOf(' ', Math.Min(title.Length - 1, title.Length / 2));
            if (split <= 0) split = title.IndexOf(' ', Math.Min(title.Length, title.Length / 2));
            if (split <= 0) { TextPx(r, x, y, 18, title, color); return; }
            TextFit(r, x, y, 20, width, title.Substring(0, split), color);
            TextFit(r, x, y + 28, 20, width, title.Substring(split + 1), color);
        }

        void DrawSystemStatusDots(IntPtr r, int x, int y)
        {
            bool nativeHttps = NativeHttp.Available;
            string httpsLabel;
            SDL_Color httpsColor;
            if (NetHttp.UseProxy)
            {
                httpsLabel = "HTTPS · PROXY SET";
                httpsColor = Warning;
            }
            else if (nativeHttps)
            {
                httpsLabel = "HTTPS · NATIVE";
                httpsColor = Ok;
            }
            else
            {
                httpsLabel = "HTTPS · UNAVAILABLE";
                httpsColor = Danger;
            }
            x = DrawStatusDot(r, x, y, httpsLabel, httpsColor);

            bool rdSelected = _cfg.UseUnlockProvider &&
                string.Equals(_cfg.UnlockProviderId, UnlockProviders.RealDebridId,
                    StringComparison.OrdinalIgnoreCase);
            string rdLabel = rdSelected && _cfg.HasRealDebrid
                ? "Real-Debrid · ACTIVE"
                : (_cfg.HasRealDebrid ? "Real-Debrid · SAVED" : "Real-Debrid · NOT SET");
            SDL_Color rdColor = rdSelected && _cfg.HasRealDebrid
                ? Ok : (_cfg.HasRealDebrid || rdSelected ? Warning : Dim);
            if (!string.Equals(_cfg.UnlockProviderId, UnlockProviders.RealDebridId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(_cfg.UnlockProviderId, UnlockProviders.NoneId, StringComparison.OrdinalIgnoreCase))
            {
                bool configured = UnlockProviders.IsConfigured(_cfg, _cfg.UnlockProviderId);
                rdLabel = UnlockProviders.DisplayName(_cfg.UnlockProviderId) +
                    (configured ? (_cfg.UseUnlockProvider ? " - ACTIVE" : " - SAVED") : " - NOT SET");
                rdColor = configured ? (_cfg.UseUnlockProvider ? Ok : Warning) : Dim;
            }
            x = DrawStatusDot(r, x, y, rdLabel, rdColor);

            int enabledSources = 0;
            for (int i = 0; i < _sourceUi.Count; i++)
                if (_sourceUi[i] != null && _sourceUi[i].Enabled) enabledSources++;
            string sourceLabel;
            SDL_Color sourceColor;
            if (!string.IsNullOrEmpty(_sourceUiError))
            {
                sourceLabel = "Package Source · ERROR";
                sourceColor = Danger;
            }
            else if (enabledSources > 0)
            {
                sourceLabel = "Package Source · " + enabledSources + " ENABLED";
                sourceColor = Ok;
            }
            else if (_sourceUi.Count > 0)
            {
                sourceLabel = "Package Source · DISABLED";
                sourceColor = Warning;
            }
            else
            {
                sourceLabel = "Package Source · NONE";
                sourceColor = Dim;
            }
            DrawStatusDot(r, x, y, sourceLabel, sourceColor);
        }

        int DrawStatusDot(IntPtr r, int x, int y, string label, SDL_Color color)
        {
            FillCircle(r, x + 6, y + 8, 5, color);
            TextPx(r, x + 20, y, 15, label, Muted);
            return x + 20 + UiFont.MeasurePx(15, label) + 34;
        }

        static SDL_Color KindColor(string kind)
        {
            return PackagePresentationColor(kind);
        }

        static SDL_Color PackagePresentationColor(string kind)
        {
            if (string.Equals(kind, "base", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, "game", StringComparison.OrdinalIgnoreCase)) return C(214, 214, 220);
            if (string.Equals(kind, "update", StringComparison.OrdinalIgnoreCase)) return C(232, 176, 72);
            if (string.Equals(kind, "backport", StringComparison.OrdinalIgnoreCase)) return C(232, 176, 72);
            if (string.Equals(kind, "dlc", StringComparison.OrdinalIgnoreCase)) return C(168, 132, 220);
            return C(105, 105, 114);
        }

        static SDL_Color HosterColor(string hoster)
        {
            string h = (hoster ?? "").ToLowerInvariant();
            if (h.IndexOf("1fichier", StringComparison.Ordinal) >= 0 ||
                h.IndexOf("onefile", StringComparison.Ordinal) >= 0) return C(232, 168, 72);
            if (h.IndexOf("pixel", StringComparison.Ordinal) >= 0) return C(80, 196, 140);
            if (h.IndexOf("mediafire", StringComparison.Ordinal) >= 0) return C(88, 156, 232);
            if (h.IndexOf("gofile", StringComparison.Ordinal) >= 0) return C(176, 132, 232);
            if (h.IndexOf("mega", StringComparison.Ordinal) >= 0) return C(220, 88, 88);
            if (h.IndexOf("akr", StringComparison.Ordinal) >= 0) return C(72, 188, 196);
            if (h.IndexOf("gdrive", StringComparison.Ordinal) >= 0 ||
                h.IndexOf("drive", StringComparison.Ordinal) >= 0) return C(90, 168, 96);
            return C(160, 160, 168);
        }

        static bool VersionNewer(string remote, string installed)
        {
            if (string.IsNullOrWhiteSpace(remote)) return false;
            if (!HasVersionDigits(remote)) return false;
            if (string.IsNullOrWhiteSpace(installed)) return true;
            int[] a = ParseVersionParts(remote);
            int[] b = ParseVersionParts(installed);
            int n = Math.Max(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int av = i < a.Length ? a[i] : 0;
                int bv = i < b.Length ? b[i] : 0;
                if (av != bv) return av > bv;
            }
            return false;
        }

        static bool HasVersionDigits(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            foreach (char c in value) if (c >= '0' && c <= '9') return true;
            return false;
        }

        static int[] ParseVersionParts(string value)
        {
            string s = (value ?? "").Trim().TrimStart('v', 'V');
            var parts = new List<int>();
            int n = 0;
            bool any = false;
            for (int i = 0; i <= s.Length; i++)
            {
                char c = i < s.Length ? s[i] : '.';
                if (c >= '0' && c <= '9') { n = n * 10 + (c - '0'); any = true; }
                else if (any) { parts.Add(n); n = 0; any = false; }
            }
            return parts.Count == 0 ? new[] { 0 } : parts.ToArray();
        }

        void DrawPackageKindBadge(IntPtr r, SDL_Rect rect, PackageCandidatePresentation meta,
            SDL_Color color)
        {
            SoftRect(r, rect, Row);
            StrokeRect(r, rect, color, 2);
            TextCentered(r, rect, 16, meta != null ? meta.KindLabel : "OTHER", color);
        }

        static string KindShort(string kind)
        {
            if (string.Equals(kind, "update", StringComparison.OrdinalIgnoreCase)) return "UPD";
            if (string.Equals(kind, "backport", StringComparison.OrdinalIgnoreCase)) return "BP";
            if (string.Equals(kind, "dlc", StringComparison.OrdinalIgnoreCase)) return "DLC";
            if (string.Equals(kind, "base", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, "game", StringComparison.OrdinalIgnoreCase)) return "BASE";
            return "OTHER";
        }

        static string PackageTitle(string kind)
        {
            if (string.Equals(kind, "update", StringComparison.OrdinalIgnoreCase)) return "Game update";
            if (string.Equals(kind, "backport", StringComparison.OrdinalIgnoreCase)) return "Backport";
            if (string.Equals(kind, "dlc", StringComparison.OrdinalIgnoreCase)) return "Add-on content";
            if (string.Equals(kind, "base", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, "game", StringComparison.OrdinalIgnoreCase)) return "Base game package";
            return "Other package";
        }

        SDL_Color StateColor(DlState state)
        {
            switch (state)
            {
                case DlState.Completed:
                case DlState.Installed: return Ok;
                case DlState.Paused: return Warning;
                case DlState.Failed: return Danger;
                case DlState.Canceled: return Dim;
                case DlState.Finalizing: return Violet;
                case DlState.Installing: return Violet;
                case DlState.Submitted: return Violet;
                default: return Accent;
            }
        }

        static string StateLabel(DlItem item)
        {
            if (item != null && item.State == DlState.Completed && !string.IsNullOrEmpty(item.Error))
                return "Install failed";
            return StateLabel(item == null ? DlState.Finalizing : item.State);
        }

        static string StateLabel(DlState state)
        {
            if (state == DlState.Completed) return "Ready";
            if (state == DlState.Installed) return "Installed";
            if (state == DlState.Downloading) return "Downloading";
            if (state == DlState.Finalizing) return "Finalizing";
            if (state == DlState.Resolving) return "Preparing";
            if (state == DlState.Installing) return "Installing";
            if (state == DlState.Submitted) return "Sent to PS4";
            if (state == DlState.Paused) return "Paused";
            if (state == DlState.Failed) return "Failed";
            if (state == DlState.Canceled) return "Canceled";
            return state.ToString();
        }

        string FormatDlLine(DlItem item)
        {
            if (item == null) return "";
            if (Extracting(item)) return DownloadManager.Human(item.Done) + (item.Total > 0 ? " / " + DownloadManager.Human(item.Total) : " extracted · total pending");
            if (item.State == DlState.Failed && !string.IsNullOrEmpty(item.Error))
                return "Error: " + item.Error;
            if (item.State == DlState.Completed && !string.IsNullOrEmpty(item.Error))
                return "Install failed: " + item.Error + "  ·  CROSS retries";
            if (item.State == DlState.Installed)
                return item.StatusText ?? "Installed — local PKG cleaned";
            if (item.State == DlState.Completed)
                return "100%   Ready to install  ·  CROSS";
            if (item.State == DlState.Finalizing)
                return item.StatusText ?? "Finalizing and verifying package...";
            if (item.State == DlState.Submitted)
                return item.StatusText ?? "Sent to PS4 — verification pending · PKG kept";
            if (item.State == DlState.Downloading || item.State == DlState.Paused ||
                item.State == DlState.Resolving)
            {
                int pct = item.Total > 0
                    ? Math.Min(100, (int)(item.Done * 100.0 / Math.Max(1.0, item.Total)))
                    : 0;
                string core = DownloadManager.FormatProgress(_cfg, item.Done, item.Total,
                    item.BytesPerSec, item.EtaSeconds);
                return (item.Total > 0 ? pct + "%   " : "") + core;
            }
            if (item.Total > 0)
                return Math.Min(100, (int)(item.Done * 100.0 / Math.Max(1.0, item.Total))) + "%";
            return item.StatusText ?? "";
        }

        static string DlStatsLabel(int mode)
        {
            switch (mode)
            {
                case 1: return "Size only";
                case 2: return "Size + MB/s";
                case 3: return "Size + ETA";
                default: return "Size + MB/s + ETA";
            }
        }

        static string TextFitValue(string value, int max)
        {
            if (string.IsNullOrEmpty(value)) return "Not configured";
            return value.Length <= max ? value : value.Substring(0, max - 3) + "...";
        }

        void Fill(IntPtr r, int x, int y, int w, int h, SDL_Color c)
        {
            if (w <= 0 || h <= 0) return;
            SDL_SetRenderDrawColor(r, c.r, c.g, c.b, 255);
            var rc = new SDL_Rect { x = x, y = y, w = w, h = h };
            SDL_RenderFillRect(r, ref rc);
        }

        void FillAlpha(IntPtr r, int x, int y, int w, int h, SDL_Color c, byte alpha)
        {
            if (w <= 0 || h <= 0 || alpha == 0) return;
            if (AdvancedBlendEffects)
            {
                SDL_SetRenderDrawBlendMode(r, SDL_BlendMode.SDL_BLENDMODE_BLEND);
                SDL_SetRenderDrawColor(r, c.r, c.g, c.b, alpha);
                var blended = new SDL_Rect { x = x, y = y, w = w, h = h };
                SDL_RenderFillRect(r, ref blended);
                SDL_SetRenderDrawBlendMode(r, SDL_BlendMode.SDL_BLENDMODE_NONE);
                return;
            }
            int a = alpha;
            var safe = C(
                (byte)((Bg.r * (255 - a) + c.r * a) / 255),
                (byte)((Bg.g * (255 - a) + c.g * a) / 255),
                (byte)((Bg.b * (255 - a) + c.b * a) / 255));
            Fill(r, x, y, w, h, safe);
        }

        static SDL_Color C(byte r, byte g, byte b) { return new SDL_Color { r = r, g = g, b = b, a = 255 }; }

        static string Clip(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= n ? s : s.Substring(0, n);
        }

        #endregion
    }
}
