using System;
using System.Collections.Generic;
using System.Text;

namespace Orbis
{
    /// <summary>Real-Debrid REST: unrestrict host links with user API token.</summary>
    internal static class RealDebridClient
    {
        const string Api = "https://api.real-debrid.com/rest/1.0";
        internal static Action<int, Func<bool>> WaitImpl;
        internal const int TransientCreateRetries = 3;

        public static string Unrestrict(string token, string hostUrl, Func<bool> cancel = null, bool queueRetry = false)
        {
            return Unrestrict(token, hostUrl, AppSettings.RdLocationAuto, cancel, queueRetry);
        }

        public static string Unrestrict(string token, string hostUrl, string preferredLocation,
            Func<bool> cancel = null, bool queueRetry = false)
        {
            if (string.IsNullOrEmpty(token))
                throw new Exception("Real-Debrid token missing (Options > Settings)");
            if (string.IsNullOrEmpty(hostUrl))
                throw new Exception("Empty host URL");

            string body = "link=" + Uri.EscapeDataString(hostUrl);
            for (int attempt = 0; ; attempt++)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                string json;
                try { json = NetHttp.PostForm(Api + "/unrestrict/link", body, 60000, null, token.Trim(), null, cancel); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { throw DebridResolutionError.FromTransport("Real-Debrid", hostUrl, ex); }

                string err = JsonLite.GetString(json, "error");
                if (!string.IsNullOrEmpty(err))
                {
                    DebridResolutionError rejection = DebridResolutionError.FromResponse("Real-Debrid", hostUrl, json);
                    if (!rejection.IsTransient || queueRetry || attempt >= TransientCreateRetries) throw rejection;
                    int delay = rejection.RetryAfterSeconds > 0 ? rejection.RetryAfterSeconds : Math.Min(8, 1 << attempt);
                    Wait(Math.Min(24 * 60 * 60, delay) * 1000, cancel);
                    continue;
                }

                var downloads = DownloadCandidates(json);
                if (downloads.Count == 0)
                    throw new Exception("RD returned no download URL");
                string selected = SelectByLocation(downloads, preferredLocation);
                DownloadTransferSettings.RememberProviderLimit(selected, ConnectionLimit(json, selected));
                return selected;
            }
        }

        static void Wait(int milliseconds, Func<bool> cancel)
        {
            if (WaitImpl != null) { WaitImpl(milliseconds, cancel); return; }
            for (int remaining = milliseconds; remaining > 0; remaining -= Math.Min(100, remaining))
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                System.Threading.Thread.Sleep(Math.Min(100, remaining));
            }
        }

        internal static int ConnectionLimit(string json, string selected)
        {
            try
            {
                int limit = FindConnectionLimit(PackageSourceJson.Parse(json), selected, 1);
                return limit > 0 ? DownloadTransferSettings.ClampRangeCount(limit) : 1;
            }
            catch { return 1; }
        }

        static int FindConnectionLimit(object value, string selected, int inherited)
        {
            var row = value as Dictionary<string, object>;
            if (row != null)
            {
                object chunks, link, alternatives;
                int maximum = inherited, parsed;
                if (row.TryGetValue("chunks", out chunks))
                    maximum = int.TryParse(Convert.ToString(chunks), out parsed)
                        ? (parsed == 0 ? DownloadTransferSettings.MaxRangeCount : Math.Max(1, parsed)) : 1;
                if (row.TryGetValue("download", out link) && string.Equals(link as string, selected, StringComparison.Ordinal)) return maximum;
                if (row.TryGetValue("alternative", out alternatives)) return FindConnectionLimit(alternatives, selected, maximum);
            }
            var items = value as System.Collections.IList;
            if (items != null)
                foreach (object item in items)
                {
                    int found = FindConnectionLimit(item, selected, inherited);
                    if (found > 0) return found;
                }
            return 0;
        }

        // RD's documented response contains one `download` URL. Keep support for
        // repeated download fields / a downloads array so a regional preference
        // can be honored if RD ever supplies alternatives. No extra geo request is
        // made and the unrelated `remote` API flag is deliberately not abused.
        static List<string> DownloadCandidates(string json)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddHttp(result, seen, JsonLite.GetString(json, "download"));
            CollectKeyStrings(json, "download", result, seen, false);
            CollectKeyStrings(json, "downloads", result, seen, true);
            return result;
        }

        static void CollectKeyStrings(string json, string key, List<string> output,
            HashSet<string> seen, bool array)
        {
            if (string.IsNullOrEmpty(json)) return;
            string marker = "\"" + key + "\"";
            int at = 0;
            while ((at = json.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
            {
                int colon = json.IndexOf(':', at + marker.Length);
                if (colon < 0) return;
                int p = colon + 1;
                while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
                if (!array)
                {
                    string value;
                    if (TryReadJsonString(json, ref p, out value)) AddHttp(output, seen, value);
                }
                else if (p < json.Length && json[p] == '[')
                {
                    int depth = 1;
                    p++;
                    while (p < json.Length && depth > 0)
                    {
                        if (json[p] == '[') { depth++; p++; continue; }
                        if (json[p] == ']') { depth--; p++; continue; }
                        string value;
                        if (TryReadJsonString(json, ref p, out value)) AddHttp(output, seen, value);
                        else p++;
                    }
                }
                at = Math.Max(at + marker.Length, p);
            }
        }

        static bool TryReadJsonString(string json, ref int p, out string value)
        {
            value = null;
            while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
            if (p >= json.Length || json[p] != '"') return false;
            p++;
            var sb = new StringBuilder();
            while (p < json.Length)
            {
                char c = json[p++];
                if (c == '"') { value = sb.ToString(); return true; }
                if (c != '\\' || p >= json.Length) { sb.Append(c); continue; }
                char e = json[p++];
                if (e == 'n') sb.Append('\n');
                else if (e == 'r') sb.Append('\r');
                else if (e == 't') sb.Append('\t');
                else if (e == 'u' && p + 3 < json.Length)
                {
                    int code;
                    if (int.TryParse(json.Substring(p, 4),
                        System.Globalization.NumberStyles.HexNumber, null, out code))
                        sb.Append((char)code);
                    p += 4;
                }
                else sb.Append(e);
            }
            return false;
        }

        static void AddHttp(List<string> output, HashSet<string> seen, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            value = value.Trim();
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return;
            if (seen.Add(value)) output.Add(value);
        }

        static string SelectByLocation(List<string> downloads, string preference)
        {
            preference = AppSettings.NormalizeRdLocation(preference);
            if (preference == AppSettings.RdLocationAuto || downloads.Count < 2)
                return downloads[0];

            int wanted = preference == AppSettings.RdLocationUs ? 1 : -1;
            string best = downloads[0];
            int bestScore = LocationHint(best) * wanted;
            foreach (string candidate in downloads)
            {
                int score = LocationHint(candidate) * wanted;
                if (score > bestScore) { bestScore = score; best = candidate; }
            }
            return best;
        }

        // Scores only explicit CDN hostname location tokens; DNS/IP geolocation
        // would add latency, privacy leakage and unreliable console dependencies.
        static int LocationHint(string url)
        {
            string host;
            try { host = new Uri(url).Host.ToLowerInvariant(); }
            catch { return 0; }
            string[] tokens = host.Split(new[] { '.', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string token in tokens)
            {
                if (TokenIs(token, "lax", "sfo", "sea", "dal", "dfw", "chi", "ord",
                    "nyc", "iad", "mia", "atl", "us")) return 1;
                if (TokenIs(token, "ams", "fra", "lhr", "lon", "par", "rbx", "waw",
                    "hel", "sto", "mad", "eu")) return -1;
            }
            return 0;
        }

        static bool TokenIs(string token, params string[] prefixes)
        {
            foreach (string prefix in prefixes)
            {
                if (token == prefix) return true;
                if (token.StartsWith(prefix, StringComparison.Ordinal) && token.Length > prefix.Length)
                {
                    bool digits = true;
                    for (int i = prefix.Length; i < token.Length; i++)
                        if (!char.IsDigit(token[i])) { digits = false; break; }
                    if (digits) return true;
                }
            }
            return false;
        }

        // ---- Torrents: Real-Debrid fetches the swarm; the PS4 only downloads finished files ----

        internal sealed class TorrentFile
        {
            internal int Id;
            internal string Path = "";
            internal long Bytes;
            internal bool Selected;
        }

        internal sealed class Torrent
        {
            internal string Id = "", Hash = "", Filename = "", Status = "";
            internal int Progress, Seeders;
            internal long Speed, Bytes;
            internal readonly List<TorrentFile> Files = new List<TorrentFile>();
            internal readonly List<string> Links = new List<string>();
            internal bool Failed
            {
                get { return Status == "magnet_error" || Status == "error" || Status == "virus" || Status == "dead"; }
            }
        }

        /// <summary>The account's existing torrent for this info hash, or "" when none is usable.
        /// Reusing it keeps a retried or restarted magnet from adding a duplicate.</summary>
        internal static string FindTorrent(string token, string infoHash, Func<bool> cancel)
        {
            var rows = Call(token, Api + "/torrents?limit=100", null, cancel) as System.Collections.IList;
            if (rows == null) return "";
            foreach (object item in rows)
            {
                Torrent row = ParseTorrent(item as Dictionary<string, object>);
                if (row != null && !row.Failed && string.Equals(row.Hash, infoHash, StringComparison.OrdinalIgnoreCase))
                    return row.Id;
            }
            return "";
        }

        internal static string AddMagnet(string token, string magnet, Func<bool> cancel)
        {
            var row = Call(token, Api + "/torrents/addMagnet", "magnet=" + Uri.EscapeDataString(magnet), cancel) as Dictionary<string, object>;
            string id = Field(row, "id");
            if (!ValidTorrentId(id)) throw new Exception("Real-Debrid did not return a torrent ID");
            return id;
        }

        internal static Torrent TorrentInfo(string token, string id, Func<bool> cancel)
        {
            if (!ValidTorrentId(id)) throw new Exception("Invalid Real-Debrid torrent ID");
            Torrent torrent = ParseTorrent(Call(token, Api + "/torrents/info/" + id, null, cancel) as Dictionary<string, object>);
            if (torrent == null) throw new Exception("Real-Debrid returned no torrent details");
            return torrent;
        }

        /// <summary>files is "all" or comma-separated file IDs from TorrentInfo.</summary>
        internal static void SelectFiles(string token, string id, string files, Func<bool> cancel)
        {
            if (!ValidTorrentId(id)) throw new Exception("Invalid Real-Debrid torrent ID");
            Call(token, Api + "/torrents/selectFiles/" + id, "files=" + Uri.EscapeDataString(files), cancel);
        }

        static object Call(string token, string url, string form, Func<bool> cancel)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new Exception("Magnet links need Real-Debrid; connect it in Connections");
            if (cancel != null && cancel()) throw new OperationCanceledException();
            string json;
            try
            {
                json = form == null ? NetHttp.GetString(url, 20000, null, token.Trim(), null, cancel)
                    : NetHttp.PostForm(url, form, 30000, null, token.Trim(), null, cancel);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw DebridResolutionError.FromTransport("Real-Debrid", url, ex); }
            if (string.IsNullOrWhiteSpace(json)) return null;
            if (!string.IsNullOrEmpty(JsonLite.GetString(json, "error")))
                throw DebridResolutionError.FromResponse("Real-Debrid", url, json);
            return PackageSourceJson.Parse(json);
        }

        static Torrent ParseTorrent(Dictionary<string, object> row)
        {
            if (row == null || !ValidTorrentId(Field(row, "id"))) return null;
            var torrent = new Torrent
            {
                Id = Field(row, "id"), Hash = Field(row, "hash").ToLowerInvariant(),
                Filename = Field(row, "filename"), Status = Field(row, "status"),
                Progress = (int)Math.Max(0, Math.Min(100, Number(row, "progress"))),
                Seeders = (int)Math.Max(0, Math.Min(100000, Number(row, "seeders"))),
                Speed = Math.Max(0, Number(row, "speed")), Bytes = Math.Max(0, Number(row, "bytes"))
            };
            object value;
            if (row.TryGetValue("files", out value) && value is System.Collections.IList)
                foreach (object item in (System.Collections.IList)value)
                {
                    var file = item as Dictionary<string, object>;
                    if (file == null) continue;
                    torrent.Files.Add(new TorrentFile
                    {
                        Id = (int)Number(file, "id"), Path = Field(file, "path"),
                        Bytes = Math.Max(0, Number(file, "bytes")), Selected = Number(file, "selected") == 1
                    });
                }
            if (row.TryGetValue("links", out value) && value is System.Collections.IList)
                foreach (object link in (System.Collections.IList)value)
                    torrent.Links.Add(Convert.ToString(link, System.Globalization.CultureInfo.InvariantCulture) ?? "");
            return torrent;
        }

        static string Field(Dictionary<string, object> row, string name)
        {
            object value;
            return row != null && row.TryGetValue(name, out value) && value != null
                ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : "";
        }

        static long Number(Dictionary<string, object> row, string name)
        {
            string text = Field(row, name);
            if (text == "True" || text == "true") return 1;
            double parsed;
            return double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out parsed) ? (long)parsed : 0;
        }

        static bool ValidTorrentId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            foreach (char c in id) if (!char.IsLetterOrDigit(c)) return false;
            return true;
        }

        public static string UserInfo(string token)
        {
            return ProbeUser(token);
        }

        public static string ProbeUser(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return "REJECTED: Real-Debrid token missing; reconnect in Connections";
            try
            {
                string json = NetHttp.GetString(Api + "/user", 6000, null, token.Trim());
                string err = JsonLite.GetString(json, "error");
                if (!string.IsNullOrEmpty(err))
                    return ProbeFailure(DebridResolutionError.FromResponse("Real-Debrid", Api + "/user", json));
                string user = JsonLite.GetString(json, "username") ?? "?";
                string prem = JsonLite.GetString(json, "type") ?? "?";
                string exp = JsonLite.GetString(json, "expiration") ?? "";
                if (prem != "free" && prem != "premium") return "RETRY: Real-Debrid returned no recognized account tier";
                return (string.Equals(prem, "free", StringComparison.OrdinalIgnoreCase) ? "FREE: " : "OK ") + "@" + user + " (" + prem + ") " + exp;
            }
            catch (Exception ex)
            {
                return ProbeFailure(DebridResolutionError.FromTransport("Real-Debrid", Api + "/user", ex));
            }
        }

        internal static string ProbeFailure(Exception error)
        {
            var rejection = error as DebridResolutionError;
            if (rejection == null) return "RETRY: Real-Debrid account verification is unavailable; saved token kept";
            if (rejection.ProviderCode == "8" || rejection.ProviderCode == "12" || rejection.ProviderCode == "13")
                return "REJECTED: Real-Debrid rejected the saved token; reconnect in Connections (code " + rejection.ProviderCode + ")";
            if (rejection.IsRateLimited)
                return "RATE_LIMITED: Real-Debrid is busy; wait before retrying";
            if (rejection.NeedsAction)
                return "LIMITED: Real-Debrid account needs attention; check the connected account";
            return "RETRY: Real-Debrid account verification is unavailable; saved token kept";
        }
    }
}
