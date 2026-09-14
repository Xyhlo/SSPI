using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Orbis
{
    // The console's bundled Mono crashes inside the managed progressive JPEG
    // path. Decode compressed JPEG bytes in the application bootstrap instead.
    internal static unsafe class NativeJpegDecoder
    {
        internal static byte[] Decode(byte[] encoded, out int width, out int height)
        {
            if (encoded == null || encoded.Length < 3 || encoded.Length > CoverImageDecoder.MaximumEncodedBytes)
                throw new InvalidDataException("JPEG input exceeds limits");
            int w = 0, h = 0;
            fixed (byte* input = encoded)
            {
                int result = GetInfo(input, encoded.Length, &w, &h);
                if (result != 0) throw new InvalidDataException("JPEG header rejected (" + result + ")");
                if (w < 16 || h < 16 || w > 4096 || h > 4096 || (long)w * h > CoverImageDecoder.MaximumInputPixels)
                    throw new InvalidDataException("JPEG dimensions exceed limits");
                byte[] rgba = new byte[checked(w * h * 4)];
                fixed (byte* output = rgba)
                    result = DecodePixels(input, encoded.Length, output, rgba.Length, w, h);
                if (result != 0) throw new InvalidDataException("JPEG decode rejected (" + result + ")");
                width = w;
                height = h;
                return rgba;
            }
        }

        // Host regression builds link these same functions from a test DLL.
        // The shipped app binds them directly to its own native bootstrap.
#if COVER_JPEG_HOST_TEST
        [DllImport("sspi-cover-jpeg", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sspi_cover_jpeg_info")]
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
#endif
        static extern int GetInfo(byte* encoded, int length, int* width, int* height);

#if COVER_JPEG_HOST_TEST
        [DllImport("sspi-cover-jpeg", CallingConvention = CallingConvention.Cdecl, EntryPoint = "sspi_cover_jpeg_decode")]
#else
        [MethodImpl(MethodImplOptions.InternalCall)]
#endif
        static extern int DecodePixels(byte* encoded, int length, byte* rgba, int rgbaLength, int width, int height);
    }
}
