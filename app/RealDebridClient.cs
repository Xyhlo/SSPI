using System;
using System.Collections.Generic;
using System.Text;

namespace Orbis
{
    /// <summary>Real-Debrid REST: unrestrict host links with user API token.</summary>
    internal static class RealDebridClient
    {
        const string Api = "https://api.real-debrid.com/rest/1.0";

        public static string Unrestrict(string token, string hostUrl)
        {
            return Unrestrict(token, hostUrl, AppSettings.RdLocationAuto);
        }

        public static string Unrestrict(string token, string hostUrl, string preferredLocation)
        {
            if (string.IsNullOrEmpty(token))
                throw new Exception("Real-Debrid token missing (Options > Settings)");
            if (string.IsNullOrEmpty(hostUrl))
                throw new Exception("Empty host URL");

            string body = "link=" + Uri.EscapeDataString(hostUrl);
            string json;
            try
            {
                json = NetHttp.PostForm(Api + "/unrestrict/link", body, 60000, null, token.Trim());
            }
            catch (Exception ex)
            {
                throw new Exception("RD unrestrict failed: " + ex.Message);
            }

            string err = JsonLite.GetString(json, "error");
            if (!string.IsNullOrEmpty(err))
                throw new Exception("RD: " + err + " " + (JsonLite.GetString(json, "error_code") ?? ""));

            var downloads = DownloadCandidates(json);
            if (downloads.Count == 0)
                throw new Exception("RD returned no download URL");
            return SelectByLocation(downloads, preferredLocation);
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

        public static string UserInfo(string token)
        {
            return ProbeUser(token);
        }

        public static string ProbeUser(string token)
        {
            try
            {
                string json = NetHttp.GetString(Api + "/user", 6000, null, token.Trim());
                string err = JsonLite.GetString(json, "error");
                if (!string.IsNullOrEmpty(err))
                    return "ERROR: " + err;
                string user = JsonLite.GetString(json, "username") ?? "?";
                string prem = JsonLite.GetString(json, "type") ?? "?";
                string exp = JsonLite.GetString(json, "expiration") ?? "";
                return (string.Equals(prem, "free", StringComparison.OrdinalIgnoreCase) ? "EXPIRED: " : "OK ") + "@" + user + " (" + prem + ") " + exp;
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }
    }
}
