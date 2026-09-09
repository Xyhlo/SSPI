using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using static SDL2.SDL;
namespace Orbis
{
    internal static class GamepadIcons
    {
        static readonly Dictionary<string, IntPtr> Textures = new Dictionary<string, IntPtr>();
        static readonly Dictionary<string, bool> UsesArtwork = new Dictionary<string, bool>();
        static IntPtr _renderer;
        public static string Diagnostics { get { return "P4Gamepad artwork with raster fallback"; } }
        public static bool AnyLoaded { get { return _renderer != IntPtr.Zero; } }
        public static void Ensure(IntPtr renderer)
        {
            if (_renderer == renderer) return;
            foreach (var texture in Textures.Values) SDL_DestroyTexture(texture);
            Textures.Clear(); UsesArtwork.Clear(); _renderer = renderer;
        }
        static string ArtFileFor(string key)
        {
            switch (key)
            {
                case "cross": return "T_P4_Cross_Color.png";
                case "circle": return "T_P4_Circle_Color.png";
                case "square": return "T_P4_Square_Color.png";
                case "triangle": return "T_P4_Triangle_Color.png";
                case "l1": return "T_P4_L1.png";
                case "r1": return "T_P4_R1.png";
                case "l2": return "T_P4_L2.png";
                case "r2": return "T_P4_R2.png";
                case "options": return "T_P4_Options.png";
                case "share": return "T_P4_Share.png";
                case "touchpad":
                case "touch": return "T_P4_Touch_Pad.png";
                case "up": return "T_P4_Dpad_UP.png";
                case "down": return "T_P4_Dpad_Down.png";
                case "left": return "T_P4_Dpad_Left.png";
                case "right": return "T_P4_Dpad_Right.png";
                case "l3": return "T_P4_L3.png";
                case "r3": return "T_P4_R3.png";
                default: return null;
            }
        }
        static string FindArtPath(string file)
        {
            try
            {
                string bas = null;
                try { bas = Internals.IO.GetAppBaseDirectory(); } catch { bas = AppDomain.CurrentDomain.BaseDirectory; }
                string[] paths =
                {
                    "/app0/assets/images/gamepad/" + file,
                    (bas ?? ".").TrimEnd('/', '\\') + "/assets/images/gamepad/" + file,
                    (bas ?? ".").TrimEnd('/', '\\') + "\\assets\\images\\gamepad\\" + file,
                    "assets/images/gamepad/" + file,
                };
                foreach (string p in paths) { try { if (File.Exists(p)) return p; } catch { } }
            }
            catch { }
            return null;
        }
        public static bool Draw(IntPtr renderer, string key, int x, int y, int size)
        {
            if (renderer == IntPtr.Zero || string.IsNullOrEmpty(key)) return false;
            Ensure(renderer); size = Math.Max(20, Math.Min(64, size)); key = key.ToLowerInvariant();
            string cacheKey = key + ":" + size;
            IntPtr texture;
            bool artwork;
            if (!Textures.TryGetValue(cacheKey, out texture))
            {
                if (Textures.Count >= 128) { foreach (var old in Textures.Values) SDL_DestroyTexture(old); Textures.Clear(); UsesArtwork.Clear(); }
                artwork = false;
                byte[] pixels = TryLoadArtwork(key, size, out artwork);
                if (pixels == null) pixels = Rasterize(key, size);
                SDL_SetHint(SDL_HINT_RENDER_SCALE_QUALITY, "1");
                texture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ABGR8888, (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STATIC, size, size);
                if (texture == IntPtr.Zero) return false;
                var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try { if (SDL_UpdateTexture(texture, IntPtr.Zero, pin.AddrOfPinnedObject(), size * 4) != 0) { SDL_DestroyTexture(texture); return false; } }
                finally { pin.Free(); }
                SDL_SetTextureBlendMode(texture, SDL_BlendMode.SDL_BLENDMODE_BLEND);
                Textures[cacheKey] = texture;
                UsesArtwork[cacheKey] = artwork;
            }
            else UsesArtwork.TryGetValue(cacheKey, out artwork);
            var rect = new SDL_Rect { x = x, y = y, w = size, h = size };
            SDL_RenderCopy(renderer, texture, IntPtr.Zero, ref rect);
            if (!artwork && (key == "l1" || key == "r1" || key == "l2" || key == "r2"))
            {
                int font = Math.Max(12, size * 4 / 9); string label = key.ToUpperInvariant();
                UiFont.DrawPx(renderer, x + (size - UiFont.MeasurePx(font, label)) / 2, y + (size - font) / 2 - 3,
                    font, label, new SDL_Color { r = 216, g = 216, b = 216, a = 255 });
            }
            return true;
        }
        static byte[] TryLoadArtwork(string key, int size, out bool artwork)
        {
            artwork = false;
            try
            {
                string file = ArtFileFor(key);
                if (file == null) return null;
                string path = FindArtPath(file);
                if (path == null) return null;
                byte[] bytes = File.ReadAllBytes(path);
                if (bytes.Length < 32 || bytes.Length > 4 * 1024 * 1024) return null;
                using (var image = Image.Load<Rgba32>(bytes))
                {
                    if (image.Width < 8 || image.Height < 8 || image.Width > 512 || image.Height > 512) return null;
                    int x0, y0, x1, y1;
                    if (!ContentBounds(image, out x0, out y0, out x1, out y1)) return null;
                    int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
                    double fit = size / (double)Math.Max(cw, ch);
                    // Normalize visible size: longest visible edge fills the slot.
                    int tw = Math.Max(1, (int)Math.Round(cw * fit));
                    int th = Math.Max(1, (int)Math.Round(ch * fit));
                    tw = Math.Min(size, tw); th = Math.Min(size, th);
                    using (var cropped = image.Clone(ctx =>
                    {
                        ctx.Crop(new Rectangle(x0, y0, cw, ch));
                        if (tw != cw || th != ch) ctx.Resize(tw, th, KnownResamplers.Bicubic);
                    }))
                    {
                        var canvas = new byte[size * size * 4];
                        int ox = (size - cropped.Width) / 2, oy = (size - cropped.Height) / 2;
                        byte[] row = new byte[cropped.Width * cropped.Height * 4];
                        cropped.CopyPixelDataTo(row);
                        for (int yy = 0; yy < cropped.Height; yy++)
                            Buffer.BlockCopy(row, yy * cropped.Width * 4, canvas, ((oy + yy) * size + ox) * 4, cropped.Width * 4);
                        artwork = true;
                        return canvas;
                    }
                }
            }
            catch { }
            return null;
        }
        static bool ContentBounds(Image<Rgba32> image, out int x0, out int y0, out int x1, out int y1)
        {
            int minX = image.Width, minY = image.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                Rgba32 p = image[x, y];
                if (p.A < 24) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
            if (maxX < 0) { x0 = y0 = x1 = y1 = 0; return false; }
            x0 = Math.Max(0, minX - 1); y0 = Math.Max(0, minY - 1);
            x1 = Math.Min(image.Width - 1, maxX + 1); y1 = Math.Min(image.Height - 1, maxY + 1);
            return true;
        }
        static byte[] Rasterize(string key, int size)
        {
            byte red = 205, green = 205, blue = 205;
            if (key == "cross") { red = 165; green = 185; blue = 225; }
            if (key == "circle") { red = 235; green = 137; blue = 149; }
            if (key == "square") { red = 210; green = 155; blue = 211; }
            if (key == "triangle") { red = 131; green = 207; blue = 184; }
            byte[] pixels = new byte[size * size * 4]; double scale = size / 32.0, halfStroke = Math.Max(.8, size / 25.0);
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                double px = (x + .5) / scale, py = (y + .5) / scale, distance;
                if (key == "circle") distance = Math.Abs(Math.Sqrt((px-16)*(px-16)+(py-16)*(py-16))-10);
                else if (key == "cross") distance = Math.Min(Line(px,py,8,8,24,24), Line(px,py,24,8,8,24));
                else if (key == "triangle") distance = Math.Min(Line(px,py,16,5,28,26),Math.Min(Line(px,py,28,26,4,26),Line(px,py,4,26,16,5)));
                else if (key == "square") distance = Box(px,py,6,6,26,26);
                else if (key == "options") distance = Math.Min(Line(px,py,5,8,27,8),Math.Min(Line(px,py,5,16,27,16),Line(px,py,5,24,27,24)));
                else if (key == "l1" || key == "r1" || key == "l2" || key == "r2") distance = Box(px,py,1.5,6,30.5,26);
                else
                {
                    double dx=px,dy=py;
                    if (key == "down") dy=32-py;
                    else if (key == "left") { dx=py;dy=px; }
                    else if (key == "right") { dx=py;dy=32-px; }
                    distance=Math.Min(Line(dx,dy,6,21,16,11),Line(dx,dy,16,11,26,21));
                }
                double coverage=Math.Max(0,Math.Min(1,halfStroke+.5-distance*scale)); int at=(y*size+x)*4;
                pixels[at]=red;pixels[at+1]=green;pixels[at+2]=blue;pixels[at+3]=(byte)Math.Round(coverage*255);
            }
            return pixels;
        }
        static double Line(double x,double y,double ax,double ay,double bx,double by)
        {
            double dx=bx-ax,dy=by-ay,t=Math.Max(0,Math.Min(1,((x-ax)*dx+(y-ay)*dy)/(dx*dx+dy*dy)));
            dx=x-ax-t*dx;dy=y-ay-t*dy;return Math.Sqrt(dx*dx+dy*dy);
        }
        static double Box(double x,double y,double l,double t,double r,double b)
        {
            return Math.Min(Math.Min(Line(x,y,l,t,r,t),Line(x,y,r,t,r,b)),Math.Min(Line(x,y,r,b,l,b),Line(x,y,l,b,l,t)));
        }
        public static string Badge(string key) { return "["+(key??"?").ToUpperInvariant()+"]"; }
    }
}
