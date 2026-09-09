using System;

namespace Orbis
{
    internal static class AllDebridClient
    {
        const string Api = "https://api.alldebrid.com/v4";

        public static string Unrestrict(string apiKey, string hostUrl)
        {
            if (string.IsNullOrEmpty(apiKey))
                throw new Exception("AllDebrid API key missing");
            if (string.IsNullOrEmpty(hostUrl))
                throw new Exception("Empty host URL");

            string url = Api + "/link/unlock?agent=GameSearch&apikey=" +
                Uri.EscapeDataString(apiKey.Trim()) + "&link=" + Uri.EscapeDataString(hostUrl);
            string json;
            try
            {
                json = NetHttp.GetString(url, 60000, "https://alldebrid.com/");
            }
            catch (Exception ex)
            {
                throw new Exception("AllDebrid unrestrict failed: " + ex.Message);
            }

            if (!string.Equals(JsonLite.GetString(json, "status"), "success", StringComparison.OrdinalIgnoreCase))
                throw new Exception("AllDebrid: " + Error(json));
            string download = JsonLite.GetString(json, "link");
            if (!IsHttp(download))
                throw new Exception("AllDebrid returned no download URL");
            return download.Trim();
        }

        public static string ProbeUser(string apiKey)
        {
            try
            {
                string url = Api + "/user?agent=GameSearch&apikey=" +
                    Uri.EscapeDataString((apiKey ?? "").Trim());
                string json = NetHttp.GetString(url, 6000, "https://alldebrid.com/");
                if (!string.Equals(JsonLite.GetString(json, "status"), "success", StringComparison.OrdinalIgnoreCase))
                    return "ERROR: " + Error(json);
                string user = JsonLite.GetString(json, "username") ?? "?";
                string premium = JsonLite.GetBool(json, "isPremium") ? "premium" : "free";
                return ((json.IndexOf("\"isPremium\"", StringComparison.Ordinal) >= 0 && !JsonLite.GetBool(json, "isPremium") && !JsonLite.GetBool(json, "isTrial")) ? "EXPIRED: " : "OK ") + "@" + user + " (" + premium + ")";
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }

        static string Error(string json)
        {
            return JsonLite.GetString(json, "message") ??
                JsonLite.GetString(json, "code") ?? "request failed";
        }

        static bool IsHttp(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }
    }
}
