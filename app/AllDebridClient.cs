using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace Orbis
{
    internal static class AllDebridClient
    {
        const string Api = "https://api.alldebrid.com/v4";

        public static string Unrestrict(string apiKey, string hostUrl, Action<string> progress = null, Func<bool> cancel = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new Exception("Connect AllDebrid in Connections");
            if (!IsHttp(hostUrl)) throw new Exception("AllDebrid: Invalid host URL");
            if (cancel != null && cancel()) throw new OperationCanceledException();
            var data = Post("/link/unlock", "link=" + Uri.EscapeDataString(hostUrl), apiKey, hostUrl);
            string download = Text(data, "link");
            if (cancel != null && cancel()) throw new OperationCanceledException();
            // AllDebrid recommends eight connections per file; the shared scheduler applies its own lower budget.
            if (IsHttp(download)) { DownloadTransferSettings.RememberProviderLimit(download.Trim(), Math.Min(8, DownloadTransferSettings.MaxRangeCount)); return download.Trim(); }
            long delayed;
            if (!long.TryParse(Text(data, "delayed"), out delayed) || delayed <= 0)
                throw new Exception("AllDebrid returned no file link. Choose a file mirror instead of a folder or stream.");

            var elapsed = Stopwatch.StartNew();
            while (elapsed.ElapsedMilliseconds < 10 * 60 * 1000)
            {
                if (progress != null) progress("AllDebrid is preparing the file");
                // The provider requires at least five seconds between delayed-link polls.
                for (int i = 0; i < 50; i++)
                {
                    if (cancel != null && cancel()) throw new OperationCanceledException();
                    Thread.Sleep(100);
                }
                data = Post("/link/delayed", "id=" + delayed, apiKey, hostUrl);
                if (cancel != null && cancel()) throw new OperationCanceledException();
                download = Text(data, "link");
                if (IsHttp(download)) { DownloadTransferSettings.RememberProviderLimit(download.Trim(), Math.Min(8, DownloadTransferSettings.MaxRangeCount)); return download.Trim(); }
                if (Text(data, "status") == "3")
                    throw new Exception("AllDebrid could not prepare this file. Retry or choose another mirror.");
            }
            throw new Exception("AllDebrid is still preparing the file. Retry later.");
        }

        static Dictionary<string, object> Post(string path, string form, string key, string hostUrl)
        {
            string json;
            try { json = NetHttp.PostForm(Api + path, form, 60000, null, key.Trim()); }
            catch (Exception ex) { throw DebridResolutionError.FromTransport("AllDebrid", hostUrl, ex); }
            var root = Parse(json);
            if (Text(root, "status") != "success") throw DebridResolutionError.FromResponse("AllDebrid", hostUrl, json);
            var data = Object(root, "data");
            if (data == null) throw new Exception("AllDebrid returned an invalid file response");
            return data;
        }

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
