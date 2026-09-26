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
                    TransferHeaderIdentity(header, input.Length, titleId);
                    if (expectedCategory != "ac" && Be64(header, 0x418) == 0)
                        throw new IOException("Game/update package has no filesystem data");
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
        // Called only after the native engine checked this exact expected SHA-256
        // and released the completed file. Keep the existing file-change binding.
        internal static void RememberVerifiedSha256(string path, string expected)
        {
            expected = (expected ?? "").Trim().Replace("-", "").ToLowerInvariant();
            using (var input = File.OpenRead(path))
            {
                byte[] head = Read(input, (int)Math.Min(input.Length, 0x1000));
                string stamp = expected + ":" + input.Length + ":" + File.GetLastWriteTimeUtc(path).Ticks + ":" + Convert.ToBase64String(head);
                lock (CacheGate) { if (Verified.Count >= 64) Verified.Clear(); Verified["sha:" + path] = stamp; }
            }
        }
        internal static bool VerifyFile(string path, string expected, Action<string> status, Func<bool> cancel, out string error)
        {
            expected = (expected ?? "").Trim().Replace("-", "").ToLowerInvariant();
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
                    TransferHeaderIdentity(h, s.Length, titleId);
                    if (Be64(h, 0x418) == 0) throw new IOException("Update package has no filesystem data");
                    if (status != null) status("Checking update compatibility");
                }
                byte[] sfo = Entry(path, 0x1000);
                string category = SfoValue(sfo, "CATEGORY"), actual = SfoValue(sfo, "TITLE_ID");
                if (category != "gp" || !string.Equals(actual, titleId, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Update metadata does not match the selected game");
                Version version;
                if (!Version.TryParse(SfoValue(sfo, "APP_VER"), out version))
                    throw new IOException("Update has no valid application version");
                // Backports can retain the original SYSTEM_VER while replacing the
                // executables that require it. The metadata cannot prove whether
                // the installer will accept the package on this firmware.
                // Keep identity/version checks here and let BGFT report eligibility.
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

        internal static string TransferHeaderIdentity(byte[] header, long total, string titleId)
        {
            if (header == null || header.Length != 0x1000 || total < 0x1000 || Be32(header, 0) != 0x7f434e54)
                throw new IOException("Package integrity header is missing");
            bool noData = Be32(header, 0x74) == 0x1c && Be64(header, 0x430) == 0 &&
                Be64(header, 0x410) == 0 && Be64(header, 0x418) == 0 &&
                Be64(header, 0x20) >= 0x1000 && Be64(header, 0x20) <= total &&
                Be64(header, 0x28) > 0 && Be64(header, 0x28) == total - Be64(header, 0x20);
            if ((!noData && Be64(header, 0x430) != total) || (!string.IsNullOrEmpty(titleId) &&
                !string.Equals(Encoding.ASCII.GetString(header, 0x47, 9), titleId, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Package integrity identity does not match");
            long body = Be64(header, 0x20), bodySize = Be64(header, 0x28);
            long pfs = Be64(header, 0x410), pfsSize = Be64(header, 0x418);
            // PKG regions may overlap or leave padding. Validate each declared
            // extent independently; an empty body/PFS is valid for license DLC.
            if (body > total || bodySize > total - body || (bodySize > 0 && body < 0x1000) ||
                pfs > total || pfsSize > total - pfs || (pfsSize > 0 && pfs < 0x1000))
                throw new IOException("Invalid package integrity ranges");
            using (var sha = CreateSha256())
            {
                byte[] digest = sha.ComputeHash(header, 0, 0xfe0);
                for (int i = 0; i < 32; i++) if (digest[i] != header[0xfe0 + i])
                    throw new IOException("Package integrity header digest mismatch");
                return BitConverter.ToString(sha.ComputeHash(header)).Replace("-", "");
            }
        }

        internal static bool IsNoDataLicense(string path)
        {
            try { using (var file = File.OpenRead(path)) {
                byte[] h = Read(file, 0x1000);
                return Be32(h, 0) == 0x7f434e54 && Be32(h, 0x74) == 0x1c &&
                    Be64(h, 0x410) == 0 && Be64(h, 0x418) == 0;
            } } catch { return false; }
        }

        internal static long BgftPackageSize(string path)
        {
            using (var file = File.OpenRead(path))
            {
                byte[] header = Read(file, 0x1000);
                TransferHeaderIdentity(header, file.Length, null);
                // PS4AL can declare zero while its license container has bytes.
                // BGFT metadata uses the declaration; HTTP/copy totals use file.Length.
                return Be64(header, 0x430);
            }
        }

        internal static bool VerifyNoDataLicense(string path, out string error)
        {
            error = null;
            try { using (var file = File.OpenRead(path)) {
                byte[] h = Read(file, 0x1000);
                if (Be32(h, 0x74) != 0x1c || Be64(h, 0x410) != 0 || Be64(h, 0x418) != 0)
                    throw new IOException("Package is not a no-data license");
                TransferHeaderIdentity(h, file.Length, null);
                long offset = Be64(h, 0x20), remaining = Be64(h, 0x28);
                if (offset < 0x1000 || offset > file.Length || remaining <= 0 || remaining != file.Length - offset)
                    throw new IOException("License body does not reach the end of the package");
                file.Position = offset;
                using (var sha = CreateSha256()) {
                    byte[] digest = sha.ComputeHash(file);
                    for (int i = 0; i < 32; i++) if (digest[i] != h[0x160 + i])
                        throw new IOException("License body digest mismatch (incomplete or corrupt download)");
                }
                return true;
            } } catch (Exception ex) { error = ex.Message; return false; }
        }

        // Bind the completed transfer to its header without rereading the body.
        // A publisher-supplied SHA remains an explicit whole-file check below.
        internal static void VerifyTransfer(string path, string headerIdentity, string titleId,
            string expectedSha256, Func<bool> cancel)
        {
            if (cancel != null && cancel()) throw new OperationCanceledException();
            if (!string.IsNullOrEmpty(headerIdentity))
            {
                using (var input = File.OpenRead(path))
                {
                    byte[] header = Read(input, 0x1000);
                    if (!string.Equals(TransferHeaderIdentity(header, input.Length, titleId), headerIdentity, StringComparison.Ordinal))
                        throw new IOException("Downloaded package header changed; partial kept");
                }
            }
            if (!string.IsNullOrEmpty(expectedSha256))
            {
                string error;
                if (!VerifyFile(path, expectedSha256, null, cancel, out error)) throw new IOException(error);
            }
        }
        internal static bool CheckInstalledBase(string patch, string titleId, out string error)
        {
            return CheckInstalledBase(patch, titleId, new[] { "/user/app/", "/mnt/ext0/user/app/" }, out error);
        }
        const string InstalledBaseUnavailable = "Installed base PKG is unavailable for compatibility checks; update retained.";
        internal static bool IsInstalledBaseUnavailable(string error)
        {
            return string.Equals(error, InstalledBaseUnavailable, StringComparison.Ordinal);
        }
        static bool CheckInstalledBase(string patch, string titleId, string[] roots, out string error)
        {
            if (!Regex.IsMatch(titleId ?? "", @"^[A-Z]{4}[0-9]{5}$", RegexOptions.IgnoreCase))
            { error = "Invalid installed base title identity"; return false; }
            foreach (string root in roots)
            {
                string path = Path.Combine(Path.Combine(root, titleId), "app.pkg");
                if (File.Exists(path)) return CheckPatchBase(patch, path, out error);
            }
            error = InstalledBaseUnavailable;
            return false;
        }
        internal static bool WaitForInstalledBase(string patch, string titleId, Action<string> status,
            Func<bool> cancel, out string error)
        {
            return WaitForInstalledBase(patch, titleId, new[] { "/user/app/", "/mnt/ext0/user/app/" },
                30000, 250, status, cancel, out error);
        }
        internal static bool WaitForInstalledBase(string patch, string titleId, string[] roots,
            int timeoutMs, int pollMs, Action<string> status, Func<bool> cancel, out string error)
        {
            // BGFT completion and AppExists can precede visibility of app.pkg.
            // Wait only for publication; a present but incompatible base still fails immediately.
            var clock = Stopwatch.StartNew();
            timeoutMs = Math.Max(0, Math.Min(30000, timeoutMs));
            pollMs = Math.Max(25, Math.Min(1000, pollMs));
            long noticeAt = -1000;
            for (;;)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                if (CheckInstalledBase(patch, titleId, roots, out error)) return true;
                if (!IsInstalledBaseUnavailable(error) || clock.ElapsedMilliseconds >= timeoutMs) return false;
                if (status != null && clock.ElapsedMilliseconds - noticeAt >= 1000)
                { noticeAt = clock.ElapsedMilliseconds; status("Waiting for installed base package · update retained"); }
                System.Threading.Thread.Sleep((int)Math.Min(pollMs, Math.Max(1, timeoutMs - clock.ElapsedMilliseconds)));
            }
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
