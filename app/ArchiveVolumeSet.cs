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
        static readonly Regex OldName = new Regex(@"^(.*)\.(rar|r\d{2,3})$", RegexOptions.IgnoreCase);

        public static string FileName(string url, string label)
        {
            Uri uri;
            string name = Uri.TryCreate(url, UriKind.Absolute, out uri)
                ? Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)) : "";
            string set; int index;
            if (TryIndex(name, out set, out index)) return name;
            name = (label ?? "").Trim();
            return TryIndex(name, out set, out index) ? name : "";
        }

        public static bool TryIndex(string name, out string set, out int index)
        {
            set = ""; index = 0;
            if (string.IsNullOrEmpty(name) || name.Length > 240 ||
                name.IndexOfAny(new[] { '/', '\\', ':', '\0', '\r', '\n' }) >= 0) return false;
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
            index = match.Groups[2].Value.Equals("rar", StringComparison.OrdinalIgnoreCase)
                ? 1 : int.Parse(match.Groups[2].Value.Substring(1), CultureInfo.InvariantCulture) + 2;
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
}
