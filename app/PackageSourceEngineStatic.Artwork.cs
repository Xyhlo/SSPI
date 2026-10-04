using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Orbis
{
    // Pictures shipped inside a static catalog. A source package holds at most 64 files, so a
    // catalog packs its pictures into a few artwork/*.bin files and each title names its slice
    // and SHA-256. Installing a source checks every slice; the cover loader reads a slice through
    // a local reference and checks it again, so pictures never need the network.
    internal static partial class PackageSourceEngineStatic
    {
        const int MaxArtworkFiles = 16;
        internal const int MaxArtworkBytes = 512 * 1024;

        sealed class ArtworkSlice
        {
            internal string File, Sha256;
            internal long Offset;
            internal int Size;
        }

        static List<string> ArtworkFiles(Dictionary<string, object> root, HashSet<string> declared)
        {
            var result = new List<string>();
            if (Value(root, "artworkFiles", false) == null) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object value in Array(root, "artworkFiles", MaxArtworkFiles, true))
            {
                string path = RelativePath(value as string);
                if (!path.StartsWith("artwork/", StringComparison.Ordinal) || !path.EndsWith(".bin", StringComparison.Ordinal) ||
                    !declared.Contains(path) || !seen.Add(path)) throw Bad("Invalid catalog artwork reference");
                result.Add(path);
            }
            return result;
        }

        static ArtworkSlice Artwork(Dictionary<string, object> row, List<string> files, Func<string, long> size)
        {
            object value = Value(row, "art", false);
            if (value == null) return null;
            var art = Object(value, "title artwork");
            string file = RelativePath(Text(art, "file", 180, true));
            if (!files.Contains(file)) throw Bad("Title artwork refers to an undeclared artwork file");
            long offset = Integer(art, "offset", true), length = Integer(art, "size", true);
            string sha = Sha(Text(art, "sha256", 64, true));
            if (length < 100 || length > MaxArtworkBytes || offset > size(file) - length) throw Bad("Title artwork is outside its artwork file");
            return new ArtworkSlice { File = file, Offset = offset, Size = (int)length, Sha256 = sha.ToLowerInvariant() };
        }

        static void VerifyArtwork(Catalog catalog, Func<string, byte[]> read)
        {
            var packs = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (Record record in catalog.Records)
            {
                if (record.Art == null) continue;
                byte[] pack;
                if (!packs.TryGetValue(record.Art.File, out pack)) packs.Add(record.Art.File, pack = read(record.Art.File));
                var slice = new byte[record.Art.Size];
                Buffer.BlockCopy(pack, (int)record.Art.Offset, slice, 0, slice.Length);
                CheckArtwork(slice, record.Art.Sha256);
            }
        }

        // "<pack path>#<offset>+<size>+<sha256>" is a local path to the cover loader.
        static string ArtworkReference(string root, ArtworkSlice art)
        {
            return Path.Combine(root, art.File.Replace('/', Path.DirectorySeparatorChar)) + "#" +
                art.Offset.ToString(CultureInfo.InvariantCulture) + "+" + art.Size.ToString(CultureInfo.InvariantCulture) + "+" + art.Sha256;
        }

        /// <summary>Reads a catalog picture. False when the path is not an artwork reference; a
        /// reference whose slice is missing, changed or not an image throws.</summary>
        internal static bool TryReadArtwork(string reference, out byte[] image)
        {
            image = null;
            int mark = (reference ?? "").LastIndexOf('#');
            if (mark <= 0) return false;
            string[] parts = reference.Substring(mark + 1).Split('+');
            long offset; int size;
            if (parts.Length != 3 || parts[2].Length != 64 ||
                !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out offset) ||
                !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out size)) return false;
            if (size < 100 || size > MaxArtworkBytes) throw new InvalidDataException("Catalog artwork size is invalid");
            var data = new byte[size];
            using (var input = new FileStream(reference.Substring(0, mark), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (offset > input.Length - size) throw new InvalidDataException("Catalog artwork is outside its file");
                input.Position = offset;
                for (int read = 0; read < size; )
                {
                    int count = input.Read(data, read, size - read);
                    if (count == 0) throw new EndOfStreamException("Catalog artwork was truncated");
                    read += count;
                }
            }
            CheckArtwork(data, parts[2]);
            image = data;
            return true;
        }

        static void CheckArtwork(byte[] data, string sha256)
        {
            if (!Hash(data).Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw Bad("Title artwork changed; import the source again");
            bool png = data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47;
            bool jpeg = data.Length > 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
            if (!png && !jpeg) throw Bad("Title artwork must be a PNG or JPEG image");
        }
    }
}
