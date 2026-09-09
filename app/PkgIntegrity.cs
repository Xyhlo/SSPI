using System;
using System.IO;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbis
{
    internal static class PkgIntegrity
    {
        static readonly object CacheGate = new object();
        static readonly Dictionary<string, string> Verified = new Dictionary<string, string>();
        internal static SHA256 CreateSha256()
        {
            try { return new NativeSha256(); }
            catch (DllNotFoundException) { return SHA256.Create(); }
            catch (EntryPointNotFoundException) { return SHA256.Create(); }
            catch (BadImageFormatException) { return SHA256.Create(); }
        }
        sealed class NativeSha256 : SHA256
        {
            IntPtr context;
            public NativeSha256() { HashSizeValue = 256; context = gs_sha256_create(); if (context == IntPtr.Zero) throw new OutOfMemoryException(); }
            public override void Initialize() { gs_sha256_reset(context); }
            protected override void HashCore(byte[] data, int offset, int count)
            {
                var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
                try { gs_sha256_update(context, IntPtr.Add(pin.AddrOfPinnedObject(), offset), (UIntPtr)(uint)count); }
                finally { pin.Free(); }
            }
            protected override byte[] HashFinal() { byte[] result = new byte[32]; gs_sha256_finish(context, result); return result; }
            protected override void Dispose(bool disposing) { if (context != IntPtr.Zero) { gs_sha256_free(context); context = IntPtr.Zero; } base.Dispose(disposing); }
            [DllImport("libGameSearchResident.prx", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr gs_sha256_create();
            [DllImport("libGameSearchResident.prx", CallingConvention = CallingConvention.Cdecl)] static extern void gs_sha256_reset(IntPtr p);
            [DllImport("libGameSearchResident.prx", CallingConvention = CallingConvention.Cdecl)] static extern void gs_sha256_update(IntPtr p, IntPtr data, UIntPtr count);
            [DllImport("libGameSearchResident.prx", CallingConvention = CallingConvention.Cdecl)] static extern void gs_sha256_finish(IntPtr p, [Out] byte[] result);
            [DllImport("libGameSearchResident.prx", CallingConvention = CallingConvention.Cdecl)] static extern void gs_sha256_free(IntPtr p);
        }
        internal static uint Be32(byte[] b, int p) { return ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3]; }
        static long Be64(byte[] b, int p) { return checked((long)(((ulong)Be32(b, p) << 32) | Be32(b, p + 4))); }
        static byte[] Read(Stream s, int count)
        {
            byte[] b = new byte[count]; int at = 0, n;
            while (at < count && (n = s.Read(b, at, count - at)) > 0) at += n;
            if (at != count) throw new IOException("Truncated PKG");
            return b;
        }
        internal static byte[] Entry(string path, uint id)
        {
            using (var s = File.OpenRead(path))
            {
                byte[] h = Read(s, 32); uint count = Be32(h, 16), table = Be32(h, 24);
                if (count > 4096 || (long)table + count * 32L > s.Length) throw new IOException("Invalid PKG entry table");
                for (int i = 0; i < count; i++)
                {
                    s.Position = table + i * 32L; byte[] e = Read(s, 32);
                    if (Be32(e, 0) != id) continue;
                    long offset = Be32(e, 16), size = Be32(e, 20);
                    if (size > 1024 * 1024 || offset + size > s.Length) throw new IOException("Invalid PKG metadata");
                    s.Position = offset; return Read(s, (int)size);
                }
            }
            return null;
        }
        internal static string SfoValue(byte[] b, string key)
        {
            if (b == null || b.Length < 20 || BitConverter.ToUInt32(b, 0) != 0x46535000) return "";
            uint keys = BitConverter.ToUInt32(b, 8), values = BitConverter.ToUInt32(b, 12), count = BitConverter.ToUInt32(b, 16);
            if (count > 256) return "";
            for (int i = 0; i < count; i++)
            {
                int e = 20 + i * 16; if (e + 16 > b.Length) return "";
                long k = keys + (long)BitConverter.ToUInt16(b, e), v = values + (long)BitConverter.ToUInt32(b, e + 12);
                uint n = BitConverter.ToUInt32(b, e + 4);
                if (k < 0 || k + key.Length >= b.Length || v < 0 || n > 65536 || v + n > b.Length) continue;
                if (Encoding.ASCII.GetString(b, (int)k, key.Length + 1) != key + "\0") continue;
                if (BitConverter.ToUInt16(b, e + 2) == 0x0404 && n == 4)
                    return BitConverter.ToUInt32(b, (int)v).ToString("X8");
                return Encoding.UTF8.GetString(b, (int)v, (int)n).TrimEnd('\0');
            }
            return "";
        }
        internal static string FirmwareRequirement(string text)
        {
            var m = Regex.Match(text ?? "", @"(?:FW|firmware|system\s+software)\s*[:v=]?\s*(\d{1,2}\.\d{2})", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : "";
        }
        internal static string FirmwareLabel(string required, string installed)
        {
            Version need, have;
            if (!Version.TryParse(required, out need)) return "Firmware requirement unknown";
            var m = Regex.Match(installed ?? "", @"\d{1,2}\.\d{2}");
            if (!m.Success || !Version.TryParse(m.Value, out have)) return "Requires PS4 " + required + " · console firmware unknown";
            return need > have ? "Needs backport · requires " + required + " / PS4 " + m.Value : "Firmware compatible · PS4 " + m.Value;
        }
        internal static string PackageType(string path)
        {
            using (var file = File.OpenRead(path))
            {
                byte[] h = Read(file, 0x80);
                switch (Be32(h, 0x74))
                {
                    case 0x1a: return "PS4GD";
                    case 0x1b: return "PS4AC";
                    case 0x1c: return "PS4AL";
                    case 0x1e: return "PS4DP";
                    default: throw new IOException("Unsupported PKG content type");
                }
            }
        }
        internal static bool CheckMetadata(string path, string titleId, string expectedCategory, out string error)
        {
            error = null;
            try
            {
                byte[] header;
                using (var input = File.OpenRead(path))
                {
                    header = Read(input, 0x1000);
                    CheckHash(input, 0, 0xfe0, header, 0xfe0, null, null);
                }
                byte[] sfo = Entry(path, 0x1000);
                string category = SfoValue(sfo, "CATEGORY"), actualTitle = SfoValue(sfo, "TITLE_ID");
                if (!category.StartsWith(expectedCategory, StringComparison.Ordinal) ||
                    (actualTitle.Length > 0 && !string.Equals(actualTitle, titleId, StringComparison.OrdinalIgnoreCase)) ||
                    (expectedCategory != "ac" && actualTitle.Length == 0))
                    throw new IOException("PKG header and embedded title/type disagree. Installation stopped; game unchanged.");
                string content = SfoValue(sfo, "CONTENT_ID");
                string headerContent = Encoding.ASCII.GetString(header, 0x40, 36).TrimEnd('\0');
                if (content.Length > 0 && !string.Equals(content, headerContent, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("PKG content identity differs between header and metadata");
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
        internal static bool VerifyFile(string path, string expected, Action<string> status, Func<bool> cancel, out string error)
        {
            error = null;
            try
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                using (var input = File.OpenRead(path))
                {
                    byte[] head = Read(input, (int)Math.Min(input.Length, 0x1000));
                    string key = "sha:" + path;
                    string stamp = expected + ":" + input.Length + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToBase64String(head);
                    lock (CacheGate) { string previous; if (Verified.TryGetValue(key, out previous) && previous == stamp) return true; }
                    input.Position = 0; byte[] buffer = new byte[1024 * 1024]; long done = 0;
                    var clock = Stopwatch.StartNew(); long last = -500;
                    using (var sha = CreateSha256())
                    {
                        int n;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (cancel != null && cancel()) throw new OperationCanceledException();
                            sha.TransformBlock(buffer, 0, n, buffer, 0); done += n;
                            if (status != null && clock.ElapsedMilliseconds - last >= 500)
                            { last = clock.ElapsedMilliseconds; status("Verifying file " + (int)(done * 100.0 / Math.Max(1, input.Length)) + "%"); }
                        }
                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        string actual = BitConverter.ToString(sha.Hash).Replace("-", "");
                        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Package SHA-256 mismatch");
                    }
                    lock (CacheGate) { if (Verified.Count >= 64) Verified.Clear(); Verified[key] = stamp; }
                    return true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { error = ex.Message; return false; }
        }
        internal static bool ValidatePatch(string path, string titleId, string firmware, Action<string> status, Func<bool> cancel, out string error)
        {
            error = null;
            try
            {
                using (var s = File.OpenRead(path))
                {
                    byte[] h = Read(s, 0x1000);
                    if (cancel != null && cancel()) throw new OperationCanceledException();
                    CheckHash(s, 0, 0xFE0, h, 0xFE0, cancel, null);
                    string stamp = s.Length + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToBase64String(h);
                    bool cached;
                    lock (CacheGate) { string previous; cached = Verified.TryGetValue(path, out previous) && previous == stamp; }
                    if (!cached)
                    {
                        CheckHash(s, Be64(h, 0x20), Be64(h, 0x28), h, 0x160, cancel, status);
                        CheckHash(s, Be64(h, 0x410), Be64(h, 0x418), h, 0x440, cancel, status);
                        lock (CacheGate) { if (Verified.Count >= 64) Verified.Clear(); Verified[path] = stamp; }
                    }
                    if (status != null) status("Update verified · checking installed game");
                }
                byte[] sfo = Entry(path, 0x1000);
                string category = SfoValue(sfo, "CATEGORY"), actual = SfoValue(sfo, "TITLE_ID");
                if (category != "gp" || !string.Equals(actual, titleId, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Update metadata does not match the selected game");
                Version version;
                if (!Version.TryParse(SfoValue(sfo, "APP_VER"), out version))
                    throw new IOException("Update has no valid application version");
                string requiredHex = SfoValue(sfo, "SYSTEM_VER");
                if (requiredHex.Length == 8)
                {
                    string required = int.Parse(requiredHex.Substring(0, 2)).ToString() + "." + requiredHex.Substring(2, 2);
                    string label = FirmwareLabel(required, firmware);
                    if (label.StartsWith("Needs backport")) throw new IOException(label + ". Choose the matching backport.");
                }
                return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { error = ex.Message; return false; }
        }
        internal static bool CheckUpdateVersion(string incoming, string installed, out string error)
        {
            error = null; Version next, current;
            if (!Version.TryParse(incoming, out next)) { error = "Update version is missing or invalid"; return false; }
            if (Version.TryParse(installed, out current) && next < current)
            { error = "Older update " + incoming + " cannot replace installed " + installed + ". Choose a matching update/backport."; return false; }
            return true;
        }
        internal static bool CheckPatchBase(string patch, string basePath, out string error)
        {
            error = null;
            try
            {
                if (PackageType(patch) == "PS4DP") throw new IOException("Delta update needs an exact predecessor. Select a full update/backport package.");
                byte[] a = Entry(patch, 0x10), b = Entry(basePath, 0x10);
                if (a == null || b == null || a.Length < 96 || b.Length < 96)
                    throw new IOException("Cannot verify installed base package compatibility. Update retained; installation stopped.");
                using (var p = File.OpenRead(patch)) using (var s = File.OpenRead(basePath))
                {
                    byte[] ph = Read(p, 0x80), bh = Read(s, 0x80);
                    for (int i = 0x40; i < 0x40 + 36; i++) if (ph[i] != bh[i])
                        throw new IOException("Update content ID does not match the installed base game");
                }
                bool nonzero = false;
                // ENTRY_KEYS: seed digest, then seven key fingerprints. Index 1 is the image key.
                for (int i = 64; i < 96; i++)
                {
                    if (a[i] != b[i]) throw new IOException("Update was built for a different base package. Choose a matching update/backport; game unchanged.");
                    nonzero |= a[i] != 0;
                }
                if (!nonzero) throw new IOException("Update/base key fingerprint is missing; installation stopped");
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }
        internal static bool CheckInstalledBase(string patch, string titleId, out string error)
        {
            foreach (string root in new[] { "/user/app/", "/mnt/ext0/user/app/" })
            {
                string path = root + titleId + "/app.pkg";
                if (File.Exists(path)) return CheckPatchBase(patch, path, out error);
            }
            error = "Installed base PKG is unavailable for compatibility checks; update retained.";
            return false;
        }
        static void CheckHash(Stream s, long offset, long length, byte[] header, int digest, Func<bool> cancel, Action<string> status)
        {
            if (offset < 0 || length <= 0 || offset > s.Length - length) throw new IOException("Invalid update data range");
            using (var sha = CreateSha256())
            {
                s.Position = offset; byte[] buffer = new byte[1024 * 1024]; long left = length;
                var timer = Stopwatch.StartNew(); long last = -500;
                if (status != null) status("Verifying update data 0%");
                while (left > 0)
                {
                    if (cancel != null && cancel()) throw new OperationCanceledException();
                    int n = s.Read(buffer, 0, (int)Math.Min(left, buffer.Length));
                    if (n <= 0) throw new IOException("Update download is incomplete");
                    sha.TransformBlock(buffer, 0, n, buffer, 0); left -= n;
                    if (status != null && (timer.ElapsedMilliseconds - last >= 500 || left == 0))
                    {
                        last = timer.ElapsedMilliseconds;
                        status("Verifying update " + (int)((length - left) * 100.0 / length) + "% · " +
                            ((length - left) / (1024.0 * 1024)).ToString("0") + " / " + (length / (1024.0 * 1024)).ToString("0") + " MB");
                    }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                for (int i = 0; i < 32; i++) if (sha.Hash[i] != header[digest + i])
                    throw new IOException("Update integrity check failed. Re-download this package; the installed game was not modified.");
            }
        }
    }
}
