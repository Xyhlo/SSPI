using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal sealed partial class PairServer
    {
        internal sealed class ServiceNotice
        {
            internal readonly string Message;
            internal readonly bool Saved;
            internal ServiceNotice(string message, bool saved) { Message = message; Saved = saved; }
        }

        sealed class ServiceValidation
        {
            internal string Key, State, Message;
        }

        static readonly string[] ServiceIds = { "real-debrid", "torbox", "alldebrid", "premiumize" };
        static readonly string[] ServiceFields = { "key", "tb_key", "ad_key", "pm_key" };
        readonly Dictionary<string, ServiceValidation> _serviceValidation = new Dictionary<string, ServiceValidation>();
        readonly Queue<ServiceNotice> _serviceNotices = new Queue<ServiceNotice>();
        int _serviceGeneration, _serviceRevision;
        string _serviceMessage = "";
        internal Func<string, string, string> ServiceProbe;

        internal bool TryTakeServiceNotice(out ServiceNotice notice)
        {
            lock (_lock)
            {
                notice = _serviceNotices.Count == 0 ? null : _serviceNotices.Dequeue();
                return notice != null;
            }
        }

        void PublishServiceNotice(string message, bool saved)
        {
            lock (_lock)
            {
                _serviceMessage = message;
                _serviceRevision++;
                if (_serviceNotices.Count >= 20) _serviceNotices.Dequeue();
                _serviceNotices.Enqueue(new ServiceNotice(message, saved));
            }
        }

        internal string ServiceSummary(string provider, bool configured)
        {
            lock (_lock)
            {
                ServiceValidation status;
                if (configured && _serviceValidation.TryGetValue(provider, out status) &&
                    status.Key == UnlockProviders.ApiKey(Settings, provider)) return status.Message;
                return configured ? "Key saved · not validated in this session" : "Link from your phone";
            }
        }

        string ServiceJsonFields()
        {
            lock (_lock)
            {
                var json = new StringBuilder(",\"service_revision\":" + _serviceRevision +
                    ",\"service_message\":\"" + JsonLite.Escape(_serviceMessage) + "\",\"service_status\":{");
                for (int i = 0; i < ServiceIds.Length; i++)
                {
                    string id = ServiceIds[i];
                    ServiceValidation value;
                    string state = "saved", message = "Key saved · not validated in this session";
                    string key = UnlockProviders.ApiKey(Settings, id);
                    if (string.IsNullOrEmpty(key)) { state = "missing"; message = "Not connected"; }
                    else if (_serviceValidation.TryGetValue(id, out value) && value.Key == key)
                    { state = value.State; message = value.Message; }
                    if (i > 0) json.Append(',');
                    json.Append('"').Append(id).Append("\":{\"state\":\"").Append(state)
                        .Append("\",\"message\":\"").Append(JsonLite.Escape(message)).Append("\"}");
                }
                return json.Append('}').ToString();
            }
        }

        void BeginServiceValidation(string body)
        {
            int generation = ++_serviceGeneration;
            foreach (var previous in _serviceValidation.Values)
                if (previous.State == "checking")
                { previous.State = "saved"; previous.Message = "Key saved · validation superseded; save again to retry"; }
            var jobs = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < ServiceIds.Length; i++)
            {
                string id = ServiceIds[i], key = UnlockProviders.ApiKey(Settings, id);
                if (string.IsNullOrEmpty(key) || (id != Settings.UnlockProviderId && string.IsNullOrWhiteSpace(ParseForm(body, ServiceFields[i])))) continue;
                _serviceValidation[id] = new ServiceValidation { Key = key, State = "checking", Message = "Key saved · validating…" };
                jobs.Add(new KeyValuePair<string, string>(id, key));
            }
            GotKey = true;
            PublishServiceNotice(jobs.Count == 0 ? "Services saved on PS4" : "Keys saved on PS4 · validating services", true);
            if (jobs.Count == 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (var job in jobs)
                {
                    lock (_lock) { if (_stop || generation != _serviceGeneration) return; }
                    string result;
                    try { result = ServiceProbe != null ? ServiceProbe(job.Key, job.Value) : ProbeSavedService(job.Key, job.Value); }
                    catch { result = "ERROR: validation unavailable"; }
                    string state, message;
                    ClassifyServiceValidation(result, out state, out message);
                    lock (_lock)
                    {
                        if (_stop || generation != _serviceGeneration) return;
                        if (job.Value != UnlockProviders.ApiKey(Settings, job.Key)) continue;
                        _serviceValidation[job.Key] = new ServiceValidation { Key = job.Value, State = state, Message = message };
                        PublishServiceNotice(UnlockProviders.DisplayName(job.Key) + ": " + message, false);
                    }
                }
            });
        }

        static string ProbeSavedService(string provider, string key)
        {
            var snapshot = new AppSettings { UnlockProviderId = provider, UseUnlockProvider = true };
            if (provider == "real-debrid") snapshot.RealDebridToken = key;
            else if (provider == "torbox") snapshot.TorBoxApiKey = key;
            else if (provider == "alldebrid") snapshot.AllDebridApiKey = key;
            else if (provider == "premiumize") snapshot.PremiumizeApiKey = key;
            return UnlockProviders.Probe(snapshot, provider);
        }

        internal static void ClassifyServiceValidation(string result, out string state, out string message)
        {
            string status = result ?? "";
            if (status.StartsWith("OK", StringComparison.OrdinalIgnoreCase))
            { state = "valid"; message = "Key validated · account ready"; }
            else if (status.StartsWith("VALID:", StringComparison.OrdinalIgnoreCase))
            { state = "plan_unknown"; message = "Key validated · plan status unverified"; }
            else if (status.StartsWith("FREE:", StringComparison.OrdinalIgnoreCase) || status.StartsWith("EXPIRED:", StringComparison.OrdinalIgnoreCase))
            { state = "limited"; message = "Key validated · account plan restricts downloads"; }
            else
            { state = "unavailable"; message = "Key saved · validation unavailable; check the key or retry"; }
        }
    }
}
