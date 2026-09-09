using System;
using System.IO;
using System.Runtime.InteropServices;
using SDL2.Types;
using static SDL2.SDL;

namespace Orbis
{
    public partial class SearchWindow
    {
        const uint LaunchDurationMs = 900;
        IntPtr _launchCube, _launchWordmark;
        bool _launchStarted, _launchFinished;
        uint _launchStartedAt;

        static IntPtr LoadBrandTexture(IntPtr renderer, string name, int width, int height)
        {
            IntPtr texture = IntPtr.Zero;
            try
            {
                string root;
                try { root = Internals.IO.GetAppBaseDirectory(); }
                catch { root = AppDomain.CurrentDomain.BaseDirectory; }
                string path = Path.Combine(root ?? "/app0", "assets/images/" + name);
                if (!File.Exists(path)) path = "/app0/assets/images/" + name;
                // Fixed, build-generated RGBA avoids image decoding on the startup thread.
                int expected = checked(width * height * 4);
                if (!File.Exists(path) || new FileInfo(path).Length != expected) return IntPtr.Zero;
                byte[] pixels = File.ReadAllBytes(path);
                if (pixels.Length != expected) return IntPtr.Zero;
                texture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ABGR8888,
                    (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STATIC, width, height);
                if (texture == IntPtr.Zero) return IntPtr.Zero;
                var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    if (SDL_UpdateTexture(texture, IntPtr.Zero, pin.AddrOfPinnedObject(), width * 4) != 0)
                        throw new InvalidOperationException("Startup texture upload failed");
                }
                finally { pin.Free(); }
                if (SDL_SetTextureBlendMode(texture, SDL_BlendMode.SDL_BLENDMODE_BLEND) != 0)
                    throw new InvalidOperationException("Startup texture blend failed");
                return texture;
            }
            catch
            {
                if (texture != IntPtr.Zero) SDL_DestroyTexture(texture);
                return IntPtr.Zero;
            }
        }

        void PrepareLaunchBranding(IntPtr renderer)
        {
            _launchCube = LoadBrandTexture(renderer, "sspi-startup.rgba", 512, 512);
            if (_launchCube == IntPtr.Zero) FinishLaunchBranding();
        }

        void PaintLaunchBranding(IntPtr renderer, int rise)
        {
            Fill(renderer, 0, 0, W, H, C(20, 20, 20));
            if (_launchCube != IntPtr.Zero)
            {
                var cube = new SDL_Rect { x = (W - 512) / 2, y = (H - 512) / 2 + rise, w = 512, h = 512 };
                SDL_RenderCopy(renderer, _launchCube, IntPtr.Zero, ref cube);
            }
        }

        bool DrawLaunchBranding(IntPtr renderer, uint now)
        {
            if (_launchFinished) return false;
            if (_cfg.ReduceMotion) { FinishLaunchBranding(); return false; }
            if (!_launchStarted) { _launchStartedAt = now; _launchStarted = true; }
            uint elapsed = unchecked(now - _launchStartedAt);
            if (elapsed >= LaunchDurationMs) { FinishLaunchBranding(); return false; }
            double progress = elapsed / (double)LaunchDurationMs;
            // One gentle lift and settle. Integer positions, fixed-size cached textures,
            // no rotation, scaling, filters, per-frame decoding or accumulating frames.
            int rise = -(int)Math.Round(10 * Math.Sin(Math.PI * progress));
            PaintLaunchBranding(renderer, rise);
            return true;
        }

        void DrawBetaWatermark(IntPtr renderer)
        {
            TextPx(renderer, 64, 14, 15, BuildIdentity.Label, C(112, 112, 112));
        }

        void FinishLaunchBranding()
        {
            _launchFinished = true;
            if (_launchCube != IntPtr.Zero) { SDL_DestroyTexture(_launchCube); _launchCube = IntPtr.Zero; }
            if (_launchWordmark != IntPtr.Zero) { SDL_DestroyTexture(_launchWordmark); _launchWordmark = IntPtr.Zero; }
        }

        public override void Dispose()
        {
            FinishLaunchBranding();
            foreach (var frame in _caseSizes.Values) if (frame != IntPtr.Zero) SDL_DestroyTexture(frame);
            _caseSizes.Clear();
            if (_caseFrame != IntPtr.Zero) { SDL_DestroyTexture(_caseFrame); _caseFrame = IntPtr.Zero; }
            if (_headerBrand != IntPtr.Zero) { SDL_DestroyTexture(_headerBrand); _headerBrand = IntPtr.Zero; }
            base.Dispose();
        }
    }
}
