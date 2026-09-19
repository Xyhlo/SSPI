using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using static SDL2.SDL;

namespace Orbis
{
    /// <summary>Downloads encoded covers off-thread and uploads decoded RGBA on the render thread.</summary>
    internal sealed class CoverCache
    {
        const int MaxEncodedBytes = 8 * 1024 * 1024;
        const int MaxTextures = 24;
        const int MaxTextureBytes = 16 * 1024 * 1024;
        const int MaxPendingRequests = 24;
        const int FirstRetrySeconds = 6;
        internal const int UploadBytesPerFrame = 256 * 1024;
        static readonly Queue<string> PendingLogs = new Queue<string>();
        static bool _logDrainScheduled;

        readonly object _lock = new object();
        readonly Queue<Req> _q = new Queue<Req>();
        readonly Dictionary<string, ReadyImage> _ready = new Dictionary<string, ReadyImage>();
        readonly Dictionary<string, IntPtr> _tex = new Dictionary<string, IntPtr>();
        readonly Dictionary<string, TextureSize> _texSize = new Dictionary<string, TextureSize>();
        readonly Dictionary<string, long> _failUntil = new Dictionary<string, long>();
        readonly Dictionary<string, int> _failCount = new Dictionary<string, int>();
        readonly Dictionary<string, long> _lastUse = new Dictionary<string, long>();
        // Worker-only metadata cache; one title may be drawn at several sizes.
        readonly Dictionary<string, string> _fallbackUrls = new Dictionary<string, string>();
        readonly Dictionary<string, long> _fallbackUntil = new Dictionary<string, long>();
        readonly HashSet<string> _inflight = new HashSet<string>();
        readonly HashSet<string> _uploading = new HashSet<string>();
        readonly HashSet<IntPtr> _firstDraw = new HashSet<IntPtr>();
        readonly Thread _worker;
        volatile bool _run = true;
        int _viewGen;
        long _useClock;
        long _textureBytes;
        Upload _pendingUpload;

        sealed class Upload
        {
            public string Key;
            public ReadyImage Image;
            public IntPtr Texture;
            public int Row;
        }

        struct Req
        {
            public string Key;
            public string Url;
            public int Gen;
            public int Width, Height;
        }

        sealed class ReadyImage
        {
            public byte[] Pixels;
            public int Width;
            public int Height;
            public bool Opaque;
            public int Generation;
        }

        struct TextureSize
        {
            public int Width;
            public int Height;
        }

        public CoverCache()
        {
            try
            {
                AppendLog("cover session started");
            }
            catch { }
            _worker = new Thread(Worker) { IsBackground = true, Name = "Covers" };
            _worker.Start();
        }

        /// <summary>Drop work for the previous result view; decoded textures remain globally useful.</summary>
        public void BumpGeneration()
        {
            lock (_lock)
            {
                Interlocked.Increment(ref _viewGen);
                while (_q.Count > 0)
                {
                    Req stale = _q.Dequeue();
                    if (stale.Key != null) _inflight.Remove(stale.Key);
                }
                // An old view's ready buffers do not need to occupy the next view's budget.
                _ready.Clear();
            }
        }

        public void Request(string titleId, string imageUrl)
        {
            if (string.IsNullOrEmpty(titleId)) return;
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                if (ArtworkTitleId(titleId) != null) Enqueue(titleId.ToUpperInvariant(), "lookup|");
                return;
            }
            if (imageUrl.IndexOf("://", StringComparison.Ordinal) < 0 &&
                (imageUrl.IndexOf('\\') >= 0 || imageUrl.StartsWith("/", StringComparison.Ordinal)))
            {
                RequestLocal(titleId, imageUrl);
                return;
            }
            string key = titleId.ToUpperInvariant();
            string url = NormalizeUrl(imageUrl);
            if (string.IsNullOrEmpty(url))
            {
                AppendLog(key + " reject URL " + SafeUrl(imageUrl));
                return;
            }
            Enqueue(key, url);
        }

        public void RequestLocal(string titleId, string filePath)
        {
            if (string.IsNullOrEmpty(titleId) || string.IsNullOrEmpty(filePath)) return;
            // Existence and decoding belong to Worker, never to a navigation frame.
            Enqueue(titleId.ToUpperInvariant(), "local|" + filePath);
        }

        public string RequestSized(string titleId, string url, int width, int height)
        {
            string key = ((titleId ?? "") + "@" + width + "x" + height + "-" + (url ?? "").GetHashCode().ToString("x8")).ToUpperInvariant();
            lock (_lock) if (_tex.ContainsKey(key) || _inflight.Contains(key) || _ready.ContainsKey(key) || _uploading.Contains(key)) return key;
            if (string.IsNullOrWhiteSpace(url))
            {
                if (ArtworkTitleId(titleId) != null) Enqueue(key, "lookup|", width, height);
                return key;
            }
            bool local = url.IndexOf("://", StringComparison.Ordinal) < 0 && (url.StartsWith("/") || url.IndexOf('\\') >= 0);
            string source = local ? "local|" + url : NormalizeUrl(url);
            if (!string.IsNullOrEmpty(source)) Enqueue(key, source, width, height);
            return key;
        }

        void Enqueue(string key, string url, int width = 0, int height = 0)
        {
            lock (_lock)
            {
                if (_tex.ContainsKey(key) || _ready.ContainsKey(key) || _inflight.Contains(key) ||
                    _uploading.Contains(key)) return;
                if (_q.Count >= MaxPendingRequests) return; // Visible rows retry on their next draw.
                long until;
                if (_failUntil.TryGetValue(key, out until) && until > DateTime.UtcNow.Ticks) return;
                _inflight.Add(key);
                _q.Enqueue(new Req { Key = key, Url = url, Gen = _viewGen, Width = width, Height = height });
            }
        }

        public void PumpReady(IntPtr renderer, int maxPerFrame = 1)
        {
            if (renderer == IntPtr.Zero) return;
            int count = 0, budget = UploadBytesPerFrame;
            while (count < maxPerFrame && budget > 0)
            {
                if (_pendingUpload == null)
                {
                    lock (_lock)
                    {
                        foreach (var kv in _ready)
                        {
                            _pendingUpload = new Upload { Key = kv.Key, Image = kv.Value };
                            break;
                        }
                        if (_pendingUpload != null)
                        {
                            _ready.Remove(_pendingUpload.Key);
                            _uploading.Add(_pendingUpload.Key);
                        }
                    }
                }
                Upload upload = _pendingUpload;
                if (upload == null) break;
                string key = upload.Key;
                ReadyImage image = upload.Image;
                bool finished = false;
                GCHandle pin = default(GCHandle);
                try
                {
                    if (image.Generation != _viewGen) { finished = true; continue; }
                    if (image.Width <= 0 || image.Height <= 0 || image.Pixels == null ||
                        image.Width > 1440 || image.Height > 512 ||
                        (long)image.Width * image.Height * 4 != image.Pixels.Length)
                        throw new InvalidDataException("Invalid cover pixel buffer");
                    if (upload.Texture == IntPtr.Zero)
                    {
                        // Same byte layout, but opaque images need the exact
                        // framebuffer format to select SDL's direct-copy path.
                        upload.Texture = SDL_CreateTexture(renderer, image.Opaque ? SDL_PIXELFORMAT_BGR888 : SDL_PIXELFORMAT_ABGR8888,
                            (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STATIC, image.Width, image.Height);
                        if (upload.Texture == IntPtr.Zero)
                            throw new Exception("SDL_CreateTexture: " + SDL_GetError());
                        SDL_SetTextureBlendMode(upload.Texture, image.Opaque ? SDL_BlendMode.SDL_BLENDMODE_NONE : SDL_BlendMode.SDL_BLENDMODE_BLEND);
                    }
                    int pitch = image.Width * 4;
                    int rows = Math.Min(image.Height - upload.Row, budget / pitch);
                    if (rows == 0) break;
                    var area = new SDL_Rect { x = 0, y = upload.Row, w = image.Width, h = rows };
                    pin = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
                    int rc = SDL_UpdateTexture(upload.Texture, ref area,
                        IntPtr.Add(pin.AddrOfPinnedObject(), upload.Row * pitch), pitch);
                    if (rc != 0)
                        throw new Exception("SDL_UpdateTexture: " + SDL_GetError());
                    upload.Row += rows;
                    budget -= rows * pitch;
                    if (upload.Row != image.Height) break;
                    lock (_lock)
                    {
                        if (image.Generation != _viewGen) { finished = true; continue; }
                        while (_tex.Count > 0 && (_tex.Count >= MaxTextures ||
                            _textureBytes + image.Pixels.Length > MaxTextureBytes)) EvictOne();
                        IntPtr old;
                        if (_tex.TryGetValue(key, out old) && old != IntPtr.Zero)
                        {
                            TextureSize oldSize;
                            if (_texSize.TryGetValue(key, out oldSize)) _textureBytes -= (long)oldSize.Width * oldSize.Height * 4;
                            _firstDraw.Remove(old);
                            SDL_DestroyTexture(old);
                        }
                        _tex[key] = upload.Texture;
                        _texSize[key] = new TextureSize
                        {
                            Width = image.Width,
                            Height = image.Height
                        };
                        _lastUse[key] = ++_useClock;
                        _textureBytes += image.Pixels.Length;
                        _firstDraw.Add(upload.Texture);
                        _failUntil.Remove(key);
                        _failCount.Remove(key);
                    }
                    upload.Texture = IntPtr.Zero;
                    finished = true;
                    AppendLog(key + " texture-ready");
                }
                catch (Exception ex)
                {
                    AppendLog(key + " texture FAIL " + ex.Message);
                    lock (_lock) RecordFailure(key);
                    finished = true;
                }
                finally
                {
                    if (pin.IsAllocated) pin.Free();
                    if (finished)
                    {
                        if (upload.Texture != IntPtr.Zero) try { SDL_DestroyTexture(upload.Texture); } catch { }
                        lock (_lock) _uploading.Remove(key);
                        _pendingUpload = null;
                        count++;
                    }
                }
            }
        }

        public bool TryGet(string titleId, out IntPtr texture)
        {
            int width;
            int height;
            return TryGet(titleId, out texture, out width, out height);
        }

        /// <summary>Returns a cached texture and its decoded dimensions for aspect-fit drawing.</summary>
        public bool TryGet(string titleId, out IntPtr texture, out int width, out int height)
        {
            texture = IntPtr.Zero;
            width = 0;
            height = 0;
            if (string.IsNullOrEmpty(titleId)) return false;
            string key = titleId.ToUpperInvariant();
            lock (_lock)
            {
                TextureSize size;
                if (!_tex.TryGetValue(key, out texture) || texture == IntPtr.Zero ||
                    !_texSize.TryGetValue(key, out size))
                    return false;
                width = size.Width;
                height = size.Height;
                _lastUse[key] = ++_useClock;
                return width > 0 && height > 0;
            }
        }

        void EvictOne()
        {
            string key = null;
            long oldest = long.MaxValue;
            foreach (var kv in _tex)
            {
                long used;
                if (!_lastUse.TryGetValue(kv.Key, out used)) used = 0;
                if (used >= oldest) continue;
                oldest = used;
                key = kv.Key;
            }
            if (key == null) return;
            IntPtr texture = _tex[key];
            TextureSize size;
            if (_texSize.TryGetValue(key, out size)) _textureBytes -= (long)size.Width * size.Height * 4;
            _firstDraw.Remove(texture);
            _tex.Remove(key);
            _texSize.Remove(key);
            _lastUse.Remove(key);
            if (texture != IntPtr.Zero) try { SDL_DestroyTexture(texture); } catch { }
        }

        void Worker()
        {
            while (_run)
            {
                Req request;
                lock (_lock)
                    request = _q.Count == 0 || _ready.Count >= 2 ? default(Req) : _q.Dequeue();
                if (request.Key == null)
                {
                    Thread.Sleep(100);
                    continue;
                }
                string path = null;
                bool ownsCacheFile = true;
                try
                {
                    AppendLog(request.Key + " request begin gen=" + request.Gen);
                    ReadyImage decoded;
                    try { decoded = LoadRequestImage(request, request.Url, ref path, ref ownsCacheFile); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception original)
                    {
                        if (request.Gen != _viewGen) throw new OperationCanceledException();
                        try { if (ownsCacheFile && File.Exists(path)) File.Delete(path); } catch { }
                        string fallback = FindFallback(request.Key, request.Url);
                        if (string.IsNullOrEmpty(fallback)) throw;
                        AppendLog(request.Key + " fallback after " + original.GetType().Name + " to " + SafeUrl(fallback));
                        decoded = LoadRequestImage(request, fallback, ref path, ref ownsCacheFile);
                    }
                    string digest;
                    using (var sha = SHA256.Create())
                        digest = BitConverter.ToString(sha.ComputeHash(decoded.Pixels)).Replace("-", "").ToLowerInvariant();
                    AppendLog(request.Key + " pixels sha256=" + digest + " managed_bytes=" + GC.GetTotalMemory(false));

                    lock (_lock)
                    {
                        if (request.Gen == _viewGen)
                            _ready[request.Key] = decoded;
                        _failUntil.Remove(request.Key);
                        _failCount.Remove(request.Key);
                    }
                    AppendLog(request.Key + " decode OK " + decoded.Width + "x" + decoded.Height +
                              " bytes=" + new FileInfo(path).Length);
                }
                catch (OperationCanceledException)
                {
                    if (!string.IsNullOrEmpty(path) && ownsCacheFile) DownloadResumeInfo.DeletePartial(path + ".part");
                    AppendLog(request.Key + " cancelled stale cover");
                }
                catch (Exception ex)
                {
                    string file = DescribeFile(path);
                    try { if (ownsCacheFile && File.Exists(path)) File.Delete(path); } catch { }
                    if (!string.IsNullOrEmpty(path) && ownsCacheFile) DownloadResumeInfo.DeletePartial(path + ".part");
                    lock (_lock) RecordFailure(request.Key);
                    AppendLog(request.Key + " FAIL " + ex.GetType().Name + ": " + ex.Message +
                              " file=" + file);
                }
                finally
                {
                    lock (_lock) _inflight.Remove(request.Key);
                }
            }
        }

        ReadyImage LoadRequestImage(Req request, string source, ref string path, ref bool ownsCacheFile)
        {
            if (request.Gen != _viewGen) throw new OperationCanceledException();
            // Cache the replacement under the original request so the next size or
            // application launch does not repeat a failed URL or metadata lookup.
            path = DiskPath(request.Key, request.Url);
            ownsCacheFile = true;
            if (!File.Exists(path) || new FileInfo(path).Length < 100)
            {
                if (source.StartsWith("lookup|", StringComparison.Ordinal)) throw new IOException("No source artwork supplied");
                if (source.StartsWith("local|", StringComparison.Ordinal))
                {
                    string localPath = source.Substring(6);
                    try { File.Copy(localPath, path, true); }
                    catch { path = localPath; ownsCacheFile = false; }
                }
                else
                {
                    DownloadResumeInfo.DeletePartial(path + ".part");
                    AppendLog(request.Key + " artwork-http begin");
                    NetHttp.DownloadArtwork(source, path, () => request.Gen != _viewGen, MaxEncodedBytes);
                    AppendLog(request.Key + " artwork-http complete");
                }
            }
            if (request.Gen != _viewGen) throw new OperationCanceledException();
            long size = new FileInfo(path).Length;
            if (size < 100 || size > MaxEncodedBytes) throw new IOException("encoded size " + size);
            var pixels = CoverImageDecoder.Decode(path, request.Width, request.Height,
                request.Key.EndsWith("-BACKDROP", StringComparison.Ordinal),
                stage => AppendLog(request.Key + " " + stage), request.Width > 0);
            return new ReadyImage { Pixels = pixels.Pixels, Width = pixels.Width, Height = pixels.Height,
                Opaque = pixels.Opaque, Generation = request.Gen };
        }

        string FindFallback(string key, string source)
        {
            string titleId = ArtworkTitleId(key);
            if (titleId == null) return null;
            string cacheKey = titleId + "|" + source;
            long until;
            if (_fallbackUntil.TryGetValue(cacheKey, out until) && until > DateTime.UtcNow.Ticks)
                return _fallbackUrls[cacheKey];
            string result = null;
            foreach (string root in new[] { "/system_data/priv/appmeta/", "/user/appmeta/", "/mnt/ext0/user/appmeta/" })
            {
                string local = root + titleId + "/icon0.png";
                if (File.Exists(local) && source != "local|" + local) { result = "local|" + local; break; }
            }
            if (result == null)
            {
                try { result = SelectFallbackArtwork(titleId, source, OrbisClient.Search(titleId, 8)); }
                catch (Exception ex) { AppendLog(titleId + " artwork lookup failed " + ex.GetType().Name); }
            }
            if (_fallbackUrls.Count >= 128) { _fallbackUrls.Clear(); _fallbackUntil.Clear(); }
            _fallbackUrls[cacheKey] = result;
            _fallbackUntil[cacheKey] = DateTime.UtcNow.AddMinutes(result == null ? 2 : 30).Ticks;
            return result;
        }

        internal static string ArtworkTitleId(string key)
        {
            string title = (key ?? "").ToUpperInvariant();
            int variant = title.IndexOf('@');
            if (variant >= 0) title = title.Substring(0, variant);
            if (title.EndsWith("-BACKDROP", StringComparison.Ordinal)) title = title.Substring(0, title.Length - 9);
            if (title.Length != 9 || !title.StartsWith("CUSA", StringComparison.Ordinal)) return null;
            for (int i = 4; i < title.Length; i++) if (title[i] < '0' || title[i] > '9') return null;
            return title;
        }

        internal static string SelectFallbackArtwork(string titleId, string source, IList<GameHit> hits)
        {
            if (ArtworkTitleId(titleId) == null || hits == null) return null;
            foreach (var hit in hits)
            {
                if (hit == null || !string.Equals(hit.TitleId, titleId, StringComparison.OrdinalIgnoreCase) ||
                    string.IsNullOrWhiteSpace(hit.ImageUrl)) continue;
                string url = NormalizeUrl(hit.ImageUrl);
                Uri parsed;
                if (url != null && Uri.TryCreate(url, UriKind.Absolute, out parsed) &&
                    string.IsNullOrEmpty(parsed.UserInfo) && !string.Equals(url, source, StringComparison.Ordinal)) return url;
            }
            return null;
        }

        public bool HasFailed(string key)
        {
            lock (_lock) return _failUntil.ContainsKey((key ?? "").ToUpperInvariant());
        }

        void RecordFailure(string key)
        {
            int count;
            if (!_failCount.TryGetValue(key, out count)) count = 0;
            count++;
            _failCount[key] = count;
            // Retry transient CDN/TLS failures quickly once, then back off persistent bad URLs.
            int seconds = count == 1 ? FirstRetrySeconds : (count == 2 ? 30 : 120);
            _failUntil[key] = DateTime.UtcNow.AddSeconds(seconds).Ticks;
        }

        public void Draw(IntPtr renderer, IntPtr texture, ref SDL_Rect destination)
        {
            bool first;
            lock (_lock) first = _firstDraw.Remove(texture);
            if (first) AppendLog("texture-first-draw begin " + destination.w + "x" + destination.h);
            int result = SDL_RenderCopy(renderer, texture, IntPtr.Zero, ref destination);
            if (first || result != 0) AppendLog("texture-first-draw result=" + result);
        }

        public void Draw(IntPtr renderer, IntPtr texture, ref SDL_Rect source, ref SDL_Rect destination)
        {
            bool first;
            lock (_lock) first = _firstDraw.Remove(texture);
            if (first) AppendLog("texture-first-draw begin " + source.w + "x" + source.h +
                " to=" + destination.w + "x" + destination.h);
            int result = SDL_RenderCopy(renderer, texture, ref source, ref destination);
            if (first || result != 0) AppendLog("texture-first-draw result=" + result);
        }

        static string NormalizeUrl(string value)
        {
            string url = (value ?? "").Trim()
                .Replace("&amp;", "&").Replace("&#38;", "&");
            if (url.StartsWith("//", StringComparison.Ordinal)) url = "https:" + url;
            else if (url.IndexOf("://", StringComparison.Ordinal) < 0) url = "https://" + url.TrimStart('/');
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) return null;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return null;
            return parsed.AbsoluteUri;
        }

        static string DescribeFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return "missing";
                long size = new FileInfo(path).Length;
                byte[] head = new byte[8];
                int read;
                using (var stream = File.OpenRead(path)) read = stream.Read(head, 0, head.Length);
                var hex = new StringBuilder();
                for (int i = 0; i < read; i++) hex.Append(head[i].ToString("x2"));
                return size + "b/" + hex;
            }
            catch (Exception ex) { return "inspect-" + ex.GetType().Name; }
        }

        static string DiskPath(string key, string url)
        {
            byte[] digest;
            using (var sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(url ?? ""));
            int variant = key.IndexOf('@');
            if (variant >= 0) key = key.Substring(0, variant);
            var name = new StringBuilder(key + "_");
            for (int i = 0; i < 8; i++) name.Append(digest[i].ToString("x2"));
            name.Append(".img");
            return Path.Combine(AppSettings.CoversDir, name.ToString());
        }

        static string SafeUrl(string value)
        {
            try { return new Uri(value).GetLeftPart(UriPartial.Path); }
            catch { return value ?? ""; }
        }

        static void AppendLog(string message)
        {
            lock (PendingLogs)
            {
                // Diagnostic disk latency must not stall artwork upload/drawing.
                if (PendingLogs.Count >= 256) return;
                PendingLogs.Enqueue(message);
                if (_logDrainScheduled) return;
                _logDrainScheduled = true;
            }
            ThreadPool.QueueUserWorkItem(_ => {
                while (true)
                {
                    string next;
                    lock (PendingLogs)
                    {
                        if (PendingLogs.Count == 0) { _logDrainScheduled = false; return; }
                        next = PendingLogs.Dequeue();
                    }
                    try { SspiLog.Write("startup", "cover " + next); } catch { }
                }
            });
        }
    }
}
