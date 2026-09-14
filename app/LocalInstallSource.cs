using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbis
{
    internal sealed class LocalInstallSource
    {
        internal string Path, TitleId = "", ContentId = "", Name = "", Version = "", Kind = "archive", Fingerprint = "", ImagePath = "";
        internal long Size;
        internal int ExpectedKind;

        internal static bool TryNormalizePath(string value, out string canonical)
        {
            canonical = null;
            if (string.IsNullOrEmpty(value) || value.Length >= 1024 || value.IndexOf('\\') >= 0 || value.IndexOf(':') >= 0 ||
                !value.StartsWith("/mnt/usb", StringComparison.Ordinal) || value.Length < 11 ||
                value[8] < '0' || value[8] > '7' || value[9] != '/') return false;
            foreach (char c in value) if (char.IsControl(c)) return false;
            if (Encoding.UTF8.GetByteCount(value) >= 1024) return false;
            string[] parts = value.Split('/');
            for (int i = 1; i < parts.Length; i++)
                if (parts[i].Length == 0 || parts[i] == "." || parts[i] == ".." || Encoding.UTF8.GetByteCount(parts[i]) > 240) return false;
            canonical = value;
            return true;
        }

        internal static void RequireAvailable(string path)
        {
            string canonical;
            if (!TryNormalizePath(path, out canonical)) throw new IOException("Choose a USB file with no linked or parent paths, colons, or folder names longer than 240 UTF-8 bytes");
            string current = canonical.Substring(0, 9);
            string detail;
            if (!UsbVolumeLabel.IsConnected(current, out detail)) { SspiLog.Write("download", "usb-storage " + detail); throw new IOException(detail); }
            for (;;)
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked USB files and folders are not supported");
                if (current == canonical)
                {
                    if ((attributes & FileAttributes.Directory) != 0) throw new IOException("Select a PKG, ZIP or first RAR volume");
                    return;
                }
                if ((attributes & FileAttributes.Directory) == 0) throw new IOException("USB source folder is unavailable");
                int next = canonical.IndexOf('/', current.Length + 1);
                current = next < 0 ? canonical : canonical.Substring(0, next);
            }
        }

        internal static LocalInstallSource Read(string path, bool cacheIcon)
        {
            RequireAvailable(path);
            return ReadMetadata(path, cacheIcon);
        }

        internal static List<string> ArchivePaths(string first)
        {
            RequireAvailable(first);
            List<string> paths = DiscoverArchivePaths(first);
            foreach (string path in paths) RequireAvailable(path);
            return paths;
        }

        static List<string> DiscoverArchivePaths(string first)
        {
            PackageObjectKind kind = PackageArchive.Detect(first);
            if (kind != PackageObjectKind.Rar4 && kind != PackageObjectKind.Rar5) return new List<string> { first };
            string expectedSet; int firstIndex;
            if (!ArchiveVolumeSet.TryIndex(System.IO.Path.GetFileName(first), out expectedSet, out firstIndex) || firstIndex != 1)
                throw new IOException("Select the first RAR volume");
            var volumes = new SortedDictionary<int, string>();
            int scanned = 0;
            foreach (string candidate in Directory.EnumerateFiles(System.IO.Path.GetDirectoryName(first)))
            {
                if (++scanned > 8192) throw new IOException("Move the RAR volumes into a folder with fewer than 8192 files");
                string set; int index;
                if (!ArchiveVolumeSet.TryIndex(System.IO.Path.GetFileName(candidate), out set, out index) ||
                    !string.Equals(set, expectedSet, StringComparison.OrdinalIgnoreCase)) continue;
                if (index > ArchiveVolumeSet.MaximumVolumes || volumes.ContainsKey(index))
                    throw new IOException("RAR volume sequence is duplicated or exceeds 512 files");
                string path = candidate.Replace('\\', '/');
                volumes.Add(index, path);
            }
            var paths = new List<string>();
            foreach (var volume in volumes)
            {
                if (volume.Key != paths.Count + 1) throw new IOException("Missing RAR volume " + (paths.Count + 1));
                paths.Add(volume.Value);
            }
            if (paths.Count == 0 || paths[0] != first.Replace('\\', '/')) throw new IOException("The selected first RAR volume changed");
            return paths;
        }

        internal static LocalInstallSource ReadMetadata(string path, bool cacheIcon)
        {
            var info = new LocalInstallSource { Path = path, Name = System.IO.Path.GetFileNameWithoutExtension(path) };
            PackageObjectKind objectKind = PackageArchive.Detect(path);
            if (objectKind != PackageObjectKind.Pkg && objectKind != PackageObjectKind.Zip &&
                objectKind != PackageObjectKind.Rar4 && objectKind != PackageObjectKind.Rar5 && objectKind != PackageObjectKind.SevenZip)
                throw new IOException("File is not a PS4 PKG, ZIP, RAR or 7z archive");
            if (objectKind == PackageObjectKind.Rar4 || objectKind == PackageObjectKind.Rar5)
            {
                string set; int index;
                if (!ArchiveVolumeSet.TryIndex(System.IO.Path.GetFileName(path), out set, out index) || index != 1)
                    throw new IOException("Select the first RAR volume; keep the remaining volumes beside it");
            }
            byte[] header;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                info.Size = input.Length;
                if (info.Size < 8 || info.Size > 8L * 1024 * 1024 * 1024 * 1024) throw new IOException("USB file size is invalid");
                header = ReadBytes(input, (int)Math.Min(4096, info.Size));
                input.Position = Math.Max(0, info.Size - 4096);
                byte[] tail = ReadBytes(input, (int)Math.Min(4096, info.Size));
                using (var hash = PkgIntegrity.CreateSha256())
                {
                    hash.TransformBlock(header, 0, header.Length, header, 0);
                    hash.TransformBlock(tail, 0, tail.Length, tail, 0);
                    byte[] size = Encoding.ASCII.GetBytes(info.Size.ToString(CultureInfo.InvariantCulture));
                    hash.TransformFinalBlock(size, 0, size.Length);
                    info.Fingerprint = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                }
            }
            if (objectKind != PackageObjectKind.Pkg) return info;
            PkgContentKind kind; string error;
            if (header.Length != 4096 || !PkgValidator.TryGetContentKindFromHeader(header, out kind, out error) ||
                !PkgValidator.TryGetContentIdFromHeader(header, out info.ContentId) || info.ContentId.Length != 36 ||
                !PkgValidator.TryGetTitleIdFromContentId(info.ContentId, out info.TitleId))
                throw new IOException("PKG package identity is invalid");
            info.Kind = kind == PkgContentKind.BaseGame ? "game" : kind == PkgContentKind.Patch ? "update" : "dlc";
            info.ExpectedKind = kind == PkgContentKind.BaseGame ? 6 : kind == PkgContentKind.Patch ? 8 : 7;
            if (!PkgIntegrity.CheckMetadata(path, info.TitleId, info.ExpectedKind == 6 ? "gd" : info.ExpectedKind == 8 ? "gp" : "ac", out error))
                throw new IOException(error ?? "PKG metadata is invalid");
            byte[] sfo = PkgIntegrity.Entry(path, 0x1000);
            string title = PkgIntegrity.SfoValue(sfo, "TITLE");
            if (!string.IsNullOrWhiteSpace(title)) info.Name = title.Length > 240 ? title.Substring(0, 240) : title;
            info.Version = PkgIntegrity.SfoValue(sfo, "APP_VER");
            System.Version version;
            if (info.ExpectedKind != 7 && !System.Version.TryParse(info.Version, out version))
                throw new IOException("PKG application version is invalid");
            // PKGs do not contain an authoritative backport flag. A filename label
            // affects ordering only; distinct container fingerprints stay distinct.
            if (info.ExpectedKind == 8 && Regex.IsMatch(System.IO.Path.GetFileName(path), @"(?:^|[^A-Za-z0-9])backport(?:[^A-Za-z0-9]|$)", RegexOptions.IgnoreCase)) info.Kind = "backport";
            if (cacheIcon) info.ImagePath = CacheIcon(path, info.Fingerprint);
            return info;
        }

        static byte[] ReadBytes(Stream input, int count)
        {
            byte[] data = new byte[count]; int at = 0;
            while (at < count) { int n = input.Read(data, at, count - at); if (n <= 0) throw new IOException("USB file ended unexpectedly"); at += n; }
            return data;
        }

        static string CacheIcon(string source, string fingerprint)
        {
            try
            {
                byte[] icon = PkgIntegrity.Entry(source, 0x1200);
                if (icon == null || icon.Length < 24 || icon.Length > 512 * 1024 ||
                    icon[0] != 137 || Encoding.ASCII.GetString(icon, 1, 3) != "PNG" ||
                    Encoding.ASCII.GetString(icon, 12, 4) != "IHDR") return "";
                uint width = PkgIntegrity.Be32(icon, 16), height = PkgIntegrity.Be32(icon, 20);
                if (width == 0 || height == 0 || width > 1024 || height > 1024) return "";
                string directory = System.IO.Path.Combine(AppSettings.DataDir, "cache", "usb-icons");
                Directory.CreateDirectory(directory);
                string target = System.IO.Path.Combine(directory, fingerprint + ".png");
                if (!File.Exists(target)) File.WriteAllBytes(target, icon);
                return target;
            }
            catch { return ""; }
        }
    }
}
