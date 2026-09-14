using System;
using System.Collections.Generic;
using System.IO;

namespace Orbis
{
    /// <summary>Typed runtime wrapper for one installed Package Source version.</summary>
    internal sealed class PackageSourceRuntimeAdapter : IPackageSourceRuntime
    {
        readonly InstalledPackageSource _source;
        readonly string _versionPath;
        readonly string _engineType;

        public PackageSourceRuntimeAdapter(PackageSourceRegistryEntry entry)
        {
            if (entry == null) throw new ArgumentNullException("entry");
            _engineType = (entry.EngineType ??
                (entry.Descriptor != null && entry.Descriptor.Engine != null
                    ? entry.Descriptor.Engine.Type : "")).Trim();
            _versionPath = entry.Path ?? "";
            _source = new InstalledPackageSource
            {
                SourceId = entry.SourceId ?? "",
                Enabled = entry.Enabled,
                Priority = 0,
                Descriptor = entry.Descriptor ?? new PackageSourceDescriptor
                {
                    SourceId = entry.SourceId ?? "",
                    DisplayName = entry.Name ?? "",
                    Version = entry.Version ?? "",
                    Engine = new PackageSourceEngineDescriptor
                    {
                        Type = _engineType,
                        EntryFile = entry.EntryFile ?? ""
                    }
                },
                Versions = new PackageSourceVersionState
                {
                    ActiveVersion = entry.Version ?? ""
                }
            };
            if (_source.Descriptor.Permissions == null)
                _source.Descriptor.Permissions = new PackageSourcePermission();
            if (_source.Descriptor.Permissions.NetworkOrigins == null)
                _source.Descriptor.Permissions.NetworkOrigins = new List<string>();
            if (entry.Origins != null)
            {
                foreach (string o in entry.Origins)
                    if (!string.IsNullOrEmpty(o) &&
                        !_source.Descriptor.Permissions.NetworkOrigins.Contains(o))
                        _source.Descriptor.Permissions.NetworkOrigins.Add(o);
            }
        }

        public InstalledPackageSource Source { get { return _source; } }

        public List<SourceTitleResult> Search(SourceSearchRequest request)
        {
            if (request == null) request = new SourceSearchRequest();
            if (_engineType == PackageSourceEngineStatic.EngineType)
                return PackageSourceEngineStatic.Search(_source, _versionPath, request);
            if (string.Equals(_engineType, "remote-api-v1", StringComparison.OrdinalIgnoreCase))
                return PackageSourceEngineRemote.Search(_source, _versionPath, request.Query ?? "");
            if ((string.Equals(_engineType, "recipe-v1", StringComparison.OrdinalIgnoreCase) || string.Equals(_engineType, "recipe-v2", StringComparison.OrdinalIgnoreCase)))
            {
                string error;
                List<SourceTitleResult> results = PackageSourceEngineRecipe.Search(
                    _source.Descriptor, _versionPath, request, out error);
                if (!string.IsNullOrEmpty(error)) throw new Exception(error);
                return results ?? new List<SourceTitleResult>();
            }
            return new List<SourceTitleResult>();
        }

        public List<PackageCandidate> Resolve(SourceResolveRequest request)
        {
            if (request == null) request = new SourceResolveRequest();
            if (_engineType == PackageSourceEngineStatic.EngineType)
                return PackageSourceEngineStatic.Resolve(_source, _versionPath, request);
            if (string.Equals(_engineType, "remote-api-v1", StringComparison.OrdinalIgnoreCase))
                return PackageSourceEngineRemote.Resolve(_source, _versionPath,
                    request.TitleId ?? "", request.Name ?? "");
            if ((string.Equals(_engineType, "recipe-v1", StringComparison.OrdinalIgnoreCase) || string.Equals(_engineType, "recipe-v2", StringComparison.OrdinalIgnoreCase)))
            {
                string error;
                List<PackageCandidate> results = PackageSourceEngineRecipe.Resolve(
                    _source.Descriptor, _versionPath, request, out error);
                if (!string.IsNullOrEmpty(error)) throw new Exception(error);
                return results ?? new List<PackageCandidate>();
            }
            return new List<PackageCandidate>();
        }
    }

    /// <summary>UI-facing store + coordinator facade without reflection.</summary>
    internal sealed class PackageSourceRuntimeBridge
    {
        readonly object _gate = new object();
        readonly string _bundledSourcesDirectory;
        PackageSourceStore _store;
        int _generation;
        sealed class SearchSnapshot
        { internal string Key, Fingerprint, Warning; internal int SavedTick; internal List<SourceTitleResult> Matches; }
        readonly LinkedList<SearchSnapshot> _searchSnapshots = new LinkedList<SearchSnapshot>();
        internal string BundledSourceError { get; private set; }

        public PackageSourceRuntimeBridge() : this(ApplicationBaseDirectory()) { }

        static string ApplicationBaseDirectory()
        {
            // After jailbreak the package is exposed through the launcher's sandbox
            // mount, not necessarily /app0. Use the same native path as other assets.
            try
            {
                string nativeBase = Orbis.Internals.IO.GetAppBaseDirectory();
                if (!string.IsNullOrWhiteSpace(nativeBase)) return nativeBase;
            }
            catch { }
            return AppDomain.CurrentDomain.BaseDirectory;
        }

        internal PackageSourceRuntimeBridge(string appBaseDirectory)
        {
            // Embedded PS4 Mono may not populate AppDomain.BaseDirectory.
            // Packaged assets are mounted at /app0 even in that case.
            _bundledSourcesDirectory = Path.Combine(
                string.IsNullOrWhiteSpace(appBaseDirectory) ? "/app0" : appBaseDirectory,
                "assets", "sources");
        }

        PackageSourceStore Store()
        {
            lock (_gate)
            {
                if (_store == null)
                {
                    var store = new PackageSourceStore();
                    try { store.ApplyBundledSources(_bundledSourcesDirectory); }
                    catch (Exception ex)
                    {
                        // An interrupted or invalid bundled update must not take
                        // already-working installed catalogs offline.
                        BundledSourceError = ex.Message;
                        try { AtomicFile.WriteText(Path.Combine(AppSettings.DataDir, "source-bundle-error.txt"), ex.GetType().Name + ": " + ex.Message); } catch { }
                        if (store.GetEnabledSources().Count == 0) throw;
                    }
                    _store = store;
                }
                return _store;
            }
        }

        PackageSourceCoordinator Coordinator()
        {
            var runtimes = new List<IPackageSourceRuntime>();
            foreach (var entry in Store().GetEnabledSources())
            {
                try { runtimes.Add(new PackageSourceRuntimeAdapter(entry)); }
                catch { }
            }
            return new PackageSourceCoordinator(runtimes);
        }

        internal string CatalogFingerprint()
        {
            var sources = new List<string>();
            foreach (var entry in Store().GetEnabledSources())
                sources.Add(entry.SourceId + "@" + entry.Version);
            sources.Sort(StringComparer.Ordinal);
            return string.Join("|", sources.ToArray());
        }

        public List<SourceUiEntry> List(out string error)
        {
            error = null;
            var result = new List<SourceUiEntry>();
            try
            {
                foreach (var entry in Store().GetInstalledSources())
                {
                    if (entry == null || string.IsNullOrEmpty(entry.SourceId)) continue;
                    result.Add(new SourceUiEntry
                    {
                        Id = entry.SourceId,
                        Name = string.IsNullOrEmpty(entry.Name) ? entry.SourceId : entry.Name,
                        Version = entry.Version ?? "",
                        Trust = entry.Trust ?? "",
                        Enabled = entry.Enabled
                    });
                }
            }
            catch (Exception ex) { error = RootMessage(ex); }
            return result;
        }

        public bool HasEnabled(out string error)
        {
            foreach (var item in List(out error))
                if (item.Enabled) return true;
            return false;
        }

        public bool Install(string path, out string error)
        {
            error = null;
            try
            {
                // Materialize before first store initialization cleans abandoned staging.
                var info = new FileInfo(path);
                if (!info.Exists || info.Length < 1 || info.Length > PackageSourcePackage.MaximumCompressedBytes)
                    throw new IOException("Source file is missing, empty or too large");
                byte[] bytes = File.ReadAllBytes(path);
                Store().Install(bytes);
                InvalidateCatalogs();
                return true;
            }
            catch (Exception ex) { error = RootMessage(ex); return false; }
        }

        public bool SetEnabled(string id, bool enabled, out string error)
        {
            error = null;
            try
            {
                if (!Store().SetEnabled(id, enabled))
                {
                    error = "Source not found";
                    return false;
                }
                InvalidateCatalogs();
                return true;
            }
            catch (Exception ex) { error = RootMessage(ex); return false; }
        }

        public bool Remove(string id, out string error)
        {
            error = null;
            try
            {
                if (!Store().Remove(id))
                {
                    error = "Source not found";
                    return false;
                }
                InvalidateCatalogs();
                return true;
            }
            catch (Exception ex) { error = RootMessage(ex); return false; }
        }

        public List<SourceTitleResult> Search(string query, out string error)
        {
            SourceSearchPage page = SearchPage(query, 0, 25, null, null, out error);
            return page == null ? null : page.Results;
        }

        internal bool Install(byte[] bytes, out string error)
        {
            error = null;
            try { Store().Install(bytes); InvalidateCatalogs(); return true; }
            catch (Exception ex) { error = RootMessage(ex); return false; }
        }

        public SourceSearchPage SearchPage(string query, int offset, int limit,
            Action<SourceSearchPage> progress, Func<bool> cancel, out string error, string region = "", bool freshSearch = false)
        {
            error = null;
            try
            {
                string fingerprint = CatalogFingerprint(); int generation = _generation;
                Func<bool> canceled = () => generation != _generation || (cancel != null && cancel());
                if (canceled()) throw new OperationCanceledException();
                string snapshotKey = fingerprint + "\n" + (query ?? "").Trim() + "\n" + PackageSourceEngineStatic.NormalizeRegion(region);
                SourceSearchPage cached = freshSearch ? null : CachedPage(snapshotKey, offset, limit);
                if (cached != null)
                {
                    if (canceled() || fingerprint != CatalogFingerprint()) throw new OperationCanceledException("Source changed during search");
                    LastPartialWarning = cached.Warning;
                    if (progress != null) progress(cached);
                    if (canceled() || fingerprint != CatalogFingerprint()) throw new OperationCanceledException("Source changed during search");
                    return cached;
                }
                PackageSourceCoordinator coordinator = Coordinator();
                Action<SourceSearchPage> publish = page => {
                    if (canceled() || fingerprint != CatalogFingerprint()) throw new OperationCanceledException("Source changed during search");
                    page.Fingerprint = fingerprint; page.Warning = FailedSourceMessage(coordinator.LastReports);
                    if (progress != null) progress(page);
                };
                SourceSearchPage results = coordinator.SearchPage(query, offset, limit, publish, canceled, region);
                if (canceled() || fingerprint != CatalogFingerprint()) throw new OperationCanceledException("Source changed during search");
                results.Fingerprint = fingerprint;
                LastPartialWarning = results.TotalMatches > 0
                    ? FailedSourceMessage(coordinator.LastReports) : null;
                results.Warning = LastPartialWarning;
                if (results.TotalMatches == 0)
                    error = FailedSourceMessage(coordinator.LastReports);
                if (string.IsNullOrEmpty(error)) SaveSnapshot(snapshotKey, results);
                if (canceled() || fingerprint != CatalogFingerprint()) throw new OperationCanceledException("Source changed during search");
                return string.IsNullOrEmpty(error) ? results : null;
            }
            catch (Exception ex) { error = RootMessage(ex); return null; }
        }

        public List<PackageCandidate> Resolve(string titleId, string name, out string error)
        {
            return Resolve(titleId, name, "", out error);
        }

        public List<PackageCandidate> Resolve(string titleId, string name, string region, out string error)
        {
            return Resolve(titleId, name, region, "", out error);
        }

        public List<PackageCandidate> Resolve(string titleId, string name, string region,
            string catalogUrl, out string error)
        { return Resolve(titleId, name, region, catalogUrl, "", "", out error); }

        public List<PackageCandidate> Resolve(string titleId, string name, string region,
            string catalogUrl, string sourceId, string sourceVersion, out string error)
        {
            error = null;
            try
            {
                PackageSourceCoordinator coordinator = Coordinator();
                string fingerprint = CatalogFingerprint(); int generation = _generation;
                if (!string.IsNullOrEmpty(sourceId))
                {
                    bool found = false;
                    foreach (var entry in Store().GetEnabledSources())
                        if (entry.SourceId == sourceId && (string.IsNullOrEmpty(sourceVersion) || entry.Version == sourceVersion)) { found = true; break; }
                    if (!found) throw new InvalidOperationException("The selected source changed or is disabled. Search again.");
                }
                List<PackageCandidate> results = coordinator.Resolve(titleId, name, region, catalogUrl, sourceId, sourceVersion,
                    PackageSourceEngineRemote.MaxPackages, () => generation != _generation);
                if (generation != _generation || fingerprint != CatalogFingerprint()) throw new OperationCanceledException("Source changed during package lookup");
                LastPartialWarning = results.Count > 0
                    ? FailedSourceMessage(coordinator.LastReports) : null;
                if (results.Count == 0)
                    error = FailedSourceMessage(coordinator.LastReports);
                return string.IsNullOrEmpty(error) ? results : null;
            }
            catch (Exception ex) { error = RootMessage(ex); return null; }
        }

        void InvalidateCatalogs()
        { System.Threading.Interlocked.Increment(ref _generation); lock (_gate) _searchSnapshots.Clear(); PackageSourceEngineStatic.Invalidate(); }

        SourceSearchPage CachedPage(string key, int offset, int limit)
        {
            offset = Math.Max(0, offset); limit = limit <= 0 ? 25 : Math.Min(250, limit);
            lock (_gate)
            {
                for (var node = _searchSnapshots.First; node != null; node = node.Next)
                {
                    SearchSnapshot item = node.Value;
                    if (item.Key != key) continue;
                    int elapsed = unchecked(Environment.TickCount - item.SavedTick);
                    if (offset == 0 && (elapsed < 0 || elapsed > (string.IsNullOrEmpty(item.Warning) ? 120000 : 15000))) { _searchSnapshots.Remove(node); return null; }
                    _searchSnapshots.Remove(node); _searchSnapshots.AddFirst(item);
                    var page = new SourceSearchPage { Offset = offset, PageSize = limit, TotalMatches = item.Matches.Count,
                        Fingerprint = item.Fingerprint, Warning = item.Warning, IsComplete = true };
                    for (int i = offset; i < item.Matches.Count && page.Results.Count < limit; i++) page.Results.Add(PackageSourceEngineStatic.CloneTitle(item.Matches[i]));
                    if (offset + page.Results.Count < item.Matches.Count) page.NextCursor = (offset + page.Results.Count).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return page;
                }
            }
            return null;
        }

        void SaveSnapshot(string key, SourceSearchPage page)
        {
            if (!page.IsComplete || page.AllMatches == null) return;
            lock (_gate)
            {
                for (var node = _searchSnapshots.First; node != null; node = node.Next)
                    if (node.Value.Key == key) { _searchSnapshots.Remove(node); break; }
                var matches = new List<SourceTitleResult>(page.AllMatches.Count);
                foreach (var title in page.AllMatches) matches.Add(PackageSourceEngineStatic.CloneTitle(title));
                _searchSnapshots.AddFirst(new SearchSnapshot { Key = key, Fingerprint = page.Fingerprint,
                    Warning = page.Warning, SavedTick = Environment.TickCount, Matches = matches });
                int rows = 0; foreach (var item in _searchSnapshots) rows += item.Matches.Count;
                while (_searchSnapshots.Count > 4 || rows > 60000)
                { rows -= _searchSnapshots.Last.Value.Matches.Count; _searchSnapshots.RemoveLast(); }
            }
        }

        /// <summary>Failure text from sources that failed while others succeeded.
        /// Null when the last call fully succeeded or failed outright (see error).</summary>
        public string LastPartialWarning { get; private set; }

        static string FailedSourceMessage(List<SourceExecutionReport> reports)
        {
            if (reports == null || reports.Count == 0) return null;
            int failures = 0;
            string first = null;
            foreach (SourceExecutionReport report in reports)
            {
                if (report == null || report.Success) continue;
                failures++;
                if (string.IsNullOrEmpty(first)) first = report.Message;
            }
            if (failures == 0) return null;
            string message = string.IsNullOrEmpty(first) ? "Source request failed" : first;
            if (failures > 1) message += " (and " + (failures - 1) + " more source failures)";
            return message;
        }

        static string RootMessage(Exception ex)
        {
            while (ex != null && ex.InnerException != null) ex = ex.InnerException;
            string m = ex == null ? "error" : (ex.Message ?? "error");
            if (m.Length > 96) m = m.Substring(0, 96);
            return m;
        }
    }

    internal sealed class SourceUiEntry
    {
        public string Id;
        public string Name;
        public string Version;
        public string Trust;
        public bool Enabled;
    }
}
