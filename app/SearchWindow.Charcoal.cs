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
        int _fileScroll;
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

        void TreeBranch(IntPtr r, int stemX, int top, int mid, int bottom, int endX, bool last)
        {
            Fill(r, stemX, top, 2, Math.Max(1, (last ? mid : bottom) - top), Muted);
            // A short dash makes the parent/child relationship visible without another frame.
            Fill(r, stemX, mid, Math.Max(1, endX - stemX - 8), 2, Muted);
        }

        List<GameHit> FilteredResults()
        {
            if (object.ReferenceEquals(_filteredSource, _results) && _filteredRegion == _regionFilter) return _filteredResults;
            _filteredSource = _results; _filteredRegion = _regionFilter;
            _filteredResults = new List<GameHit>();
            foreach (var hit in _results)
            {
                string region = (hit.Region ?? "").ToUpperInvariant();
                bool known = region == "EU" || region == "US" || region == "JP";
                if (_regionFilter == 0 || (_regionFilter == 4 ? !known : region == RegionFilters[_regionFilter])) _filteredResults.Add(hit);
            }
            _focus = _listScroll = 0;
            return _filteredResults;
        }

        bool PackageMatches(int index)
        {
            var meta = _linkPresentation[index];
            if (_packageFilter > 0 && (_packageFilter == 4 ? !string.Equals(meta.Kind, "backport", StringComparison.OrdinalIgnoreCase) : PackageKindOrder(meta.Kind) != _packageFilter - 1)) return false;
            if (_hostFilter > 0 && (_hostFilter > _detailHosts.Count || !string.Equals(meta.Hoster, _detailHosts[_hostFilter - 1], StringComparison.OrdinalIgnoreCase))) return false;
            if (_latestUpdateOnly && SamePackageKind(meta.Kind, "update"))
                for (int i = 0; i < _linkPresentation.Count; i++)
                    if (SamePackageKind(_linkPresentation[i].Kind, "update") && ComparePackageVersions(_linkPresentation[i].Version, meta.Version) > 0) return false;
            return true;
        }

        void ResetPackageFilters()
        {
            _packageFilter = _hostFilter = 0; _latestUpdateOnly = false;
            _detailHosts.Clear(); _expandedPackageGroups.Clear();
        }

        bool PackageGroupQueued(int index, IList<DlItem> items)
        {
            if (index < 0 || index >= _linkPresentation.Count) return false;
            string group = DetailGroupKey(_linkPresentation[index]);
            for (int i = 0; i < _linkPresentation.Count && i < _linkCandidates.Count; i++)
                if (string.Equals(DetailGroupKey(_linkPresentation[i]), group, StringComparison.OrdinalIgnoreCase) && IsCandidateQueued(_linkCandidates[i], items)) return true;
            return false;
        }

        void RefreshQueueModel(bool force = false)
        {
            if (!force && _queueModelReady && UiElapsed(_queueModelAt) < 250) return;
            _queueModelAt = UiTick(); _queueModelReady = true;
            _queueSnapshot = _dlMgr.Snapshot();
            _queueGroups = BuildDownloadGroups(_queueSnapshot);
            _queueRows = BuildDownloadRows(_queueGroups);
            _downloadView = _queueSnapshot;
        }

        bool QueueGroupMatches(DownloadGroup group)
        {
            if (_queueFilter == 0) return true;
            foreach (var item in group.Items)
            {
                if (_queueFilter == 1 && (item.State == DlState.Downloading || item.State == DlState.Resolving || item.State == DlState.Finalizing || item.State == DlState.Installing)) return true;
                if (_queueFilter == 2 && (item.State == DlState.Failed || item.State == DlState.Paused || item.State == DlState.Canceled)) return true;
            }
            return false;
        }

        static bool Extracting(DlItem item)
        {
            return item != null && (item.StatusText ?? "").IndexOf("extract", StringComparison.OrdinalIgnoreCase) >= 0 &&
                (item.State == DlState.Downloading || item.State == DlState.Finalizing);
        }

        static string FileBytes(long value)
        {
            if (value >= 1000000000) return (value / 1000000000.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " GB";
            return (value / 1000000.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB";
        }

        static string VisibleState(DlItem item)
        {
            if (item == null) return "Queued";
            if (Extracting(item)) return "Extracting";
            if ((item.StatusText ?? "").StartsWith("Installing packages", StringComparison.OrdinalIgnoreCase)) return "Installing in order";
            if (item.State == DlState.Queued && !string.IsNullOrEmpty(item.InstallAfterId)) return "Waiting for dependency";
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
            TextPx(r, ContentX + ContentWidth - UiFont.MeasurePx(18, countText), 330, 18, countText, Dim);
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
                    if (focused) StrokeRect(r, new SDL_Rect { x = x - 6, y = art.y - 6, w = posterW + 12, h = art.h + 12 }, Accent, 2);
                    TextFit(r, x, 693, 22, posterW, LibraryTitle(hit), White);
                    TextFit(r, x, 731, 17, posterW, hit.TitleId ?? "", Muted);
                    lock (_lock) if (_libraryHasUpdate.Contains(hit.TitleId ?? "")) SoftRect(r, new SDL_Rect { x = x + posterW - 21, y = art.y + 14, w = 9, h = 9 }, Warning);
                }
                Fill(r, ContentX, 794, ContentWidth, 1, Border);
                var chosen = _libraryGames[selected];
                TextFit(r, ContentX, 818, 22, 1050, LibraryTitle(chosen), White);
                string updateInfo; bool hasUpdate;
                lock (_lock) { hasUpdate = _libraryUpdateInfo.TryGetValue(chosen.TitleId ?? "", out updateInfo); }
                TextFit(r, ContentX, 858, 18, 1100, hasUpdate ? updateInfo : chosen.TitleId, hasUpdate ? Warning : Muted);
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
            if (busy) { DrawCharcoalLoading(r, false); return; }
            var list = FilteredResults();
            string count = list.Count + (list.Count == 1 ? " title" : " titles");
            string queryLine = "“" + (_query ?? "") + "”  ·  " + count;
            TextFit(r, ContentX, 150, 22, ContentWidth, queryLine, White);
            int chipW = 138, chipGap = 8;
            int chipsTotal = RegionFilters.Length * chipW + (RegionFilters.Length - 1) * chipGap;
            int chipStart = ContentX + (ContentWidth - chipsTotal) / 2;
            for (int i = 0; i < RegionFilters.Length; i++) FilterChip(r, chipStart + i * (chipW + chipGap), 218, chipW, RegionFilters[i], _regionFilter == i);
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
                DesignCard(r, new SDL_Rect { x = ContentX, y = y, w = ContentWidth, h = 114 }, on);
                DrawCase(r, hit, new SDL_Rect { x = ContentX + 22, y = y + 9 - FocusLift(on), w = 74, h = 94 });
                TextFit(r, ContentX + 122, y + 18, 27, ContentWidth - 440, hit.Name, White);
                TextFit(r, ContentX + 122, y + 65, 19, ContentWidth - 440, hit.TitleId ?? "", Muted);
                TextPx(r, ContentX + ContentWidth - 297, y + 45, 18, "View packages", on ? White : Dim);
                DrawPillRight(r, ContentX + ContentWidth - 66, y + 41, hit.Region ?? "?", Muted);
                DesignIcon(r, "chevron", ContentX + ContentWidth - 43, y + 46, 21, on ? White : Dim);
            }
            DrawScrollBar(r, new SDL_Rect { x = ContentX + ContentWidth + 16, y = top, w = 4, h = 618 }, list.Count, visible, _listScroll);
        }

        void DrawCharcoalLoading(IntPtr r, bool resolving)
        {
            if (resolving && _selected != null) { DrawGamePackagePage(r, true); return; }
            TextCentered(r, new SDL_Rect { x = ContentX, y = 385, w = ContentWidth, h = 58 }, 32, "Searching for “" + (_query ?? "") + "”", White);
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
            const int leftX = 240, rightX = 568, rightW = 1112;
            DrawCase(r, _selected, new SDL_Rect { x = leftX + 20, y = 164, w = 240, h = 304 });
            // Left title facts restore detail without touching queue/install behavior.
            TextFit(r, leftX, 478, 20, 280, LibraryTitle(_selected), White);
            string leftIdentity = _selected.TitleId ?? "";
            if (!string.IsNullOrWhiteSpace(_selected.Region) && _selected.Region != "?") leftIdentity += "  ·  " + _selected.Region;
            TextFit(r, leftX, 504, 17, 280, leftIdentity, Muted);
            TextFit(r, leftX, 528, 17, 280, PackageSizeSummary(), Muted);
            bool installedLeft = _libraryGames.Exists(g => string.Equals(g.TitleId, _selected.TitleId, StringComparison.OrdinalIgnoreCase));
            TextFit(r, leftX, 552, 17, 280, installedLeft ? "In your library" : "Not installed · PS4 " + _firmwareVersion, installedLeft ? Ok : Muted);
            TextPx(r, rightX, 158, 16, "AVAILABLE PACKAGES", Dim);
            TextFit(r, rightX, 195, 36, rightW, LibraryTitle(_selected), White);
            string identity = _selected.TitleId ?? "";
            if (!string.IsNullOrWhiteSpace(_selected.Region) && _selected.Region != "?") identity += "  ·  " + _selected.Region;
            TextFit(r, rightX, 254, 18, rightW - 230, identity, Muted);
            bool installed = installedLeft;
            if (installed) TextPx(r, rightX + rightW - UiFont.MeasurePx(18, "In your library"), 254, 18, "In your library", Ok);
            if (busy)
            {
                TextPx(r, rightX, 325, 20, "Checking packages and mirrors…", Muted);
                DrawActivityRail(r, new SDL_Rect { x = rightX, y = 373, w = rightW, h = 4 });
                for (int i = 0; i < 4; i++)
                {
                    int y = 413 + i * 94;
                    Fill(r, rightX + 24, y + 20, 64, 24, Raised);
                    Fill(r, rightX + 128, y + 18, 380 - i * 43, 14, Raised);
                    Fill(r, rightX + 128, y + 52, 230, 10, Raised);
                    Fill(r, rightX, y + 85, rightW, 1, Border);
                }
                return;
            }
            Fill(r, rightX, 346, rightW, 1, Border);
            for (int i = 0; i < PackageFilters.Length; i++)
            {
                int x = rightX + i * rightW / PackageFilters.Length, w = rightW / PackageFilters.Length;
                string label = PackageFilters[i] + "  " + _detailTypeCounts[i];
                TextCentered(r, new SDL_Rect { x = x, y = 300, w = w, h = 38 }, 20, label, _packageFilter == i ? White : _detailTypeCounts[i] > 0 ? Muted : Dim);
                if (_packageFilter == i) Fill(r, x + 24, 345, w - 48, 2, Accent);
            }
            GamepadIcons.Draw(r, "l2", rightX, 369, 26);
            TextFit(r, rightX + 34, 372, 18, 380, (_hostFilter == 0 ? "Any host" : _detailHosts[_hostFilter - 1]), Muted);
            GamepadIcons.Draw(r, "triangle", rightX + 470, 369, 26);
            TextFit(r, rightX + 504, 372, 18, 300, "Mirrors", Muted);
            GamepadIcons.Draw(r, "r2", rightX + rightW - 247, 369, 26);
            TextFit(r, rightX + rightW - 213, 372, 18, 213, (_latestUpdateOnly ? "Latest update" : "All update versions"), Muted);
            if (_detailRows.Count == 0)
            {
                DesignIcon(r, "download", rightX + rightW / 2 - 20, 470, 40, Dim);
                TextCentered(r, new SDL_Rect { x = rightX, y = 548, w = rightW, h = 45 }, 28, string.IsNullOrEmpty(_resolveError) ? "No matching packages" : "Packages unavailable", White);
                TextWrappedCentered(r, rightX + 90, 610, 20, rightW - 180, string.IsNullOrEmpty(_resolveError) ? "Try another package type, host or version." : _resolveError, Muted);
                return;
            }
            RefreshQueueModel();
            const int top = 418, rowH = 94, visible = 5;
            EnsureVisible(ref _detailScroll, _detailFocus, _detailRows.Count, visible);
            DrawSelectedPackageInfo(r, leftX, _linkPresentation[_detailRows[_detailFocus]]);
            for (int i = 0; i < visible && _detailScroll + i < _detailRows.Count; i++)
            {
                int ordinal = _detailScroll + i, index = _detailRows[ordinal], y = top + i * rowH;
                var meta = _linkPresentation[index]; bool child = DetailRowIsChild(ordinal), on = ordinal == _detailFocus;
                int x = rightX + (child ? 46 : 0), width = rightW - (child ? 46 : 0);
                if (child) TreeBranch(r, rightX + 22, Math.Max(top, y - 13), y + 45, y + rowH, x, DetailRowIsLastChild(ordinal));
                var rect = new SDL_Rect { x = x, y = y, w = width, h = 86 };
                if (on) DesignCard(r, rect, true); else Fill(r, x + 14, y + 85, width - 28, 1, Border);
                string group = DetailGroupKey(meta); int hosts = 0;
                for (int j = 0; j < _linkPresentation.Count; j++) if (PackageMatches(j) && DetailGroupKey(_linkPresentation[j]) == group) hosts++;
                bool queued = PackageGroupQueued(index, _queueSnapshot);
                int tx = x + (child ? 22 : 131), textW = width - (child ? 230 : 335);
                if (!child)
                {
                    var tag = new SDL_Rect { x = x + 23, y = y + 17, w = 88, h = 32 };
                    TextCentered(r, tag, 15, KindShort(meta.Kind), PackageTint(meta.Kind));
                }
                TextFit(r, tx, y + 16, 23, textW, child ? meta.Hoster : PackageDisplayTitle(meta.Kind, meta.PackageTitle, _selected.Name, meta.Hoster, meta.Version), White);
                string sizeHint = meta.Candidate != null && meta.Candidate.ExpectedByteSize.GetValueOrDefault() > 0
                    ? DownloadManager.Human(meta.Candidate.ExpectedByteSize.Value) : "";
                string sub = queued ? "In queue" : child ? "Alternate mirror" : hosts + (hosts == 1 ? " mirror" : " mirrors");
                if (!string.IsNullOrEmpty(sizeHint)) sub += " · " + sizeHint;
                SDL_Color subColor = queued ? Ok : Muted;
                if (meta.Candidate != null && !child)
                {
                    string required = meta.Candidate.RequiredFirmware;
                    if (string.IsNullOrEmpty(required)) required = PkgIntegrity.FirmwareRequirement(meta.Candidate.Label + " " + meta.Candidate.DisplayName);
                    if (!string.IsNullOrEmpty(required))
                    {
                        string fwLabel = PkgIntegrity.FirmwareLabel(required, _firmwareVersion);
                        bool incompatible = fwLabel.StartsWith("Needs backport", StringComparison.Ordinal);
                        if (_cfg.ShowFirmwareHints || incompatible)
                        {
                            sub += " · " + fwLabel;
                            if (incompatible) subColor = Warning;
                        }
                    }
                    else if (_cfg.ShowFirmwareHints)
                    {
                        sub += " · Firmware not supplied";
                    }
                }
                TextFit(r, tx, y + 50, 17, textW, sub, subColor);
                if (!child) TextFit(r, x + width - 188, y + 29, 17, 146, meta.Hoster, HosterColor(meta.Hoster));
                DesignIcon(r, "chevron", x + width - 34, y + 33, 18, on ? White : Dim);
            }
            DrawScrollBar(r, new SDL_Rect { x = rightX + rightW + 13, y = top, w = 4, h = 462 }, _detailRows.Count, visible, _detailScroll);
            TextFit(r, rightX, 905, 17, rightW, "Install order   Base → Update → DLC   ·   Square queues recommended", Dim);
        }

        void DrawSelectedPackageInfo(IntPtr r, int x, PackageCandidatePresentation meta)
        {
            TextPx(r, x, 580, 15, "SELECTED PACKAGE", Dim);
            string name = SamePackageKind(meta.Kind, "dlc") ? meta.PackageTitle : PackageTitle(meta.Kind);
            TextWrapped(r, x, 612, 22, 280, name, White);
            Fill(r, x, 700, 280, 1, Border);
            TextPx(r, x, 716, 17, "Version", Dim);
            TextFit(r, x + 98, 716, 18, 182, string.IsNullOrWhiteSpace(meta.Version) ? "Not supplied" : "v" + meta.Version.TrimStart('v', 'V'), Muted);
            string size = meta.Candidate != null && meta.Candidate.ExpectedByteSize.GetValueOrDefault() > 0
                ? DownloadManager.Human(meta.Candidate.ExpectedByteSize.Value) : "Not supplied";
            TextPx(r, x, 748, 17, "Size", Dim);
            TextFit(r, x + 98, 748, 18, 182, size, Muted);
            string required = meta.Candidate == null ? "" : meta.Candidate.RequiredFirmware;
            TextPx(r, x, 780, 17, "Firmware", Dim);
            string fwText = string.IsNullOrEmpty(required) ? "Not supplied" : required + "+";
            if (meta.Candidate != null && string.IsNullOrEmpty(required))
            {
                string inferred = PkgIntegrity.FirmwareRequirement(meta.Candidate.Label + " " + meta.Candidate.DisplayName);
                if (!string.IsNullOrEmpty(inferred)) fwText = PkgIntegrity.FirmwareLabel(inferred, _firmwareVersion);
            }
            TextFit(r, x + 98, 780, 18, 182, fwText, Muted);
            TextPx(r, x, 812, 17, "Host", Dim);
            TextFit(r, x + 98, 812, 18, 182, meta.Hoster ?? "Unknown host", HosterColor(meta.Hoster));
            TextPx(r, x, 844, 17, "Mirrors", Dim);
            TextFit(r, x + 98, 844, 18, 182, meta.MirrorCount + (meta.MirrorCount == 1 ? " host" : " hosts"), Muted);
            TextPx(r, x, 876, 15, "SOURCE", Dim);
            TextFit(r, x, 900, 18, 280, meta.Source, Muted);
        }

        static string TransferSizeLine(DlItem item)
        {
            return DownloadManager.Human(item.Done) + (item.Total > 0 ? " / " + DownloadManager.Human(item.Total) : "");
        }

        static string TransferRateLine(DlItem item)
        {
            if (item.State == DlState.Failed) return item.Error ?? "Download failed";
            if (item.State == DlState.Paused) return "Paused";
            if (item.State != DlState.Downloading && item.State != DlState.Resolving && !Extracting(item)) return item.StatusText ?? VisibleState(item);
            string rate = item.BytesPerSec > 0 ? (item.BytesPerSec / 1000000.0).ToString("0.00") + " MB/s" : "Measuring speed…";
            string eta = item.EtaSeconds > 0 ? "ETA " + (item.EtaSeconds / 60) + ":" + (item.EtaSeconds % 60).ToString("00") : "ETA —";
            return rate + "  ·  " + eta + (Extracting(item) ? "  ·  Extracting" : "");
        }

        void DrawCharcoalDownloads(IntPtr r)
        {
            RefreshQueueModel();
            if (!string.IsNullOrEmpty(_fileDetailId)) { DrawFileDetails(r); return; }
            int active = 0, extracting = 0;
            foreach (var item in _queueSnapshot) { if (Extracting(item)) extracting++; else if (item.State == DlState.Downloading) active++; }
            TextPx(r, ContentX, 132, 42, "Downloads", White);
            TextPx(r, ContentX, 190, 22, _queueGroups.Count + (_queueGroups.Count == 1 ? " game" : " games") + "  ·  " + _queueSnapshot.Count + (_queueSnapshot.Count == 1 ? " package" : " packages") + (active > 0 ? "  ·  " + active + " downloading" : "") + (extracting > 0 ? "  ·  " + extracting + " extracting" : ""), Muted);
            TextPx(r, ContentX, 249, 20, "L2   " + QueueFilters[_queueFilter] + " downloads", White);
            TextFit(r, ContentX + ContentWidth - 400, 249, 20, 400, "LEFT / RIGHT   Expand / collapse", Muted);
            Fill(r, ContentX, 287, ContentWidth, 1, Border);
            var rows = _queueRows; ClampDownloadFocus(rows);
            if (rows.Count == 0) { TextPx(r, ContentX + 28, 396, 30, _queueSnapshot.Count == 0 ? "Nothing queued" : "No jobs match this filter", White); return; }
            const int top = 308, rowH = 120, visible = 5;
            EnsureVisible(ref _dlScroll, _dlFocus, rows.Count, visible);
            for (int i = 0; i < visible && _dlScroll + i < rows.Count; i++)
            {
                int index = _dlScroll + i, y = top + i * rowH; var tree = rows[index]; var group = tree.Group; var item = tree.Item;
                bool child = !tree.IsRoot; int x = ContentX + (child ? 72 : 0), width = ContentWidth - (child ? 72 : 0);
                if (child) TreeBranch(r, ContentX + 28, Math.Max(top, y - 14), y + 51, y + rowH, x, tree.IsLastChild);
                Card(r, new SDL_Rect { x = x, y = y, w = width, h = 106 }, index == _dlFocus, Panel, Accent);
                if (!child)
                {
                    EnsureDownloadArtwork(group);
                    DrawCover(r, new GameHit { TitleId = group.TitleId, Name = group.Name, ImageUrl = group.ImageUrl }, new SDL_Rect { x = x + 18, y = y + 16, w = 66, h = 74 });
                }
                else TextPx(r, x + 24, y + 38, 17, KindShort(item.Kind), Dim);
                int tx = x + (child ? 136 : 112), textW = width - (child ? 470 : 446);
                TextFit(r, tx, y + 9, 27, textW, child ? PackageDisplayTitle(item.Kind, item.Label, group.Name, "", item.PackageVersion) : group.Name, White);
                TextFit(r, tx, y + 49, 20, textW, child ? TransferRateLine(item) : group.TitleId + "  ·  " + PackageMix(group), Muted);
                string state = child ? VisibleState(item) : DownloadGroupStatus(group);
                if (!child) foreach (var member in group.Items) if (Extracting(member)) { state = "Extracting"; break; }
                TextFit(r, x + width - 315, y + 14, 20, 290, state, child ? StateColor(item.State) : DownloadGroupColor(group));
                if (child)
                {
                    if (_cfg.NerdStats && index == _dlFocus) DrawFocusedSparkline(r, new SDL_Rect { x = tx, y = y + 79, w = textW, h = 21 });
                    else DrawProgress(r, new SDL_Rect { x = tx, y = y + 84, w = textW, h = 6 }, item);
                    TextFit(r, x + width - 315, y + 49, 18, 290, TransferSizeLine(item), Muted);
                }
                else
                {
                    long done, total; DownloadGroupProgress(group, out done, out total);
                    if (_collapsedDownloadGroups.Contains(group.TitleId ?? ""))
                    {
                        Fill(r, tx, y + 84, textW, 6, Raised);
                        if (total > 0) Fill(r, tx, y + 84, (int)Math.Min(textW, Math.Max(0, done * (double)textW / total)), 6, Accent);
                    }
                    TextFit(r, x + width - 315, y + 48, 18, 290, DownloadManager.Human(done) + (total > 0 ? " / " + DownloadManager.Human(total) : ""), Muted);
                    if (_collapsedDownloadGroups.Contains(group.TitleId ?? "")) TextFit(r, tx, y + 49, 18, textW, GroupProgressLine(group, done, total), Muted);
                    TextFit(r, x + width - 315, y + 77, 18, 290, (_collapsedDownloadGroups.Contains(group.TitleId ?? "") ? "+ Expand · " : "− Collapse · ") + group.Items.Count + (group.Items.Count == 1 ? " package" : " packages"), Muted);
                }
            }
            DrawScrollBar(r, new SDL_Rect { x = ContentX + ContentWidth + 16, y = top, w = 5, h = 586 }, rows.Count, visible, _dlScroll);
            TextPx(r, ContentX, 932, 20, "Install order   Base → Update → DLC", Muted);
        }

        void OpenFileDetails(DlItem item)
        {
            if (item == null) return;
            _fileDetailId = item.Id; _fileScroll = 0; _fileReadAt = 0;
            lock (_lock) _fileOutputs = new List<FileOutput>();
            try { _fileVolumes = string.IsNullOrEmpty(item.ArchiveVolumes) ? new List<ArchiveVolume>() : ArchiveVolumeSet.Decode(item.ArchiveVolumes); }
            catch { _fileVolumes = new List<ArchiveVolume>(); }
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
                    string directory = Path.Combine(AppSettings.DataDir, "resident", "archives", id);
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
                            string state = "Extracted · waiting";
                            string journal = Path.Combine(directory, "install-" + (i + 1).ToString("000") + ".txt");
                            if (File.Exists(journal) && new FileInfo(journal).Length < 1024)
                            {
                                string[] record = File.ReadAllText(journal).Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                                int task;
                                if (record.Length == 4 && int.TryParse(record[1], out task)) state = record[2] == "1" ? "BGFT transfer complete" : task >= 0 ? "Submitted to BGFT" : "Preparing installation";
                            }
                            rows.Add(new FileOutput { Name = fields[0], Size = size, State = state });
                        }
                    }
                    else if (Directory.Exists(directory))
                    {
                        foreach (string path in Directory.EnumerateFiles(directory, "pkg-*.pkg"))
                        {
                            if (rows.Count >= 256) break;
                            rows.Add(new FileOutput { Name = Path.GetFileName(path), Size = new FileInfo(path).Length, State = "Extracting · size so far" });
                        }
                        rows.Sort((a,b) => string.CompareOrdinal(a.Name,b.Name));
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                finally
                {
                    lock (_lock) { if (_fileDetailId == id) _fileOutputs = rows; _fileReadBusy = false; }
                }
            });
        }

        void DrawFileDetails(IntPtr r)
        {
            DlItem item = _queueSnapshot.Find(x => x.Id == _fileDetailId);
            if (item == null) { _fileDetailId = null; return; }
            RefreshFileOutputs(item);
            List<FileOutput> outputs; lock (_lock) outputs = _fileOutputs;
            bool extracting = Extracting(item), archive = _fileVolumes.Count > 0 || extracting || outputs.Count > 0;
            int stage = item.State == DlState.Submitted || item.State == DlState.Installed || item.State == DlState.Installing || (item.StatusText ?? "").StartsWith("Installing packages") ? 3 : extracting ? 1 : item.State == DlState.Finalizing || item.State == DlState.Completed ? 2 : 0;
            TextFit(r, ContentX, 132, 38, ContentWidth, item.Name ?? item.TitleId, White);
            TextFit(r, ContentX, 191, 22, ContentWidth, item.TitleId + " · " + (archive ? _fileVolumes.Count + " archive volumes" : "Direct package · extraction skipped"), Muted);
            string[] stages = { "Download", archive ? "Extract PKGs" : "Direct PKG", "Verify files", "Install in order" };
            for (int i = 0; i < 4; i++) FilterChip(r, ContentX + i * 364, 246, 344, (i + 1) + "  " + stages[i], stage == i);
            Card(r, new SDL_Rect { x = ContentX, y = 320, w = ContentWidth, h = 172 }, false, Panel, Border);
            TextFit(r, ContentX + 26, 340, 30, ContentWidth - 52, VisibleState(item), White);
            TextFit(r, ContentX + 26, 389, 22, ContentWidth - 52, !string.IsNullOrEmpty(item.Error) ? item.Error : item.StatusText ?? "Waiting for progress", Muted);
            DrawProgress(r, new SDL_Rect { x = ContentX + 26, y = 439, w = 800, h = 8 }, item);
            TextFit(r, ContentX + 860, 430, 21, 550, item.State == DlState.Installed ? "Installation confirmed" : stage == 3 ? "Waiting for install confirmation" : FormatDlLine(item), Muted);
            TextPx(r, ContentX, 522, 26, archive && stage == 0 ? "Archive volumes" : "Package files", White);
            var related = new List<DlItem>();
            foreach (var child in _queueSnapshot) if (child.Id == item.Id || (!string.IsNullOrEmpty(item.TitleId) && child.TitleId == item.TitleId)) related.Add(child);
            int count = archive && stage == 0 ? _fileVolumes.Count : outputs.Count > 0 ? outputs.Count : related.Count;
            _fileScroll = Math.Max(0, Math.Min(_fileScroll, Math.Max(0, count - 4)));
            long volumeTotal = 0, offset = 0; foreach (var v in _fileVolumes) volumeTotal += v.Size;
            for (int i = 0; i < count; i++)
            {
                string name, state, size;
                if (archive && stage == 0)
                {
                    var volume = _fileVolumes[i]; name = volume.Name; size = volume.Size > 0 ? FileBytes(volume.Size) : "Size pending";
                    bool known = volumeTotal > 0 && item.Total == volumeTotal && volume.Size > 0;
                    state = known && item.Done >= offset + volume.Size ? "Downloaded" : known && item.Done > offset ? "Downloading" : "Waiting";
                    offset += volume.Size;
                }
                else if (outputs.Count > 0) { var output = outputs[i]; name = output.Name; size = FileBytes(output.Size); state = output.State; }
                else { var child = related[i]; name = string.IsNullOrEmpty(child.Label) ? PackageTitle(child.Kind) : child.Label; size = child.Total > 0 ? FileBytes(child.Total) : "Size pending"; state = VisibleState(child); }
                if (i < _fileScroll || i >= _fileScroll + 4) continue;
                int y = 576 + (i - _fileScroll) * 80;
                TreeBranch(r, ContentX + 18, y - 8, y + 30, y + 80, ContentX + 64, i == count - 1);
                Card(r, new SDL_Rect { x = ContentX + 64, y = y, w = ContentWidth - 64, h = 68 }, false, Panel, Border);
                TextFit(r, ContentX + 86, y + 15, 22, 790, name, White);
                TextFit(r, ContentX + 910, y + 19, 20, 180, size, Muted);
                TextFit(r, ContentX + 1110, y + 19, 20, 300, state, Muted);
            }
            if (archive && stage == 1) TextFit(r, ContentX, 925, 20, ContentWidth, "Extracted output appears as it is reported. Download and extraction use separate byte totals.", Muted);
            else TextFit(r, ContentX, 925, 20, ContentWidth, "↑ / ↓ Scroll files    ·    CIRCLE returns to queue    ·    Files are kept until installation is confirmed", Muted);
        }

        void DrawCharcoalPair(IntPtr r)
        {
            Fill(r, 0, 0, W, H, Bg);
            TextPx(r, ContentX, 135, 42, "Configure your PS4", White);
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
            string[] steps = { "1  Scan the QR code", "2  Link services and add sources", "3  Customize and save to your PS4" };
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
