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
            _frameSamples = _frameMisses = _frameLongest = 0; _frameWindowStart = now;
            ThreadPool.QueueUserWorkItem(_ => { try { File.WriteAllText(Path.Combine(AppSettings.DataDir, "frame-timing.log"), report + "\n"); } catch { } });
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
            if (frame != IntPtr.Zero) SDL_RenderCopy(renderer, frame, IntPtr.Zero, ref box);
            else if (_caseFrame != IntPtr.Zero) SDL_RenderCopy(renderer, _caseFrame, IntPtr.Zero, ref box);
            else Fill(renderer, box.x, box.y, box.w, box.h, C(20, 77, 157));
            var art = new SDL_Rect { x = box.x + (int)Math.Round(box.w * .017), y = box.y + (int)Math.Round(box.h * .146), w = (int)Math.Round(box.w * .942), h = (int)Math.Round(box.h * .821) };
            IntPtr texture; int width, height;
            string key = game == null ? "" : _covers.RequestSized(game.TitleId, game.ImageUrl, art.w, art.h);
            if (_covers.TryGet(key, out texture, out width, out height)) CopyCoverFill(renderer, texture, width, height, art);
            else if (game != null && _covers.TryGet(game.TitleId, out texture, out width, out height)) CopyCoverFill(renderer, texture, width, height, art);
            else DrawCover(renderer, game, art);
        }

        static void CopyCoverFill(IntPtr renderer, IntPtr texture, int width, int height, SDL_Rect destination)
        {
            if (width <= 0 || height <= 0 || destination.w <= 0 || destination.h <= 0) return;
            double ratio = destination.w / (double)destination.h;
            int cropW = width, cropH = height;
            if (width / (double)height > ratio) cropW = Math.Max(1, (int)Math.Round(height * ratio));
            else cropH = Math.Max(1, (int)Math.Round(width / ratio));
            var source = new SDL_Rect { x = (width - cropW) / 2, y = (height - cropH) / 2, w = cropW, h = cropH };
            SDL_RenderCopy(renderer, texture, ref source, ref destination);
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
            RefreshQueueModel();
            if (!string.IsNullOrEmpty(_fileDetailId)) { DrawFileDetails(renderer); return; }
            var rows = _queueRows; ClampDownloadFocus(rows);
            if (_downloadFilesTitle != null) { DrawTransferFiles(renderer); return; }
            int filterX = ContentX;
            for (int i = 0; i < QueueFilters.Length; i++)
            {
                TextPx(renderer, filterX, 148, 22, QueueFilters[i], i == _queueFilter ? White : Muted);
                if (i == _queueFilter) Fill(renderer, filterX, 189, 26, 2, White);
                filterX += UiFont.MeasurePx(22, QueueFilters[i]) + 35;
            }
            if (rows.Count == 0) { TextPx(renderer, ContentX, 380, 32, "Nothing queued", White); return; }
            // Focus expands within the list; selection order remains stable while jobs update.
            EnsureVisible(ref _dlScroll, _dlFocus, rows.Count, 4);
            int y = 224;
            for (int i = _dlScroll; i < rows.Count && i < _dlScroll + 4; i++)
            {
                var row = rows[i]; var group = row.Group; var item = row.Item;
                bool focus = i == _dlFocus;
                int height = focus ? 320 : 112;
                var area = new SDL_Rect { x = ContentX, y = y, w = ContentWidth, h = height };
                Fill(renderer, area.x, area.y, area.w, area.h, focus ? C(35, 36, 37) : Panel);
                EnsureDownloadArtwork(group);
                if (focus && !string.IsNullOrEmpty(group.ImageUrl))
                {
                    string key = (group.TitleId ?? "") + "-BACKDROP";
                    _covers.Request(key, group.ImageUrl);
                    IntPtr backdrop; int bw, bh;
                    if (_covers.TryGet(key, out backdrop, out bw, out bh)) CopyCoverFill(renderer, backdrop, bw, bh, area);
                }
                if (focus)
                {
                    // Four cheap strokes keep focus visible without a blur pass.
                    for (int ring = 4; ring >= 1; ring--)
                    {
                        var halo = new SDL_Rect { x = area.x - ring, y = area.y - ring,
                            w = area.w + ring * 2, h = area.h + ring * 2 };
                        byte light = (byte)(42 + (4 - ring) * 17);
                        StrokeRect(renderer, halo, C(light, light, light), 1);
                    }
                    StrokeRect(renderer, area, Accent, 1);
                }
                int coverW = focus ? 142 : 68, coverH = focus ? 184 : 88;
                DrawCase(renderer, new GameHit { TitleId = group.TitleId, Name = group.Name, ImageUrl = group.ImageUrl },
                    new SDL_Rect { x = area.x + 34, y = y + (height - coverH) / 2 - FocusLift(focus), w = coverW, h = coverH });
                int tx = area.x + (focus ? 218 : 134), tw = ContentWidth - (focus ? 258 : 420);
                TextFit(renderer, tx, y + (focus ? 45 : 23), focus ? 31 : 26, tw, group.Name, White);
                if (focus)
                {
                    TextPx(renderer, tx, y + 102, 21, group.TitleId ?? "", Muted);
                    DrawGroupKinds(renderer, group, tx + 164, y + 102, true);
                    var track = new SDL_Rect { x = tx, y = y + 176, w = ContentWidth - 258, h = 7 };
                    Fill(renderer, track.x, track.y, track.w, track.h, C(81, 83, 85));
                    if (item != null)
                    {
                        double target = item.Total > 0 ? Math.Min(1, Math.Max(0, item.Done / (double)item.Total)) : 0;
                        uint now = UiTick();
                        if (_pocketProgressId != item.Id || _cfg.ReduceMotion || target < _pocketProgress) _pocketProgress = target;
                        else _pocketProgress += (target - _pocketProgress) * Math.Min(1, unchecked(now - _pocketAt) / 140.0);
                        _pocketProgressId = item.Id; _pocketAt = now;
                        Fill(renderer, track.x, track.y, Math.Max(1, (int)(track.w * _pocketProgress)), track.h, White);
                        TextFit(renderer, tx, y + 205, 22, 520, TransferSizeLine(item), White);
                        if (_cfg.DownloadStatsMode != 1)
                        {
                            string rate = item.State == DlState.Failed ? "Files kept for retry" : TransferRateLine(item);
                            if (item.State == DlState.Downloading)
                            {
                                string speed = item.BytesPerSec > 0 ? (item.BytesPerSec / 1000000.0).ToString("0.00") + " MB/s" : "Measuring speed…";
                                string eta = item.EtaSeconds > 0 ? "ETA " + item.EtaSeconds / 60 + ":" + (item.EtaSeconds % 60).ToString("00") : "ETA —";
                                rate = _cfg.DownloadStatsMode == 2 ? speed : _cfg.DownloadStatsMode == 3 ? eta : speed + "  ·  " + eta;
                            }
                            TextFit(renderer, tx + 540, y + 205, 23, ContentWidth - 800, rate, White);
                        }
                        if (_cfg.NerdStats) DrawFocusedSparkline(renderer, new SDL_Rect { x = tx, y = y + 133, w = ContentWidth - 258, h = 31 });
                        if (item.State == DlState.Failed) TextFit(renderer, tx, y + 258, 19, tw - 180, FriendlyTransferFailure(item.Error), Danger);
                        else if (item.State != DlState.Downloading) TextFit(renderer, tx, y + 258, 21, tw - 180, VisibleState(item), StateColor(item.State));
                    }
                    TextFit(renderer, area.x + area.w - 185, y + 265, 20, 150, "Files  " + group.Items.Count + "  ›", White);
                }
                else
                {
                    DrawGroupKinds(renderer, group, tx, y + 70, false);
                    TextFit(renderer, area.x + area.w - 252, y + 46, 21, 220, DownloadGroupStatus(group), Muted);
                }
                y += height + 20;
            }
            if (rows.Count > 4) DrawScrollBar(renderer, new SDL_Rect { x = ContentX + ContentWidth + 16, y = 224, w = 4, h = 716 }, rows.Count, 4, _dlScroll);
        }

        void DrawTransferFiles(IntPtr renderer)
        {
            var rows = _queueRows;
            if (rows.Count == 0) { TextPx(renderer, ContentX, 220, 30, "No files queued", White); return; }
            var group = rows[0].Group;
            DrawCase(renderer, new GameHit { TitleId = group.TitleId, Name = group.Name, ImageUrl = group.ImageUrl }, new SDL_Rect { x = ContentX, y = 149, w = 70, h = 89 });
            TextFit(renderer, ContentX + 96, 149, 32, ContentWidth - 96, group.Name, White);
            TextPx(renderer, ContentX + 96, 201, 18, group.TitleId + " · " + rows.Count + (rows.Count == 1 ? " file" : " files"), Muted);
            TextPx(renderer, ContentX, 275, 19, "Package queue", White);
            TextFit(renderer, ContentX + 918, 276, 18, 522, "Base → Update → DLC", Muted);
            Fill(renderer, ContentX, 316, ContentWidth, 1, Border);
            EnsureVisible(ref _dlScroll, _dlFocus, rows.Count, 4);
            for (int i = _dlScroll; i < rows.Count && i < _dlScroll + 4; i++)
            {
                var item = rows[i].Item; int y = 338 + (i - _dlScroll) * 140; bool on = i == _dlFocus;
                var box = new SDL_Rect { x = ContentX - 16, y = y - 8, w = ContentWidth + 32, h = 124 };
                if (on) DesignCard(renderer, box, true); else Fill(renderer, ContentX, y + 122, ContentWidth, 1, Border);
                var tag = new SDL_Rect { x = ContentX + 8, y = y + 13, w = 96, h = 30 };
                SoftRect(renderer, tag, Raised); TextCentered(renderer, tag, 15, KindShort(item.Kind), PackageTint(item.Kind));
                TextFit(renderer, ContentX + 128, y + 5, 24, 820, PackageDisplayTitle(item.Kind, item.Label, group.Name, "", item.PackageVersion), White);
                TextFit(renderer, ContentX + 128, y + 48, 18, 850, item.State == DlState.Failed ? FriendlyTransferFailure(item.Error) : TransferRateLine(item), item.State == DlState.Failed ? Danger : Muted);
                TextFit(renderer, ContentX + 1080, y + 7, 19, 350, VisibleState(item), StateColor(item.State));
                TextFit(renderer, ContentX + 1080, y + 49, 18, 350, TransferSizeLine(item), Muted);
                DrawProgress(renderer, new SDL_Rect { x = ContentX + 128, y = y + 97, w = ContentWidth - 140, h = 4 }, item);
            }
        }
    }
}
