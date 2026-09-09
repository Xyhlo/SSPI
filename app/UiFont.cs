using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SDL2.Types.FreeType;
using static SDL2.SDL;

namespace Orbis
{
    /// <summary>
    /// Crisp FreeType text for software SDL. Integer pixel sizes, cached textures, no scale-up blur.
    /// Falls back to PixelFont if FreeType/font file unavailable.
    /// </summary>
    internal static unsafe class UiFont
    {
        static IntPtr _lib;
        static FT_Face* _face;
        static bool _ready;
        static bool _tried;
        static string _fontPath = "";
        static IntPtr _renderer;
        static readonly object Gate = new object();
        static readonly Dictionary<TextureKey, CacheEntry> Cache = new Dictionary<TextureKey, CacheEntry>();
        static readonly Dictionary<MeasureKey, int> WidthCache = new Dictionary<MeasureKey, int>();
        const int MaxCache = 256;
        const int MaxWidthCache = 1024;

        struct MeasureKey : IEquatable<MeasureKey>
        {
            public int Px;
            public string Text;
            public bool Equals(MeasureKey other)
            {
                return Px == other.Px && string.Equals(Text, other.Text, StringComparison.Ordinal);
            }
            public override bool Equals(object obj) { return obj is MeasureKey && Equals((MeasureKey)obj); }
            public override int GetHashCode()
            {
                return (Px * 397) ^ (Text == null ? 0 : Text.GetHashCode());
            }
        }

        struct TextureKey : IEquatable<TextureKey>
        {
            public int Px;
            public byte R, G, B;
            public string Text;
            public bool Equals(TextureKey other)
            {
                return Px == other.Px && R == other.R && G == other.G && B == other.B &&
                    string.Equals(Text, other.Text, StringComparison.Ordinal);
            }
            public override bool Equals(object obj) { return obj is TextureKey && Equals((TextureKey)obj); }
            public override int GetHashCode()
            {
                int hash = Px;
                hash = (hash * 397) ^ R;
                hash = (hash * 397) ^ G;
                hash = (hash * 397) ^ B;
                return (hash * 397) ^ (Text == null ? 0 : Text.GetHashCode());
            }
        }

        struct CacheEntry
        {
            public IntPtr Tex;
            public int W, H;
            public long LastUse;
        }

        static long _tick;

        public static bool Ready { get { Ensure(); return _ready; } }

        public static void BindRenderer(IntPtr renderer)
        {
            lock (Gate)
            {
                if (_renderer != IntPtr.Zero && renderer != _renderer)
                    ClearCache();
                _renderer = renderer;
            }
        }

        public static void Ensure()
        {
            if (_tried) return;
            lock (Gate)
            {
                if (_tried) return;
                _tried = true;
                try
                {
                    string[] paths =
                    {
                        "assets/fonts/Inter-Regular.ttf",
                        "/app0/assets/fonts/Inter-Regular.ttf",
                        System.IO.Path.Combine(AppSettings.DataDir, "Inter-Regular.ttf")
                    };
                    try
                    {
                        string bas = Orbis.Internals.IO.GetAppBaseDirectory();
                        if (!string.IsNullOrEmpty(bas))
                            paths = new[]
                            {
                                System.IO.Path.Combine(bas, "assets", "fonts", "Inter-Regular.ttf"),
                                "assets/fonts/Inter-Regular.ttf",
                                "/app0/assets/fonts/Inter-Regular.ttf"
                            };
                    }
                    catch { }

                    if (FT_Init_FreeType(out _lib) != 0)
                    {
                        _ready = false;
                        return;
                    }
                    foreach (var p in paths)
                    {
                        if (string.IsNullOrEmpty(p) || !System.IO.File.Exists(p)) continue;
                        FT_Face* face;
                        if (FT_New_Face(_lib, p, 0, out face) == 0 && face != null)
                        {
                            _face = face;
                            _fontPath = p;
                            _ready = true;
                            break;
                        }
                    }
                }
                catch
                {
                    _ready = false;
                }
            }
        }

        /// <summary>scale 2/3/4/5 map to ~16/20/28/36 px FreeType.</summary>
        public static int PxFromScale(int scale)
        {
            if (scale <= 2) return 18;
            if (scale == 3) return 24;
            if (scale == 4) return 32;
            return 40;
        }

        public static void Draw(IntPtr renderer, int x, int y, int scale, string text, SDL_Color color)
        {
            DrawPx(renderer, x, y, PxFromScale(scale), text, color);
        }

        public static void DrawPx(IntPtr renderer, int x, int y, int px, string text, SDL_Color color)
        {
            Ensure();
            if (!_ready || _face == null || renderer == IntPtr.Zero)
            {
                int fallbackScale = Math.Max(1, (px + 6) / 7);
                PixelFont.Draw(renderer, x, y, fallbackScale, text ?? "", color.r, color.g, color.b);
                return;
            }
            if (string.IsNullOrEmpty(text)) return;
            IntPtr tex;
            int tw, th;
            if (!GetTexture(renderer, text, px, color, out tex, out tw, out th))
            {
                int fallbackScale = Math.Max(1, (px + 6) / 7);
                PixelFont.Draw(renderer, x, y, fallbackScale, text, color.r, color.g, color.b);
                return;
            }
            var dst = new SDL_Rect { x = x, y = y, w = tw, h = th };
            SDL_RenderCopy(renderer, tex, IntPtr.Zero, ref dst);
        }

        public static int Measure(int scale, string text)
        {
            return MeasurePx(PxFromScale(scale), text);
        }

        public static int MeasurePx(int px, string text)
        {
            Ensure();
            if (!_ready || string.IsNullOrEmpty(text))
                return PixelFont.TextWidth(Math.Max(1, (px + 6) / 7), text ?? "");
            lock (Gate)
            {
                var key = new MeasureKey { Px = px, Text = text };
                int cached;
                if (WidthCache.TryGetValue(key, out cached)) return cached;
                if (FT_Set_Pixel_Sizes(_face, 0, px) != 0)
                    return PixelFont.TextWidth(Math.Max(1, (px + 6) / 7), text);
                int width = MeasureAdvance(text);
                if (WidthCache.Count >= MaxWidthCache) WidthCache.Clear();
                WidthCache[key] = width;
                return width;
            }
        }

        public static string EllipsizePx(string text, int px, int maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0) return "";
            if (MeasurePx(px, text) <= maxWidth) return text;
            const string suffix = "...";
            int suffixWidth = MeasurePx(px, suffix);
            if (suffixWidth >= maxWidth) return suffix;
            int lo = 0;
            int hi = text.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (MeasurePx(px, text.Substring(0, mid)) + suffixWidth <= maxWidth) lo = mid;
                else hi = mid - 1;
            }
            return text.Substring(0, lo).TrimEnd() + suffix;
        }

        static bool GetTexture(IntPtr renderer, string text, int px, SDL_Color color,
            out IntPtr tex, out int w, out int h)
        {
            tex = IntPtr.Zero; w = h = 0;
            var key = new TextureKey
            {
                Px = px,
                R = color.r,
                G = color.g,
                B = color.b,
                Text = text
            };
            lock (Gate)
            {
                _tick++;
                CacheEntry e;
                if (Cache.TryGetValue(key, out e) && e.Tex != IntPtr.Zero)
                {
                    e.LastUse = _tick;
                    Cache[key] = e;
                    tex = e.Tex; w = e.W; h = e.H;
                    return true;
                }
            }

            IntPtr created;
            int cw, ch;
            if (!Rasterize(renderer, text, px, color, out created, out cw, out ch))
                return false;

            lock (Gate)
            {
                if (Cache.Count >= MaxCache)
                    EvictOne();
                Cache[key] = new CacheEntry { Tex = created, W = cw, H = ch, LastUse = ++_tick };
            }
            tex = created; w = cw; h = ch;
            return true;
        }

        static void EvictOne()
        {
            TextureKey worst = default(TextureKey);
            bool found = false;
            long oldest = long.MaxValue;
            foreach (var kv in Cache)
            {
                if (kv.Value.LastUse < oldest)
                {
                    oldest = kv.Value.LastUse;
                    worst = kv.Key;
                    found = true;
                }
            }
            if (!found) return;
            var e = Cache[worst];
            if (e.Tex != IntPtr.Zero)
            {
                try { SDL_DestroyTexture(e.Tex); } catch { }
            }
            Cache.Remove(worst);
        }

        static void ClearCache()
        {
            foreach (var entry in Cache.Values)
                if (entry.Tex != IntPtr.Zero) try { SDL_DestroyTexture(entry.Tex); } catch { }
            Cache.Clear();
        }

        static bool Rasterize(IntPtr renderer, string text, int px, SDL_Color color,
            out IntPtr tex, out int w, out int h)
        {
            tex = IntPtr.Zero; w = h = 0;
            lock (Gate)
            {
                if (FT_Set_Pixel_Sizes(_face, 0, px) != 0) return false;
                int adv = MeasureAdvance(text);
                int ascender = (int)(_face->Size->Metrics.Ascender >> 6);
                int descender = (int)(-_face->Size->Metrics.Descender >> 6);
                if (ascender <= 0) ascender = px;
                if (descender < 2) descender = Math.Max(2, px / 4);
                int height = Math.Max(px + 8, ascender + descender + 6);
                w = Math.Min(Math.Max(8, adv + 4), 3600);
                h = Math.Min(height, 128);

                IntPtr surf = SDL_CreateRGBSurface(0, w, h, 32,
                    0x00FF0000u, 0x0000FF00u, 0x000000FFu, 0xFF000000u);
                if (surf == IntPtr.Zero) return false;
                var surface = (SDL_Surface*)surf.ToPointer();
                SDL_FillRect(surf, IntPtr.Zero, SDL_MapRGBA(surface->format, 0, 0, 0, 0));

                int baseline = ascender + 2;
                if (baseline < px) baseline = px;
                if (baseline > h - 2) baseline = h - 2;

                int penX = 2;
                FT_GlyphSlot* slot = _face->Glyph;
                for (int i = 0; i < text.Length; i++)
                {
                    char ch = text[i];
                    if (ch == '\n') continue;
                    uint gi = FT_Get_Char_Index(_face, ch);
                    if (FT_Load_Glyph(_face, gi, 0) != 0) { penX += px / 3; continue; }
                    if (FT_Render_Glyph(slot, 0) != 0) { penX += GlyphAdv(slot); continue; }

                    int gx = penX + slot->BitmapLeft;
                    int gy = baseline - slot->BitmapTop;
                    BlitGlyph(surface, &slot->Bitmap, gx, gy, color);
                    penX += GlyphAdv(slot);
                    if (penX >= w - 4) break;
                }

                // Glyph coverage is already stored in alpha; colour-keying clips dark glyph edges.
                // Glyphs are rasterized at their final display size. Never resample them.
                SDL_SetHint(SDL_HINT_RENDER_SCALE_QUALITY, "0");
                tex = SDL_CreateTextureFromSurface(renderer, surf);
                SDL_SetHint(SDL_HINT_RENDER_SCALE_QUALITY, "1");
                SDL_FreeSurface(surf);
                if (tex == IntPtr.Zero) return false;
                SDL_SetTextureBlendMode(tex, SDL_BlendMode.SDL_BLENDMODE_BLEND);
                // Keep the actual surface width: changing it here scales the entire texture.
                return true;
            }
        }

        static int MeasureAdvance(string text)
        {
            int width = 0;
            FT_GlyphSlot* slot = _face->Glyph;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n') continue;
                uint gi = FT_Get_Char_Index(_face, text[i]);
                if (FT_Load_Glyph(_face, gi, 0) != 0)
                {
                    width += Math.Max(6, (int)_face->Size->Metrics.XPPem / 3);
                    continue;
                }
                width += GlyphAdv(slot);
            }
            return Math.Max(8, width);
        }

        static int GlyphAdv(FT_GlyphSlot* slot)
        {
            int adv = slot->Advance.X;
            if (adv <= 0) adv = (int)(slot->Metrics.HoriAdvance >> 6);
            if (adv <= 0) adv = 8;
            return adv;
        }

        static void BlitGlyph(SDL_Surface* surface, FT_Bitmap* bmp, int ox, int oy, SDL_Color color)
        {
            if (bmp->Buffer == null || bmp->Width <= 0 || bmp->Rows <= 0) return;
            int pitch = bmp->Pitch;
            byte* src = bmp->Buffer;
            byte* dst = (byte*)surface->pixels.ToPointer();
            int sp = surface->pitch;
            int sw = surface->w;
            int sh = surface->h;
            int rows = (int)bmp->Rows;
            int cols = (int)bmp->Width;

            for (int row = 0; row < rows; row++)
            {
                int dy = oy + row;
                if (dy < 0 || dy >= sh) continue;
                for (int col = 0; col < cols; col++)
                {
                    int dx = ox + col;
                    if (dx < 0 || dx >= sw) continue;
                    byte a = src[row * pitch + col];
                    if (a < 8) continue;
                    int di = dy * sp + dx * 4;
                    dst[di + 0] = color.b;
                    dst[di + 1] = color.g;
                    dst[di + 2] = color.r;
                    dst[di + 3] = a;
                }
            }
        }

        [DllImport("libSceFreeTypeOl", CallingConvention = CallingConvention.Cdecl)]
        static extern int FT_Init_FreeType(out IntPtr ftLib);

        [DllImport("libSceFreeTypeOl", CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
        static extern int FT_New_Face(IntPtr ftLib, string fontPath, int faceIndex, out FT_Face* face);

        [DllImport("libSceFreeTypeOl", CallingConvention = CallingConvention.Cdecl)]
        static extern int FT_Set_Pixel_Sizes(FT_Face* face, int pixelWidth, int pixelHeight);

        [DllImport("libSceFreeTypeOl", CallingConvention = CallingConvention.Cdecl)]
        static extern int FT_Load_Glyph(FT_Face* face, uint glyphIndex, uint loadFlags);

        [DllImport("libSceFreeTypeOl", CallingConvention = CallingConvention.Cdecl)]
        static extern int FT_Render_Glyph(FT_GlyphSlot* slot, int renderMode);

        [DllImport("libSceFreeTypeOl", CallingConvention = CallingConvention.Cdecl)]
        static extern uint FT_Get_Char_Index(FT_Face* face, ulong charCode);
    }
}
