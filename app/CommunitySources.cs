using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Orbis
{
    internal sealed class CommunitySourceEntry
    {
        internal string Id, Name, Message, Tags, Date;
        internal long Size;
    }

    internal enum CommunityDirectoryProblem
    {
        NotConfigured, SecureNetworking, Dns, Timeout, Connection, Tls, Rejected, Refused,
        NotFound, RateLimited, Busy, Server, Http, InvalidResponse, Unexpected
    }

    /// <summary>One classified directory failure. The UI text never contains a host
    /// name or raw exception text; the log carries the full detail.</summary>
    internal sealed class CommunityDirectoryFailure
    {
        internal CommunityDirectoryProblem Problem;
        internal int HttpStatus, WaitSeconds;
        internal bool Transient;
        internal string NativeCode = "", Reason = "", Advice = "", Body = "";

        internal string Message
        {
            get
            {
                if (Problem == CommunityDirectoryProblem.NotConfigured) return "Community directory is not configured in this build.";
                if (Problem == CommunityDirectoryProblem.Rejected) return "Community directory did not accept this SSPI build (HTTP 403). Update SSPI.";
                return "Community directory unavailable (" + Reason + "). " + Suffix;
            }
        }

        internal string SavedNotice { get { return "Showing the saved list (" + Reason + "). " + Suffix; } }

        string Suffix
        {
            get
            {
                if (Advice.Length == 0) return "Triangle retries.";
                return Advice + (Problem == CommunityDirectoryProblem.Rejected ? "." : "; Triangle retries.");
            }
        }
    }

    internal sealed class CommunityDirectoryPage
    {
        internal List<CommunitySourceEntry> Entries = new List<CommunitySourceEntry>();
        internal string Next = "";
        internal int Attempts;
        // Entries are the last successfully fetched and validated copy of this page.
        internal bool Saved;
        internal CommunityDirectoryFailure Failure;
    }

    internal static class CommunitySources
    {
        internal static string Endpoint { get { return DistributionSettings.DirectoryEndpoint; } }
        const string Client = "SSPI-community-v1";
        // Include the authenticated envelope, base64 expansion and {"blob":""} wrapper.
        internal const int MaximumDownloadResponseBytes =
            ((PackageSourcePackage.MaximumCompressedBytes + 80 + 2) / 3) * 4 + 11;
        // Shared by public clients: hides raw storage, not a confidentiality boundary.
        const string Seed = "SSPI community source envelope v1 · shared directory";
        const int MaximumDirectoryResponse = 400000;
        internal static bool ValidId(string id)
        {
            if (id == null || id.Length != 64) return false;
            foreach (char c in id) if (!(c >= 'a' && c <= 'f') && !(c >= '0' && c <= '9')) return false;
            return true;
        }
        internal static string Text(Dictionary<string, object> d, string key, int max)
        {
            object o; string s = d.TryGetValue(key, out o) ? o as string : null;
            var b = new StringBuilder(); foreach (char c in s ?? "") { if (!char.IsControl(c)) b.Append(c); if (b.Length >= max) break; }
            return b.ToString();
        }
        internal static byte[] OpenEnvelope(string encoded, int maximum)
        {
            if (encoded == null || encoded.Length > (maximum + 80L) * 4 / 3 + 4) throw new InvalidDataException("Source envelope exceeds limit");
            byte[] bytes = Convert.FromBase64String(encoded);
            if (bytes.Length < 65 || bytes.Length > maximum + 80 || bytes[0] != 1 || (bytes.Length - 49) % 16 != 0)
                throw new InvalidDataException("Invalid source envelope");
            byte[] keys; using (var sha = SHA512.Create()) keys = sha.ComputeHash(Encoding.UTF8.GetBytes(Seed));
            var encryption = new byte[32]; var authentication = new byte[32]; var iv = new byte[16];
            Buffer.BlockCopy(keys, 0, encryption, 0, 32); Buffer.BlockCopy(keys, 32, authentication, 0, 32); Buffer.BlockCopy(bytes, 1, iv, 0, 16);
            byte[] tag; using (var mac = new HMACSHA256(authentication)) tag = mac.ComputeHash(bytes, 0, bytes.Length - 32);
            int difference = 0; for (int i = 0; i < 32; i++) difference |= tag[i] ^ bytes[bytes.Length - 32 + i];
            if (difference != 0) throw new InvalidDataException("Source authentication failed");
            using (var aes = new AesManaged { Key = encryption, IV = iv, Mode = CipherMode.CBC, Padding = PaddingMode.PKCS7 })
            using (var decrypt = aes.CreateDecryptor()) {
                byte[] plain = decrypt.TransformFinalBlock(bytes, 17, bytes.Length - 49);
                if (plain.Length > maximum) throw new InvalidDataException("Source exceeds limit");
                return plain;
            }
        }
        /// <summary>One directory request. The page is validated before it is returned or saved.</summary>
        internal static List<CommunitySourceEntry> List(string after, out string next, int timeoutMs = 30000, Func<bool> cancel = null)
        {
            if (!string.IsNullOrEmpty(after) && !ValidId(after)) throw new InvalidDataException("Invalid directory page");
            string response = NetHttp.GetStringDirect(Endpoint + "list?after=" + (after ?? ""), timeoutMs, null, Client,
                null, NetHttp.DefaultResponseBytes, cancel);
            var rows = ParseList(response, out next);
            Remember(after ?? "", rows, next, response);
            return rows;
        }
        static List<CommunitySourceEntry> ParseList(string response, out string next)
        {
            if (response == null || response.Length > MaximumDirectoryResponse) throw new InvalidDataException("Directory response too large");
            var root = PackageSourceJson.Parse(response) as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("Invalid directory response");
            next = Text(root, "next", 64); if (next.Length > 0 && !ValidId(next)) throw new InvalidDataException("Invalid next page");
            object value; var rows = root.TryGetValue("items", out value) ? value as IList : null;
            if (rows == null || rows.Count > 30) throw new InvalidDataException("Invalid directory entries");
            var result = new List<CommunitySourceEntry>();
            foreach (object row in rows) {
                try {
                    var d = row as Dictionary<string, object>; if (d == null) continue;
                    string id = Text(d, "id", 64); if (!ValidId(id)) continue;
                    string encoded = Text(d, "meta", 12000);
                    var metadata = PackageSourceJson.Parse(Encoding.UTF8.GetString(OpenEnvelope(encoded, 8192))) as Dictionary<string, object>;
                    if (metadata == null || Text(metadata,"id",64) != id) continue;
                    long size = Convert.ToInt64(d["size"]); if (size < 1 || size > PackageSourcePackage.MaximumCompressedBytes) continue;
                    var tags = new List<string>(); object raw; var list = metadata.TryGetValue("tags", out raw) ? raw as IList : null;
                    if (list != null) foreach (var tag in list) { if (tag is string && tags.Count < 5) tags.Add(Text(new Dictionary<string, object>{{"t",tag}},"t",24)); }
                    result.Add(new CommunitySourceEntry { Id=id, Name=Text(metadata,"name",80), Message=Text(metadata,"message",500), Tags=string.Join(" · ",tags.ToArray()), Date=Text(d,"date",10),Size=size });
                } catch { /* An invalid submission must not break the whole page. */ }
            }
            return result;
        }
        internal static byte[] Download(CommunitySourceEntry entry)
        {
            if (entry == null || !ValidId(entry.Id)) throw new InvalidDataException("Invalid source ID");
            string response = NetHttp.GetStringDirect(Endpoint + "file/" + entry.Id, 45000,
                null, Client, null, MaximumDownloadResponseBytes);
            if (response.Length > MaximumDownloadResponseBytes) throw new InvalidDataException("Source response too large");
            // This endpoint has exactly one base64 field. Keep large file data out
            // of the manifest parser, whose deliberate limit is only 512 KiB.
            if (!response.StartsWith("{\"blob\":\"", StringComparison.Ordinal) || !response.EndsWith("\"}", StringComparison.Ordinal))
                throw new InvalidDataException("Source was removed or unavailable");
            byte[] bytes = OpenEnvelope(response.Substring(9, response.Length - 11), PackageSourcePackage.MaximumCompressedBytes);
            string hash; using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            if (bytes.Length != entry.Size || hash != entry.Id) throw new InvalidDataException("Source identity mismatch");
            PackageSourcePackage.Open(bytes); // existing declarative schema/archive guards
            return bytes;
        }

        // ---- Browsing: bounded retries, classified failures and the last good page ----

        // Transient failures are retried inside the background fetch: two more attempts
        // after about 1 s and 3 s. A wait requested by the server (HTTP 429/503, or the
        // shared per-host cooldown that such a reply starts) is honoured up to
        // MaxServerWaitSeconds. Each attempt has a real deadline enforced through the
        // request's cancel hook, because the console's receive timeout is at least 120 s,
        // and the whole refresh stays inside one budget.
        internal const int MaxAttempts = 3, MaxServerWaitSeconds = 20;
        static readonly int[] RetryDelaysMs = { 1000, 3000 };
        const int MinimumAttemptMs = 5000;
        internal static int AttemptTimeoutMs = 30000, BrowseBudgetMs = 60000; // host tests shorten these

        /// <summary>Fetches one directory page on the calling (background) thread.
        /// Never throws: a failure is classified and logged, and the last good copy of
        /// the page is returned with it when one exists.</summary>
        internal static CommunityDirectoryPage Browse(string after, Action<string> progress)
        {
            after = after ?? "";
            var page = new CommunityDirectoryPage();
            var clock = Stopwatch.StartNew();
            try { if (Endpoint.Length == 0) throw new InvalidOperationException("Community directory is not configured in this build"); }
            catch (Exception ex)
            {
                page.Failure = new CommunityDirectoryFailure { Problem = CommunityDirectoryProblem.NotConfigured, Reason = "not configured in this build" };
                Log("failed", after, 0, clock.ElapsedMilliseconds, page.Failure, -1, ex, -1);
                return page;
            }
            for (int attempt = 1; ; attempt++)
            {
                page.Attempts = attempt;
                long started = clock.ElapsedMilliseconds;
                int timeout = (int)Math.Max(1000, Math.Min(AttemptTimeoutMs, BrowseBudgetMs - started));
                long deadline = started + timeout;
                try
                {
                    string next;
                    var rows = List(after, out next, timeout, () => clock.ElapsedMilliseconds >= deadline);
                    page.Entries = rows; page.Next = next;
                    Log("ok", after, attempt, clock.ElapsedMilliseconds, null, -1, null, rows.Count);
                    return page;
                }
                catch (Exception ex)
                {
                    // The only cancellation source here is the attempt deadline.
                    var failure = Classify(ex, ex is OperationCanceledException || clock.ElapsedMilliseconds >= deadline);
                    int delay = RetryDelay(failure, attempt, clock.ElapsedMilliseconds);
                    Log("failed", after, attempt, clock.ElapsedMilliseconds, failure, delay, ex, -1);
                    if (delay < 0) return Fail(page, after, failure, clock);
                    Report(progress, Capitalize(failure.Reason) + " · retrying in " + Seconds(delay) +
                        " (attempt " + (attempt + 1) + " of " + MaxAttempts + ")");
                    Thread.Sleep(delay);
                    Report(progress, "Connecting (attempt " + (attempt + 1) + " of " + MaxAttempts + ")...");
                }
            }
        }

        /// <summary>Milliseconds to wait before the next attempt, or -1 to stop.</summary>
        static int RetryDelay(CommunityDirectoryFailure failure, int attempt, long elapsedMs)
        {
            if (!failure.Transient || attempt >= MaxAttempts) return -1;
            long delay = RetryDelaysMs[Math.Min(attempt - 1, RetryDelaysMs.Length - 1)];
            if (failure.WaitSeconds > 0)
            {
                if (failure.WaitSeconds > MaxServerWaitSeconds) return -1;
                // Also outlasts the shared cooldown, which would refuse an earlier request.
                delay = Math.Max(delay, failure.WaitSeconds * 1000L + 250);
            }
            if (elapsedMs + delay + Math.Min(MinimumAttemptMs, AttemptTimeoutMs) > BrowseBudgetMs) return -1;
            return (int)delay;
        }

        static CommunityDirectoryPage Fail(CommunityDirectoryPage page, string after, CommunityDirectoryFailure failure, Stopwatch clock)
        {
            page.Failure = failure;
            SavedPage saved = SavedFor(after);
            if (saved != null)
            {
                page.Entries = new List<CommunitySourceEntry>(saved.Entries);
                page.Next = saved.Next; page.Saved = true;
                Log("saved_list", after, page.Attempts, clock.ElapsedMilliseconds, null, -1, null, saved.Entries.Count);
            }
            return page;
        }

        /// <summary>Short, host-free text for a failed source download, or null when the
        /// original message (validation, identity or format) should be shown unchanged.</summary>
        internal static string DescribeDownloadFailure(Exception error)
        {
            CommunityDirectoryFailure failure = Classify(error, false);
            try
            {
                SspiLog.Write("network", "event=community_source_install result=failed problem=" + failure.Problem +
                    " http=" + failure.HttpStatus + " native=" + (failure.NativeCode.Length > 0 ? failure.NativeCode : "none") +
                    " chain=" + Chain(error) + " exception=" + error);
            }
            catch { }
            if (failure.Problem == CommunityDirectoryProblem.InvalidResponse || failure.Problem == CommunityDirectoryProblem.Unexpected ||
                failure.Problem == CommunityDirectoryProblem.NotConfigured) return null;
            // A missing file usually means the source left the directory.
            string advice = failure.Problem == CommunityDirectoryProblem.NotFound ? "Refresh the list" : failure.Advice;
            return Capitalize(failure.Reason) + (advice.Length > 0 ? ". " + advice : "");
        }

        // ---- Failure classification ----

        // Console transport errors, as NativeHttp.DescribeRequestError formats them:
        // "sceHttp send 0x80436007 host=… ssl=0x0 verify=0x20 errno=0x…".
        static readonly Regex NativeError = new Regex(
            @"(?:sceHttp[A-Za-z]*(?: (?:send|read))?|header failed:) 0x(?<rc>[0-9A-Fa-f]{1,8})(?: host=\S*)?" +
            @"(?: ssl=0x(?<ssl>[0-9A-Fa-f]{1,8}))?(?: verify=0x(?<verify>[0-9A-Fa-f]{1,8}))?(?: errno=0x(?<errno>[0-9A-Fa-f]{1,8}))?",
            RegexOptions.CultureInvariant);

        internal static CommunityDirectoryFailure Classify(Exception error, bool timedOut)
        {
            try { return ClassifyCore(error, timedOut); }
            catch { return Problem(CommunityDirectoryProblem.Unexpected, "unexpected error", "", false); }
        }

        // A request stopped by its deadline carries no resolver code, and an unreachable
        // DNS server looks exactly like that on the console.
        const string TimeoutAdvice = "Check the PS4's network and DNS settings";

        static CommunityDirectoryFailure ClassifyCore(Exception error, bool timedOut)
        {
            ServiceHttpException service = null; WebException web = null; SocketException socket = null;
            bool invalid = false, authentication = false, timeout = false;
            var text = new StringBuilder();
            int depth = 0;
            for (Exception e = error; e != null && depth < 8; e = e.InnerException, depth++)
            {
                if (service == null) service = e as ServiceHttpException;
                if (web == null) web = e as WebException;
                if (socket == null) socket = e as SocketException;
                invalid |= e is InvalidDataException;
                authentication |= e is AuthenticationException;
                timeout |= e is TimeoutException;
                text.Append(e.Message).Append(" | ");
            }
            string message = text.ToString();
            if (service != null) return FromStatus(service.StatusCode, service.RetryAfterSeconds, service.Body);
            var http = web != null ? web.Response as HttpWebResponse : null;
            if (http != null)
            {
                int status = (int)http.StatusCode, retryAfter = 0;
                try { retryAfter = ServiceHttpException.ParseRetryAfter(http.Headers["Retry-After"], DateTime.UtcNow); } catch { }
                return FromStatus(status, retryAfter, ReadBody(http));
            }
            if (invalid) return Problem(CommunityDirectoryProblem.InvalidResponse, "invalid response", "Update SSPI if this continues", false);
            // An answer the server did send (status or data, above) outranks the deadline.
            if (timedOut) return Problem(CommunityDirectoryProblem.Timeout, "connection timed out", TimeoutAdvice, true);
            if (message.IndexOf("Response too large", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("exceeds byte limit", StringComparison.OrdinalIgnoreCase) >= 0)
                return Problem(CommunityDirectoryProblem.InvalidResponse, "response too large", "Update SSPI if this continues", false);
            if (message.IndexOf("needs native sceHttp", StringComparison.Ordinal) >= 0)
                return Problem(CommunityDirectoryProblem.SecureNetworking, "secure networking did not start", "Restart SSPI", false);
            Match native = NativeError.Match(message);
            if (native.Success) return FromNative(native);
            if (message.IndexOf("sceHttp", StringComparison.Ordinal) >= 0)
                return Problem(CommunityDirectoryProblem.Connection, "could not connect", "Check the PS4's network connection", true);
            if (socket != null) return FromSocket(socket.SocketErrorCode);
            if (web != null) return FromWebStatus(web.Status);
            if (authentication) return Problem(CommunityDirectoryProblem.Tls, "secure connection failed", "Check the PS4's date, time and DNS setting", true);
            if (timeout) return Problem(CommunityDirectoryProblem.Timeout, "connection timed out", TimeoutAdvice, true);
            if (message.IndexOf("redirect", StringComparison.OrdinalIgnoreCase) >= 0)
                return Problem(CommunityDirectoryProblem.InvalidResponse, "unexpected redirect", "Update SSPI if this continues", false);
            if (error is IOException) return Problem(CommunityDirectoryProblem.Connection, "connection interrupted", "Check the PS4's network connection", true);
            return Problem(CommunityDirectoryProblem.Unexpected, "unexpected error", "", false);
        }

        static CommunityDirectoryFailure FromStatus(int status, int retryAfter, string body)
        {
            CommunityDirectoryFailure failure;
            string code = "HTTP " + status.ToString(CultureInfo.InvariantCulture);
            if (status == 403)
            {
                // The directory itself answers with a small JSON error when it does not
                // accept the client; an HTML page comes from the network edge instead.
                string trimmed = (body ?? "").TrimStart();
                failure = trimmed.Length == 0 || trimmed[0] == '{'
                    ? Problem(CommunityDirectoryProblem.Rejected, "this SSPI build was not accepted, " + code, "Update SSPI", false)
                    : Problem(CommunityDirectoryProblem.Refused, "access refused, " + code, "Try again later or on another network", false);
            }
            else if (status == 404) failure = Problem(CommunityDirectoryProblem.NotFound, "not found, " + code, "Update SSPI", false);
            else if (status == 429 || status == 503)
            {
                // Same defaults as ProviderCooldown, which refuses new requests meanwhile.
                int wait = retryAfter > 0 ? retryAfter : status == 429 ? 60 : 15;
                failure = Problem(status == 429 ? CommunityDirectoryProblem.RateLimited : CommunityDirectoryProblem.Busy,
                    (status == 429 ? "too many requests, " : "server busy, ") + code, "Wait " + WaitText(wait), wait <= MaxServerWaitSeconds);
                failure.WaitSeconds = wait;
            }
            else if (status == 408 || status == 425) failure = Problem(CommunityDirectoryProblem.Http, "request timed out, " + code, "", true);
            else if (status >= 500 && status <= 599) failure = Problem(CommunityDirectoryProblem.Server, "server error, " + code, "", true);
            else failure = Problem(CommunityDirectoryProblem.Http, code, "", false);
            failure.HttpStatus = status;
            failure.Body = Excerpt(body, 120);
            return failure;
        }

        static CommunityDirectoryFailure FromNative(Match native)
        {
            uint rc = Hex(native.Groups["rc"]), ssl = Hex(native.Groups["ssl"]), verify = Hex(native.Groups["verify"]), errno = Hex(native.Groups["errno"]);
            string code = "0x" + rc.ToString("X8", CultureInfo.InvariantCulture);
            CommunityDirectoryFailure failure;
            if (IsDnsCode(rc) || IsDnsCode(errno))
                failure = Problem(CommunityDirectoryProblem.Dns, "DNS lookup failed, " + Codes(rc, errno, IsDnsCode), "Check the PS4's DNS setting or try Automatic", true);
            else if (IsTlsCode(rc) || IsTlsCode(errno) || ssl != 0 || verify != 0)
            {
                // sceHttps verify flags: 0x04 host name, 0x08/0x10 validity dates, 0x20 unknown issuer.
                string advice = (verify & 0x18u) != 0 ? "Check the PS4's date and time"
                    : (verify & 0x04u) != 0 ? "Check the PS4's DNS setting" : "Check the PS4's date, time and DNS setting";
                failure = Problem(CommunityDirectoryProblem.Tls, "secure connection failed, " + Codes(rc, errno, IsTlsCode) +
                    (verify != 0 ? ", verify 0x" + verify.ToString("X", CultureInfo.InvariantCulture) : ""), advice, true);
            }
            else if (IsTimeoutCode(rc) || IsTimeoutCode(errno))
                failure = Problem(CommunityDirectoryProblem.Timeout, "connection timed out, " + Codes(rc, errno, IsTimeoutCode), TimeoutAdvice, true);
            else failure = Problem(CommunityDirectoryProblem.Connection, "could not connect, " + code, "Check the PS4's network connection", true);
            failure.NativeCode = code;
            return failure;
        }

        // The request result, plus errno when errno rather than the result identifies the cause.
        static string Codes(uint rc, uint errno, Func<uint, bool> identifies)
        {
            string code = "0x" + rc.ToString("X8", CultureInfo.InvariantCulture);
            return !identifies(rc) && identifies(errno) ? code + ", errno 0x" + errno.ToString("X", CultureInfo.InvariantCulture) : code;
        }

        // libSceHttp resolver errors (0x804360xx), libSceNet resolver errors
        // (0x804101DC-0x804101EF) and the matching raw resolver errno values.
        static bool IsDnsCode(uint code)
        {
            return (code & 0xFFFFFF00u) == 0x80436000u || (code >= 0x804101DCu && code <= 0x804101EFu) || (code >= 0xDCu && code <= 0xEFu);
        }

        // libSceHttps (0x80435xxx), libSceSsl (0x8095xxxx) and SCE_HTTP_ERROR_SSL.
        static bool IsTlsCode(uint code)
        {
            return (code & 0xFFFFF000u) == 0x80435000u || (code & 0xFFFF0000u) == 0x80950000u || code == 0x80431075u;
        }

        // SCE_HTTP_ERROR_TIMEOUT and ETIMEDOUT in libSceNet and raw form.
        static bool IsTimeoutCode(uint code) { return code == 0x80431068u || code == 0x8041013Cu || code == 0x3Cu; }

        static CommunityDirectoryFailure FromSocket(SocketError error)
        {
            if (error == SocketError.HostNotFound || error == SocketError.TryAgain || error == SocketError.NoData || error == SocketError.NoRecovery)
                return Problem(CommunityDirectoryProblem.Dns, "DNS lookup failed", "Check the PS4's DNS setting or try Automatic", true);
            if (error == SocketError.TimedOut)
                return Problem(CommunityDirectoryProblem.Timeout, "connection timed out", TimeoutAdvice, true);
            return Problem(CommunityDirectoryProblem.Connection, "could not connect", "Check the PS4's network connection", true);
        }

        static CommunityDirectoryFailure FromWebStatus(WebExceptionStatus status)
        {
            if (status == WebExceptionStatus.NameResolutionFailure || status == WebExceptionStatus.ProxyNameResolutionFailure)
                return FromSocket(SocketError.HostNotFound);
            if (status == WebExceptionStatus.Timeout) return FromSocket(SocketError.TimedOut);
            if (status == WebExceptionStatus.TrustFailure || status == WebExceptionStatus.SecureChannelFailure)
                return Problem(CommunityDirectoryProblem.Tls, "secure connection failed", "Check the PS4's date, time and DNS setting", true);
            return FromSocket(SocketError.SocketError);
        }

        static CommunityDirectoryFailure Problem(CommunityDirectoryProblem problem, string reason, string advice, bool transient)
        {
            return new CommunityDirectoryFailure { Problem = problem, Reason = reason, Advice = advice ?? "", Transient = transient };
        }

        static string ReadBody(HttpWebResponse response)
        {
            try
            {
                using (response)
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                {
                    var chars = new char[1024];
                    int count = reader.ReadBlock(chars, 0, chars.Length);
                    return new string(chars, 0, count);
                }
            }
            catch { return ""; }
        }

        static uint Hex(Group group)
        {
            uint value;
            return group.Success && uint.TryParse(group.Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) ? value : 0;
        }

        static string WaitText(int seconds)
        {
            return seconds < 120 ? seconds.ToString(CultureInfo.InvariantCulture) + " s"
                : ((seconds + 59) / 60).ToString(CultureInfo.InvariantCulture) + " min";
        }

        static string Seconds(int milliseconds)
        {
            return Math.Max(1, (milliseconds + 500) / 1000).ToString(CultureInfo.InvariantCulture) + " s";
        }

        static string Capitalize(string text)
        {
            return string.IsNullOrEmpty(text) ? "" : char.ToUpperInvariant(text[0]) + text.Substring(1);
        }

        static void Report(Action<string> progress, string text)
        {
            if (progress == null) return;
            try { progress(text); } catch { }
        }

        // ---- Diagnostics ----

        static string Excerpt(string text, int max)
        {
            var result = new StringBuilder();
            foreach (char c in text ?? "") { if (result.Length >= max) break; result.Append(char.IsControl(c) || c == '"' ? ' ' : c); }
            return result.ToString().Trim();
        }

        static string Chain(Exception error)
        {
            var chain = new StringBuilder();
            int depth = 0;
            for (Exception e = error; e != null && depth < 8; e = e.InnerException, depth++)
                chain.Append(depth == 0 ? "" : " -> ").Append(e.GetType().Name).Append(": ").Append(Excerpt(e.Message, 300));
            return chain.ToString();
        }

        // The classification comes first: the native log keeps about 1,400 bytes per line.
        static void Log(string result, string after, int attempt, long elapsedMs, CommunityDirectoryFailure failure,
            int retryMs, Exception error, int entries)
        {
            try
            {
                var line = new StringBuilder("event=community_directory result=").Append(result)
                    .Append(" page=").Append(after.Length == 0 ? "first" : after.Substring(0, Math.Min(12, after.Length)))
                    .Append(" attempt=").Append(attempt).Append('/').Append(MaxAttempts)
                    .Append(" elapsed_ms=").Append(elapsedMs);
                if (entries >= 0) line.Append(" entries=").Append(entries);
                if (failure != null)
                {
                    line.Append(" problem=").Append(failure.Problem).Append(" http=").Append(failure.HttpStatus)
                        .Append(" native=").Append(failure.NativeCode.Length > 0 ? failure.NativeCode : "none")
                        .Append(" wait_s=").Append(failure.WaitSeconds)
                        .Append(retryMs >= 0 ? " retry_in_ms=" + retryMs : " retry=no");
                    if (failure.Body.Length > 0) line.Append(" body=\"").Append(failure.Body).Append('"');
                }
                if (error != null) line.Append(" chain=").Append(Chain(error)).Append(" exception=").Append(error);
                SspiLog.Write("network", line.ToString());
            }
            catch { }
        }

        // ---- Last good directory ----

        sealed class SavedPage { internal List<CommunitySourceEntry> Entries; internal string Next; }
        static readonly object SavedGate = new object();
        static readonly Dictionary<string, SavedPage> SavedPages = new Dictionary<string, SavedPage>(StringComparer.Ordinal);
        static string _savedFirstHash = "";
        const int MaximumSavedPages = 16;

        // Only the first page is kept on disk, as the exact validated server response.
        // It is parsed and authenticated again by ParseList when it is read back.
        internal static string SavedListPath { get { return Path.Combine(AppSettings.DataDir, "cache", "community-directory.json"); } }

        static void Remember(string after, List<CommunitySourceEntry> rows, string next, string response)
        {
            string hash;
            using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(response)));
            lock (SavedGate)
            {
                // An empty page is not worth showing during an outage; forget any older copy.
                if (rows.Count == 0) SavedPages.Remove(after);
                else
                {
                    if (SavedPages.Count >= MaximumSavedPages && !SavedPages.ContainsKey(after)) SavedPages.Clear();
                    SavedPages[after] = new SavedPage { Entries = new List<CommunitySourceEntry>(rows), Next = next };
                }
                if (after.Length != 0 || hash == _savedFirstHash) return;
                _savedFirstHash = hash;
            }
            try
            {
                string path = SavedListPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                AtomicFile.WriteText(path, response);
            }
            catch (Exception ex)
            {
                SspiLog.Write("network", "event=community_directory result=save_failed exception=" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static SavedPage SavedFor(string after)
        {
            lock (SavedGate)
            {
                SavedPage saved;
                if (SavedPages.TryGetValue(after, out saved)) return saved;
            }
            if (after.Length != 0) return null;
            try
            {
                var file = new FileInfo(SavedListPath);
                if (!file.Exists) return null;
                if (file.Length > MaximumDirectoryResponse * 3L) throw new InvalidDataException("Saved directory is too large");
                string response = File.ReadAllText(file.FullName, Encoding.UTF8);
                string next;
                var rows = ParseList(response, out next);
                if (rows.Count == 0) return null;
                var stored = new SavedPage { Entries = rows, Next = next };
                lock (SavedGate) if (!SavedPages.ContainsKey("")) SavedPages[""] = stored;
                return stored;
            }
            catch (Exception ex)
            {
                SspiLog.Write("network", "event=community_directory result=saved_list_rejected exception=" + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }
    }
}
