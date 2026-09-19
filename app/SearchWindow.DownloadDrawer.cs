using System;
using System.Collections.Generic;
using SDL2.Types;
using Orbis.Internals;
using static SDL2.SDL;

namespace Orbis
{
    public partial class SearchWindow
    {
        const int DownloadDrawerHeight = 544;
        const int DrawerRowHeight = 62;
        const int DrawerRowStride = 70;
        static readonly string[] DrawerTabs = { "Packages", "Files", "Errors" };
        int _drawerTab, _drawerFocus, _drawerScroll;
        string[] _removeGroupIds;
        uint _drawerRowsAt;
        bool _drawerRowsDirty;
        sealed class DrawerRow
        {
            internal string Name, Detail, Size, ItemId;
            internal bool Error, Installed, Parked;
            internal long Done, Total;
            internal DlState State;
        }
        readonly List<DrawerRow> _drawerRows = new List<DrawerRow>();
        static string IncomingFileName(DlItem item, string name)
        {
            string format = item.ContainerFormat ?? "";
            if (string.IsNullOrEmpty(name)) name = (item.TitleId ?? "download") + ".bin";
            string extension = System.IO.Path.GetExtension(name).ToLowerInvariant();
            if (format == "rar" && (extension == ".rar" || (extension.Length == 4 && extension[1] == 'r' && char.IsDigit(extension[2]) && char.IsDigit(extension[3])))) return name;
            if (format == "rar" || format == "zip" || format == "7z" || format == "pkg")
                return System.IO.Path.ChangeExtension(name, "." + format);
            return name;
        }
        void OpenDownloadDrawer(DownloadGroup group)
        {
            _downloadFilesTitle = group.Key ?? "";
            var item = group.Items.Find(x => x.State == DlState.Failed) ?? PrimaryTransfer(group);
            _drawerTab = item != null && item.State == DlState.Failed ? 2 : 0;
            _drawerFocus = _drawerTab == 0 ? Math.Max(0, group.Items.IndexOf(item)) : 0; _drawerScroll = 0;
            OpenFileDetails(item); _drawerRowsDirty = true; _queueModelReady = false;
        }

        void CloseDownloadDrawer()
        {
            _downloadFilesTitle = null; _fileDetailId = null;
            _drawerRows.Clear(); _queueModelReady = false;
        }

        DownloadGroup DrawerGroup()
        { return _queueGroups.Find(group => group.Key == _downloadFilesTitle); }

        bool HandleDownloadDrawer(DS4Button button)
        {
            if (button == DS4Button.SCE_PAD_BUTTON_L2)
            { CloseDownloadDrawer(); return true; }
            var group = DrawerGroup();
            if (group == null || group.Items.Count == 0) { CloseDownloadDrawer(); return true; }
            RefreshDrawerRows(group, true);
            if (button == DS4Button.SCE_PAD_BUTTON_LEFT || button == DS4Button.SCE_PAD_BUTTON_RIGHT)
            {
                _drawerTab = (_drawerTab + (button == DS4Button.SCE_PAD_BUTTON_RIGHT ? 1 : 2)) % 3;
                _drawerFocus = _drawerScroll = 0;
                if (_drawerTab == 0) _drawerFocus = Math.Max(0, group.Items.FindIndex(x => x.Id == _fileDetailId));
                _drawerRowsDirty = true; return true;
            }
            if (button == DS4Button.SCE_PAD_BUTTON_UP || button == DS4Button.SCE_PAD_BUTTON_DOWN)
            {
                _drawerFocus = Math.Max(0, Math.Min(_drawerRows.Count - 1,
                    _drawerFocus + (button == DS4Button.SCE_PAD_BUTTON_DOWN ? 1 : -1)));
                if (_drawerTab == 0 && _drawerFocus < group.Items.Count && _fileDetailId != group.Items[_drawerFocus].Id)
                { OpenFileDetails(group.Items[_drawerFocus]); _drawerRowsDirty = true; }
                return true;
            }
            var item = group.Items.Find(x => x.Id == _fileDetailId) ?? PrimaryTransfer(group);
            if ((_drawerTab == 0 || _drawerTab == 2) && _drawerFocus < _drawerRows.Count)
                item = group.Items.Find(x => x.Id == _drawerRows[_drawerFocus].ItemId) ?? item;
            if (item == null) return true;
            if (button == DS4Button.SCE_PAD_BUTTON_CROSS) ActOnDownload(item);
            if (button == DS4Button.SCE_PAD_BUTTON_TRIANGLE) PrioritizeDownload(item);
            if (button == DS4Button.SCE_PAD_BUTTON_SQUARE)
            {
                _removeGroupIds = null; _overlayDownloadId = item.Id;
                _overlayTitle = "Remove " + PackageTitle(item.Kind) + "?"; _uiOverlay = UiOverlay.ConfirmRemove;
            }
            return true;
        }

        void RefreshDrawerRows(DownloadGroup group, bool force = false)
        {
            var selected = group.Items.Find(x => x.Id == _fileDetailId) ?? PrimaryTransfer(group);
            if (selected != null && selected.Id != _fileDetailId) OpenFileDetails(selected);
            if (!force && !_drawerRowsDirty && UiElapsed(_drawerRowsAt) < 250) return;
            _drawerRowsAt = UiTick(); _drawerRowsDirty = false; _drawerRows.Clear();
            if (_drawerTab == 0)
            {
                _drawerFocus = Math.Max(0, group.Items.FindIndex(x => x.Id == _fileDetailId));
                foreach (var item in group.Items)
                {
                    string state = VisibleState(item), detail = TransferRateLine(item);
                    // The parked TorBox line is already a complete provider sentence;
                    // prefixing the state again would repeat "Preparing in TorBox".
                    if (item.ParkedForProvider) detail = string.IsNullOrEmpty(item.StatusText) ? state : item.StatusText;
                    else if (item.State == DlState.Failed) detail = FriendlyTransferFailure(item.Error);
                    else if (!Extracting(item) && detail != state && !string.IsNullOrEmpty(detail)) detail = state + " · " + detail;
                    _drawerRows.Add(new DrawerRow { Name = PackageDisplayTitle(item.Kind, item.Label, group.Name, "", item.PackageVersion),
                        Detail = string.IsNullOrEmpty(detail) ? state : detail,
                        Size = DrawerPackageSize(item), ItemId = item.Id, Error = item.State == DlState.Failed,
                        Done = Math.Max(0, item.Done), Total = DownloadDisplayTotal(item), Installed = item.State == DlState.Installed,
                        State = item.State, Parked = item.ParkedForProvider });
                }
            }
            else if (_drawerTab == 1 && selected != null)
            {
                List<FileOutput> outputs; lock (_lock) outputs = _fileOutputs;
                long offset = 0;
                foreach (var volume in _fileVolumes)
                {
                    bool downloaded = Extracting(selected) || selected.State == DlState.Installing || selected.State == DlState.Installed || selected.State == DlState.Completed;
                    string state = downloaded ? "Downloaded" : selected.State == DlState.Failed ? "Kept for retry" :
                        volume.Size > 0 && selected.Done >= offset + volume.Size ? "Downloaded" : selected.Done > offset ? "Downloading" : "Waiting";
                    long volumeTotal = Math.Max(0, volume.Size);
                    long volumeDone = downloaded ? volumeTotal : Math.Max(0, Math.Min(volumeTotal, selected.Done - offset));
                    _drawerRows.Add(new DrawerRow { Name = IncomingFileName(selected, volume.Name), Detail = "Incoming file · " + state,
                        Size = volume.Size > 0 ? FileBytes(volume.Size) : "Size pending", ItemId = selected.Id,
                        Done = volumeDone, Total = volumeTotal, State = selected.State, Parked = selected.ParkedForProvider });
                    offset += volume.Size;
                }
                foreach (var output in outputs)
                    _drawerRows.Add(new DrawerRow { Name = output.Name, Detail = output.State, Size = FileBytes(output.Size),
                        ItemId = selected.Id, Error = output.State.StartsWith("Failed", StringComparison.Ordinal),
                        Installed = output.State == "Installed",
                        Done = output.State == "Installed" ? Math.Max(0, output.Size) : 0,
                        Total = Math.Max(0, output.Size), State = selected.State, Parked = selected.ParkedForProvider });
                if (_drawerRows.Count == 0)
                    _drawerRows.Add(new DrawerRow { Name = IncomingFileName(selected, System.IO.Path.GetFileName(selected.DestPath ?? "")),
                        Detail = Extracting(selected) ? FormatDlLine(selected) : VisibleState(selected), Size = TransferSizeLine(selected),
                        ItemId = selected.Id, Done = Math.Max(0, selected.Done), Total = Math.Max(0, selected.Total),
                        State = selected.State, Parked = selected.ParkedForProvider });
            }
            else
            {
                foreach (var item in group.Items)
                {
                    if (string.IsNullOrEmpty(item.Error)) continue;
                    string text = item.Error.Replace('\r', ' ').Replace('\n', ' ');
                    int start = 0;
                    while (start < text.Length)
                    {
                        int length = Math.Min(112, text.Length - start);
                        if (start + length < text.Length) { int space = text.LastIndexOf(' ', start + length - 1, length); if (space > start) length = space - start; }
                        _drawerRows.Add(new DrawerRow { Name = PackageTitle(item.Kind), Detail = text.Substring(start, length).Trim(),
                            ItemId = item.Id, Error = true, State = DlState.Failed });
                        start += length; while (start < text.Length && text[start] == ' ') start++;
                        if (_drawerRows.Count >= 512) break;
                    }
                    if (_drawerRows.Count >= 512) break;
                }
            }
            _drawerFocus = Math.Max(0, Math.Min(_drawerFocus, _drawerRows.Count - 1));
        }

        static long DownloadDisplayTotal(DlItem item)
        {
            return Math.Max(0, item.Total > 0 ? item.Total : Extracting(item) ? 0 : item.ExpectedByteSize);
        }

        static string DrawerPackageSize(DlItem item)
        {
            long total = DownloadDisplayTotal(item);
            if (total == 0) return item.Done > 0 ? FileBytes(item.Done) + " / pending" : "Size pending";
            return FileBytes(Math.Max(0, item.Done)) + " / " + FileBytes(total);
        }

        static double DownloadDisplayProgress(bool complete, long done, long total)
        {
            return complete ? 1 : total > 0 ? Math.Min(1, Math.Max(0, done / (double)total)) : 0;
        }

        // Retain a visible sliver for small files. Unknown sizes get the mean
        // known weight until metadata arrives, never a fabricated byte total.
        static void DownloadSegmentBounds(int width, int count, int index, double before,
            double weight, double totalWeight, out int offset, out int size)
        {
            int gap = count <= width / 8 ? 4 : 0;
            int minimum = count <= width / 4 ? 4 : 0;
            int proportional = Math.Max(0, width - gap * (count - 1) - minimum * count);
            int start = index * minimum + (int)Math.Round(proportional * before / totalWeight);
            int end = (index + 1) * minimum + (int)Math.Round(proportional * (before + weight) / totalWeight);
            offset = start + index * gap;
            size = Math.Max(0, Math.Min(width - offset, end - start));
        }

        void DrawDownloadGroupProgress(IntPtr r, DownloadGroup group, SDL_Rect track)
        {
            int known = 0;
            double knownBytes = 0;
            foreach (var entry in group.Items)
            {
                long total = DownloadDisplayTotal(entry);
                if (total > 0) { known++; knownBytes += total; }
            }
            int count = group.Items.Count;
            if (count == 0 || track.w <= 0) return;
            double unknownWeight = known > 0 ? knownBytes / known : 1;
            double totalWeight = knownBytes + (count - known) * unknownWeight, before = 0;
            for (int i = 0; i < count; i++)
            {
                var entry = group.Items[i];
                long total = DownloadDisplayTotal(entry);
                double weight = total > 0 ? total : unknownWeight;
                int offset, width;
                DownloadSegmentBounds(track.w, count, i, before, weight, totalWeight, out offset, out width);
                before += weight;
                if (width <= 0) continue;
                var segment = new SDL_Rect { x = track.x + offset, y = track.y, w = width, h = track.h };
                SoftRect(r, segment, total > 0 ? C(81, 83, 85) : C(65, 67, 69));
                bool complete = entry.State == DlState.Completed || entry.State == DlState.Installed;
                int fill = (int)Math.Round(width * DownloadDisplayProgress(complete, entry.Done, total));
                if (fill <= 0) continue;
                segment.w = Math.Min(width, fill);
                SoftRect(r, segment, entry.State == DlState.Failed ? Danger : White);
            }
        }

        static string DrawerPercentText(DrawerRow row)
        {
            if (row.Installed || row.State == DlState.Completed || row.State == DlState.Installed) return "100%";
            if (row.Total > 0) return (int)(DownloadDisplayProgress(false, row.Done, row.Total) * 100) + "%";
            return "—";
        }

        static string DownloadGroupCounter(DownloadGroup group)
        {
            int done = 0;
            foreach (var entry in group.Items)
                if (entry.State == DlState.Completed || entry.State == DlState.Installed ||
                    (DownloadDisplayTotal(entry) > 0 && entry.Done >= DownloadDisplayTotal(entry))) done++;
            var active = PrimaryTransfer(group);
            if (active != null && (active.Done > 0 || active.State == DlState.Downloading ||
                active.State == DlState.Finalizing || active.State == DlState.Installing || active.ParkedForProvider))
                done = Math.Max(done, group.Items.IndexOf(active) + 1);
            return done + " / " + group.Items.Count + " files";
        }

        static string DownloadGroupSize(DownloadGroup group)
        {
            long done = 0, total = 0;
            bool pending = false;
            foreach (var entry in group.Items)
            {
                long size = DownloadDisplayTotal(entry);
                pending |= size == 0;
                total = size > long.MaxValue - total ? long.MaxValue : total + size;
                long bytes = Math.Max(0, entry.Done);
                if (entry.State == DlState.Completed || entry.State == DlState.Installed) bytes = Math.Max(bytes, size);
                else if (size > 0) bytes = Math.Min(bytes, size);
                done = bytes > long.MaxValue - done ? long.MaxValue : done + bytes;
            }
            return total == 0 ? FileBytes(done) + " · Size pending" :
                FileBytes(done) + " / " + FileBytes(total) + (pending ? " + pending" : "");
        }

        int DownloadDrawerContentHeight(DownloadGroup group)
        {
            RefreshDrawerRows(group);
            bool warning = _drawerTab != 2 && group.Items.Exists(entry => entry.State == DlState.Failed);
            int visible = warning ? 5 : 6;
            int count = Math.Max(1, Math.Min(visible, _drawerRows.Count));
            int footer = _drawerRows.Count > visible ? 56 : 28;
            return Math.Min(DownloadDrawerHeight, 68 + (warning ? DrawerRowStride : 0) + count * DrawerRowStride + footer);
        }

        void DrawDownloadDrawer(IntPtr r, DownloadGroup group, int top)
        {
            RefreshDrawerRows(group);
            var item = group.Items.Find(x => x.Id == _fileDetailId);
            if (item != null) RefreshFileOutputs(item);
            var box = new SDL_Rect { x = ContentX, y = top, w = ContentWidth, h = DownloadDrawerContentHeight(group) };
            SoftRect(r, box, Panel);
            Fill(r, box.x, box.y, box.w, 12, Panel);
            Fill(r, box.x + 24, box.y, box.w - 48, 1, Border);
            int x = box.x + 24;
            for (int i = 0; i < DrawerTabs.Length; i++)
            {
                var tone = i == 2 && group.Items.Exists(entry => entry.State == DlState.Failed) ? Danger : i == _drawerTab ? White : Muted;
                TextPx(r, x, top + 19, 21, DrawerTabs[i], tone);
                if (i == _drawerTab) Fill(r, x, top + 49, 82, 3, tone);
                x += 156;
            }
            int rowTop = top + 68;
            int visible = 6;
            DlItem failed = group.Items.Find(entry => entry.State == DlState.Failed);
            if (failed != null && _drawerTab != 2)
            {
                SoftRect(r, new SDL_Rect { x = box.x + 20, y = rowTop, w = box.w - 40, h = DrawerRowHeight }, C(62, 31, 39));
                TextPx(r, box.x + 36, rowTop + 6, 17, "NEEDS ATTENTION · " + PackageTitle(failed.Kind), Danger);
                TextFit(r, box.x + 36, rowTop + 28, 18, box.w - 80, FriendlyTransferFailure(failed.Error) + " · Details in Errors", White);
                rowTop += DrawerRowStride; visible = 5;
            }
            EnsureVisible(ref _drawerScroll, _drawerFocus, _drawerRows.Count, visible);
            for (int i = _drawerScroll; i < _drawerRows.Count && i < _drawerScroll + visible; i++)
            {
                var row = _drawerRows[i]; int y = rowTop + (i - _drawerScroll) * DrawerRowStride; bool focused = i == _drawerFocus;
                var rowBox = new SDL_Rect { x = box.x + 20, y = y, w = box.w - 40, h = DrawerRowHeight };
                if (focused) SoftRect(r, rowBox, Focused);
                else Fill(r, box.x + 36, y + DrawerRowHeight + 3, box.w - 72, 1, Border);
                if (focused) StrokeRect(r, rowBox, row.Error ? Danger : row.Parked ? Warning : Accent, 1);
                int textX = box.x + 38, barX = box.x + 514;
                bool errorTab = _drawerTab == 2;
                int textWidth = errorTab ? box.w - 76 : barX - textX - 22;
                TextFit(r, textX, y + 8, 19, textWidth,
                    (row.Installed ? "✓  " : "") + row.Name,
                    row.Error ? Danger : row.Parked ? Warning : row.Installed ? Accent : White);
                TextFit(r, textX, y + 34, 16, textWidth, row.Detail,
                    row.Installed ? Accent : row.Error ? White : row.Parked ? White : Muted);
                if (errorTab) continue;
                int right = box.x + box.w - 38;
                int sizeRight = right - 64, sizeLeft = sizeRight - 220;
                int barWidth = Math.Max(1, sizeLeft - 24 - barX);
                var bar = new SDL_Rect { x = barX, y = y + 28, w = barWidth, h = 7 };
                SoftRect(r, bar, C(81, 83, 85));
                bool complete = row.Installed || row.State == DlState.Completed || row.State == DlState.Installed;
                int filled = (int)Math.Round(barWidth * DownloadDisplayProgress(complete, row.Done, row.Total));
                if (filled > 0) { bar.w = Math.Min(barWidth, filled); SoftRect(r, bar, row.Error ? Danger : White); }
                string sizeText = row.Size ?? "";
                string percent = DrawerPercentText(row);
                int sizeWidth = UiFont.MeasurePx(17, sizeText);
                int percentWidth = UiFont.MeasurePx(17, percent);
                TextFit(r, Math.Max(sizeLeft, sizeRight - sizeWidth), y + 21, 17, 220, sizeText, Muted);
                TextPx(r, right - percentWidth, y + 21, 17, percent,
                    row.Error ? Danger : row.Parked ? Warning : White);
            }
            if (_drawerRows.Count == 0) TextPx(r, box.x + 35, rowTop + 34, 22, _drawerTab == 2 ? "No errors for this game" : "Waiting for file information", Muted);
            if (_drawerRows.Count > visible) DrawScrollBar(r, new SDL_Rect { x = box.x + box.w - 13, y = rowTop, w = 4, h = visible * DrawerRowStride - 8 }, _drawerRows.Count, visible, _drawerScroll);
            int below = Math.Max(0, _drawerRows.Count - _drawerScroll - visible);
            string more = below > 0 ? "⌄   +" + below + " more items" : _drawerScroll > 0 ? "⌃   " + _drawerScroll + " items above" : "";
            if (more.Length > 0) TextCentered(r, new SDL_Rect { x = box.x + 24, y = top + box.h - 40, w = box.w - 48, h = 30 }, 17, more, Muted);
        }
    }
}
