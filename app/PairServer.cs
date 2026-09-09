using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal sealed class PairServer
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
        public string SourceChoicesJson = "[]";
        public Func<string, bool, string> SetSourceEnabled;
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
            OneTimeToken = File.Exists(tokenPath) ? File.ReadAllText(tokenPath).Trim() : "";
            Guid parsed;
            if (!Guid.TryParseExact(OneTimeToken, "N", out parsed)) { OneTimeToken = Guid.NewGuid().ToString("N"); AtomicFile.WriteText(tokenPath, OneTimeToken); }
            LocalIp = DetectLanIp();
            if (string.IsNullOrEmpty(LocalIp) || LocalIp == "0.0.0.0")
            {
                Status = "No LAN address — connect the PS4 to the network, then restart pairing";
                PairUrl = "";
                Running = false;
                return;
            }
            PairUrl = "http://" + LocalIp + ":" + Port + "/pair/" + OneTimeToken;
            ExpiresUtc = DateTime.MaxValue;
            GotKey = false;
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
            get { return 86400; }
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
                if (method == "POST" && path.StartsWith("/pair/" + OneTimeToken + "/wallpaper"))
                { WriteResponse(stream, 410, "text/plain", "Background uploads have been removed."); return; }
                int contentLength = ParseContentLength(headers);
                if (contentLength < 0 || ParseHeader(headers, "Transfer-Encoding") != null)
                { WriteResponse(stream, 400, "text/plain", "Invalid request length"); return; }
                string contentType = ParseHeader(headers, "Content-Type") ?? "";
                byte[] bodyBytes = ReadBody(stream, extra, contentLength);
                if (bodyBytes == null)
                { WriteResponse(stream, 400, "text/plain", "Incomplete request; nothing saved"); return; }
                string body = bodyBytes == null ? "" : Encoding.UTF8.GetString(bodyBytes);

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
                            "This five-minute session expired. Return to SSPI and choose Restart QR pair.",
                            "TIMEOUT", true, null));
                    return;
                }
                if (method == "GET" && (path == pairPrefix || path.StartsWith(pairPrefix + "?")))
                {
                    WriteResponse(stream, 200, "text/html; charset=utf-8", PairHtml());
                    return;
                }
                if (method == "POST" && path.StartsWith(pairPrefix + "/wallpaper"))
                { WriteResponse(stream, 410, "text/plain", "Background uploads have been removed. Update the pairing page."); return; }
                if (Settings != null && method == "GET" && path == pairPrefix + "/config")
                { WriteResponse(stream, 200, "application/json", ConfigJson()); return; }
                if (Settings != null && method == "POST" &&
                    (path == pairPrefix + "/services" || path == pairPrefix + "/source" || path == pairPrefix + "/appearance" || path == pairPrefix + "/source-toggle"))
                {
                    string error = SaveConfiguration(path.Substring(pairPrefix.Length), body);
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
            return @"<!doctype html><html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><title>SSPI · Console setup</title><style>
:root{color-scheme:dark;font:16px system-ui;background:#101010;color:#eee}*{box-sizing:border-box}body{margin:0}main{max-width:960px;margin:36px auto;padding:24px}header{display:flex;justify-content:space-between;align-items:center;border-bottom:1px solid #353535;padding-bottom:24px}.brand{font-size:25px;letter-spacing:3px;font-weight:750}.muted,small{color:#aaa}h1{font-size:36px;margin:32px 0 8px}nav{display:flex;gap:8px;margin:28px 0;flex-wrap:wrap}button{border:1px solid #484848;border-radius:12px;background:#292929;color:#eee;padding:13px 20px;cursor:pointer;font:inherit}nav button.active,button.save{background:#e3e3df;color:#161616;border-color:#e3e3df}section[hidden]{display:none}.card{border:1px solid #393939;border-radius:18px;padding:24px;background:#202020;margin:16px 0}.providers{display:grid;grid-template-columns:1fr 1fr;gap:18px}.providers .card{margin:0}.logo{display:flex;gap:12px;align-items:center;font-size:24px;font-weight:700;margin-bottom:20px}.mark{display:grid;place-items:center;border-radius:10px;background:#333;width:46px;height:46px;color:#89c7a7}.rd{color:#f1a14b}label{display:block;margin:18px 0 9px}input,select{width:100%;font:inherit;color:#eee;background:#141414;border:1px solid #494949;border-radius:10px;padding:13px}input[type=checkbox]{width:auto}input[type=color]{height:50px}a{color:#ccc}#notice{min-height:28px;color:#9bd2b4;margin:20px 0}#sources{white-space:pre-wrap;line-height:1.9}.actions{display:flex;gap:12px;align-items:center;margin-top:20px;flex-wrap:wrap}footer{color:#888;font-size:13px;margin-top:36px}@media(max-width:640px){main{margin:0;padding:20px}.providers{grid-template-columns:1fr}h1{font-size:30px}header .muted{font-size:12px}}
</style></head><body><main><header><div class=""brand"" style=""display:flex;align-items:center;gap:12px"">__SSPI_BRAND__SSPI</div><span class=""muted"">YOUR PS4 · CONNECTED SETUP</span></header><h1>Make it yours.</h1><p class=""muted"">Link your services, add sources, and choose your accent and background.</p><nav><button class=""active"" data-tab=""services"">Link services</button><button data-tab=""packages"">Package sources</button><button data-tab=""appearance"">Appearance</button></nav><div id=""notice"" role=""status"">Loading saved configuration…</div>
<section id=""services""><div class=""providers""><div class=""card""><div class=""logo""><span class=""mark rd"">RD</span>Real-Debrid</div><small id=""rdSaved""></small><label for=""key"">API key</label><input type=""password"" id=""key"" autocomplete=""off"" placeholder=""Paste key; blank keeps saved key""><p><a href=""https://real-debrid.com/apitoken"" target=""_blank"" rel=""noreferrer"">Get your API key ↗</a></p></div><div class=""card""><div class=""logo""><span class=""mark""><svg width=""32"" height=""32"" viewBox=""0 0 32 32"" fill=""none"" stroke=""currentColor"" stroke-width=""2""><path d=""M16 3 29 10v13l-13 7L3 23V10zM3 10l13 7 13-7M16 17v13""/></svg></span>TorBox</div><small id=""tbSaved""></small><label for=""tb_key"">API key</label><input type=""password"" id=""tb_key"" autocomplete=""off"" placeholder=""Paste key; blank keeps saved key""><p><a href=""https://torbox.app/settings"" target=""_blank"" rel=""noreferrer"">Open TorBox settings ↗</a></p></div></div><div class=""card""><label for=""provider"">Preferred download service</label><select id=""provider""><option value=""real-debrid"">Real-Debrid</option><option value=""torbox"">TorBox</option><option value=""none"">Direct links · no debrid</option></select><div class=""actions""><button class=""save"" id=""saveKeys"">Save services</button><small>Both keys are kept when you switch tabs.</small></div></div></section>
<section id=""packages"" hidden><div class=""card""><h2>Package sources</h2><p class=""muted"">Paste a .gssource / .gsource URL. Your PS4 validates and installs the source.</p><label for=""source_url"">Source URL</label><input id=""source_url"" type=""url"" placeholder=""https://…/provider.gssource""><div class=""actions""><button class=""save"" id=""installSource"">Install source on PS4</button></div></div><div class=""card""><h2>Installed on your PS4</h2><div id=""sources"">Checking…</div><p id=""sourceStatus"" class=""muted""></p></div></section>
<section id=""appearance"" hidden><div class=""card""><h2>Appearance</h2><label for=""accent"">Choose your accent</label><input id=""accent"" type=""color"" value=""#E4E4E1""><label for=""background"">Built-in background</label><select id=""background""><option value=""solid"">Plain charcoal</option><option value=""ripple"">Ripple</option><option value=""wave"">Wave</option><option value=""grid"">Mosaic</option><option value=""halo"">Halo</option><option value=""graphite"">Graphite fade</option><option value=""obsidian"">Obsidian</option><option value=""slate"">Slate glow</option></select><p>Choose darker gradients or soft accent patterns. No image uploads.</p><div class=""actions""><button class=""save"" id=""saveAppearance"">Save appearance</button><button id=""restoreAppearance"">Restore original charcoal</button></div></div></section><footer>Saved on your PS4. Keep SSPI open and both devices on the same network. Blank key fields keep your saved credentials.</footer></main><script>
const base=location.pathname.replace(/\/$/,''),$=id=>document.getElementById(id);
let dirty=false,appearanceDirty=false,sourceState='',chain=Promise.resolve();
function note(s){$('notice').textContent=s}
async function request(path,body){const r=await fetch(base+path,{method:body?'POST':'GET',body,cache:'no-store'});if(!r.ok)throw Error(await r.text());return r.json()}
function renderSources(c){const choices=c.source_choices||[],stamp=JSON.stringify(choices);if(sourceState===stamp)return;sourceState=stamp;$('sources').replaceChildren();if(!choices.length){$('sources').textContent=c.sources||'No package sources installed yet';return}for(const source of choices){const row=document.createElement('div'),label=document.createElement('span'),button=document.createElement('button');row.className='actions';label.textContent=source.name+' · v'+source.version;label.style.flex='1';button.textContent=source.enabled?'Enabled':'Disabled';button.setAttribute('aria-pressed',String(source.enabled));button.onclick=()=>{button.disabled=true;enqueue(async()=>{await saveKeys();await request('/source-toggle',new URLSearchParams({source_id:source.id,enabled:source.enabled?'off':'on'}));sourceState='';await refresh();note('Source selection saved on PS4')}).catch(()=>{}).finally(()=>button.disabled=false)};row.append(label,button);$('sources').append(row)}}
async function refresh(initial=false){let c=await request('/config');$('rdSaved').textContent=c.rd?'Key saved on PS4':'Not connected';$('tbSaved').textContent=c.tb?'Key saved on PS4':'Not connected';renderSources(c);$('sourceStatus').textContent=c.source_status||'';if(!dirty)$('provider').value=c.provider;if(!appearanceDirty){$('accent').value=c.accent;$('background').value=c.background||'solid'}if(initial)note('Saved configuration loaded.')}
async function saveKeys(){while(dirty){const saved={key:$('key').value,tb_key:$('tb_key').value,provider:$('provider').value};await request('/services',new URLSearchParams(saved));dirty=Object.keys(saved).some(k=>$(k).value!==saved[k]);if(!dirty){$('key').value='';$('tb_key').value=''}}await refresh()}
async function saveAppearance(){await saveKeys();const saved={accent:$('accent').value,background:$('background').value};await request('/appearance',new URLSearchParams(saved));appearanceDirty=Object.keys(saved).some(k=>$(k).value!==saved[k]);note('Appearance saved on PS4')}
function enqueue(fn){chain=chain.catch(()=>{}).then(fn).catch(e=>{note(e.message);throw e});return chain}
['key','tb_key','provider'].forEach(k=>$(k).addEventListener('input',()=>dirty=true));
['accent','background'].forEach(k=>$(k).addEventListener('input',()=>appearanceDirty=true));
document.querySelectorAll('nav button').forEach(b=>b.onclick=()=>enqueue(async()=>{await saveKeys();if(appearanceDirty)await saveAppearance();document.querySelectorAll('section').forEach(s=>s.hidden=s.id!==b.dataset.tab);document.querySelectorAll('nav button').forEach(t=>t.classList.toggle('active',t===b))}).catch(()=>{}));
$('saveKeys').onclick=()=>enqueue(async()=>{await saveKeys();note('Services saved on PS4')}).catch(()=>{});
$('installSource').onclick=()=>enqueue(async()=>{await saveKeys();await request('/source',new URLSearchParams({source_url:$('source_url').value}));note('Source sent to PS4 — installing…')}).catch(()=>{});
$('saveAppearance').onclick=()=>enqueue(saveAppearance).catch(()=>{});
$('restoreAppearance').onclick=()=>{appearanceDirty=true;$('background').value='solid';enqueue(saveAppearance).catch(()=>{})};
refresh(true).catch(e=>note(e.message));setInterval(()=>refresh().catch(()=>{}),3000);window.addEventListener('beforeunload',e=>{if(dirty||appearanceDirty){e.preventDefault();e.returnValue=''}});
</script></body></html>".Replace("__SSPI_BRAND__", PairBrandMarkup());
        }

        string ConfigJson()
        {
            var c = Settings;
            return "{\"rd\":" + (c.HasRealDebrid ? "true" : "false") + ",\"tb\":" + (c.HasTorBox ? "true" : "false") +
                ",\"provider\":\"" + JsonLite.Escape(c.UnlockProviderId) + "\",\"accent\":\"" + JsonLite.Escape(c.Accent) +
                "\",\"background\":\"" + JsonLite.Escape(c.BackgroundMode) + "\",\"mbps\":" + c.ConnectionMbps +
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
                string oldRd = c.RealDebridToken, oldTb = c.TorBoxApiKey, oldProvider = c.UnlockProviderId;
                string oldAccent = c.Accent, oldAccentName = c.AccentName, oldBackground = c.BackgroundMode;
                bool oldUse = c.UseUnlockProvider, oldUseRd = c.UseRealDebrid;
                string oldPending = PendingSource, oldStatus = SourceStatus;
                if (route == "/services")
                {
                    string rd = (ParseForm(body, "key") ?? "").Trim(), tb = (ParseForm(body, "tb_key") ?? "").Trim();
                    string provider = ParseForm(body, "provider") ?? c.UnlockProviderId;
                    if (provider != "real-debrid" && provider != "torbox" && provider != "none") return "Choose Real-Debrid or TorBox";
                    if ((rd.Length > 0 && rd.Length < 8) || (tb.Length > 0 && tb.Length < 8) || rd.Length > 4096 || tb.Length > 4096 ||
                        rd.IndexOfAny(new[] {'\r','\n','\0'}) >= 0 || tb.IndexOfAny(new[] {'\r','\n','\0'}) >= 0) return "Check the API key length and remove line breaks";
                    if (provider == "real-debrid" && rd.Length == 0 && !c.HasRealDebrid || provider == "torbox" && tb.Length == 0 && !c.HasTorBox) return "Paste the selected provider's API key";
                    if (rd.Length > 0) c.RealDebridToken = rd;
                    if (tb.Length > 0) c.TorBoxApiKey = tb;
                    c.UnlockProviderId = provider; c.UseUnlockProvider = provider != "none"; c.UseRealDebrid = provider == "real-debrid";
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
                    c.Accent = color.ToHex(); c.AccentName = "Custom";
                    string background = ParseForm(body, "background");
                    if (background != null) c.BackgroundMode = BackdropPattern.Modes[BackdropPattern.Index(background)];
                    c.ValidateAppearance(false);
                }
                else return "Unknown configuration section";
                if (!c.Save()) {
                    c.RealDebridToken = oldRd; c.TorBoxApiKey = oldTb; c.UnlockProviderId = oldProvider;
                    c.UseUnlockProvider = oldUse; c.UseRealDebrid = oldUseRd;
                    c.Accent = oldAccent; c.AccentName = oldAccentName; c.BackgroundMode = oldBackground;
                    PendingSource = oldPending; SourceStatus = oldStatus;
                    return "The PS4 could not save settings. Try again.";
                }
                Interlocked.Increment(ref Revision); return null;
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
