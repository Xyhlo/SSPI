using System;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Orbis
{
    internal static class TorBoxClient
    {
        const string Api = "https://api.torbox.app/v1/api";
        sealed class Pending { public string Id, FileId, FileName; public DateTime AttemptUtc; }
        static readonly Dictionary<string, Pending> PendingDownloads = new Dictionary<string, Pending>();
        static readonly object CreationGate = new object();
        static readonly Stopwatch CreationClock = Stopwatch.StartNew();
        static long NextCreation;

        // Local-only seams used by the source-linked host regression harness.
        internal static string ApiRootForTests;
        internal static string DataRootForTests;
        internal static TimeSpan ReconciliationGrace = TimeSpan.FromMinutes(2);
        static string ApiRoot { get { return string.IsNullOrEmpty(ApiRootForTests) ? Api : ApiRootForTests.TrimEnd('/'); } }
        static string DataRoot { get { return string.IsNullOrEmpty(DataRootForTests) ? AppSettings.DataDir : DataRootForTests; } }

        internal static void ResetForTests()
        {
            lock (PendingDownloads) PendingDownloads.Clear();
            NextCreation = 0;
            CreationClock.Restart();
        }

        /// <summary>Outcome of a single provider list poll for a prepared download.</summary>
        internal enum PreparedPollKind { Ready, Preparing, Rejected, Terminal, Transient }

        /// <summary>
        /// One poll result. The blocking resolve loop and the queue's parked
        /// preparation poller share this classification, so a parked TorBox item
        /// is polled with exactly the parsing and error rules of a normal resolve.
        /// </summary>
        internal sealed class PreparedPollResult
        {
            internal PreparedPollKind Kind;
            internal string FileId = "";
            internal string FileName = "";
            internal string StatusText = "";
            internal string Metric = "";
            internal string ProviderState = "";
            internal string Error = "";
            internal string RawJson = "";
            internal bool Visible;
            internal bool NeedsAction;
            internal Exception Transport;
            internal string FailedUrl;
            internal int PartNumber, PartCount, RetryAfterSeconds;
        }

        // Preparation is bounded by a deadline and reported on every poll. The host
        // regression harness overrides both seams so the loop can be driven
        // deterministically without sleeping or waiting ten minutes.
        internal static TimeSpan PreparationDeadline = TimeSpan.FromMinutes(10);
        internal static Action<int, Func<bool>> WaitImpl;

        public static string Unrestrict(string token, string hostUrl, Action<string> progress = null, Func<bool> cancel = null)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new Exception("TorBox token missing");
            if (string.IsNullOrEmpty(hostUrl))
                throw new Exception("Empty host URL");

            Pending pending = GetOrCreatePending(token, hostUrl, progress, cancel);

            string fileId = pending.FileId;
            var elapsed = Stopwatch.StartNew();
            int listed = 0, transient = 0;
            string lastState = "";
            for (int poll = 0; string.IsNullOrEmpty(fileId) && elapsed.Elapsed < PreparationDeadline; poll++)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                var outcome = PollPrepared(token, hostUrl, cancel);
                if (outcome.Kind == PreparedPollKind.Transient)
                {
                    // A list query that never answered says nothing about the prepared
                    // download, so keep it and retry a bounded number of times before
                    // surfacing the transport detail.
                    if (++transient > TransientListAttempts) throw DebridResolutionError.FromTransport("TorBox", hostUrl, outcome.Transport);
                    if (progress != null)
                        progress("TorBox preparing this file · list request retry " + transient + "/" + TransientListAttempts +
                            " · " + (int)elapsed.Elapsed.TotalSeconds + "s");
                    Wait(PollDelayWithRetryAfter(PollDelayMs(poll), outcome.RetryAfterSeconds), cancel);
                    continue;
                }
                transient = 0;
                if (outcome.Kind == PreparedPollKind.Rejected)
                    throw outcome.NeedsAction
                        ? DebridResolutionError.AmbiguousCreate("TorBox", hostUrl, "WEB_DOWNLOAD_RECONCILIATION_REQUIRED", outcome.Error)
                        : DebridResolutionError.FromResponse("TorBox", hostUrl, outcome.RawJson);
                if (outcome.Kind == PreparedPollKind.Terminal) throw PreparationFailure(hostUrl, outcome);
                if (outcome.Visible) listed++;
                fileId = outcome.Kind == PreparedPollKind.Ready ? outcome.FileId : null;
                if (!string.IsNullOrEmpty(fileId)) break;
                if (outcome.Visible)
                {
                    lastState = outcome.ProviderState;
                    if (progress != null) progress(PreparationProgress(outcome.Metric, elapsed.Elapsed));
                }
                else if (progress != null)
                {
                    // A freshly created web download is not always listed immediately.
                    // Report continuing provider activity instead of failing or waiting
                    // silently, and never create a second cloud download.
                    progress("TorBox preparing this file · waiting for the provider to list it · " + (int)elapsed.Elapsed.TotalSeconds + "s");
                }
                // Catch a newly cached file quickly; long host transfers poll at the API's five-second cadence.
                Wait(PollDelayMs(poll), cancel);
            }
            if (string.IsNullOrEmpty(fileId))
            {
                string detail = listed == 0
                    ? "the provider never listed the prepared download"
                    : lastState.Length > 0 ? "last provider state " + lastState : "no file was published yet";
                throw new Exception("TorBox is still preparing this file after " + (int)elapsed.Elapsed.TotalSeconds +
                    "s (" + detail + "). The prepared download was kept; retry from Downloads shortly.");
            }
            return RefreshPrepared(token, hostUrl, cancel);
        }

        internal static int PollDelayMs(int poll) { return poll == 0 ? 1000 : poll == 1 ? 2000 : 5000; }
        // Bounded tolerance for a list query that fails outright. The prepared
        // download id is retained across the retries, so one cloud job is reused.
        internal const int TransientListAttempts = 3;
        static Dictionary<string, object> Response(string json)
        {
            try { return Object(PackageSourceJson.Parse(json)); }
            catch { throw new IOException("TorBox returned an invalid JSON response. Retry shortly."); }
        }
        static Dictionary<string, object> Object(object value)
        {
            var result = value as Dictionary<string, object>;
            if (result == null) throw new IOException("TorBox returned unexpected download metadata.");
            return result;
        }
        static object Value(Dictionary<string, object> data, string name)
        { object value; return data.TryGetValue(name, out value) ? value : null; }
        static string Text(Dictionary<string, object> data, string name)
        {
            object value = Value(data, name);
            return value is string || value is long || value is int ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }
        static bool Flag(Dictionary<string, object> data, string name)
        { object value = Value(data, name); return value is bool && (bool)value; }
        static string First(Dictionary<string, object> data, params string[] names)
        {
            foreach (string name in names) { string value = Text(data, name); if (!string.IsNullOrEmpty(value)) return value; }
            return null;
        }
        // A freshly created web download is not always listed on the first poll, and
        // an absent entry is not an error: the caller keeps polling within its
        // deadline and reports that the provider has not published the file yet.
        static bool TryFindJob(object data, string id, out Dictionary<string, object> found)
        {
            found = data as Dictionary<string, object>;
            if (found != null)
            {
                string returnedId = First(found, "id", "webdownload_id");
                if (returnedId == null || returnedId == id) return true;
                found = null;
            }
            var items = data as List<object>;
            if (items != null) foreach (object candidate in items)
            {
                var item = candidate as Dictionary<string, object>;
                if (item != null && First(item, "id", "webdownload_id") == id) { found = item; return true; }
            }
            return false;
        }
        static List<Dictionary<string, object>> MatchingCreateAttempts(object data, string sourceUrl, DateTime attemptUtc)
        {
            var matches = new List<Dictionary<string, object>>();
            var single = data as Dictionary<string, object>;
            if (single != null) AddCreateMatch(single, sourceUrl, attemptUtc, matches);
            var items = data as List<object>;
            if (items != null)
                foreach (object value in items)
                {
                    var job = value as Dictionary<string, object>;
                    if (job != null) AddCreateMatch(job, sourceUrl, attemptUtc, matches);
                }
            return matches;
        }

        static void AddCreateMatch(Dictionary<string, object> job, string sourceUrl, DateTime attemptUtc,
            List<Dictionary<string, object>> matches)
        {
            string id = First(job, "id", "webdownload_id", "webdownloadId");
            if (!ValidId(id) || !SourceMatches(job, sourceUrl)) return;
            DateTime? created = CreateTime(job);
            if (!created.HasValue) return;
            // Provider timestamps may have whole-second precision. If evidence does
            // not fall near the persisted request attempt, leave the intent unresolved.
            if (created.Value < attemptUtc.AddSeconds(-5) || created.Value > attemptUtc.AddMinutes(5)) return;
            matches.Add(job);
        }

        static bool SourceMatches(Dictionary<string, object> job, string sourceUrl)
        {
            string[] names = { "link", "url", "source", "source_url", "sourceUrl", "src_url", "web_url", "webUrl",
                "webdownload_url", "webdownload_link", "download_link", "input_link", "original_link", "originalLink" };
            foreach (string name in names)
            {
                string value = Text(job, name);
                if (string.Equals(value, sourceUrl, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        static DateTime? CreateTime(Dictionary<string, object> job)
        {
            string[] names = { "created_at", "createdAt", "created", "created_on", "createdOn", "timestamp" };
            foreach (string name in names)
            {
                object value = Value(job, name);
                if (value == null) continue;
                double numeric;
                if ((value is long || value is int || value is double) &&
                    double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out numeric))
                {
                    try
                    {
                        if (numeric > 100000000000.0) numeric /= 1000.0;
                        if (numeric < 0 || numeric > 4102444800.0) continue;
                        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(numeric);
                    }
                    catch (ArgumentOutOfRangeException) { continue; }
                }
                string text = Convert.ToString(value, CultureInfo.InvariantCulture);
                long unix;
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out unix))
                {
                    try
                    {
                        double seconds = unix > 100000000000L ? unix / 1000.0 : unix;
                        if (seconds < 0 || seconds > 4102444800.0) continue;
                        return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
                    }
                    catch (ArgumentOutOfRangeException) { continue; }
                }
                DateTimeOffset parsed;
                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
                    return parsed.UtcDateTime;
            }
            return null;
        }
        static string ReadyFile(Dictionary<string, object> job)
        {
            var files = Value(job, "files") as List<object>;
            if (files == null || files.Count == 0) return null;
            if (files.Count > 1) throw new IOException("TorBox returned multiple files for one package. Select a single package or archive volume.");
            if (!Flag(job, "download_finished") && !Flag(job, "download_present")) return null;
            string id = Text(Object(files[0]), "id");
            if (!ValidId(id)) throw new IOException("TorBox returned an invalid file ID");
            return id;
        }
        static string ReadyFileName(Dictionary<string, object> job)
        {
            if (string.IsNullOrEmpty(ReadyFile(job))) return "";
            var files = Value(job, "files") as List<object>;
            return files == null || files.Count == 0 ? "" : Text(Object(files[0]), "name") ?? "";
        }
        static bool MissingJob(string code)
        { return code == "ITEM_NOT_FOUND" || code == "DOWNLOAD_NOT_FOUND" || code == "WEB_DOWNLOAD_NOT_FOUND"; }
        static double Number(Dictionary<string, object> job, string name)
        {
            object value = Value(job, name);
            if (!(value is long) && !(value is double) && !(value is int)) return -1;
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsNaN(number) || double.IsInfinity(number) ? -1 : number;
        }
        internal static string PreparationProgress(Dictionary<string, object> job, TimeSpan elapsed)
        {
            return PreparationProgress(MetricText(job), elapsed);
        }

        internal static string PreparationProgress(string metric, TimeSpan elapsed)
        {
            if (string.IsNullOrEmpty(metric)) return "TorBox preparing · " + (int)elapsed.TotalSeconds + "s";
            return "TorBox preparing · " + metric;
        }

        /// <summary>Provider-reported metric only (no elapsed fallback), so the
        /// queue can show a parked preparation without inventing a timer.</summary>
        static string MetricText(Dictionary<string, object> job)
        {
            var parts = new List<string>();
            string state = (Text(job, "download_state") ?? "").ToLowerInvariant();
            if (state == "queued" || state == "waiting") parts.Add("Queued at TorBox");
            else if (state == "downloading") parts.Add("Host to TorBox");
            else if (state == "staging" || state == "processing" || state == "checking") parts.Add("Staging at TorBox");
            double progress = Number(job, "progress"), speed = Number(job, "download_speed"), eta = Number(job, "eta");
            if (progress >= 0 && progress <= 1) parts.Add((progress * 100).ToString("0", CultureInfo.InvariantCulture) + "%");
            if (speed > 0) parts.Add((speed / 1000000).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s");
            if (eta > 0 && eta < 86400) parts.Add("ETA " + TimeSpan.FromSeconds(eta).ToString(@"h\:mm\:ss"));
            return string.Join(" · ", parts.ToArray());
        }

        static string HashKey(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")));
        }
        static string PendingKey(string token, string hostUrl) { return HashKey(token.Trim() + "\n" + hostUrl); }
        static string AccountKey(string token) { return HashKey(token.Trim()); }

        static Pending LookupPending(string key)
        {
            Pending pending;
            lock (PendingDownloads) { PendingDownloads.TryGetValue(key, out pending); }
            if (pending == null) pending = LoadPending(key);
            return pending;
        }

        static Pending ReloadPending(string key)
        {
            lock (PendingDownloads) PendingDownloads.Remove(key);
            return LoadPending(key);
        }

        static void CachePending(string key, Pending pending)
        {
            lock (PendingDownloads) PendingDownloads[key] = pending;
        }

        static string HashFilePart(string key) { return key.Replace("-", ""); }

        static FileStream AcquireFileLock(string path, Func<bool> cancel)
        {
            string directory = Path.GetDirectoryName(path);
            Directory.CreateDirectory(directory);
            var waited = Stopwatch.StartNew();
            while (true)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException)
                {
                    if (waited.Elapsed > TimeSpan.FromSeconds(30))
                        throw new IOException("TorBox persistence lock could not be acquired.");
                    Wait(100, cancel);
                }
            }
        }

        static string SourceLockPath(string key)
        { return Path.Combine(DataRoot, "torbox-pending", HashFilePart(key) + ".lock"); }

        static string AdmissionPath(string accountKey)
        { return Path.Combine(DataRoot, "torbox-admissions", HashFilePart(accountKey) + ".txt"); }

        static string AdmissionLockPath(string accountKey)
        { return Path.Combine(DataRoot, "torbox-admissions", HashFilePart(accountKey) + ".lock"); }

        /// <summary>The durable prepared record exists for this account and link.</summary>
        internal static bool HasPreparedDownload(string token, string hostUrl)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrEmpty(hostUrl)) return false;
            return LookupPending(PendingKey(token, hostUrl)) != null;
        }

        /// <summary>
        /// One list poll for the prepared download. Called by the blocking resolve
        /// loop and by the queue's parked poller, so both use the same parsing,
        /// same terminal-state rules and the same durable pending record. Never
        /// creates a provider job; a missing record is a rejection, not a retry.
        /// </summary>
        internal static PreparedPollResult PollPrepared(string token, string hostUrl, Func<bool> cancel)
        {
            var result = new PreparedPollResult();
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrEmpty(hostUrl))
            {
                result.Kind = PreparedPollKind.Rejected;
                result.Error = "TorBox token missing";
                return result;
            }
            string key = PendingKey(token, hostUrl);
            Pending pending = LookupPending(key);
            if (pending == null)
            {
                result.Kind = PreparedPollKind.Rejected;
                result.Error = "No prepared TorBox download; resolve the package again";
                return result;
            }
            if (cancel != null && cancel()) throw new OperationCanceledException();

            bool reconciling = string.IsNullOrEmpty(pending.Id);
            string url = ApiRoot + "/webdl/mylist?" + (reconciling ? "" : "id=" + Uri.EscapeDataString(pending.Id) + "&") + "bypass_cache=true";
            string list;
            try { list = NetHttp.GetString(url, 15000, null, token.Trim(), cancel: cancel); }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;
                var transport = DebridResolutionError.FromTransport("TorBox", hostUrl, ex) as DebridResolutionError;
                result.Transport = ex;
                result.RetryAfterSeconds = transport == null ? 0 : transport.RetryAfterSeconds;
                result.Error = transport == null ? ex.Message : transport.Message;
                bool hasProviderResponse = transport != null &&
                    (transport.HttpStatusCode != 0 || !string.IsNullOrEmpty(transport.ProviderCode));
                if (hasProviderResponse && !transport.IsTransient && !transport.IsRateLimited)
                {
                    result.Kind = PreparedPollKind.Rejected;
                    result.NeedsAction = transport.NeedsAction;
                }
                else result.Kind = PreparedPollKind.Transient;
                return result;
            }
            result.RawJson = list;
            var response = Response(list);
            if (!Flag(response, "success"))
            {
                var failure = DebridResolutionError.FromResponse("TorBox", hostUrl, list);
                result.Error = failure.Message;
                result.RetryAfterSeconds = failure.RetryAfterSeconds;
                if (failure.IsTransient || failure.IsRateLimited)
                {
                    result.Kind = PreparedPollKind.Transient;
                    result.Transport = failure;
                    return result;
                }
                if (!reconciling && MissingJob(Text(response, "error"))) ForgetPending(key);
                result.NeedsAction = failure.NeedsAction;
                result.Kind = PreparedPollKind.Rejected;
                return result;
            }

            object data = Value(response, "data");
            Dictionary<string, object> job;
            bool visible;
            if (reconciling)
            {
                var matches = MatchingCreateAttempts(data, hostUrl, pending.AttemptUtc);
                if (matches.Count > 1)
                {
                    result.Kind = PreparedPollKind.Rejected;
                    result.NeedsAction = true;
                    result.Error = "TorBox returned multiple matching downloads after an uncertain create. Check the TorBox account before retrying.";
                    return result;
                }
                if (matches.Count == 1)
                {
                    job = matches[0];
                    string id = First(job, "id", "webdownload_id", "webdownloadId");
                    if (!ValidId(id))
                    {
                        result.Kind = PreparedPollKind.Rejected;
                        result.NeedsAction = true;
                        result.Error = "TorBox found the matching download but returned no usable receipt. Check the TorBox account before retrying.";
                        return result;
                    }
                    string fileId = ReadyFile(job) ?? "";
                    pending = new Pending { Id = id, FileId = fileId,
                        FileName = string.IsNullOrEmpty(fileId) ? "" : ReadyFileName(job), AttemptUtc = pending.AttemptUtc };
                    SavePending(key, pending);
                    visible = true;
                }
                else
                {
                    result.StatusText = "TorBox checking an uncertain create";
                    if (DateTime.UtcNow - pending.AttemptUtc >= ReconciliationGrace)
                    {
                        result.Kind = PreparedPollKind.Rejected;
                        result.NeedsAction = true;
                        result.Error = "TorBox could not confirm whether this download was created. Automatic resubmission was stopped to avoid a duplicate. Check the TorBox account before retrying.";
                    }
                    else result.Kind = PreparedPollKind.Preparing;
                    return result;
                }
            }
            else visible = TryFindJob(data, pending.Id, out job);

            result.Visible = visible;
            if (visible)
            {
                string fileId = ReadyFile(job);
                if (!string.IsNullOrEmpty(fileId))
                {
                    string fileName = ReadyFileName(job);
                    if (string.IsNullOrEmpty(fileName) && pending.FileId == fileId) fileName = pending.FileName ?? "";
                    if (pending.FileId != fileId || !string.Equals(pending.FileName ?? "", fileName, StringComparison.Ordinal))
                    {
                        pending = new Pending { Id = pending.Id, FileId = fileId, FileName = fileName, AttemptUtc = pending.AttemptUtc };
                        SavePending(key, pending);
                    }
                    result.Kind = PreparedPollKind.Ready;
                    result.FileId = fileId;
                    result.FileName = fileName;
                    result.Metric = MetricText(job);
                    result.StatusText = PreparationProgress(result.Metric, TimeSpan.Zero);
                    return result;
                }
                string state = Text(job, "download_state") ?? "preparing";
                result.ProviderState = state;
                result.Metric = MetricText(job);
                result.StatusText = PreparationProgress(result.Metric, TimeSpan.Zero);
                if (state.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || state.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    ForgetPending(key);
                    result.Kind = PreparedPollKind.Terminal;
                    result.Error = "TorBox host download failed: " + state;
                    return result;
                }
            }
            else result.StatusText = "TorBox preparing";
            result.Kind = PreparedPollKind.Preparing;
            return result;
        }

        /// <summary>
        /// Queue preflight before the blocking resolve. Guarantees a prepared
        /// download exists (creating at most one cloud job on first sight),
        /// performs a bounded set of list polls for a cache hit, and returns Ready when the provider
        /// already published a file. A Ready result here lets the normal resolve
        /// skip its loop entirely, so no second provider job is ever created.
        /// </summary>
        internal static PreparedPollResult TryParkOrPrepare(string token, string hostUrl, Func<bool> cancel, Action<string> progress = null,
            bool cached = false)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrEmpty(hostUrl))
                return new PreparedPollResult { Kind = PreparedPollKind.Rejected, Error = "TorBox token missing" };
            Pending pending = GetOrCreatePending(token, hostUrl, progress, cancel);
            if (!string.IsNullOrEmpty(pending.FileId))
                return new PreparedPollResult { Kind = PreparedPollKind.Ready, FileId = pending.FileId,
                    FileName = pending.FileName ?? "" };
            var poll = PollPrepared(token, hostUrl, cancel);
            // Cache availability is a hint, not a ready receipt. Briefly check for
            // the published file before yielding to the normal preparation cadence.
            for (int n = 0; cached && n < CachedReadyPolls && poll.Kind == PreparedPollKind.Preparing; n++)
            {
                if (progress != null) progress("Cached at TorBox · starting");
                Wait(CachedReadyPollMs, cancel);
                poll = PollPrepared(token, hostUrl, cancel);
            }
            return poll;
        }

        internal const int CachedReadyPolls = 6;
        internal const int CachedReadyPollMs = 750;

        /// <summary>
        /// Web-download cache lookup. TorBox identifies a link by the MD5 of the
        /// exact URL. Returns the subset of <paramref name="urls"/> that TorBox
        /// already holds; any failure returns an empty set so the normal
        /// preparation path is unchanged. This call never creates a cloud job.
        /// </summary>
        internal static HashSet<string> CachedLinks(string token, IList<string> urls, Func<bool> cancel, int timeoutMs = 8000)
        {
            var cached = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(token) || urls == null || urls.Count == 0) return cached;
            var byHash = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string url in urls)
                if (IsHttp(url) && byHash.Count < 32) byHash[LinkHash(url)] = url;
            if (byHash.Count == 0) return cached;
            try
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                string json = NetHttp.GetString(ApiRoot + "/webdl/checkcached?hash=" +
                    string.Join(",", new List<string>(byHash.Keys).ToArray()) + "&format=object",
                    timeoutMs, null, token.Trim(), cancel: cancel);
                var response = Response(json);
                if (!Flag(response, "success")) return cached;
                var data = Value(response, "data") as Dictionary<string, object>;
                if (data != null)
                    foreach (var pair in data)
                    {
                        string url;
                        if ((pair.Value is Dictionary<string, object> || Equals(pair.Value, true)) &&
                            byHash.TryGetValue(pair.Key, out url)) cached.Add(url);
                    }
                var list = Value(response, "data") as List<object>;
                if (list != null)
                    foreach (object item in list)
                    {
                        var row = item as Dictionary<string, object>;
                        string hash = row == null ? null : Text(row, "hash"), url;
                        if (hash != null && byHash.TryGetValue(hash, out url)) cached.Add(url);
                    }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // A cache lookup is an optimisation; it must never block preparation.
                SspiLog.Write("download", "event=torbox-cache-check-failed exception=" + ex.GetType().Name);
                cached.Clear();
            }
            return cached;
        }

        internal static string LinkHash(string url)
        {
            using (var md5 = MD5.Create())
            {
                byte[] digest = md5.ComputeHash(Encoding.UTF8.GetBytes(url ?? ""));
                var text = new StringBuilder(32);
                foreach (byte b in digest) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        static Pending GetOrCreatePending(string token, string hostUrl, Action<string> progress, Func<bool> cancel)
        {
            string key = PendingKey(token, hostUrl);
            using (AcquireFileLock(SourceLockPath(key), cancel))
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                Pending pending = ReloadPending(key);
                if (pending != null) return pending;
                while (!Monitor.TryEnter(CreationGate, 100))
                    if (cancel != null && cancel()) throw new OperationCanceledException();
                try
                {
                    int delay = (int)Math.Max(0, NextCreation - CreationClock.ElapsedMilliseconds);
                    if (delay > 0) Wait(delay, cancel);
                    TakeCreateAdmission(token, hostUrl, cancel);
                    pending = new Pending { Id = "", FileId = "", AttemptUtc = DateTime.UtcNow };
                    SavePending(key, pending);
                    if (progress != null) progress("Sending file to TorBox");

                    string json;
                    try { json = CreateWebDownload(token.Trim(), hostUrl, cancel); }
                    catch (OperationCanceledException) { throw; }
                    catch (DebridResolutionError ex)
                    {
                        // These explicit HTTP responses prove the provider refused
                        // the create, so only their intent is safe to clear.
                        if (ex.HttpStatusCode == 401 || ex.HttpStatusCode == 403 || ex.HttpStatusCode == 429 ||
                            ex.ProviderCode == "DOWNLOAD_SERVER_ERROR")
                            ForgetPending(key);
                        throw;
                    }
                    finally { NextCreation = CreationClock.ElapsedMilliseconds + 1000; }

                    Dictionary<string, object> envelope;
                    try { envelope = Response(json); }
                    catch
                    {
                        throw DebridResolutionError.AmbiguousCreate("TorBox", hostUrl, "CREATE_RECEIPT_UNREADABLE",
                            "TorBox accepted a create request but its receipt could not be read. Reconciliation is required before retrying.");
                    }
                    if (!Flag(envelope, "success"))
                    {
                        object successValue = Value(envelope, "success");
                        string rejectionCode = Text(envelope, "error");
                        if (!(successValue is bool) || (bool)successValue || string.IsNullOrWhiteSpace(rejectionCode))
                        {
                            throw DebridResolutionError.AmbiguousCreate("TorBox", hostUrl, "CREATE_RECEIPT_UNREADABLE",
                                "TorBox returned an unclear create response. Reconciliation is required before retrying.");
                        }
                        DebridResolutionError rejection = DebridResolutionError.FromResponse("TorBox", hostUrl, json);
                        ForgetPending(key);
                        throw rejection;
                    }

                    Dictionary<string, object> created;
                    try { created = Object(Value(envelope, "data")); }
                    catch
                    {
                        throw DebridResolutionError.AmbiguousCreate("TorBox", hostUrl, "CREATE_RECEIPT_UNREADABLE",
                            "TorBox accepted a create request but returned no usable receipt. Reconciliation is required before retrying.");
                    }
                    string id = First(created, "webdownload_id", "webdownloadId", "id");
                    if (!ValidId(id))
                    {
                        throw DebridResolutionError.AmbiguousCreate("TorBox", hostUrl, "CREATE_RECEIPT_UNREADABLE",
                            "TorBox accepted a create request but returned no usable receipt. Reconciliation is required before retrying.");
                    }

                    pending = new Pending { Id = id, FileId = "", AttemptUtc = pending.AttemptUtc };
                    SavePending(key, pending);
                    string createdFile = ReadyFile(created);
                    if (!string.IsNullOrEmpty(createdFile))
                    {
                        pending = new Pending { Id = id, FileId = createdFile, FileName = ReadyFileName(created), AttemptUtc = pending.AttemptUtc };
                        SavePending(key, pending);
                    }
                    if (cancel != null && cancel()) throw new OperationCanceledException();
                    if (progress != null) progress("File accepted by TorBox");
                    return pending;
                }
                finally { Monitor.Exit(CreationGate); }
            }
        }

        /// <summary>Request a fresh signed link for an already prepared TorBox task.</summary>
        internal static string RefreshPrepared(string token, string sourceUrl, Func<bool> cancel)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrEmpty(sourceUrl))
                throw new Exception("TorBox token or source URL is missing");
            string key = PendingKey(token, sourceUrl);
            Pending pending = LookupPending(key);
            if (pending == null || string.IsNullOrEmpty(pending.Id) || string.IsNullOrEmpty(pending.FileId))
                throw DebridResolutionError.AmbiguousCreate("TorBox", sourceUrl, "PREPARED_RECEIPT_MISSING",
                    "TorBox has no confirmed prepared-download receipt to refresh. Automatic creation was stopped to avoid a duplicate; reconcile the existing task before retrying.");
            if (cancel != null && cancel()) throw new OperationCanceledException();
            string url = ApiRoot + "/webdl/requestdl?token=" + Uri.EscapeDataString(token.Trim()) +
                "&web_id=" + Uri.EscapeDataString(pending.Id) + "&file_id=" + Uri.EscapeDataString(pending.FileId) + "&zip_link=false";
            string json;
            try { json = NetHttp.GetString(url, 20000, cancel: cancel); }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;
                throw DebridResolutionError.FromTransport("TorBox", sourceUrl, ex);
            }
            string raw = (json ?? "").Trim().Trim('"');
            if (cancel != null && cancel()) throw new OperationCanceledException();
            if (IsHttp(raw))
            {
                DownloadTransferSettings.RememberProviderLimit(raw, DownloadTransferSettings.MaxRangeCount);
                return raw;
            }
            var response = Response(json);
            if (!Flag(response, "success"))
            {
                if (MissingJob(Text(response, "error"))) ForgetPending(key);
                throw DebridResolutionError.FromResponse("TorBox", sourceUrl, json);
            }
            string download = Text(response, "data");
            if (!IsHttp(download)) throw new IOException("TorBox returned no download URL");
            if (cancel != null && cancel()) throw new OperationCanceledException();
            DownloadTransferSettings.RememberProviderLimit(download.Trim(), DownloadTransferSettings.MaxRangeCount);
            return download.Trim();
        }

        // Submit every advertised archive part without waiting for an earlier part
        // to finish. Four bounded operations per pass keep large sets cooperative.
        internal static PreparedPollResult PrepareMany(string token, IList<string> urls, int pass, Func<bool> cancel, Action<string> progress = null)
        {
            if (urls == null || urls.Count == 0 || urls.Count > 256)
                throw new IOException("Invalid TorBox archive part count");
            int start = (int)((long)Math.Max(0, pass) * 4 % urls.Count);
            string lastMetric = "";
            for (int n = 0; n < Math.Min(4, urls.Count); n++)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                int index = (start + n) % urls.Count;
                var pending = LookupPending(PendingKey(token, urls[index]));
                if (pending != null && !string.IsNullOrEmpty(pending.FileId)) continue;
                Action<string> report = stage => {
                    if (progress == null) return;
                    int sent, ready;
                    progress("TorBox · " + MultipartMetric(token, urls, out sent, out ready) +
                        " · part " + (index + 1) + "/" + urls.Count + ": " + stage);
                };
                report(pending == null ? "Sending" : "Checking preparation");
                PreparedPollResult poll;
                try { poll = TryParkOrPrepare(token, urls[index], cancel, report); }
                catch (DebridResolutionError ex)
                {
                    // These explicit rejections did not create a job. Keep the
                    // submitted parts and retry the missing part on a later pass.
                    if (ex.ProviderCode == "ACTIVE_LIMIT" || ex.ProviderCode == "COOLDOWN_LIMIT")
                    {
                        int sent, ready;
                        string wait = ex.ProviderCode == "ACTIVE_LIMIT" ? "Waiting for a TorBox download slot" : "Waiting for TorBox cooldown";
                        string waitingMetric = MultipartMetric(token, urls, out sent, out ready) +
                            " · part " + (index + 1) + "/" + urls.Count + ": " + wait;
                        if (progress != null) progress("TorBox · " + waitingMetric);
                        return new PreparedPollResult {
                            Kind = PreparedPollKind.Preparing, Visible = true,
                            ProviderState = "waiting for capacity", Metric = waitingMetric,
                            StatusText = "TorBox preparing archive · " + waitingMetric,
                            RetryAfterSeconds = Math.Max(30, ex.RetryAfterSeconds)
                        };
                    }
                    throw ex.ForArchivePart(index + 1, urls.Count);
                }
                if (poll.Kind == PreparedPollKind.Rejected || poll.Kind == PreparedPollKind.Terminal || poll.Kind == PreparedPollKind.Transient)
                {
                    poll.FailedUrl = urls[index];
                    poll.PartNumber = index + 1; poll.PartCount = urls.Count;
                    poll.Error = "Archive part " + (index + 1) + "/" + urls.Count + ": " + poll.Error;
                    return poll;
                }
                lastMetric = string.IsNullOrEmpty(poll.Metric) ? "" : " · part " + (index + 1) + "/" + urls.Count + ": " + poll.Metric;
                report(poll.Kind == PreparedPollKind.Ready ? "Ready" : string.IsNullOrEmpty(poll.Metric) ?
                    "Waiting for TorBox" : poll.Metric);
            }
            int submitted, completed;
            string metric = MultipartMetric(token, urls, out submitted, out completed);
            if (completed < urls.Count) metric += lastMetric;
            return new PreparedPollResult {
                Kind = completed == urls.Count ? PreparedPollKind.Ready : PreparedPollKind.Preparing,
                Visible = true, ProviderState = "preparing archive",
                Metric = metric,
                StatusText = "TorBox preparing archive · " + metric
            };
        }

        static string MultipartMetric(string token, IList<string> urls, out int submitted, out int ready)
        {
            submitted = ready = 0;
            foreach (string url in urls) {
                var pending = LookupPending(PendingKey(token, url));
                if (pending == null) continue;
                submitted++;
                if (!string.IsNullOrEmpty(pending.FileId)) ready++;
            }
            return submitted + "/" + urls.Count + " parts sent · " + ready + "/" + urls.Count + " ready";
        }
        internal static DebridResolutionError PreparationFailure(string hostUrl, PreparedPollResult outcome)
        {
            if (outcome.NeedsAction)
            {
                var action = DebridResolutionError.AmbiguousCreate("TorBox", hostUrl, "WEB_DOWNLOAD_RECONCILIATION_REQUIRED", outcome.Error);
                return outcome.PartCount > 0 ? action.ForArchivePart(outcome.PartNumber, outcome.PartCount) : action;
            }
            var failure = DebridResolutionError.FromResponse("TorBox", hostUrl,
                outcome.Kind == PreparedPollKind.Terminal ? "{\"error\":\"DOWNLOAD_FAILED\"}" : outcome.RawJson);
            return outcome.PartCount > 0 ? failure.ForArchivePart(outcome.PartNumber, outcome.PartCount) : failure;
        }

        static string CreateWebDownload(string token, string hostUrl, Func<bool> cancel)
        {
            if (cancel != null && cancel()) throw new OperationCanceledException();
            try
            {
                return NetHttp.PostForm(ApiRoot + "/webdl/createwebdownload",
                    "link=" + Uri.EscapeDataString(hostUrl) + "&as_queued=false", 30000, null, token, cancel: cancel);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw DebridResolutionError.FromTransport("TorBox", hostUrl, ex); }
        }

        static int PollDelayWithRetryAfter(int normalDelayMs, int retryAfterSeconds)
        {
            if (retryAfterSeconds <= 0) return normalDelayMs;
            long requested = (long)retryAfterSeconds * 1000;
            return (int)Math.Min(int.MaxValue, Math.Max((long)normalDelayMs, requested));
        }
        static void Wait(int milliseconds, Func<bool> cancel)
        {
            if (WaitImpl != null) { WaitImpl(milliseconds, cancel); return; }
            for (int n = 0; n < milliseconds; n += 100) {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                Thread.Sleep(Math.Min(100, milliseconds - n));
            }
        }
        static bool ValidId(string id)
        {
            long value;
            return !string.IsNullOrEmpty(id) && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
        }

        static void TakeCreateAdmission(string token, string sourceUrl, Func<bool> cancel)
        {
            string account = AccountKey(token), path = AdmissionPath(AccountKey(token));
            using (AcquireFileLock(AdmissionLockPath(account), cancel))
            {
                string[] lines = ReadLinesIfPresent(path, 8192);
                var attempts = new List<long>();
                if (lines != null)
                {
                    if (lines.Length == 0 || lines[0] != "TORBOX-ADMISSIONS-1")
                        throw new IOException("TorBox create-admission history is unreadable; new creates are blocked to avoid exceeding the provider limit.");
                    for (int i = 1; i < lines.Length; i++)
                    {
                        long ticks;
                        if (!long.TryParse(lines[i], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) ||
                            ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
                            throw new IOException("TorBox create-admission history is unreadable; new creates are blocked to avoid exceeding the provider limit.");
                        attempts.Add(ticks);
                    }
                }
                long now = DateTime.UtcNow.Ticks;
                long cutoff = now - TimeSpan.FromHours(1).Ticks;
                attempts.RemoveAll(ticks => ticks < cutoff);
                if (attempts.Count >= 60)
                {
                    attempts.Sort();
                    int retry = (int)Math.Max(1, Math.Ceiling(TimeSpan.FromHours(1).TotalSeconds -
                        TimeSpan.FromTicks(Math.Max(0, now - attempts[0])).TotalSeconds));
                    var limited = (DebridResolutionError)DebridResolutionError.FromResponse("TorBox", sourceUrl,
                        "{\"error\":\"RATE_LIMITED\"}");
                    limited.IsRateLimited = true;
                    limited.IsTransient = true;
                    limited.RetryAfterSeconds = retry;
                    limited.HttpStatusCode = 429;
                    throw limited;
                }
                attempts.Add(now);
                var text = new StringBuilder("TORBOX-ADMISSIONS-1\n");
                foreach (long ticks in attempts) text.Append(ticks.ToString(CultureInfo.InvariantCulture)).Append('\n');
                AtomicFile.WriteText(path, text.ToString());
            }
        }

        static string[] ReadLinesIfPresent(string path, int maximumBytes)
        {
            string contents;
            try { contents = File.ReadAllText(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            if (Encoding.UTF8.GetByteCount(contents) > maximumBytes)
                throw new IOException("TorBox persistence record is too large to read safely.");
            string[] lines = contents.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            int count = lines.Length;
            while (count > 0 && lines[count - 1].Length == 0) count--;
            if (count != lines.Length) Array.Resize(ref lines, count);
            return lines;
        }

        static string PendingPath(string key)
        { return Path.Combine(DataRoot, "torbox-pending", HashFilePart(key) + ".txt"); }
        static Pending LoadPending(string key)
        {
            string path = PendingPath(key);
            string[] lines = ReadLinesIfPresent(path, 4096);
            if (lines == null) return null;
            Pending pending;
            long ticks;
            if (lines.Length == 5 && lines[0] == "TORBOX-PENDING-3" &&
                (lines[1] == "" || ValidId(lines[1])) && (lines[2] == "" || ValidId(lines[2])) &&
                long.TryParse(lines[4], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) &&
                ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks)
            {
                pending = new Pending { Id = lines[1], FileId = lines[2], FileName = DecodePendingFileName(lines[3]),
                    AttemptUtc = new DateTime(ticks, DateTimeKind.Utc) };
            }
            else if (lines.Length == 4 && lines[0] == "TORBOX-PENDING-2" &&
                (lines[1] == "" || ValidId(lines[1])) && (lines[2] == "" || ValidId(lines[2])) &&
                long.TryParse(lines[3], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) &&
                ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks)
            {
                pending = new Pending { Id = lines[1], FileId = lines[2], AttemptUtc = new DateTime(ticks, DateTimeKind.Utc) };
            }
            else if (lines.Length == 3 && ValidId(lines[0]) && (lines[1] == "" || ValidId(lines[1])) &&
                long.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) &&
                ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks)
            {
                // Preserve legacy acknowledged receipts even after their old six-hour
                // cache expiry: forgetting one could cause a duplicate web download.
                pending = new Pending { Id = lines[0], FileId = lines[1], AttemptUtc = DateTime.UtcNow };
                SavePending(key, pending);
            }
            else throw new IOException("TorBox prepared-download record is unreadable; automatic resubmission was blocked.");
            CachePending(key, pending);
            return pending;
        }
        static void SavePending(string key, Pending pending)
        {
            string path = PendingPath(key), dir = Path.GetDirectoryName(path);
            Directory.CreateDirectory(dir);
            AtomicFile.WriteText(path, "TORBOX-PENDING-3\n" + (pending.Id ?? "") + "\n" +
                (pending.FileId ?? "") + "\n" + EncodePendingFileName(pending.FileName) + "\n" +
                pending.AttemptUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            CachePending(key, pending);
        }
        static string EncodePendingFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "";
            byte[] bytes = Encoding.UTF8.GetBytes(fileName);
            return bytes.Length > 1024 ? "" : Convert.ToBase64String(bytes);
        }
        static string DecodePendingFileName(string encoded)
        {
            if (string.IsNullOrEmpty(encoded)) return "";
            try
            {
                byte[] bytes = Convert.FromBase64String(encoded);
                return bytes.Length > 1024 ? "" : Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }
        static void ForgetPending(string key)
        {
            File.Delete(PendingPath(key));
            lock (PendingDownloads) PendingDownloads.Remove(key);
        }

        public static string ProbeUser(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "REJECTED: TorBox API key missing";
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    string json;
                    try { json = NetHttp.GetString(Api + "/user/me", 15000, null, token.Trim()); }
                    catch (Exception ex) { throw DebridResolutionError.FromTransport("TorBox", Api, ex); }
                    var response = Response(json);
                    if (!Flag(response, "success"))
                        throw DebridResolutionError.FromResponse("TorBox", Api, json);
                    var data = Object(Value(response, "data"));
                    string user = First(data, "email", "base_email", "auth_id") ?? "?";
                    string plan = Text(data, "plan") ?? "?";
                    int planId;
                    if (!int.TryParse(plan, out planId) || planId < 0 || planId > 3)
                        return "VALID: TorBox key verified; plan status unavailable";
                    return (planId == 0 ? "FREE: " : "OK ") + "@" + user + " (plan " + plan + ")";
                }
                catch (DebridResolutionError ex)
                {
                    // AUTH_ERROR is a provider verification failure, not BAD_TOKEN.
                    // This read-only probe can retry once without creating a cloud job.
                    if (ex.ProviderCode == "AUTH_ERROR" && !ex.IsRateLimited && attempt == 0)
                    { Wait(1500, null); continue; }
                    if (ex.ProviderCode == "BAD_TOKEN" || ex.ProviderCode == "INVALID_TOKEN" || ex.ProviderCode == "NO_AUTH")
                        return "REJECTED: TorBox did not accept the saved API key";
                    if (ex.IsRateLimited) return "RATE_LIMITED: TorBox is busy; wait before retrying";
                    if (ex.ProviderCode == "PLAN_RESTRICTED_FEATURE" || ex.ProviderCode == "PLAN_RESTRICTED" || ex.ProviderCode == "NO_PREMIUM")
                        return "LIMITED: TorBox reports a plan restriction";
                    return "RETRY: TorBox account verification is unavailable; the saved key was kept";
                }
                catch
                {
                    // Provider bodies and transport exceptions may contain credentials.
                    return "RETRY: TorBox returned an unreadable account response; retry shortly";
                }
            }
        }

        static bool IsHttp(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                (value.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 value.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }
    }
}
