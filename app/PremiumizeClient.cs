using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Orbis
{
    internal static class PremiumizeClient
    {
        internal const string Api = "https://www.premiumize.me/api";

        public static string Unrestrict(string apiKey, string hostUrl, Action<string> progress = null, Func<bool> cancel = null)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new Exception("Connect Premiumize in Connections");
            if (!AllDebridClient.IsHttp(hostUrl)) throw new Exception("Premiumize: Invalid host URL");
            if (cancel != null && cancel()) throw new OperationCanceledException();
            if (progress != null) progress("Premiumize is resolving the file");
            string json;
            try { json = NetHttp.PostForm(Api + "/transfer/directdl", "src=" + Uri.EscapeDataString(hostUrl), 60000, null, apiKey.Trim(), null, cancel); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw DebridResolutionError.FromTransport("Premiumize", hostUrl, ex); }
            var root = AllDebridClient.Parse(json);
            if (AllDebridClient.Text(root, "status") != "success") throw DebridResolutionError.FromResponse("Premiumize", hostUrl, json);
            object content;
            var files = root.TryGetValue("content", out content) ? content as System.Collections.IList : null;
            // Each source candidate names one file/volume. Never substitute an arbitrary folder member.
            if (files == null || files.Count != 1)
                throw new Exception("Premiumize returned multiple files or no file. Choose an individual file mirror.");
            string link = AllDebridClient.Text(files[0] as Dictionary<string, object>, "link");
            if (!AllDebridClient.IsHttp(link)) throw new Exception("Premiumize returned no valid file URL. Retry or choose another mirror.");
            // directdl supplies no per-file connection allowance; do not assume the underlying host permits ranges.
            DownloadTransferSettings.RememberProviderLimit(link.Trim(), 1);
            return link.Trim();
        }

        public static string ProbeUser(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) return "ERROR: Premiumize API key missing";
            try
            {
                string json = NetHttp.GetStringDirect(Api + "/account/info", 6000, null, apiKey.Trim());
                var root = AllDebridClient.Parse(json);
                if (AllDebridClient.Text(root, "status") != "success")
                    return "ERROR: " + DebridResolutionError.FromResponse("Premiumize", Api, json).Message;
                string account = AllDebridClient.Text(root, "customer_id");
                if (string.IsNullOrEmpty(account) || !root.ContainsKey("premium_until")) return "ERROR: Premiumize account status unavailable";
                string until = AllDebridClient.Text(root, "premium_until");
                long expiry;
                if (string.IsNullOrEmpty(until) || (long.TryParse(until, out expiry) && expiry <= 0))
                    return "FREE: Account " + account + " (free)";
                if (!long.TryParse(until, out expiry)) return "ERROR: Premiumize account status unavailable";
                // The API supplies an expiry, not a current server clock or active-plan flag.
                // An unset PS4 clock cannot establish whether this expiry is still in the future.
                return "VALID: API key verified; plan unverified (provider expiry " +
                    new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(expiry).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ")";
            }
            catch (Exception ex) { return "ERROR: " + DebridResolutionError.FromTransport("Premiumize", Api, ex).Message; }
        }
    }
}
