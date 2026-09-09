using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using static SDL2.SDL;
using SDL2.Types;

namespace Orbis
{
    public partial class SearchWindow
    {
        long _nextUpdateScan;
        volatile bool _storageBusy;
        List<string> _storageFiles = new List<string>();
        List<long> _storageSizes = new List<long>();
        bool _storageLoaded;
        string _storageMessage = "TRIANGLE refreshes. SQUARE selects a file for deletion.", _storageDelete;
        int _storageScroll;

        void RefreshStorage()
        {
            if (_storageBusy) return;
            _storageLoaded = true; _storageBusy = true;
            ThreadPool.QueueUserWorkItem(_ => {
                var paths = new List<string>(); var sizes = new List<long>();
                try {
                    var stack = new Stack<string>();
                    foreach (string root in DownloadManager.StorageRoots()) if (Directory.Exists(root) && (File.GetAttributes(root) & FileAttributes.ReparsePoint) == 0) stack.Push(root);
                    while (stack.Count > 0 && paths.Count < 2048) {
                        string dir = stack.Pop();
                        foreach (string path in Directory.GetFiles(dir)) {
                            if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) continue;
                            var info = new FileInfo(path); if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                            paths.Add(path); sizes.Add(info.Length); if (paths.Count >= 2048) break;
                        }
                        foreach (string child in Directory.GetDirectories(dir))
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) stack.Push(child);
                    }
                    lock (_lock) { _storageFiles = paths; _storageSizes = sizes; _settingsFocus = Math.Min(_settingsFocus, Math.Max(0, paths.Count - 1)); }
                } catch (Exception ex) { _storageMessage = "Scan failed: " + ex.Message; }
                finally { _storageBusy = false; Invalidated = true; }
            });
        }

        void HandleStorage(DS4Button b)
        {
            if (_storageBusy) return;
            if (b == DS4Button.SCE_PAD_BUTTON_TRIANGLE) { _storageDelete = null; RefreshStorage(); return; }
            if (_storageDelete != null)
            {
                if (b == DS4Button.SCE_PAD_BUTTON_CROSS) {
                    string path = _storageDelete; _storageDelete = null;
                    _storageBusy = true;
                    ThreadPool.QueueUserWorkItem(_ => { string detail; _dlMgr.TryDeleteStorageFile(path, out detail); _storageMessage = detail; _storageBusy = false; RefreshStorage(); });
                } else if (b == DS4Button.SCE_PAD_BUTTON_SQUARE) { _storageDelete = null; _storageMessage = "Deletion canceled"; }
                return;
            }
            if (_storageFiles.Count == 0) return;
            if (b == DS4Button.SCE_PAD_BUTTON_UP) _settingsFocus = Math.Max(0, _settingsFocus - 1);
            if (b == DS4Button.SCE_PAD_BUTTON_DOWN) _settingsFocus = Math.Min(_storageFiles.Count - 1, _settingsFocus + 1);
            if (b == DS4Button.SCE_PAD_BUTTON_SQUARE) { _storageDelete = _storageFiles[_settingsFocus]; _storageMessage = "CROSS deletes this file permanently. SQUARE cancels."; }
        }

        void DrawStorage(IntPtr r, SDL_Rect sheet)
        {
            if (!_storageLoaded) RefreshStorage();
            TextPx(r, sheet.x, sheet.y + 15, 28, "Downloaded files", White);
            TextPx(r, sheet.x + sheet.w - 220, sheet.y + 20, 20, _freeStorageLabel + " free", Muted);
            TextFit(r, sheet.x, sheet.y + 57, 18, sheet.w, _storageBusy ? "Reading storage…" : _storageDelete == null ? "Manage package files saved by SSPI." : "Delete this file? This cannot be undone.", _storageDelete == null ? Muted : Danger);
            if (_storageBusy) DrawActivityRail(r, new SDL_Rect { x = sheet.x, y = sheet.y + 103, w = sheet.w, h = 3 });
            lock (_lock)
            {
                EnsureVisible(ref _storageScroll, _settingsFocus, _storageFiles.Count, 5);
                for (int i = 0; i < 5 && _storageScroll + i < _storageFiles.Count; i++)
                {
                    int n = _storageScroll + i, y = sheet.y + 124 + i * 99;
                    var row = new SDL_Rect { x = sheet.x, y = y, w = sheet.w, h = 86 };
                    DesignCard(r, row, n == _settingsFocus);
                    DesignIcon(r, "download", row.x + 24, y + 26, 26, _storageDelete == _storageFiles[n] ? Danger : Muted);
                    TextFit(r, row.x + 74, y + 14, 23, row.w - 315, Path.GetFileName(_storageFiles[n]), White);
                    TextFit(r, row.x + 74, y + 51, 16, row.w - 315, _storageDelete == _storageFiles[n] ? "Pending deletion · confirm below" : Path.GetExtension(_storageFiles[n]).TrimStart('.').ToUpperInvariant() + " file", Muted);
                    TextPx(r, row.x + row.w - 205, y + 28, 19, (_storageSizes[n] / 1048576.0).ToString("0.0") + " MB", Muted);
                }
                if (_storageFiles.Count == 0 && !_storageBusy)
                {
                    DesignIcon(r, "download", sheet.x + sheet.w / 2 - 22, sheet.y + 290, 44, Dim);
                    TextCentered(r, new SDL_Rect { x = sheet.x, y = sheet.y + 365, w = sheet.w, h = 45 }, 28, "No downloaded files", White);
                }
                if (_storageMessage != "TRIANGLE refreshes. SQUARE selects a file for deletion.")
                    TextFit(r, sheet.x, sheet.y + 666, 18, sheet.w, _storageMessage, _storageDelete == null ? Muted : Danger);
            }
        }
    }
}
