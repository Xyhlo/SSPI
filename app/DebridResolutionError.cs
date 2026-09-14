using System;
using System.IO;
using System.Net;
using System.Text;

namespace Orbis
{
    internal sealed class DebridResolutionError : Exception
    {
        internal readonly bool CanTryMirror;
        internal readonly string Provider;
        internal readonly string Host;
        internal readonly string ProviderCode;
        internal readonly bool CanTryProvider;
        internal bool IsRateLimited;
        internal int RetryAfterSeconds;

        DebridResolutionError(string provider, string host, string message, bool canTryMirror, string code = "", bool canTryProvider = false)
            : base(provider + ": " + message + " (" + host + ")")
        { Provider = provider; Host = host; CanTryMirror = canTryMirror; ProviderCode = code; CanTryProvider = canTryProvider; }

        internal static string HostName(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) ? uri.DnsSafeHost : "unknown host";
        }

        internal static DebridResolutionError FromResponse(string provider, string url, string json)
        {
            string code = JsonLite.GetString(json, provider == "AllDebrid" || provider == "Premiumize" ? "code" : "error") ?? "";
            int number;
            int.TryParse(JsonLite.GetString(json, "error_code"), out number);
            string message = "Could not resolve this link. Retry or choose another mirror.";
            bool alternate = false;
            if (provider == "Real-Debrid")
            {
                if (number == 16 || code == "hoster_unsupported")
                { message = "This host is unsupported. Choose another mirror."; alternate = true; }
                else if (number == 17 || number == 19 || number == 24)
                { message = "This file or host is unavailable. Choose another mirror."; alternate = true; }
                else if (number == 26)
                { message = "The provider could not process this file because its upload is too large. Choose another mirror."; alternate = true; }
                else if (number == 8 || number == 12 || number == 13)
                    message = "Your token was rejected. Reconnect in Connections.";
                else if (number == 9 || number == 14 || number == 15 || number == 22)
                    message = "Account access was refused. Check your account with the provider.";
                else if (number == 20)
                    message = "The provider reports this host is unavailable with free-tier access. Verify the connected account and retry.";
                else if (number == 18 || number == 21 || number == 23 || number == 36)
                    message = "Account or host limit reached. Wait for the limit to reset.";
                else if (number == 5 || number == 34)
                    message = "Too many requests. Wait before retrying.";
                if (number != 0) message += " Code " + number + ".";
            }
            else if (provider == "AllDebrid")
            {
                if (code == "LINK_HOST_NOT_SUPPORTED" || code == "LINK_NOT_SUPPORTED" || code == "REDIRECTOR_NOT_SUPPORTED")
                { message = "This host or link is unsupported. Choose another mirror."; alternate = true; }
                else if (code == "LINK_DOWN" || code == "LINK_HOST_UNAVAILABLE" || code == "LINK_TEMPORARY_UNAVAILABLE")
                { message = "This file or host is unavailable. Choose another mirror or retry later."; alternate = true; }
                else if (code == "AUTH_BAD_APIKEY" || code == "AUTH_MISSING_APIKEY")
                    message = "Your API key was missing or rejected. Reconnect in Connections.";
                else if (code == "AUTH_BLOCKED")
                    message = "Approve this new connection in your AllDebrid email or account, then retry. Your saved key was kept.";
                else if (code == "AUTH_USER_BANNED" || code == "ACCOUNT_INVALID")
                    message = "Account access was refused. Check your account with the provider.";
                else if (code == "MUST_BE_PREMIUM" || code == "FREE_TRIAL_LIMIT_REACHED")
                    message = "This link needs premium access or exceeds the trial allowance. Check the connected plan.";
                else if (code == "LINK_HOST_LIMIT_REACHED" || code == "LINK_TOO_MANY_DOWNLOADS" || code == "LINK_HOST_FULL")
                    message = "The host limit or download slots are exhausted. Wait before retrying.";
                else if (code == "LINK_PASS_PROTECTED")
                    message = "This link is password protected. Choose another mirror.";
                else if (code == "NO_SERVER")
                    message = "The provider refused this network or VPN connection. Check its allowed-network settings.";
                else if (code == "MAINTENANCE")
                    message = "The provider is under maintenance. Retry later.";
            }
            else if (provider == "Premiumize")
            {
                if (code == "service_unsupported")
                { message = "This host is unsupported. Choose another mirror."; alternate = true; }
                else if (code == "service_down" || code == "not_found")
                { message = "This file or host is unavailable. Choose another mirror or retry later."; alternate = true; }
                else if (code == "authentication_failed")
                    message = "Your API key was missing or rejected. Reconnect in Connections.";
                else if (code == "permission_denied")
                    message = "The provider refused account access. Check the connected account and plan.";
                else if (code == "service_limit_reached" || code == "account_limit_reached")
                    message = "Your service or fair-use allowance is exhausted. Wait for the limit to reset.";
                else if (code == "rate_limit_reached")
                    message = "Too many requests. Wait before retrying.";
                else if (code == "link_generation_failed" || code == "transient_error" || code == "unknown_error")
                    message = "The provider could not prepare the link right now. Retry shortly.";
                else if (code == "invalid_request")
                    message = "The provider rejected this source URL. Choose an individual file mirror.";
            }
            else if (code == "UNSUPPORTED_SITE")
            { message = "This host is unsupported. Choose another mirror."; alternate = true; }
            else if (code == "AUTH_ERROR")
                message = "Token verification is temporarily unavailable at TorBox. Your saved key was kept; retry shortly.";
            else if (code == "BAD_TOKEN" || code == "INVALID_TOKEN")
                message = "Your API key was rejected. Reconnect in Connections.";
            else if (code == "NO_AUTH")
                message = "No API key reached the service. Reconnect TorBox in Connections.";
            else if (code == "PLAN_RESTRICTED_FEATURE" || code == "PLAN_RESTRICTED" || code == "NO_PREMIUM")
                message = "The provider reports this feature is unavailable on the connected plan. Verify the connected account.";
            else if (code == "RATE_LIMITED" || code == "TOO_MANY_REQUESTS")
                message = "Too many requests. Wait before retrying.";
            // Never include the raw provider body: it can echo a signed URL or token.
            return new DebridResolutionError(provider, HostName(url), message, alternate, code,
                !string.IsNullOrEmpty(code) || number != 0) {
                IsRateLimited = (provider == "Real-Debrid" && (number == 5 || number == 34)) ||
                    code == "rate_limit_reached" || code == "RATE_LIMITED" || code == "TOO_MANY_REQUESTS" ||
                    code == "API_RATE_LIMIT" || code == "MAINTENANCE"
            };
        }

        internal static Exception FromTransport(string provider, string url, Exception error)
        {
            if (error is OperationCanceledException) return error;
            for (int depth = 0; error != null && error.InnerException != null && depth < 8; depth++)
            {
                if (error is WebException || error is ServiceHttpException) break;
                error = error.InnerException;
            }
            string text = error != null ? error.Message ?? "" : "";
            if (text.StartsWith("Native HTTPS POST: ", StringComparison.Ordinal))
                text = text.Substring("Native HTTPS POST: ".Length);
            else if (text.StartsWith("Native HTTPS: ", StringComparison.Ordinal))
                text = text.Substring("Native HTTPS: ".Length);
            var native = error as ServiceHttpException;
            string body = native == null ? null : native.Body;
            var web = error as WebException;
            var http = web != null ? web.Response as HttpWebResponse : null;
            int httpStatus = http != null ? (int)http.StatusCode : native == null ? 0 : native.StatusCode;
            int retryAfter = native == null ? 0 : native.RetryAfterSeconds;
            if (http != null)
                try { retryAfter = ServiceHttpException.ParseRetryAfter(http.Headers["Retry-After"], DateTime.UtcNow); }
                catch (ObjectDisposedException) { }
            if (httpStatus == 0 && text.StartsWith("HTTP ", StringComparison.Ordinal) && text.Length >= 8)
                int.TryParse(text.Substring(5, 3), out httpStatus);
            if (web != null && web.Response != null)
                try
                {
                    using (var response = web.Response)
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream ?? Stream.Null, Encoding.UTF8))
                    {
                        var chars = new char[4096];
                        int count = reader.ReadBlock(chars, 0, chars.Length);
                        body = new string(chars, 0, count);
                    }
                }
                catch { }
            // The console native transport attaches a bounded JSON body to HTTP failures.
            if (body == null && error != null)
            {
                int start = text.IndexOf('{');
                if (text.StartsWith("HTTP ", StringComparison.Ordinal) && start >= 0)
                    body = text.Substring(start, Math.Min(4096, text.Length - start));
            }
            if (!string.IsNullOrEmpty(JsonLite.GetString(body, "error")) ||
                ((provider == "AllDebrid" || provider == "Premiumize") && JsonLite.GetString(body, "status") == "error"))
            {
                var rejection = FromResponse(provider, url, body);
                rejection.IsRateLimited |= httpStatus == 429 || httpStatus == 503;
                rejection.RetryAfterSeconds = retryAfter;
                return rejection;
            }
            string diagnostic = web != null ? " " + web.Status + "." : "";
            if (httpStatus != 0) diagnostic = " HTTP " + httpStatus + ".";
            if (diagnostic.Length == 0 && text.StartsWith("HTTP ", StringComparison.Ordinal))
            {
                int status;
                if (text.Length >= 8 && int.TryParse(text.Substring(5, 3), out status) && status >= 100 && status <= 599)
                    diagnostic = " HTTP " + status + ".";
            }
            const string authStage = "HTTPS authorization header failed: 0x";
            if (diagnostic.Length == 0 && text.StartsWith(authStage, StringComparison.Ordinal) && text.Length == authStage.Length + 8)
            {
                uint nativeCode;
                if (uint.TryParse(text.Substring(authStage.Length), System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out nativeCode))
                    diagnostic = " HTTPS authorization header failed: 0x" + nativeCode.ToString("X8") + ".";
            }
            return new DebridResolutionError(provider, HostName(url),
                "The service request failed." + diagnostic + " Check the connection and retry.", false) {
                RetryAfterSeconds = retryAfter,
                IsRateLimited = httpStatus == 429 || httpStatus == 503 ||
                    diagnostic.IndexOf("HTTP 429", StringComparison.Ordinal) >= 0 || diagnostic.IndexOf("HTTP 503", StringComparison.Ordinal) >= 0
            };
        }
    }
}
