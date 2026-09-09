using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbis
{
    /// <summary>
    /// Thin plain-HTTP client to the PC Game Search API (no HTTPS on PS4).
    /// GET /games/search?q=  GET /games/{CUSA}/downloads
    /// POST /realdebrid/unrestrict  GET /realdebrid/download/{ticket}
    /// </summary>
    internal sealed class PcGameApi
    {
        static readonly Regex CusaRe = new Regex(@"^CUSA\d{5}$", RegexOptions.IgnoreCase);

        readonly string _base;

        public PcGameApi(string apiBase)
        {
            _base = (apiBase ?? "").Trim().TrimEnd('/');
            if (_base.Length == 0)
                throw new Exception("Set PC API URL in Options");
            if (!_base.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !_base.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                _base = "http://" + _base;
        }

        public string BaseUrl { get { return _base; } }

        public void Health()
        {
            string body = HttpGet(_base + "/health", 8000);
            if (string.IsNullOrEmpty(body) || body.IndexOf("ok", StringComparison.OrdinalIgnoreCase) < 0)
            {
                // older APIs may not have health — try /api
                HttpGet(_base + "/api", 8000);
            }
        }

        public List<GameHit> Search(string query, int limit = 20)
        {
            if (string.IsNullOrEmpty(query) || query.Trim().Length < 2)
                return new List<GameHit>();

            string url = _base + "/games/search?q=" + Uri.EscapeDataString(query.Trim()) +
                         "&limit=" + limit;
            string body = HttpGet(url, 60000);

            // Preferred envelope: results[]
            var objs = JsonLite.ExtractObjectArray(body, "results");
            // Some responses nest under nothing else
            var list = new List<GameHit>();
            int bad = 0;
            foreach (var obj in objs)
            {
                string tid = First(obj, "title_id", "titleId", "titleid", "cusa", "cusa_id", "id");
                string name = First(obj, "name", "title", "game_title");
                if (string.IsNullOrEmpty(tid) || !CusaRe.IsMatch(tid))
                {
                    bad++;
                    continue;
                }
                if (string.IsNullOrEmpty(name))
                {
                    bad++;
                    continue;
                }
                list.Add(new GameHit
                {
                    TitleId = tid.ToUpperInvariant(),
                    Name = name,
                    Region = First(obj, "region", "region_code") ?? "?",
                    ImageUrl = First(obj, "cover_url", "image_url", "image", "cover", "icon_url", "icon"),
                    Source = First(obj, "source", "provider") ?? "pc"
                });
            }
            if (list.Count == 0 && bad > 0 && objs.Count > 0)
                throw new Exception("Invalid response from PC API (no valid CUSA rows)");
            return list;
        }

        public List<PkgLink> GetDownloads(string titleId)
        {
            titleId = (titleId ?? "").ToUpperInvariant();
            if (!CusaRe.IsMatch(titleId))
                throw new Exception("Bad title id");

            string url = _base + "/games/" + titleId + "/downloads";
            string body;
            try
            {
                body = HttpGet(url, 120000);
            }
            catch (ApiHttpException ex)
            {
                if (ex.StatusCode == 404)
                    throw new Exception("No package links for " + titleId);
                throw;
            }

            var objs = JsonLite.ExtractObjectArray(body, "downloads");
            var list = new List<PkgLink>();
            foreach (var obj in objs)
            {
                string kind = NormalizeKind(First(obj, "kind", "type", "category"));
                string link = First(obj, "url", "link", "download_url", "clean_url");
                if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(link))
                    continue;
                string label = First(obj, "label", "name", "title") ?? kind.ToUpperInvariant();
                list.Add(new PkgLink { Kind = kind, Label = label, Url = link });
            }
            return list;
        }

        /// <summary>PC unrestricts via RD and returns a plain HTTP ticket URL for DownloadFile.</summary>
        public string UnrestrictToTicket(string hosterUrl, string rdToken)
        {
            if (string.IsNullOrEmpty(hosterUrl))
                throw new Exception("Empty hoster URL");

            // JSON body
            string json = "{\"link\":\"" + JsonLite.Escape(hosterUrl) + "\"";
            if (!string.IsNullOrEmpty(rdToken))
                json += ",\"token\":\"" + JsonLite.Escape(rdToken.Trim()) + "\"";
            json += "}";

            string body = HttpPostJson(_base + "/realdebrid/unrestrict", json, 90000);
            string err = JsonLite.GetString(body, "error") ?? JsonLite.GetString(body, "detail");
            if (!string.IsNullOrEmpty(err) && body.IndexOf("\"download\"", StringComparison.Ordinal) < 0)
                throw new Exception(err);

            string download = JsonLite.GetString(body, "download");
            if (string.IsNullOrEmpty(download))
                throw new Exception("PC API returned no download ticket");

            // Ensure absolute URL on PC
            if (download.StartsWith("/"))
                download = _base + download;
            return download;
        }

        static string NormalizeKind(string k)
        {
            k = (k ?? "").Trim().ToLowerInvariant();
            if (k == "game" || k == "base" || k == "basegame") return "game";
            if (k == "update" || k == "patch") return "update";
            if (k == "dlc" || k == "addon" || k == "add-on") return "dlc";
            if (k == "file") return "file";
            return null;
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

        static string HttpGet(string url, int timeoutMs)
        {
            // Force plain request — never go through https proxy path for PC API
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = NetHttp.UserAgent;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.KeepAlive = false;
            req.AllowAutoRedirect = true;
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException wex)
            {
                var resp = wex.Response as HttpWebResponse;
                int code = resp != null ? (int)resp.StatusCode : 0;
                string detail = wex.Message;
                if (resp != null)
                {
                    try
                    {
                        using (var stream = resp.GetResponseStream())
                        using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                            detail = reader.ReadToEnd();
                    }
                    catch { }
                }
                throw new ApiHttpException(code, detail);
            }
        }

        static string HttpPostJson(string url, string json, int timeoutMs)
        {
            byte[] data = Encoding.UTF8.GetBytes(json ?? "{}");
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.UserAgent = NetHttp.UserAgent;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.ContentType = "application/json";
            req.ContentLength = data.Length;
            req.KeepAlive = false;
            using (var rs = req.GetRequestStream())
                rs.Write(data, 0, data.Length);
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var stream = resp.GetResponseStream())
                using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                    return reader.ReadToEnd();
            }
            catch (WebException wex)
            {
                var resp = wex.Response as HttpWebResponse;
                int code = resp != null ? (int)resp.StatusCode : 0;
                string detail = wex.Message;
                if (resp != null)
                {
                    try
                    {
                        using (var stream = resp.GetResponseStream())
                        using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                            detail = reader.ReadToEnd();
                    }
                    catch { }
                }
                throw new ApiHttpException(code, detail);
            }
        }
    }

    internal sealed class ApiHttpException : Exception
    {
        public int StatusCode { get; private set; }
        public ApiHttpException(int status, string message) : base(message ?? ("HTTP " + status))
        {
            StatusCode = status;
        }
    }
}
