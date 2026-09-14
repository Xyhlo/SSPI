using System;
using System.Collections.Generic;
using System.Globalization;
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
        internal const int DefaultResponseBytes = 2 * 1024 * 1024;

        public static string UserAgent =
            "Mozilla/5.0 (PlayStation 4) AppleWebKit/537.36 SSPI/5.11";

        /// <summary>Desktop Chrome UA for standards-compatible sources that reject console user agents.</summary>
        public const string BrowserUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36";

        public static bool ForceProxy;
        public static string ProxyBase = "";
        public static string ProxyKey = "game-search-lan";
        static int _downloadRangeCount = DownloadTransferSettings.DefaultRangeCount;

        internal enum DownloadDecision
        {
            UserSingle, ProxySingle, ProviderLimit, ResumeSingle,
            ParallelBusy, ProbeRange, ProbeValidator, ProbeEncoding, ProbeLength,
            ProbeError, ProbeAccepted, ParallelSelected, RangeRejected, SingleSelected,
            NativeUnavailable, ResidentSelected, BgftSelected,
            ProbeTitleMissing, ProbeHeaderRead, ProbeHeaderSize, ProbeHeaderIntegrity
        }
        static readonly object DownloadTraceLock = new object();
        static readonly System.Diagnostics.Stopwatch DownloadTraceClock = System.Diagnostics.Stopwatch.StartNew();

        static readonly string DownloadTraceSession = Guid.NewGuid().ToString("N").Substring(0, 12);
        static string _downloadTraceBuild = "unknown";
        internal enum BackgroundRoute
        {
            Disabled, ForegroundBusy, LegacyWorkerBusy, ExistingLocalFile, ExistingPartial,
            WorkerReady, WorkerRestartRequired, WorkerUnavailable, HeaderRejected,
            Published, PublicationBusy, PublicationFailed, ForegroundSelected
        }

        internal static void ConfigureDownloadTrace(string buildHash)
        {
            _downloadTraceBuild = !string.IsNullOrEmpty(buildHash) &&
                System.Text.RegularExpressions.Regex.IsMatch(buildHash, "\\A[0-9a-fA-F]{12,64}\\z")
                ? buildHash.Substring(0, 12).ToLowerInvariant() : "unknown";
        }

        internal static void TraceBackgroundRoute(BackgroundRoute reason, string worker)
        {
            string version = !string.IsNullOrEmpty(worker) &&
                System.Text.RegularExpressions.Regex.IsMatch(worker, "\\A5[.]10-r[0-9]{1,3}\\z") ? worker : "unavailable";
            WriteTransferTrace("event=background_route reason=" + reason + " worker=" + version);
        }

        static void WriteTransferTrace(string fields)
        {
            SspiLog.Write("download", "mono_ms=" + DownloadTraceClock.ElapsedMilliseconds + " build=" + _downloadTraceBuild +
                " session=" + DownloadTraceSession + " " + fields);
        }

        internal static void TraceDownloadProbe(bool native, int status, string tag, string modified,
            string range, string encoding)
        {
            // Raw headers can contain arbitrary server data. Persist only classifications and parsed numbers.
            long start, end, total; bool unsatisfied;
            bool valid = DownloadResumeInfo.TryParseContentRange(range, out start, out end, out total, out unsatisfied);
            string validator = string.IsNullOrEmpty(tag) ? "missing" :
                DownloadTransferSettings.StrongEtag(tag) ? "strong" :
                tag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? "weak" : "invalid";
            WriteTransferTrace("event=probe transport=" + (native ? "native" : "managed") +
                " http=" + status + " etag=" + validator + " modified_present=" + (!string.IsNullOrEmpty(modified) ? 1 : 0) +
                " identity_encoding=" + (DownloadTransferSettings.IdentityEncoding(encoding) ? 1 : 0) +
                " range_valid=" + (valid ? 1 : 0) + " range_start=" + (valid ? start : -1) +
                " range_end=" + (valid ? end : -1) + " range_total=" + (valid ? total : -1));
        }

        internal static void RecordDownloadMetrics(bool native, string transport, string metrics)
        {
            // Callers supply generated numeric metrics, never provider responses or exception messages.
            string label = transport == "libcurl-managed-writer" ? transport : native ? "sceHttp-managed-writer" : "managed-http";
            WriteTransferTrace("event=sample transport=" + label + " " + metrics.Replace("\r", "").Replace("\n", " "));
        }

        internal static void TraceDownloadDecision(bool native, DownloadDecision reason, int connections = 1)
        {
            WriteTransferTrace("event=decision transport=" + (native ? "native" : "managed") +
                " reason=" + reason + " connections=" + DownloadTransferSettings.ClampRangeCount(connections));
        }

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
            string bearer = null, string userAgent = null, int maxBytes = DefaultResponseBytes)
        {
            ProviderCooldown.Check(url);
            try { return GetStringInternal(url, timeoutMs, referer, bearer, allowProxy: false, userAgent: userAgent, maxBytes: maxBytes); }
            catch (Exception ex) { ProviderCooldown.NoteException(url, ex); throw; }
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
            bool allowProxy, string userAgent, int maxBytes = DefaultResponseBytes)
        {
            if (string.IsNullOrEmpty(url)) throw new Exception("Empty URL");
            if (maxBytes <= 0) throw new ArgumentOutOfRangeException("maxBytes");
            bool https = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            bool proxyOk = allowProxy && UseProxy;

            // Emergency proxy: rewrite https → http://PC/p/... then managed plain HTTP
            if (https && proxyOk)
                return ManagedGet(ResolveUrl(url), timeoutMs, referer, bearer, addProxyKey: true, userAgent: userAgent, maxBytes: maxBytes);

            // Plain HTTP always managed
            if (!https)
                return ManagedGet(url, timeoutMs, referer, bearer, addProxyKey: false, userAgent: userAgent, maxBytes: maxBytes);

            // HTTPS without proxy: native only — never MonoBTLS
            NativeHttp.EnsureInit();
            if (!NativeHttp.Available)
                throw new Exception("HTTPS needs native sceHttp (" + NativeHttp.InitDetail + ")");

            try
            {
                return NativeHttp.GetString(url, timeoutMs, referer, bearer, maxBytes, userAgent);
            }
            catch (Exception nex)
            {
                throw new Exception("Native HTTPS: " + nex.Message, nex);
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
                    throw new Exception("Native HTTPS POST: " + nex.Message, nex);
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

        internal static bool CanUseParallelDownload(string url, string expectedTitleId = null)
        {
            if (string.IsNullOrEmpty(url)) return false;
            bool native = url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !UseProxy;
            if (UseProxy) { TraceDownloadDecision(false, DownloadDecision.ProxySingle); return false; }
            if (DownloadTransferSettings.ConnectionsFor(url, DownloadRangeCount) < 2)
            { TraceDownloadDecision(native, DownloadRangeCount < 2 ? DownloadDecision.UserSingle : DownloadDecision.ProviderLimit); return false; }
            if (DownloadTransferSettings.HasProbe(url, expectedTitleId)) return true;
            long total = -1; string etag = null, effective = null; bool valid;
            if (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                NativeHttp.EnsureInit();
                if (!NativeHttp.Available)
                { TraceDownloadDecision(true, DownloadDecision.NativeUnavailable); return false; }
                valid = NativeHttp.TryProbeContentLength(url, null, 8000, out total, out etag, out effective);
                if (!valid) return false;
            }
            else valid = TryProbeManagedContentLength(url, 8000, null, false, out total, out etag, out effective);
            if (!valid) return false;
            if (total < 8L * 1024 * 1024)
            { TraceDownloadDecision(native, DownloadDecision.ProbeLength); return false; }
            byte[] header = null;
            if (!DownloadTransferSettings.StrongEtag(etag))
            {
                if (string.IsNullOrEmpty(expectedTitleId))
                { TraceDownloadDecision(native, DownloadDecision.ProbeTitleMissing); return false; }
                HttpRangeResult result;
                try
                {
                    result = native ? ReadRangeDirect(effective, 0, 0x1000, 8000) :
                        new HttpRangeResult { Data = ReadManagedIntegrityHeader(effective, total, 8000, null, false, null), Total = total };
                }
                catch { TraceDownloadDecision(native, DownloadDecision.ProbeHeaderRead); return false; }
                if (result.Total != total)
                { TraceDownloadDecision(native, DownloadDecision.ProbeHeaderSize); return false; }
                try
                {
                    PkgIntegrity.TransferHeaderIdentity(result.Data, total, expectedTitleId);
                    header = result.Data;
                }
                catch { TraceDownloadDecision(native, DownloadDecision.ProbeHeaderIntegrity); return false; }
                etag = null;
            }
            DownloadTransferSettings.RememberProbe(url, total, etag, effective, header, expectedTitleId);
            return true;
        }

        public static long DownloadFileResumable(string url, string destPath, long existingBytes,
            Action<long, long> progress, Func<bool> cancel, int timeoutMs = 0, string bearer = null,
            string expectedTitleId = null, string expectedPackageId = null, string expectedSha256 = null,
            Action<long, long, long> telemetry = null, Action<string> phase = null)
        {
            return TransferClient.Download(url, destPath, progress, cancel, bearer,
                expectedTitleId, expectedPackageId, expectedSha256, DownloadRangeCount, telemetry, phase);
        }

        // Artwork is disposable, bounded data. It must not start package writer,
        // checkpoint or diagnostic threads for each small cover request.
        internal static long DownloadArtwork(string url, string path, Func<bool> cancel,
            int maxBytes, int timeoutMs = 30000)
        {
            var current = new Uri(url, UriKind.Absolute);
            bool proxy = current.Scheme == Uri.UriSchemeHttps && UseProxy;
            if (proxy) current = new Uri(ResolveUrl(url), UriKind.Absolute);
            for (int hop = 0; hop < 8; hop++)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                if (!string.IsNullOrEmpty(current.UserInfo)) throw new IOException("Artwork URL contains user info");
                if (current.Scheme == Uri.UriSchemeHttps && !proxy)
                    return NativeHttp.DownloadArtwork(current.AbsoluteUri, path, cancel, maxBytes, timeoutMs);
                if (current.Scheme != Uri.UriSchemeHttp && !(proxy && current.Scheme == Uri.UriSchemeHttps))
                    throw new IOException("Unsupported artwork URL scheme");
                var request = (HttpWebRequest)WebRequest.Create(current);
                request.AllowAutoRedirect = false;
                request.UserAgent = UserAgent;
                request.Timeout = timeoutMs;
                request.ReadWriteTimeout = timeoutMs;
                request.Headers["Accept-Encoding"] = "identity";
                if (proxy) request.Headers["X-GS-Proxy-Key"] = ProxyKey ?? "";
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    int status = (int)response.StatusCode;
                    if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                    {
                        if (proxy) throw new IOException("Artwork proxy redirect rejected");
                        string location = response.Headers["Location"];
                        if (string.IsNullOrEmpty(location)) throw new IOException("Artwork redirect has no Location");
                        current = new Uri(current, location);
                        continue;
                    }
                    if (status != 200) throw new IOException("Artwork HTTP " + status);
                    using (var input = response.GetResponseStream())
                        return WriteArtwork(path, (buffer, count) => input.Read(buffer, 0, count),
                            response.ContentLength, cancel, maxBytes);
                }
            }
            throw new IOException("Too many artwork redirects");
        }

        internal static long WriteArtwork(string path, Func<byte[], int, int> read,
            long expected, Func<bool> cancel, int maxBytes)
        {
            if (maxBytes < 1 || maxBytes > 8 * 1024 * 1024 || expected > maxBytes)
                throw new IOException("Artwork exceeds byte limit");
            string part = path + ".part";
            try
            {
                byte[] buffer = new byte[32 * 1024];
                long received = 0;
                using (var output = new FileStream(part, FileMode.Create, FileAccess.Write,
                    FileShare.None, 32 * 1024, FileOptions.SequentialScan))
                {
                    for (;;)
                    {
                        if (cancel != null && cancel()) throw new OperationCanceledException();
                        int count = read(buffer, buffer.Length);
                        if (count < 0 || count > buffer.Length) throw new IOException("Invalid artwork read length");
                        if (count == 0) break;
                        received += count;
                        if (received > maxBytes || (expected >= 0 && received > expected))
                            throw new IOException("Artwork exceeds response length");
                        output.Write(buffer, 0, count);
                    }
                    if (expected >= 0 && received != expected) throw new IOException("Truncated artwork response");
                }
                if (cancel != null && cancel()) throw new OperationCanceledException();
                if (File.Exists(path)) File.Delete(path);
                File.Move(part, path);
                return received;
            }
            catch
            {
                try { if (File.Exists(part)) File.Delete(part); } catch { }
                throw;
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
        sealed class Delay { internal long Until; internal int Status; }
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        static readonly Dictionary<string, Delay> _until =
            new Dictionary<string, Delay>(StringComparer.OrdinalIgnoreCase);

        public static void Check(string url)
        {
            string host = Host(url);
            if (string.IsNullOrEmpty(host)) return;
            lock (_lock)
            {
                Delay delay;
                if (_until.TryGetValue(host, out delay) && delay.Until > Clock.ElapsedMilliseconds)
                    throw new ServiceHttpException(delay.Status,
                        Math.Max(1, (delay.Until - Clock.ElapsedMilliseconds + 999) / 1000).ToString(CultureInfo.InvariantCulture), "");
                _until.Remove(host);
            }
        }

        public static bool NoteResponse(string url, int status, string retryAfter)
        {
            if (status != 429 && status != 503) return false;
            int wait = ServiceHttpException.ParseRetryAfter(retryAfter, DateTime.UtcNow);
            if (wait <= 0) wait = status == 429 ? 60 : 15;
            string host = Host(url);
            if (string.IsNullOrEmpty(host)) return true;
            lock (_lock)
            {
                long until = Clock.ElapsedMilliseconds + (long)wait * 1000;
                Delay previous;
                if (!_until.TryGetValue(host, out previous) || until > previous.Until)
                    _until[host] = new Delay { Until = until, Status = status };
            }
            return true;
        }

        public static void NoteException(string url, Exception ex)
        {
            if (ex == null) return;
            for (int depth = 0; ex.InnerException != null && depth < 8; depth++)
            {
                if (ex is WebException || ex is ServiceHttpException) break;
                ex = ex.InnerException;
            }
            var native = ex as ServiceHttpException;
            if (native != null)
            {
                NoteResponse(url, native.StatusCode, native.RetryAfterSeconds.ToString(CultureInfo.InvariantCulture));
                return;
            }
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
            string userAgent = null, int maxBytes = DefaultResponseBytes)
        {
            var req = (HttpWebRequest)WebRequest.Create(finalUrl);
            req.Method = "GET";
            req.UserAgent = string.IsNullOrEmpty(userAgent) ? UserAgent : userAgent;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            req.AllowAutoRedirect = !addProxyKey;
            req.KeepAlive = false;
            if (!string.IsNullOrEmpty(referer)) req.Referer = referer;
            req.Accept = "text/html,application/json,*/*";
            req.Headers["Accept-Language"] = "en-US,en;q=0.8";
            if (!string.IsNullOrEmpty(bearer))
                req.Headers["Authorization"] = "Bearer " + bearer;
            if (addProxyKey)
                req.Headers["X-GS-Proxy-Key"] = ProxyKey ?? "";

            HttpWebResponse getResp;
            try { getResp = RejectProxyRedirect((HttpWebResponse)req.GetResponse(), addProxyKey); }
            catch (WebException webEx) { throw CooldownOrRethrow(finalUrl, webEx); }
            using (var resp = getResp)
            using (var stream = resp.GetResponseStream())
            using (var body = new MemoryStream())
            {
                if (resp.ContentLength > maxBytes) throw new IOException("HTTP response exceeds byte limit");
                var input = stream ?? Stream.Null;
                var block = new byte[8192]; int count;
                while ((count = input.Read(block, 0, block.Length)) > 0) {
                    if (body.Length + count > maxBytes) throw new IOException("HTTP response exceeds byte limit");
                    body.Write(block, 0, count);
                }
                body.Position = 0;
                using (var reader = new StreamReader(body, Encoding.UTF8)) return reader.ReadToEnd();
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
            req.AllowAutoRedirect = !addProxyKey;
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
            try { postResp = RejectProxyRedirect((HttpWebResponse)req.GetResponse(), addProxyKey); }
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

        static HttpWebResponse RejectProxyRedirect(HttpWebResponse response, bool authenticatedProxy)
        {
            // The proxy must follow upstream redirects itself. Never forward its custom key,
            // or accept a redirect page as JSON/file data, from this client connection.
            int status = (int)response.StatusCode;
            if (authenticatedProxy && status >= 300 && status < 400)
            {
                response.Dispose();
                throw new IOException("LAN proxy returned a redirect; check the configured proxy address");
            }
            return response;
        }

        static Exception CooldownOrRethrow(string url, WebException webEx)
        {
            var http = webEx != null ? webEx.Response as HttpWebResponse : null;
            if (http != null && ((int)http.StatusCode == 429 || (int)http.StatusCode == 503))
            {
                ProviderCooldown.NoteResponse(url, (int)http.StatusCode, http.Headers["Retry-After"]);
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
            string expectedTitleId, int rangeCount, string expectedPackageId = null, string expectedSha256 = null)
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
            if (existingBytes > 0 && File.Exists(ParallelDownloadCheckpoint.RangeMapPath(part)))
            {
                existingBytes = ParallelDownloadCheckpoint.RecoverPositionedPart(part, resume);
            }
            if (existingBytes > 0 && resume != null && !resume.CanResume(finalUrl, expectedTitleId, existingBytes, expectedPackageId))
                throw new Exception(DownloadResumeInfo.RestartRequired(
                    "saved partial metadata does not match this package"));
            if (existingBytes > 0 && resume == null)
                throw new Exception(DownloadResumeInfo.RestartRequired(
                    "saved partial metadata is missing"));

            // Strict resume previously trusted canonical origin/path, package
            // identity, strong ETag and exact total. A swapped package behind
            // those values would splice foreign bytes after the durable prefix,
            // so re-read the production PKG header and require it to match the
            // durable prefix before either the parallel or the single path runs.
            // Non-strict (rotating-URL) resumes keep their Content-Range behavior.
            if (existingBytes > 0 && resume != null && resume.StrictIdentity)
                RequirePkgHeaderMatch(finalUrl, part, existingBytes, resume, bearer, addProxyKey,
                    timeoutMs, cancel);

            if (rangeCount > 1)
            {
                    long total; string etag, effective, verifiedTitle = null; byte[] header = null;
                    if (((bearer == null && !addProxyKey && DownloadTransferSettings.TakeProbe(finalUrl, out total, out etag, out effective, out header, out verifiedTitle)) ||
                        TryProbeManagedContentLength(finalUrl, Math.Min(timeoutMs, 8000), bearer, addProxyKey, out total, out etag, out effective)) && total >= 8L * 1024 * 1024)
                    {
                        if (string.IsNullOrEmpty(expectedTitleId) && header != null) expectedTitleId = verifiedTitle;
                        string headerIdentity = resume == null ? null : resume.IntegrityHeader;
                        if (!DownloadTransferSettings.StrongEtag(etag))
                        {
                            if (string.IsNullOrEmpty(expectedTitleId))
                            { TraceDownloadDecision(false, DownloadDecision.ProbeValidator); goto single; }
                            if (header == null)
                            {
                                var probe = ReadManagedIntegrityHeader(effective, total, timeoutMs,
                                    DownloadTransferSettings.SameOrigin(finalUrl, effective) ? bearer : null,
                                    addProxyKey && DownloadTransferSettings.SameOrigin(finalUrl, effective), cancel);
                                header = probe;
                            }
                            headerIdentity = PkgIntegrity.TransferHeaderIdentity(header, total, expectedTitleId);
                            if (resume != null && !string.IsNullOrEmpty(resume.IntegrityHeader) && headerIdentity != resume.IntegrityHeader)
                                throw new IOException(DownloadResumeInfo.RestartRequired("package integrity header changed"));
                            etag = null;
                        }
                        if (existingBytes > 0 && (resume == null || total != resume.Total ||
                            !resume.MatchesResponse(effective, etag, null)))
                            throw new IOException(DownloadResumeInfo.RestartRequired("range resume identity changed"));
                        try
                        {
                            TraceDownloadDecision(false, DownloadDecision.ParallelSelected, rangeCount);
                            ValidatedParallelDownload.Run(finalUrl, effective, part, total, etag, rangeCount, expectedTitleId,
                                expectedPackageId, progress, cancel, (start, end, stopped, report) =>
                                    ManagedDownloadExactRangeTo(part, start, end, total, effective, Math.Min(timeoutMs, 30000),
                                        DownloadTransferSettings.SameOrigin(finalUrl, effective) ? bearer : null,
                                        addProxyKey && DownloadTransferSettings.SameOrigin(finalUrl, effective), stopped, report, etag), existingBytes, headerIdentity);
                            PkgIntegrity.VerifyTransfer(part, headerIdentity, expectedTitleId, expectedSha256, cancel);
                            long completed = FinishManagedPart(part, finalPath, expectedTitleId);
                            if (!string.IsNullOrEmpty(expectedSha256))
                                PkgIntegrity.RememberVerifiedSha256(finalPath, expectedSha256);
                            return completed;
                        }
                        catch (DownloadRangeRejectedException)
                        {
                            TraceDownloadDecision(false, DownloadDecision.RangeRejected, rangeCount);
                            ParallelDownloadCheckpoint.DeleteAll(part);
                            existingBytes = 0; resume = null;
                        }
                    }
                    else if (total > 0 && total < 8L * 1024 * 1024)
                        TraceDownloadDecision(false, DownloadDecision.ProbeLength);
            }
            else if (existingBytes > 0) TraceDownloadDecision(false, DownloadDecision.ResumeSingle);
            else if (rangeCount > 1) TraceDownloadDecision(false, DownloadDecision.ParallelBusy);
            single:
            if (cancel != null && cancel()) throw new OperationCanceledException();

            using (TransferLaneBudget.AcquireSingle(cancel))
            {
            TraceDownloadDecision(false, DownloadDecision.SingleSelected);
            var req = (HttpWebRequest)WebRequest.Create(finalUrl);
            req.Method = "GET";
            req.UserAgent = UserAgent;
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = 600000;
            req.AllowAutoRedirect = !addProxyKey;
            req.KeepAlive = false;
            req.Headers["Accept-Encoding"] = "identity";
            if (existingBytes > 0)
            {
                req.AddRange(existingBytes);
                if (resume != null && resume.StrictIdentity) req.Headers["If-Range"] = resume.IfRange;
                // Skip If-Range for RD — CDN ETags rotate and would force full restart
            }
            if (!string.IsNullOrEmpty(bearer))
                req.Headers["Authorization"] = "Bearer " + bearer;
            if (addProxyKey)
                req.Headers["X-GS-Proxy-Key"] = ProxyKey ?? "";

            var cancellation = new TransferCancellation(cancel, () => req.Abort());
            try
            {
            long bytesReadThis = 0;
            long expectedResponse = -1;
            long expectedFinal = -1;
            long expectedRangeSpan = -1;
            HttpWebResponse response;
            try
            {
                response = RejectProxyRedirect((HttpWebResponse)req.GetResponse(), addProxyKey);
            }
            catch (WebException ex)
            {
                response = ex.Response as HttpWebResponse;
                if (response == null || response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    var failure = DownloadHttpException.Find(ex);
                    if (response != null) response.Dispose();
                    if (failure != null) throw failure;
                    throw;
                }
            }

            using (var resp = response)
            {
                string effectiveUrl = resp.ResponseUri != null ? resp.ResponseUri.AbsoluteUri : finalUrl;
                string etag = resp.Headers["ETag"];
                string lastModified = resp.Headers["Last-Modified"];

                if (resume != null && resume.StrictIdentity && !DownloadTransferSettings.IdentityEncoding(resp.Headers["Content-Encoding"]))
                    throw new Exception(DownloadResumeInfo.RestartRequired("saved range representation encoding changed"));

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
                        throw new DownloadHttpException((int)resp.StatusCode, resp.Headers["Retry-After"]);

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
                        bytesReadThis = SequentialDownloadEngine.Copy((buffer, start, count) => input.Read(buffer, start, count),
                            output, expectedResponse, done, totalUi, progress, cancel,
                            metrics => RecordDownloadMetrics(false, "managed-http", metrics));
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

            PkgIntegrity.VerifyTransfer(part, resume == null ? null : resume.IntegrityHeader, expectedTitleId, expectedSha256, cancel);
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
            if (!string.IsNullOrEmpty(expectedSha256)) PkgIntegrity.RememberVerifiedSha256(finalPath, expectedSha256);
            DownloadResumeInfo.Delete(part);
            return new FileInfo(finalPath).Length;
            }
            finally
            {
                cancellation.Dispose();
            }
            }
        }

        // Re-reads the production PKG header and requires it to match the durable
        // local prefix. A changed header is a different package: restart from zero
        // with the partial kept on disk. A failed or truncated probe cannot prove
        // identity, so it fails the attempt without deleting anything; the next
        // retry re-probes and a transient outage keeps its resume.
        static void RequirePkgHeaderMatch(string url, string part, long existingBytes,
            DownloadResumeInfo resume, string bearer, bool addProxyKey, int timeoutMs, Func<bool> cancel)
        {
            int count = string.IsNullOrEmpty(resume.IntegrityHeader) ? DownloadResumeInfo.PkgHeaderLength : 0x1000;
            byte[] fresh;
            try { fresh = ReadManagedIntegrityHeader(url, resume.Total, timeoutMs, bearer, addProxyKey, cancel, count); }
            catch (WebException error)
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                var http = DownloadHttpException.Find(error);
                if (error.Response != null) error.Response.Close();
                if (http != null) throw new DownloadHttpException(http.StatusCode,
                    http.RetryAfterSeconds > 0 ? http.RetryAfterSeconds.ToString() : null, "reading package header");
                throw;
            }
            resume.RequireHeaderMatch(part, existingBytes, fresh);
        }

        static byte[] ReadManagedIntegrityHeader(string url, long total, int timeoutMs, string bearer,
            bool addProxyKey, Func<bool> cancel, int count = 0x1000)
        {
            var request = CreateManagedRangeRequest(url, 0, count - 1,
                timeoutMs <= 0 ? 8000 : Math.Min(timeoutMs, 8000), bearer, addProxyKey);
            using (var cancellation = new TransferCancellation(cancel, () => request.Abort()))
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if (!DownloadTransferSettings.IdentityEncoding(response.Headers["Content-Encoding"]))
                    throw new IOException(DownloadResumeInfo.RestartRequired("package header representation encoding changed"));
                long start, end, size; bool unsatisfied;
                if (response.StatusCode != HttpStatusCode.PartialContent ||
                    !DownloadResumeInfo.TryParseContentRange(response.Headers["Content-Range"], out start, out end, out size, out unsatisfied) ||
                    unsatisfied || start != 0 || end != count - 1 || size != total ||
                    (response.ContentLength >= 0 && response.ContentLength != count))
                    throw new IOException("Package header probe returned an unexpected range");
                using (var input = response.GetResponseStream())
                using (var output = new MemoryStream(count))
                {
                    ExactRangeTransfer.Copy((buffer, offset, length) => input == null ? 0 : input.Read(buffer, offset, length),
                        output, count, cancel, null);
                    return output.ToArray();
                }
            }
        }

        static bool TryProbeManagedContentLength(string url, int timeoutMs, string bearer,
            bool addProxyKey, out long length, out string etag, out string effective)
        {
            length = -1;
            etag = effective = null;
            try
            {
                var req = CreateManagedRangeRequest(url, 0, 0, timeoutMs, bearer, addProxyKey);
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    TraceDownloadProbe(false, (int)resp.StatusCode, resp.Headers["ETag"], resp.Headers["Last-Modified"],
                        resp.Headers["Content-Range"], resp.Headers["Content-Encoding"]);
                    long start, end, total;
                    bool unsatisfied;
                    if (resp.StatusCode != HttpStatusCode.PartialContent)
                    { TraceDownloadDecision(false, DownloadDecision.ProbeRange); return false; }
                    if (!DownloadTransferSettings.IdentityEncoding(resp.Headers["Content-Encoding"]))
                    { TraceDownloadDecision(false, DownloadDecision.ProbeEncoding); return false; }
                    if (!DownloadResumeInfo.TryParseContentRange(resp.Headers["Content-Range"],
                            out start, out end, out total, out unsatisfied) ||
                        unsatisfied || start != 0 || total <= 0)
                    { TraceDownloadDecision(false, DownloadDecision.ProbeLength); return false; }
                    length = total;
                    etag = resp.Headers["ETag"]; effective = resp.ResponseUri.AbsoluteUri;
                    TraceDownloadDecision(false, DownloadDecision.ProbeAccepted);
                    return true;
                }
            }
            catch { TraceDownloadDecision(false, DownloadDecision.ProbeError); return false; }
        }

        static void ManagedDownloadExactRangeTo(string part, long start, long end, long expectedTotal,
            string url, int timeoutMs, string bearer, bool addProxyKey, Func<bool> cancel,
            Action<int> onChunk, string etag = null)
        {
            var req = CreateManagedRangeRequest(url, start, end, timeoutMs, bearer, addProxyKey);
            if (etag != null) req.Headers["If-Range"] = etag;
            var cancellation = new TransferCancellation(cancel, () => req.Abort());

            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                {
                    long actualStart, actualEnd, actualTotal;
                    bool unsatisfied;
                    long expected = end - start + 1;
                    if ((etag != null && !string.Equals(etag, resp.Headers["ETag"], StringComparison.Ordinal)) ||
                        resp.ResponseUri.AbsoluteUri != url || !DownloadTransferSettings.IdentityEncoding(resp.Headers["Content-Encoding"]))
                        throw new DownloadRangeRejectedException("Range response identity changed");
                    if (resp.StatusCode != HttpStatusCode.PartialContent ||
                        !DownloadResumeInfo.TryParseContentRange(resp.Headers["Content-Range"],
                            out actualStart, out actualEnd, out actualTotal, out unsatisfied) ||
                        unsatisfied || actualStart != start || actualEnd != end ||
                        actualTotal != expectedTotal ||
                        (resp.ContentLength >= 0 && resp.ContentLength != expected))
                        throw new DownloadRangeRejectedException("invalid Content-Range for bytes=" + start + "-" + end);

                    using (var input = resp.GetResponseStream())
                    using (var output = new FileStream(part, FileMode.Open, FileAccess.Write,
                        FileShare.ReadWrite, 512 * 1024))
                    {
                        output.Position = start;
                        ExactRangeTransfer.Copy((buffer, offset, count) => input == null ? 0 : input.Read(buffer, offset, count),
                            output, expected, cancel, onChunk);
                        output.Flush(true);
                    }
                }
            }
            finally
            {
                cancellation.Dispose();
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
            req.AllowAutoRedirect = !addProxyKey;
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
        public bool StrictIdentity;
        public string IntegrityHeader;
        bool integrityRevalidated;
        public string SourceResourceKey;
        public string EffectiveResourceKey;
        bool boundRenewal;

        public static string RestartRequired(string reason)
        {
            return RestartPrefix + reason + "; partial kept — CROSS restarts from zero";
        }

        public static bool IsRestartRequired(string message)
        {
            const string nativePrefix = "Native HTTPS DL: ";
            if (message != null && message.StartsWith(nativePrefix, StringComparison.Ordinal))
                message = message.Substring(nativePrefix.Length);
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
                SourceResourceKey = ResourceFingerprint(sourceUrl),
                EffectiveResourceKey = ResourceFingerprint(effectiveUrl),
                ETag = etag ?? "",
                LastModified = lastModified ?? "",
                TitleId = titleId ?? "",
                Total = total,
                PackageId = packageId ?? ""
            };
        }

        public bool CanResume(string sourceUrl, string titleId, long localLength, string packageId = null)
        {
            boundRenewal = false;
            if (localLength <= 0 || Total < localLength || Total <= 0) return false;
            // Package identity binds saved bytes to one package: a same-title,
            // same-size replacement must not splice into these bytes. Signed-URL
            // rotation keeps the same identity, so legitimate resumes still pass.
            string stored = (PackageId ?? "").Trim();
            string incoming = (packageId ?? "").Trim();
            boundRenewal = IsBoundIdentity(stored) && string.Equals(stored, incoming, StringComparison.OrdinalIgnoreCase);
            if (StrictIdentity && !string.Equals(SourceKey, Fingerprint(sourceUrl), StringComparison.Ordinal) &&
                !(boundRenewal && SameResource(SourceResourceKey, sourceUrl))) return false;
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
            if (StrictIdentity) return (string.Equals(EffectiveKey, Fingerprint(effectiveUrl), StringComparison.Ordinal) ||
                (boundRenewal && SameResource(EffectiveResourceKey, effectiveUrl))) &&
                ((DownloadTransferSettings.StrongEtag(ETag) && string.Equals(ETag, etag, StringComparison.Ordinal)) ||
                    (string.IsNullOrEmpty(ETag) && !string.IsNullOrEmpty(IntegrityHeader) && integrityRevalidated));
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

        internal void RequireHeaderMatch(string part, long existingBytes, byte[] fresh)
        {
            int count = (int)Math.Min(existingBytes, fresh.Length);
            var local = new byte[count];
            using (var input = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int read = 0;
                while (read < count)
                {
                    int got = input.Read(local, read, count - read);
                    if (got <= 0) throw new IOException("Partial prefix is unreadable");
                    read += got;
                }
            }
            if (count <= 0 || !PkgHeaderEquals(fresh, local, count))
                throw new IOException(RestartRequired("downloaded package header changed; saved bytes belong to a different package"));
            if (!string.IsNullOrEmpty(IntegrityHeader))
            {
                if (!string.Equals(PkgIntegrity.TransferHeaderIdentity(fresh, Total, TitleId), IntegrityHeader, StringComparison.Ordinal))
                    throw new IOException(RestartRequired("package integrity header changed"));
                integrityRevalidated = true;
            }
        }

        public void Save(string partPath)
        {
            string path = MetadataPath(partPath);
            bool v4 = StrictIdentity && !string.IsNullOrEmpty(SourceResourceKey) && !string.IsNullOrEmpty(EffectiveResourceKey);
            bool v5 = v4 && !string.IsNullOrEmpty(IntegrityHeader);
            string body = (v5 ? "5\n" : v4 ? "4\n" : StrictIdentity ? "3\n" : "2\n") + (SourceKey ?? "") + "\n" + (EffectiveKey ?? "") + "\n" +
                          Encode(ETag) + "\n" + Encode(LastModified) + "\n" + Encode(TitleId) + "\n" +
                          Total.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n" +
                          Encode(PackageId);
            if (v4) body += "\n" + SourceResourceKey + "\n" + EffectiveResourceKey;
            if (v5) body += "\n" + IntegrityHeader;
            AtomicFile.WriteText(path, body);
        }

        public static DownloadResumeInfo Load(string partPath)
        {
            try
            {
                string[] lines = File.ReadAllLines(MetadataPath(partPath));
                long total;
                // v2 carries PackageId; an empty identity leaves no 8th line
                // because ReadAllLines drops the trailing empty entry.
                bool v2 = (lines.Length == 8 || lines.Length == 7) && (lines[0] == "2" || lines[0] == "3");
                bool v5 = lines.Length == 11 && lines[0] == "5" && System.Text.RegularExpressions.Regex.IsMatch(lines[10], "\\A[0-9A-F]{64}\\z");
                bool v4 = (lines.Length == 10 && lines[0] == "4") || v5;
                if ((!v4 && !v2 && (lines.Length != 7 || lines[0] != "1")) ||
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
                    StrictIdentity = lines[0] == "3" || v4,
                    PackageId = (v4 || (v2 && lines.Length == 8)) ? Decode(lines[7]) : "",
                    SourceResourceKey = v4 ? lines[8] : "",
                    EffectiveResourceKey = v4 ? lines[9] : "",
                    IntegrityHeader = v5 ? lines[10] : ""
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
            if (string.IsNullOrEmpty(value) || value.Length > 256) return false;
            int at = 0, limit = value.Length;
            while (at < limit && (value[at] == ' ' || value[at] == '\t')) at++;
            while (limit > at && (value[limit - 1] == ' ' || value[limit - 1] == '\t')) limit--;
            if (limit - at < 8 || string.Compare(value, at, "bytes", 0, 5,
                StringComparison.OrdinalIgnoreCase) != 0 || value[at + 5] != ' ') return false;
            at += 6;
            bool missing = value[at] == '*';
            long a = -1, b = -1, n;
            if (missing) at++;
            else if (!TryReadRangeNumber(value, ref at, limit, out a) || at == limit || value[at++] != '-' ||
                !TryReadRangeNumber(value, ref at, limit, out b) || b < a) return false;
            if (at == limit || value[at++] != '/' || !TryReadRangeNumber(value, ref at, limit, out n) ||
                at != limit || (!missing && n <= b)) return false;
            start = a; end = b; total = n; unsatisfied = missing;
            return true;
        }

        // ASCII decimal only; culture, signs and embedded whitespace are not
        // part of a byte range. Match https/http_range.h without an extra ABI.
        static bool TryReadRangeNumber(string value, ref int at, int limit, out long number)
        {
            number = 0;
            if (at == limit || value[at] < '0' || value[at] > '9') return false;
            do
            {
                int digit = value[at] - '0';
                if (number > (long.MaxValue - digit) / 10) return false;
                number = number * 10 + digit;
                at++;
            } while (at < limit && value[at] >= '0' && value[at] <= '9');
            return true;
        }

        static string MetadataPath(string partPath) { return partPath + ".resume"; }

        static bool IsBoundIdentity(string value)
        {
            if (value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Length != 71) return false;
                for (int i = 7; i < value.Length; i++) if (!Uri.IsHexDigit(value[i])) return false;
                return true;
            }
            return value.Length == 36 && value[6] == '-' && value[16] == '_' && value[19] == '-' &&
                string.Equals(value.Substring(7, 4), "CUSA", StringComparison.OrdinalIgnoreCase);
        }

        static bool SameResource(string saved, string url)
        {
            return !string.IsNullOrEmpty(saved) && string.Equals(saved, ResourceFingerprint(url), StringComparison.Ordinal);
        }

        // Production managed PKG preflight header: the first 0x438 bytes carry the
        // magic, content ID, kind and package size. Mirrors
        // LoopbackPkgFeeder.HeaderBytes without referencing the feeder so the
        // isolated transfer tests keep compiling against this file alone.
        internal const int PkgHeaderLength = 0x438;

        internal static bool LooksLikePkgHeader(byte[] data, int count)
        {
            return data != null && count >= PkgHeaderLength && count <= data.Length &&
                data[0] == 0x7F && data[1] == 0x43 && data[2] == 0x4E && data[3] == 0x54;
        }

        /// <summary>SHA-256 (lowercase hex) of the full production header.
        /// Empty when the probe is too short or not a PKG header.</summary>
        internal static string HashPkgHeader(byte[] data, int count)
        {
            if (!LooksLikePkgHeader(data, count)) return "";
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(data, 0, PkgHeaderLength);
                var text = new StringBuilder(digest.Length * 2);
                foreach (byte b in digest) text.Append(b.ToString("x2"));
                return text.ToString();
            }
        }

        internal static bool PkgHeaderEquals(byte[] first, byte[] second, int count)
        {
            if (first == null || second == null || count <= 0 ||
                count > first.Length || count > second.Length) return false;
            for (int i = 0; i < count; i++)
                if (first[i] != second[i]) return false;
            return true;
        }
        // Query signatures may rotate; retaining bytes still requires package
        // identity, a strong ETag and an exact total.
        static string ResourceFingerprint(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return "";
            return Fingerprint(uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped));
        }

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
