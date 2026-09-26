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
        static readonly object RarGate = new object();

        /// <summary>Phase evidence for a decoder failure. The decoder reports every header,
        /// scan and payload boundary, so retryability is decided from where the failure
        /// happened rather than from the wording of its message. The thrown exception type
        /// stays System.IO.InvalidDataException, which is sealed and part of the existing
        /// contract with the install pipeline.</summary>
        internal sealed class RarFailureEvidence
        {
            internal int Code;
            internal bool PayloadStarted;
            internal bool HeaderPhaseReported;
            internal string Password = "";
        }

        internal const string RarFailureKey = "sspi.rar.failure";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void NativeRarDiagnostic(IntPtr stage, uint entry, int result, uint dictionary, uint method, ulong size);
        [DllImport("libGameSearchResident.prx", CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_extract_rar_password_diagnostic(IntPtr first, IntPtr destination,
            IntPtr names, IntPtr paths, int volumes, IntPtr packages, int capacity,
            NativeArchiveProgress progress, IntPtr password, NativeRarDiagnostic diagnostic);

        /// <summary>Regression seam. Null in production, where the console's UnRAR module is
        /// called directly; the managed harness substitutes a decoder double so the retry
        /// policy below is executed rather than re-implemented.</summary>
        internal static Func<IntPtr, IntPtr, IntPtr, IntPtr, int, IntPtr, int, NativeArchiveProgress, IntPtr,
            NativeRarDiagnostic, int> NativeRarOverride;

        static IntPtr ArchiveUtf8(string value, List<IntPtr> allocations)
        {
            if (value == null || value.IndexOf('\0') >= 0) throw new InvalidDataException("Invalid archive path or password");
            byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
            if (bytes.Length > 1024) throw new InvalidDataException("Archive path or password is too long");
            IntPtr memory = Marshal.AllocHGlobal(bytes.Length);
            allocations.Add(memory); Marshal.Copy(bytes, 0, memory, bytes.Length); return memory;
        }

        internal static List<PackageArchiveEntry> ReadNativeRar(IList<string> paths, string destination,
            Func<bool> cancel, Action<long, long> progress, string password,
            IList<string> volumeNames = null, Action<string> diagnostic = null)
        {
            if (paths == null || paths.Count == 0 || paths.Count > 512 ||
                (volumeNames != null && volumeNames.Count != paths.Count))
                throw new InvalidDataException("Invalid RAR volume set");
            CheckCancel(cancel);
            // UnRAR keeps decoder state per process. Wait only on this extraction worker.
            while (!System.Threading.Monitor.TryEnter(RarGate, 100)) CheckCancel(cancel);
            var allocations = new List<IntPtr>();
            Exception callbackError = null;
            bool payloadStarted = false;
            int headerPhaseResult = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long reportedAt = -200, reportedTotal = -1, reportedDone = -1;
            NativeArchiveProgress callback = (done, total) => {
                try {
                    CheckCancel(cancel);
                    if (callbackError != null) return 1;
                    if (progress != null && (clock.ElapsedMilliseconds - reportedAt >= 200 || total != reportedTotal ||
                        (total > 0 && done == total && done != reportedDone))) {
                        reportedAt = clock.ElapsedMilliseconds; reportedTotal = total; reportedDone = done; progress(done, total);
                    }
                    CheckCancel(cancel);
                    return 0;
                } catch (Exception ex) { callbackError = ex; return 1; }
            };
            NativeRarDiagnostic trace = (stage, entry, result, dictionary, method, size) => {
                try {
                    string name = NativeArchiveText(stage);
                    // Phase evidence for the retry policy below: the decoder reports every
                    // header/scan boundary and every payload boundary, so a failure can be
                    // classified by where it happened instead of by its message text.
                    if (name.StartsWith("process-", StringComparison.Ordinal)) payloadStarted = true;
                    else if ((name == "open-failed" || name == "scan-complete") && result != 0) headerPhaseResult = result;
                    if (diagnostic != null) diagnostic("decoder=unrar stage=" + name +
                        " entry=" + entry + " result=" + result + " dictionary_kib=" + dictionary +
                        " method=" + method + " unpacked=" + size);
                }
                catch (Exception ex) { callbackError = ex; }
            };
            try {
                IntPtr nativePaths = Marshal.AllocHGlobal(paths.Count * IntPtr.Size); allocations.Add(nativePaths);
                IntPtr nativeNames = Marshal.AllocHGlobal(paths.Count * IntPtr.Size); allocations.Add(nativeNames);
                for (int i = 0; i < paths.Count; i++) {
                    CheckCancel(cancel);
                    string path = Path.GetFullPath(paths[i]);
                    if (!File.Exists(path)) throw new InvalidDataException("Missing archive volume " + (i + 1));
                    Marshal.WriteIntPtr(nativePaths, i * IntPtr.Size, ArchiveUtf8(path, allocations));
                    Marshal.WriteIntPtr(nativeNames, i * IntPtr.Size,
                        ArchiveUtf8(ArchiveVolumeSet.DecoderName(volumeNames == null ? Path.GetFileName(path) : volumeNames[i]), allocations));
                }
                const int stride = 1032;
                IntPtr packages = Marshal.AllocHGlobal(MaximumPackageEntries * stride); allocations.Add(packages);
                IntPtr first = Marshal.ReadIntPtr(nativePaths);
                IntPtr archiveDestination = ArchiveUtf8(destination, allocations);
                IntPtr suppliedPassword = ArchiveUtf8(password ?? "", allocations);
                int count = NativeRarOverride != null
                    ? NativeRarOverride(first, archiveDestination, nativeNames, nativePaths, paths.Count,
                        packages, MaximumPackageEntries, callback, suppliedPassword, trace)
                    : gs_extract_rar_password_diagnostic(first, archiveDestination, nativeNames, nativePaths,
                        paths.Count, packages, MaximumPackageEntries, callback, suppliedPassword, trace);
                if (callbackError != null) throw callbackError;
                if (count <= 0 || count > MaximumPackageEntries) {
                    string detail;
                    switch (-count) {
                        case 22: case 24: detail = "RAR password rejected; check the password supplied by the source"; break;
                        case 15: case 18: detail = "RAR volume could not be read; check that every volume finished downloading"; break;
                        case 11: case 25: detail = "RAR dictionary or expanded size exceeds the supported memory limit"; break;
                        // Same causes and wording as the background worker (gs_archive_job.inc).
                        case 16: case 19: detail = "RAR output could not be created or written on the staging drive; check free space and that the drive is connected, then retry"; break;
                        case 1009: detail = "RAR output is larger than the staging drive can store in one file (FAT32 limit: 4 GiB); use an exFAT-formatted drive and retry"; break;
                        // UnRAR reports a broken or undecodable header block, and a wrong RAR4
                        // header-decryption key, as ERAR_BAD_DATA (see dll.cpp:241, which tests
                        // BrokenHeader before FailedHeaderDecryption, and arcread.cpp:538, which
                        // sets BrokenHeader on a header CRC failure). The text names both
                        // possibilities; which one applies is decided by phase evidence, never by
                        // this wording.
                        case 12: detail = headerPhaseResult != 0 && !payloadStarted
                            // Failed before any entry was decoded: encrypted headers with a
                            // missing or wrong password look like this. Naming the password
                            // lets the Downloads drawer offer "Enter archive password".
                            ? "RAR headers could not be read: the archive password is missing or incorrect, or the first volume is damaged"
                            : "RAR header or data integrity check failed; the archive content is damaged or truncated"; break;
                        case 1001: detail = "RAR header scan timed out"; break;
                        case 1002: detail = "RAR was read successfully but contains no PKG files; check that the source supplied a PS4 package archive"; break;
                        case 1006: detail = "RAR holds an extracted game folder (eboot.bin/sce_sys), not an installable PKG. SSPI installs PKG files only; choose another mirror"; break;
                        case 1007: detail = "RAR holds another archive instead of a PKG. Extract it on a computer or choose another mirror"; break;
                        case 1008: detail = "RAR holds split PKG pieces that must be joined first; choose another mirror"; break;
                        case 1003: detail = "Another RAR extraction is still running; retry after it finishes"; break;
                        case 1005: detail = "RAR decoder initialization failed; restart SSPI after installing the updated package"; break;
                        default: detail = "RAR decoder could not read this archive"; break;
                    }
                    string failure = detail + " (decoder " + count + "). Archive retained.";
                    var reported = new InvalidDataException(failure);
                    try { reported.Data[RarFailureKey] = new RarFailureEvidence {
                        Code = -count, PayloadStarted = payloadStarted,
                        HeaderPhaseReported = headerPhaseResult != 0, Password = password ?? "" }; }
                    catch (Exception) { }
                    throw reported;
                }
                var entries = new List<PackageArchiveEntry>(count);
                for (int i = 0; i < count; i++) {
                    IntPtr entry = IntPtr.Add(packages, i * stride);
                    string path = NativeArchiveText(entry);
                    entries.Add(new PackageArchiveEntry { Name = Path.GetFileName(path),
                        Size = Marshal.ReadInt64(entry, 1024), ExtractedPath = path });
                }
                return entries;
            }
            catch (DllNotFoundException) { throw new IOException("RAR decoder is unavailable; reinstall the current SSPI package. Archive retained."); }
            catch (EntryPointNotFoundException) { throw new IOException("RAR decoder needs the updated SSPI package; restart after updating. Archive retained."); }
            finally {
                GC.KeepAlive(callback); GC.KeepAlive(trace);
                foreach (IntPtr allocation in allocations) Marshal.FreeHGlobal(allocation);
                System.Threading.Monitor.Exit(RarGate);
            }
        }

        internal static bool IsSevenZipHeader(byte[] header)
        { return header != null && StartsWith(header, header.Length, SevenZipMagic); }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate int NativeArchiveProgress(long done, long total);
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
                return ExtractPackages(new[] { archivePath }, destination, null, null);
            throw new InvalidDataException("Not a PKG or archive");
        }

        // Mirrors gs_archive_entry_hint in the native readers: name the likely
        // reason an archive held no PKG instead of a generic failure, in the
        // background worker's words (gs_archive_job.inc).
        internal static string NoPackageMessage(string format, IEnumerable<string> entryNames)
        {
            bool dump = false, split = false, nested = false;
            foreach (string raw in entryNames)
            {
                string name = (raw ?? "").Replace('\\', '/').ToLowerInvariant();
                string leaf = name.Substring(name.LastIndexOf('/') + 1);
                if (leaf == "eboot.bin" || name.EndsWith("sce_sys/param.sfo", StringComparison.Ordinal)) dump = true;
                else if (IsSplitPackagePiece(leaf)) split = true;
                else foreach (string suffix in new[] { ".rar", ".zip", ".7z", ".r00", ".z01", ".001" })
                    if (name.EndsWith(suffix, StringComparison.Ordinal)) { nested = true; break; }
            }
            if (dump) return format + " holds an extracted game folder (eboot.bin/sce_sys), not an installable PKG. SSPI installs PKG files only; choose another mirror. Archive retained.";
            if (split) return format + " holds split PKG pieces that must be joined first; choose another mirror. Archive retained.";
            if (nested) return format + " holds another archive instead of a PKG. Extract it on a computer or choose another mirror. Archive retained.";
            return format == "ZIP" ? ZipFailureMessage(2012) : "Archive contains no PKG files";
        }

        /// <summary>A split PKG piece ends in ".pkg." plus 1 to 6 digits (game.pkg.001), as in
        /// gs_unrar.h. Sidecars such as game.pkg.md5 and archives such as game.pkg.rar are not.</summary>
        internal static bool IsSplitPackagePiece(string leaf)
        {
            int dot = leaf.LastIndexOf('.');
            int digits = dot < 0 ? 0 : leaf.Length - dot - 1;
            if (dot < 4 || digits < 1 || digits > 6 || string.CompareOrdinal(leaf, dot - 4, ".pkg", 0, 4) != 0) return false;
            for (int i = dot + 1; i < leaf.Length; i++) if (leaf[i] < '0' || leaf[i] > '9') return false;
            return true;
        }

        /// <summary>The background worker's ZIP messages (gs_archive_job.inc) for the same
        /// native codes, so In-app and background extraction name a cause in the same words.</summary>
        internal static string ZipFailureMessage(int code)
        {
            string number = code.ToString(CultureInfo.InvariantCulture);
            switch (code)
            {
                case 2001: return "Split ZIP sets are not supported; select one complete ZIP file. Archive retained.";
                case 2003: case 2010: return "ZIP is incomplete or damaged (" + number + "); archive retained. Retry the download or choose another mirror.";
                case 2005: return "ZIP uses encryption or a compression method SSPI cannot read; archive retained. Choose another mirror.";
                case 2007: return "Not enough free space for the extracted package; archive retained. Free space on the staging drive and retry.";
                case 2011: return "ZIP output could not be written to the staging drive; archive retained. Check free space and that the drive is connected, then retry.";
                case 2012: return "ZIP was read successfully but contains no PKG files; archive retained. Check that the source supplied a PS4 package archive.";
                case 2019: return "ZIP output is larger than the staging drive can store in one file (FAT32 limit: 4 GiB); archive retained. Use an exFAT-formatted drive and retry.";
                default: return "ZIP extraction failed (" + number + "); check missing volumes, storage or archive integrity";
            }
        }

        // Reader failures that are not cancellation or an input read error: the ZIP itself
        // could not be parsed or decoded.
        static bool IsZipReaderFailure(Exception error)
        {
            return !(error is OperationCanceledException) &&
                !(error is IOException && !(error is EndOfStreamException));
        }

        static Exception ZipReaderFailure(Exception error, int damagedCode)
        {
            if (error is NotSupportedException || error is System.Security.Cryptography.CryptographicException)
                return new InvalidDataException(ZipFailureMessage(2005), error);
            return new InvalidDataException(ZipFailureMessage(damagedCode) + " (" + error.Message + ")", error);
        }

        static T OpenZip<T>(Func<T> open)
        {
            try { return open(); }
            catch (Exception error) when (IsZipReaderFailure(error)) { throw ZipReaderFailure(error, 2003); }
        }

        // Directory reads can fail while enumerating; report them as a damaged ZIP (2003).
        static IEnumerable<T> ZipDirectory<T>(IEnumerable<T> entries)
        {
            IEnumerator<T> cursor;
            try { cursor = entries.GetEnumerator(); }
            catch (Exception error) when (IsZipReaderFailure(error)) { throw ZipReaderFailure(error, 2003); }
            using (cursor)
                for (;;)
                {
                    bool more;
                    try { more = cursor.MoveNext(); }
                    catch (Exception error) when (IsZipReaderFailure(error)) { throw ZipReaderFailure(error, 2003); }
                    if (!more) yield break;
                    yield return cursor.Current;
                }
        }

        static void RequireZipSpace(string destination, long required)
        {
            try { ArchiveStorage.RequireFreeSpace(destination, required); }
            catch (IOException error) when (error.Message.StartsWith("Extraction needs ", StringComparison.Ordinal))
            { throw new IOException(ZipFailureMessage(2007) + " (" + error.Message + ")", error); }
        }

        /// <summary>A failed output write is a storage failure, never a damaged archive: a full
        /// drive (Win32 39 or 112) is 2007; a write that fails as the file passes 4 GiB - 1 byte,
        /// the most FAT32 can store, is 2019 (the margin covers data the stream buffered before
        /// this write); any other write error is 2011.</summary>
        internal static IOException ZipWriteFailure(IOException error, long offset, long count)
        {
            const long buffered = 1024 * 1024;
            int code = error.HResult & 0xFFFF;
            if ((error.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) && (code == 39 || code == 112))
                return new IOException(ZipFailureMessage(2007), error);
            if (offset - buffered <= uint.MaxValue && offset + count > uint.MaxValue)
                return new IOException(ZipFailureMessage(2019), error);
            return new IOException(ZipFailureMessage(2011) + " (" + error.Message + ")", error);
        }

        static void RequireZipPkgMagic(string path, string entryName)
        {
            try { RequirePkgMagic(path, entryName); }
            catch (InvalidDataException error)
            { throw new InvalidDataException(ZipFailureMessage(2010) + " (" + entryName + " is not a PKG)", error); }
        }

        static List<PackageArchiveEntry> ReadZip(string archivePath, string destination,
            Func<bool> cancel = null, Action<long, long> progress = null)
        {
            var result = new List<PackageArchiveEntry>();
            var entryNames = new List<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var created = new List<string>();
            long expanded = 0, total = 0, writtenTotal = 0;
            int archiveEntries = 0;
            try
            {
                using (var input = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var archive = OpenZip(() => SharpCompress.Archives.Zip.ZipArchive.OpenArchive(input)))
                {
                    int preflightEntries = 0;
                    foreach (var item in ZipDirectory(archive.Entries))
                    {
                        CheckCancel(cancel);
                        if (++preflightEntries > MaximumArchiveEntries)
                            throw new InvalidDataException("Archive contains too many entries");
                        if (!item.IsDirectory) entryNames.Add(item.Key ?? "");
                        if (!item.IsDirectory && (item.Key ?? "").EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
                            AddExpanded(ref total, item.Size);
                    }
                    if (destination != null) RequireZipSpace(destination, checked(total + 64L * 1024 * 1024));
                    if (progress != null) progress(0, total);
                    foreach (var item in ZipDirectory(archive.Entries))
                    {
                        CheckCancel(cancel);
                        if (++archiveEntries > MaximumArchiveEntries)
                            throw new InvalidDataException("Archive contains too many entries");
                        string name = ValidateEntryName(item.Key);
                        if (item.IsDirectory || name.EndsWith("/", StringComparison.Ordinal) ||
                            !name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)) continue;
                        if (item.IsEncrypted) throw new InvalidDataException(ZipFailureMessage(2005));
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
                            RequireZipSpace(destination, checked(item.Size + 64L * 1024 * 1024));
                            string output = OutputPath(destination, result.Count + 1);
                            string partial = output + ".part";
                            created.Add(partial);
                            long written = 0;
                            uint crc = 0xFFFFFFFF;
                            Stream source;
                            try { source = item.OpenEntryStream(); }
                            catch (Exception error) when (IsZipReaderFailure(error)) { throw ZipReaderFailure(error, 2010); }
                            using (source)
                            using (var target = new FileStream(partial, FileMode.CreateNew,
                                FileAccess.Write, FileShare.None))
                            {
                                byte[] buffer = new byte[128 * 1024];
                                for (;;)
                                {
                                    int count;
                                    try { count = source.Read(buffer, 0, buffer.Length); }
                                    catch (Exception error) when (IsZipReaderFailure(error)) { throw ZipReaderFailure(error, 2010); }
                                    if (count <= 0) break;
                                    CheckCancel(cancel);
                                    if (count > item.Size - written)
                                        throw new InvalidDataException(ZipFailureMessage(2010) + " (" + name + " exceeds its declared size)");
                                    try { target.Write(buffer, 0, count); }
                                    catch (IOException error) { throw ZipWriteFailure(error, written, count); }
                                    crc = UpdateCrc(crc, buffer, 0, count);
                                    written += count;
                                    writtenTotal += count;
                                    if (progress != null) progress(writtenTotal, total);
                                }
                                try { target.Flush(); }
                                catch (IOException error) { throw ZipWriteFailure(error, written, 0); }
                            }
                            if (written != item.Size)
                                throw new InvalidDataException(ZipFailureMessage(2010) + " (" + name + " is truncated)");
                            if (~crc != unchecked((uint)item.Crc))
                                throw new InvalidDataException(ZipFailureMessage(2010) + " (CRC mismatch in " + name + ")");
                            RequireZipPkgMagic(partial, name);
                            File.Move(partial, output);
                            created.Remove(partial);
                            created.Add(output);
                            entry.ExtractedPath = output;
                        }
                        result.Add(entry);
                    }
                }
                if (result.Count == 0) throw new InvalidDataException(NoPackageMessage("ZIP", entryNames));
                return result;
            }
            catch
            {
                Cleanup(created);
                throw;
            }
        }

        public static List<PackageArchiveEntry> ExtractPackages(IList<string> paths,
            string destination, Func<bool> cancel, Action<long, long> progress, string password = null, string[] passwordFallbacks = null,
            IList<string> volumeNames = null, Action<string> diagnostic = null)
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
            {
                var tried = new HashSet<string>(StringComparer.Ordinal);
                int next = 0;
                for (;;)
                {
                    CheckCancel(cancel);
                    tried.Add(password ?? "");
                    try {
#if SSPI_PS4
                        return ReadNativeRar(paths, Path.GetFullPath(destination), cancel, progress, password, volumeNames, diagnostic);
#else
                        return ReadRar(paths, Path.GetFullPath(destination), cancel, progress, password);
#endif
                    }
                    catch (Exception ex)
                    {
                        // Retryability is a classified decoder outcome, not a wording match:
                        // RarPasswordRejectedException (native 22/24) and
                        // RarEncryptedHeaderException (open/list failure before any payload,
                        // with a password supplied) may spend another source password.
                        // Payload CRC/data failures, cancellation, storage and I/O errors are
                        // rethrown unchanged, so a failure after gigabytes of decoding can never
                        // restart extraction. The bound is the initial attempt plus at most the
                        // first three fallback entries, and duplicates are never repeated.
                        if (!IsRetryablePasswordFailure(ex)) throw;
                        while (passwordFallbacks != null && next < Math.Min(3, passwordFallbacks.Length) &&
                            (string.IsNullOrEmpty(passwordFallbacks[next]) || tried.Contains(passwordFallbacks[next]))) next++;
                        if (passwordFallbacks == null || next >= Math.Min(3, passwordFallbacks.Length)) throw;
                        password = passwordFallbacks[next++];
                        if (progress != null) progress(0, 0);
                    }
                }
            }
            if (paths.Count == 1 && kind == PackageObjectKind.Zip)
                return ReadZip(paths[0], Path.GetFullPath(destination), cancel, progress);
            if (kind == PackageObjectKind.Zip) throw new InvalidDataException(ZipFailureMessage(2001));
            throw new InvalidDataException("Not a supported archive");
        }

        static void CheckCancel(Func<bool> cancel)
        {
            if (cancel != null && cancel()) throw new OperationCanceledException();
        }

        /// <summary>Decides whether a decoder failure may spend another source password.
        /// Executed by ExtractPackages and by the managed regression harness, so the
        /// policy under test is the policy that ships. Only two things are retryable:
        /// an explicit password rejection (native 22/24) and an open/list failure that
        /// happened before any entry payload was decoded while a password was supplied.
        /// A payload CRC or data failure after output work, cancellation, storage and
        /// I/O errors are terminal, so a failure after gigabytes of decoding can never
        /// restart extraction.</summary>
        internal static bool IsRetryablePasswordFailure(Exception error)
        {
            if (error is System.Security.Cryptography.CryptographicException) return true;
            var evidence = error?.Data?[RarFailureKey] as RarFailureEvidence;
            if (evidence == null) return false;
            if (evidence.Code == 22 || evidence.Code == 24) return true;
            return evidence.Code == 12 && evidence.Password.Length > 0 &&
                !evidence.PayloadStarted && evidence.HeaderPhaseReported;
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
                        if (item.IsEncrypted && string.IsNullOrEmpty(password))
                        {
                            var missingPassword = new InvalidDataException("Archive password required; refresh this package from its source");
                            try { missingPassword.Data[RarFailureKey] = new RarFailureEvidence { Code = 22 }; }
                            catch (Exception) { }
                            throw missingPassword;
                        }
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
