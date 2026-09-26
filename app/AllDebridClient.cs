using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal static class AllDebridClient
    {
        const string Api = "https://api.alldebrid.com/v4";
        internal static readonly TimeSpan PreparationDeadline = TimeSpan.FromMinutes(10);
        internal const int PollDelayMilliseconds = 5000;
        internal const int TransientPollAttempts = 3;
        sealed class Pending { internal long Id; internal DateTime Until; internal DateTime RetryAt; internal string Download; internal bool CreateIntent; internal bool Ambiguous; internal bool RetryCreate; }
        static readonly Dictionary<string, Pending> PendingLinks = new Dictionary<string, Pending>();
        internal enum PreparedPollKind { Ready, Preparing, Rejected, Terminal, Transient }
        internal sealed class PreparedPollResult
        {
            internal PreparedPollKind Kind;
            internal string Download = "";
            internal string ProviderState = "";
            internal string Error = "";
            internal Exception Transport;
            internal int RetryAfterSeconds;
            internal string ProviderCode = "";
            internal bool RetryCreate;
            internal string FailedUrl = "";
            internal int PartNumber, PartCount;
            internal List<string> StartedUrls = new List<string>();
        }
        // Host regression seam; production always uses NetHttp through Post.
        internal static Func<string, string, string, string, Dictionary<string, object>> PostImpl;

        public static string Unrestrict(string apiKey, string hostUrl, Action<string> progress = null, Func<bool> cancel = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new Exception("Connect AllDebrid in Connections");
            if (!IsHttp(hostUrl)) throw new Exception("AllDebrid: Invalid host URL");
            if (cancel != null && cancel()) throw new OperationCanceledException();
            PreparedPollResult result = TryParkOrPrepare(apiKey, hostUrl, cancel);
            if (result.Kind == PreparedPollKind.Ready) return CompletePrepared(apiKey, hostUrl, result.Download);
            if (result.Kind == PreparedPollKind.Rejected || result.Kind == PreparedPollKind.Terminal)
                throw result.Transport ?? new Exception(result.Error);
            var elapsed = Stopwatch.StartNew();
            int transient = 0;
            while (elapsed.Elapsed < PreparationDeadline)
            {
                if (progress != null) progress("AllDebrid is preparing the file");
                Wait(PollDelayMilliseconds, cancel);
                result = PollPrepared(apiKey, hostUrl, cancel);
                if (result.Kind == PreparedPollKind.Ready) return CompletePrepared(apiKey, hostUrl, result.Download);
                if (result.Kind == PreparedPollKind.Terminal || result.Kind == PreparedPollKind.Rejected)
                    throw result.Transport ?? new Exception(string.IsNullOrEmpty(result.Error) ? "AllDebrid could not prepare this file. Retry or choose another mirror." : result.Error);
                if (result.Kind == PreparedPollKind.Transient && ++transient > TransientPollAttempts)
                    throw result.Transport ?? new IOException("AllDebrid delayed-link polling did not answer");
                if (result.Kind != PreparedPollKind.Transient) transient = 0;
            }
            throw new Exception("AllDebrid is still preparing the file. Retry later.");
        }

        internal static PreparedPollResult TryParkOrPrepare(string apiKey, string hostUrl, Func<bool> cancel)
        {
            Validate(apiKey, hostUrl, cancel);
            string key = PendingKey(apiKey, hostUrl);
            Pending pending = LookupPending(key);
            if (pending != null)
            {
                if (pending.RetryCreate)
                {
                    if (pending.RetryAt > DateTime.UtcNow)
                        return new PreparedPollResult { Kind = PreparedPollKind.Preparing, ProviderState = "maintenance",
                            RetryCreate = true, RetryAfterSeconds = (int)Math.Min(int.MaxValue,
                                Math.Max(1, (pending.RetryAt - DateTime.UtcNow).TotalSeconds)) };
                    pending.RetryCreate = false;
                    pending.CreateIntent = true;
                    pending.RetryAt = DateTime.MinValue;
                    SavePending(key, pending);
                    lock (PendingLinks) PendingLinks[key] = pending;
                    pending = null;
                }
                if (pending != null)
                {
                    if (pending.Ambiguous || pending.CreateIntent && pending.Id <= 0)
                        throw AmbiguousCreate(hostUrl);
                    if (IsHttp(pending.Download))
                        return new PreparedPollResult { Kind = PreparedPollKind.Ready, Download = pending.Download, ProviderState = "ready" };
                    if (pending.RetryCreate)
                        return new PreparedPollResult { Kind = PreparedPollKind.Preparing, ProviderState = "maintenance" };
                    return PollPrepared(apiKey, hostUrl, cancel);
                }
            }

            // Persist intent before /link/unlock. If the response is lost, an
            // automatic retry cannot create a second provider operation.
            pending = new Pending { Until = DateTime.UtcNow.Add(PreparationDeadline), CreateIntent = true };
            SavePending(key, pending);
            lock (PendingLinks) { if (PendingLinks.Count >= 256) PendingLinks.Clear(); PendingLinks[key] = pending; }
            Dictionary<string, object> data;
            try { data = Post("/link/unlock", "link=" + Uri.EscapeDataString(hostUrl), apiKey, hostUrl, cancel); }
            catch (DebridResolutionError ex)
            {
                // A coded provider response is a definite rejection. A transport
                // failure may have arrived after acceptance, so retain the intent.
                if (ex.ProviderCode == "MAINTENANCE" || ex.HttpStatusCode == 429)
                {
                    pending.CreateIntent = false;
                    pending.RetryCreate = true;
                    int delay = Math.Max(ex.RetryAfterSeconds, ex.ProviderCode == "MAINTENANCE" ? PollDelayMilliseconds / 1000 : 1);
                    delay = Math.Min(24 * 60 * 60, delay);
                    pending.RetryAt = DateTime.UtcNow.AddSeconds(delay);
                    pending.Until = DateTime.UtcNow.Add(PreparationDeadline);
                    SavePending(key, pending);
                    lock (PendingLinks) PendingLinks[key] = pending;
                    return new PreparedPollResult { Kind = PreparedPollKind.Preparing, ProviderState = "maintenance",
                        RetryCreate = true, RetryAfterSeconds = delay, ProviderCode = ex.ProviderCode ?? "" };
                }
                if (!string.IsNullOrEmpty(ex.ProviderCode))
                    ForgetPending(key);
                else
                {
                    // A bare HTTP status (including 5xx) cannot prove the
                    // side-effecting unlock was rejected before acceptance.
                    pending.Ambiguous = true;
                    SavePending(key, pending);
                }
                throw;
            }
            catch
            {
                pending.Ambiguous = true;
                SavePending(key, pending);
                throw;
            }

            string download = Text(data, "link");
            if (IsHttp(download))
            {
                pending.CreateIntent = false;
                pending.Download = Remember(download);
                SavePending(key, pending);
                lock (PendingLinks) PendingLinks[key] = pending;
                if (cancel != null && cancel()) throw new OperationCanceledException();
                return new PreparedPollResult { Kind = PreparedPollKind.Ready, Download = pending.Download };
            }
            long delayed;
            if (!long.TryParse(Text(data, "delayed"), NumberStyles.None, CultureInfo.InvariantCulture, out delayed) || delayed <= 0)
            {
                pending.Ambiguous = true;
                SavePending(key, pending);
                var ambiguous = AmbiguousCreate(hostUrl);
                return new PreparedPollResult { Kind = PreparedPollKind.Terminal, Error = ambiguous.Message,
                    Transport = ambiguous, ProviderCode = ambiguous.ProviderCode };
            }
            pending.Id = delayed;
            pending.CreateIntent = false;
            SavePending(key, pending);
            lock (PendingLinks) { if (PendingLinks.Count >= 256) PendingLinks.Clear(); PendingLinks[key] = pending; }
            if (cancel != null && cancel()) throw new OperationCanceledException();
            return new PreparedPollResult { Kind = PreparedPollKind.Preparing, ProviderState = "delayed" };
        }

        internal static PreparedPollResult PrepareMany(string apiKey, IList<string> urls, Func<bool> cancel,
            Action<string> progress = null)
        { return PrepareMany(apiKey, urls, 0, cancel, progress); }

        internal static PreparedPollResult PrepareMany(string apiKey, IList<string> urls, int pass,
            Func<bool> cancel, Action<string> progress = null)
        {
            if (urls == null || urls.Count == 0 || urls.Count > 256)
                throw new IOException("Invalid AllDebrid archive part count");
            int start = (int)((long)Math.Max(0, pass) * 4 % urls.Count);
            int retryAfter = 0;
            string state = "preparing archive";
            var started = new List<string>();
            for (int n = 0; n < Math.Min(4, urls.Count); n++)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                int i = (start + n) % urls.Count;
                string url = urls[i];
                if (!IsHttp(url)) throw new IOException("Invalid AllDebrid archive part URL");
                if (progress != null) progress("AllDebrid · part " + (i + 1) + "/" + urls.Count + " · checking preparation");
                PreparedPollResult result;
                try { result = TryParkOrPrepare(apiKey, url, cancel); }
                catch (DebridResolutionError ex)
                {
                    if (ex.CanTryProvider && (ex.IsTransient || ex.IsRateLimited))
                        return new PreparedPollResult { Kind = PreparedPollKind.Transient, ProviderState = ex.ProviderCode ?? "retry",
                            Error = ex.Message, Transport = ex, RetryAfterSeconds = ex.RetryAfterSeconds,
                            ProviderCode = ex.ProviderCode ?? "", FailedUrl = url, PartNumber = i + 1, PartCount = urls.Count };
                    throw ex.ForArchivePart(i + 1, urls.Count);
                }
                if (result.Kind == PreparedPollKind.Rejected || result.Kind == PreparedPollKind.Terminal || result.Kind == PreparedPollKind.Transient)
                {
                    result.FailedUrl = url;
                    result.PartNumber = i + 1;
                    result.PartCount = urls.Count;
                    result.Error = "Archive part " + (i + 1) + "/" + urls.Count + ": " + result.Error;
                    return result;
                }
                if (result.Kind == PreparedPollKind.Preparing)
                {
                    retryAfter = Math.Max(retryAfter, result.RetryAfterSeconds);
                    state = result.ProviderState ?? state;
                }
                else if (progress != null) progress("AllDebrid · part " + (i + 1) + "/" + urls.Count + " · ready");
                if (HasRemoteOperation(apiKey, url)) started.Add(url);
            }
            bool allReady = true;
            foreach (string url in urls)
            {
                Pending pending = LookupPending(PendingKey(apiKey, url));
                if (pending == null || !IsHttp(pending.Download)) { allReady = false; break; }
            }
            return new PreparedPollResult { Kind = allReady ? PreparedPollKind.Ready : PreparedPollKind.Preparing,
                ProviderState = state, RetryAfterSeconds = retryAfter, StartedUrls = started };
        }

        internal static bool HasPreparedDownloads(string apiKey, IList<string> urls)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || urls == null) return false;
            foreach (string url in urls)
                if (IsHttp(url) && HasPreparedDownload(apiKey, url)) return true;
            return false;
        }

        internal static bool HasRemoteOperation(string apiKey, string hostUrl)
        {
            if (string.IsNullOrWhiteSpace(apiKey) || !IsHttp(hostUrl)) return false;
            Pending pending = LookupPending(PendingKey(apiKey, hostUrl));
            return pending != null && !pending.RetryCreate && (pending.Id > 0 || pending.Ambiguous || pending.CreateIntent || IsHttp(pending.Download));
        }

        internal static PreparedPollResult PollPrepared(string apiKey, string hostUrl, Func<bool> cancel)
        {
            Validate(apiKey, hostUrl, cancel);
            string key = PendingKey(apiKey, hostUrl);Pending pending = LookupPending(key);
            if (pending == null)
            {
                var missing = DebridResolutionError.AmbiguousCreate("AllDebrid", hostUrl, "MISSING_RECEIPT",
                    "The retained preparation receipt is missing. Check AllDebrid before retrying this source.");
                return new PreparedPollResult { Kind = PreparedPollKind.Terminal, ProviderState = "missing receipt",
                    Error = missing.Message, Transport = missing, ProviderCode = missing.ProviderCode };
            }
            if (pending.RetryCreate) return TryParkOrPrepare(apiKey, hostUrl, cancel);
            if (IsHttp(pending.Download))
                return new PreparedPollResult { Kind = PreparedPollKind.Ready, Download = pending.Download };
            if (pending.Id <= 0)
            {
                var ambiguous = AmbiguousCreate(hostUrl);
                return new PreparedPollResult { Kind = PreparedPollKind.Terminal, ProviderState = "unconfirmed",
                    Error = ambiguous.Message, Transport = ambiguous, ProviderCode = ambiguous.ProviderCode };
            }
            Dictionary<string, object> data;
            try { data = Post("/link/delayed", "id=" + pending.Id.ToString(CultureInfo.InvariantCulture), apiKey, hostUrl, cancel); }
            catch (DebridResolutionError ex)
            {
                if (string.IsNullOrEmpty(ex.ProviderCode) || ex.IsTransient || ex.IsRateLimited)
                    return new PreparedPollResult { Kind = PreparedPollKind.Transient, Transport = ex, Error = ex.Message,
                        ProviderCode = ex.ProviderCode ?? "", RetryAfterSeconds = ex.RetryAfterSeconds };
                return new PreparedPollResult { Kind = PreparedPollKind.Rejected, Transport = ex, ProviderCode = ex.ProviderCode ?? "", Error = ex.Message };
            }
            catch (Exception ex) { return new PreparedPollResult { Kind = PreparedPollKind.Transient, Transport = ex, Error = ex.Message }; }
            string download = Text(data, "link");
            if (IsHttp(download))
            {
                // Keep the ready link in memory until the queue claims it. The
                // next preflight must not park it again or repeat /link/unlock.
                lock (PendingLinks) pending.Download = Remember(download);
                SavePending(key, pending);
                if (cancel != null && cancel()) throw new OperationCanceledException();
                return new PreparedPollResult { Kind = PreparedPollKind.Ready, Download = pending.Download, ProviderState = Text(data, "status") };
            }
            string state = Text(data, "status");
            if (state == "3")
            {
                ForgetPending(key);
                var rejection = DebridResolutionError.FromResponse("AllDebrid", hostUrl,
                    "{\"status\":\"error\",\"code\":\"LINK_DOWN\"}");
                return new PreparedPollResult { Kind = PreparedPollKind.Terminal, ProviderState = state,
                    Error = rejection.Message, Transport = rejection, ProviderCode = rejection.ProviderCode };
            }
            return new PreparedPollResult { Kind = PreparedPollKind.Preparing, ProviderState = string.IsNullOrEmpty(state) ? "delayed" : state };
        }

        internal static bool HasPreparedDownload(string apiKey, string hostUrl)
        { return !string.IsNullOrWhiteSpace(apiKey) && IsHttp(hostUrl) && LookupPending(PendingKey(apiKey, hostUrl)) != null; }

        static void Validate(string apiKey, string hostUrl, Func<bool> cancel)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new Exception("Connect AllDebrid in Connections");
            if (!IsHttp(hostUrl)) throw new Exception("AllDebrid: Invalid host URL");
            if (cancel != null && cancel()) throw new OperationCanceledException();
        }

        static string Remember(string download)
        {
            download = download.Trim();
            DownloadTransferSettings.RememberProviderLimit(download, Math.Min(8, DownloadTransferSettings.MaxRangeCount));
            return download;
        }
        internal static string CompletePrepared(string apiKey, string hostUrl, string download)
        {
            string key = PendingKey(apiKey, hostUrl);
            Pending pending = LookupPending(key);
            if (pending == null || pending.Id <= 0) ForgetPending(key);
            else
            {
                pending.Download = Remember(download);
                pending.Ambiguous = false;
                SavePending(key, pending);
                lock (PendingLinks) PendingLinks[key] = pending;
            }
            return Remember(download);
        }

        internal static string Refresh(string apiKey, string hostUrl, Action<string> progress, Func<bool> cancel)
        {
            string key = PendingKey(apiKey, hostUrl);
            Pending pending = LookupPending(key);
            if (pending == null)
                throw AmbiguousCreate(hostUrl);
            if (pending.Ambiguous || pending.CreateIntent && pending.Id <= 0)
                throw AmbiguousCreate(hostUrl);
            if (pending.RetryCreate)
            {
                var retry = TryParkOrPrepare(apiKey, hostUrl, cancel);
                if (retry.Kind == PreparedPollKind.Ready) return CompletePrepared(apiKey, hostUrl, retry.Download);
                if (retry.Kind == PreparedPollKind.Rejected || retry.Kind == PreparedPollKind.Terminal)
                    throw retry.Transport ?? AmbiguousCreate(hostUrl);
                throw new ProviderPreparationWaitException(UnlockProviders.AllDebridId, hostUrl,
                    retry.ProviderState, retry.RetryAfterSeconds, HasRemoteOperation(apiKey, hostUrl), retry.Transport);
            }
            if (pending.Id <= 0)
            {
                // Immediate /link/unlock links have no remote delayed job to renew.
                ForgetPending(key);
                return Unrestrict(apiKey, hostUrl, progress, cancel);
            }
            lock (PendingLinks) pending.Download = "";
            SavePending(key, pending);
            lock (PendingLinks) PendingLinks[key] = pending;
            if (progress != null) progress("AllDebrid is refreshing the prepared file");
            var poll = PollPrepared(apiKey, hostUrl, cancel);
            if (poll.Kind == PreparedPollKind.Ready) return CompletePrepared(apiKey, hostUrl, poll.Download);
            if (poll.Kind == PreparedPollKind.Rejected || poll.Kind == PreparedPollKind.Terminal)
                throw poll.Transport ?? AmbiguousCreate(hostUrl);
            throw new ProviderPreparationWaitException(UnlockProviders.AllDebridId, hostUrl,
                poll.ProviderState, Math.Max(PollDelayMilliseconds / 1000, poll.RetryAfterSeconds),
                HasRemoteOperation(apiKey, hostUrl), poll.Transport);
        }

        static Dictionary<string, object> Post(string path, string form, string key, string hostUrl, Func<bool> cancel = null)
        {
            if (PostImpl != null) return PostImpl(path, form, key, hostUrl);
            if (cancel != null && cancel()) throw new OperationCanceledException();
            string json;
            try { json = NetHttp.PostForm(Api + path, form, 60000, null, key.Trim(), null, cancel); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw DebridResolutionError.FromTransport("AllDebrid", hostUrl, ex); }
            var root = Parse(json);
            if (Text(root, "status") != "success") throw DebridResolutionError.FromResponse("AllDebrid", hostUrl, json);
            var data = Object(root, "data");
            if (data == null) throw new Exception("AllDebrid returned an invalid file response");
            return data;
        }

        static void Wait(int milliseconds, Func<bool> cancel)
        {
            for (int remaining = milliseconds; remaining > 0; remaining -= Math.Min(100, remaining))
            { if (cancel != null && cancel()) throw new OperationCanceledException(); Thread.Sleep(Math.Min(100, remaining)); }
        }

        static string PendingKey(string apiKey, string hostUrl)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(apiKey.Trim() + "\n" + hostUrl)));
        }
        static string PendingPath(string key)
        { return Path.Combine(AppSettings.DataDir, "alldebrid-pending", key.Replace("-", "") + ".txt"); }
        static Pending LookupPending(string key)
        {
            Pending pending;lock (PendingLinks) PendingLinks.TryGetValue(key, out pending);
            if (pending != null && pending.Until <= DateTime.UtcNow) pending = null;
            if (pending == null) pending = LoadPending(key);return pending;
        }
        static Pending LoadPending(string key)
        {
            try
            {
                string path = PendingPath(key);if (!File.Exists(path)) return null;
                if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Invalid AllDebrid pending receipt");
                string[] lines = File.ReadAllLines(path);long id, ticks;
                if ((lines.Length < 2 || lines.Length > 4) || !long.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id < 0 ||
                    !long.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) || ticks <= 0 ||
                    ticks > DateTime.UtcNow.AddDays(30).Ticks) throw new InvalidDataException("Invalid AllDebrid pending receipt");
                string state = lines.Length >= 3 ? lines[2] : (id == 0 ? "intent" : "ready");
                bool intent = state == "intent" && id == 0;
                bool ambiguous = state == "ambiguous";
                DateTime retryAt = DateTime.MinValue;
                if (state == "retry")
                {
                    long retryTicks;
                    if (lines.Length != 4 || !long.TryParse(lines[3], NumberStyles.None, CultureInfo.InvariantCulture, out retryTicks) ||
                        retryTicks < DateTime.MinValue.Ticks || retryTicks > DateTime.UtcNow.AddDays(30).Ticks)
                        throw new InvalidDataException("Invalid AllDebrid retry receipt");
                    retryAt = new DateTime(retryTicks, DateTimeKind.Utc);
                }
                string download = "";
                if (state == "ready" && lines.Length == 4)
                {
                    try { download = Encoding.UTF8.GetString(Convert.FromBase64String(lines[3])); }
                    catch (FormatException) { throw new InvalidDataException("Invalid AllDebrid ready receipt"); }
                    if (!IsHttp(download)) throw new InvalidDataException("Invalid AllDebrid ready receipt");
                }
                // The old two/three-line format did not retain direct ready URLs.
                // Re-resolve those safely; delayed IDs and ambiguous intents remain durable.
                if (state == "ready" && id == 0 && string.IsNullOrEmpty(download)) return null;
                var pending = new Pending { Id = id, Until = new DateTime(ticks, DateTimeKind.Utc), CreateIntent = intent,
                    Ambiguous = ambiguous, RetryCreate = state == "retry", RetryAt = retryAt, Download = download };
                lock (PendingLinks) { if (PendingLinks.Count >= 256) PendingLinks.Clear(); PendingLinks[key] = pending; }return pending;
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        internal static Action<string, string> SavePendingImpl;
        static void SavePending(string key, Pending pending)
        {
            string state = pending.Ambiguous ? "ambiguous" : pending.RetryCreate ? "retry" : pending.CreateIntent ? "intent" : "ready";
            string value = pending.Id.ToString(CultureInfo.InvariantCulture) + "\n" + pending.Until.Ticks.ToString(CultureInfo.InvariantCulture) + "\n" + state;
            if (pending.RetryCreate) value += "\n" + pending.RetryAt.Ticks.ToString(CultureInfo.InvariantCulture);
            else if (IsHttp(pending.Download)) value += "\n" + Convert.ToBase64String(Encoding.UTF8.GetBytes(pending.Download));
            if (SavePendingImpl != null) { SavePendingImpl(PendingPath(key), value); return; }
            string path = PendingPath(key), dir = Path.GetDirectoryName(path);
            Directory.CreateDirectory(dir);
            AtomicFile.WriteText(path, value);
        }

        static DebridResolutionError AmbiguousCreate(string hostUrl)
        { return DebridResolutionError.AmbiguousCreate("AllDebrid", hostUrl, "AMBIGUOUS_CREATE",
            "A previous unlock request has no confirmed response. Check AllDebrid before trying this source again."); }
        static void ForgetPending(string key)
        { lock (PendingLinks) PendingLinks.Remove(key);try { File.Delete(PendingPath(key)); } catch { } }

        public static string ProbeUser(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return "ERROR: AllDebrid API key missing";
            try
            {
                string json = NetHttp.GetStringDirect(Api + "/user", 6000, null, apiKey.Trim());
                var root = Parse(json);
                if (Text(root, "status") != "success") return "ERROR: " + DebridResolutionError.FromResponse("AllDebrid", Api, json).Message;
                var user = Object(Object(root, "data"), "user");
                object flag;
                if (user == null || !user.TryGetValue("isPremium", out flag) || !(flag is bool))
                    return "ERROR: AllDebrid account status unavailable";
                bool premium = (bool)flag, trial = user.TryGetValue("isTrial", out flag) && Equals(flag, true);
                return (premium || trial ? "OK " : "FREE: ") + "@" + Text(user, "username") +
                    (premium ? " (premium)" : trial ? " (trial)" : " (free)");
            }
            catch (Exception ex) { return "ERROR: " + DebridResolutionError.FromTransport("AllDebrid", Api, ex).Message; }
        }

        internal static Dictionary<string, object> Parse(string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 2 * 1024 * 1024) throw new FormatException("Invalid provider response size");
            var root = PackageSourceJson.Parse(json) as Dictionary<string, object>;
            if (root == null) throw new FormatException("Invalid provider response");
            return root;
        }

        internal static Dictionary<string, object> Object(Dictionary<string, object> value, string key)
        {
            object item;
            return value != null && value.TryGetValue(key, out item) ? item as Dictionary<string, object> : null;
        }

        internal static string Text(Dictionary<string, object> value, string key)
        {
            object item;
            return value != null && value.TryGetValue(key, out item) ? Convert.ToString(item, System.Globalization.CultureInfo.InvariantCulture) : "";
        }

        internal static bool IsHttp(string value)
        {
            Uri uri;
            return !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri) &&
                string.IsNullOrEmpty(uri.UserInfo) && (uri.Scheme == "http" || uri.Scheme == "https");
        }
    }
}
