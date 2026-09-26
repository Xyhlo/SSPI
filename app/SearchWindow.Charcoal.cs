using System;
using System.Collections.Generic;
using System.IO;
using SDL2.Types;
using static SDL2.SDL;

namespace Orbis
{
    public partial class SearchWindow
    {
        const int ContentX = 240, ContentWidth = 1440;
        static readonly SDL_Color PrimaryFill = C(228, 228, 225);
        static readonly SDL_Color PrimaryInk = C(32, 32, 32);
        readonly SDL_Rect[] _roundedRects = new SDL_Rect[56];
        static readonly int[][] CornerInsets = MakeCornerInsets();
        int _packageFilter, _hostFilter;
        bool _latestUpdateOnly;
        readonly List<string> _detailHosts = new List<string>();
        static readonly string[] PackageFilters = { "All", "Base", "Updates", "DLC", "Backport" };
        readonly int[] _detailTypeCounts = new int[5];
        int _regionFilter;
        List<GameHit> _filteredResults = new List<GameHit>();
        List<GameHit> _filteredSource;
        int _filteredRegion = -1;
        int _filteredSearchGeneration = -1;
        static readonly string[] RegionFilters = { "All regions", "EU", "US", "JP", "Other" };
        int _queueFilter;
        static readonly string[] QueueFilters = { "All", "Active", "Attention" };
        volatile bool _consoleScanBusy;
        volatile List<GameHit> _pendingConsoleInstalled;
        uint _queueModelAt;
        bool _queueModelReady;
        List<DlItem> _queueSnapshot = new List<DlItem>();
        List<DownloadGroup> _queueGroups = new List<DownloadGroup>();
        List<DownloadTreeRow> _queueRows = new List<DownloadTreeRow>();
        string _fileDetailId;
        List<ArchiveVolume> _fileVolumes = new List<ArchiveVolume>();
        sealed class FileOutput { public string Name; public string State; public long Size; }
        List<FileOutput> _fileOutputs = new List<FileOutput>();
        uint _fileReadAt;
        volatile bool _fileReadBusy;

        static int[][] MakeCornerInsets()
        {
            var result = new int[13][];
            for (int radius = 0; radius <= 12; radius++)
            {
                result[radius] = new int[radius];
                for (int y = 0; y < radius; y++)
                {
                    double dy = radius - y - .5;
                    result[radius][y] = Math.Max(0, (int)Math.Ceiling(radius - Math.Sqrt(radius * radius - dy * dy)));
                }
            }
            return result;
        }

        // At most 52 tiny bands, submitted in one SDL call. No textures, allocation or blur per panel.
        void Rounded(IntPtr r, SDL_Rect rect, SDL_Color color, int stroke)
        {
            if (rect.w <= 0 || rect.h <= 0) return;
            int radius = Math.Min(12, Math.Min(rect.w, rect.h) / 2);
            int n = 0;
            if (radius < 2) { Fill(r, rect.x, rect.y, rect.w, rect.h, color); return; }
            if (stroke == 0)
            {
                _roundedRects[n++] = new SDL_Rect { x = rect.x, y = rect.y + radius, w = rect.w, h = rect.h - radius * 2 };
                for (int y = 0; y < radius; y++)
                {
                    int inset = CornerInsets[radius][y];
                    _roundedRects[n++] = new SDL_Rect { x = rect.x + inset, y = rect.y + y, w = rect.w - inset * 2, h = 1 };
                    _roundedRects[n++] = new SDL_Rect { x = rect.x + inset, y = rect.y + rect.h - 1 - y, w = rect.w - inset * 2, h = 1 };
                }
            }
            else
            {
                stroke = Math.Min(stroke, radius);
                for (int y = 0; y < radius; y++)
                {
                    int outer = CornerInsets[radius][y];
                    int inner = y < stroke ? rect.w / 2 : stroke + (y - stroke < radius - stroke ? CornerInsets[radius - stroke][y - stroke] : 0);
                    int width = y < stroke ? rect.w - outer * 2 : Math.Max(1, inner - outer);
                    _roundedRects[n++] = new SDL_Rect { x = rect.x + outer, y = rect.y + y, w = width, h = 1 };
                    _roundedRects[n++] = new SDL_Rect { x = rect.x + outer, y = rect.y + rect.h - 1 - y, w = width, h = 1 };
                    if (y >= stroke)
                    {
                        _roundedRects[n++] = new SDL_Rect { x = rect.x + rect.w - outer - width, y = rect.y + y, w = width, h = 1 };
                        _roundedRects[n++] = new SDL_Rect { x = rect.x + rect.w - outer - width, y = rect.y + rect.h - 1 - y, w = width, h = 1 };
                    }
                }
                _roundedRects[n++] = new SDL_Rect { x = rect.x, y = rect.y + radius, w = stroke, h = rect.h - radius * 2 };
                _roundedRects[n++] = new SDL_Rect { x = rect.x + rect.w - stroke, y = rect.y + radius, w = stroke, h = rect.h - radius * 2 };
            }
            SDL_SetRenderDrawColor(r, color.r, color.g, color.b, 255);
            SDL_RenderFillRects(r, _roundedRects, n);
        }

        void FilterChip(IntPtr r, int x, int y, int w, string label, bool active)
        {
            var rect = new SDL_Rect { x = x, y = y, w = w, h = 48 };
            SoftRect(r, rect, active ? Raised : C(29, 29, 29));
            TextCentered(r, rect, 20, label, active ? White : Muted);
        }

        // A row of chips whose active highlight glides between them: backgrounds
        // first, then the moving highlight, then every label on top of it.
        void DrawChipRow(IntPtr r, int x, int y, int w, int gap, string[] labels, int active, int glideSlot)
        {
            for (int i = 0; i < labels.Length; i++)
                SoftRect(r, new SDL_Rect { x = x + i * (w + gap), y = y, w = w, h = 48 }, C(29, 29, 29));
            if (active >= 0 && active < labels.Length)
                SoftRect(r, Glide(glideSlot, new SDL_Rect { x = x + active * (w + gap), y = y, w = w, h = 48 }), Raised);
            for (int i = 0; i < labels.Length; i++)
                TextCentered(r, new SDL_Rect { x = x + i * (w + gap), y = y, w = w, h = 48 }, 20, labels[i], i == active ? White : Muted);
        }

        void TreeBranch(IntPtr r, int stemX, int top, int mid, int bottom, int endX, bool last)
        {
            Fill(r, stemX, top, 2, Math.Max(1, (last ? mid : bottom) - top), Muted);
            // A short dash makes the parent/child relationship visible without another frame.
            Fill(r, stemX, mid, Math.Max(1, endX - stemX - 8), 2, Muted);
        }

        List<GameHit> FilteredResults()
        {
            lock (_lock)
            {
                if (object.ReferenceEquals(_filteredSource, _results) && _filteredRegion == _regionFilter) return _filteredResults;
                GameHit focused = _filteredRegion == _regionFilter && _filteredSearchGeneration == _searchGeneration &&
                    _focus >= 0 && _focus < _filteredResults.Count ? _filteredResults[_focus] : null;
                _filteredSource = _results; _filteredRegion = _regionFilter;
                _filteredSearchGeneration = _searchGeneration;
                _filteredResults = new List<GameHit>();
                foreach (var hit in _results)
                {
                    foreach (var variant in hit.Variants ?? new List<GameHit> { hit }) {
                        string region = (variant.Region ?? "").ToUpperInvariant();
                        bool known = region == "EU" || region == "US" || region == "JP";
                        if (_regionFilter == 0 || (_regionFilter == 4 ? !known : region == RegionFilters[_regionFilter])) {
                            _filteredResults.Add(hit.WithVariant(variant)); break;
                        }
                    }
                }
                int nextFocus = -1;
                if (focused != null)
                    nextFocus = _filteredResults.FindIndex(hit => hit.TitleId == focused.TitleId &&
                        hit.Region == focused.Region && hit.Source == focused.Source &&
                        hit.SourceVersion == focused.SourceVersion && hit.CatalogUrl == focused.CatalogUrl);
                _focus = nextFocus < 0 ? 0 : nextFocus;
                _listScroll = nextFocus < 0 ? 0 : Math.Min(_listScroll, Math.Max(0, _filteredResults.Count - 5));
                if (_focus < _listScroll) _listScroll = _focus;
                if (_focus >= _listScroll + 5) _listScroll = _focus - 4;
                return _filteredResults;
            }
        }

        bool PackageMatches(int index)
        {
            var meta = _linkPresentation[index];
            if (!PackageSupported(meta.Candidate)) return false;
            if (_packageFilter > 0 && (_packageFilter == 4 ? !string.Equals(meta.Kind, "backport", StringComparison.OrdinalIgnoreCase) : PackageKindOrder(meta.Kind) != _packageFilter - 1)) return false;
            if (_hostFilter > 0 && (_hostFilter > _detailHosts.Count || !string.Equals(meta.Hoster, _detailHosts[_hostFilter - 1], StringComparison.OrdinalIgnoreCase))) return false;
            if (_latestUpdateOnly && SamePackageKind(meta.Kind, "update"))
                for (int i = 0; i < _linkPresentation.Count; i++)
                    if (PackageSupported(_linkPresentation[i].Candidate) && SamePackageKind(_linkPresentation[i].Kind, "update") && ComparePackageVersions(_linkPresentation[i].Version, meta.Version) > 0) return false;
            return true;
        }

        void ResetPackageFilters()
        {
            _packageFilter = _hostFilter = 0; _latestUpdateOnly = false;
            _detailHosts.Clear(); _expandedPackageGroups.Clear();
        }

        IList<DlItem> _queuedGroupItems;
        IList<PackageCandidatePresentation> _queuedGroupPresentation;
        readonly HashSet<string> _queuedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool PackageGroupQueued(int index, IList<DlItem> items)
        {
            if (index < 0 || index >= _linkPresentation.Count) return false;
            if (!object.ReferenceEquals(items, _queuedGroupItems) ||
                !object.ReferenceEquals(_linkPresentation, _queuedGroupPresentation))
            {
                _queuedGroupItems = items; _queuedGroupPresentation = _linkPresentation;
                _queuedGroups.Clear();
                foreach (var meta in _linkPresentation)
                    if (IsCandidateQueued(meta.Candidate, items)) _queuedGroups.Add(DetailGroupKey(meta));
            }
            return _queuedGroups.Contains(DetailGroupKey(_linkPresentation[index]));
        }

        List<DlItem> ReadDownloadSnapshot()
        {
            List<DlItem> current;
            if (_dlMgr != null && _dlMgr.TrySnapshot(out current)) _queueSnapshot = current;
            return _queueSnapshot;
        }

        void RefreshQueueModel(bool force = false)
        {
            if (!force && _queueModelReady && UiElapsed(_queueModelAt) < 250) return;
            var previousGroup = _dlFocus >= 0 && _dlFocus < _queueRows.Count ? _queueRows[_dlFocus].Group : null;
            string focusKey = previousGroup == null ? null : previousGroup.Key;
            _queueModelAt = UiTick(); _queueModelReady = true;
            _queueSnapshot = ReadDownloadSnapshot();
            _queueGroups = BuildDownloadGroups(_queueSnapshot);
            // Follow a newly failed focused job into Attention instead of making
            // its card disappear as soon as it leaves the Active filter.
            if (_queueFilter == 1 && focusKey != null)
            {
                var focused = _queueGroups.Find(group => group.Key == focusKey);
                if (focused != null && HasNewQueueFailure(previousGroup.Items, focused.Items))
                {
                    _queueFilter = 2;
                    _dlScroll = 0;
                    if (_downloadFilesTitle == focusKey)
                    {
                        _drawerTab = 2;
                        _drawerFocus = _drawerScroll = 0;
                        _drawerRowsDirty = true;
                    }
                }
            }
            _queueRows = BuildDownloadRows(_queueGroups);
            int focus = _queueRows.FindIndex(row => row.Group.Key == (_downloadFilesTitle ?? focusKey));
            if (focus >= 0) _dlFocus = focus;
            if (_downloadFilesTitle != null && !_queueGroups.Exists(group => group.Key == _downloadFilesTitle)) CloseDownloadDrawer();
            _downloadView = _queueSnapshot;
        }

        internal static bool HasNewQueueFailure(IList<DlItem> previous, IList<DlItem> current)
        {
            if (previous == null || current == null) return false;
            foreach (var item in current)
            {
                if (item.State != DlState.Failed) continue;
                foreach (var old in previous)
                    if (old.Id == item.Id && (old.State == DlState.Downloading ||
                        old.State == DlState.Resolving || old.State == DlState.Finalizing ||
                        old.State == DlState.Installing)) return true;
            }
            return false;
        }

        bool QueueGroupMatches(DownloadGroup group)
        {
            if (_queueFilter == 0) return true;
            foreach (var item in group.Items)
            {
                if (_queueFilter == 1 && (item.State == DlState.Downloading || item.State == DlState.Resolving || item.State == DlState.Finalizing || item.State == DlState.Installing || item.ParkedForProvider)) return true;
                if (_queueFilter == 2 && (item.State == DlState.Failed || item.State == DlState.Paused || item.State == DlState.Canceled)) return true;
            }
            return false;
        }

        internal static bool IsArchiveItem(DlItem item)
        {
            return item != null && (item.ContainerFormat == "rar" || item.ContainerFormat == "zip" ||
                item.ContainerFormat == "7z" || item.ContainerFormat == "archive" || item.Kind == "archive" ||
                !string.IsNullOrEmpty(item.ArchiveVolumes));
        }

        static bool Extracting(DlItem item)
        {
            if (item == null || item.State != DlState.Finalizing || item.CancelRequested ||
                item.RemoveRequested || item.ResidentRemovePending) return false;
            return item.StatsPhase == "extracting" || (string.IsNullOrEmpty(item.StatsPhase) &&
                (item.StatusText ?? "").StartsWith("Extracting", StringComparison.OrdinalIgnoreCase));
        }

        static string FileBytes(long value)
        {
            if (value >= 1000000000) return (value / 1000000000.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " GB";
            return (value / 1000000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        }

        static string VisibleState(DlItem item)
        {
            if (item == null) return "Queued";
            if (item.RemoveRequested || item.ResidentRemovePending) return item.StatusText ?? "Removing";
            if (item.CancelRequested) return item.StatusText ?? "Canceling";
            if (Extracting(item)) return "Extracting";
            if (item.ParkedForProvider) return "Preparing in TorBox";
            if (item.State == DlState.Submitted && !item.Background && PkgValidator.BgftSubTypeForKind(item.Kind) == 7) return "Installing";
            if ((item.StatusText ?? "").StartsWith("Installing packages", StringComparison.OrdinalIgnoreCase)) return "Installing in order";
            if (item.State == DlState.Queued && !item.InstallAfterConfirmed && !string.IsNullOrEmpty(item.InstallAfterId)) return "Waiting for dependency";
            return StateLabel(item.State);
        }

        static string LibraryTitle(GameHit hit)
        {
            if (hit == null) return "Unknown title";
            return string.IsNullOrWhiteSpace(hit.Name) || string.Equals(hit.Name, hit.TitleId, StringComparison.OrdinalIgnoreCase)
                ? "Title name unavailable" : hit.Name;
        }

        static string PackageDisplayTitle(string kind, string label, string gameName, string host, string version)
        {
            string title = PackageTitle(kind);
            if (SamePackageKind(kind, "dlc") && !string.IsNullOrWhiteSpace(label) &&
                !string.Equals(label, gameName, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(label, host, StringComparison.OrdinalIgnoreCase)) title = label;
            if (!string.IsNullOrWhiteSpace(version)) title += "  ·  v" + version.TrimStart('v', 'V');
            else if (SamePackageKind(kind, "update") || SamePackageKind(kind, "backport")) title += " · version not supplied";
            return title;
        }

        void DrawCharcoalSearch(IntPtr r)
        {
            if (_softKbOpen) { DrawSoftKeyboardModal(r, "Search games", _query, "R2 SEARCH"); return; }
            var field = new SDL_Rect { x = ContentX, y = 174, w = ContentWidth, h = 80 };
            DesignCard(r, field, _searchLandingFocus == 0);
            DesignIcon(r, "search", ContentX + 26, 200, 26, Muted);
            TextFit(r, ContentX + 70, 194, 27, ContentWidth - 285, string.IsNullOrEmpty(_query) ? "Search games or CUSA ID" : _query, string.IsNullOrEmpty(_query) ? Muted : White);
            GamepadIcons.Draw(r, "cross", ContentX + ContentWidth - 155, 202, 24);
            TextPx(r, ContentX + ContentWidth - 120, 200, 20, "Search", White);
            TextPx(r, ContentX, 321, 28, "Your library", White);
            int count = _libraryGames.Count;
            int selected = Math.Max(0, Math.Min(count - 1, _searchLandingFocus - 1));
            int visible = Math.Min(count, 5), posterW = 224, gap = 44;
            EnsureVisible(ref _libraryScroll, selected, count, Math.Max(1, visible));
            string countText = count + (count == 1 ? " title" : " titles") + " · installed on this PS4";
            TextFit(r, ContentX + 225, 330, 18, ContentWidth - 440, countText, Dim);
            DrawTouchpadAction(r, ContentX + ContentWidth - 188, 314, "Expand library");
            if (count == 0)
            {
                DesignIcon(r, "library", 930, 446, 48, Dim);
                TextCentered(r, new SDL_Rect { x = ContentX, y = 530, w = ContentWidth, h = 45 }, 27, (_libraryScanBusy || _consoleScanBusy) ? "Reading your library…" : "Your installed games appear here", White);
                TextCentered(r, new SDL_Rect { x = ContentX, y = 590, w = ContentWidth, h = 35 }, 20, "Search for a title above to view its packages.", Muted);
            }
            else
            {
                int shelfX = (W - (visible * posterW + (visible - 1) * gap)) / 2;
                for (int i = 0; i < visible; i++)
                {
                    int index = _libraryScroll + i; var hit = _libraryGames[index]; bool focused = _searchLandingFocus == index + 1;
                    int x = shelfX + i * (posterW + gap);
                    var art = new SDL_Rect { x = x, y = 388 - FocusLift(focused), w = posterW, h = 284 };
                    DrawCase(r, hit, art);
                    if (focused) StrokeRect(r, Glide(GlidePoster, new SDL_Rect { x = x - 6, y = art.y - 6, w = posterW + 12, h = art.h + 12 }), Accent, 2);
                    TextFit(r, x, 693, 22, posterW, LibraryTitle(hit), White);
                    TextFit(r, x, 731, 17, posterW, LibraryInstalledVersion(hit), Muted);
                    bool newer; string updateStatus = LibraryUpdateStatus(hit, out newer);
                    TextFit(r, x, 760, 16, posterW, updateStatus, newer ? Warning : Dim);
                    if (newer) SoftRect(r, new SDL_Rect { x = x + posterW - 21, y = art.y + 14, w = 9, h = 9 }, Warning);
                }
                Fill(r, ContentX, 794, ContentWidth, 1, Border);
                var chosen = _libraryGames[selected];
                TextFit(r, ContentX, 818, 22, 1050, LibraryTitle(chosen), White);
                bool hasUpdate; string updateInfo = LibraryUpdateStatus(chosen, out hasUpdate);
                TextFit(r, ContentX, 858, 18, 1100, chosen.TitleId + " · " + LibraryInstalledVersion(chosen) + " · " + updateInfo, hasUpdate ? Warning : Muted);
                TextFit(r, ContentX, 888, 16, 1100, "Official update metadata · DLC and backport availability unknown", Dim);
                string page = (selected + 1) + " / " + count;
                TextPx(r, ContentX + ContentWidth - UiFont.MeasurePx(18, page), 822, 18, page, Muted);
                if (count > visible) { TextPx(r, ContentX - 36, 520, 32, _libraryScroll > 0 ? "‹" : "", Muted); TextPx(r, ContentX + ContentWidth + 16, 520, 32, _libraryScroll + visible < count ? "›" : "", Muted); }
            }
            if (_cfg.SearchContinue && _landingContinue.Count > 0)
            {
                int i = Math.Max(0, Math.Min(_landingContinue.Count - 1, _searchLandingFocus - count - 1)); var item = _landingContinue[i];
                bool on = _searchLandingFocus > count;
                TextFit(r, ContentX, 917, 18, ContentWidth - 300, "Continue · " + (item.Name ?? item.TitleId) + " · " + VisibleState(item), on ? White : Muted);
                if (on) Fill(r, ContentX, 947, 240, 2, Accent);
            }
        }

        void DrawCharcoalResults(IntPtr r)
        {
            bool busy; lock (_lock) busy = _browseBusy && _busyKind == BusyKind.Searching;
            if (busy && _results.Count == 0) { DrawCharcoalLoading(r, false); return; }
            var list = FilteredResults();
            string count = list.Count + (list.Count == 1 ? " title" : " titles");
            string queryLine = (string.IsNullOrWhiteSpace(_query) ? "All titles" : "“" + _query + "”") + "  ·  " + count;
            if (busy) queryLine += "  ·  Checking remaining sources…";
            TextFit(r, ContentX, 150, 22, ContentWidth, queryLine, White);
            int chipW = 138, chipGap = 8;
            int chipsTotal = RegionFilters.Length * chipW + (RegionFilters.Length - 1) * chipGap;
            int chipStart = ContentX + (ContentWidth - chipsTotal) / 2;
            DrawChipRow(r, chipStart, 218, chipW, chipGap, RegionFilters, _regionFilter, GlideChips);
            if (list.Count == 0)
            {
                // Single empty state: heading + guidance are always distinct.
                // Empty success leaves _searchError null (see StartSearchJob), so a
                // failed source never duplicates the "no match" text.
                bool failed = !string.IsNullOrEmpty(_searchError);
                DesignIcon(r, failed ? "error" : "search", 938, 438, 44, failed ? Danger : Dim);
                string heading = failed ? "Search unavailable" : _results.Count == 0 ? "No matching titles" : "No titles in this region";
                string guidance = failed ? _searchError : _results.Count == 0 ? "Try a shorter name, another region or the exact CUSA ID." : "Choose All regions to see the other matches.";
                TextCentered(r, new SDL_Rect { x = ContentX, y = 515, w = ContentWidth, h = 48 }, 28, heading, White);
                TextWrappedCentered(r, 460, 580, 20, 1000, guidance, Muted);
                return;
            }
            const int visible = 5, top = 296, rowH = 126;
            EnsureVisible(ref _listScroll, _focus, list.Count, visible);
            for (int i = 0; i < visible && _listScroll + i < list.Count; i++)
            {
                int index = _listScroll + i, y = top + i * rowH; var hit = list[index]; bool on = index == _focus;
                DesignCard(r, new SDL_Rect { x = ContentX, y = y, w = ContentWidth, h = 114 }, on, GlideList);
                DrawCase(r, hit, new SDL_Rect { x = ContentX + 22, y = y + 9 - FocusLift(on), w = 74, h = 94 });
                TextFit(r, ContentX + 122, y + 18, 27, ContentWidth - 440, hit.Name, White);
                TextFit(r, ContentX + 122, y + 65, 19, ContentWidth - 440, hit.TitleId ?? "", Muted);
                TextPx(r, ContentX + ContentWidth - 297, y + 45, 18, "View packages", on ? White : Dim);
                DrawPillRight(r, ContentX + ContentWidth - 66, y + 41, hit.Region ?? "?", Muted);
                DesignIcon(r, "chevron", ContentX + ContentWidth - 43, y + 46, 21, on ? White : Dim);
            }
            FlushGlideRing(r);
            DrawScrollBar(r, new SDL_Rect { x = ContentX + ContentWidth + 16, y = top, w = 4, h = 618 }, list.Count, visible, _listScroll);
        }

        void DrawCharcoalLoading(IntPtr r, bool resolving)
        {
            if (resolving && _selected != null) { DrawGamePackagePage(r, true); return; }
            TextCentered(r, new SDL_Rect { x = ContentX, y = 385, w = ContentWidth, h = 58 }, 32,
                string.IsNullOrWhiteSpace(_query) ? "Browsing titles" : "Searching for “" + _query + "”", White);
            TextCentered(r, new SDL_Rect { x = ContentX, y = 455, w = ContentWidth, h = 42 }, 21, "Checking titles and regions from your enabled sources.", Muted);
            DrawActivityRail(r, new SDL_Rect { x = 610, y = 529, w = 700, h = 5 });
        }

        void DrawCharcoalDetail(IntPtr r)
        {
            if (_selected == null) return;
            bool busy; lock (_lock) busy = _browseBusy && _busyKind == BusyKind.Resolving;
            DrawGamePackagePage(r, busy);
        }

        static SDL_Color PackageTint(string kind)
        {
            if (SamePackageKind(kind, "update")) return C(232, 178, 79);
            if (SamePackageKind(kind, "dlc")) return C(184, 151, 232);
            if (SamePackageKind(kind, "backport")) return C(102, 198, 204);
            return PrimaryFill;
        }

        void DrawGamePackagePage(IntPtr r, bool busy)
        {
            const int leftX = ContentX, leftW = 328, rightX = 620, rightW = 1060;
            bool installed = _libraryGames.Exists(g => string.Equals(g.TitleId, _selected.TitleId, StringComparison.OrdinalIgnoreCase));
            TextFit(r, ContentX, 148, 34, ContentWidth, LibraryTitle(_selected), White);
            string identity = _selected.TitleId ?? "";
            if (!string.IsNullOrWhiteSpace(_selected.Region) && _selected.Region != "?") identity += "  ·  " + _selected.Region;
            identity += "  ·  PS4 " + _firmwareVersion;
            TextFit(r, ContentX, 201, 18, ContentWidth - 280, identity, Muted);
            string library = installed ? "In your library" : "Not installed";
            TextPx(r, ContentX + ContentWidth - UiFont.MeasurePx(18, library), 201, 18, library, installed ? Ok : Dim);
            Fill(r, ContentX, 246, ContentWidth, 1, Border);

            DrawCase(r, _selected, new SDL_Rect { x = leftX + (leftW - 192) / 2, y = 278, w = 192, h = 244 });
            if (busy)
            {
                TextPx(r, rightX, 283, 23, "Finding packages", White);
                TextPx(r, rightX, 328, 18, "Checking versions and available mirrors…", Muted);
                DrawActivityRail(r, new SDL_Rect { x = rightX, y = 371, w = rightW, h = 3 });
                for (int i = 0; i < 5; i++)
                {
                    int y = 403 + i * 98;
                    Fill(r, rightX + 24, y + 19, 290 - i * 24, 15, Raised);
                    Fill(r, rightX + 24, y + 52, 156, 10, Raised);
                    Fill(r, rightX + rightW - 174, y + 31, 128, 10, Raised);
                    Fill(r, rightX, y + 87, rightW, 1, Border);
                }
                return;
            }

            for (int i = 0; i < PackageFilters.Length; i++)
            {
                int x = rightX + i * rightW / PackageFilters.Length, w = rightW / PackageFilters.Length;
                string label = PackageFilters[i] + "  " + _detailTypeCounts[i];
                TextCentered(r, new SDL_Rect { x = x, y = 276, w = w, h = 44 }, 20, label,
                    _packageFilter == i ? White : _detailTypeCounts[i] > 0 ? Muted : Dim);
            }
            {
                int w = rightW / PackageFilters.Length, x = rightX + _packageFilter * w;
                var line = Glide(GlideTabs, new SDL_Rect { x = x + 22, y = 329, w = w - 44, h = 2 });
                Fill(r, line.x, line.y, line.w, line.h, Accent);
            }
            Fill(r, rightX, 332, rightW, 1, Border);
            GamepadIcons.Draw(r, "l2", rightX, 350, 26);
            string regionLabel = "Region " + (string.IsNullOrEmpty(_selected.Region) ? "?" : _selected.Region);
            if (_selected.Variants != null && _selected.Variants.Count > 1) {
                int regionIndex = _selected.Variants.FindIndex(v => v.TitleId == _selected.TitleId && v.Region == _selected.Region && v.Source == _selected.Source && v.CatalogUrl == _selected.CatalogUrl);
                regionLabel += "  ·  " + (Math.Max(0, regionIndex) + 1) + "/" + _selected.Variants.Count;
            }
            TextFit(r, rightX + 36, 352, 18, 330, regionLabel, Muted);
            string versions = _latestUpdateOnly ? "Latest update" : "All update versions";
            int versionX = rightX + rightW - UiFont.MeasurePx(18, versions);
            GamepadIcons.Draw(r, "r2", versionX - 36, 350, 26);
            TextPx(r, versionX, 352, 18, versions, Muted);
            if (ShowsProviderStatus)
                TextCentered(r, new SDL_Rect { x = rightX + (rightW - 300) / 2, y = 350, w = 300, h = 28 }, 16,
                    UnlockProviders.EnabledSummary(_cfg), Muted);
            RefreshDetailRegionModel(installed);
            bool notice = HasRegionNotice;
            if (notice) DrawRegionNotice(r, rightX, 394, rightW);

            if (_detailRows.Count == 0)
            {
                bool checking = string.IsNullOrEmpty(_resolveError) && _linkStatusLookup != null && _linkStatusLookup.IsChecking;
                if (checking) DrawProviderCheckingDots(r, rightX + rightW / 2 - 14, 522);
                else DesignIcon(r, string.IsNullOrEmpty(_resolveError) ? "download" : "error", rightX + rightW / 2 - 20, 503, 40, Dim);
                TextCentered(r, new SDL_Rect { x = rightX, y = 574, w = rightW, h = 45 }, 27,
                    ResolveEmptyHeading(), White);
                TextWrappedCentered(r, rightX + 90, 633, 19, rightW - 180,
                    ResolveEmptyDescription(), Muted);
                return;
            }

            RefreshQueueModel();
            const int rowH = 98;
            int top = notice ? 462 : 401, visible = notice ? 4 : 5;
            int focused = Math.Max(0, Math.Min(_detailFocus, _detailRows.Count - 1));
            EnsureVisible(ref _detailScroll, focused, _detailRows.Count, visible);
            DrawSelectedPackageInfo(r, leftX, _linkPresentation[_detailRows[focused]]);
            for (int i = 0; i < visible && _detailScroll + i < _detailRows.Count; i++)
            {
                int ordinal = _detailScroll + i, index = _detailRows[ordinal], y = top + i * rowH;
                var meta = _linkPresentation[index];
                bool child = DetailRowIsChild(ordinal), on = ordinal == focused;
                int x = rightX + (child ? 36 : 0), width = rightW - (child ? 36 : 0);
                if (child)
                {
                    int bottom = DetailRowIsLastChild(ordinal) ? y + 44 : y + rowH;
                    Fill(r, rightX + 14, Math.Max(top, y - 12), 1, bottom - Math.Max(top, y - 12), Border);
                    Fill(r, rightX + 14, y + 44, 12, 1, Border);
                }
                var rect = new SDL_Rect { x = x, y = y, w = width, h = 88 };
                if (on) DesignCard(r, rect, true, GlideList);
                else Fill(r, x + 20, y + 87, width - 40, 1, Border);
                if (ShowsProviderStatus)
                {
                    DebridHostState support = PackageLinkState(index, child);
                    SDL_Color tone = LinkHostTone(support);
                    if (support == DebridHostState.Supported)
                        Fill(r, x + 8, y + 22, 7, 44, C((byte)(tone.r / 5), (byte)(tone.g / 5), (byte)(tone.b / 5)));
                    Fill(r, x + 10, y + 24, 3, 40, tone);
                }
                else if (!child) Fill(r, x + 10, y + 24, 3, 40, PackageTint(meta.Kind));
                bool queued = PackageGroupQueued(index, _queueSnapshot);
                bool blocked = meta.Candidate != null && !string.IsNullOrEmpty(meta.Candidate.ResolutionError);
                string title = child ? meta.Hoster : PackageRowTitle(meta);
                TextFit(r, x + 24, y + 15, 24, width - (child ? 80 : 300), title, White);
                string sub = child ? "Alternate mirror" : meta.MirrorCount + (meta.MirrorCount == 1 ? " mirror" : " mirrors");
                if (queued) sub += "  ·  In queue";
                else if (blocked) sub += "  ·  Requires another mirror";
                else if (meta.Candidate != null && meta.Candidate.ExpectedByteSize.GetValueOrDefault() > 0)
                    sub += "  ·  " + DownloadManager.Human(meta.Candidate.ExpectedByteSize.Value);
                int subX = x + 24;
                if (!child)
                {
                    // Group caption in the package tint keeps base / update / DLC rows distinct.
                    string caption = PackageGroupCaption(meta.Kind);
                    TextPx(r, subX, y + 55, 14, caption, PackageTint(meta.Kind));
                    subX += UiFont.MeasurePx(14, caption) + 14;
                }
                TextFit(r, subX, y + 52, 17, x + width - (child ? 62 : 300) - subX, sub, blocked ? Warning : queued ? Ok : Muted);
                if (!child)
                {
                    SDL_Color statusTone;
                    string status = PackageStatusText(index, out statusTone);
                    if (queued) { status = "In queue"; statusTone = Ok; }
                    if (!string.IsNullOrEmpty(status)) DrawPillRight(r, x + width - 50, y + 12, status, statusTone);
                    string host = meta.Hoster ?? "";
                    int hostW = Math.Min(200, UiFont.MeasurePx(16, host));
                    TextFit(r, x + width - 50 - hostW, y + 55, 16, 200, host, on ? Muted : Dim);
                }
                DesignIcon(r, "chevron", x + width - 32, y + 35, 17, on ? White : Dim);
            }
            FlushGlideRing(r);
            DrawScrollBar(r, new SDL_Rect { x = rightX + rightW + 13, y = top, w = 4, h = visible * rowH - 10 }, _detailRows.Count, visible, _detailScroll);
            Fill(r, rightX, 910, rightW, 1, Border);
            if (ShowsProviderStatus)
                DrawSelectedLinkGuidance(r, rightX + 70, 914, rightW - 140, _linkPresentation[_detailRows[focused]].Candidate);
            else TextPx(r, rightX, 930, 16, "Base  →  Update / Backport  →  DLC", Dim);
            string position = (focused + 1) + " / " + _detailRows.Count;
            TextPx(r, rightX + rightW - UiFont.MeasurePx(16, position), 930, 16, position, Dim);
        }

        static string PackageRowTitle(PackageCandidatePresentation meta)
        {
            string title = SamePackageKind(meta.Kind, "dlc") ? meta.PackageTitle : PackageTitle(meta.Kind);
            if (!string.IsNullOrWhiteSpace(meta.Version)) title += "  ·  v" + meta.Version.TrimStart('v', 'V');
            return title;
        }

        PackageCandidate _inspectorCandidate;
        string _inspectorArchive = "Package file";
        string _inspectorFirmware = "Not supplied", _inspectorFirmwareVersion;

        void DrawSelectedPackageInfo(IntPtr r, int x, PackageCandidatePresentation meta)
        {
            const int width = 328;
            var candidate = meta.Candidate;
            if (!object.ReferenceEquals(_inspectorCandidate, candidate) || _inspectorFirmwareVersion != _firmwareVersion)
            {
                _inspectorCandidate = candidate;
                _inspectorFirmwareVersion = _firmwareVersion;
                string required = candidate == null ? "" : candidate.RequiredFirmware;
                if (candidate != null && string.IsNullOrEmpty(required))
                    required = PkgIntegrity.FirmwareRequirement(candidate.Label + " " + candidate.DisplayName);
                _inspectorFirmware = string.IsNullOrEmpty(required) ? "Not supplied" : PkgIntegrity.FirmwareLabel(required, _firmwareVersion);
                _inspectorArchive = "Package file";
                if (candidate != null && !string.IsNullOrEmpty(candidate.ArchiveVolumes))
                {
                    try
                    {
                        int count = ArchiveVolumeSet.Decode(candidate.ArchiveVolumes).Count;
                        _inspectorArchive = count + (count == 1 ? " archive part" : " archive parts");
                    }
                    catch { _inspectorArchive = "Archive details unavailable"; }
                }
                else if (candidate != null && !string.IsNullOrEmpty(candidate.ArchivePassword))
                    _inspectorArchive = "Archive supported";
            }
            TextPx(r, x, 553, 14, "SELECTED PACKAGE", Dim);
            TextFit(r, x, 580, 24, width, SamePackageKind(meta.Kind, "dlc") ? meta.PackageTitle : PackageTitle(meta.Kind), White);
            Fill(r, x, 620, width, 1, Border);
            DrawPackageFact(r, x, 637, "Version", string.IsNullOrWhiteSpace(meta.Version) ? "Not supplied" : "v" + meta.Version.TrimStart('v', 'V'), Muted);
            string size = candidate != null && candidate.ExpectedByteSize.GetValueOrDefault() > 0
                ? DownloadManager.Human(candidate.ExpectedByteSize.Value) : "Not supplied";
            DrawPackageFact(r, x, 669, "Size", size, Muted);
            string firmware = _inspectorFirmware;
            DrawPackageFact(r, x, 701, "Firmware", firmware, firmware.StartsWith("Needs backport", StringComparison.Ordinal) ? Warning : Muted);
            DrawPackageFact(r, x, 733, "Host", meta.Hoster ?? "Unknown", Muted);
            DrawPackageFact(r, x, 765, "Mirrors", meta.MirrorIndex + " of " + meta.MirrorCount, Muted);
            DrawPackageFact(r, x, 797, "Source", meta.Source, Muted);
            DrawPackageFact(r, x, 829, "Files", _inspectorArchive, Muted);
            string note = candidate == null ? "" : candidate.ResolutionError;
            bool failed = !string.IsNullOrEmpty(note);
            if (!failed && candidate != null)
            {
                string label = (candidate.Label ?? "").Trim();
                if (!string.IsNullOrEmpty(label) && !string.Equals(label, meta.PackageTitle, StringComparison.OrdinalIgnoreCase)) note = label;
            }
            if (!string.IsNullOrEmpty(note))
            {
                Fill(r, x, 870, width, 1, Border);
                TextWrapped(r, x, 889, 16, width, note, failed ? Warning : Dim);
            }
        }

        void DrawPackageFact(IntPtr r, int x, int y, string label, string value, SDL_Color color)
        {
            TextPx(r, x, y, 16, label, Dim);
            TextFit(r, x + 98, y, 17, 230, value, color);
        }

        static string TransferSizeLine(DlItem item)
        {
            return DownloadManager.Human(item.Done) + (item.Total > 0 ? " / " + DownloadManager.Human(item.Total) : "");
        }

        static string TransferRateLine(DlItem item, int statsMode = 0)
        {
            if (item.State == DlState.Failed) return item.Error ?? "Download failed";
            if (item.State == DlState.Paused) return "Paused";
            // A parked TorBox preparation is progress of its own; the provider's
            // own metric must not be replaced by a "Measuring speed." placeholder.
            if (item.ParkedForProvider) return !string.IsNullOrEmpty(item.StatusText) ? item.StatusText : "Preparing in TorBox · other downloads continue";
            // A resolving row is not transferring bytes yet, so a rate line would
            // read "Measuring speed." forever. Show the resolver's own progress
            // instead: an uncached provider preparing the file reports continuously.
            if (item.State == DlState.Resolving && !string.IsNullOrEmpty(item.StatusText)) return item.StatusText;
            if (item.State == DlState.Downloading && item.BytesPerSec <= 0 && !string.IsNullOrEmpty(item.StatusText))
                return item.StatusText;
            if (item.State != DlState.Downloading && item.State != DlState.Resolving && !Extracting(item)) return item.StatusText ?? VisibleState(item);
            string rate = item.BytesPerSec > 0 ? (item.BytesPerSec / 1000000.0).ToString("0.00") + " MB/s" : "Measuring speed…";
            string eta = item.EtaSeconds > 0 ? "ETA " + (item.EtaSeconds / 60) + ":" + (item.EtaSeconds % 60).ToString("00") : "ETA —";
            if (statsMode == 2) return rate;
            if (statsMode == 3) return eta;
            return rate + "  ·  " + eta + (Extracting(item) ? "  ·  Extracting" : "");
        }

        void OpenFileDetails(DlItem item)
        {
            if (item == null) return;
            _fileDetailId = item.Id; _fileReadAt = 0;
            lock (_lock) _fileOutputs = new List<FileOutput>();
            try { _fileVolumes = string.IsNullOrEmpty(item.ArchiveVolumes) ? new List<ArchiveVolume>() : ArchiveVolumeSet.Decode(item.ArchiveVolumes); }
            catch { _fileVolumes = new List<ArchiveVolume>(); }
        }

        static string JournalFileState(string value, bool failed, string error)
        {
            string[] record = (value ?? "").Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int task;
            if ((record.Length < 4 || record.Length > 6) || !int.TryParse(record[1], out task)) return "Waiting for dependency";
            bool validKind = record.Length < 6 || record[5] == "6" || record[5] == "7" || record[5] == "8";
            bool verifiedRoute = record.Length >= 5 && record[4] == "6" && validKind;
            verifiedRoute |= record.Length == 6 && record[4] == "7" && record[5] == "6" && task >= 0;
            if (verifiedRoute && record[2] == "1" && record[3] == "0") return "Installed";
            if (failed) return "Failed · " + (error ?? "Installation not confirmed");
            return task >= 0 || record[3] == "2" ? "Installing" : "Waiting to install";
        }

        void RefreshFileOutputs(DlItem item)
        {
            if (_fileReadBusy || (_fileReadAt != 0 && UiElapsed(_fileReadAt) < 1000)) return;
            string id = item.Id ?? "";
            if (id.Length == 0 || id.Length > 100) return;
            foreach (char c in id) if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return;
            _fileReadBusy = true; _fileReadAt = UiTick();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                var rows = new List<FileOutput>();
                try
                {
                    string directory = ResidentDownloadService.ArchiveJournalDirectory(id);
                    if (!string.IsNullOrEmpty(item.DestPath)) {
                        string staged = Path.Combine(Path.GetDirectoryName(item.DestPath), "extract-" + id);
                        if (Directory.Exists(staged)) directory = staged;
                    }
                    string marker = Path.Combine(directory, "packages.txt");
                    if (File.Exists(marker) && new FileInfo(marker).Length <= 32768)
                    {
                        string[] lines = File.ReadAllLines(marker);
                        for (int i = 0; i < lines.Length && i < 256; i++)
                        {
                            string[] fields = lines[i].Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            long size;
                            if (fields.Length != 2 || !long.TryParse(fields[1], out size) || size < 0 || fields[0].Length != 11 || !fields[0].StartsWith("pkg-") || !fields[0].EndsWith(".pkg")) continue;
                            int ordinal; if (!int.TryParse(fields[0].Substring(4, 3), out ordinal) || ordinal < 1 || ordinal > 256) continue;
                            string state = "Waiting for dependency";
                            string journal = Path.Combine(directory, "install-" + (i + 1).ToString("000") + ".txt");
                            if (File.Exists(journal) && new FileInfo(journal).Length < 1024)
                            {
                                state = JournalFileState(File.ReadAllText(journal), item.State == DlState.Failed, item.Error);
                            }
                            rows.Add(new FileOutput { Name = fields[0], Size = size, State = state });
                        }
                    }
                    else if (Directory.Exists(directory))
                    {
                        foreach (string path in Directory.EnumerateFiles(directory, "pkg-*.pkg*"))
                        {
                            if (rows.Count >= 256) break;
                            if (!path.EndsWith(".pkg", StringComparison.Ordinal) && !path.EndsWith(".pkg.part", StringComparison.Ordinal)) continue;
                            rows.Add(new FileOutput { Name = Path.GetFileName(path), Size = new FileInfo(path).Length, State = "Extracting · size so far" });
                        }
                        rows.Sort((a,b) => string.CompareOrdinal(a.Name,b.Name));
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                finally
                {
                    lock (_lock) { if (_fileDetailId == id) { _fileOutputs = rows; _drawerRowsDirty = true; } _fileReadBusy = false; }
                }
            });
        }

        void DrawCharcoalPair(IntPtr r)
        {
            Fill(r, 0, 0, W, H, Bg);
            TextPx(r, ContentX, 135, 42, _pairDownloadMode ? "Send download links" : "Configure your PS4", White);
            TextPx(r, ContentX, 198, 23, "Scan with your phone on the same local network", Muted);
            Card(r, new SDL_Rect { x = ContentX, y = 284, w = 624, h = 600 }, false, Panel, Border);
            Card(r, new SDL_Rect { x = 908, y = 284, w = 772, h = 600 }, false, Panel, Border);
            bool waiting = _pairUiStage == PairUiStage.Waiting;
            if (waiting && _qr != null && _qrSize > 0)
            {
                int module = Math.Max(1, Math.Min(10, 480 / (_qrSize + 8))), total = (_qrSize + 8) * module;
                int x = ContentX + (624 - total) / 2, y = 340 + (480 - total) / 2;
                Fill(r, x, y, total, total, C(255, 255, 255));
                QrCode.Draw(r, _qr, _qrSize, x + 4 * module, y + 4 * module, module, 0, 0, 0);
            }
            else TextCentered(r, new SDL_Rect { x = ContentX + 24, y = 510, w = 576, h = 80 }, 32, _pairUiStage == PairUiStage.Complete ? "Connected" : "Session closed", _pairUiStage == PairUiStage.Complete ? Ok : Muted);
            TextPx(r, 946, 323, 30, waiting ? "Waiting for your phone" : _pairUiStage == PairUiStage.Complete ? "Service connected" : "Pairing ended", White);
            string[] steps = _pairDownloadMode
                ? new[] { "1  Scan the QR code", "2  Paste links, one per line", "3  Queue downloads on your PS4" }
                : new[] { "1  Scan the QR code", "2  Link services and add sources", "3  Customize and save to your PS4" };
            for (int i = 0; i < steps.Length; i++)
            {
                int y = 411 + i * 89;
                TreeBranch(r, 956, y - 20, y + 16, y + 89, 994, i == steps.Length - 1);
                TextFit(r, 1006, y, 25, 620, steps[i], White);
            }
            TextFit(r, 946, 710, 22, 686, waiting ? "Reusable while SSPI is open · Your settings stay saved" : _pairUiDetail, waiting ? Muted : _pairUiStage == PairUiStage.Complete ? Ok : Danger);
            TextFit(r, 946, 762, 19, 686, waiting ? _pairUrlShown : "Return to Link Services to start another session.", Muted);
            TextPx(r, ContentX, 959, 22, _pairUiStage == PairUiStage.Complete ? "× Done    ○ Close" : "○ Close", Muted);
        }
    }
}
