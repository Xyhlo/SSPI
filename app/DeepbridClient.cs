using System;

namespace Orbis
{
    internal static class DeepbridClient
    {
        const string ApiBase = "https://www.deepbrid.com/backend-dl/index.php";

        public static string Unrestrict(string apiKey, string hostUrl)
        {
            if (string.IsNullOrEmpty(apiKey))
                throw new Exception("Deepbrid API key missing");
            if (string.IsNullOrEmpty(hostUrl))
                throw new Exception("Empty host URL");

            string url = ApiBase +
                "?page=api&action=generateLink" +
                "&api_key=" + Uri.EscapeDataString(apiKey.Trim()) +
                "&link=" + Uri.EscapeDataString(hostUrl);

            string json;
            try
            {
                json = NetHttp.GetString(url, 60000, "https://www.deepbrid.com/");
            }
            catch (Exception ex)
            {
                throw new Exception("Deepbrid unrestrict failed: " + ex.Message);
            }

            string download = First(json, "downloadUrl", "download_url", "link", "url", "download");
            if (string.IsNullOrEmpty(download) ||
                !(download.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                  download.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                throw new Exception("Deepbrid returned no download URL");
            return download.Trim();
        }

        public static string ProbeUser(string apiKey)
        {
            try
            {
                string url = ApiBase +
                    "?page=api&action=accountInfo" +
                    "&api_key=" + Uri.EscapeDataString((apiKey ?? "").Trim());
                string json = NetHttp.GetString(url, 6000, "https://www.deepbrid.com/");
                string user = First(json, "username", "email", "login") ?? "?";
                string type = First(json, "type", "account_type", "premium") ?? "?";
                return "OK @" + user + " (" + type + ")";
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }

        static string First(string json, params string[] keys)
        {
            foreach (var k in keys)
            {
                string v = JsonLite.GetString(json, k);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }
    }
}
