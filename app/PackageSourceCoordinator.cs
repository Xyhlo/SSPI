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
        const int DefaultResolveTimeoutMilliseconds = 15000;
        const int DefaultResolveGraceMilliseconds = 1000;
        readonly List<IPackageSourceRuntime> _runtimes = new List<IPackageSourceRuntime>();
        readonly List<SourceExecutionReport> _lastReports = new List<SourceExecutionReport>();
        readonly object _sync = new object();
        readonly int _resolveTimeoutMilliseconds;
        readonly int _resolveGraceMilliseconds;

        public PackageSourceCoordinator(IEnumerable<IPackageSourceRuntime> runtimes)
            : this(runtimes, DefaultResolveTimeoutMilliseconds, DefaultResolveGraceMilliseconds)
        { }

        internal PackageSourceCoordinator(IEnumerable<IPackageSourceRuntime> runtimes,
            int resolveTimeoutMilliseconds, int resolveGraceMilliseconds)
        {
            _resolveTimeoutMilliseconds = Math.Max(1, resolveTimeoutMilliseconds);
            _resolveGraceMilliseconds = Math.Max(0, resolveGraceMilliseconds);
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
            Dictionary<IPackageSourceRuntime, Action> cancelCalls;
            var calls = BeginCalls((runtime, callCancel) => runtime.Search(new SourceSearchRequest
            {
                Query = request.Query, Region = request.Region, Limit = request.Limit,
                TitleId = request.TitleId, Name = request.Name, Cursor = request.Cursor,
                Cancel = () => callCancel() || IsCanceled(request.Cancel)
            }), cancel, null, out cancelCalls);
            while (calls.Count > 0)
            {
                if (IsCanceled(cancel)) { CancelCalls(cancelCalls); ThrowIfCanceled(cancel); }
                IPackageSourceRuntime runtime = null;
                DateTime earliestCompletion = DateTime.MaxValue;
                foreach (var item in calls)
                {
                    if (!item.Value.IsCompleted) continue;
                    DateTime completedAt = item.Value.GetAwaiter().GetResult().CompletedUtc;
                    if (runtime == null || completedAt < earliestCompletion)
                    { runtime = item.Key; earliestCompletion = completedAt; }
                }
                if (runtime == null)
                {
                    var pending = new List<Task>(); foreach (var pendingCall in calls.Values) pending.Add(pendingCall);
                    Task.WaitAny(pending.ToArray(), 100); continue;
                }
                var call = calls[runtime].GetAwaiter().GetResult();
                DateTime started = call.StartedUtc;
                try
                {
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
            Dictionary<IPackageSourceRuntime, Action> cancelCalls;
            var calls = BeginCalls((runtime, callCancel) => runtime.Resolve(new SourceResolveRequest
            {
                TitleId = request.TitleId, Name = request.Name, Region = request.Region,
                CatalogUrl = request.CatalogUrl, Limit = request.Limit, Cursor = request.Cursor,
                Cancel = () => callCancel() || IsCanceled(request.Cancel)
            }), cancel, selected, out cancelCalls);
            var callOrder = new List<IPackageSourceRuntime>(calls.Keys);
            callOrder.Sort(CompareRuntime);
            var completed = new Dictionary<IPackageSourceRuntime, SourceCall<PackageCandidate>>();
            var preparedResults = new Dictionary<IPackageSourceRuntime, List<PackageCandidate>>();
            DateTime resolveStarted = DateTime.UtcNow;
            DateTime resolveDeadline = resolveStarted.AddMilliseconds(_resolveTimeoutMilliseconds);
            DateTime? usefulResultDeadline = null;
            while (calls.Count > 0)
            {
                if (IsCanceled(cancel)) { CancelCalls(cancelCalls); ThrowIfCanceled(cancel); }

                IPackageSourceRuntime runtime = null;
                DateTime earliestCompletion = DateTime.MaxValue;
                var pendingRuntimes = new List<IPackageSourceRuntime>();
                var pendingTasks = new List<Task<SourceCall<PackageCandidate>>>();
                foreach (IPackageSourceRuntime candidate in callOrder)
                {
                    Task<SourceCall<PackageCandidate>> task;
                    if (!calls.TryGetValue(candidate, out task)) continue;
                    if (task.IsCompleted)
                    {
                        DateTime completedAt = task.GetAwaiter().GetResult().CompletedUtc;
                        if (runtime == null || completedAt < earliestCompletion)
                        { runtime = candidate; earliestCompletion = completedAt; }
                        continue;
                    }
                    pendingRuntimes.Add(candidate);
                    pendingTasks.Add(task);
                }
                if (runtime == null)
                {
                    DateTime now = DateTime.UtcNow;
                    DateTime waitDeadline = resolveDeadline;
                    if (usefulResultDeadline.HasValue && usefulResultDeadline.Value < waitDeadline)
                        waitDeadline = usefulResultDeadline.Value;
                    if (now >= waitDeadline)
                    {
                        bool overallTimeout = now >= resolveDeadline;
                        foreach (IPackageSourceRuntime waiting in pendingRuntimes)
                        {
                            Action stop;
                            if (cancelCalls.TryGetValue(waiting, out stop)) stop();
                            DateTime started = resolveStarted;
                            reports.Add(Report(waiting.Source, started, false,
                                overallTimeout ? SourceFailureCode.TimedOut : SourceFailureCode.Canceled,
                                overallTimeout ? "Source resolution timed out" : "Source resolution stopped after other sources returned candidates", 0));
                            calls.Remove(waiting);
                            cancelCalls.Remove(waiting);
                        }
                        break;
                    }
                    int remaining = (int)Math.Max(1, Math.Min(100, (waitDeadline - now).TotalMilliseconds));
                    int finished = Task.WaitAny(pendingTasks.ToArray(), remaining);
                    if (finished >= 0) runtime = pendingRuntimes[finished];
                    else continue;
                }

                SourceCall<PackageCandidate> call = calls[runtime].GetAwaiter().GetResult();
                completed[runtime] = call;
                calls.Remove(runtime);
                cancelCalls.Remove(runtime);
                if (call.Error != null)
                    reports.Add(Report(runtime.Source, call.StartedUtc, false, MapFailure(call.Error), SafeMessage(call.Error), 0));
                else
                {
                    reports.Add(Report(runtime.Source, call.StartedUtc, true, SourceFailureCode.None, "", 0));
                    try { preparedResults[runtime] = PrepareResolveResults(runtime, call.Results, request, excludedSourceId); }
                    catch (Exception ex)
                    {
                        call.Error = ex;
                        reports[reports.Count - 1].Success = false;
                        reports[reports.Count - 1].FailureCode = MapFailure(ex);
                        reports[reports.Count - 1].Message = SafeMessage(ex);
                    }
                    merged.Clear(); seen.Clear();
                    MergeCompletedResolveResults(callOrder, completed, preparedResults,
                        limit, merged, seen, reports);
                    if (merged.Count >= limit) break;
                    if (merged.Count > 0 && !usefulResultDeadline.HasValue)
                        usefulResultDeadline = DateTime.UtcNow.AddMilliseconds(_resolveGraceMilliseconds);
                }
            }

            if (calls.Count > 0)
            {
                foreach (var waiting in calls.Keys)
                {
                    Action stop;
                    if (cancelCalls.TryGetValue(waiting, out stop)) stop();
                }
            }
            merged.Clear(); seen.Clear();
            MergeCompletedResolveResults(callOrder, completed, preparedResults,
                limit, merged, seen, reports);
            PublishReports(reports);
            ThrowIfCanceled(cancel);
            return merged;
        }

        static List<PackageCandidate> PrepareResolveResults(IPackageSourceRuntime runtime,
            List<PackageCandidate> results, SourceResolveRequest request, string excludedSourceId)
        {
            results = results ?? new List<PackageCandidate>();
            if (!IsStatic(runtime)) results = GroupArchiveVolumes(results);
            var prepared = new List<PackageCandidate>();
            foreach (PackageCandidate candidate in results)
            {
                if (!IsUsable(candidate, request.TitleId)) continue;
                if (excludedSourceId.Length > 0 && !string.Equals(candidate.TitleId, request.TitleId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(request.Region) && !string.IsNullOrEmpty(candidate.Region) && PackageSourceEngineStatic.NormalizeRegion(candidate.Region) !=
                    PackageSourceEngineStatic.NormalizeRegion(request.Region)) continue;
                Stamp(candidate, runtime.Source);
                prepared.Add(candidate);
            }
            return prepared;
        }

        static void MergeCompletedResolveResults(List<IPackageSourceRuntime> callOrder,
            Dictionary<IPackageSourceRuntime, SourceCall<PackageCandidate>> completed,
            Dictionary<IPackageSourceRuntime, List<PackageCandidate>> preparedResults, int limit,
            List<PackageCandidate> merged, HashSet<string> seen, List<SourceExecutionReport> reports)
        {
            foreach (SourceExecutionReport report in reports)
                if (report.Success) report.ResultCount = 0;
            foreach (IPackageSourceRuntime runtime in callOrder)
            {
                SourceCall<PackageCandidate> call;
                if (!completed.TryGetValue(runtime, out call) || call.Error != null) continue;
                List<PackageCandidate> results;
                if (!preparedResults.TryGetValue(runtime, out results)) continue;
                int accepted = 0;
                foreach (PackageCandidate candidate in results)
                {
                    string key = CandidateKey(candidate);
                    if (!seen.Add(key)) continue;
                    merged.Add(candidate);
                    accepted++;
                    if (merged.Count >= limit) break;
                }
                for (int i = reports.Count - 1; i >= 0; i--)
                    if (reports[i].SourceId == runtime.Source.SourceId && reports[i].Success)
                    { reports[i].ResultCount = accepted; break; }
                if (merged.Count >= limit) break;
            }
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
                try { volumes = ArchiveVolumeSet.Validate(volumes); }
                catch (System.IO.InvalidDataException) { continue; }
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
        sealed class SourceCall<T> { public List<T> Results; public Exception Error; public DateTime StartedUtc; public DateTime CompletedUtc; }
        Dictionary<IPackageSourceRuntime, Task<SourceCall<T>>> BeginCalls<T>(Func<IPackageSourceRuntime, Func<bool>, List<T>> run,
            Func<bool> cancel, Func<IPackageSourceRuntime, bool> include,
            out Dictionary<IPackageSourceRuntime, Action> cancelCalls)
        {
            var calls = new Dictionary<IPackageSourceRuntime, Task<SourceCall<T>>>();
            cancelCalls = new Dictionary<IPackageSourceRuntime, Action>();
            foreach (var runtime in _runtimes)
            {
                if (!IsEnabled(runtime) || (include != null && !include(runtime))) continue;
                var captured = runtime;
                int abandoned = 0;
                Func<bool> callCancel = () => Volatile.Read(ref abandoned) != 0 || IsCanceled(cancel);
                cancelCalls[captured] = () => Interlocked.Exchange(ref abandoned, 1);
                calls[captured] = Task.Run(() => {
                    bool slot = false;
                    DateTime started = DateTime.UtcNow;
                    SemaphoreSlim slots = IsStatic(captured) ? LocalSourceSlots : SourceSlots;
                    try {
                        while (!slots.Wait(100)) ThrowIfCanceled(callCancel); slot = true;
                        ThrowIfCanceled(callCancel);
                        started = DateTime.UtcNow;
                        var result = new SourceCall<T> { StartedUtc = started };
                        result.Results = run(captured, callCancel);
                        result.CompletedUtc = DateTime.UtcNow;
                        return result;
                    }
                    catch (Exception ex) { return new SourceCall<T> { StartedUtc = started, CompletedUtc = DateTime.UtcNow, Error = ex }; }
                    finally { if (slot) slots.Release(); }
                });
            }
            return calls;
        }

        static bool IsStatic(IPackageSourceRuntime runtime)
        { return runtime.Source.Descriptor != null && runtime.Source.Descriptor.Engine != null && PackageSourceEngineStatic.IsCatalog(runtime.Source.Descriptor.Engine.Type); }
        static void ThrowIfCanceled(Func<bool> cancel)
        { if (cancel != null && cancel()) throw new OperationCanceledException(); }

        static bool IsCanceled(Func<bool> cancel)
        { return cancel != null && cancel(); }

        static void CancelCalls(Dictionary<IPackageSourceRuntime, Action> cancelCalls)
        { foreach (Action cancel in cancelCalls.Values) cancel(); }

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
