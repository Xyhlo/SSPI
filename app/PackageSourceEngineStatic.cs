using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbis
{
    // Data-only catalog reader. No HTTP client, process, reflection or provider access.
    internal static class PackageSourceEngineStatic
    {
        internal const string EngineType = "embedded-catalog-v1";
        internal const int MaxTitles = 30000;
        const int MaxJsonBytes = 2 * 1024 * 1024;
        const int MaxShardBytes = 1500000;
        const int MaxIndexBytes = 4 * 1024 * 1024;
        static readonly Regex TitlePattern = new Regex(@"\A(?:CUSA|SLUS|SCUS|SLES|SCES|SLPS|SLPM|SCPS)[0-9]{5}\z");
        static readonly object Gate = new object();
        static readonly Dictionary<string, Catalog> Catalogs = new Dictionary<string, Catalog>(StringComparer.Ordinal);
        static readonly LinkedList<string> CatalogOrder = new LinkedList<string>();
        static int Epoch;

        sealed class Record
        {
            internal SourceTitleResult Title;
            internal string PackagesFile, NormalizedId, NormalizedName, SearchText;
            internal int ReleaseCount;
        }
        sealed class Shard
        {
            internal string Path;
            internal Dictionary<string, List<PackageCandidate>> Titles;
        }
        sealed class Catalog
        {
            internal readonly List<Record> Records = new List<Record>();
            internal readonly Dictionary<string, Record> ById = new Dictionary<string, Record>(StringComparer.OrdinalIgnoreCase);
            internal readonly LinkedList<Shard> Shards = new LinkedList<Shard>();
            internal readonly object ShardGate = new object();
            internal List<string> PackageFiles;
            internal Func<string, byte[]> Read;
            internal PackageSourceDescriptor Descriptor;
            internal long PublishedTitles, PublishedReady, PublishedReleases;
        }

        internal static void Invalidate()
        { lock (Gate) { Epoch++; Catalogs.Clear(); CatalogOrder.Clear(); } }

        internal static string NormalizeRegion(string region)
        { return PackageSourceIdentity.NormalizeRegion(region); }

        internal static string NormalizeSearch(string value)
        {
            var text = new StringBuilder(); bool space = false;
            foreach (char c in (value ?? "").Normalize(NormalizationForm.FormKD).ToLowerInvariant())
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
                if (category == UnicodeCategory.NonSpacingMark || category == UnicodeCategory.SpacingCombiningMark || category == UnicodeCategory.EnclosingMark) continue;
                if (char.IsLetterOrDigit(c)) { if (space && text.Length > 0) text.Append(' '); text.Append(c); space = false; }
                else space = true;
            }
            return text.ToString();
        }

        internal static int SearchRank(SourceTitleResult title, string query)
        { string normalized = NormalizeSearch(query); return Rank(NormalizeSearch(title.TitleId), NormalizeSearch(title.DisplayName), NormalizeSearch(title.DisplayName + " " + title.TitleId), normalized, normalized.Split(' ')); }

        static int Rank(string id, string name, string text, string query, string[] words)
        {
            if (query.Length == 0) return 4;
            if (id == query) return 0;
            if (name == query) return 1;
            if (name.StartsWith(query, StringComparison.Ordinal)) return 2;
            foreach (string word in words) if (text.IndexOf(word, StringComparison.Ordinal) < 0) return -1;
            return 3;
        }

        internal static List<SourceTitleResult> Search(InstalledPackageSource source, string path, SourceSearchRequest request)
        {
            CheckCanceled(request.Cancel);
            Catalog catalog = GetCatalog(source, path, request.Cancel);
            string query = NormalizeSearch(request.Query); string region = NormalizeRegion(request.Region);
            string[] words = query.Split(' ');
            var matches = new List<KeyValuePair<int, Record>>();
            foreach (Record record in catalog.Records)
            {
                CheckCanceled(request.Cancel);
                if (region.Length > 0 && record.Title.Region != region) continue;
                int rank = Rank(record.NormalizedId, record.NormalizedName, record.SearchText, query, words);
                if (rank >= 0) matches.Add(new KeyValuePair<int, Record>(rank, record));
            }
            matches.Sort((a, b) => { int order = a.Key.CompareTo(b.Key); return order != 0 ? order : CompareTitle(a.Value.Title, b.Value.Title); });
            var result = new List<SourceTitleResult>(matches.Count);
            foreach (var match in matches) { var title = CloneTitle(match.Value.Title); title.SearchRankHint = match.Key; result.Add(title); }
            return result; // Coordinator selects a page only after all index records were searched.
        }

        internal static List<PackageCandidate> Resolve(InstalledPackageSource source, string path, SourceResolveRequest request)
        {
            CheckCanceled(request.Cancel);
            Catalog catalog = GetCatalog(source, path, request.Cancel);
            Record record; string id = (request.TitleId ?? "").Trim();
            if (!catalog.ById.TryGetValue(id, out record)) return new List<PackageCandidate>();
            string region = NormalizeRegion(request.Region);
            if (region.Length > 0 && record.Title.Region.Length > 0 && region != record.Title.Region) return new List<PackageCandidate>();
            lock (catalog.ShardGate)
            {
                CheckCanceled(request.Cancel);
                Shard shard = null;
                for (var item = catalog.Shards.First; item != null; item = item.Next)
                    if (item.Value.Path == record.PackagesFile) { shard = item.Value; catalog.Shards.Remove(item); break; }
                if (shard == null) shard = new Shard { Path = record.PackagesFile, Titles = ReadShard(catalog, record.PackagesFile, request.Cancel) };
                catalog.Shards.AddFirst(shard);
                while (catalog.Shards.Count > 2) catalog.Shards.RemoveLast();
                List<PackageCandidate> rows;
                if (!shard.Titles.TryGetValue(record.Title.TitleId, out rows)) throw Bad("Selected title has no published package choices");
                var result = new List<PackageCandidate>(rows.Count);
                foreach (PackageCandidate row in rows)
                    if (region.Length == 0 || row.Region == region) result.Add(CloneCandidate(row));
                return result;
            }
        }

        internal static void ValidatePackage(PackageSourcePackage package)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (PackageSourcePackageEntry entry in package.Entries) declared.Add(entry.Path);
            var manifest = Object(PackageSourceJson.Parse(package.SourceJson), "manifest");
            foreach (object entry in Array(manifest, "files", 63, true))
            {
                var row = Object(entry, "file declaration");
                RelativePath(Text(row, "path", 180, true));
                Integer(row, "size", true); Sha(Text(row, "sha256", 64, true));
            }
            Catalog catalog = LoadIndex(package.Descriptor, package.ReadCatalogFile, declared, null);
            long releases = 0;
            foreach (string path in catalog.PackageFiles)
            {
                var shard = ReadShard(catalog, path, null);
                foreach (var rows in shard.Values) releases += rows.Count;
            }
            if (catalog.PublishedReady != catalog.Records.Count || catalog.PublishedTitles < catalog.Records.Count || catalog.PublishedReleases != releases)
                throw Bad("Catalog counts do not match its ready titles and package choices");
        }

        static Catalog GetCatalog(InstalledPackageSource source, string path, Func<bool> cancel)
        {
            if (source == null || source.Descriptor == null) throw Bad("Installed source descriptor is missing");
            string root = Path.GetFullPath(path);
            string key = source.SourceId + "\n" + source.Descriptor.Version + "\n" + root;
            int epoch;
            lock (Gate)
            {
                Catalog cached;
                if (Catalogs.TryGetValue(key, out cached)) { CatalogOrder.Remove(key); CatalogOrder.AddFirst(key); return cached; }
                epoch = Epoch;
            }
            byte[] manifestBytes = ReadContained(root, "source.json", 256 * 1024);
            var manifest = Object(PackageSourceJson.Parse(Utf8(manifestBytes)), "manifest");
            if (Text(manifest, "id", 128, true) != source.SourceId || Text(manifest, "version", 64, true) != source.Descriptor.Version)
                throw Bad("Installed source identity changed; import it again");
            var entries = Array(manifest, "files", 63, true);
            var files = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            foreach (object entry in entries)
            {
                var row = Object(entry, "file declaration"); string file = RelativePath(Text(row, "path", 180, true));
                if (files.ContainsKey(file)) throw Bad("Duplicate file declaration");
                files.Add(file, row);
            }
            Func<string, byte[]> read = file =>
            {
                Dictionary<string, object> row;
                if (!files.TryGetValue(file, out row)) throw Bad("Catalog references an undeclared file");
                byte[] data = ReadContained(root, file, file.StartsWith("packages/", StringComparison.Ordinal) ? MaxShardBytes : MaxJsonBytes);
                string expected = Text(row, "sha256", 64, true);
                if (data.LongLength != Integer(row, "size", true) || !Hash(data).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw Bad("Installed catalog file changed; import a verified source again");
                return data;
            };
            var declared = new HashSet<string>(files.Keys, StringComparer.Ordinal); declared.Add("source.json");
            Catalog loaded = LoadIndex(source.Descriptor, read, declared, cancel);
            lock (Gate)
            {
                if (epoch != Epoch) throw new OperationCanceledException("Source changed during catalog loading");
                Catalogs[key] = loaded; CatalogOrder.Remove(key); CatalogOrder.AddFirst(key);
                while (CatalogOrder.Count > 2) { Catalogs.Remove(CatalogOrder.Last.Value); CatalogOrder.RemoveLast(); }
            }
            return loaded;
        }

        static Catalog LoadIndex(PackageSourceDescriptor descriptor, Func<string, byte[]> read, HashSet<string> declared, Func<bool> cancel)
        {
            if (descriptor.Engine.Type != EngineType || descriptor.Engine.EntryFile != "catalog.json") throw Bad("Static engine entry must be catalog.json");
            if (descriptor.Permissions.NetworkOrigins.Count != 0 || descriptor.Permissions.RedirectOrigins.Count != 0) throw Bad("Static catalogs cannot declare network origins");
            string[] version = descriptor.Version.Split('.'); int component;
            if (version.Length < 2 || version.Length > 4) throw Bad("Static catalog version must use numeric components");
            foreach (string item in version) if (!int.TryParse(item, NumberStyles.None, CultureInfo.InvariantCulture, out component) || component < 0) throw Bad("Static catalog version component is invalid");
            var root = Parse(read("catalog.json"), MaxJsonBytes);
            if (Text(root, "format", 64, true) != EngineType || Integer(root, "formatVersion", true) != 1)
                throw Bad("Catalog format needs a newer SSPI version");
            if (Text(root, "searchNormalization", 64, true) != "nfkd-lower-alnum-spaces-v1") throw Bad("Unsupported catalog search normalization");
            var indexFiles = Paths(root, "indexFiles", "index/", declared);
            var packageFiles = Paths(root, "packageFiles", "packages/", declared);
            var expectedFiles = new HashSet<string>(indexFiles, StringComparer.Ordinal); expectedFiles.UnionWith(packageFiles); expectedFiles.Add("catalog.json");
            foreach (string file in declared) if (file != "source.json" && file != "signature.ed25519" && !expectedFiles.Contains(file)) throw Bad("Static source has unused payload files");
            var counts = Object(Value(root, "counts", true), "catalog counts");
            Integer(counts, "multipartGroups", true); Integer(counts, "unresolved", true);
            var catalog = new Catalog { Descriptor = descriptor, Read = read, PackageFiles = packageFiles,
                PublishedTitles = Integer(counts, "titles", true), PublishedReady = Integer(counts, "readyTitles", true), PublishedReleases = Integer(counts, "releases", true) };
            long indexBytes = 0;
            foreach (string file in indexFiles)
            {
                CheckCanceled(cancel); byte[] bytes = read(file); indexBytes += bytes.Length;
                if (indexBytes > MaxIndexBytes) throw Bad("Catalog title index exceeds its memory budget; split the source");
                var page = Parse(bytes, MaxJsonBytes);
                foreach (object item in Array(page, "titles", 500, true))
                {
                    var row = Object(item, "indexed title"); string id = TitleId(Text(row, "titleId", 16, true));
                    string region = NormalizeRegion(Text(row, "region", 32, false));
                    if (catalog.ById.ContainsKey(id)) throw Bad("Duplicate or conflicting indexed title identity: " + id);
                    string packages = RelativePath(Text(row, "packagesFile", 180, true));
                    if (!packageFiles.Contains(packages)) throw Bad("Indexed title refers to an undeclared package shard");
                    string name = Text(row, "name", 512, true); string icon = Url(Text(row, "icon", 2048, false), false);
                    long releaseCount = Integer(row, "releaseCount", true);
                    if (releaseCount < 1 || releaseCount > 512) throw Bad("Indexed title has invalid package count");
                    foreach (object kind in Array(row, "kinds", 8, true)) CheckKind(kind as string);
                    string suppliedSearch = Text(row, "searchText", 2048, false);
                    var title = new SourceTitleResult { SourceId = descriptor.SourceId, SourceVersion = descriptor.Version,
                        SourceAttribution = descriptor.DisplayName, TitleId = id, DisplayName = name, Region = region, ImageUrl = icon,
                        StableResultId = id + "|" + region };
                    var record = new Record { Title = title, PackagesFile = packages, ReleaseCount = (int)releaseCount,
                        NormalizedId = NormalizeSearch(id), NormalizedName = NormalizeSearch(name), SearchText = NormalizeSearch(name + " " + id + " " + suppliedSearch) };
                    catalog.Records.Add(record); catalog.ById.Add(id, record);
                    if (catalog.Records.Count > MaxTitles) throw Bad("Catalog has too many indexed titles; split the source");
                }
            }
            if (catalog.Records.Count == 0) throw Bad("Catalog has no ready titles");
            return catalog;
        }

        static Dictionary<string, List<PackageCandidate>> ReadShard(Catalog catalog, string path, Func<bool> cancel)
        {
            var root = Parse(catalog.Read(path), MaxShardBytes);
            var titles = Object(Value(root, "titles", true), "package titles");
            var result = new Dictionary<string, List<PackageCandidate>>(StringComparer.OrdinalIgnoreCase);
            foreach (var title in titles)
            {
                CheckCanceled(cancel); string id = TitleId(title.Key); Record record;
                if (result.ContainsKey(id)) throw Bad("Case-colliding package title identity");
                if (!catalog.ById.TryGetValue(id, out record) || record.PackagesFile != path) throw Bad("Package shard title is not mapped by the index: " + id);
                var entries = title.Value as IList;
                if (entries == null || entries.Count == 0 || entries.Count > 512) throw Bad("Package choices must contain 1 to 512 records");
                var rows = new List<PackageCandidate>(); var ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (object entry in entries)
                {
                    var row = Object(entry, "package choice"); string packageId = Text(row, "id", 256, true);
                    if (!ids.Add(packageId)) throw Bad("Duplicate candidate identity for " + id);
                    string candidateTitle = Text(row, "titleId", 16, false);
                    if (candidateTitle.Length > 0 && TitleId(candidateTitle) != id) throw Bad("Package title does not match selected regional title: " + id);
                    string region = NormalizeRegion(Text(row, "region", 32, false));
                    if (region.Length > 0 && record.Title.Region.Length > 0 && region != record.Title.Region) throw Bad("Package region contradicts indexed title: " + id);
                    string publishedKind = Text(row, "kind", 16, true); string kind = CheckKind(publishedKind);
                    var candidate = new PackageCandidate { SourceId = catalog.Descriptor.SourceId, SourceVersion = catalog.Descriptor.Version,
                        SourceAttribution = catalog.Descriptor.DisplayName, CandidateId = packageId, TitleId = id, DisplayName = record.Title.DisplayName,
                        Region = record.Title.Region.Length > 0 ? record.Title.Region : region, Url = Url(Text(row, "url", 4096, true), true), PackageKindHint = kind,
                        Label = Text(row, "label", 512, false), HosterName = Text(row, "hoster", 256, false),
                        PackageVersion = Text(row, "version", 64, false), RequiredFirmware = Text(row, "requiredFirmware", 64, false),
                        PackageGroupId = Text(row, "groupId", 256, false), SourcePageUrl = Url(Text(row, "sourcePageUrl", 2048, false), false),
                        ArchivePassword = Text(row, "archivePassword", 256, false),
                        ArchivePasswords = PasswordDefaults(row),
                        ExpectedSha256 = Sha(Text(row, "sha256", 64, false)),
                        ExpectedContentId = Text(row, "contentId", 128, false), AccessType = Access(Text(row, "accessType", 32, true)) };
                    if (publishedKind == "backport" && candidate.Label.IndexOf("backport", StringComparison.OrdinalIgnoreCase) < 0)
                        candidate.Label = candidate.Label.Length == 0 ? "Backport" : "Backport - " + candidate.Label;
                    long size = Integer(row, "size", false); if (size > 0) candidate.ExpectedByteSize = size;
                    object volumesValue = Value(row, "volumes", false);
                    if (volumesValue != null)
                    {
                        var volumes = volumesValue as IList;
                        if (volumes == null || volumes.Count < 2 || volumes.Count > ArchiveVolumeSet.MaximumVolumes) throw Bad("Multipart choice needs a complete declared sequence");
                        var set = new List<ArchiveVolume>(); var urls = new HashSet<string>(StringComparer.Ordinal);
                        foreach (object part in volumes)
                        {
                            var volume = Object(part, "archive volume"); string url = Url(Text(volume, "url", 4096, true), true);
                            if (!urls.Add(url)) throw Bad("Duplicate multipart URL");
                            set.Add(new ArchiveVolume { Url = url, Name = Text(volume, "name", 240, true),
                                AccessType = Access(Text(volume, "accessType", 32, true)).ToString(),
                                Size = Integer(volume, "size", false), Sha256 = Sha(Text(volume, "sha256", 64, false)) });
                        }
                        var ordered = ArchiveVolumeSet.Validate(set);
                        for (int i = 0; i < set.Count; i++) if (!object.ReferenceEquals(set[i], ordered[i])) throw Bad("Archive volumes must be in numeric order");
                        if (candidate.Url != ordered[0].Url || candidate.AccessType.ToString() != ordered[0].AccessType) throw Bad("Multipart parent must match its first volume");
                        candidate.ArchiveVolumes = ArchiveVolumeSet.Encode(ordered);
                    }
                    else
                    {
                        string name = Text(row, "name", 512, false), archiveSet; int partIndex;
                        if (name.Length == 0) name = ArchiveVolumeSet.FileName(candidate.Url, candidate.Label);
                        if (ArchiveVolumeSet.TryIndex(name, out archiveSet, out partIndex) && (partIndex > 1 || archiveSet.EndsWith(".part*.rar", StringComparison.OrdinalIgnoreCase))) {
                            if (candidate.AccessType == PackageAccessType.Direct) throw Bad("Numbered RAR needs its declared multipart volume sequence");
                            candidate.ResolutionError = "This mirror is missing its archive volume list; choose another mirror or update the source";
                        }
                    }
                    rows.Add(candidate);
                }
                if (rows.Count != record.ReleaseCount) throw Bad("Indexed package count disagrees with its shard: " + id);
                result.Add(id, rows);
            }
            foreach (Record record in catalog.Records)
                if (record.PackagesFile == path && !result.ContainsKey(record.Title.TitleId)) throw Bad("Indexed title has no package rows: " + record.Title.TitleId);
            return result;
        }

        static List<string> Paths(Dictionary<string, object> root, string key, string prefix, HashSet<string> declared)
        {
            var result = new List<string>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object value in Array(root, key, 62, true))
            {
                string path = RelativePath(value as string);
                if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.Ordinal) || !declared.Contains(path) || !seen.Add(path)) throw Bad("Invalid catalog file reference");
                result.Add(path);
            }
            if (result.Count == 0) throw Bad("Catalog file list is empty");
            return result;
        }
        static string RelativePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 180 || path.IndexOfAny(new[] { '\\', ':', '\0', '\r', '\n' }) >= 0 || path.StartsWith("/", StringComparison.Ordinal)) throw Bad("Invalid catalog path");
            foreach (char c in path) if (char.IsControl(c)) throw Bad("Control character in catalog path");
            foreach (string part in path.Split('/')) if (part.Length == 0 || part == "." || part == "..") throw Bad("Invalid catalog path component");
            return path;
        }
        static byte[] ReadContained(string root, string relative, int maximum)
        {
            RelativePath(relative); string path = root;
            if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw Bad("Linked catalog directories are not allowed");
            foreach (string component in relative.Split('/'))
            {
                path = Path.Combine(path, component);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Bad("Linked catalog files are not allowed");
            }
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 1 || info.Length > maximum) throw Bad("Catalog file is missing or oversized");
            var data = new byte[(int)info.Length];
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            { int offset = 0; while (offset < data.Length) { int count = input.Read(data, offset, data.Length - offset); if (count == 0) throw Bad("Catalog file was truncated"); offset += count; } if (input.ReadByte() != -1) throw Bad("Catalog file changed during reading"); }
            return data;
        }
        static Dictionary<string, object> Parse(byte[] data, int maximum)
        { if (data == null || data.Length == 0 || data.Length > maximum) throw Bad("Catalog JSON is missing or oversized"); return Object(StrictJson.Parse(Utf8(data)), "catalog JSON"); }
        static string Utf8(byte[] data) { try { return new UTF8Encoding(false, true).GetString(data); } catch (ArgumentException) { throw Bad("Catalog JSON is not valid UTF-8"); } }
        static Dictionary<string, object> Object(object value, string label) { var row = value as Dictionary<string, object>; if (row == null) throw Bad(label + " must be an object"); return row; }
        static object Value(Dictionary<string, object> row, string key, bool required) { object value; if (!row.TryGetValue(key, out value)) { if (required) throw Bad("Missing catalog field: " + key); return null; } return value; }
        static IList Array(Dictionary<string, object> row, string key, int maximum, bool required) { object value = Value(row, key, required); var list = value as IList; if (list == null || list.Count > maximum) throw Bad("Invalid catalog array: " + key); return list; }
        static string Text(Dictionary<string, object> row, string key, int maximum, bool required)
        {
            object value = Value(row, key, required); if (value == null && !required) return "";
            string text = value as string; if (text == null || text.Length > maximum || (required && text.Trim().Length == 0)) throw Bad("Invalid catalog text field: " + key);
            if (key == "label") text = text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            for (int i = 0; i < text.Length; i++)
            { if (char.IsControl(text[i])) throw Bad("Control character in catalog field: " + key); if (char.IsSurrogate(text[i])) { if (!char.IsHighSurrogate(text[i]) || i + 1 >= text.Length || !char.IsLowSurrogate(text[++i])) throw Bad("Invalid Unicode in catalog field: " + key); } }
            return text;
        }
        static long Integer(Dictionary<string, object> row, string key, bool required) { object value = Value(row, key, required); if (value == null && !required) return 0; if (!(value is long) || (long)value < 0) throw Bad("Invalid catalog integer: " + key); return (long)value; }
        static string TitleId(string value) { value = (value ?? "").ToUpperInvariant(); if (!TitlePattern.IsMatch(value)) throw Bad("Unsupported catalog title identifier"); return value; }
        static string CheckKind(string kind) { if (kind != "base" && kind != "update" && kind != "dlc" && kind != "unknown" && kind != "backport") throw Bad("Unsupported package kind"); return kind == "backport" ? "unknown" : kind; }
        static PackageAccessType Access(string value) { if (value == "Direct") return PackageAccessType.Direct; if (value == "HosterLanding") return PackageAccessType.HosterLanding; throw Bad("Unsupported package access type"); }
        static string Sha(string value) { if (value.Length > 0 && (value.Length != 64 || !Regex.IsMatch(value, "\\A[0-9a-fA-F]{64}\\z"))) throw Bad("Invalid package SHA-256"); return value; }
        static string Url(string value, bool required)
        {
            if (value.Length == 0 && !required) return ""; Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host)) throw Bad("Catalog URL must be HTTP or HTTPS without embedded credentials");
            return value;
        }
        static string Hash(byte[] value) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", "").ToLowerInvariant(); }
        static string PasswordDefaults(Dictionary<string, object> row)
        {
            object value = Value(row, "archivePasswords", false);
            if (value != null && !(value is IList)) throw Bad("archivePasswords must be an array");
            try { return ArchivePasswordDefaults.Encode(value as IList); }
            catch (FormatException ex) { throw Bad(ex.Message); }
        }
        static InvalidDataException Bad(string message) { return new InvalidDataException("Static catalog: " + message); }
        static void CheckCanceled(Func<bool> cancel) { if (cancel != null && cancel()) throw new OperationCanceledException(); }
        internal static int CompareTitle(SourceTitleResult a, SourceTitleResult b)
        { int n = StringComparer.OrdinalIgnoreCase.Compare(a.DisplayName, b.DisplayName); if (n != 0) return n; n = StringComparer.Ordinal.Compare(a.SourceId, b.SourceId); if (n != 0) return n; n = StringComparer.Ordinal.Compare(a.TitleId, b.TitleId); return n != 0 ? n : StringComparer.Ordinal.Compare(a.Region, b.Region); }
        internal static SourceTitleResult CloneTitle(SourceTitleResult value)
        { return new SourceTitleResult { SourceId = value.SourceId, SourceVersion = value.SourceVersion, SourceAttribution = value.SourceAttribution, StableResultId = value.StableResultId, TitleId = value.TitleId, DisplayName = value.DisplayName, Region = value.Region, ImageUrl = value.ImageUrl, CatalogUrl = value.CatalogUrl, Rating = value.Rating, Genres = value.Genres, Backport = value.Backport, SearchRankHint = value.SearchRankHint, Variants = value.Variants == null ? null : new List<SourceTitleResult>(value.Variants) }; }
        static PackageCandidate CloneCandidate(PackageCandidate value)
        { return new PackageCandidate { SourceId = value.SourceId, SourceVersion = value.SourceVersion, SourceAttribution = value.SourceAttribution, CandidateId = value.CandidateId, TitleId = value.TitleId, DisplayName = value.DisplayName, Region = value.Region, PackageKindHint = value.PackageKindHint, PackageVersion = value.PackageVersion, RequiredFirmware = value.RequiredFirmware, PackageGroupId = value.PackageGroupId, HosterName = value.HosterName, Label = value.Label, Url = value.Url, AccessType = value.AccessType, SourcePageUrl = value.SourcePageUrl, ExpectedByteSize = value.ExpectedByteSize, ExpectedSha256 = value.ExpectedSha256, ExpectedContentId = value.ExpectedContentId, ArchiveVolumes = value.ArchiveVolumes, ArchivePassword = value.ArchivePassword, ArchivePasswords = value.ArchivePasswords, ResolutionError = value.ResolutionError }; }
    }
}
