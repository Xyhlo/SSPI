using System;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System.Runtime.InteropServices;
using static SDL2.SDL;

namespace Orbis
{
    public partial class SearchWindow
    {
        IntPtr _patternTexture;
        string _patternKey = "", _patternPendingKey = "", _patternFailedKey = "";
        byte[] _patternPixels;
        bool _patternBusy;
        readonly object _patternLock = new object();
        string _patternDesiredKey = "", _patternUploadKey = "";
        int _patternMode = -1, _patternColor, _patternUploadRow;
        IntPtr _patternUploadTexture;
        byte[] _patternUploadPixels;
        bool _patternDisposed;

        bool DrawPattern(IntPtr renderer)
        {
            if (_patternDisposed) return false;
            bool custom = _cfg.BackgroundMode == AppSettings.BackgroundImage && PixelBackground.IsOwnedPath(_cfg.BackgroundImagePath);
            string customPath = custom ? _cfg.BackgroundImagePath : "";
            string overlay = _cfg.BackgroundOverlay;
            int imageOpacity = _cfg.BackgroundImageOpacity, effectOpacity = _cfg.BackgroundEffectOpacity;
            int mode = custom ? BackdropPattern.Modes.Length : BackdropPattern.Index(_cfg.BackgroundMode);
            if (mode == 0)
            {
                if (_patternTexture != IntPtr.Zero) { SDL_DestroyTexture(_patternTexture); _patternTexture = IntPtr.Zero; }
                AbortPatternUpload();
                lock (_patternLock) { _patternDesiredKey = ""; _patternPixels = null; }
                _patternMode = 0;
                _patternKey = ""; return false;
            }
            var color = Accent;
            int rgb = color.r << 16 | color.g << 8 | color.b;
            string desiredKey = mode + ":" + rgb + ":" + customPath + ":" + overlay + ":" + imageOpacity + ":" + effectOpacity;
            if (_patternMode != mode || _patternColor != rgb || _patternDesiredKey != desiredKey)
            {
                _patternMode = mode; _patternColor = rgb;
                lock (_patternLock) _patternDesiredKey = desiredKey;
            }
            string key = _patternDesiredKey;
            if (_patternUploadKey != key) AbortPatternUpload();
            lock (_patternLock)
            {
                if (_patternPixels != null)
                {
                    if (_patternPendingKey == key)
                    {
                        _patternUploadPixels = _patternPixels;
                        _patternUploadKey = key;
                    }
                    _patternPixels = null;
                }
            }
            PumpPatternUpload(renderer);
            lock (_patternLock)
            {
                if (_patternKey != key && _patternUploadKey != key && _patternFailedKey != key && !_patternBusy)
                {
                    _patternBusy = true;
                    ThreadPool.QueueUserWorkItem(_ => {
                        byte[] pixels = null;
                        try
                        {
                            if (custom) pixels = PixelBackground.Render(customPath, W, H, color.r, color.g, color.b, overlay, imageOpacity, effectOpacity);
                            else {
                            byte[] small = BackdropPattern.Render(mode, color.r, color.g, color.b);
                            using (var image = Image.LoadPixelData<Rgba32>(CoverImageDecoder.CreateConfiguration(),
                                small, BackdropPattern.Width, BackdropPattern.Height))
                            {
                                image.Mutate(x => x.Resize(W, H));
                                pixels = new byte[W * H * 4]; image.CopyPixelDataTo(pixels);
                            }
                            }
                        }
                        catch { }
                        lock (_patternLock)
                        {
                            if (!_patternDisposed && _patternDesiredKey == key)
                            {
                                if (pixels == null) _patternFailedKey = key;
                                _patternPixels = pixels; _patternPendingKey = key;
                            }
                            _patternBusy = false;
                        }
                        Invalidated = true;
                    });
                }
            }
            // Keep the previous complete background visible while the replacement
            // is prepared. Never publish partially uploaded rows.
            if (_patternTexture != IntPtr.Zero)
            {
                var destination = new SDL_Rect { x = 0, y = 0, w = W, h = H };
                SDL_RenderCopy(renderer, _patternTexture, IntPtr.Zero, ref destination);
                return true;
            }
            return false;
        }

        void PumpPatternUpload(IntPtr renderer)
        {
            if (_patternUploadPixels == null) return;
            var pin = default(GCHandle);
            try
            {
                if (_patternUploadPixels.Length != W * H * 4) throw new InvalidOperationException("Invalid pattern pixels");
                if (_patternUploadTexture == IntPtr.Zero)
                {
                    _patternUploadTexture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_BGR888,
                        (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STATIC, W, H);
                    if (_patternUploadTexture == IntPtr.Zero ||
                        SDL_SetTextureBlendMode(_patternUploadTexture, SDL_BlendMode.SDL_BLENDMODE_NONE) != 0)
                        throw new InvalidOperationException("Pattern texture creation failed");
                }
                int pitch = W * 4;
                int rows = Math.Min(H - _patternUploadRow, CoverCache.UploadBytesPerFrame / pitch);
                var area = new SDL_Rect { x = 0, y = _patternUploadRow, w = W, h = rows };
                pin = GCHandle.Alloc(_patternUploadPixels, GCHandleType.Pinned);
                if (SDL_UpdateTexture(_patternUploadTexture, ref area,
                    IntPtr.Add(pin.AddrOfPinnedObject(), _patternUploadRow * pitch), pitch) != 0)
                    throw new InvalidOperationException("Pattern texture upload failed");
                _patternUploadRow += rows;
                if (_patternUploadRow == H)
                {
                    if (_patternTexture != IntPtr.Zero) SDL_DestroyTexture(_patternTexture);
                    _patternTexture = _patternUploadTexture; _patternKey = _patternUploadKey;
                    _patternUploadTexture = IntPtr.Zero;
                    AbortPatternUpload();
                }
            }
            catch
            {
                lock (_patternLock) _patternFailedKey = _patternUploadKey;
                AbortPatternUpload();
            }
            finally { if (pin.IsAllocated) pin.Free(); }
        }

        void AbortPatternUpload()
        {
            if (_patternUploadTexture != IntPtr.Zero) SDL_DestroyTexture(_patternUploadTexture);
            _patternUploadTexture = IntPtr.Zero; _patternUploadPixels = null;
            _patternUploadKey = ""; _patternUploadRow = 0;
        }

        void ReleasePattern()
        {
            ReleaseSceneCache();
            CloseLibrary();
            lock (_patternLock) { _patternDisposed = true; _patternPixels = null; _patternDesiredKey = ""; }
            AbortPatternUpload();
            if (_patternTexture != IntPtr.Zero) SDL_DestroyTexture(_patternTexture);
            _patternTexture = IntPtr.Zero;
        }
    }
}
