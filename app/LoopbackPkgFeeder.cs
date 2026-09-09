using System;
using System.IO;
using System.Threading;

namespace Orbis
{
    internal static class LoopbackPkgFeeder
    {
        public const int HeaderBytes = 0x438;
        const int ChunkBytes = 8 * 1024 * 1024;

        public static bool IsPkgHeader(byte[] data)
        {
            return data != null && data.Length >= HeaderBytes &&
                data[0] == 0x7F && data[1] == 0x43 && data[2] == 0x4E && data[3] == 0x54;
        }

        public static long PreparePartial(string url, string effectiveUrl, string finalPath,
            byte[] header, long total, string titleId, string packageId = null)
        {
            if (!IsPkgHeader(header) || total < HeaderBytes)
                throw new InvalidDataException("Invalid PKG feeder header");

            string directory = Path.GetDirectoryName(finalPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
            string part = finalPath + ".part";
            long existing = File.Exists(part) ? new FileInfo(part).Length : 0;
            DownloadResumeInfo resume = existing > 0 ? DownloadResumeInfo.Load(part) : null;
            bool keep = existing > 0 && existing <= total && resume != null &&
                resume.Total == total && resume.CanResume(url, titleId, existing, packageId) &&
                PrefixMatches(part, header, (int)Math.Min(existing, header.Length));
            if (!keep && existing > 0)
            {
                DownloadResumeInfo.DeletePartial(part);
                existing = 0;
            }

            using (var output = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, 256 * 1024))
            {
                if (existing < header.Length)
                {
                    output.Position = existing;
                    output.Write(header, (int)existing, header.Length - (int)existing);
                    existing = header.Length;
                }
                output.SetLength(existing);
                output.Flush();
            }
            DownloadResumeInfo.Create(url, effectiveUrl, "", "", total, titleId, packageId).Save(part);
            return existing;
        }

        public static long Feed(string url, string finalPath, long offset, long total,
            Action<long, long> progress, Func<bool> stop, Func<bool> paused, int timeoutMs)
        {
            if (offset < HeaderBytes || offset > total) throw new ArgumentOutOfRangeException("offset");
            string part = finalPath + ".part";
            using (var output = new FileStream(part, FileMode.Open, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, 256 * 1024))
            {
                if (output.Length != offset)
                    throw new IOException("PKG feeder partial length changed");
                output.Position = offset;
                while (offset < total)
                {
                    if (stop != null && stop()) throw new OperationCanceledException();
                    while (paused != null && paused())
                    {
                        if (stop != null && stop()) throw new OperationCanceledException();
                        Thread.Sleep(100);
                    }

                    int count = (int)Math.Min(ChunkBytes, total - offset);
                    HttpRangeResult range = NetHttp.ReadRangeDirect(url, offset, count, timeoutMs);
                    if (range == null || range.Data == null || range.Data.Length != count)
                        throw new IOException("PKG feeder returned a short range");
                    if (range.Total > 0 && range.Total != total)
                        throw new IOException(DownloadResumeInfo.RestartRequired(
                            "source length changed during BGFT feed"));
                    output.Write(range.Data, 0, range.Data.Length);
                    output.Flush();
                    offset += range.Data.Length;
                    if (progress != null) progress(offset, total);
                }
            }
            return offset;
        }

        static bool PrefixMatches(string path, byte[] expected, int count)
        {
            if (count <= 0) return true;
            try
            {
                byte[] actual = new byte[count];
                using (var input = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                {
                    int read = 0;
                    while (read < count)
                    {
                        int n = input.Read(actual, read, count - read);
                        if (n <= 0) return false;
                        read += n;
                    }
                }
                for (int i = 0; i < count; i++)
                    if (actual[i] != expected[i]) return false;
                return true;
            }
            catch { return false; }
        }
    }
}
