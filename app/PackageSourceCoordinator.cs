using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Orbis
{
    /// <summary>
    /// Narrow runtime boundary used by the coordinator. Implementations may be remote-api-v1 or
    /// recipe-v1 engines, but neither the coordinator nor an engine receives AppSettings or a
    /// Link Service.
    /// </summary>
    internal interface IPackageSourceRuntime
    {
        InstalledPackageSource Source { get; }
        List<SourceTitleResult> Search(SourceSearchRequest request);
        List<PackageCandidate> Resolve(SourceResolveRequest request);
    }

    /// <summary>
    /// Coordinates enabled Package Sources and isolates failures so one bad source cannot discard
    /// another source's results. Ordering and duplicate selection are deterministic.
    /// </summary>
    internal sealed class PackageSourceCoordinator
    {
        readonly List<IPackageSourceRuntime> _runtimes = new List<IPackageSourceRuntime>();
        readonly List<SourceExecutionReport> _lastReports = new List<SourceExecutionReport>();
        readonly object _sync = new object();

        public PackageSourceCoordinator(IEnumerable<IPackageSourceRuntime> runtimes)
        {
            if (runtimes == null) return;
            foreach (var runtime in runtimes)
                if (runtime != null) _runtimes.Add(runtime);
            _runtimes.Sort(CompareRuntime);
        }

        public List<SourceExecutionReport> LastReports
        {
            get
            {
                lock (_sync) return new List<SourceExecutionReport>(_lastReports);
            }
        }

        public bool HasEnabledSources
        {
            get
            {
                foreach (var runtime in _runtimes)
                    if (IsEnabled(runtime)) return true;
                return false;
            }
        }

        public List<SourceTitleResult> Search(string query, int limit)
        {
            return SearchPage(query, 0, limit, null, null).Results;
        }

        public SourceSearchPage SearchPage(string query, int offset, int limit,
            Action<SourceSearchPage> progress, Func<bool> cancel, string region = "")
        {
            if (offset < 0) offset = 0;
            if (limit <= 0) limit = 25;
            if (limit > 250) limit = 250;
            var request = new SourceSearchRequest { Query = (query ?? "").Trim(), Region = PackageSourceEngineStatic.NormalizeRegion(region), Limit = PackageSourceEngineStatic.MaxTitles, Cancel = cancel };
            var merged = new List<SourceTitleResult>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var reports = new List<SourceExecutionReport>();
            var calls = BeginCalls(runtime => runtime.Search(request), cancel, null);
            while (calls.Count > 0)
            {
                ThrowIfCanceled(cancel);
                IPackageSourceRuntime runtime = null;
                foreach (var item in calls) if (item.Value.IsCompleted) { runtime = item.Key; break; }
                if (runtime == null)
                {
                    var pending = new List<Task>(); foreach (var call in calls.Values) pending.Add(call);
                    Task.WaitAny(pending.ToArray(), 100); continue;
                }
                DateTime started = DateTime.UtcNow;
                try
                {
                    var call = calls[runtime].GetAwaiter().GetResult();
                    if (call.Error != null) throw call.Error;
                    List<SourceTitleResult> results = call.Results ?? new List<SourceTitleResult>();
                    int accepted = 0;
                    foreach (var result in results)
                    {
                        if (result == null) continue;
                        if (string.IsNullOrWhiteSpace(result.TitleId) &&
                            string.IsNullOrWhiteSpace(result.CatalogUrl) &&
                            string.IsNullOrWhiteSpace(result.StableResultId)) continue;
                        Stamp(result, runtime.Source);
                        result.Region = PackageSourceEngineStatic.NormalizeRegion(result.Region);
                        if (request.Region.Length > 0 && result.Region != request.Region) continue;
                        string key = result.SourceId + "\n" + result.SourceVersion + "\n" +
                            (!string.IsNullOrWhiteSpace(result.CatalogUrl) ? "catalog:" + result.CatalogUrl.Trim() :
                            (result.TitleId ?? "").Trim().ToUpperInvariant() + "\n" + result.Region);
                        if (!seen.Add(key)) continue;
                        if (merged.Count >= 60000) throw new InvalidOperationException("Too many source matches; narrow the search");
                        merged.Add(result);
                        accepted++;
                    }
                    reports.Add(Report(runtime.Source, started, true, SourceFailureCode.None, "", accepted));
                }
                catch (Exception ex)
                {
                    reports.Add(Report(runtime.Source, started, false, MapFailure(ex), SafeMessage(ex), 0));
                }
                calls.Remove(runtime);
                ThrowIfCanceled(cancel);
                PublishReports(reports);
                if (progress != null) progress(Page(merged, query, offset, limit, calls.Count == 0));
            }
            PublishReports(reports);
            ThrowIfCanceled(cancel);
            return Page(merged, query, offset, limit, true);
        }

        static SourceSearchPage Page(List<SourceTitleResult> merged, string query, int offset, int limit, bool complete)
        {
            var sorted = new List<SourceTitleResult>(merged);
            var ranks = new Dictionary<SourceTitleResult, int>();
            foreach (var title in sorted) ranks[title] = title.SearchRankHint >= 0 ? title.SearchRankHint : PackageSourceEngineStatic.SearchRank(title, query);
            sorted.Sort((a, b) => {
                int ar = ranks[a], br = ranks[b];
                int order = (ar < 0 ? 4 : ar).CompareTo(br < 0 ? 4 : br);
                return order != 0 ? order : PackageSourceEngineStatic.CompareTitle(a, b);
            });
            sorted = SourceTitleResult.GroupGames(sorted);
            var page = new SourceSearchPage { Offset = offset, PageSize = limit, TotalMatches = sorted.Count, IsComplete = complete,
                AllMatches = complete ? sorted : null };
            for (int i = offset; i < sorted.Count && page.Results.Count < limit; i++) page.Results.Add(PackageSourceEngineStatic.CloneTitle(sorted[i]));
            if (offset + page.Results.Count < sorted.Count) page.NextCursor = (offset + page.Results.Count).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return page;
        }

        public List<PackageCandidate> Resolve(string titleId, string name, int limit)
        {
            return Resolve(titleId, name, "", limit);
        }

        public List<PackageCandidate> Resolve(string titleId, string name, string region, int limit)
        {
            return Resolve(titleId, name, region, "", limit);
        }

        public List<PackageCandidate> Resolve(string titleId, string name, string region,
            string catalogUrl, int limit)
        { return Resolve(titleId, name, region, catalogUrl, "", "", limit, null); }

        public List<PackageCandidate> Resolve(string titleId, string name, string region,
            string catalogUrl, string sourceId, string sourceVersion, int limit, Func<bool> cancel,
            string excludedSourceId = "")
        {
            if (limit <= 0) limit = PackageSourceEngineRemote.MaxPackages;
            if (limit > PackageSourceEngineRemote.MaxPackages) limit = PackageSourceEngineRemote.MaxPackages;
            var request = new SourceResolveRequest
            {
                TitleId = (titleId ?? "").Trim(),
                Name = (name ?? "").Trim(),
                Region = (region ?? "").Trim(),
                CatalogUrl = (catalogUrl ?? "").Trim(),
                Limit = limit, Cancel = cancel
            };
            var merged = new List<PackageCandidate>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var reports = new List<SourceExecutionReport>();

            Func<IPackageSourceRuntime, bool> selected = runtime => runtime.Source.SourceId != excludedSourceId && (string.IsNullOrEmpty(sourceId) ||
                (runtime.Source.SourceId == sourceId && (string.IsNullOrEmpty(sourceVersion) ||
                (runtime.Source.Descriptor != null && runtime.Source.Descriptor.Version == sourceVersion))));
            var calls = BeginCalls(runtime => runtime.Resolve(request), cancel, selected);
            foreach (var runtime in _runtimes)
            {
                if (!IsEnabled(runtime) || !selected(runtime)) continue;
                ThrowIfCanceled(cancel);
                DateTime started = DateTime.UtcNow;
                try
                {
                    var call = calls[runtime].GetAwaiter().GetResult();
                    if (call.Error != null) throw call.Error;
                    List<PackageCandidate> results = call.Results ?? new List<PackageCandidate>();
                    if (!IsStatic(runtime)) results = GroupArchiveVolumes(results);
                    int accepted = 0;
                    foreach (var candidate in results)
                    {
                        if (!IsUsable(candidate, request.TitleId)) continue;
                        if (excludedSourceId.Length > 0 && !string.Equals(candidate.TitleId, request.TitleId, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!string.IsNullOrEmpty(request.Region) && !string.IsNullOrEmpty(candidate.Region) && PackageSourceEngineStatic.NormalizeRegion(candidate.Region) !=
                            PackageSourceEngineStatic.NormalizeRegion(request.Region)) continue;
                        Stamp(candidate, runtime.Source);
                        string key = CandidateKey(candidate);
                        if (!seen.Add(key)) continue;
                        merged.Add(candidate);
                        accepted++;
                        if (merged.Count >= limit) break;
                    }
                    reports.Add(Report(runtime.Source, started, true, SourceFailureCode.None, "", accepted));
                }
                catch (Exception ex)
                {
                    reports.Add(Report(runtime.Source, started, false, MapFailure(ex), SafeMessage(ex), 0));
                }
                if (merged.Count >= limit) break;
            }
            PublishReports(reports);
            ThrowIfCanceled(cancel);
            return merged;
        }

        static List<PackageCandidate> GroupArchiveVolumes(List<PackageCandidate> candidates)
        {
            var groups = new Dictionary<string, List<PackageCandidate>>(StringComparer.OrdinalIgnoreCase);
            foreach (PackageCandidate candidate in candidates)
            {
                if (candidate == null || !string.IsNullOrEmpty(candidate.ArchiveVolumes)) continue;
                string name = ArchiveVolumeSet.FileName(candidate.Url, candidate.Label), set; int index; Uri uri;
                if (!ArchiveVolumeSet.TryIndex(name, out set, out index) || !Uri.TryCreate(candidate.Url, UriKind.Absolute, out uri)) continue;
                string key = candidate.TitleId + "\n" + candidate.PackageKindHint + "\n" +
                    candidate.SourcePageUrl + "\n" + candidate.PackageVersion + "\n" + candidate.PackageGroupId + "\n" + uri.Host + "\n" + set;
                List<PackageCandidate> group;
                if (!groups.TryGetValue(key, out group)) groups[key] = group = new List<PackageCandidate>();
                group.Add(candidate);
            }
            var removed = new HashSet<PackageCandidate>();
            foreach (var group in groups.Values)
            {
                if (group.Count < 2) continue;
                var volumes = new List<ArchiveVolume>();
                foreach (PackageCandidate candidate in group) volumes.Add(new ArchiveVolume {
                    Name = ArchiveVolumeSet.FileName(candidate.Url, candidate.Label), Url = candidate.Url,
                    AccessType = candidate.AccessType.ToString(), Sha256 = candidate.ExpectedSha256,
                    Size = candidate.ExpectedByteSize ?? 0 });
                // Ambiguous/missing sequences stay separate; do not combine alternate mirrors.
                try { volumes = ArchiveVolumeSet.Validate(volumes); } catch (System.IO.InvalidDataException) { foreach (var invalid in group) removed.Add(invalid); continue; }
                PackageCandidate first = group.Find(c => c.Url == volumes[0].Url);
                first.ArchiveVolumes = ArchiveVolumeSet.Encode(volumes);
                first.Label = volumes[0].Name + " (" + volumes.Count + " volumes)";
                first.ExpectedByteSize = null; first.ExpectedSha256 = "";
                foreach (PackageCandidate candidate in group) if (!object.ReferenceEquals(first, candidate)) removed.Add(candidate);
            }
            return candidates.FindAll(candidate => !removed.Contains(candidate));
        }

        static readonly SemaphoreSlim SourceSlots = new SemaphoreSlim(2, 2);
        static readonly SemaphoreSlim LocalSourceSlots = new SemaphoreSlim(2, 2);
        sealed class SourceCall<T> { public List<T> Results; public Exception Error; }
        Dictionary<IPackageSourceRuntime, Task<SourceCall<T>>> BeginCalls<T>(Func<IPackageSourceRuntime, List<T>> run,
            Func<bool> cancel, Func<IPackageSourceRuntime, bool> include)
        {
            var calls = new Dictionary<IPackageSourceRuntime, Task<SourceCall<T>>>();
            foreach (var runtime in _runtimes)
            {
                if (!IsEnabled(runtime) || (include != null && !include(runtime))) continue;
                var captured = runtime;
                calls[captured] = Task.Run(() => {
                    bool slot = false;
                    SemaphoreSlim slots = IsStatic(captured) ? LocalSourceSlots : SourceSlots;
                    try {
                        while (!slots.Wait(100)) ThrowIfCanceled(cancel); slot = true;
                        ThrowIfCanceled(cancel);
                        return new SourceCall<T> { Results = run(captured) };
                    }
                    catch (Exception ex) { return new SourceCall<T> { Error = ex }; }
                    finally { if (slot) slots.Release(); }
                });
            }
            return calls;
        }

        static bool IsStatic(IPackageSourceRuntime runtime)
        { return runtime.Source.Descriptor != null && runtime.Source.Descriptor.Engine != null && PackageSourceEngineStatic.IsCatalog(runtime.Source.Descriptor.Engine.Type); }
        static void ThrowIfCanceled(Func<bool> cancel)
        { if (cancel != null && cancel()) throw new OperationCanceledException(); }

        static bool IsEnabled(IPackageSourceRuntime runtime)
        {
            return runtime != null && runtime.Source != null && runtime.Source.Enabled;
        }

        static int CompareRuntime(IPackageSourceRuntime a, IPackageSourceRuntime b)
        {
            int ap = a != null && a.Source != null ? a.Source.Priority : int.MaxValue;
            int bp = b != null && b.Source != null ? b.Source.Priority : int.MaxValue;
            int byPriority = ap.CompareTo(bp);
            if (byPriority != 0) return byPriority;
            string aid = a != null && a.Source != null ? a.Source.SourceId : "";
            string bid = b != null && b.Source != null ? b.Source.SourceId : "";
            return StringComparer.OrdinalIgnoreCase.Compare(aid, bid);
        }

        static bool IsUsable(PackageCandidate candidate, string requestedTitleId)
        {
            if (candidate == null) return false;
            Uri uri;
            if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out uri) ||
                !(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) return false;
            return string.IsNullOrEmpty(candidate.TitleId) || string.IsNullOrEmpty(requestedTitleId) ||
                string.Equals(candidate.TitleId, requestedTitleId, StringComparison.OrdinalIgnoreCase);
        }

        static string CandidateKey(PackageCandidate candidate)
        {
            return candidate.SourceId + "\n" + candidate.SourceVersion + "\n" + candidate.TitleId + "\n" +
                PackageSourceEngineStatic.NormalizeRegion(candidate.Region) + "\n" + candidate.PackageKindHint + "\n" +
                candidate.PackageVersion + "\n" + candidate.RequiredFirmware + "\n" + candidate.PackageGroupId + "\n" +
                candidate.CandidateId + "\n" + candidate.Url + "\n" + candidate.ExpectedContentId + "\n" + candidate.ExpectedSha256;
        }

        static void Stamp(SourceTitleResult result, InstalledPackageSource source)
        {
            if (source == null) return;
            result.SourceId = source.SourceId ?? "";
            if (source.Descriptor != null)
                result.SourceVersion = source.Descriptor.Version ?? "";
            if (string.IsNullOrEmpty(result.SourceAttribution) && source.Descriptor != null)
                result.SourceAttribution = source.Descriptor.DisplayName ?? "";
        }

        static void Stamp(PackageCandidate candidate, InstalledPackageSource source)
        {
            if (source == null) return;
            candidate.SourceId = source.SourceId ?? "";
            if (source.Descriptor != null)
                candidate.SourceVersion = source.Descriptor.Version ?? "";
            if (string.IsNullOrEmpty(candidate.SourceAttribution) && source.Descriptor != null)
                candidate.SourceAttribution = source.Descriptor.DisplayName ?? "";
        }

        static SourceExecutionReport Report(InstalledPackageSource source, DateTime started,
            bool success, SourceFailureCode code, string message, int count)
        {
            return new SourceExecutionReport
            {
                SourceId = source != null ? source.SourceId ?? "" : "",
                SourceVersion = source != null && source.Descriptor != null ? source.Descriptor.Version ?? "" : "",
                StartedUtc = started,
                CompletedUtc = DateTime.UtcNow,
                Success = success,
                FailureCode = code,
                Message = message ?? "",
                ResultCount = count
            };
        }

        static SourceFailureCode MapFailure(Exception ex)
        {
            if (ex is TimeoutException) return SourceFailureCode.TimedOut;
            if (ex is OperationCanceledException) return SourceFailureCode.Canceled;
            return SourceFailureCode.InvalidResponse;
        }

        static string SafeMessage(Exception ex)
        {
            string message = ex != null ? ex.Message ?? "Source failed" : "Source failed";
            if (message.Length > 160) message = message.Substring(0, 160);
            return message;
        }

        void PublishReports(List<SourceExecutionReport> reports)
        {
            lock (_sync)
            {
                _lastReports.Clear();
                _lastReports.AddRange(reports);
            }
        }
    }
}
