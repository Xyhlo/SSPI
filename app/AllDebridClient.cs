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
        sealed class Pending { internal long Id; internal DateTime Until; internal string Download; }
        static readonly Dictionary<string, Pending> PendingLinks = new Dictionary<string, Pending>();
        internal enum PreparedPollKind { Ready, Preparing, Rejected, Terminal, Transient }
        internal sealed class PreparedPollResult
        {
            internal PreparedPollKind Kind;
            internal string Download = "";
            internal string ProviderState = "";
            internal string Error = "";
            internal Exception Transport;
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
                throw new Exception(result.Error);
            var elapsed = Stopwatch.StartNew();
            int transient = 0;
            while (elapsed.Elapsed < PreparationDeadline)
            {
                if (progress != null) progress("AllDebrid is preparing the file");
                Wait(PollDelayMilliseconds, cancel);
                result = PollPrepared(apiKey, hostUrl, cancel);
                if (result.Kind == PreparedPollKind.Ready) return CompletePrepared(apiKey, hostUrl, result.Download);
                if (result.Kind == PreparedPollKind.Terminal || result.Kind == PreparedPollKind.Rejected)
                    throw new Exception(string.IsNullOrEmpty(result.Error) ? "AllDebrid could not prepare this file. Retry or choose another mirror." : result.Error);
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
            if (pending != null) return new PreparedPollResult { Kind = IsHttp(pending.Download) ? PreparedPollKind.Ready : PreparedPollKind.Preparing, Download = pending.Download ?? "", ProviderState = "delayed" };
            // Never retry this create/unlock request automatically: a transport
            // timeout can mean the provider accepted it without returning the ID.
            var data = Post("/link/unlock", "link=" + Uri.EscapeDataString(hostUrl), apiKey, hostUrl);
            if (cancel != null && cancel()) throw new OperationCanceledException();
            string download = Text(data, "link");
            if (IsHttp(download))
            {
                pending = new Pending { Until = DateTime.UtcNow.Add(PreparationDeadline), Download = Remember(download) };
                lock (PendingLinks) { if (PendingLinks.Count >= 256) PendingLinks.Clear(); PendingLinks[key] = pending; }
                return new PreparedPollResult { Kind = PreparedPollKind.Ready, Download = pending.Download };
            }
            long delayed;
            if (!long.TryParse(Text(data, "delayed"), NumberStyles.None, CultureInfo.InvariantCulture, out delayed) || delayed <= 0)
                return new PreparedPollResult { Kind = PreparedPollKind.Rejected, Error = "AllDebrid returned no file link. Choose a file mirror instead of a folder or stream." };
            pending = new Pending { Id = delayed, Until = DateTime.UtcNow.Add(PreparationDeadline) };
            SavePending(key, pending);
            lock (PendingLinks) { if (PendingLinks.Count >= 256) PendingLinks.Clear(); PendingLinks[key] = pending; }
            return new PreparedPollResult { Kind = PreparedPollKind.Preparing, ProviderState = "delayed" };
        }

        internal static PreparedPollResult PollPrepared(string apiKey, string hostUrl, Func<bool> cancel)
        {
            Validate(apiKey, hostUrl, cancel);
            string key = PendingKey(apiKey, hostUrl);Pending pending = LookupPending(key);
            if (pending == null) return new PreparedPollResult { Kind = PreparedPollKind.Rejected, Error = "No retained AllDebrid delayed ID; resolve the package again" };
            if (IsHttp(pending.Download)) return new PreparedPollResult { Kind = PreparedPollKind.Ready, Download = pending.Download };
            Dictionary<string, object> data;
            try { data = Post("/link/delayed", "id=" + pending.Id.ToString(CultureInfo.InvariantCulture), apiKey, hostUrl); }
            catch (DebridResolutionError ex)
            {
                if (string.IsNullOrEmpty(ex.ProviderCode)) return new PreparedPollResult { Kind = PreparedPollKind.Transient, Transport = ex, Error = ex.Message };
                return new PreparedPollResult { Kind = PreparedPollKind.Rejected, Error = ex.Message };
            }
            catch (Exception ex) { return new PreparedPollResult { Kind = PreparedPollKind.Transient, Transport = ex, Error = ex.Message }; }
            if (cancel != null && cancel()) throw new OperationCanceledException();
            string download = Text(data, "link");
            if (IsHttp(download))
            {
                // Keep the ready link in memory until the queue claims it. The
                // next preflight must not park it again or repeat /link/unlock.
                lock (PendingLinks) pending.Download = Remember(download);
                return new PreparedPollResult { Kind = PreparedPollKind.Ready, Download = pending.Download, ProviderState = Text(data, "status") };
            }
            string state = Text(data, "status");
            if (state == "3") { ForgetPending(key); return new PreparedPollResult { Kind = PreparedPollKind.Terminal, ProviderState = state, Error = "AllDebrid could not prepare this file. Retry or choose another mirror." }; }
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
        static string CompletePrepared(string apiKey, string hostUrl, string download)
        { ForgetPending(PendingKey(apiKey, hostUrl));return Remember(download); }

        static Dictionary<string, object> Post(string path, string form, string key, string hostUrl)
        {
            if (PostImpl != null) return PostImpl(path, form, key, hostUrl);
            string json;
            try { json = NetHttp.PostForm(Api + path, form, 60000, null, key.Trim()); }
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
                string path = PendingPath(key);if (!File.Exists(path) || new FileInfo(path).Length > 256) return null;
                string[] lines = File.ReadAllLines(path);long id, ticks;
                if (lines.Length != 2 || !long.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0 ||
                    !long.TryParse(lines[1], NumberStyles.None, CultureInfo.InvariantCulture, out ticks) || ticks <= DateTime.UtcNow.Ticks ||
                    ticks > DateTime.UtcNow.Add(PreparationDeadline).Ticks) return null;
                var pending = new Pending { Id = id, Until = new DateTime(ticks, DateTimeKind.Utc) };
                lock (PendingLinks) { if (PendingLinks.Count >= 256) PendingLinks.Clear(); PendingLinks[key] = pending; }return pending;
            }
            catch { return null; }
        }
        static void SavePending(string key, Pending pending)
        {
            try
            {
                string path = PendingPath(key), dir = Path.GetDirectoryName(path);Directory.CreateDirectory(dir);
                AtomicFile.WriteText(path, pending.Id.ToString(CultureInfo.InvariantCulture) + "\n" + pending.Until.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            catch { }
        }
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
