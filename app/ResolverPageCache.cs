using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
namespace Orbis
{
    internal static class ResolverPageCache
    {
        static readonly object Gate = new object();
        static readonly System.Threading.SemaphoreSlim Slots = new System.Threading.SemaphoreSlim(3);
        internal static string Key(string value)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
        }
        public static string Get(string scope, string url, int timeout, string referer, string bearer, string userAgent)
        {
            if (!string.IsNullOrEmpty(bearer)) return NetHttp.GetStringDirect(url, timeout, referer, bearer, userAgent);
            string dir = Path.Combine(AppSettings.DataDir, "page-cache");
            string path = Path.Combine(dir, Key(scope + "\n" + url + "\n" + referer + "\n" + userAgent));
            lock (Gate) try { if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromMinutes(10) && new FileInfo(path).Length <= 2 * 1024 * 1024) return File.ReadAllText(path); } catch { }
            if (!Slots.Wait(Math.Min(30000, Math.Max(1000, timeout)))) throw new TimeoutException("Resolver connections are busy");
            string body;
            try { body = SourceHttps.GetString(url, timeout, referer, userAgent); }
            finally { Slots.Release(); }
            if ((body ?? "").Length > 2 * 1024 * 1024) throw new InvalidDataException("Source response exceeds 2 MiB");
            lock (Gate) try
            {
                Directory.CreateDirectory(dir);
                var files = new DirectoryInfo(dir).GetFiles();
                Array.Sort(files, (a,b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                long size = Encoding.UTF8.GetByteCount(body ?? ""); foreach (var f in files) size += f.Length;
                for (int i = 0; i < files.Length && (files.Length - i >= 48 || size > 24 * 1024 * 1024); i++) { size -= files[i].Length; files[i].Delete(); }
                AtomicFile.WriteText(path, body);
            } catch { }
            return body ?? "";
        }
    }
}
