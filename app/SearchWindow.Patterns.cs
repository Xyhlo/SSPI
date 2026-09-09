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

        bool DrawPattern(IntPtr renderer)
        {
            int mode = BackdropPattern.Index(_cfg.BackgroundMode);
            if (mode == 0)
            {
                if (_patternTexture != IntPtr.Zero) { SDL_DestroyTexture(_patternTexture); _patternTexture = IntPtr.Zero; }
                _patternKey = ""; return false;
            }
            var color = Accent;
            string key = mode + ":" + color.r + ":" + color.g + ":" + color.b;
            lock (_patternLock)
            {
                if (_patternPixels != null)
                {
                    if (_patternPendingKey == key)
                    {
                        if (_patternTexture != IntPtr.Zero) SDL_DestroyTexture(_patternTexture);
                        _patternTexture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ABGR8888,
                            (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STATIC, W, H);
                        if (_patternTexture != IntPtr.Zero)
                        {
                            var pin = GCHandle.Alloc(_patternPixels, GCHandleType.Pinned);
                            try
                            {
                                if (SDL_UpdateTexture(_patternTexture, IntPtr.Zero, pin.AddrOfPinnedObject(), W * 4) != 0)
                                { SDL_DestroyTexture(_patternTexture); _patternTexture = IntPtr.Zero; }
                            }
                            finally { pin.Free(); }
                            if (_patternTexture != IntPtr.Zero) SDL_SetTextureBlendMode(_patternTexture, SDL_BlendMode.SDL_BLENDMODE_NONE);
                        }
                        if (_patternTexture == IntPtr.Zero) _patternFailedKey = key;
                        else _patternKey = key;
                    }
                    _patternPixels = null;
                }
                if (_patternKey != key && _patternFailedKey != key && !_patternBusy)
                {
                    _patternBusy = true;
                    ThreadPool.QueueUserWorkItem(_ => {
                        byte[] pixels = null;
                        try
                        {
                            byte[] small = BackdropPattern.Render(mode, color.r, color.g, color.b);
                            using (var image = Image.LoadPixelData<Rgba32>(small, BackdropPattern.Width, BackdropPattern.Height))
                            {
                                image.Mutate(x => x.Resize(W, H));
                                pixels = new byte[W * H * 4]; image.CopyPixelDataTo(pixels);
                            }
                        }
                        catch { }
                        lock (_patternLock)
                        {
                            if (pixels == null) _patternFailedKey = key;
                            _patternPixels = pixels; _patternPendingKey = key; _patternBusy = false;
                        }
                        Invalidated = true;
                    });
                }
            }
            if (_patternTexture != IntPtr.Zero && _patternKey == key)
            {
                var destination = new SDL_Rect { x = 0, y = 0, w = W, h = H };
                SDL_RenderCopy(renderer, _patternTexture, IntPtr.Zero, ref destination);
                return true;
            }
            return false;
        }
    }
}
