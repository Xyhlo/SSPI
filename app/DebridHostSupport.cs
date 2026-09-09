using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Orbis
{
    internal sealed class DebridHostList
    {
        internal readonly Dictionary<string, bool> Domains = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        internal bool? Supports(string url)
        {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return false;
            string host = parsed.DnsSafeHost.TrimEnd('.');
            foreach (var pair in Domains)
                if (host.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith("." + pair.Key, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            return Domains.Count == 0 ? (bool?)null : false;
        }
    }

    internal static class DebridHostSupport
    {
        sealed class Cached { internal DebridHostList Hosts; internal DateTime Until; }
        static readonly Dictionary<string, Cached> Cache = new Dictionary<string, Cached>();

        internal static DebridHostList Parse(string provider, string json)
        {
            var result = new DebridHostList();
            var root = PackageSourceJson.Parse(json) as Dictionary<string, object>;
            if (root == null) throw new FormatException("Invalid host list");
            if (provider == UnlockProviders.TorBoxId)
            {
                object data, success;
                if (!root.TryGetValue("success", out success) || !Equals(success, true) ||
                    !root.TryGetValue("data", out data) || !(data is IList)) throw new FormatException("Invalid TorBox host list");
                foreach (var item in (IList)data)
                {
                    var row = item as Dictionary<string, object>;
                    object domains, status;
                    if (row == null || !row.TryGetValue("domains", out domains) || !(domains is IList) ||
                        !row.TryGetValue("status", out status) || !(status is bool)) continue;
                    foreach (var domain in (IList)domains) Add(result, domain as string, (bool)status);
                }
            }
            else
            {
                foreach (var row in root)
                {
                    var details = row.Value as Dictionary<string, object>;
                    if (details == null) continue;
                    object supported, status;
                    bool available = !details.TryGetValue("supported", out supported) || Convert.ToString(supported) == "1";
                    if (details.TryGetValue("status", out status))
                        available &= string.Equals(Convert.ToString(status), "up", StringComparison.OrdinalIgnoreCase);
                    Add(result, row.Key, available);
                }
            }
            if (result.Domains.Count == 0) throw new FormatException("Empty host list");
            return result;
        }

        static void Add(DebridHostList result, string domain, bool available)
        {
            Uri uri;
            if (string.IsNullOrEmpty(domain) || !Uri.TryCreate("https://" + domain, UriKind.Absolute, out uri) ||
                uri.Host != domain || domain.IndexOf('.') < 0 || uri.AbsolutePath != "/") return;
            result.Domains[domain] = available;
        }

        internal static DebridHostList Load(AppSettings cfg, string provider, bool refresh)
        {
            string token = provider == UnlockProviders.TorBoxId ? cfg.TorBoxApiKey : cfg.RealDebridToken;
            string key;
            using (var sha = SHA256.Create()) key = provider + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(token ?? "")));
            lock (Cache)
            {
                Cached cached;
                if (Cache.TryGetValue(key, out cached) && cached.Until > DateTime.UtcNow) return cached.Hosts;
                if (!refresh) return null;
            }
            DebridHostList hosts = null;
            try
            {
                string endpoint = provider == UnlockProviders.TorBoxId
                    ? "https://api.torbox.app/v1/api/webdl/hosters"
                    : "https://api.real-debrid.com/rest/1.0/hosts/status";
                string json = NetHttp.GetStringDirect(endpoint, 8000, null, token);
                hosts = Parse(provider, json);
            }
            catch
            {
                // An unavailable status endpoint must not turn into an empty catalog.
                // Real-Debrid's public host inventory is a safe fallback for support.
                if (provider == UnlockProviders.RealDebridId)
                    try { hosts = Parse(provider, NetHttp.GetStringDirect("https://api.real-debrid.com/rest/1.0/hosts", 5000)); } catch { }
            }
            lock (Cache)
            {
                if (Cache.Count > 16) Cache.Clear();
                Cache[key] = new Cached { Hosts = hosts, Until = DateTime.UtcNow.AddSeconds(hosts == null ? 30 : 300) };
            }
            return hosts;
        }

        internal static string UnsupportedMessage(AppSettings cfg, string url, string provider)
        {
            Uri uri;
            string host = Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.Host : "This host";
            string other = provider == UnlockProviders.TorBoxId ? UnlockProviders.RealDebridId : UnlockProviders.TorBoxId;
            var alternatives = Load(cfg, other, false);
            string action = alternatives != null && alternatives.Supports(url) == true
                ? UnlockProviders.DisplayName(other) + " lists this host; select it in Connections."
                : "Choose another mirror or refresh the package list.";
            return UnlockProviders.DisplayName(provider) + " cannot resolve " + host + " right now. " + action;
        }

        internal static List<PackageCandidate> Filter(AppSettings cfg, List<PackageCandidate> candidates, bool refresh, out string message)
        {
            message = null;
            if (!cfg.UseUnlockProvider || cfg.UnlockProviderId == UnlockProviders.NoneId) return candidates;
            var hosts = Load(cfg, cfg.UnlockProviderId, refresh);
            if (hosts == null) return candidates;
            var result = new List<PackageCandidate>();
            foreach (var candidate in candidates)
            {
                bool usable = candidate.AccessType == PackageAccessType.Direct || hosts.Supports(candidate.Url) != false;
                if (usable && !string.IsNullOrEmpty(candidate.ArchiveVolumes))
                {
                    foreach (var volume in ArchiveVolumeSet.Decode(candidate.ArchiveVolumes))
                        if (!string.Equals(volume.AccessType, "Direct", StringComparison.OrdinalIgnoreCase) && hosts.Supports(volume.Url) == false) usable = false;
                }
                if (usable) result.Add(candidate);
                else if (message == null) message = UnsupportedMessage(cfg, candidate.Url, cfg.UnlockProviderId);
            }
            return result;
        }
    }
}
