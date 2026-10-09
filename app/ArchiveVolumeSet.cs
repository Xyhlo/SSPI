using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbis
{
    internal sealed class ArchiveVolume
    {
        public string Url = "";
        public string Name = "";
        public string AccessType = "Unknown";
        public string Sha256 = "";
        public long Size;
    }

    internal static class ArchiveVolumeSet
    {
        public const int MaximumVolumes = 512;
        static readonly Regex NewName = new Regex(@"^(.*)\.part(\d+)\.rar$", RegexOptions.IgnoreCase);
        static readonly Regex OldName = new Regex(@"^(.*)\.(rar|r\d{2,3}|[s-z]\d{2})$", RegexOptions.IgnoreCase);
        static readonly Regex RepeatedRarExtension = new Regex(@"^(.+\.part\d+\.rar)(?:\.rar)+$", RegexOptions.IgnoreCase);

        internal static string DecoderName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 240) return name;
            Match match = RepeatedRarExtension.Match(name);
            return match.Success ? match.Groups[1].Value : name;
        }

        public static string FileName(string url, string label)
        {
            Uri uri;
            string name = Uri.TryCreate(url, UriKind.Absolute, out uri)
                ? Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)) : "";
            string set; int index;
            if (TryIndex(name, out set, out index)) return DecoderName(name);
            name = (label ?? "").Trim();
            return TryIndex(name, out set, out index) ? DecoderName(name) : "";
        }

        public static bool TryIndex(string name, out string set, out int index)
        {
            set = ""; index = 0;
            if (string.IsNullOrEmpty(name) || name.Length > 240 ||
                name.IndexOfAny(new[] { '/', '\\', ':', '\0', '\r', '\n' }) >= 0) return false;
            name = DecoderName(name);
            Match match = NewName.Match(name);
            if (match.Success)
            {
                if (!int.TryParse(match.Groups[2].Value, out index) || index < 1) return false;
                set = match.Groups[1].Value + ".part*.rar";
                return true;
            }
            match = OldName.Match(name);
            if (!match.Success) return false;
            set = match.Groups[1].Value + ".r*";
            string extension = match.Groups[2].Value;
            index = extension.Equals("rar", StringComparison.OrdinalIgnoreCase)
                ? 1
                : (char.ToLowerInvariant(extension[0]) - 'r') * 100 +
                    int.Parse(extension.Substring(1), CultureInfo.InvariantCulture) + 2;
            return true;
        }

        public static List<ArchiveVolume> Validate(IEnumerable<ArchiveVolume> input)
        {
            var volumes = new List<ArchiveVolume>(input ?? new ArchiveVolume[0]);
            if (volumes.Count == 0 || volumes.Count > MaximumVolumes)
                throw new InvalidDataException("Invalid archive volume count");
            string expectedSet = null;
            var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (ArchiveVolume volume in volumes)
            {
                string set; int index; Uri uri;
                if (volume == null || !TryIndex(volume.Name, out set, out index) ||
                    !Uri.TryCreate(volume.Url, UriKind.Absolute, out uri) ||
                    (uri.Scheme != "https" && uri.Scheme != "http") || volume.Size < 0)
                    throw new InvalidDataException("Invalid archive volume metadata");
                if (expectedSet == null) expectedSet = set;
                if (!string.Equals(set, expectedSet, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Archive volumes belong to different sets");
                if (indexes.ContainsKey(volume.Name)) throw new InvalidDataException("Duplicate archive volume");
                indexes.Add(volume.Name, index);
                if (!string.IsNullOrEmpty(volume.Sha256) && !Regex.IsMatch(volume.Sha256, @"\A[0-9a-fA-F]{64}\z"))
                    throw new InvalidDataException("Invalid volume SHA-256");
            }
            volumes.Sort((a, b) => indexes[a.Name].CompareTo(indexes[b.Name]));
            for (int i = 0; i < volumes.Count; i++)
                if (indexes[volumes[i].Name] != i + 1)
                    throw new InvalidDataException("Missing archive volume " + (i + 1));
            return volumes;
        }

        public static string Encode(IEnumerable<ArchiveVolume> input)
        {
            var text = new StringBuilder();
            foreach (ArchiveVolume volume in Validate(input))
            {
                foreach (string field in new[] { volume.Url, volume.Name, volume.AccessType, volume.Sha256 })
                    text.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(field ?? ""))).Append('|');
                text.Append(volume.Size.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            return text.ToString();
        }

        public static List<ArchiveVolume> Decode(string text)
        {
            if (string.IsNullOrEmpty(text)) return new List<ArchiveVolume>();
            if (text.Length > 4 * 1024 * 1024) throw new InvalidDataException("Archive metadata too large");
            var volumes = new List<ArchiveVolume>();
            foreach (string line in text.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Split('|');
                if (fields.Length != 5) throw new InvalidDataException("Invalid archive volume record");
                volumes.Add(new ArchiveVolume
                {
                    Url = DecodeField(fields[0]), Name = DecodeField(fields[1]),
                    AccessType = DecodeField(fields[2]), Sha256 = DecodeField(fields[3]),
                    Size = long.Parse(fields[4], CultureInfo.InvariantCulture)
                });
                if (volumes.Count > MaximumVolumes) throw new InvalidDataException("Too many archive volumes");
            }
            return Validate(volumes);
        }

        static string DecodeField(string value)
        {
            return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value));
        }
    }

    /// <summary>RAR volume structure, read from a volume's own headers without a password and
    /// without decoding. A part whose upload or copy stopped early fails this check at once,
    /// while the decoder reports it only after decoding everything before the gap: as an
    /// unreadable archive, or as a data check failure that on an encrypted file reads like a
    /// wrong password. Encrypted headers cannot be read without the password, so such a volume
    /// stays undecided. gs_rar_volume_inspect in resident/plugin/gs_archive_job.inc reads
    /// volumes the same way; keep the two in step.</summary>
    internal static class RarVolumes
    {
        internal const int Unknown = 0, Complete = 1, Truncated = 2;

        internal sealed class Volume
        {
            public int State, Format;   // Format 4: RAR 1.5-4.x, 5: RAR 5.0, 0: neither signature
            public bool More;           // Complete: the volume says another one follows it
            public bool Encrypted;      // file data or headers are encrypted
            public bool Unchecked;      // something encrypted has no RAR 5.0 password check value
            public long Size, Needed;   // length; Truncated: end of the first block past it (0: unknown)
        }

        static uint Crc32(byte[] data, int offset, int length)
        {
            uint crc = 0xffffffffu;
            for (int i = 0; i < length; i++)
            {
                crc ^= data[offset + i];
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1u)));
            }
            return ~crc;
        }

        // RAR 5.0 variable-length integer at data[at]; false when it does not end before limit.
        static bool VInt(byte[] data, int limit, ref int at, out ulong value)
        {
            value = 0;
            for (int shift = 0; at < limit && shift < 64; shift += 7)
            {
                byte b = data[at++];
                value |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) return true;
            }
            return false;
        }

        static uint Le32(byte[] data, int at)
        { return (uint)(data[at] | data[at + 1] << 8 | data[at + 2] << 16 | data[at + 3] << 24); }

        // RAR 5.0 encryption parameters start with a version and flags; flag 1 stores the
        // password check value that the decoder tests before it decodes anything.
        static void CryptFlags(byte[] data, int limit, int at, Volume volume)
        {
            ulong version, flags;
            volume.Encrypted = true;
            if (!VInt(data, limit, ref at, out version) || !VInt(data, limit, ref at, out flags) || (flags & 1) == 0) volume.Unchecked = true;
        }

        static bool ReadAt(Stream stream, long offset, byte[] buffer, int length)
        {
            stream.Position = offset;
            for (int done = 0; done < length;)
            {
                int read = stream.Read(buffer, done, length - done);
                if (read <= 0) return false;
                done += read;
            }
            return true;
        }

        /// <summary>The volume's structure, or null when the file cannot be opened or read.</summary>
        internal static Volume Inspect(string path)
        {
            const int HeaderLimit = 65536;
            var volume = new Volume();
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long size = stream.Length, pos;
                    var header = new byte[HeaderLimit];
                    bool split = false;
                    volume.Size = size;
                    if (size >= 8 && ReadAt(stream, 0, header, 8) && header[0] == 0x52 && header[1] == 0x61 && header[2] == 0x72 &&
                        header[3] == 0x21 && header[4] == 0x1a && header[5] == 0x07)
                    {
                        if (header[6] == 1 && header[7] == 0) volume.Format = 5;
                        else if (header[6] == 0) volume.Format = 4;
                    }
                    pos = volume.Format == 5 ? 8 : 7;
                    for (int blocks = 0; volume.Format != 0 && blocks < 4096; blocks++)
                    {
                        ulong data = 0;
                        if (volume.Format == 5)
                        {
                            // CRC32, header size (at most 3 bytes), header; data follows. Every volume
                            // ends with an end of archive header, so running out before it is a gap.
                            if (size - pos < 7) { volume.State = Truncated; volume.Needed = pos < size ? pos + 7 : 0; break; }
                            if (!ReadAt(stream, pos, header, 7)) break;
                            uint crc = Le32(header, 0); int at = 4; ulong length, type, flags, extra = 0;
                            // A block type other than 1-5 is not a RAR 5.0 header: undecided, never short.
                            if (!VInt(header, 7, ref at, out length) || length == 0 || length > 2 * 1024 * 1024 ||
                                (at < 7 && (header[at] == 0 || header[at] > 5))) break;
                            long headerEnd = pos + at + (long)length;
                            if (headerEnd > size) { volume.State = Truncated; volume.Needed = headerEnd; break; }
                            int span = at - 4 + (int)length;
                            if (span > HeaderLimit || !ReadAt(stream, pos + 4, header, span) || Crc32(header, 0, span) != crc) break;
                            at -= 4;
                            if (!VInt(header, span, ref at, out type) || !VInt(header, span, ref at, out flags) ||
                                ((flags & 1) != 0 && !VInt(header, span, ref at, out extra)) ||
                                ((flags & 2) != 0 && !VInt(header, span, ref at, out data)) || extra > (ulong)(span - at)) break;
                            if (type == 4) { CryptFlags(header, span, at, volume); break; } // the rest needs the password
                            if (type == 5)
                            {
                                ulong end;
                                if (!VInt(header, span, ref at, out end)) break;
                                volume.More = (end & 1) != 0; volume.State = Complete; break;
                            }
                            if (type == 2 && (flags & 1) != 0)
                            {
                                // Extra area records: size, type, data. Type 1 holds encryption parameters.
                                for (int record = span - (int)extra; record < span;)
                                {
                                    int field = record; ulong recordSize, recordType;
                                    if (!VInt(header, span, ref field, out recordSize) || recordSize == 0 || recordSize > (ulong)(span - field)) break;
                                    record = field + (int)recordSize;
                                    if (VInt(header, record, ref field, out recordType) && recordType == 1) CryptFlags(header, record, field, volume);
                                }
                            }
                            pos = headerEnd;
                        }
                        else
                        {
                            // CRC16 (low half of CRC32), type, flags, header size; data follows. RAR 2.x
                            // volumes have no end of archive header, so a file may end after a block.
                            if (pos == size) { volume.State = Complete; volume.More = split; break; }
                            if (size - pos < 7) { volume.State = Truncated; volume.Needed = pos + 7; break; }
                            if (!ReadAt(stream, pos, header, 7)) break;
                            int crc = header[0] | header[1] << 8, type = header[2], flags = header[3] | header[4] << 8;
                            int length = header[5] | header[6] << 8;
                            bool check = false;
                            if (length < 7 || type < 0x72 || type > 0x7b) break; // not a RAR 1.5-4.x block: undecided
                            if (pos + length > size) { volume.State = Truncated; volume.Needed = pos + length; break; }
                            if (!ReadAt(stream, pos, header, length)) break;
                            if (type == 0x73) check = (flags & 0x2) == 0;            // main header; an old comment is outside its CRC
                            else if (type == 0x74 || type == 0x7a)                   // file or service header: packed size
                            {
                                if (length < ((flags & 0x100) != 0 ? 40 : 32)) break;
                                data = Le32(header, 7) | ((flags & 0x100) != 0 ? (ulong)Le32(header, 32) << 32 : 0);
                                check = (flags & 0x8) == 0;
                            }
                            else if (type == 0x7b) check = (flags & 0x4) == 0;       // end of archive; recovery volumes zero its tail
                            else if (type == 0x77 || type == 0x78 || (flags & 0x8000) != 0)
                            {
                                if (length < 11) break;
                                data = Le32(header, 7);
                            }
                            if (check && (Crc32(header, 2, length - 2) & 0xffffu) != (uint)crc) break;
                            if (type == 0x73 && (flags & 0x80) != 0) { volume.Encrypted = volume.Unchecked = true; break; } // encrypted headers
                            if (type == 0x74) { split = (flags & 0x2) != 0; if ((flags & 0x4) != 0) volume.Encrypted = volume.Unchecked = true; }
                            if (type == 0x7b) { volume.More = (flags & 1) != 0; volume.State = Complete; break; }
                            pos += length;
                        }
                        if (data > (ulong)(long.MaxValue - pos)) break;
                        if (pos + (long)data > size) { volume.State = Truncated; volume.Needed = pos + (long)data; break; }
                        pos += (long)data;
                    }
                }
                return volume;
            }
            catch (Exception) { return null; }
        }

        // Decimal units, as the Downloads page shows sizes; same digits as the resident.
        static string Bytes(long bytes)
        {
            return bytes >= 1000000000
                ? (bytes / 1000000000).ToString(CultureInfo.InvariantCulture) + "." +
                    (bytes % 1000000000 / 10000000).ToString("00", CultureInfo.InvariantCulture) + " GB"
                : (bytes / 1000000).ToString(CultureInfo.InvariantCulture) + "." +
                    (bytes % 1000000 / 100000).ToString(CultureInfo.InvariantCulture) + " MB";
        }

        /// <summary>After a failed RAR extraction: describes a supplied part that is shorter than
        /// its headers declare, or the part after the last one when that one says the set goes on;
        /// null when no part shows such a problem. When no part's headers can be read (encrypted
        /// headers), parts are compared by size instead: RAR writes every part except the last at
        /// the same size. verified: everything encrypted is RAR 5.0 with a password check value,
        /// which the decoder tests before decoding, so a later data check failure is damage, not
        /// a wrong password. Same rules and words as gs_rar_set_problem in the resident.</summary>
        internal static string SetProblem(IList<string> paths, bool inbox, out bool verified)
        {
            verified = false;
            if (paths == null || paths.Count == 0 || paths.Count > ArchiveVolumeSet.MaximumVolumes) return null;
            var sizes = new long[paths.Count];
            int part = 0, total = 0, missing = 0;
            bool readable = false, unreadable = false, encrypted = false, checkedAll = true, rar5 = true;
            long size = 0, needed = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                Volume volume = Inspect(paths[i]);
                if (volume == null) return null;
                sizes[i] = volume.Size; encrypted |= volume.Encrypted;
                if (volume.Unchecked) checkedAll = false;
                if (volume.Format != 5) rar5 = false;
                if (volume.State != Unknown) readable = true;
                else if (!volume.Encrypted) unreadable = true;
                if (volume.State == Truncated && part == 0) { part = i + 1; size = volume.Size; needed = volume.Needed; }
                if (volume.State == Complete && !volume.More && total == 0) total = i + 1;
                if (i == paths.Count - 1 && volume.State == Complete && volume.More) missing = paths.Count + 1;
            }
            if (part == 0 && !readable && !unreadable)
            {
                long largest = 0;
                foreach (long value in sizes) largest = Math.Max(largest, value);
                for (int i = 0; i < sizes.Length - 1 && part == 0; i++)
                    if (sizes[i] < largest) { part = i + 1; size = sizes[i]; needed = largest; }
            }
            verified = rar5 && encrypted && checkedAll && !unreadable;
            string action = inbox ? "Upload" : "Copy or download";
            if (part > 0)
            {
                string of = total > 0 ? " of " + total.ToString(CultureInfo.InvariantCulture) : "";
                string named = "RAR part " + part.ToString(CultureInfo.InvariantCulture) + of + " is incomplete: ";
                return named + (needed > size ? Bytes(size) + " of at least " + Bytes(needed) : "its end is missing") +
                    ". " + action + " that part again, then retry";
            }
            if (missing > 0)
                return "RAR part " + missing.ToString(CultureInfo.InvariantCulture) + " is missing: part " +
                    (missing - 1).ToString(CultureInfo.InvariantCulture) + " says the set continues. " +
                    (inbox ? "Upload" : "Supply") + " every part, then retry";
            return null;
        }

        /// <summary>A data check failure on a set whose password the RAR 5.0 check value verified.</summary>
        internal static string Damaged(bool inbox, int parts)
        {
            return "RAR data is damaged: an encrypted file failed its checksum after its key was verified. " +
                (inbox ? "Upload" : "Copy or download") + (parts > 1 ? " the parts" : " the archive") + " again, then retry";
        }

        /// <summary>FTP inbox hand-off check for the RAR set whose first volume is first, in step
        /// with gs_inbox_rar_hold in resident/plugin/gs_ftp_inbox.inc: what the set still waits
        /// for, or null when every present part holds what its headers declare and the last one
        /// says the set ends there, or when the headers cannot be read (the quiet period then
        /// decides). encrypted: a part is encrypted; the resident worker leaves such a set to the
        /// application, which supplies archive passwords.</summary>
        internal static string InboxHold(string first, out bool encrypted)
        {
            encrypted = false;
            List<string> parts = InboxParts(first);
            bool more = false;
            for (int i = 0; i < parts.Count; i++)
            {
                Volume volume = Inspect(parts[i]);
                if (volume == null) return null;
                encrypted |= volume.Encrypted;
                if (volume.State == Truncated) return "Waiting for part " + (i + 1).ToString(CultureInfo.InvariantCulture) + " to finish uploading";
                more = volume.State == Complete && volume.More;
                if (volume.State == Complete && !more) return null;
            }
            return more ? "Waiting for part " + (parts.Count + 1).ToString(CultureInfo.InvariantCulture) : null;
        }

        // Present, non-empty parts 1, 2, ... of first's set, up to the first gap, matched as
        // LocalInstallSource discovers volumes for extraction.
        static List<string> InboxParts(string first)
        {
            var result = new List<string>();
            string expected; int firstIndex;
            try
            {
                if (!ArchiveVolumeSet.TryIndex(Path.GetFileName(first), out expected, out firstIndex) || firstIndex != 1) return result;
                var found = new Dictionary<int, string>();
                int scanned = 0;
                foreach (string candidate in Directory.EnumerateFiles(Path.GetDirectoryName(first)))
                {
                    if (++scanned > 8192) break;
                    string set; int index;
                    if (!ArchiveVolumeSet.TryIndex(Path.GetFileName(candidate), out set, out index) || index > ArchiveVolumeSet.MaximumVolumes ||
                        !string.Equals(set, expected, StringComparison.OrdinalIgnoreCase) || found.ContainsKey(index)) continue;
                    if (new FileInfo(candidate).Length > 0) found.Add(index, candidate);
                }
                for (int index = 1; found.ContainsKey(index); index++) result.Add(found[index]);
            }
            catch (Exception) { result.Clear(); }
            return result;
        }
    }
}
