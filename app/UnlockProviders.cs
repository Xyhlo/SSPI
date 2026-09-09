using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Orbis
{
    /// <summary>Link Services optionally turn compatible landing-page URLs into download URLs.</summary>
    internal static class UnlockProviders
    {
        public const string RealDebridId = "real-debrid";
        public const string DeepbridId = "deepbrid";
        public const string AllDebridId = "alldebrid";
        public const string TorBoxId = "torbox";
        public const string NoneId = "none";

        public static string DisplayName(string id)
        {
            if (string.Equals(id, DeepbridId, StringComparison.OrdinalIgnoreCase)) return "Deepbrid";
            if (string.Equals(id, AllDebridId, StringComparison.OrdinalIgnoreCase)) return "AllDebrid";
            if (string.Equals(id, TorBoxId, StringComparison.OrdinalIgnoreCase)) return "TorBox";
            if (string.Equals(id, NoneId, StringComparison.OrdinalIgnoreCase)) return "Direct links";
            return "Real-Debrid";
        }

        public static bool IsConfigured(AppSettings cfg, string id)
        {
            if (cfg == null || id == DeepbridId || id == AllDebridId) return false;
            if (string.Equals(id, DeepbridId, StringComparison.OrdinalIgnoreCase))
                return cfg.HasDeepbrid;
            if (string.Equals(id, AllDebridId, StringComparison.OrdinalIgnoreCase))
                return cfg.HasAllDebrid;
            if (string.Equals(id, TorBoxId, StringComparison.OrdinalIgnoreCase))
                return cfg.HasTorBox;
            if (string.Equals(id, NoneId, StringComparison.OrdinalIgnoreCase))
                return true;
            return cfg.HasRealDebrid;
        }

        public static string Unrestrict(AppSettings cfg, string hosterUrl, Action<string> progress = null, Func<bool> cancel = null)
        {
            if (cfg == null) throw new Exception("No settings");
            if (!cfg.UseUnlockProvider || cfg.UnlockProviderId == NoneId) return hosterUrl;
            string selected = cfg.UnlockProviderId ?? RealDebridId;
            if (selected != RealDebridId && selected != TorBoxId) throw new Exception("Select Real-Debrid or TorBox in Connections");
            string status = CachedProbe(cfg, selected);
            // A failed account-status request is not evidence that a subscription expired.
            if (!status.StartsWith("EXPIRED:", StringComparison.Ordinal)) return UnrestrictWith(cfg, hosterUrl, selected, progress, cancel);
            foreach (string id in new[] { RealDebridId, TorBoxId })
            {
                if (id == selected || !IsConfigured(cfg, id)) continue;
                if (Probe(cfg, id).StartsWith("EXPIRED:", StringComparison.Ordinal)) continue;
                try { return UnrestrictWith(cfg, hosterUrl, id, progress, cancel); }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            throw new Exception(DisplayName(selected) + " subscription expired. Renew it or select another Link Service in Settings.");
        }

        sealed class AccountCache { public string Status; public DateTime Until; }
        static readonly Dictionary<string, AccountCache> Accounts = new Dictionary<string, AccountCache>();
        static string CachedProbe(AppSettings cfg, string id)
        {
            string token = id == TorBoxId ? cfg.TorBoxApiKey : cfg.RealDebridToken;
            string key;
            using (var sha = SHA256.Create()) key = id + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(token ?? "")));
            lock (Accounts) {
                AccountCache cached;
                if (Accounts.TryGetValue(key, out cached) && cached.Until > DateTime.UtcNow) return cached.Status;
                Accounts[key] = new AccountCache { Status = "Status check pending", Until = DateTime.UtcNow.AddSeconds(15) };
            }
            System.Threading.ThreadPool.QueueUserWorkItem(_ => {
                string result;
                try { result = id == TorBoxId ? TorBoxClient.ProbeUser(token) : RealDebridClient.ProbeUser(token); } catch { result = "Status unavailable"; }
                lock (Accounts) {
                    if (Accounts.Count > 16) Accounts.Clear();
                    Accounts[key] = new AccountCache { Status = result, Until = DateTime.UtcNow.AddSeconds(result.StartsWith("ERROR") || result.StartsWith("Status unavailable") ? 30 : 300) };
                }
            });
            return "Status check pending";
        }
        public static string Probe(AppSettings cfg, string id)
        {
            if (cfg == null) return "Not connected";
            string token = id == DeepbridId ? cfg.DeepbridApiKey : id == AllDebridId ? cfg.AllDebridApiKey : id == TorBoxId ? cfg.TorBoxApiKey : cfg.RealDebridToken;
            string key;
            using (var sha = SHA256.Create()) key = id + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(token ?? "")));
            lock (Accounts) { AccountCache cached; if (Accounts.TryGetValue(key, out cached) && cached.Until > DateTime.UtcNow) return cached.Status; }
            string status = ProbeUncached(cfg, id);
            if (status.StartsWith("ERROR", StringComparison.Ordinal)) status = "Status unavailable; downloads will still be attempted";
            lock (Accounts) { if (Accounts.Count > 16) Accounts.Clear(); Accounts[key] = new AccountCache { Status = status, Until = DateTime.UtcNow.AddSeconds(status.StartsWith("Status unavailable") ? 30 : 300) }; }
            return status;
        }

        static string UnrestrictWith(AppSettings cfg, string hosterUrl, string id, Action<string> progress, Func<bool> cancel)
        {
            if (cfg == null) throw new Exception("No settings");
            if (!cfg.UseUnlockProvider ||
                string.Equals(cfg.UnlockProviderId, NoneId, StringComparison.OrdinalIgnoreCase))
                return hosterUrl;
            if (cancel != null && cancel()) throw new OperationCanceledException();
            // Host inventories can lag behind aliases and provider support. Let the
            // selected provider resolve the actual URL and report its own result.
            if (string.Equals(id, DeepbridId, StringComparison.OrdinalIgnoreCase))
            {
                if (!cfg.HasDeepbrid) throw new Exception("Connect Deepbrid in Settings");
                return DeepbridClient.Unrestrict(cfg.DeepbridApiKey, hosterUrl);
            }
            if (string.Equals(id, AllDebridId, StringComparison.OrdinalIgnoreCase))
            {
                if (!cfg.HasAllDebrid) throw new Exception("Connect AllDebrid in Settings");
                return AllDebridClient.Unrestrict(cfg.AllDebridApiKey, hosterUrl);
            }
            if (string.Equals(id, TorBoxId, StringComparison.OrdinalIgnoreCase))
            {
                if (!cfg.HasTorBox) throw new Exception("Connect TorBox in Settings");
                return TorBoxClient.Unrestrict(cfg.TorBoxApiKey, hosterUrl, progress, cancel);
            }

            if (!cfg.HasRealDebrid) throw new Exception("Pair Real-Debrid in Settings");
            return RealDebridClient.Unrestrict(cfg.RealDebridToken, hosterUrl,
                cfg.RealDebridLocation);
        }

        static string ProbeUncached(AppSettings cfg, string id)
        {
            if (string.Equals(id, DeepbridId, StringComparison.OrdinalIgnoreCase))
            {
                if (cfg == null || !cfg.HasDeepbrid) return "Not connected";
                return DeepbridClient.ProbeUser(cfg.DeepbridApiKey);
            }
            if (string.Equals(id, AllDebridId, StringComparison.OrdinalIgnoreCase))
            {
                if (cfg == null || !cfg.HasAllDebrid) return "Not connected";
                return AllDebridClient.ProbeUser(cfg.AllDebridApiKey);
            }
            if (string.Equals(id, TorBoxId, StringComparison.OrdinalIgnoreCase))
            {
                if (cfg == null || !cfg.HasTorBox) return "Not connected";
                return TorBoxClient.ProbeUser(cfg.TorBoxApiKey);
            }
            if (string.Equals(id, NoneId, StringComparison.OrdinalIgnoreCase))
                return "Direct hoster URLs (slow)";
            if (cfg == null || !cfg.HasRealDebrid) return "Not paired";
            return RealDebridClient.ProbeUser(cfg.RealDebridToken);
        }
    }
}
