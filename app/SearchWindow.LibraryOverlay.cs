using System;
using System.Runtime.InteropServices;
using System.Threading;
using SDL2.Types;
using static SDL2.SDL;

namespace Orbis
{
    public partial class SearchWindow
    {
        bool _libraryOpen, _returnToLibrary, _libraryCaptureAttempted;
        int _expandedLibraryFocus;
        IntPtr _libraryBackdrop;
        IntPtr _libraryBackdropUpload;
        byte[] _libraryBackdropPixels;
        int _libraryBackdropRow, _libraryBackdropGeneration;

        void CloseLibrary()
        {
            lock (_lock) {
                _libraryOpen = false;
                Interlocked.Increment(ref _libraryBackdropGeneration);
                Interlocked.Exchange(ref _libraryBackdropPixels, null);
                if (_libraryBackdrop != IntPtr.Zero) SDL_DestroyTexture(_libraryBackdrop);
                if (_libraryBackdropUpload != IntPtr.Zero) SDL_DestroyTexture(_libraryBackdropUpload);
                _libraryBackdropUpload = IntPtr.Zero;
                _libraryBackdropRow = 0;
                _libraryBackdrop = IntPtr.Zero;
                _libraryCaptureAttempted = false;
            }
        }

        void OpenLibrary()
        {
            CloseLibrary();
            _expandedLibraryFocus = Math.Max(0, Math.Min(_libraryGames.Count - 1, _searchLandingFocus - 1));
            _libraryOpen = true;
            _covers.BumpGeneration();
            Invalidated = true;
        }

        internal static int LibraryMove(int focus, int count, int direction)
        {
            if (count <= 0) return 0;
            return Math.Max(0, Math.Min(count - 1, focus + direction));
        }

        void HandleLibrary(DS4Button button)
        {
            if (button == DS4Button.SCE_PAD_BUTTON_CIRCLE || button == DS4Button.SCE_PAD_BUTTON_TOUCH_PAD) { CloseLibrary(); return; }
            if (button == DS4Button.SCE_PAD_BUTTON_L1 || button == DS4Button.SCE_PAD_BUTTON_R1) {
                CloseLibrary(); CycleTab(button == DS4Button.SCE_PAD_BUTTON_L1 ? -1 : 1); return;
            }
            int move = button == DS4Button.SCE_PAD_BUTTON_LEFT ? -1 : button == DS4Button.SCE_PAD_BUTTON_RIGHT ? 1 :
                button == DS4Button.SCE_PAD_BUTTON_UP ? -5 : button == DS4Button.SCE_PAD_BUTTON_DOWN ? 5 :
                button == DS4Button.SCE_PAD_BUTTON_L2 ? -10 : button == DS4Button.SCE_PAD_BUTTON_R2 ? 10 : 0;
            int oldPage = _expandedLibraryFocus / 10;
            _expandedLibraryFocus = LibraryMove(_expandedLibraryFocus, _libraryGames.Count, move);
            if (oldPage != _expandedLibraryFocus / 10) _covers.BumpGeneration();
            _searchLandingFocus = _expandedLibraryFocus + 1;
            if (button == DS4Button.SCE_PAD_BUTTON_CROSS && _libraryGames.Count > 0) {
                _selected = _libraryGames[_expandedLibraryFocus];
                _returnToLibrary = true; CloseLibrary(); StartResolveJob();
            }
        }

        void CaptureLibraryBackdrop(IntPtr renderer)
        {
            _libraryCaptureAttempted = true;
            var full = new SDL_Rect { x = 0, y = 0, w = W, h = H };
            var memory = Marshal.AllocHGlobal(W * H * 4);
            try {
                if (SDL_RenderReadPixels(renderer, ref full, SDL_PIXELFORMAT_ABGR8888, memory, W * 4) != 0) return;
                var captured = new byte[W * H * 4];
                Marshal.Copy(memory, captured, 0, captured.Length);
                int generation = _libraryBackdropGeneration;
                ThreadPool.QueueUserWorkItem(_ => {
                    try {
                        byte[] pixels = BuildLibraryBackdrop(captured);
                        lock (_lock)
                            if (generation == _libraryBackdropGeneration) _libraryBackdropPixels = pixels;
                    } catch (Exception ex) { SspiLog.Write("startup", "library backdrop: " + ex.GetType().Name); }
                });
            } finally { Marshal.FreeHGlobal(memory); }
        }

        internal static byte[] BuildLibraryBackdrop(byte[] captured)
        {
            const int bw = 240, bh = 135;
            if (captured == null || captured.Length != W * H * 4) throw new ArgumentException("Invalid frame");
            var small = new byte[bw * bh * 4];
            for (int y = 0; y < bh; y++) for (int x = 0; x < bw; x++) {
                int i = (y * bw + x) * 4, source = ((y * H / bh) * W + x * W / bw) * 4;
                for (int c = 0; c < 3; c++) small[i + c] = captured[source + c];
                small[i + 3] = 255;
            }
            var blurred = new byte[small.Length];
            for (int y = 0; y < bh; y++) for (int x = 0; x < bw; x++) {
                int i = (y * bw + x) * 4;
                for (int c = 0; c < 3; c++) {
                    int sum = 0, n = 0;
                    for (int yy = Math.Max(0, y - 2); yy <= Math.Min(bh - 1, y + 2); yy++)
                        for (int xx = Math.Max(0, x - 2); xx <= Math.Min(bw - 1, x + 2); xx++) { sum += small[(yy * bw + xx) * 4 + c]; n++; }
                    blurred[i + c] = (byte)(sum / n * 0.42);
                }
                blurred[i + 3] = 255;
            }
            // Pre-size and composite the rounded panel once. Software rendering
            // now copies native-format opaque pixels instead of scaling/blending.
            var result = new byte[W * 880 * 4];
            for (int y = 0; y < 880; y++) for (int x = 0; x < W; x++) {
                int dst = (y * W + x) * 4, src = (((y + 106) * bh / H) * bw + x * bw / W) * 4;
                int px = Math.Max(222 - x, x - 1697), py = Math.Max(156 - (y + 106), (y + 106) - 941);
                bool panel = x >= 210 && x < 1710 && y + 106 >= 144 && y + 106 < 954 &&
                    (px <= 0 || py <= 0 || px * px + py * py <= 144);
                for (int c = 0; c < 3; c++) result[dst + c] = panel
                    ? (byte)((blurred[src + c] * 30 + (c == 0 ? 29 : c == 1 ? 30 : 32) * 225) / 255)
                    : blurred[src + c];
                result[dst + 3] = 255;
            }
            return result;
        }

        void PumpLibraryBackdrop(IntPtr renderer)
        {
            byte[] pixels = Volatile.Read(ref _libraryBackdropPixels);
            if (pixels == null || _libraryBackdrop != IntPtr.Zero) return;
            if (_libraryBackdropUpload == IntPtr.Zero) {
                _libraryBackdropUpload = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_BGR888,
                    (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STATIC, W, 880);
                if (_libraryBackdropUpload == IntPtr.Zero) { Interlocked.Exchange(ref _libraryBackdropPixels, null); return; }
                SDL_SetTextureBlendMode(_libraryBackdropUpload, SDL_BlendMode.SDL_BLENDMODE_NONE);
            }
            int rows = Math.Min(32, 880 - _libraryBackdropRow);
            var area = new SDL_Rect { x = 0, y = _libraryBackdropRow, w = W, h = rows };
            var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            int status;
            try { status = SDL_UpdateTexture(_libraryBackdropUpload, ref area, IntPtr.Add(pin.AddrOfPinnedObject(), _libraryBackdropRow * W * 4), W * 4); }
            finally { pin.Free(); }
            if (status != 0) {
                SDL_DestroyTexture(_libraryBackdropUpload); _libraryBackdropUpload = IntPtr.Zero;
                Interlocked.Exchange(ref _libraryBackdropPixels, null); return;
            }
            _libraryBackdropRow += rows;
            if (_libraryBackdropRow == 880) {
                _libraryBackdrop = _libraryBackdropUpload; _libraryBackdropUpload = IntPtr.Zero;
                Interlocked.Exchange(ref _libraryBackdropPixels, null);
            }
        }

        void DrawLibraryOverlay(IntPtr r)
        {
            if (!_libraryCaptureAttempted) CaptureLibraryBackdrop(r);
            PumpLibraryBackdrop(r);
            if (_libraryBackdrop != IntPtr.Zero) {
                var dst = new SDL_Rect { x = 0, y = 106, w = W, h = 880 };
                SDL_RenderCopy(r, _libraryBackdrop, IntPtr.Zero, ref dst);
            } else Fill(r, 0, 106, W, 880, C(12, 13, 15));
            var panel = new SDL_Rect { x = 210, y = 144, w = 1500, h = 810 };
            if (_libraryBackdrop == IntPtr.Zero) SoftRect(r, panel, C(29, 30, 32));
            StrokeRect(r, panel, Border, 1);
            TextPx(r, 250, 172, 28, "Your library", White);
            TextFit(r, 470, 181, 17, 780, _libraryGames.Count + " installed · update information shown when available", Muted);
            DrawTouchpadAction(r, 1470, 166, "Close");
            Fill(r, 250, 225, 1420, 1, Border);
            int count = _libraryGames.Count;
            _expandedLibraryFocus = LibraryMove(_expandedLibraryFocus, count, 0);
            if (count == 0) {
                TextCentered(r, new SDL_Rect { x = 250, y = 475, w = 1420, h = 50 }, 26,
                    _consoleScanBusy ? "Reading installed applications…" : "No installed applications found", Muted);
                return;
            }
            int first = _expandedLibraryFocus / 10 * 10;
            for (int index = first; index < Math.Min(count, first + 10); index++) {
                var game = _libraryGames[index]; int slot = index - first;
                int x = 254 + (slot % 5) * 284, y = 247 + (slot / 5) * 295;
                bool focus = index == _expandedLibraryFocus, newer;
                string state = LibraryUpdateStatus(game, out newer);
                var art = new SDL_Rect { x = x + 41, y = y, w = 176, h = 222 };
                DrawCase(r, game, art);
                if (newer || focus) StrokeRect(r, new SDL_Rect { x = art.x - 6, y = y - 6, w = 188, h = 234 }, newer ? Warning : Accent, focus ? 2 : 1);
                TextFit(r, x + 7, y + 234, 19, 252, LibraryTitle(game), focus ? White : Muted);
                TextFit(r, x + 7, y + 262, 15, 252, state, newer ? Warning : Muted);
            }
            Fill(r, 250, 846, 1420, 1, Border);
            var selected = _libraryGames[_expandedLibraryFocus];
            TextFit(r, 250, 864, 22, 1050, LibraryTitle(selected), White);
            TextFit(r, 250, 904, 17, 1100, selected.TitleId + " · " + LibraryInstalledVersion(selected), Muted);
            GamepadIcons.Draw(r, "l2", 1460, 887, 28);
            TextPx(r, 1505, 890, 18, (first / 10 + 1) + " / " + ((count + 9) / 10), Muted);
            GamepadIcons.Draw(r, "r2", 1617, 887, 28);
        }

        void DrawTouchpadAction(IntPtr r, int x, int y, string label)
        {
            // Align with the section baseline; use the same understated hint style as tabs.
            GamepadIcons.Draw(r, "touchpad", x + 5, y + 15, 30);
            TextFit(r, x + 46, y + 16, 18, 142, label, Muted);
        }
    }
}
