using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        const int MaxConcurrentClients = 4;
        const int RequestTimeoutMilliseconds = 10000;
        readonly object _clientsLock = new object();
        readonly HashSet<TcpClient> _clients = new HashSet<TcpClient>();
        int _sessionMinutes = 5;
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
            _sessionMinutes = Math.Max(1, Math.Min(1440, minutes));
            ExpiresUtc = DateTime.UtcNow.AddMinutes(_sessionMinutes);
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
            TcpListener listener = _listener;
            _thread = new Thread(() => ListenLoop(listener)) { IsBackground = true };
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
            lock (_clientsLock)
            {
                _stop = true;
                Running = false;
                try { if (_listener != null) _listener.Stop(); } catch { }
                _listener = null;
                foreach (TcpClient client in _clients)
                    try { client.Close(); } catch { }
            }
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

        void ListenLoop(TcpListener listener)
        {
            try
            {
                Status = "Pair server on " + PairUrl;
                while (!_stop && ReferenceEquals(_listener, listener) && !Expired)
                {
                    if (!listener.Pending())
                    {
                        Thread.Sleep(50);
                        continue;
                    }
                    TcpClient client = null;
                    try
                    {
                        client = listener.AcceptTcpClient();
                        client.ReceiveTimeout = 5000;
                        client.SendTimeout = 15000;
                        lock (_clientsLock)
                        {
                            if (_stop || !ReferenceEquals(_listener, listener) || _clients.Count >= MaxConcurrentClients)
                                continue;
                            _clients.Add(client);
                            TcpClient accepted = client;
                            try
                            {
                                var worker = new Thread(() => ServeClient(accepted, listener)) { IsBackground = true };
                                worker.Start();
                                client = null; // The worker now owns the connection.
                            }
                            catch { _clients.Remove(accepted); throw; }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!_stop && ReferenceEquals(_listener, listener)) Status = "Client err: " + ex.GetType().Name;
                    }
                    finally
                    {
                        try { if (client != null) client.Close(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_stop && ReferenceEquals(_listener, listener)) Status = "Listen fail: " + ex.Message;
            }
            finally
            {
                try { listener.Stop(); } catch { }
                lock (_clientsLock)
                {
                    if (ReferenceEquals(_listener, listener))
                    {
                        _listener = null;
                        Running = false;
                        foreach (TcpClient client in _clients)
                            try { client.Close(); } catch { }
                    }
                }
            }
        }

        void ServeClient(TcpClient client, TcpListener listener)
        {
            try
            {
                if (!_stop && ReferenceEquals(_listener, listener)) HandleClient(client);
            }
            catch (Exception ex)
            {
                if (!_stop && ReferenceEquals(_listener, listener)) Status = "Client err: " + ex.GetType().Name;
            }
            finally
            {
                try { client.Close(); } catch { }
                lock (_clientsLock) _clients.Remove(client);
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
            var requestTime = Stopwatch.StartNew();
            using (var stream = client.GetStream())
            {
                string headers;
                byte[] extra;
                if (!ReadHeaders(stream, requestTime, out headers, out extra)) return;
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
                byte[] bodyBytes = ReadBody(stream, extra, contentLength, requestTime);
                if (bodyBytes == null)
                { WriteResponse(stream, 400, "text/plain", "Incomplete request; nothing saved"); return; }
                SetRequestReadTimeout(stream, requestTime);
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
                // The phone downloader stays open while it is used; only an idle session expires.
                if (path.StartsWith(pairPrefix, StringComparison.Ordinal)) ExpiresUtc = DateTime.UtcNow.AddMinutes(_sessionMinutes);
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
                if (HandleQueue(stream, method, path, pairPrefix)) return;
                if (HandleLibrary(stream, method, path, pairPrefix, contentType, bodyBytes)) return;
                if (HandleLogs(stream, method, path, pairPrefix)) return;
                if (wallpaper)
                {
                    if (contentType != "application/octet-stream") { WriteResponse(stream, 400, "text/plain", "Use the Appearance page to upload a background."); return; }
                    string error;
                    lock (_lock) error = PixelBackground.Save(Settings, bodyBytes);
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
                {
                    string config;
                    lock (_lock) config = ConfigJson();
                    WriteResponse(stream, 200, "application/json", config); return;
                }
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

        static void SetRequestReadTimeout(NetworkStream stream, Stopwatch requestTime)
        {
            long remaining = RequestTimeoutMilliseconds - requestTime.ElapsedMilliseconds;
            if (remaining <= 0) throw new IOException("Pairing request timed out");
            stream.ReadTimeout = (int)Math.Min(5000, remaining);
        }

        static bool ReadHeaders(NetworkStream stream, Stopwatch requestTime, out string headers, out byte[] extra)
        {
            headers = "";
            extra = new byte[0];
            var ms = new MemoryStream();
            int match = 0;
            var one = new byte[1];
            while (ms.Length < 8192)
            {
                SetRequestReadTimeout(stream, requestTime);
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

        static byte[] ReadBody(NetworkStream stream, byte[] extra, int contentLength, Stopwatch requestTime)
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
                SetRequestReadTimeout(stream, requestTime);
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

        static string _pageHtml;

        // The phone downloader (PhoneCompanion.html) ships inside main.exe as an embedded resource.
        string PairHtml()
        {
            string page = _pageHtml;
            if (page != null) return page;
            try
            {
                using (var stream = typeof(PairServer).Assembly.GetManifestResourceStream("Orbis.PhoneCompanion.html"))
                using (var reader = stream == null ? null : new StreamReader(stream, Encoding.UTF8))
                    page = reader == null ? null : reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException || ex is BadImageFormatException) { page = null; }
            if (string.IsNullOrEmpty(page))
                return PairResultHtml("Phone page unavailable",
                    "This SSPI build is missing its phone page. Reinstall SSPI, then scan the QR code again.", "MISSING", true, null);
            return _pageHtml = page;
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
