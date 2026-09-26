using System;
using System.IO;

namespace Orbis
{
    // The phone sends only an opaque 320 x 180 RGBA canvas, never the original photo.
    internal static class PixelBackground
    {
        internal const int Width = 320, Height = 180, ByteCount = Width * Height * 4;
        internal static bool IsOwnedPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            try {
                string name = Path.GetFileName(path);
                Guid id;
                return name.StartsWith("background-", StringComparison.Ordinal) && name.EndsWith(".rgba", StringComparison.Ordinal) &&
                    Guid.TryParseExact(name.Substring(11, name.Length - 16), "N", out id) &&
                    string.Equals(Path.GetFullPath(path), Path.GetFullPath(Path.Combine(AppSettings.DataDir, name)), StringComparison.Ordinal);
            } catch { return false; }
        }

        internal static string Save(AppSettings settings, byte[] rgba)
        {
            if (settings == null || rgba == null || rgba.Length != ByteCount) return "Upload a 320 x 180 pixel background from the Appearance page.";
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
            string path = Path.Combine(AppSettings.DataDir, "background-" + Guid.NewGuid().ToString("N") + ".rgba");
            lock (settings) {
                string oldPath = settings.BackgroundImagePath, oldMode = settings.BackgroundMode;
                try {
                    Directory.CreateDirectory(AppSettings.DataDir);
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                        stream.Write(rgba, 0, rgba.Length); stream.Flush(true);
                    }
                    settings.BackgroundImagePath = path; settings.BackgroundMode = AppSettings.BackgroundImage;
                    if (!settings.Save()) throw new IOException("Could not save appearance settings.");
                } catch {
                    settings.BackgroundImagePath = oldPath; settings.BackgroundMode = oldMode;
                    try { File.Delete(path); } catch { }
                    return "Could not save the background. Check free space and try again.";
                }
                if (IsOwnedPath(oldPath) && oldPath != path) try { File.Delete(oldPath); } catch { }
            }
            return null;
        }

        internal static byte[] ReadPixels(string path)
        {
            if (!IsOwnedPath(path)) throw new IOException("Unknown background path");
            byte[] small;
            using (var file = File.OpenRead(path)) {
                if (file.Length != ByteCount) throw new IOException("Incomplete background");
                small = new byte[ByteCount]; int done = 0;
                while (done < small.Length) { int n = file.Read(small, done, small.Length - done); if (n == 0) throw new EndOfStreamException(); done += n; }
            }
            return small;
        }

        internal static byte[] Mask(string pattern)
        {
            int index = BackdropPattern.Index(pattern);
            var mask = new byte[BackdropPattern.Width * BackdropPattern.Height];
            if (index == 0) return mask;
            var rgba = BackdropPattern.Render(index, 255, 255, 255);
            for (int i = 0; i < mask.Length; i++)
                mask[i] = (byte)Math.Min(255, Math.Max(0, Math.Max(rgba[i * 4], Math.Max(rgba[i * 4 + 1], rgba[i * 4 + 2])) - 12) * 4);
            return mask;
        }

        internal static byte[] Render(string path, int width, int height, byte red = 255, byte green = 255, byte blue = 255,
            string pattern = "solid", int imageOpacity = 35, int effectOpacity = 0)
        {
            var small = ReadPixels(path);
            var mask = Mask(pattern);
            int pw = BackdropPattern.Width, ph = BackdropPattern.Height;
            var composite = new byte[pw * ph * 4];
            double image = Math.Max(0, Math.Min(100, imageOpacity)) / 100.0;
            double effect = Math.Max(0, Math.Min(100, effectOpacity)) / 100.0;
            var accent = new[] { red, green, blue };
            for (int y = 0; y < ph; y++) for (int x = 0; x < pw; x++) {
                int src = ((y * Height / ph) * Width + x * Width / pw) * 4, dst = (y * pw + x) * 4;
                double luminance = (small[src] * .2126 + small[src + 1] * .7152 + small[src + 2] * .0722) / 255.0;
                double ink = mask[y * pw + x] / 255.0 * effect;
                for (int c = 0; c < 3; c++) {
                    double tinted = small[src + c] * (1 - effect) + accent[c] * luminance * effect;
                    composite[dst + c] = (byte)Math.Min(255, tinted * image + accent[c] * ink * .35);
                }
                composite[dst + 3] = 255;
            }
            var pixels = new byte[checked(width * height * 4)];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
                int src = ((y * ph / height) * pw + x * pw / width) * 4, dst = (y * width + x) * 4;
                pixels[dst] = composite[src]; pixels[dst + 1] = composite[src + 1];
                pixels[dst + 2] = composite[src + 2]; pixels[dst + 3] = 255;
            }
            return pixels;
        }
    }
}
