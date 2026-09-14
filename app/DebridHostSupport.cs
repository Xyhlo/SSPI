using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbis
{
    internal enum DebridHostState { Unknown, Supported, Unavailable, Unsupported }

    internal sealed class DebridHostList
    {
        internal readonly Dictionary<string, bool> Domains = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, DebridHostState> States = new Dictionary<string, DebridHostState>(StringComparer.OrdinalIgnoreCase);
        internal readonly HashSet<string> CacheDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string, string> AccountNotes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal sealed class Rule { internal Regex Pattern; internal DebridHostState State; internal bool Cache; }
        internal readonly List<Rule> Rules = new List<Rule>();

        internal bool CanCheckCache(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return false;
            foreach (string domain in CacheDomains)
                if (uri.Host.Equals(domain, StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)) return true;
            return MatchRule(url, true) != null;
        }

        Rule MatchRule(string url, bool cacheOnly)
        {
            if (string.IsNullOrEmpty(url) || url.Length > 8192) return null;
            var elapsed = Stopwatch.StartNew();
            foreach (Rule rule in Rules)
            {
                if (elapsed.ElapsedMilliseconds > 30) break;
                if (cacheOnly && !rule.Cache) continue;
                try { Match match = rule.Pattern.Match(url); if (match.Success && match.Index == 0) return rule; }
                catch (RegexMatchTimeoutException) { }
            }
            return null;
        }

        internal DebridHostState GetState(string url)
        {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed) ||
                (parsed.Scheme != "https" && parsed.Scheme != "http")) return DebridHostState.Unknown;
            string host = parsed.DnsSafeHost.TrimEnd('.');
            int matchedLength = 0;
            DebridHostState result = DebridHostState.Unknown;
            foreach (var pair in States)
                if (pair.Key.Length > matchedLength && (host.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) ||
                    host.EndsWith("." + pair.Key, StringComparison.OrdinalIgnoreCase)))
                {
                    result = pair.Value;
                    matchedLength = pair.Key.Length;
                }
            // Inventories can omit aliases and newly added hosters. Absence is not proof of rejection.
            if (matchedLength == 0)
            {
                Rule rule = MatchRule(url, false);
                if (rule != null) return rule.State;
            }
            return result;
        }

        internal bool? Supports(string url)
        {
            DebridHostState state = GetState(url);
            return state == DebridHostState.Unknown ? (bool?)null : state == DebridHostState.Supported;
        }

        internal string AccountNote(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return "";
            string best = ""; int length = 0;
            foreach (var pair in AccountNotes)
                if (pair.Key.Length > length && (uri.Host.Equals(pair.Key, StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.EndsWith("." + pair.Key, StringComparison.OrdinalIgnoreCase))) { best = pair.Value; length = pair.Key.Length; }
            return best;
        }
    }

    internal static class DebridHostSupport
    {
        sealed class Cached { internal DebridHostList Hosts; internal DateTime Until; }
        static readonly Dictionary<string, Cached> Cache = new Dictionary<string, Cached>();

        internal static DebridHostList Parse(string provider, string json)
        {
            if (string.IsNullOrEmpty(json) || json.Length > 512 * 1024) throw new FormatException("Invalid host list size");
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
                    foreach (var domain in (IList)domains)
                    {
                        Add(result, domain as string, (bool)status ? DebridHostState.Supported : DebridHostState.Unavailable);
                        string d = domain as string;
                        if (d != null && result.States.ContainsKey(d))
                        {
                            if (LimitReached(row, "daily_link_limit", "daily_link_used") || LimitReached(row, "daily_bandwidth_limit", "daily_bandwidth_used"))
                                result.AccountNotes[d] = "Daily account allowance reached";
                            else
                            {
                                object note;
                                if (row.TryGetValue("note", out note) && note is string && ((string)note).IndexOf("captcha", StringComparison.OrdinalIgnoreCase) >= 0)
                                    result.AccountNotes[d] = "Interactive verification may be required";
                            }
                        }
                    }
                }
            }
            else if (provider == UnlockProviders.AllDebridId)
            {
                object status, data, hosts;
                if (!root.TryGetValue("status", out status) || !Equals(status, "success") ||
                    !root.TryGetValue("data", out data) || !(data is Dictionary<string, object>) ||
                    !((Dictionary<string, object>)data).TryGetValue("hosts", out hosts) || !(hosts is Dictionary<string, object>))
                    throw new FormatException("Invalid AllDebrid host list");
                foreach (var pair in (Dictionary<string, object>)hosts)
                {
                    var row = pair.Value as Dictionary<string, object>;
                    object domains, available, quota;
                    if (row == null || !row.TryGetValue("domains", out domains) || !(domains is IList)) continue;
                    DebridHostState state = DebridHostState.Supported;
                    if (row.TryGetValue("status", out available))
                        state = available is bool ? ((bool)available ? DebridHostState.Supported : DebridHostState.Unavailable) : DebridHostState.Unknown;
                    long remaining;
                    bool limited = row.TryGetValue("quota", out quota) && long.TryParse(Convert.ToString(quota), out remaining) && remaining <= 0;
                    foreach (var domain in (IList)domains)
                    {
                        Add(result, domain as string, state);
                        string d = domain as string;
                        if (limited && d != null && result.States.ContainsKey(d)) result.AccountNotes[d] = "Account host allowance reached";
                    }
                }
            }
            else if (provider == UnlockProviders.PremiumizeId)
            {
                object status, direct, cache, aliases, patterns;
                if (!root.TryGetValue("status", out status) || !Equals(status, "success") ||
                    !root.TryGetValue("directdl", out direct) || !(direct is IList)) throw new FormatException("Invalid Premiumize service list");
                var directServices = Strings((IList)direct);
                var cacheServices = root.TryGetValue("cache", out cache) && cache is IList ? Strings((IList)cache) : new HashSet<string>();
                var aliasMap = root.TryGetValue("aliases", out aliases) ? aliases as Dictionary<string, object> : null;
                var patternMap = root.TryGetValue("regexpatterns", out patterns) ? patterns as Dictionary<string, object> : null;
                var services = new HashSet<string>(directServices);
                if (aliasMap != null) foreach (string service in aliasMap.Keys) services.Add(service);
                if (patternMap != null) foreach (string service in patternMap.Keys) services.Add(service);
                foreach (string service in services)
                {
                    DebridHostState state = directServices.Contains(service) ? DebridHostState.Supported : DebridHostState.Unavailable;
                    bool cacheable = cacheServices.Contains(service);
                    Add(result, service, state);
                    if (cacheable && result.States.ContainsKey(service)) result.CacheDomains.Add(service);
                    object values;
                    if (aliasMap != null && aliasMap.TryGetValue(service, out values) && values is IList)
                        foreach (var value in (IList)values)
                        {
                            string domain = value as string;
                            Add(result, domain, state);
                            if (cacheable && domain != null && result.States.ContainsKey(domain)) result.CacheDomains.Add(domain);
                        }
                    if (patternMap != null && patternMap.TryGetValue(service, out values) && values is IList)
                        foreach (var value in (IList)values)
                        {
                            string pattern = value as string;
                            if (string.IsNullOrEmpty(pattern) || pattern.Length > 2048 || result.Rules.Count >= 256) continue;
                            try { result.Rules.Add(new DebridHostList.Rule { Pattern = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(10)), State = state, Cache = cacheable }); }
                            catch (ArgumentException) { }
                        }
                }
            }
            else if (provider == UnlockProviders.RealDebridId)
            {
                foreach (var row in root)
                {
                    var details = row.Value as Dictionary<string, object>;
                    if (details == null) continue;
                    object supported, status;
                    DebridHostState state = DebridHostState.Unknown;
                    if (details.TryGetValue("supported", out supported))
                    {
                        if (Convert.ToString(supported) == "0" || Equals(supported, false)) state = DebridHostState.Unsupported;
                        else if (Convert.ToString(supported) == "1" || Equals(supported, true)) state = DebridHostState.Supported;
                    }
                    else if (details.ContainsKey("id") && details.ContainsKey("name"))
                        state = DebridHostState.Supported; // /hosts is a support inventory, not a cache report.
                    if (details.TryGetValue("status", out status))
                    {
                        string current = Convert.ToString(status);
                        if (string.Equals(current, "unsupported", StringComparison.OrdinalIgnoreCase)) state = DebridHostState.Unsupported;
                        else if (state == DebridHostState.Supported)
                            state = string.Equals(current, "up", StringComparison.OrdinalIgnoreCase) ? DebridHostState.Supported :
                                string.Equals(current, "down", StringComparison.OrdinalIgnoreCase) ? DebridHostState.Unavailable : DebridHostState.Unknown;
                    }
                    Add(result, row.Key, state);
                }
            }
            if (result.Domains.Count == 0 && result.Rules.Count == 0) throw new FormatException("Empty host list");
            return result;
        }

        static HashSet<string> Strings(IList items)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items) if (item is string) result.Add((string)item);
            return result;
        }

        static bool LimitReached(Dictionary<string, object> row, string limitKey, string usedKey)
        {
            object limit, used; long maximum, consumed;
            return row.TryGetValue(limitKey, out limit) && row.TryGetValue(usedKey, out used) &&
                long.TryParse(Convert.ToString(limit), out maximum) && long.TryParse(Convert.ToString(used), out consumed) &&
                maximum > 0 && consumed >= maximum;
        }

        internal static void AddRealDebridAliases(DebridHostList hosts, string json)
        {
            if (hosts == null || string.IsNullOrEmpty(json) || json.Length > 512 * 1024) return;
            var patterns = PackageSourceJson.Parse(json) as IList;
            if (patterns == null) return;
            foreach (object value in patterns)
            {
                string pattern = value as string;
                if (string.IsNullOrEmpty(pattern) || pattern.Length > 2048 || hosts.Rules.Count >= 256) continue;
                int end = pattern.LastIndexOf('/');
                if (pattern[0] != '/' || end <= 0) continue;
                pattern = pattern.Substring(1, end - 1).Replace("\\/", "/");
                // Preserve these official grouped-domain expressions while exposing
                // their canonical host for availability attribution.
                pattern = pattern.Replace("(docs|drive)\\.google\\.com", "(?:docs\\.google\\.com|drive\\.google\\.com)")
                    .Replace("send\\.(cm|now)", "(?:send\\.cm|send\\.now)");
                string canonical = null;
                foreach (string domain in hosts.States.Keys)
                    if (pattern.IndexOf(Regex.Escape(domain), StringComparison.OrdinalIgnoreCase) >= 0 &&
                        (canonical == null || domain.Length > canonical.Length)) canonical = domain;
                if (canonical == null)
                {
                    // These rename pairs were verified against /hosts and /hosts/regex.
                    // They only apply when today's regex still explicitly matches the alias.
                    string[,] renamed = { { "ddl.to", "ddownload.com" }, { "hexload.com", "hexupload.net" },
                        { "mega.nz", "mega.co.nz" }, { "radiotunes.com", "sky.fm" } };
                    for (int i = 0; i < renamed.GetLength(0); i++)
                        if (hosts.States.ContainsKey(renamed[i, 1]) && pattern.IndexOf(Regex.Escape(renamed[i, 0]), StringComparison.OrdinalIgnoreCase) >= 0)
                        { canonical = renamed[i, 1]; break; }
                }
                // Only attach aliases to a host independently present in the current inventory.
                if (canonical == null) continue;
                try { hosts.Rules.Add(new DebridHostList.Rule { Pattern = new Regex("^(?:" + pattern + ")",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(10)), State = hosts.States[canonical] }); }
                catch (ArgumentException) { }
            }
        }

        static void Add(DebridHostList result, string domain, DebridHostState state)
        {
            Uri uri;
            if (string.IsNullOrEmpty(domain) || !Uri.TryCreate("https://" + domain, UriKind.Absolute, out uri) ||
                uri.Host != domain || domain.IndexOf('.') < 0 || uri.AbsolutePath != "/") return;
            result.Domains[domain] = state == DebridHostState.Supported;
            result.States[domain] = state;
        }

        internal static DebridHostList Load(AppSettings cfg, string provider, bool refresh)
        {
            string token = UnlockProviders.ApiKey(cfg, provider);
            return Load(provider, token, refresh, 8000, null);
        }

        internal static string CredentialScope(string provider, string token)
        {
            using (var sha = SHA256.Create())
                return provider + ":" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(token ?? "")));
        }

        internal static DebridHostList Load(string provider, string token, bool refresh, int timeoutMs, Func<bool> canceled)
        {
            if (!UnlockProviders.IsSupported(provider) ||
                (canceled != null && canceled())) return null;
            string key = CredentialScope(provider, token);
            lock (Cache)
            {
                Cached cached;
                if (Cache.TryGetValue(key, out cached) && cached.Until > DateTime.UtcNow) return cached.Hosts;
                if (!refresh) return null;
            }
            if (string.IsNullOrWhiteSpace(token) || timeoutMs < 500) return null;
            var elapsed = Stopwatch.StartNew();
            DebridHostList hosts = null;
            try
            {
                string endpoint = provider == UnlockProviders.TorBoxId
                    ? "https://api.torbox.app/v1/api/webdl/hosters"
                    : provider == UnlockProviders.AllDebridId ? "https://api.alldebrid.com/v4.1/user/hosts"
                    : provider == UnlockProviders.PremiumizeId ? "https://www.premiumize.me/api/services/list"
                    : "https://api.real-debrid.com/rest/1.0/hosts/status";
                string json = NetHttp.GetStringDirect(endpoint, timeoutMs, null, token);
                hosts = Parse(provider, json);
            }
            catch
            {
                // An unavailable status endpoint must not turn into an empty catalog.
                // Real-Debrid's public host inventory is a safe fallback for support.
                int remaining = timeoutMs - (int)elapsed.ElapsedMilliseconds;
                if (provider == UnlockProviders.RealDebridId && remaining >= 500 && (canceled == null || !canceled()))
                    try { hosts = Parse(provider, NetHttp.GetStringDirect("https://api.real-debrid.com/rest/1.0/hosts", remaining)); } catch { }
            }
            int aliasTimeout = timeoutMs - (int)elapsed.ElapsedMilliseconds;
            if (provider == UnlockProviders.RealDebridId && hosts != null && aliasTimeout >= 500 && (canceled == null || !canceled()))
                try { AddRealDebridAliases(hosts, NetHttp.GetStringDirect("https://api.real-debrid.com/rest/1.0/hosts/regex", aliasTimeout)); } catch { }
            if (canceled != null && canceled()) return null;
            lock (Cache)
            {
                if (Cache.Count > 16) Cache.Clear();
                Cache[key] = new Cached { Hosts = hosts, Until = DateTime.UtcNow.AddSeconds(hosts == null ? 30 : 60) };
            }
            return hosts;
        }

        internal static string UnsupportedMessage(AppSettings cfg, string url, string provider)
        {
            Uri uri;
            string host = Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.Host : "This host";
            string action = "Choose another mirror or refresh the package list.";
            foreach (string other in new[] { UnlockProviders.RealDebridId, UnlockProviders.TorBoxId, UnlockProviders.AllDebridId, UnlockProviders.PremiumizeId })
            {
                if (other == provider || !UnlockProviders.IsConfigured(cfg, other)) continue;
                var alternatives = Load(cfg, other, false);
                if (alternatives == null || alternatives.Supports(url) != true) continue;
                action = UnlockProviders.DisplayName(other) + " lists this host; select it in Connections.";
                break;
            }
            return UnlockProviders.DisplayName(provider) + " cannot resolve " + host + " right now. " + action;
        }

        internal static List<PackageCandidate> Filter(AppSettings cfg, List<PackageCandidate> candidates, bool refresh, out string message)
        {
            // Host status is advisory. All package links stay visible and selectable.
            message = null;
            return candidates;
        }
    }
}
