using System;
using System.Collections.Generic;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace Orbis
{
    internal sealed class CoverImageData
    {
        public byte[] Pixels;
        public int Width, Height;
        public bool Opaque;
    }

    internal static class CoverImageDecoder
    {
        internal const int MaximumEncodedBytes = 8 * 1024 * 1024;
        internal const int MaximumInputPixels = 4 * 1024 * 1024;
        const int MaximumInputDimension = 4096;
        const int MaximumTextureDimension = 512;
        const int MaximumChunks = 1024;
        const int MaximumFrames = 16;

        internal static Configuration CreateConfiguration()
        {
            Configuration config = Configuration.Default.Clone();
            config.MaxDegreeOfParallelism = 1;
            config.MemoryAllocator = new SimpleGcMemoryAllocator();
            return config;
        }

        internal static CoverImageData Decode(string path, int requestedWidth, int requestedHeight,
            bool backdrop, Action<string> stage = null, bool caseArtwork = false)
        {
            if (requestedWidth < 0 || requestedHeight < 0 ||
                (requestedWidth == 0) != (requestedHeight == 0) ||
                requestedWidth > MaximumTextureDimension || requestedHeight > MaximumTextureDimension)
                throw Bad("Invalid requested cover dimensions");
            Notify(stage, "read");
            byte[] encoded = ReadEncoded(path);
            if (FourCC(encoded, 0, "RIFF"))
            {
                Notify(stage, "normalize");
                encoded = NormalizeWebP(encoded);
            }
            else if (!IsJpeg(encoded) && !IsPng(encoded))
                throw Bad("Unsupported cover image format");

            Configuration config = CreateConfiguration();
            using (var stream = new MemoryStream(encoded, false))
            {
                int inputWidth, inputHeight;
                using (Image<Rgba32> image = LoadImage(config, stream, encoded, stage, out inputWidth, out inputHeight))
                {
                    ValidateDimensions(image.Width, image.Height);
                    if (image.Width != inputWidth || image.Height != inputHeight ||
                        image.Frames.Count > MaximumFrames ||
                        (long)image.Width * image.Height * image.Frames.Count > MaximumInputPixels)
                        throw Bad("Decoded image exceeds identified bounds");
                    Notify(stage, "resize");
                    if (caseArtwork && !backdrop)
                    {
                        Rectangle artwork = CaseArtworkRegion(image);
                        if (artwork.Width == image.Width && artwork.Height == image.Height)
                        {
                            // Give unframed posters a small full-bleed inset inside
                            // the physical case without changing the case geometry.
                            int insetX = image.Width * 2 / 100, insetY = image.Height * 2 / 100;
                            artwork = new Rectangle(insetX, insetY, image.Width - insetX * 2, image.Height - insetY * 2);
                        }
                        if (artwork.Width != image.Width || artwork.Height != image.Height)
                            image.Mutate(x => x.Crop(artwork));
                    }
                    if (backdrop)
                    {
                        image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(1440, 320),
                            Mode = ResizeMode.Crop, Sampler = KnownResamplers.Lanczos3 }));
                        for (int yy = 0; yy < image.Height; yy++)
                            for (int xx = 0; xx < image.Width; xx++)
                            {
                                Rgba32 pixel = image[xx, yy];
                                double shade = .07 + .30 * xx / (double)image.Width;
                                image[xx, yy] = new Rgba32((byte)(24 + pixel.R * shade),
                                    (byte)(24 + pixel.G * shade), (byte)(24 + pixel.B * shade), 255);
                            }
                    }
                    else if (requestedWidth > 0)
                        image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(requestedWidth, requestedHeight),
                            Mode = ResizeMode.Crop, Sampler = KnownResamplers.Lanczos3 }));
                    else if (image.Width > MaximumTextureDimension || image.Height > MaximumTextureDimension)
                    {
                        double scale = Math.Min((double)MaximumTextureDimension / image.Width,
                            (double)MaximumTextureDimension / image.Height);
                        int width = Math.Max(1, (int)Math.Round(image.Width * scale));
                        int height = Math.Max(1, (int)Math.Round(image.Height * scale));
                        image.Mutate(x => x.Resize(width, height));
                    }
                    long pixels = (long)image.Width * image.Height;
                    if (pixels <= 0 || pixels > (backdrop ? 1440L * 320 : 512L * 512))
                        throw Bad("Cover output exceeds limits");
                    Notify(stage, "copy");
                    byte[] rgba = new byte[checked((int)pixels * 4)];
                    image.CopyPixelDataTo(rgba);
                    bool opaque = true;
                    for (int alpha = 3; alpha < rgba.Length; alpha += 4)
                        if (rgba[alpha] != 255) { opaque = false; break; }
                    return new CoverImageData { Pixels = rgba, Width = image.Width, Height = image.Height, Opaque = opaque };
                }
            }
        }

        // Normalize a clearly baked-in blue retail header before adding our case.
        // Run once on the decoder thread, not while rendering. Blue artwork alone
        // is insufficient: require a shallow blue band, white logo and a boundary.
        internal static Rectangle CaseArtworkRegion(Image<Rgba32> image)
        {
            int w = image.Width, h = image.Height;
            var full = new Rectangle(0, 0, w, h);
            if (w < 60 || h < 80 || w / (double)h < .60 || w / (double)h > 1.10) return full;
            int first = -1, last = -1, blueRows = 0, white = 0, samples = 0;
            for (int y = Math.Max(1, h / 100); y < h * 28 / 100; y++)
            {
                int blue = 0;
                for (int i = 0; i < 40; i++)
                {
                    Rgba32 p = image[w / 10 + i * (w * 8 / 10 - 1) / 39, y];
                    if (p.A > 220 && p.B > 90 && p.B > p.R * 1.5 && p.G > p.R * 1.2) blue++;
                    if (i < 18) { samples++; if (p.R > 210 && p.G > 210 && p.B > 210) white++; }
                }
                if (blue >= 28) { if (first < 0) first = y; last = y; blueRows++; }
                else if (blueRows >= h * 4 / 100 && y > last + h / 100) break;
            }
            if (first < 0 || first > h * 10 / 100 || blueRows < h * 4 / 100 ||
                last >= h * 27 / 100 || white < Math.Max(2, samples / 100)) return full;
            int belowBlue = 0;
            for (int i = 0; i < 40; i++)
            {
                Rgba32 p = image[w / 10 + i * (w * 8 / 10 - 1) / 39, h * 2 / 5];
                if (p.B > 90 && p.B > p.R * 1.5 && p.G > p.R * 1.2) belowBlue++;
            }
            if (belowBlue >= 28) return full;
            int left = Math.Max(1, w * 3 / 100), top = last + 1;
            return new Rectangle(left, top, w - left - Math.Max(1, w * 5 / 100), h - top - Math.Max(1, h * 4 / 100));
        }

        static Image<Rgba32> LoadImage(Configuration config, MemoryStream stream, byte[] encoded,
            Action<string> stage, out int width, out int height)
        {
            if (IsJpeg(encoded))
            {
                Notify(stage, "native-jpeg begin");
                byte[] rgba = NativeJpegDecoder.Decode(encoded, out width, out height);
                Notify(stage, "native-jpeg complete");
                return Image.LoadPixelData<Rgba32>(config, rgba, width, height);
            }
            Notify(stage, "identify");
            IImageInfo info = Image.Identify(config, stream);
            if (info == null) throw Bad("Image dimensions unavailable");
            width = info.Width; height = info.Height;
            ValidateDimensions(width, height);
            stream.Position = 0;
            Notify(stage, "decode");
            return Image.Load<Rgba32>(config, stream);
        }

        static byte[] ReadEncoded(string path)
        {
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (file.Length < 12 || file.Length > MaximumEncodedBytes) throw Bad("Encoded cover exceeds limits");
                byte[] bytes = new byte[(int)file.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int read = file.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw Bad("Truncated cover file");
                    offset += read;
                }
                if (file.ReadByte() != -1) throw Bad("Cover file changed while reading");
                return bytes;
            }
        }

        // WebP readers must ignore unknown ancillary chunks. This bundled decoder
        // rejects them, so give it a bounded decode copy with known chunks intact.
        // Do not rewrite compressed pixels, alpha, animation, or color metadata.
        internal static byte[] NormalizeWebP(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 20 || bytes.Length > MaximumEncodedBytes ||
                !FourCC(bytes, 0, "RIFF") || !FourCC(bytes, 8, "WEBP") ||
                (ulong)U32(bytes, 4) + 8UL != (ulong)bytes.Length)
                throw Bad("Invalid WebP RIFF length");
            var chunks = new List<int>();
            int offset = 12, count = 0, frames = 0, images = 0;
            int canvasWidth = 0, canvasHeight = 0, imageWidth = 0, imageHeight = 0;
            int metadataFlags = 0, flags = 0;
            bool extended = false, animation = false, alpha = false, removed = false;
            long framePixels = 0;
            while (offset < bytes.Length)
            {
                if (++count > MaximumChunks) throw Bad("Too many WebP chunks");
                int end = ChunkEnd(bytes, offset, bytes.Length);
                int payload = offset + 8, size = (int)U32(bytes, offset + 4);
                bool known = true;
                if (FourCC(bytes, offset, "VP8X"))
                {
                    if (offset != 12 || extended || size != 10) throw Bad("Invalid WebP extended header");
                    extended = true; flags = bytes[payload];
                    if ((flags & 0xC1) != 0 || bytes[payload + 1] != 0 || bytes[payload + 2] != 0 || bytes[payload + 3] != 0)
                        throw Bad("Unsupported WebP feature bits");
                    canvasWidth = 1 + U24(bytes, payload + 4); canvasHeight = 1 + U24(bytes, payload + 7);
                    ValidateDimensions(canvasWidth, canvasHeight);
                }
                else if (FourCC(bytes, offset, "VP8 ") || FourCC(bytes, offset, "VP8L"))
                {
                    if (++images != 1 || animation || frames != 0) throw Bad("Invalid WebP image order");
                    ImageDimensions(bytes, offset, size, out imageWidth, out imageHeight);
                }
                else if (FourCC(bytes, offset, "ALPH"))
                {
                    if (!extended || alpha || images != 0 || animation || size < 1 || (flags & 0x10) == 0)
                        throw Bad("Invalid WebP alpha chunk");
                    alpha = true;
                }
                else if (FourCC(bytes, offset, "ANIM"))
                {
                    if (!extended || animation || images != 0 || alpha || size != 6 || (flags & 2) == 0)
                        throw Bad("Invalid WebP animation header");
                    animation = true;
                }
                else if (FourCC(bytes, offset, "ANMF"))
                {
                    if (!animation || ++frames > MaximumFrames || size < 16) throw Bad("Invalid WebP animation frame");
                    int x = U24(bytes, payload) * 2, y = U24(bytes, payload + 3) * 2;
                    int width = U24(bytes, payload + 6) + 1, height = U24(bytes, payload + 9) + 1;
                    if ((long)x + width > canvasWidth || (long)y + height > canvasHeight || (bytes[payload + 15] & 0xFC) != 0)
                        throw Bad("WebP frame exceeds canvas");
                    ValidateDimensions(width, height, 1);
                    framePixels += (long)canvasWidth * canvasHeight;
                    if (framePixels > MaximumInputPixels) throw Bad("WebP animation exceeds pixel budget");
                    ValidateFrameChunks(bytes, payload + 16, payload + size, width, height);
                }
                else if (FourCC(bytes, offset, "ICCP") || FourCC(bytes, offset, "EXIF") || FourCC(bytes, offset, "XMP "))
                {
                    int bit = FourCC(bytes, offset, "ICCP") ? 0x20 : FourCC(bytes, offset, "EXIF") ? 8 : 4;
                    if (!extended || (metadataFlags & bit) != 0 || (flags & bit) == 0 || size < 1)
                        throw Bad("Invalid WebP metadata flags");
                    if (bit == 0x20 && (images != 0 || animation)) throw Bad("WebP color profile follows image data");
                    metadataFlags |= bit;
                }
                else { known = false; removed = true; }
                if (known) chunks.Add(offset);
                offset = end;
            }
            if ((flags & 0x2C) != metadataFlags || (animation ? frames == 0 || images != 0 : images != 1 || (flags & 2) != 0))
                throw Bad("Incomplete WebP image structure");
            if (!animation && extended && (canvasWidth != imageWidth || canvasHeight != imageHeight))
                throw Bad("WebP canvas and image differ");
            if (!removed) return bytes;
            using (var clean = new MemoryStream(bytes.Length))
            {
                clean.Write(bytes, 0, 12);
                foreach (int start in chunks) clean.Write(bytes, start, ChunkEnd(bytes, start, bytes.Length) - start);
                byte[] result = clean.ToArray();
                WriteU32(result, 4, (uint)(result.Length - 8));
                return result;
            }
        }

        static void ValidateFrameChunks(byte[] bytes, int offset, int end, int width, int height)
        {
            bool alpha = false, image = false;
            int chunks = 0;
            while (offset < end)
            {
                if (++chunks > MaximumChunks) throw Bad("Too many WebP frame chunks");
                int next = ChunkEnd(bytes, offset, end), size = (int)U32(bytes, offset + 4);
                if (FourCC(bytes, offset, "ALPH"))
                { if (alpha || image || size < 1) throw Bad("Invalid frame alpha"); alpha = true; }
                else if (FourCC(bytes, offset, "VP8 ") || FourCC(bytes, offset, "VP8L"))
                {
                    int w, h;
                    if (image) throw Bad("Multiple WebP frame images");
                    ImageDimensions(bytes, offset, size, out w, out h);
                    if (w != width || h != height) throw Bad("Frame dimensions differ from image");
                    image = true;
                }
                else throw Bad("Unsupported WebP frame chunk");
                offset = next;
            }
            if (!image) throw Bad("Missing WebP frame image");
        }

        static void ImageDimensions(byte[] bytes, int offset, int size, out int width, out int height)
        {
            int p = offset + 8;
            if (FourCC(bytes, offset, "VP8 "))
            {
                if (size < 10 || (bytes[p] & 1) != 0 || bytes[p + 3] != 0x9D || bytes[p + 4] != 1 || bytes[p + 5] != 0x2A)
                    throw Bad("Invalid WebP lossy header");
                width = (bytes[p + 6] | bytes[p + 7] << 8) & 0x3FFF;
                height = (bytes[p + 8] | bytes[p + 9] << 8) & 0x3FFF;
            }
            else
            {
                if (size < 5 || bytes[p] != 0x2F || (bytes[p + 4] & 0xE0) != 0) throw Bad("Invalid WebP lossless header");
                uint packed = U32(bytes, p + 1);
                width = (int)(packed & 0x3FFF) + 1; height = (int)((packed >> 14) & 0x3FFF) + 1;
            }
            ValidateDimensions(width, height, 1);
        }

        static int ChunkEnd(byte[] bytes, int offset, int limit)
        {
            if (offset < 12 || limit - offset < 8) throw Bad("Truncated WebP chunk header");
            uint length = U32(bytes, offset + 4);
            long end = (long)offset + 8 + length + (length & 1);
            if (end > limit || ((length & 1) != 0 && bytes[(int)end - 1] != 0)) throw Bad("Invalid WebP chunk length or padding");
            return (int)end;
        }
        static void ValidateDimensions(int width, int height, int minimum = 16)
        {
            if (width < minimum || height < minimum || width > MaximumInputDimension || height > MaximumInputDimension ||
                (long)width * height > MaximumInputPixels) throw Bad("Cover dimensions exceed limits");
        }
        static bool IsJpeg(byte[] b) { return b.Length >= 3 && b[0] == 255 && b[1] == 216 && b[2] == 255; }
        static bool IsPng(byte[] b) { return b.Length >= 8 && b[0] == 137 && b[1] == 80 && b[2] == 78 && b[3] == 71 && b[4] == 13 && b[5] == 10 && b[6] == 26 && b[7] == 10; }
        static bool FourCC(byte[] b, int at, string value) { return at >= 0 && b.Length - at >= 4 && b[at] == value[0] && b[at + 1] == value[1] && b[at + 2] == value[2] && b[at + 3] == value[3]; }
        static int U24(byte[] b, int at) { return b[at] | b[at + 1] << 8 | b[at + 2] << 16; }
        static uint U32(byte[] b, int at) { return (uint)(b[at] | b[at + 1] << 8 | b[at + 2] << 16 | b[at + 3] << 24); }
        static void WriteU32(byte[] b, int at, uint value) { for (int i = 0; i < 4; i++) b[at + i] = (byte)(value >> (i * 8)); }
        static void Notify(Action<string> stage, string value) { if (stage != null) stage(value); }
        static InvalidDataException Bad(string message) { return new InvalidDataException(message); }
    }
}
