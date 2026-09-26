using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal sealed partial class PairServer
    {
        public const int Port = 8741;
        public bool Running { get; private set; }
        public string OneTimeToken { get; private set; }
        public string PairUrl { get; private set; }
        public string LocalIp { get; private set; }
        public string Status { get; private set; }
        public bool GotKey { get; private set; }
        public string ReceivedKey { get; private set; }
        public string ReceivedDeepbridKey { get; private set; }
        public string ReceivedAllDebridKey { get; private set; }
        public string ReceivedTorBoxKey { get; private set; }
        public string ReceivedProvider { get; private set; }
        public string ReceivedSourceUrl { get; private set; }
        public int ReceivedMbps { get; private set; }
        public bool GotWallpaper { get; private set; }
        public byte[] ReceivedWallpaper { get; private set; }
        public string ReceivedWallpaperType { get; private set; }
        public DateTime ExpiresUtc { get; private set; }

        public AppSettings Settings;
        public string InstalledSources = "";
        public string SourceStatus = "";
        public string PendingSource;
        public int Revision;
        public int ConnectedRevision;
        internal volatile string PhoneError = "";
        public string SourceChoicesJson = "[]";
        public Func<string, bool, string> SetSourceEnabled;
        public Func<string, string> QueueDownloadLink;
        public Func<List<ArchiveVolume>, string> QueueDownloadArchive;
        readonly object _downloadLock = new object();
        readonly HashSet<string> _queuedLinks = new HashSet<string>(StringComparer.Ordinal);

        sealed class DownloadBatch
        {
            internal readonly List<string> Urls = new List<string>();
            internal List<ArchiveVolume> Volumes;
        }

        static List<DownloadBatch> DownloadBatches(List<string> urls, bool multipart)
        {
            var result = new List<DownloadBatch>();
            var named = new Dictionary<string, DownloadBatch>(StringComparer.OrdinalIgnoreCase);
            if (multipart)
            {
                if (urls.Count < 2) throw new IOException("Send all RAR parts together, in volume order.");
                var batch = new DownloadBatch { Volumes = new List<ArchiveVolume>() };
                bool allNamed = urls.TrueForAll(url => ArchiveVolumeSet.FileName(url, "").Length > 0);
                for (int i = 0; i < urls.Count; i++) {
                    batch.Urls.Add(urls[i]);
                    batch.Volumes.Add(new ArchiveVolume { Url = urls[i], AccessType = "Unknown",
                        Name = allNamed ? ArchiveVolumeSet.FileName(urls[i], "") : "archive.part" + (i + 1) + ".rar" });
                }
                batch.Volumes = ArchiveVolumeSet.Validate(batch.Volumes);
                result.Add(batch); return result;
            }
            foreach (string url in urls)
            {
                string name = ArchiveVolumeSet.FileName(url, ""), set; int index;
                DownloadBatch batch;
                if (ArchiveVolumeSet.TryIndex(name, out set, out index))
                {
                    if (!named.TryGetValue(set, out batch)) {
                        batch = new DownloadBatch { Volumes = new List<ArchiveVolume>() };
                        named.Add(set, batch); result.Add(batch);
                    }
                    batch.Urls.Add(url);
                    batch.Volumes.Add(new ArchiveVolume { Url = url, Name = name, AccessType = "Unknown" });
                }
                else { batch = new DownloadBatch(); batch.Urls.Add(url); result.Add(batch); }
            }
            foreach (var batch in result)
                if (batch.Volumes != null) {
                    batch.Volumes = ArchiveVolumeSet.Validate(batch.Volumes);
                    if (batch.Volumes.Count == 1) batch.Volumes = null;
                }
            return result;
        }

        internal string QueueDownloads(string links, bool multipart = false)
        {
            if (QueueDownloadLink == null) throw new IOException("Downloads are not ready. Reopen Downloads on your PS4.");
            var unique = new HashSet<string>(StringComparer.Ordinal);
            var validated = new List<string>();
            int repeated = 0;
            foreach (string line in (links ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string url = line.Trim();
                if (url.Length == 0) continue;
                if (url.Length > 8192 || !CloudCatalog.ValidLink(url))
                    throw new IOException("Enter one complete HTTP or HTTPS link per line. Nothing in this batch was queued.");
                if (unique.Add(url)) validated.Add(url); else repeated++;
                if (validated.Count > 50) throw new IOException("Send up to 50 links at a time. Nothing in this batch was queued.");
            }
            if (validated.Count == 0) throw new IOException("Paste at least one download link.");
            var batches = DownloadBatches(validated, multipart);
            if (batches.Exists(batch => batch.Volumes != null) && QueueDownloadArchive == null)
                throw new IOException("Archive downloads are not ready. Reopen Downloads on your PS4.");
            int queued = 0, duplicate = repeated, failed = 0;
            lock (_downloadLock)
            {
                foreach (var batch in batches)
                {
                    if (batch.Urls.TrueForAll(url => _queuedLinks.Contains(url))) { duplicate += batch.Urls.Count; continue; }
                    try
                    {
                        string id = batch.Volumes == null ? QueueDownloadLink(batch.Urls[0]) : QueueDownloadArchive(batch.Volumes);
                        if (string.IsNullOrEmpty(id)) { failed++; continue; }
                        // Bound session memory. The persisted download queue also deduplicates.
                        if (_queuedLinks.Count >= 500) _queuedLinks.Clear();
                        foreach (string url in batch.Urls) _queuedLinks.Add(url);
                        queued++;
                    }
                    catch { failed++; }
                }
            }
            return "{\"queued\":" + queued + ",\"duplicates\":" + duplicate + ",\"failed\":" + failed + "}";
        }
        TcpListener _listener;
        Thread _thread;
        volatile bool _stop;
        readonly object _lock = new object();

        public void Start(int minutes = 5)
        {
            if (Running)
            {
                // DHCP can reassign the LAN address mid-session: refresh the
                // advertised URL instead of serving a stale one.
                RefreshLanAddress();
                return;
            }
            string tokenPath = Path.Combine(AppSettings.DataDir, "pair-token.txt");
            Directory.CreateDirectory(AppSettings.DataDir);
            OneTimeToken = Guid.NewGuid().ToString("N");
            try { File.Delete(tokenPath); } catch { }
            LocalIp = DetectLanIp();
            if (string.IsNullOrEmpty(LocalIp) || LocalIp == "0.0.0.0")
            {
                Status = "No LAN address — connect the PS4 to the network, then restart pairing";
                PairUrl = "";
                Running = false;
                return;
            }
            PairUrl = "http://" + LocalIp + ":" + Port + "/pair/" + OneTimeToken;
            ExpiresUtc = DateTime.UtcNow.AddMinutes(Math.Max(1, Math.Min(30, minutes)));
            GotKey = false;
            PhoneError = "";
            ReceivedKey = null;
            ReceivedDeepbridKey = null;
            ReceivedAllDebridKey = null;
            ReceivedTorBoxKey = null;
            ReceivedProvider = null; ReceivedSourceUrl = null; ReceivedMbps = 0;
            GotWallpaper = false;
            ReceivedWallpaper = null;
            ReceivedWallpaperType = null;
            Status = "Listening " + PairUrl;
            _stop = false;
            try
            {
                _listener = new TcpListener(IPAddress.Any, Port);
                _listener.Start();
            }
            catch (Exception)
            {
                Status = "Pairing port unavailable. Close other SSPI instances and retry.";
                PairUrl = "";
                Running = false;
                return;
            }
            Running = true;
            _thread = new Thread(ListenLoop) { IsBackground = true };
            _thread.Start();
        }

        public void RefreshLanAddress()
        {
            try
            {
                string current = DetectLanIp();
                if (!string.IsNullOrEmpty(current) && current != "0.0.0.0" &&
                    !string.Equals(current, LocalIp, StringComparison.Ordinal))
                {
                    LocalIp = current;
                    PairUrl = "http://" + LocalIp + ":" + Port + "/pair/" + OneTimeToken;
                    Status = "Listening " + PairUrl;
                }
            }
            catch { }
        }

        public void Stop()
        {
            _stop = true;
            Running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
            Status = "Stopped";
        }

        public bool Expired
        {
            get { return DateTime.UtcNow > ExpiresUtc; }
        }

        public int RemainingSeconds
        {
            get { return Math.Max(0, (int)Math.Ceiling((ExpiresUtc - DateTime.UtcNow).TotalSeconds)); }
        }

        void ListenLoop()
        {
            try
            {
                Status = "Pair server on " + PairUrl;
                while (!_stop && !Expired)
                {
                    if (!_listener.Pending())
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    TcpClient client = null;
                    try
                    {
                        client = _listener.AcceptTcpClient();
                        client.ReceiveTimeout = 5000;
                        client.SendTimeout = 15000;
                        HandleClient(client);
                    }
                    catch (Exception ex)
                    {
                        Status = "Client err: " + ex.GetType().Name;
                    }
                    finally
                    {
                        try { if (client != null) client.Close(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Status = "Listen fail: " + ex.Message;
                Running = false;
            }
            finally
            {
                try { if (_listener != null) _listener.Stop(); } catch { }
                Running = false;
            }
        }

        public byte[] TakeWallpaper()
        {
            lock (_lock)
            {
                byte[] data = ReceivedWallpaper;
                ReceivedWallpaper = null;
                GotWallpaper = false;
                return data;
            }
        }

        void HandleClient(TcpClient client)
        {
            using (var stream = client.GetStream())
            {
                string headers;
                byte[] extra;
                if (!ReadHeaders(stream, out headers, out extra)) return;
                string first = headers.Split('\n')[0];
                string method = "GET";
                string path = "/";
                var parts = first.Split(' ');
                if (parts.Length >= 2)
                {
                    method = parts[0].Trim().ToUpperInvariant();
                    path = parts[1].Trim();
                }
                bool wallpaper = method == "POST" && path == "/pair/" + OneTimeToken + "/wallpaper";
                bool cover = method == "POST" && path.StartsWith("/pair/" + OneTimeToken + "/cover/", StringComparison.Ordinal) && !path.EndsWith("/restore", StringComparison.Ordinal);
                int contentLength;
                if (wallpaper || cover) {
                    if (!int.TryParse(ParseHeader(headers, "Content-Length"), out contentLength) || (cover ? contentLength != CustomCovers.ByteCount && contentLength != CustomCovers.UploadByteCount : contentLength != PixelBackground.ByteCount))
                        contentLength = -1;
                } else contentLength = ParseContentLength(headers);
                if (contentLength < 0 || ParseHeader(headers, "Transfer-Encoding") != null)
                { WriteResponse(stream, 400, "text/plain", "Invalid request length"); return; }
                string contentType = ParseHeader(headers, "Content-Type") ?? "";
                byte[] bodyBytes = ReadBody(stream, extra, contentLength);
                if (bodyBytes == null)
                { WriteResponse(stream, 400, "text/plain", "Incomplete request; nothing saved"); return; }
                string body = wallpaper || cover ? "" : Encoding.UTF8.GetString(bodyBytes);

                string pairPrefix = "/pair/" + OneTimeToken;
                string statusPath = "/status/" + OneTimeToken;
                if (method == "GET" && path == statusPath)
                {
                    string state = GotKey ? "ok" : (Expired ? "expired" : "pending");
                    if (GotWallpaper && state == "pending") state = "wallpaper";
                    string json = "{\"state\":\"" + state + "\",\"remaining\":" + RemainingSeconds +
                        ",\"wallpaper\":" + (GotWallpaper ? "true" : "false") + "}";
                    WriteResponse(stream, 200, "application/json; charset=utf-8", json);
                    return;
                }

                if (Expired)
                {
                    WriteResponse(stream, 410, "text/html; charset=utf-8",
                        PairResultHtml("Pairing timed out",
                            "This session expired. Return to SSPI and open a new QR code.",
                            "TIMEOUT", true, null));
                    return;
                }
                if (method == "GET" && (path == pairPrefix || path.StartsWith(pairPrefix + "?")))
                {
                    WriteResponse(stream, 200, "text/html; charset=utf-8", PairHtml());
                    return;
                }
                if (method == "POST" && path == pairPrefix + "/connected")
                {
                    Interlocked.Increment(ref ConnectedRevision);
                    WriteResponse(stream, 200, "application/json", "{\"connected\":true}"); return;
                }
                if (HandleLibrary(stream, method, path, pairPrefix, contentType, bodyBytes)) return;
                if (wallpaper)
                {
                    if (contentType != "application/octet-stream") { WriteResponse(stream, 400, "text/plain", "Use the Appearance page to upload a background."); return; }
                    string error = PixelBackground.Save(Settings, bodyBytes);
                    if (error == null) Interlocked.Increment(ref Revision);
                    PublishPhoneNotice(error ?? "Background saved from phone", error != null);
                    WriteResponse(stream, error == null ? 200 : 400, error == null ? "application/json" : "text/plain", error ?? "{\"saved\":true}");
                    return;
                }
                if (Settings != null && method == "GET" && path == pairPrefix + "/wallpaper")
                {
                    try { WriteResponse(stream, 200, "application/json", "{\"rgba\":\"" + Convert.ToBase64String(PixelBackground.ReadPixels(Settings.BackgroundImagePath)) + "\"}"); }
                    catch (IOException) { WriteResponse(stream, 404, "text/plain", "Upload a background picture first."); }
                    return;
                }
                if (method == "POST" && path == pairPrefix + "/background-mask")
                {
                    WriteResponse(stream, 200, "application/json", "{\"mask\":\"" + Convert.ToBase64String(PixelBackground.Mask(ParseForm(body, "pattern"))) + "\"}");
                    return;
                }
                if (Settings != null && method == "GET" && path == pairPrefix + "/config")
                { WriteResponse(stream, 200, "application/json", ConfigJson()); return; }
                if (method == "POST" && path == pairPrefix + "/downloads")
                {
                    try { WriteResponse(stream, 200, "application/json", QueueDownloads(ParseForm(body, "links"), ParseForm(body, "multipart") == "1")); }
                    catch (IOException ex) { WriteResponse(stream, 400, "text/plain", ex.Message); }
                    catch (InvalidDataException ex) { WriteResponse(stream, 400, "text/plain", ex.Message); }
                    return;
                }
                if (Settings != null && method == "POST" &&
                    (path == pairPrefix + "/services" || path == pairPrefix + "/source" || path == pairPrefix + "/appearance" || path == pairPrefix + "/source-toggle"))
                {
                    string error = SaveConfiguration(path.Substring(pairPrefix.Length), body);
                    if (error != null) PublishPhoneNotice(error, true);
                    else if (path != pairPrefix + "/services") PublishPhoneNotice("Phone settings saved", false);
                    WriteResponse(stream, error == null ? 200 : 400, error == null ? "application/json" : "text/plain", error ?? "{\"saved\":true}"); return;
                }
                if (method == "GET" && path == "/")
                {
                    WriteResponse(stream, 200, "text/plain", "SSPI pair server");
                    return;
                }
                WriteResponse(stream, 404, "text/plain", "not found");
            }
        }

        static bool ReadHeaders(NetworkStream stream, out string headers, out byte[] extra)
        {
            headers = "";
            extra = new byte[0];
            var ms = new MemoryStream();
            int match = 0;
            var one = new byte[1];
            while (ms.Length < 8192)
            {
                int n = stream.Read(one, 0, 1);
                if (n <= 0) return false;
                ms.WriteByte(one[0]);
                if (one[0] == (match == 0 || match == 2 ? (byte)'\r' : (byte)'\n')) match++;
                else match = one[0] == (byte)'\r' ? 1 : 0;
                if (match == 4)
                {
                    headers = Encoding.ASCII.GetString(ms.ToArray());
                    extra = new byte[0];
                    return true;
                }
            }
            return false;
        }

        static int ParseContentLength(string headers)
        {
            string v = ParseHeader(headers, "Content-Length");
            int n;
            if (v == null) return 0;
            return int.TryParse(v, out n) && n >= 0 && n <= 65536 ? n : -1;
        }

        static string ParseHeader(string headers, string name)
        {
            if (string.IsNullOrEmpty(headers)) return null;
            string[] lines = headers.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                if (line.Substring(0, c).Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(c + 1).Trim();
            }
            return null;
        }

        static byte[] ReadBody(NetworkStream stream, byte[] extra, int contentLength)
        {
            if (contentLength <= 0) return extra ?? new byte[0];
            byte[] body = new byte[contentLength];
            int got = 0;
            if (extra != null && extra.Length > 0)
            {
                int take = Math.Min(extra.Length, contentLength);
                Buffer.BlockCopy(extra, 0, body, 0, take);
                got = take;
            }
            while (got < contentLength)
            {
                int n = stream.Read(body, got, contentLength - got);
                if (n <= 0) break;
                got += n;
            }
            if (got == contentLength) return body;
            return null;
        }

        static string ParseForm(string body, string name)
        {
            if (string.IsNullOrEmpty(body)) return null;
            foreach (var part in body.Split('&'))
            {
                var kv = part.Split(new[] { '=' }, 2);
                if (kv.Length == 2 && Uri.UnescapeDataString(kv[0].Replace("+", " "))
                        .Equals(name, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(kv[1].Replace("+", " "));
            }
            return null;
        }

        static string _pairBrandMarkup;
        static string PairBrandMarkup()
        {
            if (_pairBrandMarkup != null) return _pairBrandMarkup;
            _pairBrandMarkup = "";
            try
            {
                string path = "/app0/assets/images/sspi-pair-logo.png";
                if (!File.Exists(path)) path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "assets/images/sspi-pair-logo.png");
                if (File.Exists(path) && new FileInfo(path).Length <= 65536)
                    _pairBrandMarkup = "<img width='54' height='54' alt='' src='data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path)) + "'>";
            }
            catch { }
            return _pairBrandMarkup;
        }

        string PairHtml()
        {
            return @"<!doctype html><html lang=""en""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1,viewport-fit=cover""><title>SSPI · Console setup</title><style>
:root{color-scheme:dark;font:16px system-ui;background:#101010;color:#eee}*{box-sizing:border-box}body{margin:0}main{max-width:960px;margin:36px auto;padding:24px}header{display:flex;justify-content:space-between;align-items:center;border-bottom:1px solid #353535;padding-bottom:24px}.brand{font-size:25px;letter-spacing:3px;font-weight:750}.muted,small{color:#aaa}h1{font-size:36px;margin:32px 0 8px}nav{display:flex;gap:8px;margin:28px 0;flex-wrap:wrap}button{border:1px solid #484848;border-radius:12px;background:#292929;color:#eee;padding:13px 20px;cursor:pointer;font:inherit}nav button.active,button.save{background:#e3e3df;color:#161616;border-color:#e3e3df}section[hidden]{display:none}.card{border:1px solid #393939;border-radius:18px;padding:24px;background:#202020;margin:16px 0}.providers{display:grid;grid-template-columns:1fr 1fr;gap:18px}.providers .card{margin:0}.logo{display:flex;gap:12px;align-items:center;font-size:24px;font-weight:700;margin-bottom:20px}.mark{display:grid;place-items:center;border-radius:10px;background:#333;width:46px;height:46px;color:#89c7a7}.rd{color:#f1a14b}label{display:block;margin:18px 0 9px}input,select,textarea{width:100%;font:inherit;color:#eee;background:#141414;border:1px solid #494949;border-radius:10px;padding:13px}input[type=checkbox]{width:auto}input[type=color]{height:50px}a{color:#ccc}#notice{position:sticky;top:12px;z-index:5;min-height:28px;color:#d7f3e2;background:#183225;border:1px solid #6cbf95;border-radius:12px;padding:16px;margin:20px 0;box-shadow:0 4px 20px #0008}#notice:empty{display:none}.service-state{display:block;min-height:40px}.service-state[data-state=valid]{color:#9bd2b4}.service-state[data-state=limited],.service-state[data-state=plan_unknown],.service-state[data-state=unavailable]{color:#f0ce89}button:disabled{opacity:.65;cursor:wait}#sources{white-space:pre-wrap;line-height:1.9}.actions{display:flex;gap:12px;align-items:center;margin-top:20px;flex-wrap:wrap}footer{color:#888;font-size:13px;margin-top:36px}@media(max-width:640px){main{margin:0;padding:20px}.providers{grid-template-columns:1fr}h1{font-size:30px}header .muted{font-size:12px}}
#coverEditor{scroll-margin-top:90px}#coverPreview:not([hidden]),#coverIconPreview:not([hidden]){display:block}
:root{--accent:#bca4e8;--bg:#111214;--panel:#1c1d20;--line:#38393d;background:var(--bg);font:16px/1.5 system-ui,-apple-system,sans-serif}body{background:radial-gradient(ellipse at 80% 0,#242029 0,transparent 55%);min-height:100vh}main{max-width:920px;margin:0 auto;padding:24px 24px 60px}header{padding:8px 0 20px;border-color:var(--line)}.brand{font-size:21px;letter-spacing:2px;font-weight:600}.brand img{width:38px;height:38px}header .muted{font-size:12px;letter-spacing:.04em}h1{font-size:28px;line-height:1.2;margin:26px 0 8px;font-weight:500}h2{font-size:22px;font-weight:500;margin:0 0 12px}p{color:#aaa;line-height:1.5}nav{position:sticky;top:0;z-index:6;display:flex;flex-wrap:nowrap;gap:8px;margin:22px -4px 18px;padding:8px 4px;background:#141517;overflow-x:auto;scrollbar-width:none;overscroll-behavior-x:contain;touch-action:pan-x}nav::-webkit-scrollbar{display:none}nav button{flex:0 0 auto;min-height:48px;padding:12px 16px;border:0;border-radius:0;border-bottom:2px solid transparent;background:transparent;color:#aaa}nav button.active{background:transparent;color:#fff;border-color:var(--accent)}button{touch-action:manipulation;min-height:48px;font-size:16px;border-radius:10px;background:#292a2e;border-color:#45464c}button.save{background:#e4e4e1;border-color:#e4e4e1;color:#191a1c}button:focus-visible,input:focus-visible,select:focus-visible,textarea:focus-visible{outline:2px solid var(--accent);outline-offset:3px}button:disabled{opacity:.45}.card{background:var(--panel);border:1px solid var(--line);border-radius:14px;padding:20px;margin:16px 0;min-width:0}.providers{gap:12px}.logo{font-size:20px;font-weight:500;gap:10px;margin-bottom:10px}.mark{width:34px;height:34px;border-radius:7px;font-size:12px;color:var(--accent)}input,select,textarea{background:#151619;border-color:#45464b;min-height:48px}input[type=checkbox]{width:22px;height:22px;min-height:0;margin:0 10px 0 0;vertical-align:middle;accent-color:var(--accent)}input[type=range]{accent-color:var(--accent);padding:0;cursor:pointer}input[type=color]{padding:6px;cursor:pointer}label{font-size:14px;color:#c8c8cd;margin:16px 0 8px}.service-state{min-height:24px;font-size:13px}#notice{position:fixed;top:auto;bottom:max(18px,env(safe-area-inset-bottom));left:50%;transform:translateX(-50%);width:calc(100% - 32px);max-width:600px;margin:0;padding:12px 18px;background:#292a2e;border:1px solid #777;color:#eee;font-size:14px;z-index:20;box-shadow:0 8px 28px #0009}#pageError{padding:14px 16px;border:1px solid #c97a8a;border-radius:10px;background:#341e27;color:#f4c7d0;overflow-wrap:anywhere;white-space:pre-line}.actions{gap:10px;margin-top:16px}.actions input{flex:1;min-width:150px}footer{font-size:12px;color:#8c8c94;margin-top:24px}.preview-pair{display:grid;grid-template-columns:1fr 1fr;gap:16px;align-items:start;margin:20px 0}.preview-pair figure{margin:0;min-width:0}.preview-pair figcaption{font-size:12px;color:#aaa;margin:8px 0}.preview-pair canvas{display:block;width:100%;height:auto;max-width:256px;border-radius:6px}#coverEditor{scroll-margin-top:84px}#libraryGrid{grid-template-columns:repeat(auto-fill,minmax(140px,1fr))!important}#libraryGrid button{padding:12px}.effect-controls{display:grid;grid-template-columns:minmax(0,1fr) 90px;gap:14px}.effect-controls label{margin-top:10px}.back{margin-bottom:18px;background:transparent}.editor-actions{position:sticky;bottom:0;background:var(--panel);padding:12px 0 max(12px,env(safe-area-inset-bottom));z-index:4}.editor-actions button{flex:1}#coverHint{font-size:13px}[hidden]{display:none!important}@media(max-width:640px){main{padding:12px 16px 52px}header{padding-bottom:14px}h1{font-size:25px;margin-top:20px}.card{padding:16px}.providers{grid-template-columns:1fr}nav{margin-left:-16px;margin-right:-16px;padding-left:12px;padding-right:12px}nav button{padding:12px}#libraryGrid{grid-template-columns:repeat(2,minmax(0,1fr))!important;gap:10px!important}#libraryGrid img{height:150px!important}.preview-pair{gap:12px}.actions>button{flex-grow:1}header .muted{font-size:11px}.actions small{width:100%}}@media(prefers-reduced-motion:reduce){*{scroll-behavior:auto!important;animation:none!important}}
.providers{display:block}.providers .card{margin:10px 0;padding:0}.provider summary{cursor:pointer;position:relative;padding:18px 40px 16px 18px;list-style:none;min-height:74px}.provider summary::-webkit-details-marker{display:none}.provider summary:after{content:'›';position:absolute;right:20px;top:21px;color:#aaa;font-size:22px}.provider[open] summary:after{transform:rotate(90deg)}.provider .logo{margin-bottom:4px}.provider .service-state{margin-left:44px}.provider-fields{padding:0 18px 18px}.provider[open] summary{border-bottom:1px solid #393939}.provider-fields a{display:inline-flex;align-items:center;min-height:44px}#notice{pointer-events:none}.effect-controls+label{margin-top:18px}</style></head><body><main><header><div class=""brand"" style=""display:flex;align-items:center;gap:12px"">__SSPI_BRAND__SSPI</div><span class=""muted"">PS4 · PHONE COMPANION</span></header><h1>Your PS4, in your hands.</h1><p class=""muted"">Manage downloads, artwork and settings.</p><nav aria-label=""Phone sections""><button class=""active"" data-tab=""services"">Services</button><button data-tab=""packages"">Sources</button><button data-tab=""appearance"">Appearance</button><button data-tab=""downloads"">Downloads</button><button data-tab=""library"">Library</button></nav><div id=""pageError"" role=""alert"" hidden></div><div id=""notice"" role=""status"" aria-live=""polite"">Loading saved configuration…</div>
<section id=""services""><div class=""providers""><details class=""card provider""><summary><div class=""logo""><span class=""mark"">RD</span>Real-Debrid</div><small class=""service-state"" id=""rdSaved""></small></summary><div class=""provider-fields""><label for=""key"">API key</label><input type=""password"" id=""key"" autocomplete=""off"" placeholder=""Paste key; blank keeps saved key""><p><a href=""https://real-debrid.com/apitoken"" target=""_blank"" rel=""noreferrer"">Get your API key ↗</a></p></div></details><details class=""card provider""><summary><div class=""logo""><span class=""mark"">TB</span>TorBox</div><small class=""service-state"" id=""tbSaved""></small></summary><div class=""provider-fields""><label for=""tb_key"">API key</label><input type=""password"" id=""tb_key"" autocomplete=""off"" placeholder=""Paste key; blank keeps saved key""><p><a href=""https://torbox.app/settings"" target=""_blank"" rel=""noreferrer"">Get your API key ↗</a></p></div></details><details class=""card provider""><summary><div class=""logo""><span class=""mark"">AD</span>AllDebrid</div><small class=""service-state"" id=""adSaved""></small></summary><div class=""provider-fields""><label for=""ad_key"">API key</label><input type=""password"" id=""ad_key"" autocomplete=""off"" placeholder=""Paste key; blank keeps saved key""><p><a href=""https://alldebrid.com/apikeys"" target=""_blank"" rel=""noreferrer"">Get your API key ↗</a></p></div></details><details class=""card provider""><summary><div class=""logo""><span class=""mark"">PM</span>Premiumize</div><small class=""service-state"" id=""pmSaved""></small></summary><div class=""provider-fields""><label for=""pm_key"">API key</label><input type=""password"" id=""pm_key"" autocomplete=""off"" placeholder=""Paste key; blank keeps saved key""><p><a href=""https://www.premiumize.me/account"" target=""_blank"" rel=""noreferrer"">Get your API key ↗</a></p></div></details></div><div class=""card""><label for=""provider"">Preferred download service</label><select id=""provider""><option value=""real-debrid"">Real-Debrid</option><option value=""torbox"">TorBox</option><option value=""alldebrid"">AllDebrid</option><option value=""premiumize"">Premiumize</option><option value=""none"">Direct links · no debrid</option></select><div class=""actions""><button class=""save"" id=""saveKeys"">Save and validate</button><small>Check each service you want to use. All keys stay saved.</small></div></div></section>
<section id=""packages"" hidden><div class=""card""><h2>Package sources</h2><p class=""muted"">Paste a .gssource / .gsource URL. Your PS4 validates and installs the source.</p><label for=""source_url"">Source URL</label><input id=""source_url"" type=""url"" placeholder=""https://…/provider.gssource""><div class=""actions""><button class=""save"" id=""installSource"">Install source on PS4</button></div></div><div class=""card""><h2>Installed on your PS4</h2><div id=""sources"">Checking…</div><p id=""sourceStatus"" class=""muted""></p></div></section>
<section id=""downloads"" hidden><div class=""card""><h2>Send download links</h2><p class=""muted"">Paste up to 50 links, one per line. Direct packages, archives and supported hoster links use your PS4's selected service and staging drive.</p><label for=""downloadLinks"">Download links</label><textarea id=""downloadLinks"" rows=""7"" maxlength=""60000"" autocomplete=""off"" spellcheck=""false"" placeholder=""https://example.com/file.pkg""></textarea><label><input type=""checkbox"" id=""multipartLinks""> These links are parts of one RAR archive, in volume order</label><small>Named parts are grouped automatically. Enable this for links that hide their filenames.</small><div class=""actions""><button class=""save"" id=""queueLinks"">Queue on PS4</button></div><p id=""downloadResult"" role=""status"" aria-live=""polite""></p><small>Duplicates are skipped. Titles and artwork appear as package identity is resolved. The PS4 handles verification, extraction and installation order.</small></div></section>
<section id=""appearance"" hidden><div class=""card""><h2>Appearance</h2><label for=""accent"">Choose your accent</label><input id=""accent"" type=""color"" value=""#E4E4E1""><label for=""background"">Background</label><select id=""background""><option value=""solid"">Plain charcoal</option><option value=""ripple"">Ripple</option><option value=""wave"">Wave</option><option value=""grid"">Mosaic</option><option value=""halo"">Halo</option><option value=""graphite"">Graphite fade</option><option value=""obsidian"">Obsidian</option><option value=""slate"">Slate glow</option><option value=""flowers"">ASCII Flowers</option><option value=""image"">Custom pixel background</option></select><p>Upload your own picture. Your phone crops and converts it to 320 × 180 pixels; the original stays on your phone.</p><label for=""background_overlay"">Pattern over your picture</label><select id=""background_overlay""></select><label for=""background_image_opacity"">Picture opacity <output id=""imageOpacityValue"">35%</output></label><input id=""background_image_opacity"" type=""range"" min=""0"" max=""100"" value=""35""><label for=""background_effect_opacity"">Accent and pattern strength <output id=""effectOpacityValue"">35%</output></label><input id=""background_effect_opacity"" type=""range"" min=""0"" max=""100"" value=""35""><p>The preview includes your accent tint, masked pattern and opacity. Save appearance to apply these controls.</p><label for=""backgroundFile"">Background picture</label><input id=""backgroundFile"" type=""file"" accept=""image/jpeg,image/png,image/webp""><canvas id=""backgroundPreview"" width=""320"" height=""180"" hidden style=""width:100%;max-width:640px;image-rendering:pixelated;border-radius:12px;margin-top:16px""></canvas><div class=""actions""><button id=""uploadBackground"" disabled>Upload pixel background</button></div><div class=""actions""><button class=""save"" id=""saveAppearance"">Save appearance</button><button id=""restoreAppearance"">Restore original charcoal</button></div></div></section><section id=""library"" hidden><div class=""card"" id=""libraryList""><h2>Your library</h2><p>Choose an installed game to change its SSPI case artwork and PS4 homescreen icon. Both previews are fitted on your phone.</p><div class=""actions""><input id=""libraryFilter"" type=""search"" placeholder=""Find a game"" aria-label=""Find a game""><button id=""refreshLibrary"">Refresh library</button></div><div id=""libraryGrid"" style=""display:grid;grid-template-columns:repeat(auto-fill,minmax(140px,1fr));gap:12px;margin-top:18px""></div></div><div id=""coverEditor"" class=""card"" hidden><button type=""button"" class=""back"" id=""closeCover"">‹ All games</button><h2 id=""coverTitle""></h2><p id=""coverId"" class=""muted""></p><label for=""coverFile"">Choose a cover (JPG, PNG or WebP)</label><input id=""coverFile"" type=""file"" accept=""image/jpeg,image/png,image/webp""><label for=""coverFit"">Fit artwork</label><select id=""coverFit""><option value=""crop"">Fill artwork — center crop</option><option value=""contain"">Keep whole picture</option></select><div class=""effect-controls""><div><label for=""coverEffect"">Outline style</label><select id=""coverEffect""><option value=""none"">None</option><option value=""neon"">Neon Glow</option><option value=""retro"">Retro Lines</option><option value=""clean"">Clean Outline</option><option value=""arcade"">Arcade Corners</option></select></div><div><label for=""coverColor"">Color</label><input id=""coverColor"" type=""color"" value=""#c7a4ff""></div></div><label for=""coverEdge"">Outline follows</label><select id=""coverEdge""><option value=""alpha"">PNG / picture edges</option><option value=""frame"">Rectangular frame</option></select><label for=""coverWidth"">Thickness <output id=""coverWidthValue"">5</output></label><input id=""coverWidth"" type=""range"" min=""2"" max=""12"" value=""5""><p id=""coverHint"">Effects are baked on your phone before upload.</p><div class=""preview-pair""><figure><canvas id=""coverPreview"" width=""384"" height=""432"" hidden></canvas><figcaption>SSPI case</figcaption></figure><figure><canvas id=""coverIconPreview"" width=""512"" height=""512"" hidden></canvas><figcaption>PS4 home screen</figcaption></figure></div><div class=""actions editor-actions""><button class=""save"" id=""saveCover"" disabled>Save cover</button><button id=""restoreCover"">Restore original</button></div><p>SSPI artwork refreshes automatically. The 512 × 512 PS4 icon keeps an original backup for restore. Restart your PS4 after saving to refresh the homescreen.</p></div></section><footer>Saved on your PS4. Keep SSPI open and both devices on the same network. Blank key fields keep your saved credentials.</footer></main><script>
const base=location.pathname.replace(/\/$/,''),$=id=>document.getElementById(id);
function selectTab(id){document.querySelectorAll('section').forEach(s=>s.hidden=s.id!==id);document.querySelectorAll('nav button').forEach(b=>{b.classList.toggle('active',b.dataset.tab===id);b.removeAttribute('aria-current')})}
if(location.hash==='#downloads')selectTab('downloads');
$('queueLinks').onclick=()=>{const button=$('queueLinks'),submitted=$('downloadLinks').value,multipart=$('multipartLinks').checked;button.disabled=true;enqueue(async()=>{await saveKeys();const result=await request('/downloads',new URLSearchParams({links:submitted,multipart:multipart?'1':'0'}));const text=result.queued+' queued, '+result.duplicates+' duplicates skipped'+(result.failed?', '+result.failed+' could not be queued. Retry this batch; accepted links will be skipped.':'.');$('downloadResult').textContent=text;note(text);if(!result.failed&&$('downloadLinks').value===submitted)$('downloadLinks').value=''}).catch(()=>{}).finally(()=>button.disabled=false)};
let dirty=false,appearanceDirty=false,sourceState='',serviceRevision=-1,chain=Promise.resolve();
const serviceFields=['key','tb_key','ad_key','pm_key'],serviceCards=[['real-debrid','rdSaved','rd'],['torbox','tbSaved','tb'],['alldebrid','adSaved','ad'],['premiumize','pmSaved','pm']];
for(const [id,element] of serviceCards){const label=document.createElement('label'),box=document.createElement('input');box.type='checkbox';box.id='enabled-'+id;box.onchange=()=>dirty=true;label.append(box,document.createTextNode(' Enable for downloads'));$(element).closest('.provider').querySelector('.provider-fields').prepend(label)}
function enabledServices(){return serviceCards.filter(([id])=>$('enabled-'+id).checked).map(([id])=>id).join(',')}
function serviceSnapshot(){const saved={provider:$('provider').value,enabled_providers:enabledServices()};for(const key of serviceFields)saved[key]=$(key).value;return saved}
let noticeTimer=0;
function note(s){clearTimeout(noticeTimer);$('notice').textContent=s;noticeTimer=setTimeout(()=>$('notice').textContent='',4500)}
function clearError(){$('pageError').hidden=true;$('pageError').textContent=''}
function showError(message){clearTimeout(noticeTimer);$('notice').textContent='';$('pageError').textContent=message;$('pageError').hidden=false}
async function failureText(r){if(r.status===410)return 'Pairing expired. Open a new QR code in SSPI and scan again.';const text=await r.text();return text.startsWith('<')?'PS4 request failed ('+r.status+'). Try again from SSPI.':text.slice(0,500)||'PS4 request failed ('+r.status+').'}
async function request(path,body){const r=await fetch(base+path,{method:body?'POST':'GET',body,cache:'no-store'});if(!r.ok)throw Error(await failureText(r));return r.json()}
function renderSources(c){const choices=c.source_choices||[],stamp=JSON.stringify(choices);if(sourceState===stamp)return;sourceState=stamp;$('sources').replaceChildren();if(!choices.length){$('sources').textContent=c.sources||'No package sources installed yet';return}for(const source of choices){const row=document.createElement('div'),label=document.createElement('span'),button=document.createElement('button');row.className='actions';label.textContent=source.name+' · v'+source.version;label.style.flex='1';button.textContent=source.enabled?'Enabled':'Disabled';button.setAttribute('aria-pressed',String(source.enabled));button.onclick=()=>{button.disabled=true;enqueue(async()=>{await saveKeys();await request('/source-toggle',new URLSearchParams({source_id:source.id,enabled:source.enabled?'off':'on'}));sourceState='';await refresh();note('Source selection saved on PS4')}).catch(()=>{}).finally(()=>button.disabled=false)};row.append(label,button);$('sources').append(row)}}
async function refresh(initial=false){let c=await request('/config');for(const [id,element,flag] of serviceCards){const status=(c.service_status||{})[id],el=$(element);el.textContent=status?status.message:c[flag]?'Key saved on PS4':'Not connected';el.dataset.state=status?status.state:c[flag]?'saved':'missing';if(!dirty)$('enabled-'+id).checked=(c.enabled_providers||'').split(',').includes(id)}if(c.has_background_image&&!savedPictureRequested&&!backgroundPixels){savedPictureRequested=true;const selection=backgroundSelection;request('/wallpaper').then(v=>{if(selection===backgroundSelection&&!backgroundPixels){backgroundPixels=decodePixels(v.rgba);schedulePreview()}}).catch(e=>showError(e.message))}renderSources(c);$('sourceStatus').textContent=c.source_status||'';if(!dirty)$('provider').value=c.provider;document.documentElement.style.setProperty('--accent',c.accent);if(!appearanceDirty){$('accent').value=c.accent;$('background').value=c.background||'solid';$('background_overlay').value=c.background_overlay||'solid';$('background_image_opacity').value=c.background_image_opacity??35;$('background_effect_opacity').value=c.background_effect_opacity??35;schedulePreview()}if(c.service_revision!==serviceRevision){serviceRevision=c.service_revision;if(c.service_message)note(c.service_message);else if(initial)note('Saved configuration loaded.')}}
async function saveKeys(force=false){if(force)dirty=true;while(dirty){const saved=serviceSnapshot();await request('/services',new URLSearchParams(saved));const current=serviceSnapshot();dirty=Object.keys(saved).some(k=>current[k]!==saved[k]);for(const key of serviceFields)if($(key).value===saved[key])$(key).value=''}await refresh()}
async function saveAppearance(){await saveKeys();const saved=appearanceSnapshot();await request('/appearance',new URLSearchParams(saved));appearanceDirty=Object.keys(saved).some(k=>$(k).value!==saved[k]);note('Appearance saved on PS4')}
function enqueue(fn){chain=chain.catch(()=>{}).then(async()=>{clearError();return await fn()}).catch(e=>{showError(e.message);throw e});return chain}
[...serviceFields,'provider'].forEach(k=>$(k).addEventListener('input',()=>dirty=true));
$('provider').addEventListener('change',()=>{const id=$('provider').value;if(id==='none'){for(const [name] of serviceCards)$('enabled-'+name).checked=false}else $('enabled-'+id).checked=true;dirty=true});
['accent','background','background_overlay','background_image_opacity','background_effect_opacity'].forEach(k=>$(k).addEventListener('input',()=>{appearanceDirty=true;schedulePreview()}));
const tabs=['services','packages','appearance','downloads','library'];
function navigate(id){if(!tabs.includes(id))return;selectTab(id);history.replaceState(null,'','#'+id);const active=document.querySelector('nav button.active');active.setAttribute('aria-current','page');active.scrollIntoView({block:'nearest',inline:'nearest'});if(id==='library')loadLibrary().catch(e=>showError(e.message))}
document.querySelectorAll('nav button').forEach(b=>b.onclick=()=>navigate(b.dataset.tab));
let swipeStart=null;
document.querySelector('main').addEventListener('touchstart',e=>{if(e.target.closest('button,input,select,textarea,canvas,nav')||e.touches.length!==1){swipeStart=null;return}swipeStart={x:e.touches[0].clientX,y:e.touches[0].clientY}}, {passive:true});
document.querySelector('main').addEventListener('touchend',e=>{if(!swipeStart)return;const dx=e.changedTouches[0].clientX-swipeStart.x,dy=e.changedTouches[0].clientY-swipeStart.y;swipeStart=null;if(Math.abs(dx)<80||Math.abs(dx)<Math.abs(dy)*2)return;const current=tabs.indexOf(document.querySelector('nav button.active').dataset.tab);navigate(tabs[Math.max(0,Math.min(tabs.length-1,current+(dx<0?1:-1)))])},{passive:true});

$('saveKeys').onclick=()=>{const button=$('saveKeys');button.disabled=true;button.textContent='Saving…';note('Saving keys on PS4…');enqueue(()=>saveKeys(true)).catch(()=>{}).finally(()=>{button.disabled=false;button.textContent='Save and validate'})};
$('installSource').onclick=()=>enqueue(async()=>{await saveKeys();await request('/source',new URLSearchParams({source_url:$('source_url').value}));note('Source sent to PS4 — installing…')}).catch(()=>{});
$('saveAppearance').onclick=()=>enqueue(saveAppearance).catch(()=>{});
$('restoreAppearance').onclick=()=>{appearanceDirty=true;$('background').value='solid';enqueue(saveAppearance).catch(()=>{})};
let backgroundPixels=null,backgroundSelection=0,savedPictureRequested=false,previewTimer=0,previewVersion=0;
const masks=new Map();
for(const original of $('background').options){if(original.value==='image')continue;const option=original.cloneNode(true);if(option.value==='solid')option.textContent='None — accent tint only';$('background_overlay').append(option)}
function appearanceSnapshot(){const value={};for(const k of ['accent','background','background_overlay','background_image_opacity','background_effect_opacity'])value[k]=$(k).value;return value}
function decodePixels(value){return Uint8Array.from(atob(value),c=>c.charCodeAt(0))}
function schedulePreview(){$('imageOpacityValue').textContent=$('background_image_opacity').value+'%';$('effectOpacityValue').textContent=$('background_effect_opacity').value+'%';clearTimeout(previewTimer);const version=++previewVersion;previewTimer=setTimeout(()=>renderPreview(version).catch(e=>showError(e.message)),80)}
async function renderPreview(version){if(!backgroundPixels)return;const pattern=$('background_overlay').value;if(!masks.has(pattern)){const pending=request('/background-mask',new URLSearchParams({pattern})).then(v=>decodePixels(v.mask));masks.set(pattern,pending);pending.catch(()=>masks.delete(pattern))}const mask=await masks.get(pattern);if(version!==previewVersion)return;if(mask.length!==640*360)throw Error('Background preview mask is incomplete.');const state=appearanceSnapshot(),canvas=$('backgroundPreview');canvas.width=640;canvas.height=360;const ctx=canvas.getContext('2d'),result=ctx.createImageData(640,360),rgba=result.data,accent=[1,3,5].map(i=>parseInt(state.accent.slice(i,i+2),16)),image=Number(state.background_image_opacity)/100,effect=Number(state.background_effect_opacity)/100;for(let y=0;y<360;y++)for(let x=0;x<640;x++){const src=((y>>1)*320+(x>>1))*4,dst=(y*640+x)*4,luminance=(backgroundPixels[src]*.2126+backgroundPixels[src+1]*.7152+backgroundPixels[src+2]*.0722)/255,ink=mask[y*640+x]/255*effect;for(let c=0;c<3;c++){const tinted=backgroundPixels[src+c]*(1-effect)+accent[c]*luminance*effect;rgba[dst+c]=Math.floor(Math.min(255,tinted*image+accent[c]*ink*.35))}rgba[dst+3]=255}ctx.putImageData(result,0,0);canvas.hidden=false}
$('backgroundFile').onchange=async()=>{const version=++backgroundSelection,file=$('backgroundFile').files[0];if(!file)return;$('uploadBackground').disabled=true;let url='';try{if(!['image/jpeg','image/png','image/webp'].includes(file.type)||file.size>20*1024*1024)throw Error('Choose a JPG, PNG or WebP under 20 MB.');url=URL.createObjectURL(file);const img=new Image();await new Promise((resolve,reject)=>{img.onload=resolve;img.onerror=()=>reject(Error('This picture could not be opened.'));img.src=url});if(version!==backgroundSelection)return;const canvas=document.createElement('canvas');canvas.width=320;canvas.height=180;const ctx=canvas.getContext('2d',{willReadFrequently:true}),scale=Math.max(320/img.naturalWidth,180/img.naturalHeight);ctx.fillStyle='#191919';ctx.fillRect(0,0,320,180);ctx.imageSmoothingEnabled=true;ctx.drawImage(img,(320-img.naturalWidth*scale)/2,(180-img.naturalHeight*scale)/2,img.naturalWidth*scale,img.naturalHeight*scale);backgroundPixels=new Uint8Array(ctx.getImageData(0,0,320,180).data);schedulePreview();$('uploadBackground').disabled=false;note('Preview ready. Upload to apply this picture on your PS4.')}catch(e){showError(e.message)}finally{if(url)URL.revokeObjectURL(url)}};
$('uploadBackground').onclick=()=>{const pixels=backgroundPixels;if(!pixels)return;const button=$('uploadBackground');button.disabled=true;enqueue(async()=>{const r=await fetch(base+'/wallpaper',{method:'POST',headers:{'Content-Type':'application/octet-stream'},body:pixels});if(!r.ok)throw Error(await failureText(r));$('background').value='image';appearanceDirty=true;await saveAppearance();note('Pixel background and effects saved on PS4.')}).catch(()=>{}).finally(()=>button.disabled=!backgroundPixels)};

let libraryGames=[],coverGame=null,coverImage=null,coverPixels=null,coverSelection=0,coverBusy=false,coverDirty=false;
function drawLibrary(){const filter=$('libraryFilter').value.trim().toLowerCase(),grid=$('libraryGrid');grid.replaceChildren();for(const game of libraryGames){if(!(game.name+' '+game.id).toLowerCase().includes(filter))continue;const button=document.createElement('button'),image=document.createElement('img'),title=document.createElement('div'),detail=document.createElement('small');image.loading='lazy';image.alt='';image.src=base+'/cover/'+game.id;image.style='width:100%;height:140px;object-fit:contain;margin-bottom:10px';image.onerror=()=>image.hidden=true;title.textContent=game.name;detail.textContent=game.id+(game.custom?' · Custom cover':'');button.style='min-width:0;overflow-wrap:anywhere;text-align:left';button.append(image,title,detail);button.onclick=()=>{if(coverBusy)return;coverGame=game;coverImage=coverPixels=null;coverDirty=false;coverSelection++;$('coverFile').value='';$('coverPreview').hidden=$('coverIconPreview').hidden=true;$('saveCover').disabled=true;$('coverTitle').textContent=game.name;$('coverId').textContent=game.id;$('coverEditor').hidden=false;$('libraryList').hidden=true;$('coverColor').value=$('accent').value;$('coverEditor').scrollIntoView({behavior:'auto',block:'start'})};grid.append(button)}if(!grid.children.length)grid.textContent=libraryGames.length?'No matching games.':'No installed games yet. Wait for the PS4 library scan, then refresh.'}
async function loadLibrary(){const data=await request('/library');libraryGames=data.games||[];drawLibrary()}
$('refreshLibrary').onclick=()=>loadLibrary().catch(e=>showError(e.message));$('libraryFilter').oninput=drawLibrary;
function artworkCanvas(w,h){const c=document.createElement('canvas');c.width=w;c.height=h;return c}
function colorMask(image,color){const c=artworkCanvas(image.width,image.height),ctx=c.getContext('2d');ctx.drawImage(image,0,0);ctx.globalCompositeOperation='source-in';ctx.fillStyle=color;ctx.fillRect(0,0,c.width,c.height);return c}
function insetArtworkMask(mask,radius){const c=artworkCanvas(mask.width,mask.height),ctx=c.getContext('2d');ctx.drawImage(mask,0,0);if(radius<=0)return c;const shifted=artworkCanvas(c.width,c.height),s=shifted.getContext('2d');ctx.globalCompositeOperation='destination-in';for(let i=0;i<16;i++){const a=i*Math.PI/8;s.clearRect(0,0,c.width,c.height);s.drawImage(mask,Math.cos(a)*radius,Math.sin(a)*radius);ctx.drawImage(shifted,0,0)}return c}
function artworkRing(mask,inset,width,color){const c=insetArtworkMask(mask,inset),ctx=c.getContext('2d');ctx.globalCompositeOperation='destination-out';ctx.drawImage(insetArtworkMask(mask,inset+width),0,0);return colorMask(c,color)}
function outlinedArtwork(image,w,h,options){
 const style=options.style,thickness=options.width*w/512,art=artworkCanvas(w,h),a=art.getContext('2d'),scale=(options.fit==='contain'?Math.min:Math.max)(w/image.naturalWidth,h/image.naturalHeight),dw=image.naturalWidth*scale,dh=image.naturalHeight*scale;
 a.imageSmoothingEnabled=true;a.imageSmoothingQuality='high';a.drawImage(image,(w-dw)/2,(h-dh)/2,dw,dh);
 const out=artworkCanvas(w,h),ctx=out.getContext('2d');ctx.fillStyle='#1c1e22';ctx.fillRect(0,0,w,h);ctx.drawImage(art,0,0);
 if(style==='none')return out;
 let mask=art;if(options.edge==='frame'||style==='arcade'){mask=artworkCanvas(w,h);const m=mask.getContext('2d');m.fillStyle='#fff';m.fillRect(0,0,w,h)}
 mask=colorMask(mask,options.color);
 const effect=artworkCanvas(w,h),e=effect.getContext('2d');
 if(style==='neon'){const glow=artworkRing(mask,thickness*.6,thickness,options.color);e.save();e.shadowColor=options.color;e.shadowBlur=thickness*3;e.drawImage(glow,0,0);e.drawImage(glow,0,0);e.restore();e.drawImage(artworkRing(mask,thickness*.9,thickness*.4,'#ffffff'),0,0)}
 if(style==='clean')e.drawImage(artworkRing(mask,thickness*.5,thickness,options.color),0,0);
 if(style==='retro'){for(const [inset,color] of [[.5,'#ffffff'],[2,options.color],[3.5,options.color]])e.drawImage(artworkRing(mask,thickness*inset,thickness*.55,color),0,0)}
 // Effects overlay the fitted picture; never shrink it or add a dark border.
 e.globalCompositeOperation='destination-in';e.drawImage(mask,0,0);ctx.drawImage(effect,0,0);
 if(style==='arcade'){ctx.strokeStyle=options.color;ctx.lineWidth=thickness;ctx.lineCap='square';const length=Math.min(w,h)*.18,inset=thickness;ctx.beginPath();for(const [x,y,sx,sy] of [[inset,inset,1,1],[w-inset,inset,-1,1],[inset,h-inset,1,-1],[w-inset,h-inset,-1,-1]]){ctx.moveTo(x+sx*length,y);ctx.lineTo(x,y);ctx.lineTo(x,y+sy*length)}ctx.stroke()}
 return out;
}
let coverFrame=0;
function scheduleCover(){if(coverBusy)return;coverDirty=true;coverPixels=null;$('saveCover').disabled=true;cancelAnimationFrame(coverFrame);coverFrame=requestAnimationFrame(fitCover)}
function fitCover(){if(!coverImage||coverBusy)return;try{const fitted=[],options={style:$('coverEffect').value,color:$('coverColor').value,width:Number($('coverWidth').value),fit:$('coverFit').value,edge:$('coverEdge').value};$('coverWidthValue').textContent=options.width;for(const name of ['coverPreview','coverIconPreview']){const canvas=$(name),ctx=canvas.getContext('2d');ctx.drawImage(outlinedArtwork(coverImage,canvas.width,canvas.height,options),0,0);fitted.push(new Uint8Array(ctx.getImageData(0,0,canvas.width,canvas.height).data));canvas.hidden=false}coverPixels=new Uint8Array(fitted[0].length+fitted[1].length);coverPixels.set(fitted[0]);coverPixels.set(fitted[1],fitted[0].length);$('saveCover').disabled=false;$('coverHint').textContent='Preview ready. These exact effects will be saved to both covers.'}catch(e){coverPixels=null;showError('Could not prepare this cover. Choose a smaller picture.')}}
for(const id of ['coverFit','coverEffect','coverColor','coverEdge','coverWidth'])$(id).addEventListener('input',scheduleCover);
$('closeCover').onclick=()=>{if(coverBusy)return;$('coverEditor').hidden=true;$('libraryList').hidden=false;$('libraryList').scrollIntoView({block:'start'})};
$('coverFile').onchange=()=>{const file=$('coverFile').files[0],selection=++coverSelection;coverPixels=coverImage=null;$('saveCover').disabled=true;$('coverPreview').hidden=$('coverIconPreview').hidden=true;if(!file)return;if(!['image/jpeg','image/png','image/webp'].includes(file.type)||file.size>20*1024*1024){showError('Choose a JPG, PNG or WebP under 20 MB.');return}const url=URL.createObjectURL(file),image=new Image();image.onload=()=>{URL.revokeObjectURL(url);if(selection!==coverSelection)return;if(image.naturalWidth*image.naturalHeight>48000000){showError('Choose a picture smaller than 48 megapixels.');return}coverImage=image;coverDirty=true;clearError();fitCover()};image.onerror=()=>{URL.revokeObjectURL(url);if(selection===coverSelection)showError('That picture could not be read. Try another image.')};image.src=url};
async function changeCover(restore){if(!coverGame||coverBusy||(!restore&&!coverPixels))return;const game=coverGame,pixels=coverPixels;coverBusy=true;for(const id of ['coverEffect','coverColor','coverEdge','coverWidth','closeCover'])$(id).disabled=true;$('saveCover').disabled=$('restoreCover').disabled=$('coverFile').disabled=$('coverFit').disabled=true;try{await enqueue(async()=>{const response=await fetch(base+'/cover/'+game.id+(restore?'/restore':''),{method:'POST',headers:{'Content-Type':restore?'application/x-www-form-urlencoded':'application/octet-stream'},body:restore?'':pixels});if(!response.ok)throw Error(await failureText(response));const result=await response.json();coverDirty=false;await loadLibrary();if(result.ps4==='failed'||result.ps4==='unavailable'||result.ps4==='partial')showError(result.message);else note(result.message);if(restore){coverImage=coverPixels=null;$('coverPreview').hidden=$('coverIconPreview').hidden=true;$('coverFile').value=''}})}catch(e){showError(e.message)}finally{coverBusy=false;for(const id of ['coverEffect','coverColor','coverEdge','coverWidth','closeCover'])$(id).disabled=false;$('restoreCover').disabled=$('coverFile').disabled=$('coverFit').disabled=false;$('saveCover').disabled=!coverPixels}}
$('saveCover').onclick=()=>changeCover(false);$('restoreCover').onclick=()=>changeCover(true);
let offline=false;
refresh(true).then(async()=>{await request('/connected',new URLSearchParams());if(tabs.includes(location.hash.slice(1)))navigate(location.hash.slice(1));note('Phone connected. You can continue using your PS4.');}).catch(e=>showError(e.message));
setInterval(()=>{if(document.hidden)return;refresh().then(()=>{if(offline){offline=false;clearError();note('PS4 reconnected.')}}).catch(e=>{offline=true;showError(e.message==='Failed to fetch'?'Connection lost. Keep SSPI open and reconnect to the same network.':e.message)})},5000);window.addEventListener('beforeunload',e=>{if(dirty||appearanceDirty||coverDirty||coverBusy){e.preventDefault();e.returnValue=''}});
</script></body></html>".Replace("__SSPI_BRAND__", PairBrandMarkup());
        }

        string ConfigJson()
        {
            var c = Settings;
            return "{\"rd\":" + (c.HasRealDebrid ? "true" : "false") + ",\"tb\":" + (c.HasTorBox ? "true" : "false") +
                ",\"ad\":" + (c.HasAllDebrid ? "true" : "false") + ",\"pm\":" + (c.HasPremiumize ? "true" : "false") + ServiceJsonFields() +
                ",\"enabled_providers\":\"" + string.Join(",", UnlockProviders.EnabledIds(c)) + "\",\"provider\":\"" + JsonLite.Escape(c.UnlockProviderId) + "\",\"accent\":\"" + JsonLite.Escape(c.Accent) +
                "\",\"background\":\"" + JsonLite.Escape(c.BackgroundMode) + "\",\"background_overlay\":\"" + c.BackgroundOverlay +
                "\",\"background_image_opacity\":" + c.BackgroundImageOpacity + ",\"background_effect_opacity\":" + c.BackgroundEffectOpacity +
                ",\"has_background_image\":" + (PixelBackground.IsOwnedPath(c.BackgroundImagePath) ? "true" : "false") + ",\"mbps\":" + c.ConnectionMbps +
                ",\"reduce_motion\":" + (c.ReduceMotion ? "true" : "false") + ",\"show_continue\":" + (c.SearchContinue ? "true" : "false") +
                ",\"show_recents\":" + (c.SearchRecents ? "true" : "false") + ",\"sources\":\"" + JsonLite.Escape(InstalledSources) +
                "\",\"source_status\":\"" + JsonLite.Escape(SourceStatus) + "\",\"source_choices\":" + SourceChoicesJson + "}";
        }

        internal string SaveConfiguration(string route, string body)
        {
            lock (_lock)
            {
                if (route == "/source-toggle") {
                    string id = ParseForm(body, "source_id"), enabled = ParseForm(body, "enabled");
                    if (string.IsNullOrEmpty(id) || id.Length > 128 || (enabled != "on" && enabled != "off")) return "Choose an installed source";
                    if (SetSourceEnabled == null) return "Source controls are not ready. Try again.";
                    string error;
                    try { error = SetSourceEnabled(id, enabled == "on"); }
                    catch { return "Could not save the source selection. Try again."; }
                    if (error != null) return error;
                    Interlocked.Increment(ref Revision); return null;
                }
                var c = Settings;
                string oldRd = c.RealDebridToken, oldTb = c.TorBoxApiKey, oldAd = c.AllDebridApiKey, oldPm = c.PremiumizeApiKey, oldProvider = c.UnlockProviderId;
                string oldAccent = c.Accent, oldAccentName = c.AccentName, oldBackground = c.BackgroundMode, oldEnabled = c.EnabledUnlockProviders;
                string oldOverlay = c.BackgroundOverlay;
                int oldImageOpacity = c.BackgroundImageOpacity, oldEffectOpacity = c.BackgroundEffectOpacity;
                bool oldUse = c.UseUnlockProvider, oldUseRd = c.UseRealDebrid;
                string oldPending = PendingSource, oldStatus = SourceStatus;
                if (route == "/services")
                {
                    string rd = (ParseForm(body, "key") ?? "").Trim(), tb = (ParseForm(body, "tb_key") ?? "").Trim();
                    string ad = (ParseForm(body, "ad_key") ?? "").Trim(), pm = (ParseForm(body, "pm_key") ?? "").Trim();
                    string provider = ParseForm(body, "provider") ?? c.UnlockProviderId;
                    string requestedEnabled = ParseForm(body, "enabled_providers");
                    if (requestedEnabled != null)
                    {
                        foreach (string candidate in requestedEnabled.Split(','))
                            if (candidate.Trim().Length != 0 && !UnlockProviders.IsSupported(candidate.Trim())) return "Choose supported download services";
                        requestedEnabled = UnlockProviders.NormalizeIds(requestedEnabled);
                        var selected = new List<string>(requestedEnabled.Split(','));
                        if (requestedEnabled.Length == 0) provider = "none";
                        else if (!selected.Contains(provider)) provider = selected[0];
                    }
                    if (provider != "real-debrid" && provider != "torbox" && provider != "alldebrid" && provider != "premiumize" && provider != "none") return "Choose a supported download service";
                    foreach (string key in new[] { rd, tb, ad, pm })
                        if ((key.Length > 0 && key.Length < 8) || key.Length > 4096 || key.IndexOfAny(new[] {'\r','\n','\0'}) >= 0)
                            return "Check the API key length and remove line breaks";
                    if (provider == "real-debrid" && rd.Length == 0 && !c.HasRealDebrid || provider == "torbox" && tb.Length == 0 && !c.HasTorBox ||
                        provider == "alldebrid" && ad.Length == 0 && !c.HasAllDebrid || provider == "premiumize" && pm.Length == 0 && !c.HasPremiumize) return "Paste the selected provider's API key";
                    if (requestedEnabled != null)
                        foreach (string id in requestedEnabled.Split(','))
                            if (id.Length != 0 && !UnlockProviders.IsConfigured(c, id) &&
                                (id == "real-debrid" ? rd : id == "torbox" ? tb : id == "alldebrid" ? ad : pm).Length == 0)
                                return "Paste a key for each enabled service";
                    string enabled = string.Join(",", UnlockProviders.EnabledIds(c));
                    if (rd.Length > 0) c.RealDebridToken = rd;
                    if (tb.Length > 0) c.TorBoxApiKey = tb;
                    if (ad.Length > 0) c.AllDebridApiKey = ad;
                    if (pm.Length > 0) c.PremiumizeApiKey = pm;
                    c.UnlockProviderId = provider; c.UseUnlockProvider = provider != "none"; c.UseRealDebrid = provider == "real-debrid";
                    c.EnabledUnlockProviders = requestedEnabled ?? (provider == "none" ? "" : UnlockProviders.NormalizeIds(enabled + "," + provider));
                }
                else if (route == "/source")
                {
                    string source = (ParseForm(body, "source_url") ?? "").Trim(); Uri uri;
                    if (source.Length > 2048 || !Uri.TryCreate(source, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo)) return "Paste a valid source URL";
                    if (!string.IsNullOrEmpty(PendingSource)) return "A source is already waiting to install";
                    PendingSource = source; SourceStatus = "Waiting for PS4 source validation";
                }
                else if (route == "/appearance")
                {
                    string accent = ParseForm(body, "accent"); ThemeColor color; ThemeAccentPreset preset;
                    if (!ThemePalette.TryResolveAccent(accent ?? "", out color, out preset)) return "Invalid accent color";
                    int imageOpacity = c.BackgroundImageOpacity, effectOpacity = c.BackgroundEffectOpacity;
                    string imageValue = ParseForm(body, "background_image_opacity"), effectValue = ParseForm(body, "background_effect_opacity");
                    if ((imageValue != null && !int.TryParse(imageValue, out imageOpacity)) ||
                        (effectValue != null && !int.TryParse(effectValue, out effectOpacity)) ||
                        imageOpacity < 0 || imageOpacity > 100 || effectOpacity < 0 || effectOpacity > 100) return "Opacity must be between 0 and 100.";
                    c.Accent = color.ToHex(); c.AccentName = "Custom";
                    c.BackgroundImageOpacity = imageOpacity; c.BackgroundEffectOpacity = effectOpacity;
                    string overlay = ParseForm(body, "background_overlay");
                    if (overlay != null) c.BackgroundOverlay = BackdropPattern.Modes[BackdropPattern.Index(overlay)];
                    string background = ParseForm(body, "background");
                    if (background != null) c.BackgroundMode = background == AppSettings.BackgroundImage && PixelBackground.IsOwnedPath(c.BackgroundImagePath)
                        ? AppSettings.BackgroundImage : BackdropPattern.Modes[BackdropPattern.Index(background)];
                    c.ValidateAppearance(false);
                }
                else return "Unknown configuration section";
                if (!c.Save()) {
                    c.RealDebridToken = oldRd; c.TorBoxApiKey = oldTb; c.AllDebridApiKey = oldAd; c.PremiumizeApiKey = oldPm; c.UnlockProviderId = oldProvider;
                    c.UseUnlockProvider = oldUse; c.UseRealDebrid = oldUseRd; c.EnabledUnlockProviders = oldEnabled;
                    c.Accent = oldAccent; c.AccentName = oldAccentName; c.BackgroundMode = oldBackground;
                    c.BackgroundOverlay = oldOverlay; c.BackgroundImageOpacity = oldImageOpacity; c.BackgroundEffectOpacity = oldEffectOpacity;
                    PendingSource = oldPending; SourceStatus = oldStatus;
                    return "The PS4 could not save settings. Try again.";
                }
                Interlocked.Increment(ref Revision);
                if (route == "/services") BeginServiceValidation(body);
                return null;
            }
        }

        static string PairResultHtml(string title, string message, string tag, bool failed, string retryPath)
        {
            string kind = failed ? "failed" : "saved";
            string retry = string.IsNullOrEmpty(retryPath) ? "" :
                "<a class='retry' href='" + retryPath + "'>TRY AGAIN →</a>";
            return PageHead(title) + "<main class='shell result'><header><div class='titlebar'><span class='eyebrow'>SUPER SIMPLE PACKAGE INSTALLER / SETUP</span><span class='local'>LOCAL ONLY</span></div>" +
                "<h1>" + title + "</h1><p>" + message + "</p></header><section class='state " + kind + "'>" +
                "<div class='resultMark'>" + (failed ? "!" : "✓") + "</div><div><b>" + title + "</b><span>" +
                message + "</span></div><strong>" + tag + "</strong></section>" + retry + "</main></body></html>";
        }

        static string PageHead(string title)
        {
            return "<!DOCTYPE html><html lang='en'><head><meta charset='utf-8'/><meta name='viewport' content='width=device-width,initial-scale=1'/><title>" + title + "</title><style>" +
                ":root{color-scheme:dark;--bg:#1b1b1b;--panel:#252525;--row:#2b2b2b;--raised:#343434;--line:#494949;--text:#f3f3f1;--muted:#b8b8b4;--dim:#a1a19b;--ok:#83d7a3;--bad:#f495a1}" +
                "*{box-sizing:border-box}html{background:var(--bg)}body{margin:0;min-height:100vh;background:var(--bg);color:var(--text);font-family:system-ui,-apple-system,Segoe UI,sans-serif;padding:28px;line-height:1.5}" +
                ".shell{width:min(780px,100%);margin:16px auto;padding:30px;background:var(--panel);border:1px solid var(--line);border-radius:18px}.shell:before{display:none}header{padding:0 0 24px;border-bottom:1px solid var(--line)}.titlebar{display:flex;align-items:center;justify-content:space-between;gap:16px}.eyebrow,.local{font-size:12px;color:var(--muted);letter-spacing:.05em}.local{border:1px solid var(--line);padding:4px 10px;border-radius:8px;white-space:nowrap}h1{font-size:34px;line-height:1.2;font-weight:600;letter-spacing:-.03em;margin:20px 0 12px}p{margin:0;color:var(--muted)}" +
                ".state{margin:24px 0;padding:20px;background:var(--row);border:1px solid var(--line);border-radius:12px;display:grid;grid-template-columns:32px minmax(0,1fr) auto;gap:16px;align-items:center}.state b{display:block;font-size:18px;font-weight:600}.state span{display:block;font-size:14px;color:var(--muted);margin-top:5px;overflow-wrap:anywhere}.state strong{font-size:11px;font-weight:500;border:1px solid var(--line);padding:5px 9px;border-radius:7px}.stateIcon{width:26px;height:26px;border:2px solid var(--line);border-top-color:var(--text);border-radius:50%;animation:spin 1.2s linear infinite}.stateIcon i,.sweep{display:none}.state.saved strong,.state.saved .resultMark{color:var(--ok)}.state.failed strong,.state.failed .resultMark{color:var(--bad)}.state.saved .stateIcon,.state.failed .stateIcon{animation:none}.state.saved .stateIcon{border-color:var(--ok)}.state.failed .stateIcon{border-color:var(--bad)}" +
                "fieldset,.credentials{min-width:0;border:0;padding:0;margin:0 0 26px}legend,.sectionLabel{width:100%;padding:0 0 12px;font-size:13px;font-weight:500;color:var(--muted);letter-spacing:.04em}legend em,.sectionLabel span{float:right;font-style:normal;font-size:11px;color:var(--dim)}.branches{margin:0 0 0 12px;padding-left:26px;border-left:1px solid var(--line);display:grid;gap:10px}.choice{position:relative;display:flex;align-items:center;gap:14px;background:var(--row);border:1px solid var(--line);border-radius:10px;padding:14px 16px;cursor:pointer}.choice:before{content:'';position:absolute;left:-27px;top:50%;width:19px;height:1px;background:var(--muted)}.choice:last-child:after{content:'';position:absolute;left:-28px;top:calc(50% + 1px);bottom:-2px;width:2px;background:var(--panel)}.choice input{width:18px;height:18px;flex:0 0 auto;margin:0;accent-color:#e4e4e1}.choice span{font-size:16px;font-weight:500;min-width:0}.choice small{display:block;color:var(--muted);font-size:13px;font-weight:400;margin-top:3px}.choice:has(input:checked){background:var(--raised);border-color:#b8b8b4}.field{display:block;font-size:12px;color:var(--muted);margin-top:14px}.field input{display:block;width:100%;min-width:0;margin-top:8px;padding:14px;background:var(--bg);border:1px solid var(--line);border-radius:10px;color:var(--text);font:16px system-ui,sans-serif}.field input:focus-visible,button:focus-visible,.choice:focus-within,.retry:focus-visible{outline:2px solid #e4e4e1;outline-offset:3px}.field input::placeholder{color:#93938d}" +
                "button,.retry{display:block;width:100%;padding:15px 20px;border:1px solid #e4e4e1;border-radius:10px;background:#e4e4e1;color:#202020;font:600 15px system-ui,sans-serif;text-align:center;text-decoration:none;cursor:pointer}button span{float:right}button:disabled{opacity:.5;cursor:default}#bgbtn{margin-top:14px;background:var(--raised);border-color:var(--line);color:var(--text)}.fine{font-size:12px;color:var(--dim);margin-top:18px;line-height:1.5}.fine i{display:inline-block;width:16px;height:1px;background:var(--line);margin:0 8px;vertical-align:middle}.result{margin-top:12vh}.resultMark{display:grid;place-items:center;font-size:26px}.retry{margin-top:24px}[hidden]{display:none!important}@keyframes spin{to{transform:rotate(360deg)}}@media(prefers-reduced-motion:reduce){*{animation:none!important;scroll-behavior:auto!important}}@media(max-width:540px){body{padding:12px}.shell{padding:20px 16px;margin:0 auto;border-radius:14px}h1{font-size:28px}.titlebar{gap:8px}.eyebrow{font-size:10px}.state{grid-template-columns:28px minmax(0,1fr);padding:16px;gap:12px}.state strong{grid-column:2;width:max-content}.branches{padding-left:20px}.choice:before{left:-21px;width:14px}.choice:last-child:after{left:-22px}.choice{padding:12px}.sectionLabel span,legend em{float:none;display:block;margin-top:4px}}" +
                "</style></head><body>";
        }

        static void WriteResponse(NetworkStream stream, int code, string contentType, string body)
        {
            byte[] data = Encoding.UTF8.GetBytes(body ?? "");
            WriteResponseBytes(stream, code, contentType, data);
        }

        static void WriteResponseBytes(NetworkStream stream, int code, string contentType, byte[] data)
        {
            string reason = code == 200 ? "OK" : code == 404 ? "Not Found" : code == 410 ? "Gone" : "Error";
            string hdr =
                "HTTP/1.0 " + code + " " + reason + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + data.Length + "\r\n" +
                "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nX-Frame-Options: DENY\r\nConnection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(hdr);
            stream.Write(h, 0, h.Length);
            stream.Write(data, 0, data.Length);
            stream.Flush();
        }

        public static string DetectLanIp()
        {
            try
            {
                using (var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    s.Connect("8.8.8.8", 65530);
                    var ep = s.LocalEndPoint as IPEndPoint;
                    if (ep != null && IsLanAddress(ep.Address)) return ep.Address.ToString();
                }
            }
            catch { }
            // A LAN without Internet still has a usable interface. Some older
            // Mono ports do not implement interface enumeration, hence fallback.
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var address in nic.GetIPProperties().UnicastAddresses)
                        if (IsLanAddress(address.Address)) return address.Address.ToString();
                }
            }
            catch { }
            try
            {
                foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (IsLanAddress(ip)) return ip.ToString();
                }
            }
            catch { }
            return "0.0.0.0";
        }

        internal static bool IsLanAddress(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] != 0 && bytes[0] != 127 && bytes[0] < 224 &&
                !(bytes[0] == 169 && bytes[1] == 254);
        }
    }
}
