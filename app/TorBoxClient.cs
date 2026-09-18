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
        sealed class Pending { public string Id, FileId; public DateTime Until; }
        static readonly Dictionary<string, Pending> PendingDownloads = new Dictionary<string, Pending>();

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
            internal string StatusText = "";
            internal string Metric = "";
            internal string ProviderState = "";
            internal string Error = "";
            internal string RawJson = "";
            internal bool Visible;
            internal Exception Transport;
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

            string json = null;
            string key = PendingKey(token, hostUrl);
            Pending pending = LookupPending(key);
            if (cancel != null && cancel()) throw new OperationCanceledException();
            if (pending == null)
            {
                json = CreateWebDownload(token.Trim(), hostUrl, progress, cancel);
                var created = Object(Value(Response(json), "data"));
                string id = First(created, "webdownload_id", "webdownloadId", "id");
                if (!ValidId(id)) throw new Exception("TorBox returned an invalid web download ID");
                pending = new Pending { Id = id, FileId = ReadyFile(created), Until = DateTime.UtcNow.AddHours(6) };
                SavePending(key, pending);
                lock (PendingDownloads) { if (PendingDownloads.Count >= 256) PendingDownloads.Clear(); PendingDownloads[key] = pending; }
            }

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
                    Wait(PollDelayMs(poll), cancel);
                    continue;
                }
                transient = 0;
                if (outcome.Kind == PreparedPollKind.Rejected)
                    throw DebridResolutionError.FromResponse("TorBox", hostUrl, outcome.RawJson);
                if (outcome.Kind == PreparedPollKind.Terminal) throw new Exception(outcome.Error);
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
            if (cancel != null && cancel()) throw new OperationCanceledException();
            string url = Api + "/webdl/requestdl?token=" + Uri.EscapeDataString(token.Trim()) +
                "&web_id=" + Uri.EscapeDataString(pending.Id) + "&file_id=" + Uri.EscapeDataString(fileId) + "&zip_link=false";
            try
            {
                json = NetHttp.GetString(url, 20000);
            }
            catch (Exception ex)
            {
                // Transport failure (timeout/5xx/429): keep the prepared IDs.
                throw DebridResolutionError.FromTransport("TorBox", hostUrl, ex);
            }

            string raw = (json ?? "").Trim().Trim('"');
            if (cancel != null && cancel()) throw new OperationCanceledException();
            if (IsHttp(raw))
            {
                DownloadTransferSettings.RememberProviderLimit(raw.Trim(), 4);
                return raw.Trim();
            }
            var linkResponse = Response(json);
            if (!Flag(linkResponse, "success"))
            {
                // Invalid/expired tokens do not invalidate the prepared download.
                if (MissingJob(Text(linkResponse, "error"))) ForgetPending(key);
                throw DebridResolutionError.FromResponse("TorBox", hostUrl, json);
            }
            string download = Text(linkResponse, "data");
            if (!IsHttp(download)) throw new Exception("TorBox returned no download URL");
            if (cancel != null && cancel()) throw new OperationCanceledException();
            DownloadTransferSettings.RememberProviderLimit(download.Trim(), 4);
            return download.Trim();
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
            double progress = Number(job, "progress"), speed = Number(job, "download_speed"), eta = Number(job, "eta");
            if (progress >= 0 && progress <= 1) parts.Add((progress * 100).ToString("0", CultureInfo.InvariantCulture) + "%");
            if (speed > 0) parts.Add((speed / 1000000).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s");
            if (eta > 0 && eta < 86400) parts.Add("ETA " + TimeSpan.FromSeconds(eta).ToString(@"h\:mm\:ss"));
            return string.Join(" · ", parts.ToArray());
        }

        static string PendingKey(string token, string hostUrl)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(token.Trim() + "\n" + hostUrl)));
        }

        static Pending LookupPending(string key)
        {
            Pending pending;
            lock (PendingDownloads) { PendingDownloads.TryGetValue(key, out pending); }
            if (pending != null && pending.Until < DateTime.UtcNow) pending = null;
            if (pending == null) pending = LoadPending(key);
            return pending;
        }

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
            string list;
            try { list = NetHttp.GetString(Api + "/webdl/mylist?id=" + Uri.EscapeDataString(pending.Id) + "&bypass_cache=true", 15000, null, token.Trim()); }
            catch (Exception ex)
            {
                result.Kind = PreparedPollKind.Transient;
                result.Transport = ex;
                result.Error = ex.Message;
                return result;
            }
            result.RawJson = list;
            var response = Response(list);
            if (!Flag(response, "success"))
            {
                if (MissingJob(Text(response, "error"))) ForgetPending(key);
                result.Kind = PreparedPollKind.Rejected;
                result.Error = Text(response, "error") ?? "the provider rejected the list request";
                return result;
            }
            Dictionary<string, object> job;
            bool visible = TryFindJob(Value(response, "data"), pending.Id, out job);
            result.Visible = visible;
            if (visible)
            {
                // Readiness is checked before the state wording, matching the
                // original resolve loop: a listed file id wins over any state text.
                string fileId = ReadyFile(job);
                if (!string.IsNullOrEmpty(fileId))
                {
                    pending.FileId = fileId;
                    SavePending(key, pending);
                    result.Kind = PreparedPollKind.Ready;
                    result.FileId = fileId;
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
            else
            {
                result.StatusText = "TorBox preparing";
            }
            result.Kind = PreparedPollKind.Preparing;
            return result;
        }

        /// <summary>
        /// Queue preflight before the blocking resolve. Guarantees a prepared
        /// download exists (creating at most one cloud job on first sight),
        /// performs at most one list poll, and returns Ready when the provider
        /// already published a file. A Ready result here lets the normal resolve
        /// skip its loop entirely, so no second provider job is ever created.
        /// </summary>
        internal static PreparedPollResult TryParkOrPrepare(string token, string hostUrl, Func<bool> cancel)
        {
            if (string.IsNullOrWhiteSpace(token) || string.IsNullOrEmpty(hostUrl))
                return new PreparedPollResult { Kind = PreparedPollKind.Rejected, Error = "TorBox token missing" };
            string key = PendingKey(token, hostUrl);
            Pending pending = LookupPending(key);
            if (pending != null && !string.IsNullOrEmpty(pending.FileId))
                return new PreparedPollResult { Kind = PreparedPollKind.Ready, FileId = pending.FileId };
            if (pending == null)
            {
                string json = CreateWebDownload(token.Trim(), hostUrl, null, cancel);
                var created = Object(Value(Response(json), "data"));
                string id = First(created, "webdownload_id", "webdownloadId", "id");
                if (!ValidId(id)) throw new Exception("TorBox returned an invalid web download ID");
                pending = new Pending { Id = id, FileId = ReadyFile(created), Until = DateTime.UtcNow.AddHours(6) };
                SavePending(key, pending);
                lock (PendingDownloads) { if (PendingDownloads.Count >= 256) PendingDownloads.Clear(); PendingDownloads[key] = pending; }
                if (!string.IsNullOrEmpty(pending.FileId))
                    return new PreparedPollResult { Kind = PreparedPollKind.Ready, FileId = pending.FileId };
            }
            return PollPrepared(token, hostUrl, cancel);
        }
        static string CreateWebDownload(string token, string hostUrl, Action<string> progress, Func<bool> cancel)
        {
            for (int attempt = 0; ; attempt++)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                try
                {
                    string json;
                    try { json = NetHttp.PostForm(Api + "/webdl/createwebdownload",
                        "link=" + Uri.EscapeDataString(hostUrl) + "&as_queued=false", 30000, null, token); }
                    catch (Exception ex) { throw DebridResolutionError.FromTransport("TorBox", hostUrl, ex); }
                    if (!Flag(Response(json), "success")) throw DebridResolutionError.FromResponse("TorBox", hostUrl, json);
                    return json;
                }
                catch (DebridResolutionError ex)
                {
                    // AUTH_ERROR explicitly means the server failed to verify the key;
                    // unlike a transport timeout, it confirms no download was created.
                    if (attempt != 0 || ex.ProviderCode != "AUTH_ERROR") throw;
                    if (progress != null) progress("TorBox token verification unavailable · retrying once");
                    Wait(1500, cancel);
                }
            }
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
        static string PendingPath(string key)
        { return Path.Combine(AppSettings.DataDir, "torbox-pending", key.Replace("-", "") + ".txt"); }
        static Pending LoadPending(string key)
        {
            try {
                string path = PendingPath(key);
                if (!File.Exists(path) || new FileInfo(path).Length > 1024) return null;
                string[] lines = File.ReadAllLines(path); long ticks;
                if (lines.Length != 3 || !ValidId(lines[0]) || (lines[1] != "" && !ValidId(lines[1])) ||
                    !long.TryParse(lines[2], out ticks) || ticks <= DateTime.UtcNow.Ticks || ticks > DateTime.UtcNow.AddHours(6).Ticks) return null;
                var pending = new Pending { Id = lines[0], FileId = lines[1], Until = new DateTime(ticks, DateTimeKind.Utc) };
                lock (PendingDownloads) { if (PendingDownloads.Count >= 256) PendingDownloads.Clear(); PendingDownloads[key] = pending; }
                return pending;
            } catch { return null; }
        }
        static void SavePending(string key, Pending pending)
        {
            try {
                string path = PendingPath(key), dir = Path.GetDirectoryName(path);
                Directory.CreateDirectory(dir);
                AtomicFile.WriteText(path, pending.Id + "\n" + (pending.FileId ?? "") + "\n" + pending.Until.Ticks.ToString(CultureInfo.InvariantCulture));
                var files = new DirectoryInfo(dir).GetFiles("*.txt");
                if (files.Length > 256) { Array.Sort(files, (a,b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc)); for (int i=0; i<files.Length-256; i++) files[i].Delete(); }
            } catch { } // An unavailable cache must not stop link resolution.
        }
        static void ForgetPending(string key)
        {
            lock (PendingDownloads) PendingDownloads.Remove(key);
            try { File.Delete(PendingPath(key)); } catch { }
        }

        public static string ProbeUser(string token)
        {
            try
            {
                string json = NetHttp.GetString(Api + "/user/me", 6000, null, (token ?? "").Trim());
                var response = Response(json);
                if (!Flag(response, "success"))
                    return "ERROR: " + DebridResolutionError.FromResponse("TorBox", Api, json).Message;
                var data = Object(Value(response, "data"));
                string user = First(data, "email", "base_email", "auth_id") ?? "?";
                string plan = Text(data, "plan") ?? "?";
                int planId;
                if (!int.TryParse(plan, out planId) || planId < 0) return "ERROR: TorBox returned no recognized account plan";
                return (plan == "0" ? "FREE: " : "OK ") + "@" + user + " (plan " + plan + ")";
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
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
