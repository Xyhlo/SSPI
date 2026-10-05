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
        public static string Get(string scope, string url, int timeout, string referer, string bearer, string userAgent,
            Func<Uri, bool> allowOrigin = null, Func<bool> cancel = null)
        {
            if (cancel != null && cancel()) throw new OperationCanceledException();
            if (allowOrigin != null && !allowOrigin(new Uri(url, UriKind.Absolute))) throw new IOException("Source request origin is not permitted");
            if (!string.IsNullOrEmpty(bearer)) return NetHttp.GetStringDirect(url, timeout, referer, bearer, userAgent, cancel: cancel, allowOrigin: allowOrigin);
            string dir = Path.Combine(AppSettings.DataDir, "page-cache");
            string path = Path.Combine(dir, Key((allowOrigin == null ? "" : "origin-checked-v1\n") + scope + "\n" + url + "\n" + referer + "\n" + userAgent));
            lock (Gate) try
            {
                DateTime now = DateTime.UtcNow;
                if (now.Year >= 2020 && File.Exists(path))
                {
                    DateTime modified = File.GetLastWriteTimeUtc(path);
                    TimeSpan age = now - modified;
                    if (modified.Year >= 2020 && age >= TimeSpan.Zero && age < TimeSpan.FromMinutes(10) &&
                        new FileInfo(path).Length <= 2 * 1024 * 1024) return File.ReadAllText(path);
                }
            } catch { }
            int waitTimeout = Math.Min(30000, Math.Max(1000, timeout));
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                int remaining = waitTimeout - (int)Math.Min(waitTimeout, wait.ElapsedMilliseconds);
                if (remaining <= 0) throw new TimeoutException("Resolver connections are busy");
                if (Slots.Wait(Math.Min(100, remaining))) break;
            }
            string body;
            try
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                body = SourceHttps.GetString(url, timeout, referer, userAgent, cancel: cancel, allowOrigin: allowOrigin);
            }
            finally { Slots.Release(); }
            if (cancel != null && cancel()) throw new OperationCanceledException();
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
