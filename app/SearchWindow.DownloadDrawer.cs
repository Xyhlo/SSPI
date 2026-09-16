using System;
using System.Collections.Generic;
using SDL2.Types;
using Orbis.Internals;
using static SDL2.SDL;

namespace Orbis
{
    public partial class SearchWindow
    {
        const int DownloadDrawerHeight = 340;
        static readonly string[] DrawerTabs = { "Packages", "Files", "Errors" };
        int _drawerTab, _drawerFocus, _drawerScroll;
        string[] _removeGroupIds;
        uint _drawerRowsAt;
        bool _drawerRowsDirty;
        sealed class DrawerRow
        {
            internal string Name, Detail, Size, ItemId;
            internal bool Error, Installed;
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
                    if (item.State == DlState.Failed) detail = FriendlyTransferFailure(item.Error);
                    else if (!Extracting(item) && detail != state && !string.IsNullOrEmpty(detail)) detail = state + " · " + detail;
                    _drawerRows.Add(new DrawerRow { Name = PackageDisplayTitle(item.Kind, item.Label, group.Name, "", item.PackageVersion),
                        Detail = string.IsNullOrEmpty(detail) ? state : detail,
                        Size = TransferSizeLine(item), ItemId = item.Id, Error = item.State == DlState.Failed });
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
                    _drawerRows.Add(new DrawerRow { Name = IncomingFileName(selected, volume.Name), Detail = "Incoming file · " + state,
                        Size = volume.Size > 0 ? FileBytes(volume.Size) : "Size pending", ItemId = selected.Id });
                    offset += volume.Size;
                }
                foreach (var output in outputs)
                    _drawerRows.Add(new DrawerRow { Name = output.Name, Detail = output.State, Size = FileBytes(output.Size),
                        ItemId = selected.Id, Error = output.State.StartsWith("Failed", StringComparison.Ordinal), Installed = output.State == "Installed" });
                if (_drawerRows.Count == 0)
                    _drawerRows.Add(new DrawerRow { Name = IncomingFileName(selected, System.IO.Path.GetFileName(selected.DestPath ?? "")),
                        Detail = Extracting(selected) ? FormatDlLine(selected) : VisibleState(selected), Size = TransferSizeLine(selected), ItemId = selected.Id });
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
                            ItemId = item.Id, Error = true });
                        start += length; while (start < text.Length && text[start] == ' ') start++;
                        if (_drawerRows.Count >= 512) break;
                    }
                    if (_drawerRows.Count >= 512) break;
                }
            }
            _drawerFocus = Math.Max(0, Math.Min(_drawerFocus, _drawerRows.Count - 1));
        }

        void DrawDownloadDrawer(IntPtr r, DownloadGroup group, int top)
        {
            RefreshDrawerRows(group);
            var item = group.Items.Find(x => x.Id == _fileDetailId);
            if (item != null) RefreshFileOutputs(item);
            var box = new SDL_Rect { x = ContentX, y = top, w = ContentWidth, h = DownloadDrawerHeight };
            SoftRect(r, box, Panel);
            Fill(r, box.x, box.y, box.w, 12, Panel);
            Fill(r, box.x + 24, box.y, box.w - 48, 1, Border);
            int x = box.x + 24;
            for (int i = 0; i < DrawerTabs.Length; i++)
            {
                var tone = i == 2 && group.Items.Exists(entry => entry.State == DlState.Failed) ? Danger : i == _drawerTab ? White : Muted;
                TextPx(r, x, top + 16, 20, DrawerTabs[i], tone);
                if (i == _drawerTab) Fill(r, x, top + 49, 82, 3, tone);
                x += 160;
            }
            TextFit(r, box.x + 570, top + 19, 17, box.w - 610, "← / → Switch tabs   ·   ↑ / ↓ Select   ·   L2 Close", Muted);
            int rowTop = top + 64, visible = 3;
            DlItem failed = group.Items.Find(entry => entry.State == DlState.Failed);
            if (failed != null && _drawerTab != 2)
            {
                SoftRect(r, new SDL_Rect { x = box.x + 20, y = rowTop, w = box.w - 40, h = 63 }, C(62, 31, 39));
                TextPx(r, box.x + 36, rowTop + 7, 17, "NEEDS ATTENTION · " + PackageTitle(failed.Kind), Danger);
                TextFit(r, box.x + 36, rowTop + 32, 18, box.w - 80, FriendlyTransferFailure(failed.Error) + " · Details in Errors", White);
                rowTop += 71; visible = 2;
            }
            EnsureVisible(ref _drawerScroll, _drawerFocus, _drawerRows.Count, visible);
            for (int i = _drawerScroll; i < _drawerRows.Count && i < _drawerScroll + visible; i++)
            {
                var row = _drawerRows[i]; int y = rowTop + (i - _drawerScroll) * 69; bool focused = i == _drawerFocus;
                var rowBox = new SDL_Rect { x = box.x + 20, y = y, w = box.w - 40, h = 61 };
                SoftRect(r, rowBox, focused ? Focused : Row);
                if (focused) StrokeRect(r, rowBox, row.Error ? Danger : Accent, 1);
                TextFit(r, box.x + 37, y + 6, 19, box.w - 400, (row.Installed ? "✓  " : "") + row.Name, row.Error ? Danger : row.Installed ? Accent : White);
                TextFit(r, box.x + 37, y + 33, 17, box.w - 80, row.Detail, row.Installed ? Accent : row.Error ? White : Muted);
                TextFit(r, box.x + box.w - 340, y + 8, 17, 300, row.Size ?? "", Muted);
            }
            if (_drawerRows.Count == 0) TextPx(r, box.x + 35, rowTop + 38, 23, _drawerTab == 2 ? "No errors for this game" : "Waiting for file information", Muted);
            if (_drawerRows.Count > visible) DrawScrollBar(r, new SDL_Rect { x = box.x + box.w - 13, y = rowTop, w = 4, h = visible * 69 - 8 }, _drawerRows.Count, visible, _drawerScroll);
            TextFit(r, box.x + 27, top + box.h - 35, 16, box.w - 54,
                (_drawerRows.Count == 0 ? "" : (_drawerFocus + 1) + " / " + _drawerRows.Count + "   ·   ") +
                "Base → Update → DLC   ·   Files stay until installation is confirmed", Muted);
        }
    }
}
