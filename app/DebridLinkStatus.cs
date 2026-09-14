using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal enum DebridCacheState { Unknown, Checking, Cached, NotCached, NotExposed }

    internal sealed class DebridLinkStatus
    {
        internal readonly DebridHostState HostState;
        internal readonly DebridCacheState CacheState;
        internal readonly string Detail;
        internal readonly string AlternativeProvider;
        internal readonly string AccountNote;

        internal DebridLinkStatus(DebridHostState host, DebridCacheState cache, string detail, string alternative = "", string accountNote = "")
        {
            HostState = host; CacheState = cache; Detail = detail; AlternativeProvider = alternative; AccountNote = accountNote;
        }
    }

    internal sealed class DebridProviderStatus
    {
        internal readonly string ProviderId;
        internal readonly DebridLinkStatus Status;
        internal DebridProviderStatus(string providerId, DebridLinkStatus status) { ProviderId = providerId; Status = status; }
    }

    internal sealed class DebridMultiProviderStatusLookup : IDisposable
    {
        readonly string[] providers;
        readonly DebridLinkStatusLookup[] lookups;
        int revision;
        internal int Revision { get { return Interlocked.CompareExchange(ref revision, 0, 0); } }
        internal bool IsChecking { get { foreach (var lookup in lookups) if (lookup != null && lookup.IsChecking) return true; return false; } }
        internal DebridMultiProviderStatusLookup(AppSettings cfg, IList<PackageCandidate> candidates, Action updated)
        {
            providers = UnlockProviders.EnabledIds(cfg);
            lookups = new DebridLinkStatusLookup[providers.Length];
            for (int i = 0; i < providers.Length; i++)
                lookups[i] = new DebridLinkStatusLookup(cfg, candidates, () => {
                    Interlocked.Increment(ref revision); if (updated != null) updated();
                }, providers[i], false);
        }

        internal DebridProviderStatus[] GetAll(PackageCandidate candidate)
        {
            var result = new DebridProviderStatus[providers.Length];
            for (int i = 0; i < providers.Length; i++) result[i] = new DebridProviderStatus(providers[i], lookups[i].Get(candidate));
            return result;
        }

        internal DebridLinkStatus Get(PackageCandidate candidate)
        {
            DebridLinkStatus best = null;
            foreach (var lookup in lookups)
            {
                var current = lookup.Get(candidate);
                if (best == null || Rank(current) < Rank(best)) best = current;
            }
            return best;
        }

        static int Rank(DebridLinkStatus value)
        {
            return value.HostState == DebridHostState.Supported ? (value.CacheState == DebridCacheState.Cached ? 0 : 1) :
                value.HostState == DebridHostState.Unknown ? 2 : 3;
        }

        public void Dispose() { foreach (var lookup in lookups) lookup.Dispose(); }
    }

    // Read-only hints: no unrestrict/create/download requests are made by this session.
    internal sealed class DebridLinkStatusLookup : IDisposable
    {
        const int MaximumCandidates = 128, MaximumHashes = 128, BatchSize = 32;
        const int CallTimeoutMs = 4000, SessionTimeoutMs = 20000;
        static readonly SemaphoreSlim NetworkSlot = new SemaphoreSlim(1, 1);
        static readonly DebridLinkStatus Unknown = new DebridLinkStatus(DebridHostState.Unknown, DebridCacheState.Unknown, "Status not checked");
        sealed class CachedResult { internal DebridCacheState State; internal DateTime Until; }
        static readonly Dictionary<string, CachedResult> Cache = new Dictionary<string, CachedResult>();
        sealed class Row
        {
            internal PackageCandidate Candidate;
            internal string Url, Volumes, Error;
            internal PackageAccessType Access;
            internal readonly List<string> HosterUrls = new List<string>();
            internal readonly List<string> Hashes = new List<string>();
            internal bool Direct, HasDirect, Invalid;
        }

        readonly string provider, token;
        readonly bool checkCache;
        readonly Dictionary<string, string> otherTokens = new Dictionary<string, string>();
        readonly Action updated;
        readonly List<Row> rows = new List<Row>();
        readonly object stateLock = new object();
        readonly Dictionary<PackageCandidate, DebridLinkStatus> states = new Dictionary<PackageCandidate, DebridLinkStatus>();
        int disposed;
        int checking;
        internal bool IsChecking { get { return Volatile.Read(ref checking) != 0; } }

        internal DebridLinkStatusLookup(AppSettings cfg, IList<PackageCandidate> candidates, Action updated, string providerId = null, bool checkCache = true)
        {
            this.checkCache = checkCache;
            this.updated = updated;
            provider = providerId ?? (cfg != null && cfg.UseUnlockProvider ? cfg.UnlockProviderId : UnlockProviders.NoneId);
            token = UnlockProviders.ApiKey(cfg, provider);
            foreach (string other in new[] { UnlockProviders.RealDebridId, UnlockProviders.TorBoxId, UnlockProviders.AllDebridId, UnlockProviders.PremiumizeId })
                if (providerId == null && other != provider && UnlockProviders.IsEnabled(cfg, other))
                    otherTokens[other] = UnlockProviders.ApiKey(cfg, other);
            if (candidates != null)
                for (int i = 0; i < candidates.Count && rows.Count < MaximumCandidates; i++)
                {
                    PackageCandidate candidate = candidates[i];
                    if (candidate == null || states.ContainsKey(candidate)) continue;
                    rows.Add(new Row { Candidate = candidate, Url = candidate.Url, Volumes = candidate.ArchiveVolumes,
                        Access = candidate.AccessType, Error = candidate.ResolutionError });
                    states[candidate] = Initial(candidate);
                }
            if (rows.Count != 0 && IsProvider && !string.IsNullOrWhiteSpace(token))
            {
                Volatile.Write(ref checking, 1);
                ThreadPool.QueueUserWorkItem(delegate { Run(); });
            }
        }

        bool IsProvider { get { return UnlockProviders.IsSupported(provider); } }
        bool HasCacheApi { get { return checkCache && (provider == UnlockProviders.TorBoxId || provider == UnlockProviders.PremiumizeId); } }
        bool Canceled() { return Interlocked.CompareExchange(ref disposed, 0, 0) != 0; }
        public void Dispose() { Interlocked.Exchange(ref disposed, 1); }

        internal DebridLinkStatus Get(PackageCandidate candidate)
        {
            if (candidate == null) return Unknown;
            lock (stateLock)
            {
                DebridLinkStatus status;
                return states.TryGetValue(candidate, out status) ? status : Unknown;
            }
        }

        DebridLinkStatus Initial(PackageCandidate candidate)
        {
            if (!IsProvider) return new DebridLinkStatus(DebridHostState.Unknown, DebridCacheState.NotExposed, "Direct mode; provider status not checked");
            if (string.IsNullOrWhiteSpace(token)) return new DebridLinkStatus(DebridHostState.Unknown, DebridCacheState.Unknown, "Connect your link service to check status");
            if (candidate.AccessType == PackageAccessType.Direct && string.IsNullOrEmpty(candidate.ArchiveVolumes))
                return new DebridLinkStatus(DebridHostState.Unknown, DebridCacheState.NotExposed, "Direct file; provider cache is not used");
            return new DebridLinkStatus(DebridHostState.Unknown,
                HasCacheApi ? DebridCacheState.Checking : DebridCacheState.NotExposed,
                HasCacheApi ? "Checking provider status" : "Cache status not provided");
        }

        void Run()
        {
            bool entered = false;
            var elapsed = Stopwatch.StartNew();
            var cacheResults = new Dictionary<string, DebridCacheState>(StringComparer.OrdinalIgnoreCase);
            DebridHostList hosts = null;
            var otherHosts = new Dictionary<string, DebridHostList>();
            try
            {
                while (!Canceled() && elapsed.ElapsedMilliseconds < SessionTimeoutMs * 4)
                    if (NetworkSlot.Wait(100)) { entered = true; break; }
                if (!entered || Canceled()) return;
                elapsed.Restart(); // Waiting for another read-only lookup is not an API timeout.
                foreach (Row row in rows) Prepare(row);
                if (!rows.Exists(delegate(Row row) { return !row.Direct && !row.Invalid; })) return;
                hosts = DebridHostSupport.Load(provider, token, true, Timeout(elapsed), Canceled);
                if (Canceled()) return;
                Publish(hosts, otherHosts, cacheResults, HasCacheApi);
                if (HasCacheApi && provider == UnlockProviders.TorBoxId)
                    CheckTorBoxCache(elapsed, cacheResults, delegate { Publish(hosts, otherHosts, cacheResults, true); });
                else if (HasCacheApi && provider == UnlockProviders.PremiumizeId)
                    CheckPremiumizeCache(hosts, elapsed, cacheResults, delegate { Publish(hosts, otherHosts, cacheResults, true); });
                if (Canceled()) return;
                foreach (var other in otherTokens)
                {
                    if (Canceled() || Timeout(elapsed) < 500) break;
                    otherHosts[other.Key] = DebridHostSupport.Load(other.Key, other.Value, true, Timeout(elapsed), Canceled);
                }
            }
            catch
            {
                // A decoration failure must never interrupt package selection or change account state.
            }
            finally
            {
                if (entered) NetworkSlot.Release();
                Volatile.Write(ref checking, 0);
                if (!Canceled()) Publish(hosts, otherHosts, cacheResults, false);
            }
        }

        static int Timeout(Stopwatch elapsed)
        {
            return Math.Max(0, Math.Min(CallTimeoutMs, SessionTimeoutMs - (int)elapsed.ElapsedMilliseconds));
        }

        static bool IsFileUrl(string url)
        {
            Uri parsed;
            return !string.IsNullOrWhiteSpace(url) && url.Length <= 8192 &&
                Uri.TryCreate(url, UriKind.Absolute, out parsed) && string.IsNullOrEmpty(parsed.UserInfo) &&
                (parsed.Scheme == "https" || parsed.Scheme == "http");
        }

        static void Prepare(Row row)
        {
            try
            {
                if (!string.IsNullOrEmpty(row.Volumes))
                {
                    // Large sets remain selectable; decoration is bounded independently of extraction.
                    if (row.Volumes.Length > 256 * 1024) { row.Invalid = true; return; }
                    foreach (ArchiveVolume volume in ArchiveVolumeSet.Decode(row.Volumes))
                        AddUrl(row, volume.Url, string.Equals(volume.AccessType, "Direct", StringComparison.OrdinalIgnoreCase));
                }
                else AddUrl(row, row.Url, row.Access == PackageAccessType.Direct);
                row.Direct = row.HasDirect && row.HosterUrls.Count == 0;
                if (!row.Direct && row.HosterUrls.Count == 0) row.Invalid = true;
            }
            catch { row.Invalid = true; }
        }

        static void AddUrl(Row row, string url, bool direct)
        {
            if (!IsFileUrl(url)) { row.Invalid = true; return; }
            if (direct) { row.HasDirect = true; return; }
            row.HosterUrls.Add(url);
            row.Hashes.Add(LinkHash(url));
        }

        internal static string LinkHash(string url)
        {
            // Query strings can identify the file. Never normalize them away.
            using (var md5 = MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", "").ToLowerInvariant();
        }

        void CheckTorBoxCache(Stopwatch elapsed, Dictionary<string, DebridCacheState> results, Action changed)
        {
            string scope = DebridHostSupport.CredentialScope(provider, token) + ":";
            var pending = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Row row in rows)
            {
                if (row.Invalid || row.HasDirect || !string.IsNullOrEmpty(row.Error)) continue;
                foreach (string hash in row.Hashes)
                {
                    if (seen.Count >= MaximumHashes || !seen.Add(hash)) continue;
                    CachedResult cached;
                    lock (Cache)
                    {
                        if (Cache.TryGetValue(scope + hash, out cached) && cached.Until > DateTime.UtcNow)
                        { results[hash] = cached.State; continue; }
                    }
                    pending.Add(hash);
                }
            }
            changed();
            for (int start = 0; start < pending.Count && !Canceled() && Timeout(elapsed) >= 500; start += BatchSize)
            {
                var batch = pending.GetRange(start, Math.Min(BatchSize, pending.Count - start));
                Dictionary<string, DebridCacheState> parsed;
                bool failed = false;
                try
                {
                    string url = "https://api.torbox.app/v1/api/webdl/checkcached?format=object&list_files=false&hash=" + string.Join(",", batch.ToArray());
                    parsed = ParseTorBoxCache(NetHttp.GetStringDirect(url, Timeout(elapsed), null, token), batch);
                }
                catch
                {
                    failed = true;
                    parsed = new Dictionary<string, DebridCacheState>();
                    foreach (string hash in batch) parsed[hash] = DebridCacheState.Unknown;
                }
                if (Canceled()) return;
                lock (Cache)
                {
                    if (Cache.Count + parsed.Count > 512) Cache.Clear();
                    foreach (var pair in parsed)
                    {
                        results[pair.Key] = pair.Value;
                        Cache[scope + pair.Key] = new CachedResult { State = pair.Value,
                            Until = DateTime.UtcNow.AddSeconds(pair.Value == DebridCacheState.Unknown ? 15 : 60) };
                    }
                }
                changed();
                // Authentication, rate limits and malformed replies are not repeated for every batch.
                if (failed) break;
            }
        }

        internal static Dictionary<string, DebridCacheState> ParseTorBoxCache(string json, IList<string> requested)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 256 * 1024) throw new FormatException("Invalid cache response size");
            var root = PackageSourceJson.Parse(json) as Dictionary<string, object>;
            object success, data, error;
            if (root == null || !root.TryGetValue("success", out success) || !Equals(success, true) ||
                !root.TryGetValue("data", out data) || (data != null && !(data is Dictionary<string, object>)) ||
                (root.TryGetValue("error", out error) && error != null && !Equals(error, "")))
                throw new FormatException("Invalid cache response");
            // The documented empty-cache response uses data:null; success:false is never a cache miss.
            var entries = data == null ? new Dictionary<string, object>() :
                new Dictionary<string, object>((Dictionary<string, object>)data, StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, DebridCacheState>(StringComparer.OrdinalIgnoreCase);
            foreach (string hash in requested)
            {
                object value, responseHash, cached;
                if (!entries.TryGetValue(hash, out value)) { result[hash] = DebridCacheState.NotCached; continue; }
                var entry = value as Dictionary<string, object>;
                if (entry == null || !entry.TryGetValue("hash", out responseHash) ||
                    !string.Equals(responseHash as string, hash, StringComparison.OrdinalIgnoreCase))
                    throw new FormatException("Cache response hash mismatch");
                bool hasCached = entry.TryGetValue("cached", out cached);
                if (hasCached && !(cached is bool)) throw new FormatException("Invalid cache flag");
                result[hash] = hasCached && !(bool)cached ? DebridCacheState.NotCached : DebridCacheState.Cached;
            }
            return result;
        }

        void CheckPremiumizeCache(DebridHostList hosts, Stopwatch elapsed, Dictionary<string, DebridCacheState> results, Action changed)
        {
            if (hosts == null) return;
            string scope = DebridHostSupport.CredentialScope(provider, token) + ":";
            var pending = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Row row in rows)
            {
                if (row.Invalid || row.HasDirect || !string.IsNullOrEmpty(row.Error)) continue;
                foreach (string url in row.HosterUrls)
                {
                    string hash = LinkHash(url);
                    if (!hosts.CanCheckCache(url)) { results[hash] = DebridCacheState.NotExposed; continue; }
                    if (seen.Count >= MaximumHashes || !seen.Add(url)) continue;
                    CachedResult cached;
                    lock (Cache)
                    {
                        if (Cache.TryGetValue(scope + hash, out cached) && cached.Until > DateTime.UtcNow)
                        { results[hash] = cached.State; continue; }
                    }
                    pending.Add(url);
                }
            }
            changed();
            for (int start = 0; start < pending.Count && !Canceled() && Timeout(elapsed) >= 500; start += BatchSize)
            {
                var batch = pending.GetRange(start, Math.Min(BatchSize, pending.Count - start));
                Dictionary<string, DebridCacheState> parsed;
                bool failed = false;
                try
                {
                    var form = new StringBuilder();
                    foreach (string url in batch)
                    {
                        if (form.Length != 0) form.Append('&');
                        form.Append("items%5B%5D=").Append(Uri.EscapeDataString(url));
                    }
                    parsed = ParsePremiumizeCache(NetHttp.PostForm("https://www.premiumize.me/api/cache/check", form.ToString(), Timeout(elapsed), null, token), batch);
                }
                catch
                {
                    failed = true;
                    parsed = new Dictionary<string, DebridCacheState>();
                    foreach (string url in batch) parsed[LinkHash(url)] = DebridCacheState.Unknown;
                }
                if (Canceled()) return;
                lock (Cache)
                {
                    if (Cache.Count + parsed.Count > 512) Cache.Clear();
                    foreach (var pair in parsed)
                    {
                        results[pair.Key] = pair.Value;
                        Cache[scope + pair.Key] = new CachedResult { State = pair.Value,
                            Until = DateTime.UtcNow.AddSeconds(pair.Value == DebridCacheState.Unknown ? 15 : 60) };
                    }
                }
                changed();
                if (failed) break;
            }
        }

        internal static Dictionary<string, DebridCacheState> ParsePremiumizeCache(string json, IList<string> requested)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 256 * 1024) throw new FormatException("Invalid cache response size");
            var root = PackageSourceJson.Parse(json) as Dictionary<string, object>;
            object status, response;
            if (root == null || !root.TryGetValue("status", out status) || !Equals(status, "success") ||
                !root.TryGetValue("response", out response) || !(response is IList) || ((IList)response).Count != requested.Count)
                throw new FormatException("Invalid cache response");
            var result = new Dictionary<string, DebridCacheState>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < requested.Count; i++)
            {
                object cached = ((IList)response)[i];
                if (!(cached is bool)) throw new FormatException("Invalid cache flag");
                result[LinkHash(requested[i])] = (bool)cached ? DebridCacheState.Cached : DebridCacheState.NotCached;
            }
            return result;
        }

        static DebridHostState HostState(Row row, DebridHostList hosts)
        {
            if (hosts == null || row.Invalid || row.HosterUrls.Count == 0) return DebridHostState.Unknown;
            DebridHostState result = DebridHostState.Supported;
            foreach (string url in row.HosterUrls)
            {
                DebridHostState state = hosts.GetState(url);
                if (state == DebridHostState.Unsupported) return state;
                if (state == DebridHostState.Unavailable) result = state;
                else if (state == DebridHostState.Unknown && result == DebridHostState.Supported) result = state;
            }
            return result;
        }

        void Publish(DebridHostList hosts, Dictionary<string, DebridHostList> otherHosts, Dictionary<string, DebridCacheState> results, bool checking)
        {
            if (Canceled()) return;
            var next = new Dictionary<PackageCandidate, DebridLinkStatus>();
            foreach (Row row in rows)
            {
                DebridHostState host = HostState(row, hosts);
                DebridCacheState cache = HasCacheApi ? DebridCacheState.Cached : DebridCacheState.NotExposed;
                if (row.Invalid || !string.IsNullOrEmpty(row.Error)) cache = DebridCacheState.Unknown;
                else if (row.HasDirect) cache = DebridCacheState.NotExposed;
                else if (HasCacheApi)
                {
                    bool missing = row.Hashes.Count == 0, notCached = false, unknown = false, notExposed = false;
                    foreach (string hash in row.Hashes)
                    {
                        DebridCacheState state;
                        if (!results.TryGetValue(hash, out state)) missing = true;
                        else if (state == DebridCacheState.NotCached) notCached = true;
                        else if (state == DebridCacheState.NotExposed) notExposed = true;
                        else if (state != DebridCacheState.Cached) unknown = true;
                    }
                    cache = notCached ? DebridCacheState.NotCached : notExposed ? DebridCacheState.NotExposed : unknown ? DebridCacheState.Unknown :
                        missing ? (checking ? DebridCacheState.Checking : DebridCacheState.Unknown) : DebridCacheState.Cached;
                }
                string alternative = "";
                if (host != DebridHostState.Supported && !row.HasDirect && otherHosts != null)
                    foreach (var other in otherHosts)
                        if (HostState(row, other.Value) == DebridHostState.Supported) { alternative = other.Key; break; }
                string service = UnlockProviders.DisplayName(provider);
                string detail;
                if (row.Invalid) detail = "Source link status unavailable";
                else if (!string.IsNullOrEmpty(row.Error)) detail = "Source could not resolve this file; refresh or choose another mirror";
                else if (row.Direct) detail = "Direct file; provider cache is not used";
                else if (row.HasDirect) detail = "Includes direct files; provider cache cannot confirm the full set";
                else if (cache == DebridCacheState.Cached) detail = row.Hashes.Count > 1 ? "All archive parts are cached on " + service : "Cached on " + service;
                else if (cache == DebridCacheState.Checking) detail = "Checking " + service + " cache";
                else if (cache == DebridCacheState.NotCached) detail = row.Hashes.Count > 1 ? "Full archive set is not cached; " + service + " must fetch missing parts" : "Not cached; " + service + " must fetch the file";
                else if (cache == DebridCacheState.NotExposed) detail = "Cache status not provided";
                else detail = "Cache check unavailable; you can still try this link";
                if (!row.Direct && !row.Invalid)
                {
                    if (host == DebridHostState.Unavailable) detail += ". Host unavailable; choose another mirror or retry later";
                    else if (host == DebridHostState.Unsupported) detail += ". Unsupported host; choose another mirror or link service";
                    else if (host == DebridHostState.Unknown) detail += ". Host support unconfirmed";
                    else if (cache != DebridCacheState.Cached) detail += ". Host supported; account limits still apply";
                }
                string accountNote = "";
                if (hosts != null)
                    foreach (string url in row.HosterUrls) { accountNote = hosts.AccountNote(url); if (accountNote.Length != 0) break; }
                if (accountNote.Length != 0) detail += ". " + accountNote;
                next[row.Candidate] = new DebridLinkStatus(host, cache, detail, alternative, accountNote);
            }
            lock (stateLock)
            {
                if (Canceled()) return;
                foreach (var pair in next) states[pair.Key] = pair.Value;
            }
            if (!Canceled() && updated != null)
                try { updated(); } catch { }
        }
    }
}
