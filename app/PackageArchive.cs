using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using SharpCompress.Archives.Rar;
using SharpCompress.Readers;

namespace Orbis
{
    internal enum PackageObjectKind
    {
        Unknown,
        Pkg,
        Zip,
        Rar4,
        Rar5
    }

    internal sealed class PackageArchiveEntry
    {
        public string Name = "";
        public long CompressedSize;
        public long Size;
        public string ExtractedPath = "";
    }

    internal static class PackageArchive
    {
        const int MaximumArchiveEntries = 4096;
        const int MaximumPackageEntries = 256;
        const long MaximumExpandedBytes = 512L * 1024 * 1024 * 1024;
        static readonly byte[] Rar4Magic = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00 };
        static readonly byte[] Rar5Magic = { 0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00 };
        static readonly uint[] CrcTable = BuildCrcTable();

        public static PackageObjectKind Detect(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return PackageObjectKind.Unknown;
            byte[] header = new byte[8];
            int read;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                read = input.Read(header, 0, header.Length);
            if (read >= 4 && header[0] == 0x7F && header[1] == 0x43 &&
                header[2] == 0x4E && header[3] == 0x54) return PackageObjectKind.Pkg;
            if (read >= 4 && header[0] == 0x50 && header[1] == 0x4B &&
                ((header[2] == 0x03 && header[3] == 0x04) ||
                 (header[2] == 0x05 && header[3] == 0x06) ||
                 (header[2] == 0x07 && header[3] == 0x08))) return PackageObjectKind.Zip;
            if (StartsWith(header, read, Rar5Magic)) return PackageObjectKind.Rar5;
            if (StartsWith(header, read, Rar4Magic)) return PackageObjectKind.Rar4;
            return PackageObjectKind.Unknown;
        }

        public static List<PackageArchiveEntry> ListPackages(string archivePath)
        {
            PackageObjectKind kind = Detect(archivePath);
            
            if (kind == PackageObjectKind.Zip) return ReadZip(archivePath, null);
            if (kind == PackageObjectKind.Rar4 || kind == PackageObjectKind.Rar5)
                return ReadRar(new[] { archivePath }, null, null, null);
            throw new InvalidDataException("Not a PKG or archive");
        }

        public static List<PackageArchiveEntry> ExtractPackages(string archivePath,
            string destinationDirectory)
        {
            if (string.IsNullOrEmpty(destinationDirectory))
                throw new ArgumentException("Extraction directory is empty");
            string destination = Path.GetFullPath(destinationDirectory);
            Directory.CreateDirectory(destination);
            PackageObjectKind kind = Detect(archivePath);
            
            if (kind == PackageObjectKind.Zip) return ReadZip(archivePath, destination);
            if (kind == PackageObjectKind.Rar4 || kind == PackageObjectKind.Rar5)
                return ReadRar(new[] { archivePath }, destination, null, null);
            throw new InvalidDataException("Not a PKG or archive");
        }

        static List<PackageArchiveEntry> ReadZip(string archivePath, string destination,
            Func<bool> cancel = null, Action<long, long> progress = null)
        {
            var result = new List<PackageArchiveEntry>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var created = new List<string>();
            long expanded = 0;
            int archiveEntries = 0;
            try
            {
                using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var archive = new ZipArchive(input, ZipArchiveMode.Read, false))
                {
                    foreach (ZipArchiveEntry item in archive.Entries)
                    {
                        CheckCancel(cancel);
                        if (++archiveEntries > MaximumArchiveEntries)
                            throw new InvalidDataException("Archive contains too many entries");
                        string name = ValidateEntryName(item.FullName);
                        if (name.EndsWith("/", StringComparison.Ordinal) ||
                            !name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!names.Add(name))
                            throw new InvalidDataException("Duplicate PKG archive entry: " + name);
                        if (result.Count >= MaximumPackageEntries)
                            throw new InvalidDataException("Archive contains too many PKG files");
                        AddExpanded(ref expanded, item.Length);
                        var entry = new PackageArchiveEntry
                        {
                            Name = name,
                            CompressedSize = item.CompressedLength,
                            Size = item.Length
                        };
                        if (destination != null)
                        {
                            ArchiveStorage.RequireFreeSpace(destination, checked(item.Length + 64L * 1024 * 1024));
                            string output = OutputPath(destination, result.Count + 1);
                            string partial = output + ".part";
                            created.Add(partial);
                            long written = 0;
                            using (Stream source = item.Open())
                            using (var target = new FileStream(partial, FileMode.CreateNew,
                                FileAccess.Write, FileShare.None))
                            {
                                byte[] buffer = new byte[128 * 1024];
                                int count;
                                while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    CheckCancel(cancel);
                                    target.Write(buffer, 0, count);
                                    written += count;
                                    if (progress != null) progress(written, item.Length);
                                    if (written > item.Length)
                                        throw new InvalidDataException("ZIP entry exceeds its declared size: " + name);
                                }
                            }
                            if (written != item.Length)
                                throw new InvalidDataException("ZIP entry is truncated: " + name);
                            RequirePkgMagic(partial, name);
                            File.Move(partial, output);
                            created.Remove(partial);
                            created.Add(output);
                            entry.ExtractedPath = output;
                        }
                        result.Add(entry);
                    }
                }
                if (result.Count == 0) throw new InvalidDataException("Archive contains no PKG files");
                return result;
            }
            catch
            {
                Cleanup(created);
                throw;
            }
        }

        public static List<PackageArchiveEntry> ExtractPackages(IList<string> paths,
            string destination, Func<bool> cancel, Action<long, long> progress, string password = null)
        {
            if (paths == null || paths.Count == 0) throw new InvalidDataException("Missing archive volume 1");
            CheckCancel(cancel);
            Directory.CreateDirectory(destination);
            PackageObjectKind kind = Detect(paths[0]);
            if (kind == PackageObjectKind.Rar4 || kind == PackageObjectKind.Rar5)
                return ReadRar(paths, Path.GetFullPath(destination), cancel, progress, password);
            if (paths.Count == 1 && kind == PackageObjectKind.Zip)
                return ReadZip(paths[0], Path.GetFullPath(destination), cancel, progress);
            throw new InvalidDataException("Not a supported archive");
        }

        static void CheckCancel(Func<bool> cancel)
        {
            if (cancel != null && cancel()) throw new OperationCanceledException();
        }

        static List<PackageArchiveEntry> ReadRar(IEnumerable<string> paths, string destination,
            Func<bool> cancel, Action<long, long> progress, string password = null)
        {
            var streams = new List<Stream>();
            var created = new List<string>();
            try
            {
                foreach (string path in paths)
                {
                    CheckCancel(cancel);
                    if (!File.Exists(path)) throw new InvalidDataException("Missing archive volume");
                    streams.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                    if (streams.Count > 512) throw new InvalidDataException("Too many archive volumes");
                }
                using (var archive = RarArchive.OpenArchive(streams, new ReaderOptions { LeaveStreamOpen = true, Password = password }))
                {
                    var result = new List<PackageArchiveEntry>();
                    var selected = new Dictionary<string, PackageArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    long expanded = 0;
                    int entries = 0;
                    foreach (var item in archive.Entries)
                    {
                        CheckCancel(cancel);
                        if (++entries > MaximumArchiveEntries) throw new InvalidDataException("Archive contains too many entries");
                        string name = ValidateEntryName(item.Key);
                        if (!item.IsComplete) throw new InvalidDataException("Missing or truncated RAR volume: " + name);
                        if (item.IsEncrypted && string.IsNullOrEmpty(password)) throw new InvalidDataException("Archive password required; refresh this package from its source");
                        if (item.IsDirectory) continue;
                        if (!names.Add(name)) throw new InvalidDataException("Duplicate archive entry: " + name);
                        // Solid decoding also consumes non-PKG entries. Bound their expansion too.
                        AddExpanded(ref expanded, item.Size);
                        if (!name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)) continue;
                        if (result.Count >= MaximumPackageEntries) throw new InvalidDataException("Archive contains too many PKG files");
                        var entry = new PackageArchiveEntry { Name = name, Size = item.Size, CompressedSize = item.CompressedSize };
                        result.Add(entry);
                        selected.Add(name, entry);
                    }
                    if (result.Count == 0) throw new InvalidDataException("Archive contains no PKG files");
                    if (destination == null) return result;
                    long required = 0;
                    foreach (var entry in result) required = checked(required + entry.Size);
                    ArchiveStorage.RequireFreeSpace(destination, checked(required + 64L * 1024 * 1024));
                    long writtenTotal = 0;
                    int extracted = 0;
                    // Sequential extraction preserves the RAR solid dictionary across entries/volumes.
                    foreach (var stream in streams) stream.Position = 0;
                    using (var reader = SharpCompress.Readers.Rar.RarReader.OpenReader(streams, new ReaderOptions { LeaveStreamOpen = true, Password = password }))
                    {
                        while (reader.MoveToNextEntry())
                        {
                            CheckCancel(cancel);
                            PackageArchiveEntry entry;
                            if (reader.Entry.IsDirectory || !selected.TryGetValue(
                                ValidateEntryName(reader.Entry.Key), out entry)) continue;
                            string output = OutputPath(destination, ++extracted);
                            string partial = output + ".part";
                            created.Add(partial);
                            long written = 0;
                            uint crc = 0xFFFFFFFF;
                            using (Stream input = reader.OpenEntryStream())
                            using (var target = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            {
                                byte[] buffer = new byte[128 * 1024];
                                int count;
                                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    CheckCancel(cancel);
                                    if (count > entry.Size - written) throw new InvalidDataException("RAR entry exceeds declared size");
                                    target.Write(buffer, 0, count);
                                    crc = UpdateCrc(crc, buffer, 0, count);
                                    written += count;
                                    writtenTotal += count;
                                    if (progress != null) progress(writtenTotal, required);
                                }
                            }
                            if (written != entry.Size) throw new InvalidDataException("Truncated RAR entry: " + entry.Name);
                            // The reader validates RAR data checksums; compare CRC32 explicitly as well.
                            if (reader.Entry.Crc != 0 && ~crc != unchecked((uint)reader.Entry.Crc))
                                throw new InvalidDataException("RAR entry CRC mismatch: " + entry.Name);
                            RequirePkgMagic(partial, entry.Name);
                            File.Move(partial, output);
                            created.Remove(partial);
                            created.Add(output);
                            entry.ExtractedPath = output;
                        }
                    }
                    if (extracted != result.Count) throw new InvalidDataException("RAR extraction is incomplete");
                    return result;
                }
            }
            catch
            {
                Cleanup(created);
                throw;
            }
            finally
            {
                foreach (Stream stream in streams) stream.Dispose();
            }
        }

        static string ValidateEntryName(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 1024 || value.IndexOf('\0') >= 0)
                throw new InvalidDataException("Invalid archive entry name");
            string path = value.Replace('\\', '/');
            if (path.StartsWith("/", StringComparison.Ordinal) || path.IndexOf(':') >= 0)
                throw new InvalidDataException("Invalid archive entry path: " + value);
            string[] parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i] == "." || parts[i] == ".." || (parts[i].Length == 0 && i != parts.Length - 1))
                    throw new InvalidDataException("Invalid archive entry path: " + value);
            return path;
        }

        static string OutputPath(string destination, int ordinal)
        {
            string path = Path.Combine(destination,
                "pkg-" + ordinal.ToString("D3", CultureInfo.InvariantCulture) + ".pkg");
            if (File.Exists(path) || File.Exists(path + ".part"))
                throw new IOException("Extraction target already exists: " + path);
            return path;
        }

        static void RequirePkgMagic(string path, string entryName)
        {
            byte[] magic = new byte[4];
            int read;
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                read = input.Read(magic, 0, magic.Length);
            if (read != 4 || magic[0] != 0x7F || magic[1] != 0x43 ||
                magic[2] != 0x4E || magic[3] != 0x54)
                throw new InvalidDataException("Archive entry is not a PKG: " + entryName);
        }

        static void AddExpanded(ref long total, long size)
        {
            if (size < 0 || size > MaximumExpandedBytes - total)
                throw new InvalidDataException("Archive PKG data exceeds 512 GiB");
            total += size;
        }

        static long CheckedEnd(long start, ulong count, long fileLength)
        {
            if (count > (ulong)long.MaxValue || start < 0 || (ulong)start + count > (ulong)fileLength)
                throw new InvalidDataException("RAR4 entry is truncated");
            return start + (long)count;
        }

        static void CopyExactly(Stream input, Stream output, long count, ref uint crc)
        {
            byte[] buffer = new byte[128 * 1024];
            while (count > 0)
            {
                int wanted = (int)Math.Min(buffer.Length, count);
                int read = input.Read(buffer, 0, wanted);
                if (read <= 0) throw new EndOfStreamException("RAR4 entry is truncated");
                output.Write(buffer, 0, read);
                crc = UpdateCrc(crc, buffer, 0, read);
                count -= read;
            }
        }

        static byte[] ReadExactly(Stream input, int count)
        {
            byte[] data = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = input.Read(data, offset, count - offset);
                if (read <= 0) throw new EndOfStreamException("Archive is truncated");
                offset += read;
            }
            return data;
        }

        static ushort U16(byte[] value, int offset)
        {
            return (ushort)(value[offset] | value[offset + 1] << 8);
        }

        static uint U32(byte[] value, int offset)
        {
            return (uint)(value[offset] | value[offset + 1] << 8 |
                value[offset + 2] << 16 | value[offset + 3] << 24);
        }

        static bool StartsWith(byte[] value, int length, byte[] prefix)
        {
            if (length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (value[i] != prefix[i]) return false;
            return true;
        }

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                    value = (value & 1) != 0 ? 0xEDB88320U ^ (value >> 1) : value >> 1;
                table[i] = value;
            }
            return table;
        }

        static uint UpdateCrc(uint crc, byte[] data, int offset, int count)
        {
            for (int i = 0; i < count; i++)
                crc = CrcTable[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }

        static void Cleanup(List<string> paths)
        {
            foreach (string path in paths)
                try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        static NotSupportedException RarNotSupported()
        {
            return new NotSupportedException("RAR not supported");
        }
    }
}
