using System;
using System.IO;
using System.Threading;
using SDL2.Types;
using static SDL2.SDL;

namespace Orbis
{
    public partial class SearchWindow
    {
        string _downloadFilesTitle;
        uint _framePrevious, _frameWindowStart;
        int _frameSamples, _frameMisses, _frameLongest;
        IntPtr _headerBrand;
        readonly System.Collections.Generic.Dictionary<string, IntPtr> _caseSizes = new System.Collections.Generic.Dictionary<string, IntPtr>();
        bool _headerBrandTried;
        string _pocketProgressId;
        double _pocketProgress;
        uint _pocketAt;

        void DrawHeaderBrand(IntPtr renderer)
        {
            if (!_headerBrandTried)
            {
                _headerBrandTried = true;
                _headerBrand = LoadBrandTexture(renderer, "sspi-header.rgba", 56, 56);
            }
            if (_headerBrand != IntPtr.Zero)
            {
                var destination = new SDL_Rect { x = 60, y = 30, w = 56, h = 56 };
                SDL_RenderCopy(renderer, _headerBrand, IntPtr.Zero, ref destination);
            }
        }

        int FocusLift(bool focused)
        {
            if (!focused || _cfg.ReduceMotion) return 0;
            double t = Math.Min(1, UiElapsed(_lastInputAt) / 140.0);
            return (int)Math.Round(4 * (1 - Math.Pow(1 - t, 3)));
        }

        int FooterLift(string label)
        {
            if (_cfg.ReduceMotion || _tab != TopTab.Downloads ||
                (label != "Pause" && label != "Resume" && label != "Files" && label != "Cancel")) return 0;
            double t = Math.Min(1, UiElapsed(_lastInputAt) / 200.0);
            return (int)Math.Round(3 * Math.Sin(t * Math.PI));
        }

        void RecordFrameTiming(uint now)
        {
            if (_framePrevious != 0)
            {
                int elapsed = (int)unchecked(now - _framePrevious);
                _frameSamples++;
                if (elapsed > 20) _frameMisses++;
                _frameLongest = Math.Max(_frameLongest, elapsed);
            }
            _framePrevious = now;
            if (_frameWindowStart == 0) _frameWindowStart = now;
            uint duration = unchecked(now - _frameWindowStart);
            if (duration < 10000) return;
            string report = "build=" + BuildIdentity.Label + " fps=" + (_frameSamples * 1000.0 / duration).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) +
                " frames_over_20ms=" + _frameMisses + " longest_ms=" + _frameLongest + " renderer=software 1920x1080";
            if (BuildIdentity.OwnerDebug)
            {
                try { report += " owner_debug=1 managed_heap_bytes=" + GC.GetTotalMemory(false) +
                    " gc0=" + GC.CollectionCount(0) + " gc1=" + GC.CollectionCount(1) + " gc2=" + GC.CollectionCount(2); }
                catch (Exception ex) { report += " owner_debug=1 managed_heap_probe_error=" + ex.GetType().Name; }
            }
            _frameSamples = _frameMisses = _frameLongest = 0; _frameWindowStart = now;
            ThreadPool.QueueUserWorkItem(_ => SspiLog.Write("startup", "frame_sample " + report));
        }

        static DlItem PrimaryTransfer(DownloadGroup group)
        {
            foreach (var item in group.Items)
                if (item.State == DlState.Downloading || item.State == DlState.Resolving || item.State == DlState.Finalizing || item.State == DlState.Installing) return item;
            foreach (var item in group.Items)
                if (item.State == DlState.Failed || item.State == DlState.Paused || item.State == DlState.Queued) return item;
            return group.Items.Count > 0 ? group.Items[0] : null;
        }

        void DrawCase(IntPtr renderer, GameHit game, SDL_Rect box)
        {
            if (!_caseFrameTried) { _caseFrameTried = true; _caseFrame = LoadBrandTexture(renderer, "ps4-case.rgba", 556, 704); }
            string frameKey = box.w + "x" + box.h;
            IntPtr frame;
            if (!_caseSizes.TryGetValue(frameKey, out frame))
            {
                frame = LoadBrandTexture(renderer, "ps4-case-" + frameKey + ".rgba", box.w, box.h);
                _caseSizes[frameKey] = frame;
            }
            if (frame == IntPtr.Zero && _caseFrame == IntPtr.Zero) Fill(renderer, box.x, box.y, box.w, box.h, C(20, 77, 157));
            var art = CaseArtworkBounds(box);
            IntPtr texture; int width, height;
            string key = game == null ? "" : _covers.RequestSized(game.TitleId, game.ImageUrl, art.w, art.h);
            if (_covers.TryGet(key, out texture, out width, out height)) CopyCoverFill(renderer, texture, width, height, art);
            else Fill(renderer, art.x, art.y, art.w, art.h, C(28, 30, 34));
            if (frame != IntPtr.Zero) SDL_RenderCopy(renderer, frame, IntPtr.Zero, ref box);
            else if (_caseFrame != IntPtr.Zero) SDL_RenderCopy(renderer, _caseFrame, IntPtr.Zero, ref box);
        }

        static SDL_Rect CaseArtworkBounds(SDL_Rect box)
        {
            return new SDL_Rect { x = box.x + (int)Math.Round(box.w * .017), y = box.y + (int)Math.Round(box.h * .146),
                w = (int)Math.Round(box.w * .942), h = (int)Math.Round(box.h * .821) };
        }

        void CopyCoverFill(IntPtr renderer, IntPtr texture, int width, int height, SDL_Rect destination)
        {
            if (width <= 0 || height <= 0 || destination.w <= 0 || destination.h <= 0) return;
            double ratio = destination.w / (double)destination.h;
            int cropW = width, cropH = height;
            if (width / (double)height > ratio) cropW = Math.Max(1, (int)Math.Round(height * ratio));
            else cropH = Math.Max(1, (int)Math.Round(width / ratio));
            var source = new SDL_Rect { x = (width - cropW) / 2, y = (height - cropH) / 2, w = cropW, h = cropH };
            _covers.Draw(renderer, texture, ref source, ref destination);
        }

        void CopyDownloadBackdrop(IntPtr renderer, IntPtr texture, int width, int height, SDL_Rect area, bool drawer)
        {
            if (width <= 0 || height <= 0 || area.w <= 0 || area.h <= 0) return;
            double ratio = area.w / (double)area.h;
            int cropW = width, cropH = height;
            if (width / (double)height > ratio) cropW = Math.Max(1, (int)Math.Round(height * ratio));
            else cropH = Math.Max(1, (int)Math.Round(width / ratio));
            int cropX = (width - cropW) / 2, cropY = (height - cropH) / 2;
            int radius = Math.Min(12, Math.Min(area.w, area.h) / 2);
            // Copy disjoint bands without changing native clip state on the
            // selected-backdrop path implicated by the console stall.
            int bands = (drawer ? radius : radius * 2) + 1;
            MarkUiProgress("downloads-backdrop-copy");
            for (int i = 0; i < bands; i++)
            {
                SDL_Rect band;
                if (i == 0) band = new SDL_Rect { x = area.x, y = area.y + radius,
                    w = area.w, h = area.h - (drawer ? radius : radius * 2) };
                else
                {
                    int row = (i - 1) % radius, inset = CornerInsets[radius][row];
                    band = new SDL_Rect { x = area.x + inset, y = i <= radius ? area.y + row : area.y + area.h - 1 - row,
                        w = area.w - inset * 2, h = 1 };
                }
                if (band.w <= 0 || band.h <= 0) continue;
                int left = (int)((long)(band.x - area.x) * cropW / area.w);
                int top = (int)((long)(band.y - area.y) * cropH / area.h);
                int right = (int)(((long)(band.x - area.x + band.w) * cropW + area.w - 1) / area.w);
                int bottom = (int)(((long)(band.y - area.y + band.h) * cropH + area.h - 1) / area.h);
                var source = new SDL_Rect { x = cropX + left, y = cropY + top, w = right - left, h = bottom - top };
                _covers.Draw(renderer, texture, ref source, ref band);
            }
        }

        void DrawGroupKinds(IntPtr renderer, DownloadGroup group, int x, int y, bool active)
        {
            bool baseGame = false, update = false, dlc = false, backport = false;
            foreach (var item in group.Items)
            {
                if (SamePackageKind(item.Kind, "update")) update = true;
                else if (SamePackageKind(item.Kind, "dlc")) dlc = true;
                else if (SamePackageKind(item.Kind, "backport")) backport = true;
                else baseGame = true;
            }
            string main = baseGame ? "Base package" : update ? "Update" : dlc ? "DLC" : "Backport";
            TextPx(renderer, x, y, 21, main, active ? White : Muted); x += UiFont.MeasurePx(21, main) + 20;
            if (update && baseGame) { TextPx(renderer, x, y, 21, "+Update", Warning); x += 110; }
            if (dlc && (baseGame || update)) { TextPx(renderer, x, y, 21, "+DLC", C(190, 164, 216)); x += 83; }
            if (backport && (baseGame || update || dlc)) TextPx(renderer, x, y, 21, "+Backport", C(167, 193, 218));
        }

        void DrawRefinedDownloads(IntPtr renderer)
        {
            MarkUiProgress("downloads-model");
            RefreshQueueModel();
            var rows = _queueRows; ClampDownloadFocus(rows);
            int filterX = ContentX;
            for (int i = 0; i < QueueFilters.Length; i++)
            {
                TextPx(renderer, filterX, 148, 22, QueueFilters[i], i == _queueFilter ? White : Muted);
                if (i == _queueFilter) Fill(renderer, filterX, 189, 26, 2, White);
                filterX += UiFont.MeasurePx(22, QueueFilters[i]) + 35;
            }
            if (rows.Count == 0) { TextPx(renderer, ContentX, 380, 32, "Nothing queued", White); return; }
            // Focus expands within the list; selection order remains stable while jobs update.
            if (_downloadFilesTitle != null) _dlScroll = _dlFocus;
            else EnsureVisible(ref _dlScroll, _dlFocus, rows.Count, 4);
            int y = 224;
            for (int i = _dlScroll; i < rows.Count && i < _dlScroll + 4; i++)
            {
                var row = rows[i]; var group = row.Group; var item = row.Item;
                bool focus = i == _dlFocus;
                bool drawer = focus && group.Key == _downloadFilesTitle;
                int height = focus ? (drawer ? 240 : 320) : 112;
                if (y + height > 948) break;
                var area = new SDL_Rect { x = ContentX, y = y, w = ContentWidth, h = height };
                var frame = area;
                if (drawer) frame.h += DownloadDrawerHeight;
                MarkUiProgress("downloads-card");
                SoftRect(renderer, frame, focus ? C(35, 36, 37) : Panel);
                MarkUiProgress("downloads-artwork-lookup");
                EnsureDownloadArtwork(group);
                if (focus && !string.IsNullOrEmpty(group.ImageUrl))
                {
                    string key = (group.TitleId ?? "") + "-BACKDROP";
                    _covers.Request(key, group.ImageUrl);
                    IntPtr backdrop; int bw, bh;
                    if (_covers.TryGet(key, out backdrop, out bw, out bh)) CopyDownloadBackdrop(renderer, backdrop, bw, bh, area, drawer);
                }
                int coverW = focus ? 142 : 68, coverH = focus ? 184 : 88;
                MarkUiProgress("downloads-cover");
                DrawCase(renderer, new GameHit { TitleId = group.TitleId, Name = group.Name, ImageUrl = group.ImageUrl },
                    new SDL_Rect { x = area.x + 34, y = y + (height - coverH) / 2 - FocusLift(focus), w = coverW, h = coverH });
                MarkUiProgress("downloads-text");
                int tx = area.x + (focus ? 218 : 134), tw = ContentWidth - (focus ? 408 : 420);
                int detailShift = drawer ? -60 : 0;
                TextFit(renderer, tx, y + (focus ? (drawer ? 25 : 45) : 23), focus ? 31 : 26, tw, group.Name, White);
                if (focus)
                {
                    TextPx(renderer, tx, y + (drawer ? 77 : 102), 21, group.TitleId ?? "", Muted);
                    DrawGroupKinds(renderer, group, tx + 164, y + (drawer ? 77 : 102), true);
                    var track = new SDL_Rect { x = tx, y = y + 176 + detailShift, w = ContentWidth - 252, h = 7 };
                    Fill(renderer, track.x, track.y, track.w, track.h, C(81, 83, 85));
                    if (item != null)
                    {
                        double target = item.Total > 0 ? Math.Min(1, Math.Max(0, item.Done / (double)item.Total)) : 0;
                        uint now = UiTick();
                        if (_pocketProgressId != item.Id || _cfg.ReduceMotion || target < _pocketProgress) _pocketProgress = target;
                        else _pocketProgress += (target - _pocketProgress) * Math.Min(1, unchecked(now - _pocketAt) / 140.0);
                        _pocketProgressId = item.Id; _pocketAt = now;
                        Fill(renderer, track.x, track.y, Math.Max(1, (int)(track.w * _pocketProgress)), track.h, White);
                        TextFit(renderer, tx, y + 205 + detailShift, 22, 440, TransferSizeLine(item), White);
                        if (_cfg.DownloadStatsMode != 1)
                        {
                            string rate = item.State == DlState.Failed ? "Files kept for retry" : TransferRateLine(item);
                            if (item.State == DlState.Downloading)
                            {
                                string speed = item.BytesPerSec > 0 ? (item.BytesPerSec / 1000000.0).ToString("0.00") + " MB/s" : "Measuring speed…";
                                string eta = item.EtaSeconds > 0 ? "ETA " + item.EtaSeconds / 60 + ":" + (item.EtaSeconds % 60).ToString("00") : "ETA —";
                                rate = _cfg.DownloadStatsMode == 2 ? speed : _cfg.DownloadStatsMode == 3 ? eta : speed + "  ·  " + eta;
                            }
                            TextFit(renderer, tx + 460, y + 205 + detailShift, 23, ContentWidth - 712, rate, White);
                        }
                        if (_cfg.NerdStats && !drawer) DrawFocusedSparkline(renderer, new SDL_Rect { x = tx, y = y + 133, w = ContentWidth - 252, h = 31 }, item);
                        var failed = group.Items.Find(entry => entry.State == DlState.Failed);
                        if (failed != null) TextFit(renderer, tx, y + 258 + detailShift, 19, tw, "Needs attention · " + PackageTitle(failed.Kind) + " · " + FriendlyTransferFailure(failed.Error), Danger);
                        else TextFit(renderer, tx, y + 258 + detailShift, 21, tw, ActiveTransferOwnerText(item), StateColor(item.State));
                    }
                    TextFit(renderer, area.x + area.w - 172, y + (drawer ? 186 : 265), 18, 150,
                        (drawer ? "L2  Close  ˄" : "R2  " + group.Items.Count + " files  ˅"), White);
                }
                else
                {
                    DrawGroupKinds(renderer, group, tx, y + 70, false);
                    TextFit(renderer, area.x + area.w - 252, y + 46, 21, 220, DownloadGroupStatus(group), Muted);
                }
                y += height;
                if (drawer) { MarkUiProgress("downloads-drawer"); DrawDownloadDrawer(renderer, group, y); y += DownloadDrawerHeight; }
                if (focus)
                {
                    MarkUiProgress("downloads-outline");
                    SDL_Color stateTone = DownloadRingColor(item);
                    bool moving = item != null && (item.State == DlState.Downloading || item.State == DlState.Resolving ||
                        item.State == DlState.Finalizing || item.State == DlState.Installing);
                    double pulse = !_cfg.ReduceMotion && moving ? .65 + .35 * (.5 + .5 * Math.Sin(UiTick() * Math.PI / 1600.0)) : 1;
                    var halo = new SDL_Rect { x = frame.x - 2, y = frame.y - 2, w = frame.w + 4, h = frame.h + 4 };
                    StrokeRect(renderer, halo, C((byte)(stateTone.r * pulse / 3), (byte)(stateTone.g * pulse / 3), (byte)(stateTone.b * pulse / 3)), 1);
                    StrokeRect(renderer, frame, C((byte)(stateTone.r * pulse), (byte)(stateTone.g * pulse), (byte)(stateTone.b * pulse)), 2);
                }
                y += 20;
            }
            if (rows.Count > 4) DrawScrollBar(renderer, new SDL_Rect { x = ContentX + ContentWidth + 16, y = 224, w = 4, h = 716 }, rows.Count, 4, _dlScroll);
            MarkUiProgress("downloads-complete");
        }

        static SDL_Color DownloadRingColor(DlItem item)
        {
            if (item == null) return C(146, 153, 163);
            switch (item.State)
            {
                case DlState.Downloading: return C(87, 196, 220);
                case DlState.Resolving: return C(180, 155, 220);
                case DlState.Finalizing:
                case DlState.Installing:
                case DlState.Submitted: return C(234, 191, 102);
                case DlState.Failed: return C(229, 119, 134);
                case DlState.Completed:
                case DlState.Installed: return C(131, 207, 164);
                default: return C(146, 153, 163);
            }
        }

    }
}
