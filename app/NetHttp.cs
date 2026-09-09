using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal sealed class HttpRangeResult
    {
        public byte[] Data;
        public long Total;
        public string EffectiveUrl;
    }

    /// <summary>
    /// HTTPS = firmware sceHttp only (unless emergency LAN proxy is ON).
    /// Never silently fall back to MonoBTLS for https://.
    /// </summary>
    internal static class NetHttp
    {
        public static string UserAgent =
            "Mozilla/5.0 (PlayStation 4) AppleWebKit/537.36 SSPI/5.10";

        /// <summary>Desktop Chrome UA for standards-compatible sources that reject console user agents.</summary>
        public const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

        public static bool ForceProxy;
        public static string ProxyBase = "";
        public static string ProxyKey = "game-search-lan";
        static int _downloadRangeCount = DownloadTransferSettings.DefaultRangeCount;

        public static int DownloadRangeCount
        {
            get { return _downloadRangeCount; }
            set { _downloadRangeCount = DownloadTransferSettings.ClampRangeCount(value); }
        }

        static NetHttp()
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; }
            catch
            {
                try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls; } catch { }
            }
            try
            {
                // Keep the runtime's normal chain, hostname, expiry, and usage checks.
                // A null callback restores platform validation instead of overriding it.
                ServicePointManager.ServerCertificateValidationCallback = null;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.DefaultConnectionLimit = 8;
            }
            catch { }
        }

        /// <summary>True only when emergency LAN proxy is explicitly enabled.</summary>
        public static bool UseProxy
        {
            get { return ForceProxy && !string.IsNullOrEmpty((ProxyBase ?? "").Trim()); }
        }

        public static string TransportLabel
        {
            get
            {
                if (UseProxy) return "PROXY";
                if (NativeHttp.Available) return "NATIVE";
                return "NO-HTTPS";
            }
        }

        public static string ResolveUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            if (!UseProxy) return url;
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return url;
            string root = (ProxyBase ?? "").TrimEnd('/');
            if (root.Length == 0) return url;
            return root + "/p/" + Uri.EscapeDataString(url);
        }

        public static string GetStringDirect(string url, int timeoutMs = 45000, string referer = null,
            string bearer = null, string userAgent = null)
        {
            return GetStringInternal(url, timeoutMs, referer, bearer, allowProxy: false, userAgent: userAgent);
        }

        public static string GetString(string url, int timeoutMs = 45000, string referer = null,
            string bearer = null, string userAgent = null)
        {
            ProviderCooldown.Check(url);
            try
            {
                return GetStringInternal(url, timeoutMs, referer, bearer, allowProxy: true, userAgent: userAgent);
            }
            catch (Exception ex)
            {
                ProviderCooldown.NoteException(url, ex);
                throw;
            }
        }

        static string GetStringInternal(string url, int timeoutMs, string referer, string bearer,
            bool allowProxy, string userAgent)
        {
            if (string.IsNullOrEmpty(url)) throw new Exception("Empty URL");
            bool https = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            bool proxyOk = allowProxy && UseProxy;

            // Emergency proxy: rewrite https → http://PC/p/... then managed plain HTTP
            if (https && proxyOk)
                return ManagedGet(ResolveUrl(url), timeoutMs, referer, bearer, addProxyKey: true, userAgent: userAgent);

            // Plain HTTP always managed
            if (!https)
                return ManagedGet(url, timeoutMs, referer, bearer, addProxyKey: false, userAgent: userAgent);

            // HTTPS without proxy: native only — never MonoBTLS
            NativeHttp.EnsureInit();
            if (!NativeHttp.Available)
                throw new Exception("HTTPS needs native sceHttp (" + NativeHttp.InitDetail + ")");

            try
            {
                return NativeHttp.GetString(url, timeoutMs, referer, bearer, 2 * 1024 * 1024, userAgent);
            }
            catch (Exception nex)
            {
                throw new Exception("Native HTTPS: " + nex.Message);
            }
        }

        public static string PostForm(string url, string formBody, int timeoutMs = 45000,
            string referer = null, string bearer = null, string contentType = null)
        {
            ProviderCooldown.Check(url);
            try
            {
                if (string.IsNullOrEmpty(url)) throw new Exception("Empty URL");
                bool https = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

                if (https && UseProxy)
                    return ManagedPost(ResolveUrl(url), formBody, timeoutMs, referer, bearer, contentType, true);

                if (!https)
                    return ManagedPost(url, formBody, timeoutMs, referer, bearer, contentType, false);

                NativeHttp.EnsureInit();
                if (!NativeHttp.Available)
                    throw new Exception("HTTPS POST needs native sceHttp (" + NativeHttp.InitDetail + ")");

                try
                {
                    return NativeHttp.PostForm(url, formBody, timeoutMs, referer, bearer);
                }
                catch (Exception nex)
                {
                    throw new Exception("Native HTTPS POST: " + nex.Message);
                }
            }
            catch (Exception ex)
            {
                ProviderCooldown.NoteException(url, ex);
                throw;
            }
        }

        public static long DownloadFile(string url, string destPath, Action<long, long> progress,
            int timeoutMs = 0, string bearer = null)
        {
            return DownloadFileResumable(url, destPath, 0, progress, null, timeoutMs, bearer, null);
        }

        public static HttpRangeResult ReadRangeDirect(string url, long start, int count, int timeoutMs = 45000)
        {
            if (string.IsNullOrEmpty(url)) throw new Exception("Empty URL");
            if (start < 0 || count <= 0) throw new ArgumentOutOfRangeException();
            if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return ManagedReadRange(url, start, count, timeoutMs);

            NativeHttp.EnsureInit();
            if (!NativeHttp.Available)
                throw new Exception("HTTPS range needs native sceHttp (" + NativeHttp.InitDetail + ")");
            return NativeHttp.ReadRange(url, start, count, timeoutMs);
        }

        public static long DownloadFileResumable(string url, string destPath, long existingBytes,
            Action<long, long> progress, Func<bool> cancel, int timeoutMs = 0, string bearer = null,
            string expectedTitleId = null, string expectedPackageId = null)
        {
            if (string.IsNullOrEmpty(url)) throw new Exception("Empty URL");
            bool https = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            int to = timeoutMs <= 0 ? 600000 : timeoutMs;
            int rangeCount = 1; // Sequential engine; legacy range maps are recovered before resuming.

            if (https && UseProxy)
                return ManagedDownloadResumable(ResolveUrl(url), destPath, existingBytes, progress,
                    cancel, to, bearer, true, expectedTitleId, rangeCount, expectedPackageId);

            if (!https)
                return ManagedDownloadResumable(url, destPath, existingBytes, progress, cancel, to,
                    bearer, false, expectedTitleId, rangeCount, expectedPackageId);

            NativeHttp.EnsureInit();
            if (!NativeHttp.Available)
                throw new Exception("HTTPS DL needs native sceHttp (" + NativeHttp.InitDetail + ")");

            try
            {
                return NativeHttp.DownloadFileResumable(url, destPath, existingBytes, progress,
                    cancel, to, bearer, expectedTitleId, rangeCount, expectedPackageId);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception nex)
            {
                throw new Exception("Native HTTPS DL: " + nex.Message);
            }
        }

        // Source packages are small. Follow HTTP redirects explicitly so HTTPS
        // is always handed to sceHttp instead of Mono's unavailable BTLS store.
        public static long DownloadSourceFile(string url, string path,
            Action<long, long> progress, Func<bool> cancel, int timeoutMs)
        {
            var current = new Uri(url, UriKind.Absolute);
            for (int hop = 0; hop < 8; hop++)
            {
                if (!string.IsNullOrEmpty(current.UserInfo)) throw new IOException("Source URL contains user info");
                if (current.Scheme == Uri.UriSchemeHttps)
                    return NativeHttp.DownloadFileResumable(current.AbsoluteUri, path, 0,
                        progress, cancel, timeoutMs, null, null, 1);
                if (current.Scheme != Uri.UriSchemeHttp) throw new IOException("Unsupported source URL scheme");
                if (cancel != null && cancel()) throw new OperationCanceledException();
                var req = (HttpWebRequest)WebRequest.Create(current);
                req.AllowAutoRedirect = false;
                req.UserAgent = UserAgent;
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                using (var response = (HttpWebResponse)req.GetResponse())
                {
                    int status = (int)response.StatusCode;
                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                    {
                        string location = response.Headers["Location"];
                        if (string.IsNullOrEmpty(location)) throw new IOException("Source redirect has no Location");
                        current = new Uri(current, location);
                        continue;
                    }
                    if (status != 200) throw new IOException("Source HTTP " + status);
                    const long limit = 4L * 1024 * 1024;
                    if (response.ContentLength > limit) throw new IOException("Source exceeds 4 MiB");
                    long done = 0;
                    using (var input = response.GetResponseStream())
                    using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
                    {
                        byte[] buffer = new byte[16384]; int count;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (cancel != null && cancel()) throw new OperationCanceledException();
                            done += count;
                            if (done > limit) throw new IOException("Source exceeds 4 MiB");
                            output.Write(buffer, 0, count);
                            if (progress != null) progress(done, response.ContentLength);
                        }
                    }
                    if (response.ContentLength >= 0 && done != response.ContentLength)
                        throw new IOException("Incomplete source download");
                    return done;
                }
            }
            throw new IOException("Too many source redirects");
        }

    /// <summary>Per-provider HTTP 429 cooldown. A throttled host stops receiving
    /// new API requests until its Retry-After deadline; preparation IDs and
    /// cancellation are unaffected — only new calls are refused with the wait.</summary>
    internal static class ProviderCooldown
    {
        static readonly object _lock = new object();
        static readonly Dictionary<string, DateTime> _until =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        public static void Check(string url)
        {
            string host = Host(url);
            if (string.IsNullOrEmpty(host)) return;
            lock (_lock)
            {
                DateTime until;
                if (_until.TryGetValue(host, out until) && until > DateTime.UtcNow)
                    throw new Exception("Provider cooling down: " + host + " retry in " +
                        Math.Max(1, (int)(until - DateTime.UtcNow).TotalSeconds) + "s (HTTP 429)");
            }
        }

        public static bool NoteResponse(string url, int status, string retryAfter)
        {
            if (status != 429) return false;
            int wait = 60;
            if (!string.IsNullOrEmpty(retryAfter))
            {
                int seconds;
                if (int.TryParse(retryAfter.Trim(), out seconds) && seconds >= 0)
                    wait = Math.Min(300, seconds);
                else
                {
                    DateTime date;
                    if (DateTime.TryParse(retryAfter, out date))
                        wait = Math.Max(0, Math.Min(300,
                            (int)(date.ToUniversalTime() - DateTime.UtcNow).TotalSeconds));
                }
            }
            string host = Host(url);
            if (string.IsNullOrEmpty(host)) return true;
            lock (_lock) _until[host] = DateTime.UtcNow.AddSeconds(wait);
            return true;
        }

        public static void NoteException(string url, Exception ex)
        {
            if (ex == null) return;
            var web = ex as WebException;
            if (web != null)
            {
                var response = web.Response as HttpWebResponse;
                if (response != null)
                {
                    NoteResponse(url, (int)response.StatusCode, response.Headers["Retry-After"]);
                    return;
                }
            }
            string message = ex.Message ?? "";
            if (message.IndexOf("(429)", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("HTTP 429", StringComparison.OrdinalIgnoreCase) >= 0)
                NoteResponse(url, 429, null);
        }

        static string Host(string url)
        {
            try
            {
                var uri = new Uri(url, UriKind.Absolute);
                return uri.Host ?? "";
            }
            catch { return ""; }
        }
    }

        static string ManagedGet(string finalUrl, int timeoutMs, string referer, string bearer, bool addProxyKey,
            string userAgent = null)
        {
            var req = (HttpWebRequest)WebRequest.Create(finalUrl);
            req.Method = "GET";
            req.UserAgent = string.IsNullOrEmpty(userAgent) ? UserAgent : userAgent;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;
            if (!string.IsNullOrEmpty(referer)) req.Referer = referer;
            req.Accept = "text/html,application/json,*/*";
            req.Headers["Accept-Language"] = "en-US,en;q=0.8";
            if (!string.IsNullOrEmpty(bearer))
                req.Headers["Authorization"] = "Bearer " + bearer;
            if (addProxyKey)
                req.Headers["X-GS-Proxy-Key"] = ProxyKey ?? "";

            HttpWebResponse getResp;
            try { getResp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException webEx) { throw CooldownOrRethrow(finalUrl, webEx); }
            using (var resp = getResp)
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                {
                    var text = new StringBuilder(); var block = new char[8192]; int count;
                    while ((count = reader.Read(block, 0, block.Length)) > 0) {
                        if (text.Length + count > 2 * 1024 * 1024) throw new IOException("HTTP response exceeds 2 MiB");
                        text.Append(block, 0, count);
                    }
                    return text.ToString();
                }
        }

        static string ManagedPost(string finalUrl, string formBody, int timeoutMs, string referer,
            string bearer, string contentType, bool addProxyKey)
        {
            byte[] data = Encoding.UTF8.GetBytes(formBody ?? "");
            var req = (HttpWebRequest)WebRequest.Create(finalUrl);
            req.Method = "POST";
            req.UserAgent = UserAgent;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;
            req.ContentType = contentType ?? "application/x-www-form-urlencoded; charset=UTF-8";
            req.ContentLength = data.Length;
            if (!string.IsNullOrEmpty(referer)) req.Referer = referer;
            if (!string.IsNullOrEmpty(bearer))
                req.Headers["Authorization"] = "Bearer " + bearer;
            if (addProxyKey)
                req.Headers["X-GS-Proxy-Key"] = ProxyKey ?? "";
            req.Accept = "application/json,*/*";

            using (var rs = req.GetRequestStream())
                rs.Write(data, 0, data.Length);

            HttpWebResponse postResp;
            try { postResp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException webEx) { throw CooldownOrRethrow(finalUrl, webEx); }
            using (var resp = postResp)
            using (var stream = resp.GetResponseStream())
            using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                {
                    var text = new StringBuilder(); var block = new char[8192]; int count;
                    while ((count = reader.Read(block, 0, block.Length)) > 0) {
                        if (text.Length + count > 2 * 1024 * 1024) throw new IOException("HTTP response exceeds 2 MiB");
                        text.Append(block, 0, count);
                    }
                    return text.ToString();
                }
        }

        static Exception CooldownOrRethrow(string url, WebException webEx)
        {
            var http = webEx != null ? webEx.Response as HttpWebResponse : null;
            if (http != null && (int)http.StatusCode == 429)
            {
                ProviderCooldown.NoteResponse(url, 429, http.Headers["Retry-After"]);
                return new Exception("Provider cooling down: request throttled (HTTP 429)");
            }
            return webEx;
        }

        static HttpRangeResult ManagedReadRange(string url, long start, int count, int timeoutMs)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = UserAgent;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;
            req.Headers["Accept-Encoding"] = "identity";
            req.AddRange(start, checked(start + count - 1));

            using (var resp = (HttpWebResponse)req.GetResponse())
            {
                if (resp.StatusCode != HttpStatusCode.PartialContent &&
                    !(resp.StatusCode == HttpStatusCode.OK && start == 0))
                    throw new Exception("HTTP " + (int)resp.StatusCode + " reading PKG header");

                long total = resp.StatusCode == HttpStatusCode.OK ? resp.ContentLength : -1;
                if (resp.StatusCode == HttpStatusCode.PartialContent)
                {
                    long rangeStart, rangeEnd;
                    bool unsatisfied;
                    if (!DownloadResumeInfo.TryParseContentRange(resp.Headers["Content-Range"],
                        out rangeStart, out rangeEnd, out total, out unsatisfied) || unsatisfied || rangeStart != start)
                        throw new Exception("Invalid Content-Range reading PKG header");
                }

                var output = new MemoryStream(count);
                using (var input = resp.GetResponseStream())
                {
                    byte[] buffer = new byte[Math.Min(16 * 1024, count)];
                    while (output.Length < count)
                    {
                        int wanted = Math.Min(buffer.Length, count - (int)output.Length);
                        int read = input == null ? 0 : input.Read(buffer, 0, wanted);
                        if (read <= 0) break;
                        output.Write(buffer, 0, read);
                    }
                }
                return new HttpRangeResult
                {
                    Data = output.ToArray(),
                    Total = total,
                    EffectiveUrl = resp.ResponseUri != null ? resp.ResponseUri.AbsoluteUri : url
                };
            }
        }

        static long ManagedDownloadResumable(string finalUrl, string destPath, long existingBytes,
            Action<long, long> progress, Func<bool> cancel, int timeoutMs, string bearer, bool addProxyKey,
            string expectedTitleId, int rangeCount, string expectedPackageId = null)
        {
            string dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            string part = destPath.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                ? destPath : destPath + ".part";
            string finalPath = destPath.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                ? destPath.Substring(0, destPath.Length - 5) : destPath;
            if (existingBytes > 0 && File.Exists(part))
                existingBytes = new FileInfo(part).Length;
            else
                existingBytes = 0;

            DownloadResumeInfo resume = existingBytes > 0 ? DownloadResumeInfo.Load(part) : null;
            if (existingBytes > 0 && resume == null)
            {
                // Crash residue from positioned parallel writes: a range map without
                // usable metadata. Adopt the verified contiguous prefix, drop the
                // sparse tail, and continue under the identity rule.
                long mapTotal; int mapN;
                if (ParallelDownloadCheckpoint.TryPeekRangeMap(part, out mapTotal, out mapN))
                {
                    long[] mapDone = ParallelDownloadCheckpoint.LoadRangeMap(part, mapTotal, mapN);
                    long[] mapNeeds = ParallelDownloadCheckpoint.SpanNeeds(mapTotal, mapN);
                    long keep = ParallelDownloadCheckpoint.RetainedBytes(mapDone, mapNeeds);
                    try
                    {
                        using (var fs = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.None))
                            if (fs.Length != keep) fs.SetLength(keep);
                    }
                    catch { }
                    ParallelDownloadCheckpoint.DeleteRangeMap(part);
                    existingBytes = File.Exists(part) ? new FileInfo(part).Length : 0;
                    if (keep > 0 && existingBytes == keep)
                    {
                        resume = DownloadResumeInfo.Create(finalUrl, finalUrl, null, null,
                            mapTotal, expectedTitleId, expectedPackageId);
                        resume.Save(part);
                    }
                }
            }
            if (existingBytes > 0 && resume != null && !resume.CanResume(finalUrl, expectedTitleId, existingBytes, expectedPackageId))
                throw new Exception(DownloadResumeInfo.RestartRequired(
                    "saved partial metadata does not match this package"));
            if (existingBytes > 0 && resume == null)
                throw new Exception(DownloadResumeInfo.RestartRequired(
                    "saved partial metadata is missing"));

            var req = (HttpWebRequest)WebRequest.Create(finalUrl);
            req.Method = "GET";
            req.UserAgent = UserAgent;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = 600000;
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;
            req.Headers["Accept-Encoding"] = "identity";
            if (existingBytes > 0)
            {
                req.AddRange(existingBytes);
                // Skip If-Range for RD — CDN ETags rotate and would force full restart
            }
            if (!string.IsNullOrEmpty(bearer))
                req.Headers["Authorization"] = "Bearer " + bearer;
            if (addProxyKey)
                req.Headers["X-GS-Proxy-Key"] = ProxyKey ?? "";

            var requestDone = new ManualResetEvent(false);
            Thread abortThread = null;
            if (cancel != null)
            {
                abortThread = new Thread(() =>
                {
                    while (!requestDone.WaitOne(100))
                    {
                        bool stop = false;
                        try { stop = cancel(); } catch { }
                        if (!stop) continue;
                        try { req.Abort(); } catch { }
                        return;
                    }
                }) { IsBackground = true, Name = "HTTP cancel" };
                abortThread.Start();
            }
            try
            {
            long bytesReadThis = 0;
            long expectedResponse = -1;
            long expectedFinal = -1;
            long expectedRangeSpan = -1;
            HttpWebResponse response;
            try
            {
                response = (HttpWebResponse)req.GetResponse();
            }
            catch (WebException ex)
            {
                response = ex.Response as HttpWebResponse;
                if (response == null || response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    if (response != null) response.Dispose();
                    throw;
                }
            }

            using (var resp = response)
            {
                string effectiveUrl = resp.ResponseUri != null ? resp.ResponseUri.AbsoluteUri : finalUrl;
                string etag = resp.Headers["ETag"];
                string lastModified = resp.Headers["Last-Modified"];

                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    long start, end, total;
                    bool unsatisfied;
                    if (resume == null ||
                        !DownloadResumeInfo.TryParseContentRange(resp.Headers["Content-Range"], out start, out end, out total, out unsatisfied) ||
                        !unsatisfied || total != existingBytes || total != resume.Total ||
                        !resume.MatchesResponse(effectiveUrl, etag, lastModified))
                    {
                        throw new Exception(DownloadResumeInfo.RestartRequired(
                            "server rejected the saved byte offset (HTTP 416)"));
                    }
                    expectedFinal = total;
                }
                else
                {
                    if (resp.StatusCode != HttpStatusCode.OK && resp.StatusCode != HttpStatusCode.PartialContent)
                        throw new Exception("HTTP " + (int)resp.StatusCode + " downloading");

                    bool append = resp.StatusCode == HttpStatusCode.PartialContent && existingBytes > 0;
                    if (resp.StatusCode == HttpStatusCode.PartialContent)
                    {
                        long start, end, total;
                        bool unsatisfied;
                        if (!append || resume == null ||
                            !DownloadResumeInfo.TryParseContentRange(resp.Headers["Content-Range"], out start, out end, out total, out unsatisfied) ||
                            unsatisfied || start != existingBytes || total != resume.Total ||
                            !resume.MatchesResponse(effectiveUrl, etag, lastModified))
                        {
                            throw new Exception(DownloadResumeInfo.RestartRequired(
                                "server returned an invalid Content-Range"));
                        }
                        expectedRangeSpan = end - start + 1;
                        expectedFinal = total;
                        if (resp.ContentLength >= 0 && resp.ContentLength != expectedRangeSpan)
                        {
                            throw new Exception(DownloadResumeInfo.RestartRequired(
                                "Content-Range does not match Content-Length"));
                        }
                    }
                    else if (existingBytes > 0)
                    {
                        throw new Exception(DownloadResumeInfo.RestartRequired(
                            "server ignored the Range request (HTTP 200)"));
                    }

                    if (resp.ContentLength >= 0)
                    {
                        expectedResponse = resp.ContentLength;
                        if (!append) expectedFinal = resp.ContentLength;
                    }

                    if (!append)
                        resume = DownloadResumeInfo.Create(finalUrl, effectiveUrl, etag, lastModified,
                            expectedFinal, expectedTitleId, expectedPackageId);
                    else
                        resume.Refresh(etag, lastModified, expectedFinal);
                    resume.Save(part);

                    long done = append ? existingBytes : 0;
                    long totalUi = expectedFinal > 0 ? expectedFinal : 0;
                    var mode = append ? FileMode.Append : FileMode.Create;
                    using (var input = resp.GetResponseStream())
                    using (var output = new FileStream(part, mode, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                    {
                        if (input == null) throw new IOException("Empty download stream");
                        bytesReadThis = SequentialDownloadEngine.Copy(buffer => input.Read(buffer, 0, buffer.Length),
                            output, expectedResponse, done, totalUi, progress, cancel);
                    }
                }
            }

            if (cancel != null && cancel())
                throw new OperationCanceledException("paused");

            if (expectedResponse >= 0 && bytesReadThis != expectedResponse)
                throw new Exception("truncated: read " + bytesReadThis + " of " + expectedResponse);
            if (expectedRangeSpan >= 0 && bytesReadThis != expectedRangeSpan)
                throw new Exception("truncated range: read " + bytesReadThis + " of " + expectedRangeSpan);
            long finalLen = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (expectedFinal >= 0 && finalLen != expectedFinal)
                throw new Exception("final size " + finalLen + " != expected " + expectedFinal);

            PkgValResult vr;
            string vdetail;
            if (!string.IsNullOrEmpty(expectedTitleId) &&
                !PkgValidator.TryValidate(part, expectedTitleId, out vr, out vdetail))
            {
                if (vr != PkgValResult.NativeParseFailed)
                {
                    DownloadResumeInfo.DeletePartial(part);
                    throw new Exception("PKG invalid: " + vdetail);
                }
            }

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(part, finalPath);
            DownloadResumeInfo.Delete(part);
            return new FileInfo(finalPath).Length;
            }
            finally
            {
                requestDone.Set();
                if (abortThread == null || abortThread.Join(1000)) requestDone.Close();
            }
        }

        static bool TryProbeManagedContentLength(string url, int timeoutMs, string bearer,
            bool addProxyKey, out long length)
        {
            length = -1;
            try
            {
                var req = CreateManagedRangeRequest(url, 0, 0, timeoutMs, bearer, addProxyKey);
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    long start, end, total;
                    bool unsatisfied;
                    if (resp.StatusCode != HttpStatusCode.PartialContent ||
                        !DownloadResumeInfo.TryParseContentRange(resp.Headers["Content-Range"],
                            out start, out end, out total, out unsatisfied) ||
                        unsatisfied || start != 0 || end != 0 || total <= 0 ||
                        (resp.ContentLength >= 0 && resp.ContentLength != 1))
                        return false;
                    length = total;
                    return true;
                }
            }
            catch { return false; }
        }

        static long ManagedDownloadParallelN(string url, string part, string finalPath, long total,
            int rangeCount, Action<long, long> progress, Func<bool> cancel, int timeoutMs,
            string bearer, bool addProxyKey, string expectedTitleId, string expectedPackageId = null)
        {
            int n = DownloadTransferSettings.ClampRangeCount(rangeCount);
            if (n < 2 || total < 8L * 1024 * 1024)
                throw new Exception("too small for parallel");
            if (cancel != null && cancel())
                throw new OperationCanceledException("paused");

            // Positioned writes into one preallocated .part: no merge copy, and the
            // range map keeps completed spans across pause/restart (SSPI-11/12).
            var starts = new long[n];
            var ends = new long[n];
            var needs = new long[n];
            for (int i = 0; i < n; i++)
            {
                starts[i] = total * i / n;
                ends[i] = (total * (i + 1) / n) - 1;
                needs[i] = ends[i] - starts[i] + 1;
            }
            try
            {
                using (var pre = new FileStream(part, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
                    if (pre.Length != total) pre.SetLength(total);
            }
            catch (Exception ex) { throw new IOException("Cannot preallocate partial file: " + ex.Message); }
            long[] done = ParallelDownloadCheckpoint.LoadRangeMap(part, total, n);
            for (int i = 0; i < n; i++) done[i] = Math.Min(done[i], needs[i]);

            var errors = new Exception[n];
            var progressLock = new object();
            long lastReport = -1;
            long lastMapSave = 0;
            int failed = 0;
            Func<bool> cancelOrFail = () =>
                (cancel != null && cancel()) || Interlocked.CompareExchange(ref failed, 0, 0) != 0;
            Action report = () =>
            {
                if (progress == null) return;
                long sum = 0;
                bool save = false;
                lock (progressLock)
                {
                    for (int i = 0; i < n; i++) sum += done[i];
                    if (sum == lastReport) return;
                    lastReport = sum;
                    long now = DateTime.UtcNow.Ticks;
                    if (now - lastMapSave > TimeSpan.TicksPerSecond * 5)
                    {
                        lastMapSave = now;
                        save = true;
                    }
                }
                // Crash-recovery map (throttled); exit paths consolidate anyway.
                if (save) ParallelDownloadCheckpoint.SaveRangeMap(part, total, done);
                progress(sum, total);
            };
            report();

            var threads = new Thread[n];
            for (int i = 0; i < n; i++)
            {
                int index = i;
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        if (done[index] >= needs[index]) return;
                        ManagedDownloadExactRangeTo(part, starts[index] + done[index], ends[index], total,
                            url, timeoutMs, bearer, addProxyKey, cancelOrFail, bytes =>
                            {
                                lock (progressLock) done[index] += bytes;
                                report();
                            });
                        lock (progressLock) done[index] = needs[index];
                        report();
                    }
                    catch (Exception ex)
                    {
                        errors[index] = ex;
                        Interlocked.Exchange(ref failed, 1);
                    }
                }) { IsBackground = true, Name = "managed-dl-r" + index };
                threads[i].Start();
            }
            for (int i = 0; i < n; i++)
                threads[i].Join();

            Exception firstError = null;
            for (int i = 0; i < n; i++)
            {
                if (errors[i] == null) continue;
                if (firstError == null || !(errors[i] is OperationCanceledException))
                    firstError = errors[i];
                if (!(errors[i] is OperationCanceledException)) break;
            }

            // Consolidate on the way out: keep the contiguous prefix plus the first
            // partial span, truncate the sparse tail, and record fresh metadata so
            // any resume appends after verified bytes instead of after a gap.
            long keep = ParallelDownloadCheckpoint.RetainedBytes(done, needs);
            try
            {
                using (var fs = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.None))
                    if (fs.Length != keep) fs.SetLength(keep);
            }
            catch { }
            ParallelDownloadCheckpoint.DeleteRangeMap(part);
            if (keep > 0)
            {
                var consolidated = DownloadResumeInfo.Create(url, url, null, null,
                    total, expectedTitleId, expectedPackageId);
                consolidated.Save(part);
            }
            else
            {
                ParallelDownloadCheckpoint.DeleteAll(part);
            }
            if (progress != null)
                try { progress(keep, total); } catch { }

            if (cancel != null && cancel())
                throw new OperationCanceledException("paused at " + keep + " bytes");
            if (firstError != null)
                throw new Exception("parallel: " + firstError.Message);
            if (keep != total)
                throw new Exception("parallel checkpoint " + keep + " != " + total);
            if (new FileInfo(part).Length != total)
                throw new Exception("parallel size " + new FileInfo(part).Length + " != " + total);
            return FinishManagedPart(part, finalPath, expectedTitleId);
        }
        static void ManagedDownloadExactRange(string url, long start, long end, long expectedTotal,
            string destination, int timeoutMs, string bearer, bool addProxyKey, Func<bool> cancel,
            Action<int> onChunk)
        {
            var req = CreateManagedRangeRequest(url, start, end, timeoutMs, bearer, addProxyKey);
            var requestDone = new ManualResetEvent(false);
            Thread abortThread = null;
            if (cancel != null)
            {
                abortThread = new Thread(() =>
                {
                    while (!requestDone.WaitOne(100))
                    {
                        bool stop = false;
                        try { stop = cancel(); } catch { }
                        if (!stop) continue;
                        try { req.Abort(); } catch { }
                        return;
                    }
                }) { IsBackground = true, Name = "managed range cancel" };
                abortThread.Start();
            }

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    long actualStart, actualEnd, actualTotal;
                    bool unsatisfied;
                    long expected = end - start + 1;
                    if (resp.StatusCode != HttpStatusCode.PartialContent ||
                        !DownloadResumeInfo.TryParseContentRange(resp.Headers["Content-Range"],
                            out actualStart, out actualEnd, out actualTotal, out unsatisfied) ||
                        unsatisfied || actualStart != start || actualEnd != end ||
                        actualTotal != expectedTotal ||
                        (resp.ContentLength >= 0 && resp.ContentLength != expected))
                        throw new Exception("invalid Content-Range for bytes=" + start + "-" + end);

                    long received = 0;
                    byte[] buffer = new byte[256 * 1024];
                    using (var input = resp.GetResponseStream())
                    using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
                        FileShare.None, 512 * 1024))
                    {
                        int read;
                        while (input != null && (read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (cancel != null && cancel())
                                throw new OperationCanceledException("paused");
                            output.Write(buffer, 0, read);
                            received += read;
                            if (onChunk != null) onChunk(read);
                        }
                        output.Flush();
                    }
                    if (received != expected)
                        throw new Exception("range read " + received + " of " + expected);
                }
            }
            finally
            {
                requestDone.Set();
                if (abortThread == null || abortThread.Join(1000)) requestDone.Close();
            }
        }

        static void ManagedDownloadExactRangeTo(string part, long start, long end, long expectedTotal,
            string url, int timeoutMs, string bearer, bool addProxyKey, Func<bool> cancel,
            Action<int> onChunk)
        {
            var req = CreateManagedRangeRequest(url, start, end, timeoutMs, bearer, addProxyKey);
            var requestDone = new ManualResetEvent(false);
            Thread abortThread = null;
            if (cancel != null)
            {
                abortThread = new Thread(() =>
                {
                    while (!requestDone.WaitOne(100))
                    {
                        bool stop = false;
                        try { stop = cancel(); } catch { }
                        if (!stop) continue;
                        try { req.Abort(); } catch { }
                        return;
                    }
                }) { IsBackground = true, Name = "managed range cancel" };
                abortThread.Start();
            }

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    long actualStart, actualEnd, actualTotal;
                    bool unsatisfied;
                    long expected = end - start + 1;
                    if (resp.StatusCode != HttpStatusCode.PartialContent ||
                        !DownloadResumeInfo.TryParseContentRange(resp.Headers["Content-Range"],
                            out actualStart, out actualEnd, out actualTotal, out unsatisfied) ||
                        unsatisfied || actualStart != start || actualEnd != end ||
                        actualTotal != expectedTotal ||
                        (resp.ContentLength >= 0 && resp.ContentLength != expected))
                        throw new Exception("invalid Content-Range for bytes=" + start + "-" + end);

                    long received = 0;
                    byte[] buffer = new byte[256 * 1024];
                    using (var input = resp.GetResponseStream())
                    using (var output = new FileStream(part, FileMode.Open, FileAccess.Write,
                        FileShare.ReadWrite, 512 * 1024))
                    {
                        output.Position = start;
                        int read;
                        while (input != null && (read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (cancel != null && cancel())
                                throw new OperationCanceledException("paused");
                            output.Write(buffer, 0, read);
                            received += read;
                            if (onChunk != null) onChunk(read);
                        }
                        output.Flush();
                    }
                    if (received != expected)
                        throw new Exception("range read " + received + " of " + expected);
                }
            }
            finally
            {
                requestDone.Set();
                if (abortThread == null || abortThread.Join(1000)) requestDone.Close();
            }
        }

        static HttpWebRequest CreateManagedRangeRequest(string url, long start, long end,
            int timeoutMs, string bearer, bool addProxyKey)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.UserAgent = UserAgent;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = true;
            req.KeepAlive = false;
            req.Headers["Accept-Encoding"] = "identity";
            req.AddRange(start, end);
            if (!string.IsNullOrEmpty(bearer))
                req.Headers["Authorization"] = "Bearer " + bearer;
            if (addProxyKey)
                req.Headers["X-GS-Proxy-Key"] = ProxyKey ?? "";
            return req;
        }

        static long FinishManagedPart(string part, string finalPath, string expectedTitleId)
        {
            PkgValResult result;
            string detail;
            if (!string.IsNullOrEmpty(expectedTitleId) &&
                !PkgValidator.TryValidate(part, expectedTitleId, out result, out detail) &&
                result != PkgValResult.NativeParseFailed)
            {
                ParallelDownloadCheckpoint.DeleteAll(part);
                throw new Exception("PKG invalid: " + detail);
            }
            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(part, finalPath);
            DownloadResumeInfo.Delete(part);
            ParallelDownloadCheckpoint.DeleteRangeMap(part);
            return new FileInfo(finalPath).Length;
        }

        static bool IsLocalIoFailure(Exception ex)
        {
            if (ex == null) return false;
            if (ex is IOException || ex is UnauthorizedAccessException || ex is OutOfMemoryException)
                return true;
            string message = ex.Message ?? "";
            return message.IndexOf("disk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("space", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   message.IndexOf("I/O", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }

    internal sealed class DownloadResumeInfo
    {
        const string RestartPrefix = "Resume unavailable: ";
        public string SourceKey;
        public string EffectiveKey;
        public string ETag;
        public string LastModified;
        public string TitleId;
        public long Total;
        /// <summary>Stable package identity (expected content ID, else expected SHA-256).
        /// Empty for metadata written before identity binding existed.</summary>
        public string PackageId;

        public static string RestartRequired(string reason)
        {
            return RestartPrefix + reason + "; partial kept — CROSS restarts from zero";
        }

        public static bool IsRestartRequired(string message)
        {
            return !string.IsNullOrEmpty(message) &&
                message.StartsWith(RestartPrefix, StringComparison.Ordinal);
        }

        public string IfRange
        {
            get
            {
                if (IsStrongEtag(ETag)) return ETag;
                return LastModified ?? "";
            }
        }

        public static DownloadResumeInfo Create(string sourceUrl, string effectiveUrl, string etag,
            string lastModified, long total, string titleId, string packageId = null)
        {
            return new DownloadResumeInfo
            {
                SourceKey = Fingerprint(sourceUrl),
                EffectiveKey = Fingerprint(effectiveUrl),
                ETag = etag ?? "",
                LastModified = lastModified ?? "",
                TitleId = titleId ?? "",
                Total = total,
                PackageId = packageId ?? ""
            };
        }

        public bool CanResume(string sourceUrl, string titleId, long localLength, string packageId = null)
        {
            if (localLength <= 0 || Total < localLength || Total <= 0) return false;
            // Package identity binds saved bytes to one package: a same-title,
            // same-size replacement must not splice into these bytes. Signed-URL
            // rotation keeps the same identity, so legitimate resumes still pass.
            string stored = (PackageId ?? "").Trim();
            string incoming = (packageId ?? "").Trim();
            if (stored.Length > 0 && incoming.Length > 0)
                return string.Equals(stored, incoming, StringComparison.OrdinalIgnoreCase);
            // Real-Debrid CDN URLs change every unrestrict — key resume on TitleId + total size.
            if (!string.IsNullOrEmpty(TitleId) && !string.IsNullOrEmpty(titleId) &&
                string.Equals(TitleId, titleId, StringComparison.OrdinalIgnoreCase))
                return true;
            // Fallback: same hoster/source fingerprint if title missing
            return string.Equals(SourceKey, Fingerprint(sourceUrl), StringComparison.Ordinal);
        }

        public bool MatchesResponse(string effectiveUrl, string etag, string lastModified)
        {
            // Real-Debrid rotates CDN URL and ETag every unrestrict.
            // Resume safety is Content-Range start == existingBytes + Total match.
            return true;
        }

        public void Refresh(string etag, string lastModified, long total)
        {
            if (!string.IsNullOrEmpty(etag)) ETag = etag;
            if (!string.IsNullOrEmpty(lastModified)) LastModified = lastModified;
            Total = total;
        }

        public void Save(string partPath)
        {
            string path = MetadataPath(partPath);
            string tmp = path + ".tmp";
            string body = "2\n" + (SourceKey ?? "") + "\n" + (EffectiveKey ?? "") + "\n" +
                          Encode(ETag) + "\n" + Encode(LastModified) + "\n" + Encode(TitleId) + "\n" +
                          Total.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" +
                          Encode(PackageId);
            File.WriteAllText(tmp, body);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static DownloadResumeInfo Load(string partPath)
        {
            try
            {
                string[] lines = File.ReadAllLines(MetadataPath(partPath));
                long total;
                // v2 carries PackageId; an empty identity leaves no 8th line
                // because ReadAllLines drops the trailing empty entry.
                bool v2 = (lines.Length == 8 || lines.Length == 7) && lines[0] == "2";
                if ((!v2 && (lines.Length != 7 || lines[0] != "1")) ||
                    !long.TryParse(lines[6], System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out total))
                    return null;
                return new DownloadResumeInfo
                {
                    SourceKey = lines[1],
                    EffectiveKey = lines[2],
                    ETag = Decode(lines[3]),
                    LastModified = Decode(lines[4]),
                    TitleId = Decode(lines[5]),
                    Total = total,
                    PackageId = (v2 && lines.Length == 8) ? Decode(lines[7]) : ""
                };
            }
            catch { return null; }
        }

        public static void Delete(string partPath)
        {
            try
            {
                string path = MetadataPath(partPath);
                if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
            }
            catch { }
        }

        public static void DeletePartial(string partPath)
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
            Delete(partPath);
        }

        public static bool TryParseContentRange(string value, out long start, out long end,
            out long total, out bool unsatisfied)
        {
            start = end = total = -1;
            unsatisfied = false;
            if (string.IsNullOrEmpty(value) || !value.StartsWith("bytes ", StringComparison.OrdinalIgnoreCase))
                return false;
            string rest = value.Substring(6).Trim();
            if (rest.StartsWith("*/", StringComparison.Ordinal))
            {
                unsatisfied = true;
                return long.TryParse(rest.Substring(2), out total) && total >= 0;
            }
            int dash = rest.IndexOf('-');
            int slash = rest.LastIndexOf('/');
            if (dash <= 0 || slash <= dash + 1 || slash + 1 >= rest.Length || rest[slash + 1] == '*')
                return false;
            return long.TryParse(rest.Substring(0, dash), out start) &&
                   long.TryParse(rest.Substring(dash + 1, slash - dash - 1), out end) &&
                   long.TryParse(rest.Substring(slash + 1), out total) &&
                   start >= 0 && end >= start && total > end;
        }

        static string MetadataPath(string partPath) { return partPath + ".resume"; }

        static bool IsStrongEtag(string etag)
        {
            return !string.IsNullOrEmpty(etag) && !etag.StartsWith("W/", StringComparison.OrdinalIgnoreCase);
        }

        static string Fingerprint(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            using (var sha = SHA256.Create())
                return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }

        static string Encode(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        static string Decode(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value ?? ""));
        }
    }
}
