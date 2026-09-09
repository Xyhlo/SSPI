using System;
using System.IO;
using System.Text;

namespace Orbis
{
    internal enum PkgValResult
    {
        Ok,
        Missing,
        TooSmall,
        BadMagic,
        LengthMismatch,
        TitleMismatch,
        NativeParseFailed
    }

    internal enum PkgContentKind
    {
        Unknown,
        BaseGame,
        Patch,
        AddOn
    }

    /// <summary>Structural PS4 PKG checks before mark-complete / install.</summary>
    internal static class PkgValidator
    {
        // Magic: 7F 43 4E 54 ("\x7fCNT")
        static readonly byte[] Magic = { 0x7F, 0x43, 0x4E, 0x54 };
        const uint ContentTypeGameData = 0x1A;
        const uint ContentTypeAddOnData = 0x1B;
        const uint ContentTypeAddOnNoData = 0x1C;
        const uint ContentTypeDeltaPatch = 0x1E;
        const uint ContentFlagFirstPatch = 0x00100000;
        const uint ContentFlagPatchGo = 0x00200000;
        const uint ContentFlagSubsequentPatch = 0x40000000;

        public static bool TryValidate(string path, string expectedTitleId, out PkgValResult result, out string detail)
        {
            if (!TryValidateStructure(path, out result, out detail))
                return false;

            try
            {
                // Native parse + optional title match. This is not a whole-file digest check.
                string tid;
                string err;
                if (!PkgInstaller.TryGetTitleId(path, out tid, out err))
                {
                    result = PkgValResult.NativeParseFailed;
                    detail = err ?? "GetTitleId failed";
                    return false;
                }
                if (!string.IsNullOrEmpty(expectedTitleId))
                {
                    string exp = expectedTitleId.Trim().ToUpperInvariant();
                    string got = (tid ?? "").Trim().ToUpperInvariant();
                    if (!string.IsNullOrEmpty(exp) && !string.Equals(exp, got, StringComparison.Ordinal))
                    {
                        string contentId;
                        if (!TryGetContentId(path, out contentId) ||
                            !ContentIdMatchesTitleId(contentId, exp))
                        {
                            result = PkgValResult.TitleMismatch;
                            detail = "expected " + exp + " got " + got;
                            return false;
                        }
                    }
                }

                result = PkgValResult.Ok;
                detail = "OK " + tid + " " + new FileInfo(path).Length + " bytes";
                return true;
            }
            catch (Exception ex)
            {
                result = PkgValResult.NativeParseFailed;
                detail = ex.Message;
                return false;
            }
        }

        public static bool TryValidateDownload(string path, string expectedTitleId, string requestedKind,
            out PkgValResult result, out string detail)
        {
            bool nativeConfirmed = TryValidate(path, expectedTitleId, out result, out detail);
            if (!nativeConfirmed && result != PkgValResult.NativeParseFailed)
                return false;

            PkgContentKind actualKind;
            string kindDetail;
            if (!TryGetContentKind(path, out actualKind, out kindDetail))
            {
                result = PkgValResult.LengthMismatch;
                detail = kindDetail;
                return false;
            }

            PkgContentKind expectedKind = RequestedKind(requestedKind);
            if (expectedKind != PkgContentKind.Unknown && actualKind != expectedKind)
            {
                result = PkgValResult.TitleMismatch;
                detail = "expected " + expectedKind + " package, got " + actualKind;
                return false;
            }

            if (!nativeConfirmed)
            {
                string contentId;
                string expected = (expectedTitleId ?? "").Trim();
                if (!string.IsNullOrEmpty(expected) &&
                    (!TryGetContentId(path, out contentId) ||
                     !ContentIdMatchesTitleId(contentId, expected)))
                {
                    result = PkgValResult.TitleMismatch;
                    detail = "could not confirm expected title " + expected + " from PKG content ID";
                    return false;
                }
                detail = "structure/content valid; native title check pending: " + detail;
            }
            return true;
        }

        internal static bool ContentIdMatchesTitleId(string contentId, string expectedTitleId)
        {
            if (string.IsNullOrEmpty(contentId) || string.IsNullOrEmpty(expectedTitleId)) return false;
            int dash = contentId.IndexOf('-');
            int underscore = dash >= 0 ? contentId.IndexOf('_', dash + 1) : -1;
            if (dash < 0 || underscore <= dash + 1) return false;
            string embeddedTitleId = contentId.Substring(dash + 1, underscore - dash - 1);
            return string.Equals(embeddedTitleId, expectedTitleId.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        internal static PkgContentKind RequestedKind(string requestedKind)
        {
            if (string.Equals(requestedKind, "game", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(requestedKind, "base", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(requestedKind, "basegame", StringComparison.OrdinalIgnoreCase))
                return PkgContentKind.BaseGame;
            if (string.Equals(requestedKind, "update", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(requestedKind, "patch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(requestedKind, "backport", StringComparison.OrdinalIgnoreCase))
                return PkgContentKind.Patch;
            if (string.Equals(requestedKind, "dlc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(requestedKind, "addon", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(requestedKind, "add-on", StringComparison.OrdinalIgnoreCase))
                return PkgContentKind.AddOn;
            return PkgContentKind.Unknown;
        }

        internal static bool TryValidateStructure(string path, out PkgValResult result, out string detail)
        {
            result = PkgValResult.Missing;
            detail = "";
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    detail = "file missing";
                    return false;
                }
                long len = new FileInfo(path).Length;
                if (len < 0x438)
                {
                    result = PkgValResult.TooSmall;
                    detail = "size " + len + " < 0x438";
                    return false;
                }

                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] mag = new byte[4];
                    if (fs.Read(mag, 0, 4) != 4 || mag[0] != Magic[0] || mag[1] != Magic[1] ||
                        mag[2] != Magic[2] || mag[3] != Magic[3])
                    {
                        result = PkgValResult.BadMagic;
                        detail = "not a PS4 PKG (bad magic)";
                        return false;
                    }

                    fs.Position = 0x20;
                    ulong bodyOffset = ReadU64BE(fs);
                    ulong bodySize = ReadU64BE(fs);
                    if (bodyOffset == 0 || bodySize == 0)
                    {
                        result = PkgValResult.LengthMismatch;
                        detail = "zero body_offset/size";
                        return false;
                    }

                    fs.Position = 0x410;
                    ulong pfsOffset = ReadU64BE(fs);
                    ulong pfsSize = ReadU64BE(fs);
                    fs.Position = 0x430;
                    ulong packageSize = ReadU64BE(fs);

                    ulong bodyEnd;
                    ulong pfsEnd = 0;
                    try
                    {
                        checked
                        {
                            bodyEnd = bodyOffset + bodySize;
                            if (pfsSize != 0) pfsEnd = pfsOffset + pfsSize;
                        }
                    }
                    catch
                    {
                        result = PkgValResult.LengthMismatch;
                        detail = "header size overflow";
                        return false;
                    }

                    ulong fileSize = (ulong)len;
                    ulong effectiveEnd = packageSize != 0 ? packageSize : fileSize;
                    if (packageSize != 0 && packageSize != fileSize)
                    {
                        result = PkgValResult.LengthMismatch;
                        detail = "file " + fileSize + " != package_size " + packageSize;
                        return false;
                    }
                    if (bodyOffset < 0x438 || bodyEnd > effectiveEnd)
                    {
                        result = PkgValResult.LengthMismatch;
                        detail = "body range " + bodyOffset + ".." + bodyEnd +
                                 " outside package " + effectiveEnd;
                        return false;
                    }
                    if (pfsSize != 0)
                    {
                        if (pfsOffset < bodyEnd || pfsEnd != effectiveEnd)
                        {
                            result = PkgValResult.LengthMismatch;
                            detail = "PFS range " + pfsOffset + ".." + pfsEnd +
                                     " inconsistent with body end " + bodyEnd +
                                     " and package " + effectiveEnd;
                            return false;
                        }
                    }
                    else if (bodyEnd != effectiveEnd)
                    {
                        result = PkgValResult.LengthMismatch;
                        detail = "no PFS and body end " + bodyEnd + " != package " + effectiveEnd;
                        return false;
                    }

                    // Deleted PS4/NC downloads and aborted BGFT copies are often full-size
                    // with a valid header. package_size matches, but the payload is zeros
                    // (sparse prealloc) or only the start of PFS was written. Installing
                    // those reports "corrupted". Require both the start and the tail of
                    // the payload to contain data.
                    if (!PayloadLooksPresent(fs, fileSize, bodyOffset, bodySize, pfsOffset, pfsSize))
                    {
                        result = PkgValResult.LengthMismatch;
                        detail = "PKG payload is empty (incomplete download)";
                        return false;
                    }
                }

                result = PkgValResult.Ok;
                detail = "structural OK " + len + " bytes";
                return true;
            }
            catch (Exception ex)
            {
                result = PkgValResult.LengthMismatch;
                detail = ex.Message;
                return false;
            }
        }

        internal static bool TryGetContentKind(string path, out PkgContentKind kind, out string detail)
        {
            kind = PkgContentKind.Unknown;
            detail = "";
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length < 0x7C)
                    {
                        detail = "PKG header too small";
                        return false;
                    }
                    fs.Position = 0x74;
                    uint contentType = ReadU32BE(fs);
                    uint contentFlags = ReadU32BE(fs);
                    return ClassifyContentKind(contentType, contentFlags, out kind, out detail);
                }
            }
            catch (Exception ex)
            {
                detail = ex.Message;
                return false;
            }
        }

        internal static bool TryGetContentId(string path, out string contentId)
        {
            contentId = "";
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (fs.Length < 0x64) return false;
                    fs.Position = 0x40;
                    byte[] data = new byte[0x24];
                    int read = fs.Read(data, 0, data.Length);
                    if (read != data.Length) return false;
                    contentId = Encoding.ASCII.GetString(data).TrimEnd('\0');
                    return contentId.Length > 0;
                }
            }
            catch { return false; }
        }

        /// <summary>Parse PKG content ID from the first header bytes (remote preflight).</summary>
        internal static bool TryGetContentIdFromHeader(byte[] header, out string contentId)
        {
            contentId = "";
            if (header == null || header.Length < 0x64) return false;
            if (header[0] != Magic[0] || header[1] != Magic[1] ||
                header[2] != Magic[2] || header[3] != Magic[3])
                return false;
            contentId = Encoding.ASCII.GetString(header, 0x40, 0x24).TrimEnd('\0');
            return contentId.Length > 0;
        }

        internal static bool TryGetContentKindFromHeader(byte[] header, out PkgContentKind kind,
            out string detail)
        {
            kind = PkgContentKind.Unknown;
            detail = "PKG header too small";
            if (header == null || header.Length < 0x7C ||
                header[0] != Magic[0] || header[1] != Magic[1] ||
                header[2] != Magic[2] || header[3] != Magic[3]) return false;
            uint contentType = ReadU32BE(header, 0x74);
            uint contentFlags = ReadU32BE(header, 0x78);
            return ClassifyContentKind(contentType, contentFlags, out kind, out detail);
        }

        internal static bool TryGetPackageSizeFromHeader(byte[] header, out long packageSize)
        {
            packageSize = 0;
            if (header == null || header.Length < 0x438 ||
                header[0] != Magic[0] || header[1] != Magic[1] ||
                header[2] != Magic[2] || header[3] != Magic[3]) return false;
            ulong value = ReadU64BE(header, 0x430);
            if (value == 0 || value > long.MaxValue) return false;
            packageSize = (long)value;
            return true;
        }

        internal static bool TryGetTitleIdFromContentId(string contentId, out string titleId)
        {
            titleId = "";
            if (string.IsNullOrEmpty(contentId)) return false;
            // Content ID form: EP0001-CUSA00000_00-...
            int dash = contentId.IndexOf('-');
            if (dash < 0 || dash + 1 >= contentId.Length) return false;
            int under = contentId.IndexOf('_', dash + 1);
            if (under < 0) under = contentId.Length;
            titleId = contentId.Substring(dash + 1, under - dash - 1).ToUpperInvariant();
            return titleId.StartsWith("CUSA", StringComparison.Ordinal) && titleId.Length >= 9;
        }

        internal static bool CheckRequestedIdentity(string requestedKind, PkgContentKind actualKind,
            string titleId, string contentId, out string error)
        {
            error = null;
            PkgContentKind expected = RequestedKind(requestedKind);
            if (expected != PkgContentKind.Unknown && actualKind != expected)
            { error = "Package type mismatch: requested " + expected + ", received " + actualKind; return false; }
            if (!string.IsNullOrEmpty(titleId) && !ContentIdMatchesTitleId(contentId, titleId))
            { error = "Package does not belong to " + titleId; return false; }
            if (actualKind != PkgContentKind.Patch) return true;
            string installed = InstalledContentId(titleId);
            if (installed.Length > 0 && !string.Equals(installed, contentId, StringComparison.OrdinalIgnoreCase))
            { error = "Update content ID differs from the installed base game. Choose the matching release."; return false; }
            return true;
        }

        static string InstalledContentId(string titleId)
        {
            if (string.IsNullOrEmpty(titleId) || !System.Text.RegularExpressions.Regex.IsMatch(titleId, @"^CUSA[0-9]{5}$")) return "";
            foreach (string root in new[] { "/system_data/priv/appmeta", "/user/appmeta", "/mnt/ext0/user/appmeta" })
            {
                try
                {
                    string path = Path.Combine(Path.Combine(root, titleId), "param.sfo");
                    if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) continue;
                    byte[] b = File.ReadAllBytes(path);
                    if (b.Length < 20 || b[0] != 0 || b[1] != 80 || b[2] != 83 || b[3] != 70) continue;
                    uint ko = BitConverter.ToUInt32(b, 8), vo = BitConverter.ToUInt32(b, 12), count = BitConverter.ToUInt32(b, 16);
                    if (count > 128) continue;
                    for (int i = 0; i < count && 20 + i * 16 + 16 <= b.Length; i++)
                    {
                        int e = 20 + i * 16;
                        long k = (long)ko + BitConverter.ToUInt16(b, e), v = (long)vo + BitConverter.ToUInt32(b, e + 12);
                        uint n = BitConverter.ToUInt32(b, e + 4);
                        if (k < 0 || k + 11 > b.Length || v < 0 || n > 128 || v + n > b.Length) continue;
                        if (Encoding.ASCII.GetString(b, (int)k, 11) == "CONTENT_ID\0")
                            return Encoding.ASCII.GetString(b, (int)v, (int)n).TrimEnd('\0').Trim();
                    }
                }
                catch { }
            }
            return "";
        }

        internal static int BgftSubTypeForKind(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return 6;
            string k = kind.ToLowerInvariant();
            if (k.Contains("dlc") || k.Contains("addon") || k.Contains("add-on")) return 7;
            if (k.Contains("update") || k.Contains("patch") || k.Contains("backport")) return 8;
            return 6;
        }

        static ulong ReadU64BE(Stream s)
        {
            byte[] b = new byte[8];
            int n = 0;
            while (n < 8)
            {
                int r = s.Read(b, n, 8 - n);
                if (r <= 0) throw new EndOfStreamException();
                n += r;
            }
            return ((ulong)b[0] << 56) | ((ulong)b[1] << 48) | ((ulong)b[2] << 40) | ((ulong)b[3] << 32) |
                   ((ulong)b[4] << 24) | ((ulong)b[5] << 16) | ((ulong)b[6] << 8) | b[7];
        }

        static ulong ReadU64BE(byte[] data, int offset)
        {
            return ((ulong)data[offset] << 56) | ((ulong)data[offset + 1] << 48) |
                ((ulong)data[offset + 2] << 40) | ((ulong)data[offset + 3] << 32) |
                ((ulong)data[offset + 4] << 24) | ((ulong)data[offset + 5] << 16) |
                ((ulong)data[offset + 6] << 8) | data[offset + 7];
        }

        static uint ReadU32BE(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) |
                ((uint)data[offset + 2] << 8) | data[offset + 3];
        }

        static bool ClassifyContentKind(uint contentType, uint contentFlags,
            out PkgContentKind kind, out string detail)
        {
            bool patchFlags = (contentFlags & (ContentFlagFirstPatch |
                ContentFlagPatchGo | ContentFlagSubsequentPatch)) != 0;
            if (contentType == ContentTypeAddOnData || contentType == ContentTypeAddOnNoData)
                kind = PkgContentKind.AddOn;
            else if (contentType == ContentTypeDeltaPatch ||
                (contentType == ContentTypeGameData && patchFlags))
                kind = PkgContentKind.Patch;
            else if (contentType == ContentTypeGameData)
                kind = PkgContentKind.BaseGame;
            else
            {
                kind = PkgContentKind.Unknown;
                detail = "unsupported content type 0x" + contentType.ToString("X");
                return false;
            }
            detail = kind + " type=0x" + contentType.ToString("X2") +
                " flags=0x" + contentFlags.ToString("X8");
            return true;
        }

        static uint ReadU32BE(Stream s)
        {
            int a = s.ReadByte();
            int b = s.ReadByte();
            int c = s.ReadByte();
            int d = s.ReadByte();
            if ((a | b | c | d) < 0) throw new EndOfStreamException();
            return ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | (uint)d;
        }

        static bool PayloadLooksPresent(FileStream fs, ulong fileSize,
            ulong bodyOffset, ulong bodySize, ulong pfsOffset, ulong pfsSize)
        {
            ulong start = 0x438;
            ulong end = fileSize;
            if (pfsSize != 0 && pfsOffset >= 0x438 && pfsOffset < fileSize)
            {
                start = pfsOffset;
                end = pfsOffset + pfsSize;
                if (end > fileSize) end = fileSize;
            }
            else if (bodyOffset >= 0x438 && bodyOffset < fileSize)
            {
                start = bodyOffset;
                end = bodyOffset + bodySize;
                if (end > fileSize) end = fileSize;
            }
            if (end <= start) return false;

            ulong length = end - start;
            int sample = 64;
            if ((ulong)sample > length) sample = (int)length;
            if (IsZeroRange(fs, start, sample)) return false;
            ulong tailAt = end - (ulong)sample;
            if (tailAt != start && IsZeroRange(fs, tailAt, sample)) return false;
            return true;
        }

        static bool IsZeroRange(FileStream fs, ulong offset, int length)
        {
            if (length <= 0) return true;
            if (offset > (ulong)long.MaxValue) return true;
            fs.Position = (long)offset;
            byte[] buf = new byte[length];
            int n = 0;
            while (n < length)
            {
                int r = fs.Read(buf, n, length - n);
                if (r <= 0) return true;
                n += r;
            }
            for (int i = 0; i < n; i++)
                if (buf[i] != 0) return false;
            return true;
        }
    }
}
