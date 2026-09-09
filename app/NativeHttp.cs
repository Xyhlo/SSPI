using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using Orbis.Internals;

namespace Orbis
{
    /// <summary>
    /// Firmware sceHttp HTTPS (OrbisShelf stack). Bootstrap via libkernel LoadStartModule
    /// then sysmodule internal NET/SSL/HTTP, then P/Invoke symbols.
    /// </summary>
    internal static class NativeHttp
    {
        public const int MethodGet = 0;
        public const int MethodPost = 1;
        public const int HttpVersion11 = 2;
        public const int HeaderOverwrite = 0;

        const int SysmodInternalNet = unchecked((int)0x8000001C);
        const int SysmodInternalHttp = unchecked((int)0x8000000A);
        const int SysmodInternalSsl = unchecked((int)0x8000000B);

        // Larger pools help concurrent three-range HTTPS downloads.
        const int NetPoolSize = 1024 * 1024;
        const int SslPoolSize = 4 * 1024 * 1024;
        const int HttpPoolSize = 2 * 1024 * 1024;
        const int DlBufSize = 1024 * 1024;
        const long ParallelMinBytes = 8L * 1024 * 1024;
        const int ParallelReadBuf = 256 * 1024;
        const int ParallelFileBuf = 512 * 1024;
        const int MaxRedirects = 8;
        const int MaxBodyDefault = 8 * 1024 * 1024;
        const uint ConnectTimeoutUs = 30u * 1000u * 1000u;
        const uint SendTimeoutUs = 30u * 1000u * 1000u;

        static readonly object Gate = new object();
        static readonly object OpenGate = new object();
        static bool _tried;
        static bool _ready;
        static string _initError = "not initialized";
        static readonly StringBuilder _log = new StringBuilder();
        static int _netPool = -1;
        static int _sslCtx = -1;
        static int _httpCtx = -1;
        static int _nativeRootCount;
        static int _nativeRootAttemptCount;
        static int _nativeRootLoadResult = int.MinValue;
        static int _nativePinnedIntermediateCount;
        // sceHttpsLoadCert may retain these pointers for the HTTP context lifetime.
        static readonly List<IntPtr> NativeCaAllocations = new List<IntPtr>();
        static readonly string[] NativeRootSubjectMarkers =
        {
            // Current Game Search services and common public/CDN chains.
            "CN=ISRG Root X1,",
            "CN=ISRG Root X2,",
            "CN=DigiCert Global Root G2,",
            "OU=GlobalSign Root CA - R3",
            "OU=GlobalSign Root CA - R6",
            "CN=GlobalSign Root R46,",
            "CN=USERTrust RSA Certification Authority,",
            "CN=COMODO RSA Certification Authority,",
            "CN=Sectigo Public Server Authentication Root R46,",
            "CN=Amazon Root CA 1,",
            "CN=GTS Root R1,",
            "CN=GTS Root R2,",
            "CN=GTS Root R3,",
            "CN=GTS Root R4,",
            "CN=Go Daddy Root Certificate Authority - G2,",
            "CN=Microsoft RSA Root Certificate Authority 2017,",
            "CN=DigiCert Trusted Root G4,"
        };
        // api.real-debrid.com currently chains through this DigiCert-issued
        // intermediate. Older libSceSsl builds can fail to assemble the server's
        // supplied intermediate even when DigiCert Global Root G2 is loaded, so
        // load only the exact DigiCert-published certificate as a supplemental
        // trust anchor. SHA-256 C06E...230E, valid through 2027-11-02.
        const string RealDebridGeoTrustIntermediateSha256 =
            "C06E307F7CFC1D32FA72A4C033C87B90019AF216F0775D64978A2ECA6C8A230E";

        public static bool Available
        {
            get { EnsureInit(); return _ready; }
        }

        public static string InitDetail
        {
            get
            {
                EnsureInit();
                return _ready
                    ? ("sceHttp OK; CA=" + _nativeRootCount + "/" + _nativeRootAttemptCount +
                       " RD-ICA=" + _nativePinnedIntermediateCount +
                       " rc=0x" + _nativeRootLoadResult.ToString("X"))
                    : ("sceHttp FAIL: " + _initError);
            }
        }

        public static void EnsureInit()
        {
            lock (Gate)
            {
                if (_tried) return;
                _tried = true;
                _log.Length = 0;
                try
                {
                    // 1) Preload firmware SPRX via libkernel (always resident) and/or eboot IC
                    PreloadSprx("/system/common/lib/libSceSysmodule.sprx");
                    PreloadSprx("/system/common/lib/libSceNet.sprx");
                    PreloadSprx("/system/common/lib/libSceSsl.sprx");
                    PreloadSprx("/system/common/lib/libSceHttp.sprx");
                    // short names for mono_dl_fallback cache
                    PreloadSprx("libSceSysmodule.sprx");
                    PreloadSprx("libSceSsl.sprx");
                    PreloadSprx("libSceHttp.sprx");

                    // 2) sysmodule internal loads (log RC, do not swallow)
                    LogRc("sys_NET", TrySysInternal(SysmodInternalNet));
                    LogRc("sys_SSL", TrySysInternal(SysmodInternalSsl));
                    LogRc("sys_HTTP", TrySysInternal(SysmodInternalHttp));

                    // 3) net / ssl / http contexts
                    int nr = CallInt("sceNetInit", () => sceNetInit());
                    LogRc("sceNetInit", nr);

                    _netPool = CallInt("sceNetPoolCreate", () => sceNetPoolCreate("GameSearchNet", NetPoolSize, 0));
                    LogRc("sceNetPoolCreate", _netPool);
                    if (_netPool < 0)
                    {
                        Fail("sceNetPoolCreate 0x" + _netPool.ToString("X"));
                        return;
                    }

                    _sslCtx = CallInt("sceSslInit", () => sceSslInit((UIntPtr)SslPoolSize));
                    LogRc("sceSslInit", _sslCtx);
                    if (_sslCtx < 0)
                    {
                        Fail("sceSslInit 0x" + _sslCtx.ToString("X"));
                        return;
                    }

                    _httpCtx = CallInt("sceHttpInit", () => sceHttpInit(_netPool, _sslCtx, (UIntPtr)HttpPoolSize));
                    LogRc("sceHttpInit", _httpCtx);
                    if (_httpCtx < 0)
                    {
                        Fail("sceHttpInit 0x" + _httpCtx.ToString("X"));
                        return;
                    }

                    // 4) Load the current Mozilla roots shipped in /app0. The PS4 firmware
                    // trust store is too old for some current public certificate chains.
                    TryLoadBundledRoots();

                    // 5) live probe
                    try
                    {
                        string probe = GetStringRaw("https://api.real-debrid.com/rest/1.0/time", 15000, null, null, 256);
                        _log.Append("probe_ok len=").Append(probe != null ? probe.Length : 0).Append("; ");
                    }
                    catch (Exception pex)
                    {
                        // init contexts OK but request failed — still mark ready so callers see Native HTTPS: errors
                        _log.Append("probe_fail=").Append(pex.Message).Append("; ");
                    }

                    _ready = true;
                    _initError = "ok";
                    _log.Append("READY");
                }
                catch (DllNotFoundException dex)
                {
                    Fail("DllNotFound: " + dex.Message);
                }
                catch (EntryPointNotFoundException eex)
                {
                    Fail("EntryPointNotFound: " + eex.Message);
                }
                catch (Exception ex)
                {
                    Fail(ex.GetType().Name + ": " + ex.Message);
                }
                FlushLog();
            }
        }

        static void Fail(string msg)
        {
            _ready = false;
            _initError = msg;
            _log.Append("FAIL=").Append(msg);
        }

        static void LogRc(string step, int rc)
        {
            _log.Append(step).Append('=').Append(rc).Append("(0x").Append(rc.ToString("X")).Append("); ");
        }

        static int CallInt(string name, Func<int> fn)
        {
            try { return fn(); }
            catch (Exception ex)
            {
                _log.Append(name).Append("_ex=").Append(ex.GetType().Name).Append(':').Append(ex.Message).Append("; ");
                throw;
            }
        }

        static int TrySysInternal(int id)
        {
            try { return sceSysmoduleLoadModuleInternal(id); }
            catch (Exception ex)
            {
                _log.Append("sys_ex_").Append(id.ToString("X")).Append('=').Append(ex.GetType().Name).Append("; ");
                return -1;
            }
        }

        static void PreloadSprx(string path)
        {
            try
            {
                // Prefer eboot InternalCall if declared
                int h = Kernel.TryLoadStartModule(path);
                _log.Append("load[").Append(path).Append("]=").Append(h).Append("; ");
            }
            catch (Exception ex)
            {
                _log.Append("load_ic_ex=").Append(ex.GetType().Name).Append("; ");
            }
            try
            {
                int h2 = sceKernelLoadStartModule(path, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
                _log.Append("kload[").Append(path).Append("]=0x").Append(h2.ToString("X")).Append("; ");
            }
            catch (Exception ex)
            {
                _log.Append("kload_ex=").Append(ex.GetType().Name).Append(':').Append(ex.Message).Append("; ");
            }
        }

        static void FlushLog()
        {
            try
            {
                string body = "ready=" + _ready + " detail=" + _initError +
                              " netPool=" + _netPool + " ssl=" + _sslCtx + " http=" + _httpCtx +
                              "\n" + _log + "\n";
                File.WriteAllText(Path.Combine(AppSettings.DataDir, "native-http.log"), body);
            }
            catch { }
        }

        static void TryLoadBundledRoots()
        {
            string certPath = FindCaBundle();
            if (string.IsNullOrEmpty(certPath))
            {
                _log.Append("native_ca=missing; ");
                return;
            }

            var allocations = new List<IntPtr>();
            try
            {
                _nativePinnedIntermediateCount = 0;
                List<byte[]> certificates = ReadPemCertificates(certPath, false);
                string realDebridIntermediatePath = FindRealDebridIntermediate();
                if (!string.IsNullOrEmpty(realDebridIntermediatePath))
                {
                    List<byte[]> realDebridIntermediates =
                        ReadPemCertificates(realDebridIntermediatePath, true);
                    // Put the firmware-specific compatibility anchor first. Some
                    // old NanoSSL builds are unreliable when many CAs are loaded.
                    certificates.InsertRange(0, realDebridIntermediates);
                }
                if (certificates.Count == 0)
                {
                    _log.Append("native_ca=empty; ");
                    return;
                }

                var entries = new List<IntPtr>(certificates.Count);
                foreach (byte[] certificateData in certificates)
                {
                    IntPtr data = Marshal.AllocHGlobal(certificateData.Length);
                    allocations.Add(data);
                    Marshal.Copy(certificateData, 0, data, certificateData.Length);

                    // PS4 SceHttpsData/SslPem is { char *ptr; size_t size } and
                    // sceHttpsLoadCert takes an array of pointers to those records.
                    // The process is x86-64, so each record is two pointer-sized fields.
                    IntPtr entry = Marshal.AllocHGlobal(IntPtr.Size * 2);
                    allocations.Add(entry);
                    Marshal.WriteIntPtr(entry, 0, data);
                    if (IntPtr.Size == 8)
                        Marshal.WriteInt64(entry, IntPtr.Size, certificateData.LongLength);
                    else
                        Marshal.WriteInt32(entry, IntPtr.Size, certificateData.Length);
                    entries.Add(entry);
                }

                IntPtr list = Marshal.AllocHGlobal(entries.Count * IntPtr.Size);
                allocations.Add(list);
                for (int i = 0; i < entries.Count; i++)
                    Marshal.WriteIntPtr(list, i * IntPtr.Size, entries[i]);

                int rc;
                try
                {
                    _nativeRootAttemptCount = entries.Count;
                    rc = sceHttpsLoadCert(_httpCtx, entries.Count, list, IntPtr.Zero, IntPtr.Zero);
                    _nativeRootLoadResult = rc;
                }
                catch (EntryPointNotFoundException)
                {
                    _log.Append("native_ca_api=missing; ");
                    FreeNativeAllocations(allocations);
                    return;
                }

                _log.Append("sceHttpsLoadCert=").Append(rc).Append("(0x")
                    .Append(rc.ToString("X")).Append(") PEM_roots=").Append(entries.Count).Append("; ");
                if (rc < 0)
                {
                    FreeNativeAllocations(allocations);
                    return;
                }

                NativeCaAllocations.AddRange(allocations);
                _nativeRootCount = entries.Count;
            }
            catch (Exception ex)
            {
                FreeNativeAllocations(allocations);
                _log.Append("native_ca_ex=").Append(ex.GetType().Name).Append(':')
                    .Append(ex.Message).Append("; ");
            }
        }

        static string FindCaBundle()
        {
            string baseDir = ".";
            try { baseDir = IO.GetAppBaseDirectory() ?? "."; } catch { }
            string[] paths =
            {
                Path.Combine(baseDir, "assets", "certs", "ca-certificates.crt"),
                Path.Combine(baseDir, "certs", "ca-certificates.crt"),
                "/app0/assets/certs/ca-certificates.crt",
                "/app0/certs/ca-certificates.crt"
            };
            foreach (string path in paths)
                if (File.Exists(path)) return path;
            return null;
        }

        static string FindRealDebridIntermediate()
        {
            string baseDir = ".";
            try { baseDir = IO.GetAppBaseDirectory() ?? "."; } catch { }
            const string fileName = "real-debrid-geotrust-tls-rsa-ca-g1.pem";
            string[] paths =
            {
                Path.Combine(baseDir, "assets", "certs", fileName),
                Path.Combine(baseDir, "certs", fileName),
                "/app0/assets/certs/" + fileName,
                "/app0/certs/" + fileName
            };
            foreach (string path in paths)
                if (File.Exists(path)) return path;
            return null;
        }

        static List<byte[]> ReadPemCertificates(string path, bool allowPinnedIntermediate)
        {
            const string begin = "-----BEGIN CERTIFICATE-----";
            const string end = "-----END CERTIFICATE-----";
            const int maxCertificates = 256;
            const int maxCertificateBytes = 64 * 1024;
            const int maxTotalBytes = 2 * 1024 * 1024;

            string pem = File.ReadAllText(path);
            var certificates = new List<byte[]>();
            int position = 0;
            int totalBytes = 0;
            while (certificates.Count < maxCertificates)
            {
                int blockStart = pem.IndexOf(begin, position, StringComparison.Ordinal);
                if (blockStart < 0) break;
                int first = blockStart + begin.Length;
                int last = pem.IndexOf(end, first, StringComparison.Ordinal);
                if (last < 0) throw new InvalidDataException("Unterminated CA certificate");
                int blockEnd = last + end.Length;

                string encoded = pem.Substring(first, last - first)
                    .Replace("\r", "").Replace("\n", "").Replace(" ", "").Replace("\t", "");
                byte[] der = Convert.FromBase64String(encoded);
                if (der.Length == 0 || der.Length > maxCertificateBytes)
                    throw new InvalidDataException("Invalid CA certificate size");

                bool selected = false;
                X509Certificate2 cert = null;
                try
                {
                    cert = new X509Certificate2(der);
                    for (int i = 0; i < NativeRootSubjectMarkers.Length; i++)
                    {
                        if (cert.Subject.IndexOf(NativeRootSubjectMarkers[i], StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            selected = true;
                            break;
                        }
                    }
                    if (!selected && allowPinnedIntermediate &&
                        HasSha256Fingerprint(der, RealDebridGeoTrustIntermediateSha256))
                    {
                        selected = true;
                        _nativePinnedIntermediateCount++;
                    }
                }
                catch
                {
                    selected = false;
                }
                finally
                {
                    if (cert != null) try { cert.Reset(); } catch { }
                }

                if (selected)
                {
                    // libSceSsl's HTTPS certificate loader expects the encoded PEM
                    // object, not the decoded ASN.1 DER payload. Preserve the complete
                    // delimiters and use the CRLF form accepted by NanoSSL on hardware.
                    string block = pem.Substring(blockStart, blockEnd - blockStart)
                        .Replace("\r\n", "\n").Replace("\r", "\n")
                        .Replace("\n", "\r\n") + "\r\n";
                    byte[] pemData = Encoding.ASCII.GetBytes(block);
                    totalBytes = checked(totalBytes + pemData.Length);
                    if (totalBytes > maxTotalBytes)
                        throw new InvalidDataException("CA bundle is too large");
                    certificates.Add(pemData);
                }

                position = blockEnd;
            }
            return certificates;
        }

        static bool HasSha256Fingerprint(byte[] certificateDer, string expectedHex)
        {
            if (certificateDer == null || string.IsNullOrEmpty(expectedHex)) return false;
            using (SHA256 sha256 = SHA256.Create())
            {
                byte[] actual = sha256.ComputeHash(certificateDer);
                if (expectedHex.Length != actual.Length * 2) return false;
                for (int i = 0; i < actual.Length; i++)
                {
                    int high = HexNibble(expectedHex[i * 2]);
                    int low = HexNibble(expectedHex[i * 2 + 1]);
                    if (high < 0 || low < 0 || actual[i] != (byte)((high << 4) | low))
                        return false;
                }
                return true;
            }
        }

        static int HexNibble(char value)
        {
            if (value >= '0' && value <= '9') return value - '0';
            if (value >= 'A' && value <= 'F') return value - 'A' + 10;
            if (value >= 'a' && value <= 'f') return value - 'a' + 10;
            return -1;
        }

        static void FreeNativeAllocations(List<IntPtr> allocations)
        {
            for (int i = allocations.Count - 1; i >= 0; i--)
            {
                try { if (allocations[i] != IntPtr.Zero) Marshal.FreeHGlobal(allocations[i]); }
                catch { }
            }
            allocations.Clear();
        }

        public static string GetString(string url, int timeoutMs, string referer, string bearer, int maxBytes = 0,
            string userAgent = null)
        {
            EnsureInit();
            if (!_ready) throw new Exception(_initError);
            if (maxBytes <= 0) maxBytes = MaxBodyDefault;
            int status;
            string body = Request(MethodGet, url, null, null, referer, bearer, timeoutMs, maxBytes, out status,
                userAgent);
            if (status < 200 || status >= 300)
                throw new Exception("HTTP " + status + " " + Clip(body, 120));
            return body;
        }

        static string GetStringRaw(string url, int timeoutMs, string referer, string bearer, int maxBytes)
        {
            int status;
            return Request(MethodGet, url, null, null, referer, bearer, timeoutMs, maxBytes, out status, null);
        }

        public static string PostForm(string url, string formBody, int timeoutMs, string referer, string bearer)
        {
            EnsureInit();
            if (!_ready) throw new Exception(_initError);
            byte[] data = Encoding.UTF8.GetBytes(formBody ?? "");
            int status;
            string body = Request(MethodPost, url, data, "application/x-www-form-urlencoded",
                referer, bearer, timeoutMs, MaxBodyDefault, out status);
            if (status < 200 || status >= 300)
            {
                if (!string.IsNullOrEmpty(body) && body.IndexOf('{') >= 0)
                    return body;
                throw new Exception("HTTP " + status + " " + Clip(body, 120));
            }
            return body;
        }

        public static HttpRangeResult ReadRange(string url, long start, int count, int timeoutMs)
        {
            EnsureInit();
            if (!_ready) throw new Exception(_initError);
            if (start < 0 || count <= 0) throw new ArgumentOutOfRangeException();

            string finalUrl;
            int status;
            int tmpl, conn, req;
            string range = "bytes=" + start + "-" + checked(start + count - 1);
            OpenFollow(MethodGet, url, null, null, null, null, timeoutMs, range, null, null,
                out tmpl, out conn, out req, out status, out finalUrl);
            try
            {
                if (status != 206 && !(status == 200 && start == 0))
                    throw new Exception("HTTP " + status + " reading PKG header");

                long total = -1;
                if (status == 206)
                {
                    long rangeStart, rangeEnd;
                    bool unsatisfied;
                    if (!DownloadResumeInfo.TryParseContentRange(GetHeader(req, "Content-Range"),
                        out rangeStart, out rangeEnd, out total, out unsatisfied) || unsatisfied || rangeStart != start)
                        throw new Exception("Invalid Content-Range reading PKG header");
                }
                else
                {
                    int lengthType;
                    UIntPtr length;
                    if (sceHttpGetResponseContentLength(req, out lengthType, out length) >= 0 && lengthType == 0)
                        total = (long)length.ToUInt64();
                }

                var output = new MemoryStream(count);
                byte[] buffer = new byte[Math.Min(16 * 1024, count)];
                while (output.Length < count)
                {
                    int wanted = Math.Min(buffer.Length, count - (int)output.Length);
                    int read = sceHttpReadData(req, buffer, (uint)wanted);
                    if (read < 0) throw new Exception("sceHttpReadData 0x" + read.ToString("X"));
                    if (read == 0) break;
                    output.Write(buffer, 0, read);
                }
                return new HttpRangeResult { Data = output.ToArray(), Total = total, EffectiveUrl = finalUrl };
            }
            finally { Close(tmpl, conn, req); }
        }

        public static long DownloadFile(string url, string destPath, Action<long, long> progress,
            int timeoutMs, string bearer)
        {
            return DownloadFileResumable(url, destPath, 0, progress, null, timeoutMs, bearer, null);
        }

        /// <summary>
        /// Resume-aware download. Validates Content-Length/Range. Leaves .part on pause.
        /// Fresh large files use configurable parallel streams when the CDN supports Range.
        /// Pause consolidates the largest contiguous prefix into the normal resumable .part.
        /// expectedTitleId: if set, PKG structural validate before rename to final path.
        /// </summary>
        public static long DownloadFileResumable(string url, string destPath, long existingBytes,
            Action<long, long> progress, Func<bool> cancel, int timeoutMs, string bearer,
            string expectedTitleId = null,
            int rangeCount = DownloadTransferSettings.DefaultRangeCount,
            string expectedPackageId = null)
        {
            EnsureInit();
            if (!_ready) throw new Exception(_initError);

            string dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            string part = destPath.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                ? destPath : destPath + ".part";
            string finalPath = destPath.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                ? destPath.Substring(0, destPath.Length - 5) : destPath;

            if (existingBytes < 0) existingBytes = 0;
            if (existingBytes > 0 && File.Exists(part))
                existingBytes = new FileInfo(part).Length;
            else if (existingBytes > 0 && !File.Exists(part))
                existingBytes = 0;

            DownloadResumeInfo resume = existingBytes > 0 ? DownloadResumeInfo.Load(part) : null;
            if (existingBytes > 0 && resume == null)
            {
                // Crash residue from positioned parallel writes: adopt the verified
                // contiguous prefix, drop the sparse tail, continue under identity rule.
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
                        resume = DownloadResumeInfo.Create(url, url, null, null,
                            mapTotal, expectedTitleId, expectedPackageId);
                        resume.Save(part);
                    }
                }
            }
            if (existingBytes > 0 && resume != null && !resume.CanResume(url, expectedTitleId, existingBytes, expectedPackageId))
                throw new Exception(DownloadResumeInfo.RestartRequired(
                    "saved partial metadata does not match this package"));
            // A partial without its source metadata cannot be resumed safely.
            if (existingBytes > 0 && resume == null)
                throw new Exception(DownloadResumeInfo.RestartRequired(
                    "saved partial metadata is missing"));

            return DownloadFileSingle(url, part, finalPath, existingBytes, resume, progress, cancel,
                timeoutMs, bearer, expectedTitleId, expectedPackageId);
        }

        static int _parallelPackageActive;

        static bool AllowParallelPackage()
        {
            return Interlocked.CompareExchange(ref _parallelPackageActive, 1, 0) == 0;
        }

        static void ReleaseParallelPackage()
        {
            Interlocked.Exchange(ref _parallelPackageActive, 0);
        }

        static bool IsLocalIoFailure(Exception ex)
        {
            if (ex == null) return false;
            if (ex is IOException || ex is UnauthorizedAccessException || ex is OutOfMemoryException)
                return true;
            string m = ex.Message ?? "";
            return m.IndexOf("disk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   m.IndexOf("space", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   m.IndexOf("I/O", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool TryProbeContentLength(string url, string bearer, int timeoutMs, out long length)
        {
            length = -1;
            int tmpl = -1, conn = -1, req = -1;
            try
            {
                string finalUrl;
                int status;
                OpenFollow(MethodGet, url, null, null, null, bearer, timeoutMs, "bytes=0-0", null, null,
                    out tmpl, out conn, out req, out status, out finalUrl);
                // Real Range: 206 + Content-Range total. Do not require end==0; some CDNs
                // answer bytes=0-0 with a larger first slice (0-8191/TOTAL) and still split.
                if (status != 206)
                    return false;
                long start, end, total;
                bool unsatisfied;
                if (DownloadResumeInfo.TryParseContentRange(GetHeader(req, "Content-Range"),
                        out start, out end, out total, out unsatisfied) &&
                    !unsatisfied && start == 0 && total > ParallelMinBytes)
                {
                    length = total;
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
            finally { Close(tmpl, conn, req); }
        }

        static long DownloadFileParallelN(string url, string part, string finalPath, long total,
            int rangeCount, Action<long, long> progress, Func<bool> cancel, int timeoutMs,
            string bearer, string expectedTitleId, string expectedPackageId = null)
        {
            int n = DownloadTransferSettings.ClampRangeCount(rangeCount);
            if (n < 2 || total < ParallelMinBytes)
                throw new Exception("too small for parallel");
            if (cancel != null && cancel())
                throw new OperationCanceledException("paused");

            ParallelDownloadCheckpoint.DeleteAll(part);

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

            var err = new Exception[n];
            var progLock = new object();
            long lastReport = -1;
            long lastMapSave = 0;
            int failFlag = 0;

            Func<bool> cancelOrFail = () =>
                (cancel != null && cancel()) || Interlocked.CompareExchange(ref failFlag, 0, 0) != 0;

            Action report = () =>
            {
                if (progress == null) return;
                long d = 0;
                bool save = false;
                lock (progLock)
                {
                    for (int i = 0; i < n; i++) d += done[i];
                    if (d == lastReport) return;
                    lastReport = d;
                    long now = DateTime.UtcNow.Ticks;
                    if (now - lastMapSave > TimeSpan.TicksPerSecond * 5)
                    {
                        lastMapSave = now;
                        save = true;
                    }
                }
                if (save) ParallelDownloadCheckpoint.SaveRangeMap(part, total, done);
                progress(d, total);
            };

            var threads = new Thread[n];
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                threads[i] = new Thread(() =>
                {
                    try
                    {
                        if (done[idx] >= needs[idx]) return;
                        DownloadExactRangeTo(part, starts[idx] + done[idx], ends[idx], total,
                            url, timeoutMs, bearer, cancelOrFail, chunk =>
                            {
                                lock (progLock) done[idx] += chunk;
                                report();
                            });
                        lock (progLock) done[idx] = needs[idx];
                        report();
                    }
                    catch (Exception ex)
                    {
                        err[idx] = ex;
                        Interlocked.Exchange(ref failFlag, 1);
                    }
                }) { IsBackground = true, Name = "dl-r" + idx };
                threads[i].Start();
            }
            for (int i = 0; i < n; i++)
                threads[i].Join();
            ParallelDownloadCheckpoint.SaveRangeMap(part, total, done);

            Exception firstErr = null;
            Exception canceledErr = null;
            for (int i = 0; i < n; i++)
            {
                if (err[i] != null)
                {
                    if (err[i] is OperationCanceledException)
                    {
                        if (canceledErr == null) canceledErr = err[i];
                    }
                    else
                    {
                        firstErr = err[i];
                        break;
                    }
                }
            }
            if (firstErr == null) firstErr = canceledErr;
            // Consolidate on the way out: keep the contiguous prefix plus the first
            // partial span, truncate the sparse tail, and record fresh metadata.
            long prefix = ParallelDownloadCheckpoint.RetainedBytes(done, needs);
            try
            {
                using (var fs = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.None))
                    if (fs.Length != prefix) fs.SetLength(prefix);
            }
            catch { }
            ParallelDownloadCheckpoint.DeleteRangeMap(part);
            if (prefix > 0)
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
                try { progress(prefix, total); } catch { }

            if (cancel != null && cancel())
                throw new OperationCanceledException("paused at " + prefix + " bytes");
            if (firstErr != null)
            {
                if (firstErr is OperationCanceledException)
                    throw new Exception("parallel worker stopped after another range failed");
                throw new Exception("parallel: " + firstErr.Message);
            }
            if (prefix != total)
                throw new Exception("parallel checkpoint " + prefix + " != " + total);
            if (new FileInfo(part).Length != total)
                throw new Exception("parallel size " + new FileInfo(part).Length + " != " + total);

            return FinishPart(part, finalPath, expectedTitleId);
        }

        static void DownloadExactRangeTo(string part, long start, long end, long expectedTotal,
            string url, int timeoutMs, string bearer, Func<bool> cancel, Action<int> onChunk)
        {
            if (cancel != null && cancel())
                throw new OperationCanceledException("paused");
            string range = "bytes=" + start + "-" + end;
            string finalUrl;
            int status;
            int tmpl, conn, req;
            OpenFollow(MethodGet, url, null, null, null, bearer, timeoutMs, range, null, null,
                out tmpl, out conn, out req, out status, out finalUrl);
            var ioDone = new ManualResetEvent(false);
            Thread abortThread = null;
            if (cancel != null)
            {
                abortThread = new Thread(() =>
                {
                    while (!ioDone.WaitOne(100))
                    {
                        bool stop = false;
                        try { stop = cancel(); } catch { }
                        if (!stop) continue;
                        try { sceHttpAbortRequest(req); } catch { }
                        return;
                    }
                }) { IsBackground = true, Name = "HTTP cancel" };
                abortThread.Start();
            }
            long need = end - start + 1;
            long got = 0;
            try
            {
                if (status != 206)
                    throw new Exception("HTTP " + status + " range " + range);
                long rs, re, total;
                bool unsatisfied;
                if (!DownloadResumeInfo.TryParseContentRange(GetHeader(req, "Content-Range"),
                        out rs, out re, out total, out unsatisfied) ||
                    unsatisfied || rs != start || re != end ||
                    (expectedTotal > 0 && total > 0 && total != expectedTotal))
                    throw new Exception("bad Content-Range for " + range);

                byte[] buf = new byte[ParallelReadBuf];
                using (var fs = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, ParallelFileBuf))
                {
                    fs.Position = start;
                    for (;;)
                    {
                        if (cancel != null && cancel())
                        {
                            try { sceHttpAbortRequest(req); } catch { }
                            throw new OperationCanceledException("paused");
                        }
                        int n = sceHttpReadData(req, buf, (uint)buf.Length);
                        if (n < 0) throw new Exception("sceHttpReadData 0x" + n.ToString("X"));
                        if (n == 0) break;
                        fs.Write(buf, 0, n);
                        got += n;
                        if (onChunk != null) onChunk(n);
                    }
                    fs.Flush();
                }
                if (got != need)
                    throw new Exception("range read " + got + " of " + need);
            }
            finally
            {
                ioDone.Set();
                if (abortThread == null || abortThread.Join(1000)) ioDone.Close();
                Close(tmpl, conn, req);
            }
        }

        static void DownloadExactRange(string url, long start, long end, long expectedTotal, string destFile,
            int timeoutMs, string bearer, Func<bool> cancel, Action<int> onChunk)
        {
            if (cancel != null && cancel())
                throw new OperationCanceledException("paused");
            string range = "bytes=" + start + "-" + end;
            string finalUrl;
            int status;
            int tmpl, conn, req;
            OpenFollow(MethodGet, url, null, null, null, bearer, timeoutMs, range, null, null,
                out tmpl, out conn, out req, out status, out finalUrl);
            var ioDone = new ManualResetEvent(false);
            Thread abortThread = null;
            if (cancel != null)
            {
                abortThread = new Thread(() =>
                {
                    while (!ioDone.WaitOne(100))
                    {
                        bool stop = false;
                        try { stop = cancel(); } catch { }
                        if (!stop) continue;
                        try { sceHttpAbortRequest(req); } catch { }
                        return;
                    }
                }) { IsBackground = true, Name = "HTTP cancel" };
                abortThread.Start();
            }
            long need = end - start + 1;
            long got = 0;
            try
            {
                if (status != 206)
                    throw new Exception("HTTP " + status + " range " + range);
                long rs, re, total;
                bool unsatisfied;
                if (!DownloadResumeInfo.TryParseContentRange(GetHeader(req, "Content-Range"),
                        out rs, out re, out total, out unsatisfied) ||
                    unsatisfied || rs != start || re != end ||
                    (expectedTotal > 0 && total > 0 && total != expectedTotal))
                    throw new Exception("bad Content-Range for " + range);

                byte[] buf = new byte[ParallelReadBuf];
                using (var fs = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None, ParallelFileBuf))
                {
                    for (;;)
                    {
                        if (cancel != null && cancel())
                        {
                            try { sceHttpAbortRequest(req); } catch { }
                            throw new OperationCanceledException("paused");
                        }
                        int n = sceHttpReadData(req, buf, (uint)buf.Length);
                        if (n < 0) throw new Exception("sceHttpReadData 0x" + n.ToString("X"));
                        if (n == 0) break;
                        fs.Write(buf, 0, n);
                        got += n;
                        if (onChunk != null) onChunk(n);
                    }
                    fs.Flush();
                }
                if (got != need)
                    throw new Exception("range read " + got + " of " + need);
            }
            finally
            {
                ioDone.Set();
                if (abortThread == null || abortThread.Join(1000)) ioDone.Close();
                Close(tmpl, conn, req);
            }
        }

        static long DownloadFileSingle(string url, string part, string finalPath, long existingBytes,
            DownloadResumeInfo resume, Action<long, long> progress, Func<bool> cancel, int timeoutMs,
            string bearer, string expectedTitleId, string expectedPackageId = null)
        {
            string range = existingBytes > 0 ? ("bytes=" + existingBytes + "-") : null;
            string finalUrl;
            int status;
            int tmpl, conn, req;
            // Never send If-Range for RD — ETags/CDN hosts change every unrestrict
            OpenFollow(MethodGet, url, null, null, null, bearer, timeoutMs, range, null, null,
                out tmpl, out conn, out req, out status, out finalUrl);
            var transferDone = new ManualResetEvent(false);
            Thread transferAbortThread = null;
            if (cancel != null)
            {
                transferAbortThread = new Thread(() =>
                {
                    while (!transferDone.WaitOne(100))
                    {
                        bool stop = false;
                        try { stop = cancel(); } catch { }
                        if (!stop) continue;
                        try { sceHttpAbortRequest(req); } catch { }
                        return;
                    }
                }) { IsBackground = true, Name = "HTTP cancel" };
                transferAbortThread.Start();
            }
            long bytesReadThis = 0;
            long expectedResponse = -1;
            long expectedFinal = -1;
            long expectedRangeSpan = -1;
            try
            {
                if (status != 200 && status != 206 && status != 416)
                    throw new Exception("HTTP " + status + " downloading");

                string etag = GetHeader(req, "ETag");
                string lastModified = GetHeader(req, "Last-Modified");
                if (status == 416)
                {
                    long start, end, total;
                    bool unsatisfied;
                    if (resume != null &&
                        DownloadResumeInfo.TryParseContentRange(GetHeader(req, "Content-Range"),
                            out start, out end, out total, out unsatisfied) &&
                        unsatisfied && total == existingBytes && total == resume.Total &&
                        resume.MatchesResponse(finalUrl, etag, lastModified))
                    {
                        bytesReadThis = 0;
                        expectedFinal = total;
                    }
                    else
                    {
                        throw new Exception(DownloadResumeInfo.RestartRequired(
                            "server rejected the saved byte offset (HTTP 416)"));
                    }
                }
                else
                {
                    bool append = status == 206 && existingBytes > 0;
                    if (status == 200 && existingBytes > 0)
                    {
                        throw new Exception(DownloadResumeInfo.RestartRequired(
                            "server ignored the Range request (HTTP 200)"));
                    }

                    if (status == 206)
                    {
                        string cr = GetHeader(req, "Content-Range");
                        long start, end, total;
                        bool unsatisfied;
                        if (!append || resume == null ||
                            !DownloadResumeInfo.TryParseContentRange(cr, out start, out end, out total, out unsatisfied) ||
                            unsatisfied || start != existingBytes || total != resume.Total ||
                            !resume.MatchesResponse(finalUrl, etag, lastModified))
                        {
                            throw new Exception(DownloadResumeInfo.RestartRequired(
                                "server returned an invalid Content-Range"));
                        }
                        expectedRangeSpan = end - start + 1;
                        expectedFinal = total;
                    }

                    int clType = 0;
                    UIntPtr cl = UIntPtr.Zero;
                    if (sceHttpGetResponseContentLength(req, out clType, out cl) >= 0 && clType == 0)
                        expectedResponse = (long)cl.ToUInt64();

                    if (expectedRangeSpan >= 0 && expectedResponse >= 0 && expectedResponse != expectedRangeSpan)
                    {
                        throw new Exception(DownloadResumeInfo.RestartRequired(
                            "Content-Range does not match Content-Length"));
                    }

                    if (expectedFinal < 0)
                    {
                        if (append && expectedResponse >= 0)
                            expectedFinal = existingBytes + expectedResponse;
                        else if (!append && expectedResponse >= 0)
                            expectedFinal = expectedResponse;
                    }

                    if (!append)
                        resume = DownloadResumeInfo.Create(url, finalUrl, etag, lastModified,
                            expectedFinal, expectedTitleId, expectedPackageId);
                    else
                        resume.Refresh(etag, lastModified, expectedFinal);
                    resume.Save(part);

                    long done = append ? existingBytes : 0;
                    long totalUi = expectedFinal > 0 ? expectedFinal : 0;
                    var mode = append ? FileMode.Append : FileMode.Create;
                    using (var fs = new FileStream(part, mode, FileAccess.Write, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
                    {
                        bytesReadThis = SequentialDownloadEngine.Copy(buffer =>
                        {
                            int n = sceHttpReadData(req, buffer, (uint)buffer.Length);
                            if (n < 0) throw new IOException("sceHttpReadData 0x" + n.ToString("X"));
                            return n;
                        }, fs, expectedResponse, done, totalUi, progress, cancel);
                    }

                    if (expectedResponse >= 0 && bytesReadThis != expectedResponse)
                        throw new Exception("truncated: read " + bytesReadThis + " of " + expectedResponse);
                    if (expectedRangeSpan >= 0 && bytesReadThis != expectedRangeSpan)
                        throw new Exception("truncated range: read " + bytesReadThis + " of " + expectedRangeSpan);

                    long finalLen = File.Exists(part) ? new FileInfo(part).Length : 0;
                    if (expectedFinal >= 0 && finalLen != expectedFinal)
                        throw new Exception("final size " + finalLen + " != expected " + expectedFinal);
                }
            }
            finally
            {
                transferDone.Set();
                if (transferAbortThread == null || transferAbortThread.Join(1000)) transferDone.Close();
                Close(tmpl, conn, req);
            }

            if (cancel != null && cancel())
                throw new OperationCanceledException("paused");

            return FinishPart(part, finalPath, expectedTitleId);
        }

        static long FinishPart(string part, string finalPath, string expectedTitleId)
        {
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
            ParallelDownloadCheckpoint.DeleteRangeMap(part);
            return new FileInfo(finalPath).Length;
        }

        static string Request(int method, string url, byte[] body, string contentType,
            string referer, string bearer, int timeoutMs, int maxBytes, out int status,
            string userAgent = null)
        {
            string finalUrl;
            int tmpl, conn, req;
            OpenFollow(method, url, body, contentType, referer, bearer, timeoutMs, null, null,
                userAgent, out tmpl, out conn, out req, out status, out finalUrl);
            try
            {
                var ms = new MemoryStream();
                byte[] buf = new byte[64 * 1024];
                for (;;)
                {
                    int n = sceHttpReadData(req, buf, (uint)buf.Length);
                    if (n < 0) throw new Exception(DescribeRequestError(req, n, "read"));
                    if (n == 0) break;
                    if (ms.Length + n > maxBytes)
                        throw new Exception("Response too large");
                    ms.Write(buf, 0, n);
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
            finally { Close(tmpl, conn, req); }
        }

        static void OpenFollow(int method, string url, byte[] body, string contentType,
            string referer, string bearer, int timeoutMs, string rangeHeader, string ifRangeHeader,
            string userAgent, out int tmpl, out int conn, out int req, out int status, out string finalUrl)
        {
            // OpenGate only covers create/config; SendRequest + status run unlocked so
            // parallel range workers overlap network RTT.
            tmpl = conn = req = -1;
            status = 0;
            finalUrl = url;
            string current = url;
            EnsureHttpsUrl(current, "request");
            string initialOrigin = OriginOf(url);
            uint recvUs = (uint)Math.Max(120000000L, (long)timeoutMs * 1000L);
            int useMethod = method;
            byte[] useBody = body;
            string useContentType = contentType;
            string ua = string.IsNullOrEmpty(userAgent) ? NetHttp.UserAgent : userAgent;

            try
            {
                for (int hop = 0; hop <= MaxRedirects; hop++)
                {
                    Close(tmpl, conn, req);
                    tmpl = conn = req = -1;

                    lock (OpenGate)
                    {
                        tmpl = sceHttpCreateTemplate(_httpCtx, ua, HttpVersion11, 0);
                        if (tmpl < 0) throw new Exception("sceHttpCreateTemplate 0x" + tmpl.ToString("X"));

                        // Leave firmware certificate checks at their secure defaults. Do not
                        // install a verification callback or disable chain/hostname checks.
                        try { sceHttpSetConnectTimeOut(tmpl, ConnectTimeoutUs); } catch { }
                        try { sceHttpSetRecvTimeOut(tmpl, recvUs); } catch { }
                        try { sceHttpSetSendTimeOut(tmpl, SendTimeoutUs); } catch { }

                        // Use the same native connection path for GET and POST. On PS4,
                        // the non-keepalive branch can lose the custom HTTPS trust context.
                        conn = sceHttpCreateConnectionWithURL(tmpl, current, 1);
                        if (conn < 0) throw new Exception("sceHttpCreateConnectionWithURL 0x" + conn.ToString("X"));

                        ulong contentLen = useBody != null ? (ulong)useBody.Length : 0UL;
                        req = sceHttpCreateRequestWithURL(conn, useMethod, current, contentLen);
                        if (req < 0) throw new Exception("sceHttpCreateRequestWithURL 0x" + req.ToString("X"));

                        // Some retail libSceHttp builds do not reliably retain the body size
                        // from CreateRequestWithURL. Set it explicitly before adding headers.
                        if (useMethod == MethodPost)
                        {
                            try
                            {
                                int lengthRc = sceHttpSetRequestContentLength(req, contentLen);
                                if (lengthRc < 0)
                                    _log.Append("post_length=0x").Append(lengthRc.ToString("X")).Append("; ");
                            }
                            catch (Exception lengthEx)
                            {
                                _log.Append("post_length_ex=").Append(lengthEx.GetType().Name).Append("; ");
                            }
                        }

                        if (!string.IsNullOrEmpty(useContentType))
                            sceHttpAddRequestHeader(req, "Content-Type", useContentType, HeaderOverwrite);
                        if (!string.IsNullOrEmpty(referer))
                            sceHttpAddRequestHeader(req, "Referer", referer, HeaderOverwrite);
                        sceHttpAddRequestHeader(req, "Accept",
                            useMethod == MethodPost ? "application/json,*/*" : "text/html,application/json,*/*",
                            HeaderOverwrite);
                        sceHttpAddRequestHeader(req, "Accept-Language", "en-US,en;q=0.8", HeaderOverwrite);
                        sceHttpAddRequestHeader(req, "Accept-Encoding", "identity", HeaderOverwrite);
                        if (!string.IsNullOrEmpty(rangeHeader))
                            sceHttpAddRequestHeader(req, "Range", rangeHeader, HeaderOverwrite);
                        if (!string.IsNullOrEmpty(ifRangeHeader))
                            sceHttpAddRequestHeader(req, "If-Range", ifRangeHeader, HeaderOverwrite);

                        if (!string.IsNullOrEmpty(bearer) &&
                            string.Equals(OriginOf(current), initialOrigin, StringComparison.OrdinalIgnoreCase))
                            sceHttpAddRequestHeader(req, "Authorization", "Bearer " + bearer.Trim(), HeaderOverwrite);
                    }

                    IntPtr postPtr = IntPtr.Zero;
                    try
                    {
                        int send;
                        if (useBody != null && useBody.Length > 0 && useMethod == MethodPost)
                        {
                            postPtr = Marshal.AllocHGlobal(useBody.Length);
                            Marshal.Copy(useBody, 0, postPtr, useBody.Length);
                            send = sceHttpSendRequest(req, postPtr, (UIntPtr)(ulong)useBody.Length);
                        }
                        else
                            send = sceHttpSendRequest(req, IntPtr.Zero, UIntPtr.Zero);
                        if (send < 0) throw new Exception(DescribeRequestError(req, send, "send"));
                    }
                    finally
                    {
                        if (postPtr != IntPtr.Zero) Marshal.FreeHGlobal(postPtr);
                    }

                    int st = 0;
                    if (sceHttpGetStatusCode(req, out st) < 0)
                        throw new Exception("sceHttpGetStatusCode failed");
                    status = st;

                    if (IsRedirectStatus(status))
                    {
                        string loc = GetHeader(req, "Location");
                        if (string.IsNullOrEmpty(loc))
                            throw new Exception("Redirect without Location");
                        string next = ResolveRedirect(current, loc);
                        EnsureHttpsUrl(next, "redirect");
                        current = next;
                        // 307/308 explicitly preserve the request method and entity.
                        // Match established browser behavior for 301/302/303.
                        if (status != 307 && status != 308)
                        {
                            useMethod = MethodGet;
                            useBody = null;
                            useContentType = null;
                        }
                        continue;
                    }

                    finalUrl = current;
                    return;
                }
                throw new Exception("Too many redirects");
            }
            catch
            {
                Close(tmpl, conn, req);
                tmpl = conn = req = -1;
                throw;
            }
        }

        static bool IsRedirectStatus(int status)
        {
            return status == 301 || status == 302 || status == 303 ||
                   status == 307 || status == 308;
        }

        static void EnsureHttpsUrl(string url, string kind)
        {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed) ||
                !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Refusing non-HTTPS " + kind + ": " + Clip(url, 160));
        }

        static string DescribeRequestError(int req, int rc, string phase)
        {
            var detail = new StringBuilder("sceHttp " + (phase ?? "request") + " 0x" + rc.ToString("X"));
            try
            {
                int sslError;
                uint verifyError;
                if (sceHttpsGetSslError(req, out sslError, out verifyError) >= 0)
                {
                    detail.Append(" ssl=0x").Append(sslError.ToString("X"));
                    if (verifyError != 0)
                        detail.Append(" verify=0x").Append(verifyError.ToString("X"));
                }
            }
            catch { }
            try
            {
                int errno;
                if (sceHttpGetLastErrno(req, out errno) >= 0 && errno != 0)
                    detail.Append(" errno=0x").Append(errno.ToString("X"));
            }
            catch { }
            detail.Append(" ca=").Append(_nativeRootCount).Append('/')
                .Append(_nativeRootAttemptCount).Append(" rdica=")
                .Append(_nativePinnedIntermediateCount).Append(" rc=0x")
                .Append(_nativeRootLoadResult.ToString("X"));

            string message = detail.ToString();
            RecordRuntimeError(message);
            return message;
        }

        static void RecordRuntimeError(string message)
        {
            lock (Gate)
            {
                _log.Append("runtime_error=").Append(message).Append("; ");
                FlushLog();
            }
        }

        static string GetHeader(int req, string name)
        {
            IntPtr hdrPtr;
            UIntPtr hdrSize;
            if (sceHttpGetAllResponseHeaders(req, out hdrPtr, out hdrSize) < 0 || hdrPtr == IntPtr.Zero)
                return null;
            int n = (int)hdrSize.ToUInt32();
            if (n <= 0 || n > 1024 * 1024) return null;
            byte[] raw = new byte[n];
            Marshal.Copy(hdrPtr, raw, 0, n);
            string block = Encoding.UTF8.GetString(raw);
            string want = name.ToLowerInvariant();
            foreach (string line in block.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int c = line.IndexOf(':');
                if (c <= 0) continue;
                if (line.Substring(0, c).Trim().ToLowerInvariant() == want)
                    return line.Substring(c + 1).Trim();
            }
            return null;
        }

        static void Close(int tmpl, int conn, int req)
        {
            lock (OpenGate)
            {
                try { if (req >= 0) sceHttpDeleteRequest(req); } catch { }
                try { if (conn >= 0) sceHttpDeleteConnection(conn); } catch { }
                try { if (tmpl >= 0) sceHttpDeleteTemplate(tmpl); } catch { }
            }
        }

        static string OriginOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int scheme = url.IndexOf("://", StringComparison.Ordinal);
            if (scheme < 0) return url;
            int slash = url.IndexOf('/', scheme + 3);
            return slash < 0 ? url : url.Substring(0, slash);
        }

        static string ResolveRedirect(string current, string location)
        {
            if (string.IsNullOrEmpty(location)) return current;
            try
            {
                // Handles absolute, scheme-relative (//host/...), and relative Location values.
                return new Uri(new Uri(current), location.Trim()).AbsoluteUri;
            }
            catch
            {
                if (location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    location.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    return location;
                if (location.StartsWith("//"))
                {
                    try
                    {
                        string scheme = new Uri(current).Scheme;
                        return scheme + ":" + location;
                    }
                    catch { return "https:" + location; }
                }
                if (location.StartsWith("/"))
                    return OriginOf(current) + location;
                int slash = current.LastIndexOf('/');
                return slash < 0 ? location : current.Substring(0, slash + 1) + location;
            }
        }

        static string Clip(string s, int n)
        {
            if (s == null) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= n ? s : s.Substring(0, n);
        }

        #region P/Invoke

        // libkernel is always loaded by eboot
        [DllImport("libkernel", EntryPoint = "sceKernelLoadStartModule", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceKernelLoadStartModule(
            [MarshalAs(UnmanagedType.LPStr)] string path,
            int argc, IntPtr argv, int flags, IntPtr pOpt, IntPtr pRes);

        [DllImport("libSceSysmodule", EntryPoint = "sceSysmoduleLoadModuleInternal", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceSysmoduleLoadModuleInternal(int moduleId);

        [DllImport("libSceNet", EntryPoint = "sceNetInit", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceNetInit();

        [DllImport("libSceNet", EntryPoint = "sceNetPoolCreate", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceNetPoolCreate([MarshalAs(UnmanagedType.LPStr)] string name, int size, int flags);

        [DllImport("libSceSsl", EntryPoint = "sceSslInit", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceSslInit(UIntPtr poolSize);

        [DllImport("libSceHttp", EntryPoint = "sceHttpInit", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpInit(int libnetMemId, int libsslCtxId, UIntPtr poolSize);

        [DllImport("libSceHttp", EntryPoint = "sceHttpCreateTemplate", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpCreateTemplate(int httpCtxId,
            [MarshalAs(UnmanagedType.LPStr)] string userAgent, int httpVer, int isAutoProxy);

        [DllImport("libSceHttp", EntryPoint = "sceHttpDeleteTemplate", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpDeleteTemplate(int templateId);

        [DllImport("libSceHttp", EntryPoint = "sceHttpCreateConnectionWithURL", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpCreateConnectionWithURL(int templateId,
            [MarshalAs(UnmanagedType.LPStr)] string url, int isEnableKeepalive);

        [DllImport("libSceHttp", EntryPoint = "sceHttpDeleteConnection", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpDeleteConnection(int connId);

        [DllImport("libSceHttp", EntryPoint = "sceHttpCreateRequestWithURL", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpCreateRequestWithURL(int connId, int method,
            [MarshalAs(UnmanagedType.LPStr)] string url, ulong contentLength);

        [DllImport("libSceHttp", EntryPoint = "sceHttpDeleteRequest", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpDeleteRequest(int reqId);

        [DllImport("libSceHttp", EntryPoint = "sceHttpAbortRequest", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpAbortRequest(int reqId);

        [DllImport("libSceHttp", EntryPoint = "sceHttpAddRequestHeader", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpAddRequestHeader(int id,
            [MarshalAs(UnmanagedType.LPStr)] string name,
            [MarshalAs(UnmanagedType.LPStr)] string value, int mode);

        [DllImport("libSceHttp", EntryPoint = "sceHttpSendRequest", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpSendRequest(int reqId, IntPtr postData, UIntPtr size);

        [DllImport("libSceHttp", EntryPoint = "sceHttpSetRequestContentLength", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpSetRequestContentLength(int reqId, ulong contentLength);

        [DllImport("libSceHttp", EntryPoint = "sceHttpsGetSslError", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpsGetSslError(int id, out int errorNumber, out uint detail);

        [DllImport("libSceHttp", EntryPoint = "sceHttpsLoadCert", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpsLoadCert(int libhttpCtxId, int caCertNum, IntPtr caList,
            IntPtr cert, IntPtr privKey);

        [DllImport("libSceHttp", EntryPoint = "sceHttpGetLastErrno", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpGetLastErrno(int reqId, out int errorNumber);

        [DllImport("libSceHttp", EntryPoint = "sceHttpGetStatusCode", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpGetStatusCode(int reqId, out int statusCode);

        [DllImport("libSceHttp", EntryPoint = "sceHttpGetResponseContentLength", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpGetResponseContentLength(int reqId, out int result, out UIntPtr contentLength);

        [DllImport("libSceHttp", EntryPoint = "sceHttpReadData", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpReadData(int reqId, byte[] data, uint size);

        [DllImport("libSceHttp", EntryPoint = "sceHttpGetAllResponseHeaders", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpGetAllResponseHeaders(int reqId, out IntPtr header, out UIntPtr headerSize);

        [DllImport("libSceHttp", EntryPoint = "sceHttpSetConnectTimeOut", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpSetConnectTimeOut(int id, uint usec);

        [DllImport("libSceHttp", EntryPoint = "sceHttpSetRecvTimeOut", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpSetRecvTimeOut(int id, uint usec);

        [DllImport("libSceHttp", EntryPoint = "sceHttpSetSendTimeOut", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceHttpSetSendTimeOut(int id, uint usec);

        #endregion
    }
}
