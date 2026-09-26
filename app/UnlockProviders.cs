using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Orbis
{
    internal sealed class ProviderPreparationWaitException : Exception
    {
        internal readonly string ProviderId;
        internal readonly string HostUrl;
        internal readonly string State;
        internal readonly int RetryAfterSeconds;
        internal readonly bool HasDurableReceipt;

        internal ProviderPreparationWaitException(string providerId, string hostUrl, string state,
            int retryAfterSeconds, bool hasDurableReceipt, Exception inner = null)
            : base("Preparing with " + DisplayName(providerId), inner)
        {
            ProviderId = providerId ?? "";
            HostUrl = hostUrl ?? "";
            State = state ?? "preparing";
            RetryAfterSeconds = Math.Max(0, retryAfterSeconds);
            HasDurableReceipt = hasDurableReceipt;
        }

        static string DisplayName(string providerId)
        { return UnlockProviders.DisplayName(providerId); }
    }

    /// <summary>Link Services optionally turn compatible landing-page URLs into download URLs.</summary>
    internal static class UnlockProviders
    {
        public const string RealDebridId = "real-debrid";
        public const string DeepbridId = "deepbrid";
        public const string AllDebridId = "alldebrid";
        public const string TorBoxId = "torbox";
        public const string PremiumizeId = "premiumize";
        public const string NoneId = "none";
        static readonly string[] ProviderIds = { RealDebridId, TorBoxId, AllDebridId, PremiumizeId };

        public static string NormalizeIds(string value)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string item in (value ?? "").Split(',')) found.Add(item.Trim());
            var ids = new List<string>();
            foreach (string id in ProviderIds) if (found.Contains(id)) ids.Add(id);
            return string.Join(",", ids.ToArray());
        }

        public static string[] EnabledIds(AppSettings cfg)
        {
            var ids = new List<string>();
            if (cfg == null || !cfg.UseUnlockProvider) return ids.ToArray();
            string selected = cfg.EnabledUnlockProviders ?? cfg.UnlockProviderId ?? RealDebridId;
            var enabled = new HashSet<string>(NormalizeIds(selected).Split(','), StringComparer.OrdinalIgnoreCase);
            if (enabled.Contains(cfg.UnlockProviderId ?? "") && IsConfigured(cfg, cfg.UnlockProviderId)) ids.Add(cfg.UnlockProviderId);
            foreach (string id in ProviderIds)
                if (enabled.Contains(id) && IsConfigured(cfg, id) && !ids.Contains(id)) ids.Add(id);
            return ids.ToArray();
        }

        public static bool IsEnabled(AppSettings cfg, string id) { return Array.IndexOf(EnabledIds(cfg), id) >= 0; }

        public static string EnabledSummary(AppSettings cfg)
        {
            string[] ids = EnabledIds(cfg);
            if (ids.Length == 0) return "Direct links";
            if (ids.Length > 2) return ids.Length + " link services";
            return DisplayName(ids[0]) + (ids.Length == 2 ? " + " + DisplayName(ids[1]) : "");
        }

        // Never send a host to a service that has not confirmed support for it.
        public static string[] RankedProviderIds(AppSettings cfg, string url)
        {
            var supported = new List<string>();
            foreach (string id in EnabledIds(cfg))
            {
                var hosts = DebridHostSupport.Load(cfg, id, false);
                var state = hosts == null ? DebridHostState.Unknown : hosts.GetState(url);
                if (state == DebridHostState.Supported) supported.Add(id);
            }
            return supported.ToArray();
        }

        internal static bool HasSupportedAlternative(AppSettings cfg, IList<string> urls, string excluded)
        {
            if (urls == null || urls.Count == 0) return false;
            foreach (string url in urls)
            {
                bool supported = false;
                foreach (string id in RankedProviderIds(cfg, url))
                    if (!string.Equals(id, excluded, StringComparison.OrdinalIgnoreCase)) { supported = true; break; }
                if (!supported) return false;
            }
            return true;
        }

        public static string DisplayName(string id)
        {
            if (string.Equals(id, DeepbridId, StringComparison.OrdinalIgnoreCase)) return "Deepbrid";
            if (string.Equals(id, AllDebridId, StringComparison.OrdinalIgnoreCase)) return "AllDebrid";
            if (string.Equals(id, TorBoxId, StringComparison.OrdinalIgnoreCase)) return "TorBox";
            if (string.Equals(id, PremiumizeId, StringComparison.OrdinalIgnoreCase)) return "Premiumize";
            if (string.Equals(id, NoneId, StringComparison.OrdinalIgnoreCase)) return "Direct links";
            return "Real-Debrid";
        }

        public static bool IsConfigured(AppSettings cfg, string id)
        {
            if (cfg == null || id == DeepbridId) return false;
            if (string.Equals(id, DeepbridId, StringComparison.OrdinalIgnoreCase))
                return cfg.HasDeepbrid;
            if (string.Equals(id, AllDebridId, StringComparison.OrdinalIgnoreCase))
                return cfg.HasAllDebrid;
            if (string.Equals(id, TorBoxId, StringComparison.OrdinalIgnoreCase))
                return cfg.HasTorBox;
            if (string.Equals(id, PremiumizeId, StringComparison.OrdinalIgnoreCase))
                return cfg.HasPremiumize;
            if (string.Equals(id, NoneId, StringComparison.OrdinalIgnoreCase))
                return true;
            return string.Equals(id, RealDebridId, StringComparison.OrdinalIgnoreCase) && cfg.HasRealDebrid;
        }

        public static bool IsSupported(string id)
        {
            return id == RealDebridId || id == TorBoxId || id == AllDebridId || id == PremiumizeId;
        }

        public static string ApiKey(AppSettings cfg, string id)
        {
            if (cfg == null) return "";
            return (id == RealDebridId ? cfg.RealDebridToken : id == TorBoxId ? cfg.TorBoxApiKey :
                id == AllDebridId ? cfg.AllDebridApiKey : id == PremiumizeId ? cfg.PremiumizeApiKey : "") ?? "";
        }

        public static string Unrestrict(AppSettings cfg, string hosterUrl, Action<string> progress = null, Func<bool> cancel = null,
            Action<string> providerSelected = null, string preferredProviderId = null, ISet<string> unavailableProviders = null,
            bool parkWhenPreparing = false, bool requirePreferredProvider = false, Action<string> providerRejected = null)
        {
            if (cfg == null) throw new Exception("No settings");
            if (!cfg.UseUnlockProvider || (cfg.EnabledUnlockProviders == null && cfg.UnlockProviderId == NoneId)) return hosterUrl;
            DebridHostSupport.RefreshEnabled(cfg);
            string[] ids = RankedProviderIds(cfg, hosterUrl);
            int preferred = Array.IndexOf(ids, preferredProviderId ?? "");
            if (preferred > 0)
            {
                string first = ids[preferred];
                Array.Copy(ids, 0, ids, 1, preferred);
                ids[0] = first;
            }
            if (requirePreferredProvider)
            {
                if (preferredProviderId == null || Array.IndexOf(ids, preferredProviderId) < 0)
                    throw DebridResolutionError.HostSupport(DisplayName(preferredProviderId), hosterUrl,
                        preferredProviderId != null && DebridHostSupport.Load(cfg, preferredProviderId, false) == null);
                ids = new[] { preferredProviderId };
            }
            if (ids.Length == 0) {
                if (EnabledIds(cfg).Length == 0) throw new Exception("Connect and enable a link service in Connections");
                // No list could be read (for example a busy network while renewing an
                // expired link mid-download). That is not a verdict on the host, so
                // report it as unchecked and retryable instead of "unsupported or exhausted".
                bool unknown = true;
                foreach (string id in EnabledIds(cfg))
                    if (DebridHostSupport.Load(cfg, id, false) != null) { unknown = false; break; }
                throw DebridResolutionError.HostSupport("Enabled services", hosterUrl, unknown);
            }
            Exception last = null;
            DebridResolutionError mirrorFailure = null;
            foreach (string selected in ids)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                if (unavailableProviders != null && unavailableProviders.Contains(selected)) continue;
                try
                {
                    if (progress != null) progress("Resolving with " + DisplayName(selected));
                    string resolved = UnrestrictWith(cfg, hosterUrl, selected, progress, cancel, parkWhenPreparing);
                    if (providerSelected != null) providerSelected(selected);
                    return resolved;
                }
                catch (OperationCanceledException) { throw; }
                catch (ProviderPreparationWaitException) { throw; }
                catch (Exception ex)
                {
                    var rejection = ex as DebridResolutionError;
                    if (parkWhenPreparing && rejection != null && (rejection.IsTransient || rejection.IsRateLimited))
                        throw new ProviderPreparationWaitException(selected, hosterUrl,
                            string.IsNullOrEmpty(rejection.ProviderCode) ? "provider retry" : rejection.ProviderCode,
                            rejection.RetryAfterSeconds, false, rejection);
                    // A timed-out create request may already have succeeded remotely.
                    // Only a definite provider rejection authorizes another provider attempt.
                    if (rejection == null || !rejection.CanTryProvider) throw;
                    if (unavailableProviders != null) unavailableProviders.Add(selected);
                    if (providerRejected != null) providerRejected(selected);
                    last = ex;
                    // An account rejection from a second service must not mask a
                    // usable service's host-specific failure. Do not ask the
                    // rejected service again for every mirror in this attempt.
                    if (rejection.CanTryMirror) mirrorFailure = rejection;
                }
            }
            if (mirrorFailure != null) throw mirrorFailure;
            if (last != null) throw last;
            throw new Exception("No enabled link service could resolve this mirror");
        }

        sealed class AccountCache { public string Status; public DateTime Until; }
        static readonly Dictionary<string, AccountCache> Accounts = new Dictionary<string, AccountCache>();
        public static string Probe(AppSettings cfg, string id)
        {
            if (cfg == null) return "Not connected";
            string token = ApiKey(cfg, id);
            string key;
            using (var sha = SHA256.Create()) key = id + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(token ?? "")));
            lock (Accounts) { AccountCache cached; if (Accounts.TryGetValue(key, out cached) && cached.Until > DateTime.UtcNow) return cached.Status; }
            string status = ProbeUncached(cfg, id);
            if (status.StartsWith("ERROR", StringComparison.Ordinal)) status = "Status unavailable; downloads will still be attempted";
            bool temporary = status.StartsWith("Status unavailable", StringComparison.Ordinal) ||
                status.StartsWith("RETRY:", StringComparison.Ordinal) || status.StartsWith("RATE_LIMITED:", StringComparison.Ordinal);
            lock (Accounts) { if (Accounts.Count > 16) Accounts.Clear(); Accounts[key] = new AccountCache { Status = status, Until = DateTime.UtcNow.AddSeconds(temporary ? 30 : 300) }; }
            return status;
        }

        sealed class ResolveGate
        {
            internal bool Busy;
            internal long Next, BlockedUntil;
            internal Exception Rejection;
        }
        static readonly Dictionary<string, ResolveGate> ResolveGates = new Dictionary<string, ResolveGate>();
        static readonly System.Diagnostics.Stopwatch ResolveClock = System.Diagnostics.Stopwatch.StartNew();

        public static string UnrestrictWith(AppSettings cfg, string hosterUrl, string id, Action<string> progress = null,
            Func<bool> cancel = null, bool parkWhenPreparing = false, bool refreshLease = false)
        {
            if (cfg == null || !IsSupported(id)) throw new Exception("No enabled link service");
            ResolveGate admission;
            lock (ResolveGates)
            {
                if (!ResolveGates.TryGetValue(id, out admission))
                    ResolveGates[id] = admission = new ResolveGate();
            }
            // A queued download must not start another create/unrestrict operation
            // while the same provider is preparing a link or asking us to back off.
            for (;;)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                lock (admission)
                {
                    long now = ResolveClock.ElapsedMilliseconds;
                    if (now < admission.BlockedUntil)
                    {
                        if (parkWhenPreparing)
                            throw new ProviderPreparationWaitException(id, hosterUrl, "provider cooldown",
                                (int)Math.Min(24 * 60 * 60, Math.Max(1, (admission.BlockedUntil - now + 999) / 1000)), false,
                                admission.Rejection);
                        throw admission.Rejection;
                    }
                    if (!admission.Busy && now >= admission.Next) { admission.Busy = true; break; }
                    if (parkWhenPreparing)
                    {
                        long due = admission.Busy ? now + 1000 : admission.Next;
                        throw new ProviderPreparationWaitException(id, hosterUrl, "provider request slot",
                            (int)Math.Max(1, Math.Min(60, (due - now + 999) / 1000)), false);
                    }
                }
                System.Threading.Thread.Sleep(50);
            }
            try { return UnrestrictAdmitted(cfg, hosterUrl, id, progress, cancel, parkWhenPreparing, refreshLease); }
            catch (DebridResolutionError ex)
            {
                if (ex.IsRateLimited)
                    lock (admission) {
                        admission.Rejection = ex;
                        admission.BlockedUntil = Math.Max(admission.BlockedUntil,
                            ResolveClock.ElapsedMilliseconds + Math.Min(24 * 60 * 60L, Math.Max(15L, ex.RetryAfterSeconds)) * 1000L);
                    }
                throw;
            }
            finally
            {
                lock (admission) { admission.Next = ResolveClock.ElapsedMilliseconds + 1000; admission.Busy = false; }
            }
        }

        static string UnrestrictAdmitted(AppSettings cfg, string hosterUrl, string id, Action<string> progress,
            Func<bool> cancel, bool parkWhenPreparing, bool refreshLease)
        {
            if (cfg == null) throw new Exception("No settings");
            if (!IsSupported(id) || !IsEnabled(cfg, id)) throw new Exception("This link service is not enabled in Connections");
            if (cancel != null && cancel()) throw new OperationCanceledException();
            var hosts = DebridHostSupport.Load(cfg, id, true);
            if (hosts == null || hosts.GetState(hosterUrl) != DebridHostState.Supported)
                throw DebridResolutionError.HostSupport(DisplayName(id), hosterUrl, hosts == null);
            if (refreshLease)
            {
                if (string.Equals(id, DeepbridId, StringComparison.OrdinalIgnoreCase))
                {
                    if (!cfg.HasDeepbrid) throw new Exception("Connect Deepbrid in Settings");
                    return DeepbridClient.Unrestrict(cfg.DeepbridApiKey, hosterUrl);
                }
                if (string.Equals(id, AllDebridId, StringComparison.OrdinalIgnoreCase))
                {
                    if (!cfg.HasAllDebrid) throw new Exception("Connect AllDebrid in Connections");
                    return AllDebridClient.Refresh(cfg.AllDebridApiKey, hosterUrl, progress, cancel);
                }
                if (string.Equals(id, PremiumizeId, StringComparison.OrdinalIgnoreCase))
                {
                    if (!cfg.HasPremiumize) throw new Exception("Connect Premiumize in Connections");
                    return PremiumizeClient.Unrestrict(cfg.PremiumizeApiKey, hosterUrl, progress, cancel);
                }
                if (string.Equals(id, TorBoxId, StringComparison.OrdinalIgnoreCase))
                {
                    if (!cfg.HasTorBox) throw new Exception("Connect TorBox in Connections");
                    return TorBoxClient.RefreshPrepared(cfg.TorBoxApiKey, hosterUrl, cancel);
                }
                if (!cfg.HasRealDebrid) throw new Exception("Pair Real-Debrid in Settings");
                return RealDebridClient.Unrestrict(cfg.RealDebridToken, hosterUrl,
                    cfg.RealDebridLocation, cancel, false);
            }
            if (string.Equals(id, DeepbridId, StringComparison.OrdinalIgnoreCase))
            {
                if (!cfg.HasDeepbrid) throw new Exception("Connect Deepbrid in Settings");
                return DeepbridClient.Unrestrict(cfg.DeepbridApiKey, hosterUrl);
            }
            if (string.Equals(id, AllDebridId, StringComparison.OrdinalIgnoreCase))
            {
                if (!cfg.HasAllDebrid) throw new Exception("Connect AllDebrid in Settings");
                if (parkWhenPreparing)
                {
                    var prepared = AllDebridClient.TryParkOrPrepare(cfg.AllDebridApiKey, hosterUrl, cancel);
                    if (prepared.Kind == AllDebridClient.PreparedPollKind.Ready)
                        return AllDebridClient.CompletePrepared(cfg.AllDebridApiKey, hosterUrl, prepared.Download);
                    if (prepared.Kind == AllDebridClient.PreparedPollKind.Preparing || prepared.Kind == AllDebridClient.PreparedPollKind.Transient)
                        throw new ProviderPreparationWaitException(id, hosterUrl, prepared.ProviderState,
                            prepared.RetryAfterSeconds, AllDebridClient.HasRemoteOperation(cfg.AllDebridApiKey, hosterUrl), prepared.Transport);
                    throw prepared.Transport ?? DebridResolutionError.FromResponse("AllDebrid", hosterUrl,
                        "{\"status\":\"error\",\"code\":\"LINK_DOWN\"}");
                }
                return AllDebridClient.Unrestrict(cfg.AllDebridApiKey, hosterUrl, progress, cancel);
            }
            if (string.Equals(id, PremiumizeId, StringComparison.OrdinalIgnoreCase))
            {
                if (!cfg.HasPremiumize) throw new Exception("Connect Premiumize in Settings");
                return PremiumizeClient.Unrestrict(cfg.PremiumizeApiKey, hosterUrl, progress, cancel);
            }
            if (string.Equals(id, TorBoxId, StringComparison.OrdinalIgnoreCase))
            {
                if (!cfg.HasTorBox) throw new Exception("Connect TorBox in Settings");
                if (parkWhenPreparing)
                {
                    var prepared = TorBoxClient.TryParkOrPrepare(cfg.TorBoxApiKey, hosterUrl, cancel, progress);
                    if (prepared.Kind == TorBoxClient.PreparedPollKind.Ready)
                        return TorBoxClient.Unrestrict(cfg.TorBoxApiKey, hosterUrl, progress, cancel);
                    if (prepared.Kind == TorBoxClient.PreparedPollKind.Preparing || prepared.Kind == TorBoxClient.PreparedPollKind.Transient)
                        throw new ProviderPreparationWaitException(id, hosterUrl, prepared.ProviderState,
                            prepared.RetryAfterSeconds, TorBoxClient.HasPreparedDownload(cfg.TorBoxApiKey, hosterUrl), prepared.Transport);
                    if (prepared.Kind == TorBoxClient.PreparedPollKind.Rejected || prepared.Kind == TorBoxClient.PreparedPollKind.Terminal)
                        throw TorBoxClient.PreparationFailure(hosterUrl, prepared);
                }
                return TorBoxClient.Unrestrict(cfg.TorBoxApiKey, hosterUrl, progress, cancel);
            }

            if (!cfg.HasRealDebrid) throw new Exception("Pair Real-Debrid in Settings");
            return RealDebridClient.Unrestrict(cfg.RealDebridToken, hosterUrl,
                cfg.RealDebridLocation, cancel, parkWhenPreparing);
        }

        internal static string RefreshSameProvider(AppSettings cfg, string hosterUrl, string providerId,
            Action<string> progress, Func<bool> cancel)
        {
            if (cfg == null || !IsEnabled(cfg, providerId))
                throw new Exception("The original link service is no longer enabled");
            if (cancel != null && cancel()) throw new OperationCanceledException();
            return UnrestrictWith(cfg, hosterUrl, providerId, progress, cancel, false, true);
        }

        static string ProbeUncached(AppSettings cfg, string id)
        {
            if (string.Equals(id, PremiumizeId, StringComparison.OrdinalIgnoreCase))
            {
                if (cfg == null || !cfg.HasPremiumize) return "Not connected";
                return PremiumizeClient.ProbeUser(cfg.PremiumizeApiKey);
            }
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
