using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
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
        Rar5,
        SevenZip
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
        static readonly byte[] SevenZipMagic = { 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C };
        static readonly uint[] CrcTable = BuildCrcTable();

        internal static bool IsSevenZipHeader(byte[] header)
        { return header != null && StartsWith(header, header.Length, SevenZipMagic); }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int NativeArchiveProgress(long done, long total);
        [DllImport("libGameSearchResident.prx", CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_7z_extract_local_ex([MarshalAs(UnmanagedType.LPStr)] string input,
            [MarshalAs(UnmanagedType.LPStr)] string destination, IntPtr packages, int capacity,
            NativeArchiveProgress progress, [Out] byte[] error, int errorCapacity);

        static List<PackageArchiveEntry> ReadSevenZip(string input, string destination,
            Func<bool> cancel = null, Action<long, long> progress = null, string password = null)
        {
            if (!string.IsNullOrEmpty(password))
                throw new NotSupportedException("Encrypted 7z archives are not supported; the archive is kept.");
            CheckCancel(cancel);
            const int stride = 1032;
            IntPtr entries = Marshal.AllocHGlobal(MaximumPackageEntries * stride);
            Exception callbackError = null;
            NativeArchiveProgress callback = (done, total) => {
                try { CheckCancel(cancel); if (progress != null) progress(done, total); return 0; }
                catch (Exception ex) { callbackError = ex; return 1; }
            };
            try
            {
                byte[] error = new byte[1024];
                int count = gs_7z_extract_local_ex(input, destination, entries, MaximumPackageEntries, callback, error, error.Length);
                if (callbackError != null) throw callbackError;
                if (count <= 0 || count > MaximumPackageEntries)
                {
                    int end = Array.IndexOf(error, (byte)0);
                    string detail = Encoding.UTF8.GetString(error, 0, end < 0 ? error.Length : end);
                    throw new InvalidDataException("7z extraction failed (" + count + "): " + detail + ". Archive retained.");
                }
                var result = new List<PackageArchiveEntry>();
                for (int i = 0; i < count; i++)
                {
                    IntPtr entry = IntPtr.Add(entries, i * stride);
                    string path = NativeArchiveText(entry);
                    result.Add(new PackageArchiveEntry { Name = Path.GetFileName(path),
                        Size = Marshal.ReadInt64(entry, 1024), ExtractedPath = destination == null ? "" : path });
                }
                return result;
            }
            catch (DllNotFoundException)
            { throw new IOException("The 7z extractor is unavailable in this installation; reinstall the current SSPI package. Archive retained."); }
            catch (EntryPointNotFoundException)
            { throw new IOException("The loaded extractor is from an older SSPI build; restart SSPI after updating. Archive retained."); }
            finally { GC.KeepAlive(callback); Marshal.FreeHGlobal(entries); }
        }

        static string NativeArchiveText(IntPtr value)
        {
            if (value == IntPtr.Zero) return "No decoder detail";
            int length = 0;
            while (length < 1024 && Marshal.ReadByte(value, length) != 0) length++;
            byte[] bytes = new byte[length];
            Marshal.Copy(value, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

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
            if (StartsWith(header, read, SevenZipMagic)) return PackageObjectKind.SevenZip;
            return PackageObjectKind.Unknown;
        }

        public static List<PackageArchiveEntry> ListPackages(string archivePath)
        {
            PackageObjectKind kind = Detect(archivePath);
            if (kind == PackageObjectKind.SevenZip) return ReadSevenZip(archivePath, null);
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
            if (kind == PackageObjectKind.SevenZip) return ReadSevenZip(archivePath, destination);
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
            long expanded = 0, total = 0, writtenTotal = 0;
            int archiveEntries = 0;
            try
            {
                using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var archive = SharpCompress.Archives.Zip.ZipArchive.OpenArchive(input))
                {
                    int preflightEntries = 0;
                    foreach (var item in archive.Entries)
                    {
                        CheckCancel(cancel);
                        if (++preflightEntries > MaximumArchiveEntries)
                            throw new InvalidDataException("Archive contains too many entries");
                        if (!item.IsDirectory && (item.Key ?? "").EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
                            AddExpanded(ref total, item.Size);
                    }
                    if (destination != null) ArchiveStorage.RequireFreeSpace(destination, checked(total + 64L * 1024 * 1024));
                    if (progress != null) progress(0, total);
                    foreach (var item in archive.Entries)
                    {
                        CheckCancel(cancel);
                        if (++archiveEntries > MaximumArchiveEntries)
                            throw new InvalidDataException("Archive contains too many entries");
                        string name = ValidateEntryName(item.Key);
                        if (item.IsDirectory || name.EndsWith("/", StringComparison.Ordinal) ||
                            !name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)) continue;
                        if (item.IsEncrypted) throw new InvalidDataException("Encrypted ZIP packages are not supported; choose another mirror");
                        if (!names.Add(name))
                            throw new InvalidDataException("Duplicate PKG archive entry: " + name);
                        if (result.Count >= MaximumPackageEntries)
                            throw new InvalidDataException("Archive contains too many PKG files");
                        AddExpanded(ref expanded, item.Size);
                        var entry = new PackageArchiveEntry
                        {
                            Name = name,
                            CompressedSize = item.CompressedSize,
                            Size = item.Size
                        };
                        if (destination != null)
                        {
                            ArchiveStorage.RequireFreeSpace(destination, checked(item.Size + 64L * 1024 * 1024));
                            string output = OutputPath(destination, result.Count + 1);
                            string partial = output + ".part";
                            created.Add(partial);
                            long written = 0;
                            uint crc = 0xFFFFFFFF;
                            using (Stream source = item.OpenEntryStream())
                            using (var target = new FileStream(partial, FileMode.CreateNew,
                                FileAccess.Write, FileShare.None))
                            {
                                byte[] buffer = new byte[128 * 1024];
                                int count;
                                while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
                                {
                                    CheckCancel(cancel);
                                    if (count > item.Size - written)
                                        throw new InvalidDataException("ZIP entry exceeds its declared size: " + name);
                                    target.Write(buffer, 0, count);
                                    crc = UpdateCrc(crc, buffer, 0, count);
                                    written += count;
                                    writtenTotal += count;
                                    if (progress != null) progress(writtenTotal, total);
                                }
                            }
                            if (written != item.Size)
                                throw new InvalidDataException("ZIP entry is truncated: " + name);
                            if (~crc != unchecked((uint)item.Crc))
                                throw new InvalidDataException("ZIP entry CRC mismatch: " + name);
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
            if (kind == PackageObjectKind.SevenZip)
            {
                if (paths.Count != 1) throw new InvalidDataException("Split 7z archives are not supported; select one complete 7z file.");
                return ReadSevenZip(paths[0], Path.GetFullPath(destination), cancel, progress, password);
            }
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
                    if (progress != null) progress(0, expanded);
                    int extracted = 0;
                    // Sequential extraction preserves the RAR solid dictionary across entries/volumes.
                    foreach (var stream in streams) stream.Position = 0;
                    using (var reader = SharpCompress.Readers.Rar.RarReader.OpenReader(streams, new ReaderOptions { LeaveStreamOpen = true, Password = password }))
                    {
                        while (reader.MoveToNextEntry())
                        {
                            CheckCancel(cancel);
                            PackageArchiveEntry entry;
                            if (reader.Entry.IsDirectory) continue;
                            if (!selected.TryGetValue(ValidateEntryName(reader.Entry.Key), out entry))
                            {
                                // Solid archives must decode preceding files before reaching a PKG.
                                // Drain explicitly so cancellation/progress stay responsive during that work.
                                using (Stream skipped = reader.OpenEntryStream())
                                {
                                    byte[] buffer = new byte[128 * 1024];
                                    long consumed = 0; int count;
                                    while ((count = skipped.Read(buffer, 0, buffer.Length)) > 0)
                                    {
                                        CheckCancel(cancel);
                                        if (count > reader.Entry.Size - consumed || count > expanded - writtenTotal)
                                            throw new InvalidDataException("RAR entry exceeds declared size");
                                        consumed += count; writtenTotal += count;
                                        if (progress != null) progress(writtenTotal, expanded);
                                    }
                                    if (consumed != reader.Entry.Size) throw new InvalidDataException("Truncated RAR entry");
                                }
                                continue;
                            }
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
                                    if (progress != null) progress(writtenTotal, expanded);
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

    }
}
