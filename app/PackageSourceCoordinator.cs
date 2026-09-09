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
            if (limit <= 0) limit = 25;
            if (limit > 100) limit = 100;
            var request = new SourceSearchRequest { Query = (query ?? "").Trim(), Limit = limit };
            var merged = new List<SourceTitleResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reports = new List<SourceExecutionReport>();

            var calls = BeginCalls(runtime => runtime.Search(request));
            foreach (var runtime in _runtimes)
            {
                if (!IsEnabled(runtime)) continue;
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
                        string key = !string.IsNullOrWhiteSpace(result.CatalogUrl)
                            ? "catalog:" + result.CatalogUrl.Trim()
                            : (result.TitleId ?? "").Trim() + "\n" + (result.Region ?? "").Trim();
                        if (!seen.Add(key)) continue;
                        merged.Add(result);
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
            return merged;
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
        {
            if (limit <= 0) limit = 100;
            if (limit > 100) limit = 100;
            var request = new SourceResolveRequest
            {
                TitleId = (titleId ?? "").Trim(),
                Name = (name ?? "").Trim(),
                Region = (region ?? "").Trim(),
                CatalogUrl = (catalogUrl ?? "").Trim(),
                Limit = limit
            };
            var merged = new List<PackageCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reports = new List<SourceExecutionReport>();

            var calls = BeginCalls(runtime => runtime.Resolve(request));
            foreach (var runtime in _runtimes)
            {
                if (!IsEnabled(runtime)) continue;
                DateTime started = DateTime.UtcNow;
                try
                {
                    var call = calls[runtime].GetAwaiter().GetResult();
                    if (call.Error != null) throw call.Error;
                    List<PackageCandidate> results = GroupArchiveVolumes(call.Results ?? new List<PackageCandidate>());
                    int accepted = 0;
                    foreach (var candidate in results)
                    {
                        if (!IsUsable(candidate, request.TitleId)) continue;
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
        sealed class SourceCall<T> { public List<T> Results; public Exception Error; }
        Dictionary<IPackageSourceRuntime, Task<SourceCall<T>>> BeginCalls<T>(Func<IPackageSourceRuntime, List<T>> run)
        {
            var calls = new Dictionary<IPackageSourceRuntime, Task<SourceCall<T>>>();
            foreach (var runtime in _runtimes)
            {
                if (!IsEnabled(runtime)) continue;
                var captured = runtime;
                calls[captured] = Task.Run(() => {
                    SourceSlots.Wait();
                    try { return new SourceCall<T> { Results = run(captured) }; }
                    catch (Exception ex) { return new SourceCall<T> { Error = ex }; }
                    finally { SourceSlots.Release(); }
                });
            }
            return calls;
        }

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
            if (!string.IsNullOrEmpty(candidate.ExpectedSha256)) return "hash:" + candidate.ExpectedSha256;
            if (!string.IsNullOrEmpty(candidate.ExpectedContentId)) return "content:" + candidate.ExpectedContentId;
            if (!string.IsNullOrEmpty(candidate.CandidateId))
                return "id:" + candidate.SourceId + ":" + candidate.CandidateId;
            return "url:" + candidate.Url;
        }

        static void Stamp(SourceTitleResult result, InstalledPackageSource source)
        {
            if (source == null) return;
            if (string.IsNullOrEmpty(result.SourceId)) result.SourceId = source.SourceId ?? "";
            if (string.IsNullOrEmpty(result.SourceVersion) && source.Descriptor != null)
                result.SourceVersion = source.Descriptor.Version ?? "";
            if (string.IsNullOrEmpty(result.SourceAttribution) && source.Descriptor != null)
                result.SourceAttribution = source.Descriptor.DisplayName ?? "";
        }

        static void Stamp(PackageCandidate candidate, InstalledPackageSource source)
        {
            if (source == null) return;
            if (string.IsNullOrEmpty(candidate.SourceId)) candidate.SourceId = source.SourceId ?? "";
            if (string.IsNullOrEmpty(candidate.SourceVersion) && source.Descriptor != null)
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
