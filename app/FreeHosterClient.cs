using System;
using System.Text.RegularExpressions;

namespace Orbis
{
    /// <summary>
    /// Just-in-time free-hoster page → direct file URL (no debrid).
    /// Interactive wait/captcha flows (typical 1fichier free) are rejected clearly.
    /// </summary>
internal static class FreeHosterClient
{
        static readonly Regex HrefRe = new Regex(
            @"href\s*=\s*[""']([^""']+)[""']",
            RegexOptions.IgnoreCase);
        static readonly Regex MediaFireDl = new Regex(
            @"https?://download\d*\.mediafire\.com/[^""'\s<>]+",
            RegexOptions.IgnoreCase);
        static readonly Regex GofileId = new Regex(
            @"gofile\.io/(?:\?c=|d/|file/)([A-Za-z0-9]+)",
            RegexOptions.IgnoreCase);
static readonly Regex PixelId = new Regex(
@"pixeldrain\.com/(?:u|l)/([A-Za-z0-9_-]+)",
RegexOptions.IgnoreCase);

public static bool IsSupportedHoster(Uri uri)
{
    if (uri == null || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
        !string.IsNullOrEmpty(uri.UserInfo)) return false;
    string host = (uri.Host ?? "").ToLowerInvariant();
    if (host.StartsWith("www.")) host = host.Substring(4);
    return host == "pixeldrain.com" || host.EndsWith(".pixeldrain.com") ||
        host == "mediafire.com" || host.EndsWith(".mediafire.com") ||
        host == "gofile.io" || host.EndsWith(".gofile.io") ||
        host == "1fichier.com" || host.EndsWith(".1fichier.com") ||
        host.Contains("rapidgator") || host.Contains("uploaded") || host == "ul.to" ||
        host.Contains("katfile") || host.Contains("nitroflare") || host.Contains("turbobit") ||
        host == "mega.nz" || host == "mega.co.nz" || host.Contains("workupload") ||
        host.Contains("buzzheavier");
}

        /// <summary>Returns a byte-stream URL or throws a short user-facing reason.</summary>
        public static string ResolveDirect(string hosterUrl)
        {
            if (string.IsNullOrEmpty(hosterUrl))
                throw new Exception("Empty hoster URL");

            Uri u;
            try { u = new Uri(hosterUrl); }
            catch { throw new Exception("Bad hoster URL"); }

            string host = (u.Host ?? "").ToLowerInvariant();
            if (host.StartsWith("www.")) host = host.Substring(4);

            // Already looks like a CDN file path — keep as-is.
            if (LooksLikeDirectFile(u))
                return hosterUrl;

            if (host == "pixeldrain.com" || host.EndsWith(".pixeldrain.com"))
                return ResolvePixeldrain(u);

            if (host == "mediafire.com" || host.EndsWith(".mediafire.com"))
                return ResolveMediaFire(hosterUrl);

            if (host == "gofile.io" || host.EndsWith(".gofile.io"))
                return ResolveGofile(u);

            if (host == "1fichier.com" || host.EndsWith(".1fichier.com"))
                throw new Exception("1fichier free needs wait/captcha — use Real-Debrid or Deepbrid");

            if (host.Contains("rapidgator") || host.Contains("uploaded") || host == "ul.to" ||
                host.Contains("katfile") || host.Contains("nitroflare") || host.Contains("turbobit"))
                throw new Exception(host + " free is interactive — use unlock provider");

            // MEGA needs their API/client — debrid only for free path on console.
            if (host == "mega.nz" || host == "mega.co.nz")
                throw new Exception("MEGA free unsupported on console — use unlock provider");

            // Hoster page: try one GET and hunt for a direct file link on allowlisted CDNs.
            try
            {
                string html = NetHttp.GetString(hosterUrl, 25000, hosterUrl);
                if (string.IsNullOrEmpty(html))
                    throw new Exception("Empty hoster page");
                if (html.IndexOf("<html", StringComparison.OrdinalIgnoreCase) < 0 &&
                    html.Length > 1024)
                    return hosterUrl; // body might already be binary mislabeled

                string found = FindDirectInHtml(html, hosterUrl);
                if (!string.IsNullOrEmpty(found))
                    return found;
            }
            catch (Exception ex)
            {
                if (ex.Message != null && ex.Message.IndexOf("interactive", StringComparison.OrdinalIgnoreCase) >= 0)
                    throw;
                throw new Exception("Free link unresolved: " + Clip(ex.Message, 48));
            }

            throw new Exception("Free download page needs browser/wait — use RD/Deepbrid");
        }

        static string ResolvePixeldrain(Uri u)
        {
            var m = PixelId.Match(u.ToString());
            if (!m.Success)
                throw new Exception("Bad Pixeldrain URL");
            string id = m.Groups[1].Value;
            // Public API streams the file without an interstitial.
            return "https://pixeldrain.com/api/file/" + id + "?download";
        }

        static string ResolveMediaFire(string pageUrl)
        {
            string html = NetHttp.GetString(pageUrl, 30000, "https://www.mediafire.com/");
            var m = MediaFireDl.Match(html ?? "");
            if (m.Success) return m.Value.Replace("&amp;", "&");
            foreach (Match h in HrefRe.Matches(html ?? ""))
            {
                string href = h.Groups[1].Value.Replace("&amp;", "&");
                if (href.IndexOf("download", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    href.IndexOf("mediafire.com", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return href;
            }
            throw new Exception("MediaFire free link not found (page may need browser)");
        }

        static string ResolveGofile(Uri u)
        {
            var m = GofileId.Match(u.ToString());
            if (!m.Success)
                throw new Exception("Bad GoFile URL");
            // Public content API. Do not embed undocumented website tokens in the client.
            string id = m.Groups[1].Value;
            string json = NetHttp.GetString(
                "https://api.gofile.io/contents/" + id,
                25000, "https://gofile.io/");
            // "link":"https://...
            var lm = Regex.Match(json ?? "", @"""link""\s*:\s*""(https?:[^""]+)""", RegexOptions.IgnoreCase);
            if (lm.Success)
                return lm.Groups[1].Value.Replace("\\/", "/");
            var dm = Regex.Match(json ?? "", @"""directLink""\s*:\s*""(https?:[^""]+)""", RegexOptions.IgnoreCase);
            if (dm.Success)
                return dm.Groups[1].Value.Replace("\\/", "/");
            throw new Exception("GoFile free API returned no file link");
        }

        static bool LooksLikeDirectFile(Uri u)
        {
            string path = (u.AbsolutePath ?? "").ToLowerInvariant();
            if (path.EndsWith(".pkg") || path.EndsWith(".zip") || path.EndsWith(".rar") ||
                path.EndsWith(".7z") || path.EndsWith(".iso"))
                return true;
            // Pixeldrain API already direct
            if (path.IndexOf("/api/file/", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        static string FindDirectInHtml(string html, string baseUrl)
        {
            var mf = MediaFireDl.Match(html);
            if (mf.Success) return mf.Value.Replace("&amp;", "&");

            foreach (Match h in HrefRe.Matches(html))
            {
                string href = h.Groups[1].Value.Replace("&amp;", "&").Trim();
                if (href.Length < 12) continue;
                string abs;
                try { abs = new Uri(new Uri(baseUrl), href).ToString(); }
                catch { continue; }
                Uri u;
                try { u = new Uri(abs); } catch { continue; }
                string host = (u.Host ?? "").ToLowerInvariant();
                if (host.Contains("download") && (host.Contains("mediafire") || host.Contains("gofile") ||
                    host.Contains("pixeldrain") || host.Contains("workupload") || host.Contains("buzzheavier")))
                    return abs;
                if (LooksLikeDirectFile(u))
                    return abs;
            }
            return null;
        }

        static string Clip(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }
    }
}
