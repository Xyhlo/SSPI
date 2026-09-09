using System;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Orbis
{
    internal static class TorBoxClient
    {
        const string Api = "https://api.torbox.app/v1/api";
        sealed class Pending { public string Id, FileId; public DateTime Until; }
        static readonly Dictionary<string, Pending> PendingDownloads = new Dictionary<string, Pending>();

        public static string Unrestrict(string token, string hostUrl, Action<string> progress = null, Func<bool> cancel = null)
        {
            if (string.IsNullOrEmpty(token))
                throw new Exception("TorBox token missing");
            if (string.IsNullOrEmpty(hostUrl))
                throw new Exception("Empty host URL");

            string json = null, key;
            using (var sha = SHA256.Create()) key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(token.Trim() + "\n" + hostUrl)));
            Pending pending;
            lock (PendingDownloads) { PendingDownloads.TryGetValue(key, out pending); }
            if (pending != null && pending.Until < DateTime.UtcNow) pending = null;
            if (pending == null) pending = LoadPending(key);
            if (cancel != null && cancel()) throw new OperationCanceledException();
            if (pending == null)
            {
            try
            {
                json = NetHttp.PostForm(Api + "/webdl/createwebdownload",
                    "link=" + Uri.EscapeDataString(hostUrl) + "&as_queued=false", 30000, null, token.Trim());
            }
            catch (Exception ex)
            {
                throw new Exception("TorBox create failed: " + ex.Message);
            }

            if (!JsonLite.GetBool(json, "success"))
                throw new Exception("TorBox: " + Error(json));
            string id = First(json, "webdownload_id", "webdownloadId", "id");
            if (string.IsNullOrEmpty(id))
                throw new Exception("TorBox returned no web download ID");
            if (!ValidId(id)) throw new Exception("TorBox returned an invalid web download ID");
            pending = new Pending { Id = id, Until = DateTime.UtcNow.AddHours(6) };
            SavePending(key, pending);
            lock (PendingDownloads) { if (PendingDownloads.Count >= 256) PendingDownloads.Clear(); PendingDownloads[key] = pending; }
            }

            string fileId = pending.FileId;
            var elapsed = Stopwatch.StartNew();
            for (int poll = 0; string.IsNullOrEmpty(fileId) && elapsed.Elapsed < TimeSpan.FromMinutes(10); poll++)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                string list = NetHttp.GetString(Api + "/webdl/mylist?id=" + Uri.EscapeDataString(pending.Id) + "&bypass_cache=true", 15000, null, token.Trim());
                if (!JsonLite.GetBool(list, "success")) { ForgetPending(key); throw new Exception("TorBox: " + Error(list)); }
                var files = JsonLite.ExtractObjectArray(list, "files");
                if (files.Count > 1) throw new Exception("TorBox returned multiple files for one package. Select a single package or archive volume.");
                if (files.Count == 1 && (JsonLite.GetBool(list, "download_finished") || JsonLite.GetBool(list, "download_present"))) { fileId = JsonLite.GetString(files[0], "id"); if (!ValidId(fileId)) throw new Exception("TorBox returned an invalid file ID"); pending.FileId = fileId; SavePending(key, pending); break; }
                string state = JsonLite.GetString(list, "download_state") ?? "preparing";
                if (state.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || state.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
                    { ForgetPending(key); throw new Exception("TorBox host download failed: " + state); }
                if (progress != null) progress("TorBox fetching from host · " + state + " · " + (int)elapsed.Elapsed.TotalSeconds + "s");
                // Catch a newly cached file quickly; long host transfers poll at the API's five-second cadence.
                Wait(PollDelayMs(poll), cancel);
            }
            if (string.IsNullOrEmpty(fileId)) throw new Exception("TorBox is still preparing this file. Retry from Downloads shortly.");
            string url = Api + "/webdl/requestdl?token=" + Uri.EscapeDataString(token.Trim()) +
                "&web_id=" + Uri.EscapeDataString(pending.Id) + "&file_id=" + Uri.EscapeDataString(fileId) + "&zip_link=false";
            try
            {
                json = NetHttp.GetString(url, 20000);
            }
            catch (Exception ex)
            {
                // Transport failure (timeout/5xx/429): keep the prepared IDs.
                throw new Exception("TorBox download link failed (preparation kept, retry reuses it): " + ex.Message);
            }

            string raw = (json ?? "").Trim().Trim('"');
            if (IsHttp(raw)) return raw;
            if (!JsonLite.GetBool(json, "success"))
            {
                // Only drop the preparation when the server confirms the job itself
                // is gone; transient failures keep IDs so the retry reuses them.
                // Underscores are normalized: DOWNLOAD_NOT_FOUND == "not found".
                string detail = ((Error(json) ?? "").ToLowerInvariant()).Replace('_', ' ');
                if (detail.Contains("not found") || detail.Contains("invalid") ||
                    detail.Contains("expired") || detail.Contains("deleted") ||
                    detail.Contains("no longer"))
                { ForgetPending(key); throw new Exception("TorBox: " + Error(json)); }
                throw new Exception("TorBox link temporarily unavailable (preparation kept, retry reuses it): " + Error(json));
            }
            string download = JsonLite.GetString(json, "data");
            if (!IsHttp(download)) throw new Exception("TorBox returned no download URL");
            if (cancel != null && cancel()) throw new OperationCanceledException();
            return download.Trim();
        }

        internal static int PollDelayMs(int poll) { return poll == 0 ? 1000 : poll == 1 ? 2000 : 5000; }
        static void Wait(int milliseconds, Func<bool> cancel)
        {
            for (int n = 0; n < milliseconds; n += 100) {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                Thread.Sleep(Math.Min(100, milliseconds - n));
            }
        }
        static bool ValidId(string id)
        {
            long value;
            return !string.IsNullOrEmpty(id) && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 0;
        }
        static string PendingPath(string key)
        { return Path.Combine(AppSettings.DataDir, "torbox-pending", key.Replace("-", "") + ".txt"); }
        static Pending LoadPending(string key)
        {
            try {
                string path = PendingPath(key);
                if (!File.Exists(path) || new FileInfo(path).Length > 1024) return null;
                string[] lines = File.ReadAllLines(path); long ticks;
                if (lines.Length != 3 || !ValidId(lines[0]) || (lines[1] != "" && !ValidId(lines[1])) ||
                    !long.TryParse(lines[2], out ticks) || ticks <= DateTime.UtcNow.Ticks || ticks > DateTime.UtcNow.AddHours(6).Ticks) return null;
                var pending = new Pending { Id = lines[0], FileId = lines[1], Until = new DateTime(ticks, DateTimeKind.Utc) };
                lock (PendingDownloads) { if (PendingDownloads.Count >= 256) PendingDownloads.Clear(); PendingDownloads[key] = pending; }
                return pending;
            } catch { return null; }
        }
        static void SavePending(string key, Pending pending)
        {
            try {
                string path = PendingPath(key), dir = Path.GetDirectoryName(path);
                Directory.CreateDirectory(dir);
                AtomicFile.WriteText(path, pending.Id + "\n" + (pending.FileId ?? "") + "\n" + pending.Until.Ticks.ToString(CultureInfo.InvariantCulture));
                var files = new DirectoryInfo(dir).GetFiles("*.txt");
                if (files.Length > 256) { Array.Sort(files, (a,b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc)); for (int i=0; i<files.Length-256; i++) files[i].Delete(); }
            } catch { } // An unavailable cache must not stop link resolution.
        }
        static void ForgetPending(string key)
        {
            lock (PendingDownloads) PendingDownloads.Remove(key);
            try { File.Delete(PendingPath(key)); } catch { }
        }

        public static string ProbeUser(string token)
        {
            try
            {
                string json = NetHttp.GetString(Api + "/user/me", 6000, null, (token ?? "").Trim());
                if (!JsonLite.GetBool(json, "success")) return "ERROR: " + Error(json);
                string user = First(json, "email", "base_email", "auth_id") ?? "?";
                string plan = JsonLite.GetString(json, "plan") ?? "?";
                return (plan == "0" ? "EXPIRED: " : "OK ") + "@" + user + " (plan " + plan + ")";
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }

        static string First(string json, params string[] keys)
        {
            foreach (string key in keys)
            {
                string value = JsonLite.GetString(json, key);
                if (!string.IsNullOrEmpty(value)) return value;
            }
            return null;
        }

        static string Error(string json)
        {
            return JsonLite.GetString(json, "detail") ??
                JsonLite.GetString(json, "error") ?? "request failed";
        }

        static bool IsHttp(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                (value.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 value.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }
    }
}
