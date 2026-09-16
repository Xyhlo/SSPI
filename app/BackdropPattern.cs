using System;

namespace Orbis
{
    internal static class BackdropPattern
    {
        public const int Width = 640, Height = 360;
        public static readonly string[] Modes = { "solid", "ripple", "wave", "grid", "halo", "graphite", "obsidian", "slate", "flowers" };
        public static readonly string[] Names = { "Plain charcoal", "Ripple", "Wave", "Mosaic", "Halo", "Graphite fade", "Obsidian", "Slate glow", "ASCII Flowers" };
        public static int Index(string mode) { return Math.Max(0, Array.FindIndex(Modes, m => string.Equals(m, mode, StringComparison.OrdinalIgnoreCase))); }
        public static bool IsDarkGradient(string mode) { int index = Index(mode); return index >= 5 && index <= 7; }
        public static byte[] Render(int pattern, byte red, byte green, byte blue)
        {
            if (pattern == 8) return RenderFlowers(red, green, blue);
            var data = new byte[Width * Height * 4];
            for (int y = 0; y < Height; y++)
                for (int x = 0; x < Width; x++)
                {
                    double u = x / (double)Width, v = y / (double)Height;
                    if (pattern >= 5)
                    {
                        double light = pattern == 5 ? 8 + 17 * (1-u) * (1-v) :
                            pattern == 6 ? 5 + 10 * Math.Exp(-((u-.75)*(u-.75)+(v-.9)*(v-.9))*4) :
                            9 + 18 * Math.Exp(-((u-.2)*(u-.2)+(v-.15)*(v-.15))*3);
                        int pixel = (y * Width + x) * 4;
                        data[pixel] = data[pixel+1] = data[pixel+2] = (byte)light;
                        data[pixel+3] = 255;
                        continue;
                    }
                    double dx = u - .82, dy = (v - .85) * .7;
                    double radius = Math.Sqrt(dx * dx + dy * dy);
                    double glow = Math.Exp(-radius * radius * 5.5);
                    double wave = .5 + .5 * Math.Cos(radius * 38);
                    if (pattern == 2) wave = .5 + .5 * Math.Sin(u * 42 + Math.Sin(v * 5) * 3);
                    if (pattern == 3) wave = .25 + .75 * (.5 + .5 * Math.Sin((x / 7) * .61 + (y / 7) * .32));
                    if (pattern == 4) wave = Math.Exp(-Math.Pow((radius - .26) * 9, 2));
                    double mesh = (x % 7 == 0 || y % 7 == 0) ? .78 : 1;
                    double intensity = pattern == 0 ? 0 : (.025 + .22 * glow) * (.30 + .70 * wave) * mesh;
                    int p = (y * Width + x) * 4;
                    data[p] = (byte)(11 + red * intensity);
                    data[p + 1] = (byte)(11 + green * intensity);
                    data[p + 2] = (byte)(12 + blue * intensity);
                    data[p + 3] = 255;
                }
            if (pattern == 0 || pattern >= 5) return data;
            // Pre-soften once on the worker. There is no frame-time blur pass.
            var horizontal = new byte[data.Length];
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
            {
                int p = (y * Width + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    int sum = 0;
                    for (int d = -2; d <= 2; d++) sum += data[(y * Width + Math.Max(0, Math.Min(Width - 1, x + d))) * 4 + channel];
                    horizontal[p + channel] = (byte)(sum / 5);
                }
                horizontal[p + 3] = 255;
            }
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
            {
                int p = (y * Width + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    int sum = 0;
                    for (int d = -2; d <= 2; d++) sum += horizontal[(Math.Max(0, Math.Min(Height - 1, y + d)) * Width + x) * 4 + channel];
                    data[p + channel] = (byte)(sum / 5);
                }
            }
            return data;
        }

        static readonly string[] Flower = {
            "    .-.    ",
            " .-(   )-. ",
            "(   .@.   )",
            " '-(   )-' ",
            "    '-'    ",
            "     |     ",
            "  \\  |  /  ",
            "   \\ | /   ",
            "    \\|/    ",
            "     |     "
        };
        static string FlowerGlyph(char c)
        {
            switch (c) {
                case '.': return "     " + "     " + "     " + "     " + "     " + " ##  " + " ##  ";
                case '-': return "     " + "     " + "     " + " ### " + "     " + "     " + "     ";
                case '(': return "   # " + "  #  " + " #   " + " #   " + " #   " + "  #  " + "   # ";
                case ')': return " #   " + "  #  " + "   # " + "   # " + "   # " + "  #  " + " #   ";
                case '@': return " ### " + "#   #" + "# ###" + "# # #" + "# ###" + "#    " + " ### ";
                case '|': return "  #  " + "  #  " + "  #  " + "  #  " + "  #  " + "  #  " + "  #  ";
                case '/': return "    #" + "   # " + "   # " + "  #  " + " #   " + " #   " + "#    ";
                case '\\': return "#    " + " #   " + " #   " + "  #  " + "   # " + "   # " + "    #";
                case '\'': return "  #  " + "  #  " + " #   " + "     " + "     " + "     " + "     ";
                default: return null;
            }
        }
        static byte[] RenderFlowers(byte red, byte green, byte blue)
        {
            var data = new byte[Width * Height * 4];
            for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++) {
                double dx = (x - Width * .82) / Width, dy = (y - Height * .78) / Height;
                double glow = .075 * Math.Exp(-(dx * dx + dy * dy) * 5);
                int p = (y * Width + x) * 4;
                data[p] = (byte)(9 + red * glow); data[p + 1] = (byte)(9 + green * glow);
                data[p + 2] = (byte)(11 + blue * glow); data[p + 3] = 255;
            }
            // A fixed bitmap is generated once on the backdrop worker, never per frame.
            int[,] blooms = { { 12, 32 }, { 16, 210 }, { 150, 272 }, { 366, 261 }, { 482, 224 }, { 565, 104 }, { 520, 12 } };
            for (int i = 0; i < blooms.GetLength(0); i++)
                for (int row = 0; row < Flower.Length; row++) for (int col = 0; col < Flower[row].Length; col++) {
                    string glyph = FlowerGlyph(Flower[row][col]); if (glyph == null) continue;
                    for (int gy = 0; gy < 7; gy++) for (int gx = 0; gx < 5; gx++) {
                        if (glyph[gy * 5 + gx] != '#') continue;
                        int x = blooms[i, 0] + col * 6 + gx, y = blooms[i, 1] + row * 9 + gy;
                        if (x >= Width || y >= Height) continue;
                        double ink = x < Width / 2 ? .105 : .19;
                        int p = (y * Width + x) * 4;
                        data[p] = (byte)Math.Min(255, data[p] + red * ink);
                        data[p + 1] = (byte)Math.Min(255, data[p + 1] + green * ink);
                        data[p + 2] = (byte)Math.Min(255, data[p + 2] + blue * ink);
                    }
                }
            return data;
        }
    }
}
