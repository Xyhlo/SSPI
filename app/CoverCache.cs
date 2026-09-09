using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using static SDL2.SDL;

namespace Orbis
{
    /// <summary>Downloads encoded covers off-thread and uploads decoded RGBA on the render thread.</summary>
    internal sealed class CoverCache
    {
        const int MaxEncodedBytes = 8 * 1024 * 1024;
        const int MaxPixels = 4 * 1024 * 1024;
        const int MaxTextureDimension = 512;
        const int MaxTextures = 24;
        const int FirstRetrySeconds = 6;

        readonly object _lock = new object();
        readonly Queue<Req> _q = new Queue<Req>();
        readonly Dictionary<string, ReadyImage> _ready = new Dictionary<string, ReadyImage>();
        readonly Dictionary<string, IntPtr> _tex = new Dictionary<string, IntPtr>();
        readonly Dictionary<string, TextureSize> _texSize = new Dictionary<string, TextureSize>();
        readonly Dictionary<string, long> _failUntil = new Dictionary<string, long>();
        readonly Dictionary<string, int> _failCount = new Dictionary<string, int>();
        readonly Dictionary<string, long> _lastUse = new Dictionary<string, long>();
        readonly HashSet<string> _inflight = new HashSet<string>();
        readonly HashSet<string> _uploading = new HashSet<string>();
        readonly Thread _worker;
        volatile bool _run = true;
        int _viewGen;
        long _useClock;

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
                string log = LogPath;
                if (File.Exists(log) && new FileInfo(log).Length > 256 * 1024)
                    File.Delete(log);
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
            }
        }

        public void Request(string titleId, string imageUrl)
        {
            if (string.IsNullOrEmpty(titleId) || string.IsNullOrEmpty(imageUrl)) return;
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
            try { if (!File.Exists(filePath)) return; } catch { return; }
            Enqueue(titleId.ToUpperInvariant(), "local|" + filePath);
        }

        public string RequestSized(string titleId, string url, int width, int height)
        {
            string key = (titleId ?? "").ToUpperInvariant() + "@" + width + "x" + height;
            lock (_lock) if (_tex.ContainsKey(key) || _inflight.Contains(key) || _ready.ContainsKey(key) || _uploading.Contains(key)) return key;
            if (string.IsNullOrEmpty(url)) return key;
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
                long until;
                if (_failUntil.TryGetValue(key, out until) && until > DateTime.UtcNow.Ticks) return;
                _inflight.Add(key);
                _q.Enqueue(new Req { Key = key, Url = url, Gen = _viewGen, Width = width, Height = height });
            }
        }

        public void PumpReady(IntPtr renderer, int maxPerFrame = 1)
        {
            if (renderer == IntPtr.Zero) return;
            int count = 0;
            while (count < maxPerFrame)
            {
                string key = null;
                ReadyImage image = null;
                lock (_lock)
                {
                    foreach (var kv in _ready)
                    {
                        key = kv.Key;
                        image = kv.Value;
                        break;
                    }
                    if (key != null)
                    {
                        _ready.Remove(key);
                        _uploading.Add(key);
                    }
                }
                if (image == null) break;

                IntPtr texture = IntPtr.Zero;
                GCHandle pin = default(GCHandle);
                try
                {
                    texture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ABGR8888,
                        (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STATIC, image.Width, image.Height);
                    if (texture == IntPtr.Zero)
                        throw new Exception("SDL_CreateTexture: " + SDL_GetError());

                    pin = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
                    int rc = SDL_UpdateTexture(texture, IntPtr.Zero, pin.AddrOfPinnedObject(), image.Width * 4);
                    if (rc != 0)
                        throw new Exception("SDL_UpdateTexture: " + SDL_GetError());
                    SDL_SetTextureBlendMode(texture, image.Opaque ? SDL_BlendMode.SDL_BLENDMODE_NONE : SDL_BlendMode.SDL_BLENDMODE_BLEND);

                    lock (_lock)
                    {
                        if (_tex.Count >= MaxTextures) EvictOne();
                        IntPtr old;
                        if (_tex.TryGetValue(key, out old) && old != IntPtr.Zero)
                            SDL_DestroyTexture(old);
                        _tex[key] = texture;
                        _texSize[key] = new TextureSize
                        {
                            Width = image.Width,
                            Height = image.Height
                        };
                        _lastUse[key] = ++_useClock;
                        _failUntil.Remove(key);
                        _failCount.Remove(key);
                    }
                    texture = IntPtr.Zero;
                }
                catch (Exception ex)
                {
                    AppendLog(key + " texture FAIL " + ex.Message);
                    lock (_lock) RecordFailure(key);
                }
                finally
                {
                    if (pin.IsAllocated) pin.Free();
                    if (texture != IntPtr.Zero) try { SDL_DestroyTexture(texture); } catch { }
                    lock (_lock) _uploading.Remove(key);
                }
                count++;
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
                    request = _q.Count == 0 || _ready.Count >= 4 ? default(Req) : _q.Dequeue();
                if (request.Key == null)
                {
                    Thread.Sleep(100);
                    continue;
                }
                bool local = request.Url != null &&
                    request.Url.StartsWith("local|", StringComparison.Ordinal);
                string path = local ? DiskPath(request.Key, request.Url) : DiskPath(request.Key, request.Url);
                try
                {
                    if (local)
                    {
                        string sourcePath = request.Url.Substring(6);
                        if (!File.Exists(path) || new FileInfo(path).Length < 100)
                        {
                            try { File.Copy(sourcePath, path, true); }
                            catch { path = sourcePath; }
                        }
                    }
                    else if (!File.Exists(path) || new FileInfo(path).Length < 100)
                    {
                        DownloadResumeInfo.DeletePartial(path + ".part");
                        NetHttp.DownloadFileResumable(request.Url, path, 0, (done, total) =>
                            {
                                if (done > MaxEncodedBytes || total > MaxEncodedBytes)
                                    throw new Exception("encoded response exceeds " + MaxEncodedBytes);
                            }, () => request.Gen != _viewGen, 30000, null, null);
                        AppendLog(request.Key + " HTTP OK " + SafeUrl(request.Url));
                    }

                    if (request.Gen != _viewGen)
                        throw new OperationCanceledException("stale cover view");

                    long encodedSize = new FileInfo(path).Length;
                    if (encodedSize < 100 || encodedSize > MaxEncodedBytes)
                        throw new Exception("encoded size " + encodedSize);

                    IImageInfo info = Image.Identify(path);
                    long identifiedPixels = info == null ? 0 : (long)info.Width * info.Height;
                    if (info == null || info.Width < 16 || info.Height < 16 || identifiedPixels > MaxPixels)
                        throw new Exception("bad dimensions " +
                            (info == null ? "unknown" : info.Width + "x" + info.Height));

                    ReadyImage decoded;
                    // Auto-detect format (Orbis icons may be WebP/JPEG/PNG).
                    using (Image<Rgba32> image = Image.Load<Rgba32>(path))
                    {
                        if (request.Key.EndsWith("-BACKDROP", StringComparison.Ordinal))
                        {
                            // Prepare the soft pocket backdrop once on the cover worker; no frame-time blur.
                            image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(1440, 320), Mode = ResizeMode.Crop }).GaussianBlur(5f));
                            for (int yy = 0; yy < image.Height; yy++)
                                for (int xx = 0; xx < image.Width; xx++)
                                {
                                    var pixel = image[xx, yy];
                                    double shade = .07 + .30 * xx / (double)image.Width;
                                    image[xx, yy] = new Rgba32((byte)(24 + pixel.R * shade), (byte)(24 + pixel.G * shade), (byte)(24 + pixel.B * shade), 255);
                                }
                        }
                        else if (request.Width > 0 && request.Height > 0)
                        {
                            image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(request.Width, request.Height), Mode = ResizeMode.Crop, Sampler = KnownResamplers.Lanczos3 }));
                        }
                        else if (image.Width > MaxTextureDimension || image.Height > MaxTextureDimension)
                        {
                            double scale = Math.Min((double)MaxTextureDimension / image.Width,
                                (double)MaxTextureDimension / image.Height);
                            int width = Math.Max(1, (int)Math.Round(image.Width * scale));
                            int height = Math.Max(1, (int)Math.Round(image.Height * scale));
                            image.Mutate(x => x.Resize(width, height));
                        }
                        long pixels = (long)image.Width * image.Height;
                        byte[] rgba = new byte[checked((int)pixels * 4)];
                        image.CopyPixelDataTo(rgba);
                        bool opaque = true;
                        for (int alpha = 3; alpha < rgba.Length; alpha += 4) if (rgba[alpha] != 255) { opaque = false; break; }
                        decoded = new ReadyImage { Pixels = rgba, Width = image.Width, Height = image.Height, Opaque = opaque };
                    }

                    lock (_lock)
                    {
                        if (request.Gen == _viewGen)
                            _ready[request.Key] = decoded;
                        _failUntil.Remove(request.Key);
                        _failCount.Remove(request.Key);
                    }
                    AppendLog(request.Key + " decode OK " + decoded.Width + "x" + decoded.Height +
                              " bytes=" + encodedSize);
                }
                catch (OperationCanceledException)
                {
                    DownloadResumeInfo.DeletePartial(path + ".part");
                    AppendLog(request.Key + " cancelled stale cover");
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                    DownloadResumeInfo.DeletePartial(path + ".part");
                    lock (_lock) RecordFailure(request.Key);
                    AppendLog(request.Key + " FAIL " + ex.GetType().Name + ": " + ex.Message +
                              " url=" + SafeUrl(request.Url) + " file=" + DescribeFile(path));
                }
                finally
                {
                    lock (_lock) _inflight.Remove(request.Key);
                }
            }
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

        static string LogPath { get { return Path.Combine(AppSettings.DataDir, "covers.log"); } }

        static readonly object LogGate = new object();
        static void AppendLog(string message)
        {
            try
            {
                lock (LogGate)
                    File.AppendAllText(LogPath, DateTime.UtcNow.ToString("s") + " " + message + "\n");
            }
            catch { }
        }
    }
}
