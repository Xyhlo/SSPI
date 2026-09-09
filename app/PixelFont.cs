using System;
using SDL2.Object;
using static SDL2.SDL;

namespace Orbis
{
    /// <summary>
    /// Crisp 5x7 bitmap font (from OrbisShelf). Drawn with FillRect — no FreeType blur.
    /// </summary>
    internal static class PixelFont
    {
        // Each glyph: 7 rows, bits 4..0 = columns left→right
        private static readonly byte[] Blank = { 0, 0, 0, 0, 0, 0, 0 };
        private static readonly byte[] Unknown = { 14, 17, 1, 2, 4, 0, 4 };
        private static readonly byte[] A = { 14, 17, 17, 31, 17, 17, 17 };
        private static readonly byte[] B = { 30, 17, 17, 30, 17, 17, 30 };
        private static readonly byte[] C = { 14, 17, 16, 16, 16, 17, 14 };
        private static readonly byte[] D = { 30, 17, 17, 17, 17, 17, 30 };
        private static readonly byte[] E = { 31, 16, 16, 30, 16, 16, 31 };
        private static readonly byte[] F = { 31, 16, 16, 30, 16, 16, 16 };
        private static readonly byte[] G = { 14, 17, 16, 23, 17, 17, 15 };
        private static readonly byte[] H = { 17, 17, 17, 31, 17, 17, 17 };
        private static readonly byte[] I = { 14, 4, 4, 4, 4, 4, 14 };
        private static readonly byte[] J = { 7, 2, 2, 2, 18, 18, 12 };
        private static readonly byte[] K = { 17, 18, 20, 24, 20, 18, 17 };
        private static readonly byte[] L = { 16, 16, 16, 16, 16, 16, 31 };
        private static readonly byte[] M = { 17, 27, 21, 21, 17, 17, 17 };
        private static readonly byte[] N = { 17, 25, 21, 19, 17, 17, 17 };
        private static readonly byte[] O = { 14, 17, 17, 17, 17, 17, 14 };
        private static readonly byte[] P = { 30, 17, 17, 30, 16, 16, 16 };
        private static readonly byte[] Q = { 14, 17, 17, 17, 21, 18, 13 };
        private static readonly byte[] R = { 30, 17, 17, 30, 20, 18, 17 };
        private static readonly byte[] S = { 15, 16, 16, 14, 1, 1, 30 };
        private static readonly byte[] T = { 31, 4, 4, 4, 4, 4, 4 };
        private static readonly byte[] U = { 17, 17, 17, 17, 17, 17, 14 };
        private static readonly byte[] V = { 17, 17, 17, 17, 17, 10, 4 };
        private static readonly byte[] W = { 17, 17, 17, 21, 21, 21, 10 };
        private static readonly byte[] X = { 17, 17, 10, 4, 10, 17, 17 };
        private static readonly byte[] Y = { 17, 17, 10, 4, 4, 4, 4 };
        private static readonly byte[] Z = { 31, 1, 2, 4, 8, 16, 31 };
        private static readonly byte[] N0 = { 14, 17, 19, 21, 25, 17, 14 };
        private static readonly byte[] N1 = { 4, 12, 4, 4, 4, 4, 14 };
        private static readonly byte[] N2 = { 14, 17, 1, 2, 4, 8, 31 };
        private static readonly byte[] N3 = { 30, 1, 1, 14, 1, 1, 30 };
        private static readonly byte[] N4 = { 2, 6, 10, 18, 31, 2, 2 };
        private static readonly byte[] N5 = { 31, 16, 16, 30, 1, 1, 30 };
        private static readonly byte[] N6 = { 14, 16, 16, 30, 17, 17, 14 };
        private static readonly byte[] N7 = { 31, 1, 2, 4, 8, 8, 8 };
        private static readonly byte[] N8 = { 14, 17, 17, 14, 17, 17, 14 };
        private static readonly byte[] N9 = { 14, 17, 17, 15, 1, 1, 14 };
        private static readonly byte[] Dash = { 0, 0, 0, 31, 0, 0, 0 };
        private static readonly byte[] Dot = { 0, 0, 0, 0, 0, 12, 12 };
        private static readonly byte[] Colon = { 0, 12, 12, 0, 12, 12, 0 };
        private static readonly byte[] Slash = { 1, 2, 2, 4, 8, 8, 16 };
        private static readonly byte[] Under = { 0, 0, 0, 0, 0, 0, 31 };
        private static readonly byte[] Lp = { 2, 4, 8, 8, 8, 4, 2 };
        private static readonly byte[] Rp = { 8, 4, 2, 2, 2, 4, 8 };
        private static readonly byte[] Lb = { 14, 8, 8, 8, 8, 8, 14 };
        private static readonly byte[] Rb = { 14, 2, 2, 2, 2, 2, 14 };
        private static readonly byte[] Plus = { 0, 4, 4, 31, 4, 4, 0 };
        private static readonly byte[] Percent = { 17, 2, 4, 8, 17, 0, 0 };
        private static readonly byte[] Excl = { 4, 4, 4, 4, 4, 0, 4 };
        private static readonly byte[] Gt = { 8, 4, 2, 1, 2, 4, 8 };
        private static readonly byte[] Lt = { 2, 4, 8, 16, 8, 4, 2 };
        private static readonly byte[] Quote = { 10, 10, 0, 0, 0, 0, 0 };
        private static readonly byte[] Comma = { 0, 0, 0, 0, 12, 4, 8 };
        private static readonly byte[] Quest = { 14, 17, 1, 2, 4, 0, 4 };

        private static byte[] Glyph(char input)
        {
            char c = char.ToUpperInvariant(input);
            switch (c)
            {
                case ' ': return Blank;
                case 'A': return A; case 'B': return B; case 'C': return C; case 'D': return D;
                case 'E': return E; case 'F': return F; case 'G': return G; case 'H': return H;
                case 'I': return I; case 'J': return J; case 'K': return K; case 'L': return L;
                case 'M': return M; case 'N': return N; case 'O': return O; case 'P': return P;
                case 'Q': return Q; case 'R': return R; case 'S': return S; case 'T': return T;
                case 'U': return U; case 'V': return V; case 'W': return W; case 'X': return X;
                case 'Y': return Y; case 'Z': return Z;
                case '0': return N0; case '1': return N1; case '2': return N2; case '3': return N3;
                case '4': return N4; case '5': return N5; case '6': return N6; case '7': return N7;
                case '8': return N8; case '9': return N9;
                case '-': return Dash; case '.': return Dot; case ':': return Colon; case '/': return Slash;
                case '_': return Under; case '(': return Lp; case ')': return Rp;
                case '[': return Lb; case ']': return Rb;
                case '+': return Plus; case '%': return Percent; case '!': return Excl;
                case '>': return Gt; case '<': return Lt; case '"': return Quote;
                case ',': return Comma; case '?': return Quest;
                case '{': return Lp; case '}': return Rp;
                default: return Unknown;
            }
        }

        public static int CharWidth(int scale) => 6 * scale;
        public static int CharHeight(int scale) => 7 * scale;

        public static int TextWidth(int scale, string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return text.Length * CharWidth(scale);
        }

        public static void Draw(IntPtr renderer, int x, int y, int scale, string text, byte r, byte g, byte b)
        {
            if (renderer == IntPtr.Zero || string.IsNullOrEmpty(text) || scale < 1)
                return;

            SDL_SetRenderDrawColor(renderer, r, g, b, 255);
            int cursor = x;
            for (int i = 0; i < text.Length; i++)
            {
                byte[] rows = Glyph(text[i]);
                for (int row = 0; row < 7; row++)
                {
                    for (int col = 0; col < 5; col++)
                    {
                        if ((rows[row] & (1 << (4 - col))) != 0)
                        {
                            var pixel = new SDL_Rect
                            {
                                x = cursor + col * scale,
                                y = y + row * scale,
                                w = scale,
                                h = scale
                            };
                            SDL_RenderFillRect(renderer, ref pixel);
                        }
                    }
                }
                cursor += CharWidth(scale);
            }
        }

        public static void Draw(Renderer renderer, int x, int y, int scale, string text, byte r, byte g, byte b)
        {
            if (renderer == null) return;
            Draw(renderer.Handler, x, y, scale, text, r, g, b);
        }
    }
}
