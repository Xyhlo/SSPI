using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SDL2.Types;
using static SDL2.SDL;

namespace Orbis
{
    public partial class SearchWindow
    {
        const int UsbInstallViewLimit = 512, UsbInstallSelectionLimit = 256;
        sealed class UsbInstallEntry
        {
            public string Path, Name, Detail;
            public bool Folder, Enabled;
            public long Length;
        }

        bool _usbInstallOpen;
        volatile bool _usbInstallBusy, _usbQueueBusy;
        int _usbInstallGeneration, _usbInstallScroll;
        string _usbInstallRoot = "", _usbInstallPath = "", _usbInstallMessage = "";
        List<UsbInstallEntry> _usbInstallEntries = new List<UsbInstallEntry>();
        readonly HashSet<string> _usbInstallSelected = new HashSet<string>(StringComparer.Ordinal);

        static bool UsbInstallType(string name, out string detail)
        {
            string extension = Path.GetExtension(name).ToLowerInvariant();
            if (extension == ".pkg") { detail = "PKG package"; return true; }
            if (extension == ".zip") { detail = "ZIP archive"; return true; }
            if (extension == ".7z" || extension == ".7zip") { detail = "7z archive"; return true; }
            string set; int volume;
            if (ArchiveVolumeSet.TryIndex(name, out set, out volume))
            {
                // Any volume may be selected; queueing groups it with its set. Numbers past
                // the set limit are not RAR continuations (split ZIP parts such as .z01).
                if (volume > ArchiveVolumeSet.MaximumVolumes) { detail = "Unsupported file type"; return false; }
                detail = volume == 1 ? "RAR archive" : "RAR volume " + volume + " - queued with its set";
                return true;
            }
            detail = "Unsupported file type";
            return false;
        }

        static string UsbInstallSize(long bytes)
        {
            return bytes >= 1073741824L ? (bytes / 1073741824.0).ToString("0.00") + " GB" :
                bytes >= 1048576L ? (bytes / 1048576.0).ToString("0.0") + " MB" :
                bytes >= 1024L ? (bytes / 1024.0).ToString("0.0") + " KB" : bytes + " B";
        }

        static string UsbInstallFullPath(string path)
        { return Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/'); }

        static bool UsbInstallInsideRoot(string root, string path)
        { return path == root || path.StartsWith(root + "/", StringComparison.Ordinal); }

        static List<UsbInstallEntry> ReadUsbInstallEntries(string root, string directory, Func<bool> canceled, out bool limited)
        {
            root = UsbInstallFullPath(root);
            string full = UsbInstallFullPath(directory);
            if (!UsbInstallInsideRoot(root, full)) throw new IOException("Outside selected drive");
            string ancestor = full;
            while (true)
            {
                if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked folders cannot be opened");
                if (ancestor == root) break;
                ancestor = UsbInstallFullPath(Path.GetDirectoryName(ancestor));
            }
            var entries = new List<UsbInstallEntry>();
            limited = false;
            using (var paths = Directory.EnumerateFileSystemEntries(full).GetEnumerator())
            {
                int scanned = 0;
                while (scanned < UsbInstallViewLimit && paths.MoveNext())
                {
                    if (canceled()) throw new OperationCanceledException();
                    scanned++;
                    string path = UsbInstallFullPath(paths.Current);
                    if (!UsbInstallInsideRoot(root, path)) continue;
                    var entry = new UsbInstallEntry { Path = path, Name = Path.GetFileName(path) };
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) entry.Detail = "Linked item unavailable";
                        else if ((attributes & FileAttributes.Directory) != 0)
                        { entry.Folder = entry.Enabled = true; entry.Detail = "Open folder"; }
                        else
                        {
                            entry.Length = new FileInfo(path).Length;
                            string kind;
                            entry.Enabled = UsbInstallType(entry.Name, out kind) && entry.Length > 0;
                            entry.Detail = entry.Length == 0 ? "Empty file" : kind + "  ·  " + UsbInstallSize(entry.Length);
                        }
                    }
                    catch { entry.Detail = "File unavailable - reconnect the drive and refresh"; }
                    entries.Add(entry);
                }
                limited = scanned == UsbInstallViewLimit;
            }
            entries.Sort((a, b) => a.Folder != b.Folder ? (a.Folder ? -1 : 1) : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
            return entries;
        }

        void OpenUsbInstall()
        {
            _usbInstallOpen = true;
            _usbInstallSelected.Clear();
            BrowseUsbInstall("", "");
        }

        void BrowseUsbInstall(string root, string directory)
        {
            _usbInstallRoot = root ?? ""; _usbInstallPath = directory ?? "";
            _usbInstallBusy = true; _usbInstallMessage = "Reading connected storage...";
            _usbInstallEntries = new List<UsbInstallEntry>(); _settingsFocus = _usbInstallScroll = 0;
            int generation = ++_usbInstallGeneration;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var entries = new List<UsbInstallEntry>(); string message = "";
                try
                {
                    if (string.IsNullOrEmpty(directory))
                    {
                        for (int i = 0; i < 8; i++)
                        {
                            if (generation != _usbInstallGeneration) return;
                            string mount = "/mnt/usb" + i;
                            if (!UsbVolumeLabel.IsConnected(mount)) continue;
                            if ((File.GetAttributes(mount) & FileAttributes.ReparsePoint) != 0) continue;
                            string label = UsbVolumeLabel.Read(mount);
                            entries.Add(new UsbInstallEntry { Path = mount, Name = string.IsNullOrEmpty(label) ? "USB " + (i + 1) : label,
                                Detail = "USB " + (i + 1) + "  ·  Browse packages and archives", Folder = true, Enabled = true });
                        }
                        if (entries.Count == 0) message = "No USB drives connected. Connect a drive, then press TRIANGLE to refresh.";
                    }
                    else
                    {
                        if (!UsbVolumeLabel.IsConnected(root)) throw new IOException("Drive disconnected");
                        bool limited;
                        entries = ReadUsbInstallEntries(root, directory, () => generation != _usbInstallGeneration, out limited);
                        if (limited) message = "Showing up to 512 entries. Open a subfolder to browse more files.";
                        else if (entries.Count == 0) message = "This folder is empty.";
                    }
                }
                catch (OperationCanceledException) { return; }
                catch { message = "Could not read this drive or folder. Reconnect the drive, then press TRIANGLE to retry."; }
                lock (_lock)
                {
                    if (!_usbInstallOpen || generation != _usbInstallGeneration) return;
                    _usbInstallEntries = entries; _usbInstallMessage = message; _usbInstallBusy = false; Invalidated = true;
                }
            });
        }

        bool UsbInstallBack()
        {
            if (_usbInstallPath.Length == 0)
            {
                ++_usbInstallGeneration; _usbInstallBusy = false; _usbInstallOpen = false;
                _settingsFocus = 2; _usbInstallEntries.Clear();
            }
            else if (_usbInstallPath == _usbInstallRoot) BrowseUsbInstall("", "");
            else BrowseUsbInstall(_usbInstallRoot, UsbInstallFullPath(Path.GetDirectoryName(_usbInstallPath)));
            return true;
        }

        bool UsbInstallAllSelected()
        {
            bool any = false, selected = false, all = true;
            foreach (var entry in _usbInstallEntries)
                if (!entry.Folder && entry.Enabled)
                {
                    any = true;
                    if (_usbInstallSelected.Contains(entry.Path)) selected = true;
                    else all = false;
                }
            // A capped selection must still offer a one-button way to clear
            // this folder, even when its remaining entries could not fit.
            return any && (all || (selected && _usbInstallSelected.Count == UsbInstallSelectionLimit));
        }

        void ToggleUsbInstallAll()
        {
            bool remove = UsbInstallAllSelected();
            foreach (var entry in _usbInstallEntries)
            {
                if (entry.Folder || !entry.Enabled) continue;
                if (remove) _usbInstallSelected.Remove(entry.Path);
                else if (_usbInstallSelected.Count < UsbInstallSelectionLimit) _usbInstallSelected.Add(entry.Path);
            }
            _usbInstallMessage = !remove && _usbInstallSelected.Count == UsbInstallSelectionLimit
                ? "Selection limit reached: queue up to 256 files at a time." : "";
        }

        void QueueUsbInstallSelection()
        {
            if (_usbQueueBusy || _usbInstallSelected.Count == 0) return;
            var paths = new List<string>(_usbInstallSelected);
            _usbQueueBusy = true; _usbInstallMessage = "Checking selected files and adding them to Downloads...";
            int generation = _usbInstallGeneration;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int queued = 0; string error = null;
                try
                {
                    List<string> sets = LocalInstallSource.FirstVolumes(paths);
                    queued = _dlMgr.QueueLocalFiles(sets, out error);
                    _dlMgr.AssignLocalArchiveTitles(sets);
                }
                catch { error = "Could not queue the selected files. Check that the drives are still connected."; }
                string message = queued + (queued == 1 ? " file queued." : " files queued.");
                if (!string.IsNullOrEmpty(error)) message += " " + error;
                lock (_lock)
                {
                    _usbQueueBusy = false;
                    if (string.IsNullOrEmpty(error)) foreach (string path in paths) _usbInstallSelected.Remove(path);
                    if (_usbInstallOpen && generation == _usbInstallGeneration) _usbInstallMessage = message;
                    Invalidated = true;
                }
                User.NotifyToast(Clip(message, 160));
            });
        }

        void HandleUsbInstall(DS4Button button)
        {
            if (_usbQueueBusy) return;
            if (button == DS4Button.SCE_PAD_BUTTON_TRIANGLE)
            { if (!_usbInstallBusy) BrowseUsbInstall(_usbInstallRoot, _usbInstallPath); return; }
            if (button == DS4Button.SCE_PAD_BUTTON_R2) { QueueUsbInstallSelection(); return; }
            if (_usbInstallBusy) return;
            int count = _usbInstallEntries.Count;
            if (button == DS4Button.SCE_PAD_BUTTON_UP) _settingsFocus = Math.Max(0, _settingsFocus - 1);
            if (button == DS4Button.SCE_PAD_BUTTON_DOWN) _settingsFocus = Math.Min(Math.Max(0, count - 1), _settingsFocus + 1);
            if (button == DS4Button.SCE_PAD_BUTTON_LEFT) _settingsFocus = Math.Max(0, _settingsFocus - 5);
            if (button == DS4Button.SCE_PAD_BUTTON_RIGHT) _settingsFocus = Math.Min(Math.Max(0, count - 1), _settingsFocus + 5);
            if (button == DS4Button.SCE_PAD_BUTTON_SQUARE) { ToggleUsbInstallAll(); return; }
            if (button != DS4Button.SCE_PAD_BUTTON_CROSS || count == 0) return;
            var entry = _usbInstallEntries[_settingsFocus];
            if (!entry.Enabled) { _usbInstallMessage = entry.Detail; return; }
            if (entry.Folder) { BrowseUsbInstall(_usbInstallRoot.Length == 0 ? entry.Path : _usbInstallRoot, entry.Path); return; }
            if (!_usbInstallSelected.Remove(entry.Path))
            {
                if (_usbInstallSelected.Count >= UsbInstallSelectionLimit) _usbInstallMessage = "Selection limit reached: queue up to 256 files at a time.";
                else { _usbInstallSelected.Add(entry.Path); _usbInstallMessage = ""; }
            }
        }

        void DrawUsbInstall(IntPtr renderer, SDL_Rect sheet)
        {
            TextPx(renderer, sheet.x, sheet.y + 15, 28, "Install from USB", White);
            TextFit(renderer, sheet.x, sheet.y + 61, 18, sheet.w, "Keep SSPI open while files are waiting to start. Keep the USB drive connected until installation finishes.", Muted);
            TextFit(renderer, sheet.x, sheet.y + 96, 17, sheet.w - 220,
                _usbInstallPath.Length == 0 ? "Connected drives" : _usbInstallPath, Dim);
            TextFit(renderer, sheet.x + sheet.w - 210, sheet.y + 96, 17, 210, _usbInstallSelected.Count + " / 256 selected", White);
            if (_usbInstallBusy || _usbQueueBusy) DrawActivityRail(renderer, new SDL_Rect { x = sheet.x, y = sheet.y + 123, w = sheet.w, h = 3 });
            if (!_usbInstallBusy)
            {
                EnsureVisible(ref _usbInstallScroll, _settingsFocus, _usbInstallEntries.Count, 5);
                for (int i = 0; i < 5 && _usbInstallScroll + i < _usbInstallEntries.Count; i++)
                {
                    int index = _usbInstallScroll + i, y = sheet.y + 137 + i * 82;
                    var entry = _usbInstallEntries[index];
                    DesignCard(renderer, new SDL_Rect { x = sheet.x, y = y, w = sheet.w, h = 72 }, _settingsFocus == index);
                    if (entry.Folder) DesignIcon(renderer, "chevron", sheet.x + 21, y + 24, 24, Muted);
                    else
                    {
                        var check = new SDL_Rect { x = sheet.x + 22, y = y + 24, w = 23, h = 23 };
                        StrokeRect(renderer, check, entry.Enabled ? Muted : Dim, 2);
                        if (_usbInstallSelected.Contains(entry.Path)) DesignIcon(renderer, "check", check.x + 3, check.y + 3, 17, Accent);
                    }
                    TextFit(renderer, sheet.x + 68, y + 10, 21, sheet.w - 190, entry.Name, entry.Enabled ? White : Dim);
                    TextFit(renderer, sheet.x + 68, y + 42, 17, sheet.w - 190, entry.Detail, Muted);
                    TextFit(renderer, sheet.x + sheet.w - 110, y + 23, 17, 90, entry.Folder ? "Open" : entry.Enabled ? (_usbInstallSelected.Contains(entry.Path) ? "Deselect" : "Select") : "Unavailable", Dim);
                }
            }
            TextFit(renderer, sheet.x, sheet.y + 561, 17, sheet.w, _usbInstallMessage, Muted);
            int half = (sheet.w - 20) / 2;
            DesignCard(renderer, new SDL_Rect { x = sheet.x, y = sheet.y + 603, w = half, h = 58 }, false);
            DesignCard(renderer, new SDL_Rect { x = sheet.x + half + 20, y = sheet.y + 603, w = half, h = 58 }, false);
            GamepadIcons.Draw(renderer, "square", sheet.x + 18, sheet.y + 615, 32);
            GamepadIcons.Draw(renderer, "r2", sheet.x + half + 38, sheet.y + 615, 32);
            TextFit(renderer, sheet.x + 64, sheet.y + 620, 20, half - 84, UsbInstallAllSelected() ? "Deselect all in folder" : "Select all in folder", Muted);
            TextFit(renderer, sheet.x + half + 84, sheet.y + 620, 20, half - 84, _usbQueueBusy ? "Adding files to Downloads..." : "Queue selected (" + _usbInstallSelected.Count + ")", _usbInstallSelected.Count > 0 ? White : Dim);
            TextFit(renderer, sheet.x, sheet.y + 683, 16, sheet.w, "CROSS open / select  ·  LEFT / RIGHT page  ·  TRIANGLE refresh  ·  CIRCLE back", Dim);
        }
    }
}
