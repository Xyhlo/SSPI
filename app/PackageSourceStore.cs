using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Orbis
{
    internal sealed class PackageSourceRegistryEntry
    {
        public string SourceId = "";
        public string Version = "";
        public string Name = "";
        public bool Enabled = true;
        public string Path = "";
        public string EngineType = "";
        public string EntryFile = "";
        public PackageSourceCapability Capabilities;
        public string Trust = "";
        public string PackageSha256 = "";
        public readonly List<string> Origins = new List<string>();
        public PackageSourceDescriptor Descriptor = new PackageSourceDescriptor();
    }

    /// <summary>
    /// Transactional registry and immutable installed-version store. All delete and move targets
    /// are derived from validated identifiers and checked against the app-owned sources root.
    /// </summary>
    internal sealed class PackageSourceStore
    {
        readonly object _lock = new object();
        readonly string _root;
        readonly string _installedRoot;
        readonly string _stagingRoot;
        readonly string _registryPath;
        List<PackageSourceRegistryEntry> _entries;

        public PackageSourceStore()
            : this(Path.Combine(AppSettings.DataDir, "sources")) { }

        internal PackageSourceStore(string root)
        {
            if (string.IsNullOrEmpty(root)) throw new ArgumentException("Source store root is empty");
            _root = Path.GetFullPath(root);
            _installedRoot = Path.Combine(_root, "installed");
            _stagingRoot = Path.Combine(_root, "staging");
            _registryPath = Path.Combine(_root, "registry.json");
            Directory.CreateDirectory(_installedRoot);
            Directory.CreateDirectory(_stagingRoot);
            _entries = LoadRegistry();
            CleanupStaging();
        }

        public List<PackageSourceRegistryEntry> GetInstalledSources()
        {
            lock (_lock) return CloneList(_entries, false);
        }

        internal void ApplyBundledSources(string directory)
        {
            string manifest = Path.Combine(directory, "bundles.json");
            if (!File.Exists(manifest)) return;
            var root = PackageSourceJson.Parse(File.ReadAllText(manifest)) as Dictionary<string, object>;
            object value;
            if (root == null || !root.TryGetValue("packages", out value) || !(value is System.Collections.IList)) return;
            foreach (var item in (System.Collections.IList)value)
            {
                var row = item as Dictionary<string, object>;
                object file;
                if (row == null || !row.TryGetValue("file", out file)) continue;
                string name = file as string;
                if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name)) continue;
                byte[] bytes = File.ReadAllBytes(Path.Combine(directory, name));
                string hash;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
                string marker = Path.Combine(_root, "bundled-" + hash + ".done");
                if (File.Exists(marker)) continue;
                // Commit the replacement before retiring the old source. A failed
                // install leaves the current registry and sources usable.
                var installed = Install(bytes);
                object replacements;
                if (row.TryGetValue("replaces", out replacements) && replacements is System.Collections.IList)
                    foreach (var old in (System.Collections.IList)replacements)
                        if (old is string && (string)old != installed.SourceId) Remove((string)old);
                AtomicFile.WriteText(marker, installed.SourceId + "\n" + installed.Version);
            }
        }

        public List<PackageSourceRegistryEntry> GetInstalled() { return GetInstalledSources(); }

        public List<PackageSourceRegistryEntry> InstalledSources { get { return GetInstalledSources(); } }

        public List<PackageSourceRegistryEntry> GetEnabledSources()
        {
            lock (_lock) return CloneList(_entries, true);
        }

        public List<PackageSourceRegistryEntry> EnabledSources { get { return GetEnabledSources(); } }

        public PackageSourceRegistryEntry Install(string packagePath)
        {
            return Commit(PackageSourcePackage.Open(packagePath));
        }

        public PackageSourceRegistryEntry Install(byte[] packageBytes)
        {
            return Commit(PackageSourcePackage.Open(packageBytes));
        }

        public PackageSourceRegistryEntry InstallFromFile(string packagePath) { return Install(packagePath); }
        public PackageSourceRegistryEntry InstallBytes(byte[] packageBytes) { return Install(packageBytes); }

        PackageSourceRegistryEntry Commit(PackageSourcePackage package)
        {
            string id = package.Descriptor.SourceId;
            string version = package.Descriptor.Version;
            string sourceRoot = OwnedChild(_installedRoot, id);
            string finalPath = OwnedChild(sourceRoot, version);
            string staging = OwnedChild(_stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            bool moved = false;
            try
            {
                package.ExtractTo(staging);
                lock (_lock)
                {
                    bool enabled = true;
                    for (int i = 0; i < _entries.Count; i++)
                        if (string.Equals(_entries[i].SourceId, id, StringComparison.Ordinal))
                            enabled = _entries[i].Enabled;

                    if (Directory.Exists(finalPath))
                    {
                        string existingDescriptor = Path.Combine(finalPath, "source.json");
                        if (!File.Exists(existingDescriptor) ||
                            !BytesEqual(File.ReadAllBytes(existingDescriptor), package.GetFile("source.json")))
                            throw new InvalidDataException("Source id/version already exists with different contents");
                        if (!DirectoryTreeEqual(finalPath, staging))
                            throw new InvalidDataException("Source id/version already exists with different payload files");
                        Directory.Delete(staging, true);
                    }
                    else
                    {
                        if (!Directory.Exists(sourceRoot)) Directory.CreateDirectory(sourceRoot);
                        Directory.Move(staging, finalPath);
                        moved = true;
                    }

                    var entry = FromPackage(package, finalPath, enabled);
                    var replacement = new List<PackageSourceRegistryEntry>();
                    foreach (var old in _entries)
                        if (!string.Equals(old.SourceId, id, StringComparison.Ordinal)) replacement.Add(old);
                    replacement.Add(entry);
                    replacement.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    SaveRegistry(replacement);
                    _entries = replacement;
                    return Clone(entry);
                }
            }
            catch
            {
                TryDeleteDirectory(staging);
                // If activation failed after move, remove only the just-created immutable version.
                if (moved) TryDeleteDirectory(finalPath);
                throw;
            }
        }

        public bool SetEnabled(string sourceId, bool enabled)
        {
            lock (_lock)
            {
                var replacement = CloneList(_entries, false);
                PackageSourceRegistryEntry found = null;
                foreach (var entry in replacement)
                    if (string.Equals(entry.SourceId, sourceId, StringComparison.Ordinal)) { found = entry; break; }
                if (found == null) return false;
                if (found.Enabled == enabled) return true;
                found.Enabled = enabled;
                SaveRegistry(replacement);
                _entries = replacement;
                return true;
            }
        }

        public bool Enable(string sourceId) { return SetEnabled(sourceId, true); }
        public bool Disable(string sourceId) { return SetEnabled(sourceId, false); }
        public bool SetSourceEnabled(string sourceId, bool enabled) { return SetEnabled(sourceId, enabled); }

        public bool Remove(string sourceId)
        {
            lock (_lock)
            {
                PackageSourceRegistryEntry found = null;
                var replacement = new List<PackageSourceRegistryEntry>();
                foreach (var entry in _entries)
                {
                    if (string.Equals(entry.SourceId, sourceId, StringComparison.Ordinal)) found = entry;
                    else replacement.Add(entry);
                }
                if (found == null) return false;

                // Commit registry removal first. A failed directory cleanup leaves inert files,
                // never a registry reference to a half-removed source.
                SaveRegistry(replacement);
                _entries = replacement;
                string ownedSourceRoot = OwnedChild(_installedRoot, found.SourceId);
                TryDeleteDirectory(ownedSourceRoot);
                return true;
            }
        }

        public bool RemoveSource(string sourceId) { return Remove(sourceId); }

        public string ReadEntryFile(PackageSourceRegistryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            string sourceRoot = OwnedChild(OwnedChild(_installedRoot, entry.SourceId), entry.Version);
            string relative = (entry.EntryFile ?? "").Replace('/', Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(Path.Combine(sourceRoot, relative));
            string prefix = sourceRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? sourceRoot : sourceRoot + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                throw new InvalidDataException("Package Source entry file is missing or outside its package");
            var info = new FileInfo(full);
            if (info.Length > PackageSourcePackage.MaximumEntryBytes)
                throw new InvalidDataException("Package Source entry file is too large");
            return new UTF8Encoding(false, true).GetString(File.ReadAllBytes(full));
        }

        List<PackageSourceRegistryEntry> LoadRegistry()
        {
            if (!File.Exists(_registryPath))
            {
                string recovery = _registryPath + ".bak";
                if (!File.Exists(recovery)) return new List<PackageSourceRegistryEntry>();
                return ParseRegistry(File.ReadAllText(recovery, Encoding.UTF8));
            }
            try { return ParseRegistry(File.ReadAllText(_registryPath, Encoding.UTF8)); }
            catch
            {
                string backup = _registryPath + ".bak";
                if (!File.Exists(backup)) throw;
                return ParseRegistry(File.ReadAllText(backup, Encoding.UTF8));
            }
        }

        List<PackageSourceRegistryEntry> ParseRegistry(string json)
        {
            var root = PackageSourceJson.Parse(json) as Dictionary<string, object>;
            object listValue;
            var list = root != null && root.TryGetValue("sources", out listValue) ? listValue as List<object> : null;
            if (list == null) throw new InvalidDataException("Invalid Package Sources registry");
            var result = new List<PackageSourceRegistryEntry>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in list)
            {
                var obj = value as Dictionary<string, object>;
                if (obj == null) throw new InvalidDataException("Invalid registry source entry");
                var item = new PackageSourceRegistryEntry
                {
                    SourceId = Text(obj, "id"),
                    Version = Text(obj, "version"),
                    Name = Text(obj, "name"),
                    Enabled = Bool(obj, "enabled", true),
                    Path = Text(obj, "path"),
                    EngineType = Text(obj, "engine_type"),
                    EntryFile = Text(obj, "entry_file"),
                    Capabilities = (PackageSourceCapability)Integer(obj, "capabilities", 0),
                    Trust = Text(obj, "trust"),
                    PackageSha256 = Text(obj, "package_sha256")
                };
                if (!ids.Add(item.SourceId)) throw new InvalidDataException("Duplicate source id in registry");
                string expected = OwnedChild(OwnedChild(_installedRoot, item.SourceId), item.Version);
                if (!string.Equals(Path.GetFullPath(item.Path), expected, StringComparison.OrdinalIgnoreCase) ||
                    !Directory.Exists(expected)) throw new InvalidDataException("Registry source path is invalid");
                object originsValue;
                var origins = obj.TryGetValue("origins", out originsValue) ? originsValue as List<object> : null;
                if (origins != null) foreach (object origin in origins)
                {
                    string text = origin as string;
                    if (text == null) throw new InvalidDataException("Invalid registry origin");
                    item.Origins.Add(text);
                }
                item.Descriptor.SourceId = item.SourceId;
                item.Descriptor.Version = item.Version;
                item.Descriptor.DisplayName = item.Name;
                item.Descriptor.Engine.Type = item.EngineType;
                item.Descriptor.Engine.EntryFile = item.EntryFile;
                item.Descriptor.Capabilities = item.Capabilities;
                foreach (string origin in item.Origins) item.Descriptor.Permissions.NetworkOrigins.Add(origin);
                result.Add(item);
            }
            return result;
        }

        void SaveRegistry(List<PackageSourceRegistryEntry> entries)
        {
            var text = new StringBuilder();
            text.Append("{\"version\":1,\"sources\":[");
            for (int i = 0; i < entries.Count; i++)
            {
                if (i != 0) text.Append(',');
                var item = entries[i];
                text.Append("{\"id\":\"").Append(JsonLite.Escape(item.SourceId));
                text.Append("\",\"version\":\"").Append(JsonLite.Escape(item.Version));
                text.Append("\",\"name\":\"").Append(JsonLite.Escape(item.Name));
                text.Append("\",\"enabled\":").Append(item.Enabled ? "true" : "false");
                text.Append(",\"path\":\"").Append(JsonLite.Escape(item.Path));
                text.Append("\",\"engine_type\":\"").Append(JsonLite.Escape(item.EngineType));
                text.Append("\",\"entry_file\":\"").Append(JsonLite.Escape(item.EntryFile));
                text.Append("\",\"capabilities\":").Append((int)item.Capabilities);
                text.Append(",\"trust\":\"").Append(JsonLite.Escape(item.Trust));
                text.Append("\",\"package_sha256\":\"").Append(JsonLite.Escape(item.PackageSha256));
                text.Append("\",\"origins\":[");
                for (int j = 0; j < item.Origins.Count; j++)
                {
                    if (j != 0) text.Append(',');
                    text.Append('"').Append(JsonLite.Escape(item.Origins[j])).Append('"');
                }
                text.Append("]}");
            }
            text.Append("]}");

            Directory.CreateDirectory(_root);
            string temp = _registryPath + ".tmp";
            string backup = _registryPath + ".bak";
            File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
            if (File.Exists(_registryPath)) File.Copy(_registryPath, backup, true);
            if (File.Exists(_registryPath)) File.Delete(_registryPath);
            File.Move(temp, _registryPath);
        }

        static PackageSourceRegistryEntry FromPackage(PackageSourcePackage package, string path, bool enabled)
        {
            var result = new PackageSourceRegistryEntry
            {
                SourceId = package.Descriptor.SourceId,
                Version = package.Descriptor.Version,
                Name = package.Descriptor.DisplayName,
                Enabled = enabled,
                Path = Path.GetFullPath(path),
                EngineType = package.Descriptor.Engine.Type,
                EntryFile = package.Descriptor.Engine.EntryFile,
                Capabilities = package.Descriptor.Capabilities,
                Trust = package.Trust,
                PackageSha256 = package.PackageSha256,
                Descriptor = package.Descriptor
            };
            foreach (string origin in package.Descriptor.Permissions.NetworkOrigins) result.Origins.Add(origin);
            return result;
        }

        static List<PackageSourceRegistryEntry> CloneList(List<PackageSourceRegistryEntry> source, bool enabledOnly)
        {
            var result = new List<PackageSourceRegistryEntry>();
            foreach (var item in source) if (!enabledOnly || item.Enabled) result.Add(Clone(item));
            return result;
        }

        static PackageSourceRegistryEntry Clone(PackageSourceRegistryEntry source)
        {
            var result = new PackageSourceRegistryEntry
            {
                SourceId = source.SourceId, Version = source.Version, Name = source.Name,
                Enabled = source.Enabled, Path = source.Path, EngineType = source.EngineType,
                EntryFile = source.EntryFile, Capabilities = source.Capabilities,
                Trust = source.Trust, PackageSha256 = source.PackageSha256,
                Descriptor = source.Descriptor
            };
            foreach (string origin in source.Origins) result.Origins.Add(origin);
            return result;
        }

        string OwnedChild(string root, string segment)
        {
            if (string.IsNullOrEmpty(segment) || segment == "." || segment == ".." ||
                segment.IndexOf('/') >= 0 || segment.IndexOf('\\') >= 0 || segment.IndexOf(':') >= 0)
                throw new InvalidDataException("Unsafe Package Source path component");
            string basePath = Path.GetFullPath(root);
            string child = Path.GetFullPath(Path.Combine(basePath, segment));
            string prefix = basePath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? basePath : basePath + Path.DirectorySeparatorChar;
            if (!child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Package Source path escaped its store");
            return child;
        }

        void CleanupStaging()
        {
            try
            {
                foreach (string directory in Directory.GetDirectories(_stagingRoot))
                    TryDeleteDirectory(directory);
                foreach (string file in Directory.GetFiles(_stagingRoot))
                    try { File.Delete(file); } catch { }
            }
            catch { }
        }

        static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            int difference = 0;
            for (int i = 0; i < a.Length; i++) difference |= a[i] ^ b[i];
            return difference == 0;
        }

        static bool DirectoryTreeEqual(string left, string right)
        {
            string[] leftFiles = Directory.GetFiles(left, "*", SearchOption.AllDirectories);
            string[] rightFiles = Directory.GetFiles(right, "*", SearchOption.AllDirectories);
            if (leftFiles.Length != rightFiles.Length) return false;
            string leftPrefix = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string rightPrefix = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (string leftFile in leftFiles)
            {
                string relative = Path.GetFullPath(leftFile).Substring(leftPrefix.Length);
                string rightFile = Path.Combine(rightPrefix, relative);
                if (!File.Exists(rightFile) || !BytesEqual(File.ReadAllBytes(leftFile), File.ReadAllBytes(rightFile)))
                    return false;
            }
            return true;
        }

        static string Text(Dictionary<string, object> obj, string key)
        {
            object value;
            string text = obj.TryGetValue(key, out value) ? value as string : null;
            if (string.IsNullOrEmpty(text)) throw new InvalidDataException("Registry field is missing: " + key);
            return text;
        }

        static bool Bool(Dictionary<string, object> obj, string key, bool defaultValue)
        {
            object value;
            if (!obj.TryGetValue(key, out value)) return defaultValue;
            if (!(value is bool)) throw new InvalidDataException("Registry boolean is invalid: " + key);
            return (bool)value;
        }

        static long Integer(Dictionary<string, object> obj, string key, long defaultValue)
        {
            object value;
            if (!obj.TryGetValue(key, out value)) return defaultValue;
            if (!(value is long)) throw new InvalidDataException("Registry integer is invalid: " + key);
            return (long)value;
        }
    }
}
