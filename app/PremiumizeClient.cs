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

        // ---- Magnets: Premiumize fetches the swarm; the PS4 only downloads finished files ----
        // Written from the published API reference; not yet exercised against a live account.

        internal sealed class MagnetFile
        {
            internal string Path = "", Link = "";
            internal long Size;
        }

        internal sealed class Transfer
        {
            internal string Id = "", Name = "", Status = "", Message = "";
            internal int Progress;
            // Status values: queued, running, finished, seeding, error.
            internal bool Ready { get { return Status == "finished" || Status == "seeding"; } }
            internal bool Failed { get { return Status == "error"; } }
        }

        /// <summary>True when Premiumize already holds this torrent, so its files download at once.</summary>
        internal static bool IsCached(string apiKey, string magnet, Func<bool> cancel)
        {
            var root = MagnetCall("/cache/check", "items%5B%5D=" + Uri.EscapeDataString(magnet), apiKey, cancel);
            var answers = Value(root, "response") as System.Collections.IList;
            return answers != null && answers.Count > 0 && answers[0] is bool && (bool)answers[0];
        }

        /// <summary>Every file of a cached or finished torrent, with its folder path and direct link.</summary>
        internal static List<MagnetFile> DirectFiles(string apiKey, string magnet, Func<bool> cancel)
        {
            var root = MagnetCall("/transfer/directdl", "src=" + Uri.EscapeDataString(magnet), apiKey, cancel);
            var result = new List<MagnetFile>();
            var content = Value(root, "content") as System.Collections.IList;
            if (content != null)
                foreach (object item in content)
                {
                    var file = item as Dictionary<string, object>;
                    string link = AllDebridClient.Text(file, "link").Trim();
                    if (!AllDebridClient.IsHttp(link)) continue;
                    long size;
                    long.TryParse(AllDebridClient.Text(file, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                    result.Add(new MagnetFile { Path = AllDebridClient.Text(file, "path"), Link = link, Size = Math.Max(0, size) });
                    if (result.Count >= 2000) break;
                }
            return result;
        }

        /// <summary>The account's transfer for this torrent, or null. A transfer that failed is
        /// skipped, so queueing the magnet again starts a fresh one.</summary>
        internal static Transfer FindTransfer(string apiKey, string infoHash, string displayName, string id, Func<bool> cancel)
        {
            var root = MagnetCall("/transfer/list", null, apiKey, cancel);
            var transfers = Value(root, "transfers") as System.Collections.IList;
            if (transfers == null) return null;
            foreach (object item in transfers)
            {
                var row = item as Dictionary<string, object>;
                if (row == null) continue;
                var transfer = new Transfer
                {
                    Id = AllDebridClient.Text(row, "id"), Name = AllDebridClient.Text(row, "name"),
                    Status = AllDebridClient.Text(row, "status").ToLowerInvariant(), Message = AllDebridClient.Text(row, "message")
                };
                double progress;
                if (double.TryParse(AllDebridClient.Text(row, "progress"), NumberStyles.Float, CultureInfo.InvariantCulture, out progress))
                    transfer.Progress = (int)Math.Max(0, Math.Min(100, progress * 100));
                bool match = id != null ? transfer.Id == id
                    // src is deprecated but still names the magnet when present; the name is the fallback.
                    : AllDebridClient.Text(row, "src").IndexOf(infoHash, StringComparison.OrdinalIgnoreCase) >= 0 ||
                      (displayName.Length > 0 && string.Equals(transfer.Name, displayName, StringComparison.Ordinal));
                if (match && (id != null || !transfer.Failed)) return transfer;
            }
            return null;
        }

        internal static string CreateTransfer(string apiKey, string magnet, Func<bool> cancel)
        {
            var root = MagnetCall("/transfer/create", "src=" + Uri.EscapeDataString(magnet), apiKey, cancel);
            string id = AllDebridClient.Text(root, "id");
            if (string.IsNullOrEmpty(id) || id.Length > 64) throw new Exception("Premiumize did not return a transfer ID");
            return id;
        }

        static Dictionary<string, object> MagnetCall(string path, string form, string apiKey, Func<bool> cancel)
        {
            if (string.IsNullOrWhiteSpace(apiKey)) throw new Exception("Connect Premiumize in Connections");
            if (cancel != null && cancel()) throw new OperationCanceledException();
            string json;
            try
            {
                json = form == null ? NetHttp.GetString(Api + path, 30000, null, apiKey.Trim(), null, cancel)
                    : NetHttp.PostForm(Api + path, form, 60000, null, apiKey.Trim(), null, cancel);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw DebridResolutionError.FromTransport("Premiumize", Api + path, ex); }
            var root = AllDebridClient.Parse(json);
            if (AllDebridClient.Text(root, "status") != "success") throw DebridResolutionError.FromResponse("Premiumize", Api + path, json);
            return root;
        }

        static object Value(Dictionary<string, object> row, string key)
        {
            object item;
            return row != null && row.TryGetValue(key, out item) ? item : null;
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
