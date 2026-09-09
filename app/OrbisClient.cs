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
    }

    internal sealed class PkgLink
    {
        public string Kind; // game, update, dlc
        public string Label;
        public string Url;
    }

    /// <summary>OrbisPatches search (same endpoints as the Python API).</summary>
    internal static class OrbisClient
    {
        const string Base = "https://orbispatches.com";
        static readonly Regex TitleIdRe = new Regex(@"^CUSA\d{5}$", RegexOptions.IgnoreCase);
        static readonly object RegionGate = new object();
        static readonly Dictionary<string, string> KnownRegions =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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
