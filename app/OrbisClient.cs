using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Orbis
{
    internal sealed class GameHit
    {
        public string TitleId;
        public string Name;
        public string Region;
        public string ImageUrl;
        public string Source;
        public string Version;
        public string Rating;
        public string Genres;
        public string Backport;
        public string CatalogUrl;
        public string SourceVersion;
    }

    internal sealed class PkgLink
    {
        public string Kind; // game, update, dlc
        public string Label;
        public string Url;
    }

    internal sealed class OrbisTitleMetadata
    {
        public bool Known;
        public string LatestVersion = "";
        public string RequiredFirmware = "";
        public DateTime ExpiresUtc;

        public bool HasNewerUpdate(string installedVersion)
        {
            Version installed, available;
            return Known && OrbisClient.TryAppVersion(installedVersion, out installed) &&
                OrbisClient.TryAppVersion(LatestVersion, out available) && available > installed;
        }

        public string UpdateLabel(string installedVersion)
        {
            if (!Known) return "Update info unavailable";
            if (LatestVersion.Length == 0) return "No update listed";
            Version installed;
            if (!OrbisClient.TryAppVersion(installedVersion, out installed)) return "Available v" + LatestVersion;
            return HasNewerUpdate(installedVersion) ? "Update v" + LatestVersion : "Latest listed v" + LatestVersion;
        }
    }

    /// <summary>OrbisPatches search (same endpoints as the Python API).</summary>
    internal static class OrbisClient
    {
        const string Base = "https://orbispatches.com";
        static readonly Regex TitleIdRe = new Regex(@"^CUSA\d{5}$", RegexOptions.IgnoreCase);
        static readonly object RegionGate = new object();
        static readonly Dictionary<string, string> KnownRegions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly object MetadataGate = new object();
        static readonly Dictionary<string, OrbisTitleMetadata> MetadataCache =
            new Dictionary<string, OrbisTitleMetadata>(StringComparer.OrdinalIgnoreCase);

        // Metadata only: this endpoint lists official patches, not DLC, backports or PKG links.
        public static OrbisTitleMetadata GetTitleMetadata(string titleId)
        {
            titleId = (titleId ?? "").Trim().ToUpperInvariant();
            if (!TitleIdRe.IsMatch(titleId)) return new OrbisTitleMetadata();
            lock (MetadataGate)
            {
                OrbisTitleMetadata cached;
                if (MetadataCache.TryGetValue(titleId, out cached) && cached.ExpiresUtc > DateTime.UtcNow)
                    return cached;
            }
            var result = new OrbisTitleMetadata();
            try
            {
                string pageUrl = Base + "/" + titleId;
                string html = NetHttp.GetString(pageUrl, 10000, Base + "/");
                string key = ParseMetadataKey(html, titleId);
                if (key != null)
                {
                    string body = "{\"titleid\":\"" + titleId + "\",\"key\":\"" + key + "\"}";
                    string json = NetHttp.PostForm(Base + "/api/internal/loadpatches", body, 10000, pageUrl);
                    result = ParseTitleMetadata(json);
                }
            }
            catch { } // Offline or a changed response means unknown, never "up to date".
            result.ExpiresUtc = DateTime.UtcNow.AddMinutes(result.Known ? 15 : 2);
            lock (MetadataGate)
            {
                if (MetadataCache.Count >= 256 && !MetadataCache.ContainsKey(titleId))
                {
                    string oldest = null; DateTime expiry = DateTime.MaxValue;
                    foreach (var item in MetadataCache)
                        if (item.Value.ExpiresUtc < expiry) { oldest = item.Key; expiry = item.Value.ExpiresUtc; }
                    if (oldest != null) MetadataCache.Remove(oldest);
                }
                MetadataCache[titleId] = result;
            }
            return result;
        }

        internal static string ParseMetadataKey(string html, string titleId)
        {
            Match attr = Regex.Match(html ?? "", @"\bdata-loadparams\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.IgnoreCase);
            if (!attr.Success) return null;
            string json = System.Net.WebUtility.HtmlDecode(attr.Groups[1].Success ? attr.Groups[1].Value : attr.Groups[2].Value).Replace('\'', '"');
            if (!string.Equals(JsonLite.GetString(json, "titleid"), titleId, StringComparison.OrdinalIgnoreCase)) return null;
            string key = JsonLite.GetString(json, "key") ?? "";
            return Regex.IsMatch(key, @"\A[a-fA-F0-9]{32,128}\z") ? key : null;
        }

        internal static OrbisTitleMetadata ParseTitleMetadata(string json)
        {
            var result = new OrbisTitleMetadata();
            if (!JsonLite.GetBool(json, "success", false) ||
                !Regex.IsMatch(json ?? "", @"""patches""\s*:\s*\[")) return result;
            bool any = false;
            Version latest = null;
            foreach (string patch in JsonLite.ExtractObjectArray(json, "patches"))
            {
                any = true;
                string version = JsonLite.GetString(patch, "version");
                Version parsed;
                if (!TryAppVersion(version, out parsed) || (latest != null && parsed <= latest)) continue;
                latest = parsed;
                result.LatestVersion = version.Trim();
                string firmware = JsonLite.GetString(patch, "required_firmware");
                Version parsedFirmware;
                result.RequiredFirmware = TryAppVersion(firmware, out parsedFirmware) ? firmware.Trim() : "";
            }
            result.Known = latest != null || (!any && Regex.IsMatch(json, @"""patches""\s*:\s*\[\s*\]"));
            return result;
        }

        internal static bool TryAppVersion(string value, out Version version)
        {
            version = null;
            if (string.IsNullOrWhiteSpace(value) || !Regex.IsMatch(value.Trim(), @"\A\d{1,5}\.\d{1,5}(?:\.\d{1,5}){0,2}\z")) return false;
            Version parsed;
            if (!Version.TryParse(value.Trim(), out parsed)) return false;
            version = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
            return true;
        }

        public static List<GameHit> Search(string query, int limit = 20)
        {
            var list = new List<GameHit>();
            if (string.IsNullOrEmpty(query) || query.Trim().Length < 2)
                return list;

            string url = Base + "/api/internal/search?term=" + Uri.EscapeDataString(query.Trim());
            string json = NetHttp.GetString(url, 40000, Base + "/");
            if (!JsonLite.GetBool(json, "success", false))
                throw new Exception(JsonLite.GetString(json, "message") ?? "Orbis search rejected");

            foreach (var obj in JsonLite.ExtractObjectArray(json, "results"))
            {
                string tid = (JsonLite.GetString(obj, "titleid") ?? "").ToUpperInvariant();
                if (!TitleIdRe.IsMatch(tid)) continue;
                var hit = new GameHit
                {
                    TitleId = tid,
                    Name = JsonLite.GetString(obj, "name") ?? tid,
                    Region = JsonLite.GetString(obj, "region") ?? "?",
                    ImageUrl = NormalizeImageUrl(JsonLite.GetString(obj, "icon")),
                    Source = "orbis"
                };
                list.Add(hit);
                // Resolve currently receives the selected TitleId/name pair only. Keep the
                // authoritative Orbis region beside the search result so resolvers can rank
                // exact-CUSA tables without changing UI/resolver method signatures.
                lock (RegionGate) KnownRegions[tid] = hit.Region;
                if (list.Count >= limit) break;
            }
            return list;
        }

        internal static string GetKnownRegion(string titleId)
        {
            if (string.IsNullOrEmpty(titleId)) return null;
            lock (RegionGate)
            {
                string region;
                return KnownRegions.TryGetValue(titleId.Trim(), out region) ? region : null;
            }
        }

        /// <summary>Exact TitleId hit with icon URL when Orbis knows it.</summary>
        public static GameHit LookupTitleId(string titleId)
        {
            titleId = (titleId ?? "").Trim().ToUpperInvariant();
            if (!TitleIdRe.IsMatch(titleId)) return null;
            var hits = Search(titleId, 12);
            foreach (var h in hits)
            {
                if (string.Equals(h.TitleId, titleId, StringComparison.OrdinalIgnoreCase))
                    return h;
            }
            // Name search fallback for sparse TitleId index
            return null;
        }

        public static GameHit LookupByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var hits = Search(name.Trim(), 8);
            if (hits.Count == 0) return null;
            string n = name.Trim();
            foreach (var h in hits)
            {
                if (string.Equals(h.Name, n, StringComparison.OrdinalIgnoreCase))
                    return h;
            }
            return hits[0];
        }

        static string NormalizeImageUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            Uri uri;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri))
            {
                if (!Uri.TryCreate(new Uri(Base + "/"), value.Trim(), out uri))
                    return null;
            }
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                return null;
            return uri.AbsoluteUri;
        }
    }
}
