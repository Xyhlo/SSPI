using System;
using System.Collections.Generic;
using System.Text;
using static SDL2.SDL;

namespace Orbis
{
    /// <summary>
    /// Complete QR Code Model 2 encoder (byte mode, ECC M).
    /// Port of Nayuki's public-domain algorithm — scannable by phones.
    /// </summary>
    internal static class QrCode
    {
        // ECC level M total data capacity (bytes) by version 1..10
        static readonly int[] DataCapacityM =
        {
            0, 16, 28, 44, 64, 86, 108, 124, 154, 182, 216
        };

        // ECC codewords per block, blocks count (group1), data codewords per block g1,
        // then optional g2 blocks / data cw per g2 block — for versions 1-10 ECC M
        // Format: eccPerBlock, numBlocks, dataPerBlock  (single group for v1-10 M is enough for short URLs)
        static readonly int[] EccPerBlockM =
        {
            0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26
        };
        static readonly int[] NumBlocksM =
        {
            0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5
        };

        public static bool[,] Encode(string text, out int size)
        {
            byte[] data = Encoding.UTF8.GetBytes(text ?? "");
            int version = 1;
            while (version <= 10)
            {
                int cap = DataCapacityM[version];
                int header = 4 + (version <= 9 ? 8 : 16);
                int need = (header + data.Length * 8 + 7) / 8;
                if (need <= cap) break;
                version++;
            }
            if (version > 10)
                throw new Exception("QR data too long");

            size = 17 + 4 * version;
            var modules = new bool[size, size];
            var isFunc = new bool[size, size];

            DrawFinders(modules, isFunc, size);
            DrawAlignment(modules, isFunc, version);
            DrawTiming(modules, isFunc, size);
            DrawDarkModule(modules, isFunc, version);
            // Format + version reserved (cleared later when writing format)
            ReserveFormat(isFunc, size);
            if (version >= 7) ReserveVersion(isFunc, size);

            byte[] dataCodewords = MakeDataCodewords(data, version);
            byte[] allCodewords = AddEccAndInterleave(dataCodewords, version);
            DrawCodewords(modules, isFunc, allCodewords, size);

            int mask = ChooseMask(modules, isFunc, size);
            ApplyMask(modules, isFunc, size, mask);
            DrawFormat(modules, isFunc, size, mask);
            if (version >= 7) DrawVersion(modules, version, size);

            return modules;
        }

        public static void Draw(IntPtr renderer, bool[,] grid, int size, int originX, int originY, int modulePx,
            byte r = 0, byte g = 0, byte b = 0)
        {
            int qz = 4 * modulePx;
            SDL_SetRenderDrawColor(renderer, 255, 255, 255, 255);
            var bg = new SDL_Rect
            {
                x = originX - qz,
                y = originY - qz,
                w = size * modulePx + qz * 2,
                h = size * modulePx + qz * 2
            };
            SDL_RenderFillRect(renderer, ref bg);

            SDL_SetRenderDrawColor(renderer, r, g, b, 255);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (!grid[y, x]) continue;
                    var px = new SDL_Rect
                    {
                        x = originX + x * modulePx,
                        y = originY + y * modulePx,
                        w = modulePx,
                        h = modulePx
                    };
                    SDL_RenderFillRect(renderer, ref px);
                }
            }
        }

        #region Data + ECC

        static byte[] MakeDataCodewords(byte[] data, int version)
        {
            int capacity = DataCapacityM[version];
            var bits = new List<bool>(capacity * 8);
            // mode = byte (0100)
            AppendBits(bits, 0x4, 4);
            AppendBits(bits, data.Length, version <= 9 ? 8 : 16);
            foreach (byte b in data)
                AppendBits(bits, b, 8);
            int capacityBits = capacity * 8;
            int term = Math.Min(4, capacityBits - bits.Count);
            AppendBits(bits, 0, term);
            while (bits.Count % 8 != 0)
                bits.Add(false);
            byte[] pad = { 0xEC, 0x11 };
            int pi = 0;
            while (bits.Count / 8 < capacity)
            {
                AppendBits(bits, pad[pi], 8);
                pi ^= 1;
            }
            var cw = new byte[capacity];
            for (int i = 0; i < capacity; i++)
            {
                int v = 0;
                for (int j = 0; j < 8; j++)
                    v = (v << 1) | (bits[i * 8 + j] ? 1 : 0);
                cw[i] = (byte)v;
            }
            return cw;
        }

        static byte[] AddEccAndInterleave(byte[] data, int version)
        {
            int eccLen = EccPerBlockM[version];
            int nBlocks = NumBlocksM[version];
            int totalData = DataCapacityM[version];
            int shortBlockData = totalData / nBlocks;
            int nLongBlocks = totalData % nBlocks;
            // For ECC M versions 1-10, all blocks same length except when remainder
            // Actually QR: first blocks are short, last nLong are shortBlockData+1
            int shortDataLen = shortBlockData;
            // Wait: totalData = short*(nBlocks-nLong) + (short+1)*nLong
            // short = totalData / nBlocks; nLong = totalData % nBlocks;
            // blocks 0..nBlocks-nLong-1 have shortDataLen, rest have shortDataLen+1

            var rs = new ReedSolomon(eccLen);
            var dataBlocks = new byte[nBlocks][];
            var eccBlocks = new byte[nBlocks][];
            int offset = 0;
            int nShort = nBlocks - nLongBlocks;
            for (int i = 0; i < nBlocks; i++)
            {
                int dl = i < nShort ? shortDataLen : shortDataLen + 1;
                dataBlocks[i] = new byte[dl];
                Array.Copy(data, offset, dataBlocks[i], 0, dl);
                offset += dl;
                eccBlocks[i] = rs.Encode(dataBlocks[i]);
            }

            int maxData = shortDataLen + (nLongBlocks > 0 ? 1 : 0);
            var result = new List<byte>(totalData + nBlocks * eccLen);
            for (int i = 0; i < maxData; i++)
                for (int b = 0; b < nBlocks; b++)
                    if (i < dataBlocks[b].Length)
                        result.Add(dataBlocks[b][i]);
            for (int i = 0; i < eccLen; i++)
                for (int b = 0; b < nBlocks; b++)
                    result.Add(eccBlocks[b][i]);
            return result.ToArray();
        }

        sealed class ReedSolomon
        {
            readonly int _degree;
            readonly byte[] _gen; // length == degree (coefficients after leading 1)

            public ReedSolomon(int degree)
            {
                // Nayuki: divisor[0] = coeff of x^{degree-1}, ... divisor[degree-1] = const
                _degree = degree;
                var result = new int[degree];
                result[degree - 1] = 1;
                int root = 1;
                for (int i = 0; i < degree; i++)
                {
                    for (int j = 0; j < degree; j++)
                    {
                        result[j] = GfMul(result[j], root);
                        if (j + 1 < degree)
                            result[j] ^= result[j + 1];
                    }
                    root = GfMul(root, 2);
                }
                _gen = new byte[degree];
                for (int i = 0; i < degree; i++)
                    _gen[i] = (byte)result[i];
            }

            public byte[] Encode(byte[] data)
            {
                var res = new byte[_degree];
                foreach (byte b in data)
                {
                    int factor = (b ^ res[0]) & 0xFF;
                    Array.Copy(res, 1, res, 0, _degree - 1);
                    res[_degree - 1] = 0;
                    if (factor != 0)
                    {
                        for (int i = 0; i < _degree; i++)
                            res[i] ^= (byte)GfMul(_gen[i] & 0xFF, factor);
                    }
                }
                return res;
            }

            static int GfMul(int x, int y)
            {
                if (x == 0 || y == 0) return 0;
                return GfExp[GfLog[x] + GfLog[y]];
            }

            static int GfPow(int x, int p)
            {
                int r = 1;
                for (int i = 0; i < p; i++) r = GfMul(r, x);
                return r;
            }

            static readonly int[] GfExp = new int[512];
            static readonly int[] GfLog = new int[256];

            static ReedSolomon()
            {
                int x = 1;
                for (int i = 0; i < 255; i++)
                {
                    GfExp[i] = x;
                    GfLog[x] = i;
                    x <<= 1;
                    if (x >= 256) x ^= 0x11D;
                }
                for (int i = 255; i < 512; i++)
                    GfExp[i] = GfExp[i - 255];
            }
        }

        #endregion

        #region Draw modules

        static void DrawFinders(bool[,] m, bool[,] f, int size)
        {
            DrawFinder(m, f, 0, 0);
            DrawFinder(m, f, size - 7, 0);
            DrawFinder(m, f, 0, size - 7);
        }

        static void DrawFinder(bool[,] m, bool[,] f, int ox, int oy)
        {
            for (int dy = -1; dy <= 7; dy++)
            {
                for (int dx = -1; dx <= 7; dx++)
                {
                    int x = ox + dx, y = oy + dy;
                    if (x < 0 || y < 0 || x >= m.GetLength(0) || y >= m.GetLength(0)) continue;
                    bool dark = (dx >= 0 && dx <= 6 && dy >= 0 && dy <= 6) &&
                                (dx == 0 || dx == 6 || dy == 0 || dy == 6 ||
                                 (dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4));
                    m[y, x] = dark;
                    f[y, x] = true;
                }
            }
        }

        static void DrawAlignment(bool[,] m, bool[,] f, int version)
        {
            if (version == 1) return;
            int[] pos = AlignmentPositions(version);
            foreach (int y in pos)
            {
                foreach (int x in pos)
                {
                    // skip if overlaps finder
                    if (f[y, x]) continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            bool dark = Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1;
                            m[yy, xx] = dark;
                            f[yy, xx] = true;
                        }
                }
            }
        }

        static int[] AlignmentPositions(int version)
        {
            // Standard positions for versions 1-10
            if (version == 1) return new int[0];
            int size = 17 + 4 * version;
            int numAlign = version / 7 + 2;
            int step;
            if (version == 32) step = 26;
            else step = (version * 4 + numAlign * 2 + 1) / (2 * numAlign - 2) * 2;
            var pos = new int[numAlign];
            pos[0] = 6;
            for (int i = numAlign - 1; i >= 1; i--)
                pos[i] = size - 7 - (numAlign - 1 - i) * step;
            return pos;
        }

        static void DrawTiming(bool[,] m, bool[,] f, int size)
        {
            for (int i = 0; i < size; i++)
            {
                if (!f[6, i]) { m[6, i] = i % 2 == 0; f[6, i] = true; }
                if (!f[i, 6]) { m[i, 6] = i % 2 == 0; f[i, 6] = true; }
            }
        }

        static void DrawDarkModule(bool[,] m, bool[,] f, int version)
        {
            int size = 17 + 4 * version;
            m[size - 8, 8] = true;
            f[size - 8, 8] = true;
        }

        static void ReserveFormat(bool[,] f, int size)
        {
            for (int i = 0; i < 9; i++)
            {
                f[8, i] = true;
                f[i, 8] = true;
            }
            for (int i = size - 8; i < size; i++)
            {
                f[8, i] = true;
                f[i, 8] = true;
            }
            f[8, 8] = true;
        }

        static void ReserveVersion(bool[,] f, int size)
        {
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 3; j++)
                {
                    f[i, size - 11 + j] = true;
                    f[size - 11 + j, i] = true;
                }
        }

        static void DrawCodewords(bool[,] m, bool[,] f, byte[] data, int size)
        {
            int i = 0;
            int bitLen = data.Length * 8;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? size - 1 - vert : vert;
                        if (!f[y, x] && i < bitLen)
                        {
                            m[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                            i++;
                        }
                    }
                }
            }
        }

        static void ApplyMask(bool[,] m, bool[,] f, int size, int mask)
        {
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (!f[y, x] && MaskBit(mask, x, y))
                        m[y, x] = !m[y, x];
        }

        static bool MaskBit(int mask, int x, int y)
        {
            switch (mask)
            {
                case 0: return (x + y) % 2 == 0;
                case 1: return y % 2 == 0;
                case 2: return x % 3 == 0;
                case 3: return (x + y) % 3 == 0;
                case 4: return (y / 2 + x / 3) % 2 == 0;
                case 5: return (x * y) % 2 + (x * y) % 3 == 0;
                case 6: return ((x * y) % 2 + (x * y) % 3) % 2 == 0;
                case 7: return ((x + y) % 2 + (x * y) % 3) % 2 == 0;
                default: return false;
            }
        }

        static int ChooseMask(bool[,] m, bool[,] f, int size)
        {
            int best = 0;
            long bestScore = long.MaxValue;
            // work on copy
            var tmp = new bool[size, size];
            for (int mask = 0; mask < 8; mask++)
            {
                for (int y = 0; y < size; y++)
                    for (int x = 0; x < size; x++)
                        tmp[y, x] = m[y, x];
                ApplyMask(tmp, f, size, mask);
                DrawFormat(tmp, f, size, mask);
                long score = Penalty(tmp, size);
                if (score < bestScore) { bestScore = score; best = mask; }
            }
            return best;
        }

        static long Penalty(bool[,] m, int size)
        {
            long p = 0;
            // N1: runs
            for (int y = 0; y < size; y++)
            {
                int run = 1;
                for (int x = 1; x < size; x++)
                {
                    if (m[y, x] == m[y, x - 1]) { run++; if (run == 5) p += 3; else if (run > 5) p++; }
                    else run = 1;
                }
            }
            for (int x = 0; x < size; x++)
            {
                int run = 1;
                for (int y = 1; y < size; y++)
                {
                    if (m[y, x] == m[y - 1, x]) { run++; if (run == 5) p += 3; else if (run > 5) p++; }
                    else run = 1;
                }
            }
            // N2: 2x2
            for (int y = 0; y < size - 1; y++)
                for (int x = 0; x < size - 1; x++)
                    if (m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1])
                        p += 3;
            // N3: finder-like
            int[] pat = { 1, 0, 1, 1, 1, 0, 1 };
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x <= size - 7; x++)
                {
                    bool ok = true;
                    for (int k = 0; k < 7; k++)
                        if ((m[y, x + k] ? 1 : 0) != pat[k]) { ok = false; break; }
                    if (ok) p += 40;
                }
            }
            for (int x = 0; x < size; x++)
            {
                for (int y = 0; y <= size - 7; y++)
                {
                    bool ok = true;
                    for (int k = 0; k < 7; k++)
                        if ((m[y + k, x] ? 1 : 0) != pat[k]) { ok = false; break; }
                    if (ok) p += 40;
                }
            }
            // N4: balance
            int dark = 0;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (m[y, x]) dark++;
            int total = size * size;
            int k5 = Math.Abs(dark * 20 - total * 10) / total; // |percent-50|/5
            p += k5 * 10;
            return p;
        }

        static void DrawFormat(bool[,] m, bool[,] f, int size, int mask)
        {
            // ECC M = 00, mask 3 bits
            int data = (0x0 << 3) | mask; // M = 00
            int rem = data;
            for (int i = 0; i < 10; i++)
                rem = (rem << 1) ^ (((rem >> 9) & 1) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;

            void set(int x, int y, bool dark)
            {
                m[y, x] = dark;
            }

            // horizontal
            for (int i = 0; i <= 5; i++) set(i, 8, ((bits >> i) & 1) != 0);
            set(7, 8, ((bits >> 6) & 1) != 0);
            set(8, 8, ((bits >> 7) & 1) != 0);
            set(8, 7, ((bits >> 8) & 1) != 0);
            for (int i = 9; i <= 14; i++) set(8, 14 - i, ((bits >> i) & 1) != 0);

            // vertical copy
            for (int i = 0; i <= 7; i++) set(size - 1 - i, 8, ((bits >> i) & 1) != 0);
            for (int i = 8; i <= 14; i++) set(8, size - 15 + i, ((bits >> i) & 1) != 0);
        }

        static void DrawVersion(bool[,] m, int version, int size)
        {
            int rem = version;
            for (int i = 0; i < 12; i++)
                rem = (rem << 1) ^ (((rem >> 11) & 1) * 0x1F25);
            int bits = (version << 12) | rem;
            for (int i = 0; i < 18; i++)
            {
                bool dark = ((bits >> i) & 1) != 0;
                int a = size - 11 + i % 3;
                int b = i / 3;
                m[b, a] = dark;
                m[a, b] = dark;
            }
        }

        static void AppendBits(List<bool> bits, int val, int len)
        {
            for (int i = len - 1; i >= 0; i--)
                bits.Add(((val >> i) & 1) != 0);
        }

        #endregion
    }
}
