using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Orbis
{
    internal sealed partial class PairServer
    {
        // Set by SearchWindow. The snapshot never blocks the UI thread; actions reuse the console's queue actions.
        internal Func<List<DlItem>> QueueSnapshot;
        internal Func<string, string, string> QueueAction;
        string _lastQueueJson = "{\"ready\":false,\"items\":[]}";

        bool HandleQueue(NetworkStream stream, string method, string path, string prefix)
        {
            if (method == "GET" && path == prefix + "/queue")
            {
                List<DlItem> items = null;
                try { if (QueueSnapshot != null) items = QueueSnapshot(); }
                catch (Exception ex) { SspiLog.Write("network", "phone queue snapshot failed: " + ex.GetType().Name); }
                // A busy queue lock keeps the last answer; the phone polls again shortly.
                if (items != null) _lastQueueJson = QueueJson(items);
                WriteResponse(stream, 200, "application/json; charset=utf-8", _lastQueueJson);
                return true;
            }
            string actionPrefix = prefix + "/queue/";
            if (method != "POST" || !path.StartsWith(actionPrefix, StringComparison.Ordinal)) return false;
            string rest = path.Substring(actionPrefix.Length);
            int slash = rest.IndexOf('/');
            string id = slash > 0 ? Uri.UnescapeDataString(rest.Substring(0, slash)) : "";
            string action = slash > 0 ? rest.Substring(slash + 1) : "";
            if (!ValidQueueId(id) || (action != "pause" && action != "resume" && action != "retry" &&
                action != "install" && action != "remove" && action != "clear"))
            { WriteResponse(stream, 400, "text/plain", "Unknown download action."); return true; }
            if (QueueAction == null)
            { WriteResponse(stream, 400, "text/plain", "Downloads are not ready. Reopen Downloads on your PS4."); return true; }
            string error;
            try { error = QueueAction(id, action); }
            catch (Exception ex) { error = "Could not update the download: " + ex.Message; }
            if (error != null) { WriteResponse(stream, 400, "text/plain", error); return true; }
            WriteResponse(stream, 200, "application/json", "{\"requested\":true}");
            return true;
        }

        static bool ValidQueueId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 96) return false;
            foreach (char c in id)
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-' || c == '_' || c == '.')) return false;
            return true;
        }

        // Display fields only: no download URLs, source pages or archive passwords leave the PS4.
        internal static string QueueJson(List<DlItem> items)
        {
            var json = new StringBuilder("{\"ready\":true,\"items\":[");
            bool first = true;
            foreach (var i in items)
            {
                if (i == null || !ValidQueueId(i.Id)) continue;
                if (!first) json.Append(',');
                first = false;
                string image = i.ImageUrl ?? "";
                if (image.Length > 2048 || !(image.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                    image.StartsWith("http://", StringComparison.OrdinalIgnoreCase))) image = "";
                string provider = string.IsNullOrEmpty(i.ResolvedProviderId) ? "" : UnlockProviders.DisplayName(i.ResolvedProviderId);
                // Extraction and verification both run as Finalizing; the phone needs to know which.
                string activity = SearchWindow.Extracting(i) ? "extracting" :
                    i.State == DlState.Finalizing && i.StatsPhase == "validating" ? "verifying" : "";
                json.Append("{\"id\":\"").Append(JsonLite.Escape(i.Id))
                    .Append("\",\"title\":\"").Append(JsonLite.Escape(i.Name ?? ""))
                    .Append("\",\"titleId\":\"").Append(CustomCovers.ValidId(i.TitleId) ? i.TitleId : "")
                    .Append("\",\"kind\":\"").Append(JsonLite.Escape((i.Kind ?? "").ToLowerInvariant()))
                    .Append("\",\"state\":\"").Append(i.State.ToString().ToLowerInvariant())
                    .Append("\",\"done\":").Append(Math.Max(0, i.Done).ToString(CultureInfo.InvariantCulture))
                    .Append(",\"total\":").Append(Math.Max(0, i.Total).ToString(CultureInfo.InvariantCulture))
                    .Append(",\"bps\":").Append(((long)Math.Max(0, i.BytesPerSec)).ToString(CultureInfo.InvariantCulture))
                    .Append(",\"eta\":").Append(Math.Max(0, i.EtaSeconds).ToString(CultureInfo.InvariantCulture))
                    .Append(",\"status\":\"").Append(JsonLite.Escape(i.StatusText ?? ""))
                    .Append("\",\"error\":\"").Append(JsonLite.Escape(i.Error ?? ""))
                    .Append("\",\"image\":\"").Append(JsonLite.Escape(image))
                    .Append("\",\"parts\":").Append(ArchiveParts(i.ArchiveVolumes).ToString(CultureInfo.InvariantCulture))
                    .Append(",\"archive\":").Append(SearchWindow.IsArchiveItem(i) ? "true" : "false")
                    .Append(",\"activity\":\"").Append(activity).Append('"')
                    .Append(",\"provider\":\"").Append(JsonLite.Escape(provider))
                    .Append("\",\"after\":\"").Append(JsonLite.Escape(i.InstallOrderReady ? "" : i.InstallAfterId ?? ""))
                    .Append("\",\"password\":").Append(SearchWindow.NeedsArchivePassword(i) ? "true" : "false")
                    .Append(",\"parked\":").Append(i.ParkedForProvider ? "true" : "false")
                    .Append('}');
            }
            return json.Append("]}").ToString();
        }

        static int ArchiveParts(string volumes)
        {
            if (string.IsNullOrEmpty(volumes)) return 0;
            int count = 0;
            foreach (string line in volumes.Split('\n')) if (line.Length > 0) count++;
            return count;
        }
    }
}
