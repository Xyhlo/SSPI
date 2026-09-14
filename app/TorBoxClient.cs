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
            if (string.IsNullOrWhiteSpace(token))
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
                json = CreateWebDownload(token.Trim(), hostUrl, progress, cancel);
                var created = Object(Value(Response(json), "data"));
                string id = First(created, "webdownload_id", "webdownloadId", "id");
                if (!ValidId(id)) throw new Exception("TorBox returned an invalid web download ID");
                pending = new Pending { Id = id, FileId = ReadyFile(created), Until = DateTime.UtcNow.AddHours(6) };
                SavePending(key, pending);
                lock (PendingDownloads) { if (PendingDownloads.Count >= 256) PendingDownloads.Clear(); PendingDownloads[key] = pending; }
            }

            string fileId = pending.FileId;
            var elapsed = Stopwatch.StartNew();
            for (int poll = 0; string.IsNullOrEmpty(fileId) && elapsed.Elapsed < TimeSpan.FromMinutes(10); poll++)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                string list;
                try { list = NetHttp.GetString(Api + "/webdl/mylist?id=" + Uri.EscapeDataString(pending.Id) + "&bypass_cache=true", 15000, null, token.Trim()); }
                catch (Exception ex) { throw DebridResolutionError.FromTransport("TorBox", hostUrl, ex); }
                var response = Response(list);
                if (!Flag(response, "success"))
                {
                    if (MissingJob(Text(response, "error"))) ForgetPending(key);
                    throw DebridResolutionError.FromResponse("TorBox", hostUrl, list);
                }
                var job = FindJob(Value(response, "data"), pending.Id);
                fileId = ReadyFile(job);
                if (!string.IsNullOrEmpty(fileId)) { pending.FileId = fileId; SavePending(key, pending); break; }
                string state = Text(job, "download_state") ?? "preparing";
                if (state.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 || state.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0)
                    { ForgetPending(key); throw new Exception("TorBox host download failed: " + state); }
                if (progress != null) progress(PreparationProgress(job, elapsed.Elapsed));
                // Catch a newly cached file quickly; long host transfers poll at the API's five-second cadence.
                Wait(PollDelayMs(poll), cancel);
            }
            if (string.IsNullOrEmpty(fileId)) throw new Exception("TorBox is still preparing this file. Retry from Downloads shortly.");
            if (cancel != null && cancel()) throw new OperationCanceledException();
            string url = Api + "/webdl/requestdl?token=" + Uri.EscapeDataString(token.Trim()) +
                "&web_id=" + Uri.EscapeDataString(pending.Id) + "&file_id=" + Uri.EscapeDataString(fileId) + "&zip_link=false";
            try
            {
                json = NetHttp.GetString(url, 20000);
            }
            catch (Exception ex)
            {
                // Transport failure (timeout/5xx/429): keep the prepared IDs.
                throw DebridResolutionError.FromTransport("TorBox", hostUrl, ex);
            }

            string raw = (json ?? "").Trim().Trim('"');
            if (cancel != null && cancel()) throw new OperationCanceledException();
            if (IsHttp(raw))
            {
                DownloadTransferSettings.RememberProviderLimit(raw.Trim(), 4);
                return raw.Trim();
            }
            var linkResponse = Response(json);
            if (!Flag(linkResponse, "success"))
            {
                // Invalid/expired tokens do not invalidate the prepared download.
                if (MissingJob(Text(linkResponse, "error"))) ForgetPending(key);
                throw DebridResolutionError.FromResponse("TorBox", hostUrl, json);
            }
            string download = Text(linkResponse, "data");
            if (!IsHttp(download)) throw new Exception("TorBox returned no download URL");
            if (cancel != null && cancel()) throw new OperationCanceledException();
            DownloadTransferSettings.RememberProviderLimit(download.Trim(), 4);
            return download.Trim();
        }

        internal static int PollDelayMs(int poll) { return poll == 0 ? 1000 : poll == 1 ? 2000 : 5000; }
        static Dictionary<string, object> Response(string json)
        {
            try { return Object(PackageSourceJson.Parse(json)); }
            catch { throw new IOException("TorBox returned an invalid JSON response. Retry shortly."); }
        }
        static Dictionary<string, object> Object(object value)
        {
            var result = value as Dictionary<string, object>;
            if (result == null) throw new IOException("TorBox returned unexpected download metadata.");
            return result;
        }
        static object Value(Dictionary<string, object> data, string name)
        { object value; return data.TryGetValue(name, out value) ? value : null; }
        static string Text(Dictionary<string, object> data, string name)
        {
            object value = Value(data, name);
            return value is string || value is long || value is int ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        }
        static bool Flag(Dictionary<string, object> data, string name)
        { object value = Value(data, name); return value is bool && (bool)value; }
        static string First(Dictionary<string, object> data, params string[] names)
        {
            foreach (string name in names) { string value = Text(data, name); if (!string.IsNullOrEmpty(value)) return value; }
            return null;
        }
        static Dictionary<string, object> FindJob(object data, string id)
        {
            var item = data as Dictionary<string, object>;
            if (item != null)
            {
                string returnedId = First(item, "id", "webdownload_id");
                if (returnedId == null || returnedId == id) return item;
            }
            var items = data as List<object>;
            if (items != null) foreach (object candidate in items)
            {
                item = candidate as Dictionary<string, object>;
                if (item != null && First(item, "id", "webdownload_id") == id) return item;
            }
            throw new IOException("TorBox did not return the requested download. Retry shortly; preparation was kept.");
        }
        static string ReadyFile(Dictionary<string, object> job)
        {
            var files = Value(job, "files") as List<object>;
            if (files == null || files.Count == 0) return null;
            if (files.Count > 1) throw new IOException("TorBox returned multiple files for one package. Select a single package or archive volume.");
            if (!Flag(job, "download_finished") && !Flag(job, "download_present")) return null;
            string id = Text(Object(files[0]), "id");
            if (!ValidId(id)) throw new IOException("TorBox returned an invalid file ID");
            return id;
        }
        static bool MissingJob(string code)
        { return code == "ITEM_NOT_FOUND" || code == "DOWNLOAD_NOT_FOUND" || code == "WEB_DOWNLOAD_NOT_FOUND"; }
        static double Number(Dictionary<string, object> job, string name)
        {
            object value = Value(job, name);
            if (!(value is long) && !(value is double) && !(value is int)) return -1;
            double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsNaN(number) || double.IsInfinity(number) ? -1 : number;
        }
        internal static string PreparationProgress(Dictionary<string, object> job, TimeSpan elapsed)
        {
            string message = "TorBox preparing";
            double progress = Number(job, "progress"), speed = Number(job, "download_speed"), eta = Number(job, "eta");
            if (progress >= 0 && progress <= 1) message += " · " + (progress * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
            if (speed > 0) message += " · " + (speed / 1000000).ToString("0.0", CultureInfo.InvariantCulture) + " MB/s";
            if (eta > 0 && eta < 86400) message += " · ETA " + TimeSpan.FromSeconds(eta).ToString(@"h\:mm\:ss");
            else message += " · " + (int)elapsed.TotalSeconds + "s";
            return message;
        }
        static string CreateWebDownload(string token, string hostUrl, Action<string> progress, Func<bool> cancel)
        {
            for (int attempt = 0; ; attempt++)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                try
                {
                    string json;
                    try { json = NetHttp.PostForm(Api + "/webdl/createwebdownload",
                        "link=" + Uri.EscapeDataString(hostUrl) + "&as_queued=false", 30000, null, token); }
                    catch (Exception ex) { throw DebridResolutionError.FromTransport("TorBox", hostUrl, ex); }
                    if (!Flag(Response(json), "success")) throw DebridResolutionError.FromResponse("TorBox", hostUrl, json);
                    return json;
                }
                catch (DebridResolutionError ex)
                {
                    // AUTH_ERROR explicitly means the server failed to verify the key;
                    // unlike a transport timeout, it confirms no download was created.
                    if (attempt != 0 || ex.ProviderCode != "AUTH_ERROR") throw;
                    if (progress != null) progress("TorBox token verification unavailable · retrying once");
                    Wait(1500, cancel);
                }
            }
        }
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
                var response = Response(json);
                if (!Flag(response, "success"))
                    return "ERROR: " + DebridResolutionError.FromResponse("TorBox", Api, json).Message;
                var data = Object(Value(response, "data"));
                string user = First(data, "email", "base_email", "auth_id") ?? "?";
                string plan = Text(data, "plan") ?? "?";
                int planId;
                if (!int.TryParse(plan, out planId) || planId < 0) return "ERROR: TorBox returned no recognized account plan";
                return (plan == "0" ? "FREE: " : "OK ") + "@" + user + " (plan " + plan + ")";
            }
            catch (Exception ex)
            {
                return "ERROR: " + ex.Message;
            }
        }

        static bool IsHttp(string value)
        {
            return !string.IsNullOrEmpty(value) &&
                (value.TrimStart().StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 value.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        }
    }
}
