using System;
using System.Text.RegularExpressions;
using System.Threading;

namespace Orbis
{
    internal static class DownloadLinkRecovery
    {
        internal const int MaximumRenewals = 3;
        // A dead link that cannot be renewed (a direct link, or a link service that
        // is unreachable because the console is offline) keeps its link and waits.
        internal const int MaximumStallWaits = 30;
        const int StallWaitMs = 30000;
        static readonly Regex HttpFailure = new Regex(
            @"\bHTTP(?:/\d(?:\.\d)?)?(?:\s+(?:status(?:\s+code)?\s*[:=]?\s*|error\s*[:=]?\s*)?|[:=]\s*)[\[(]?\s*(401|403|410)\b|\bremote server returned an error:\s*\((401|403|410)\)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        static readonly Regex BgftAuthorizationFailure = new Regex(
            @"\bAPI=sceBgftServiceDownloadGetProgress\s+rc=0x00000000\s+error=0x80991401\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static bool IsExpired(string message)
        {
            return !string.IsNullOrEmpty(message) &&
                (HttpFailure.IsMatch(message) || BgftAuthorizationFailure.IsMatch(message));
        }

        internal static bool IsExpired(Exception error)
        {
            if (error == null || error is OperationCanceledException) return false;
            var http = DownloadHttpException.Find(error);
            if (http != null) return http.StatusCode == 401 || http.StatusCode == 403 || http.StatusCode == 410;
            // Legacy resident status records still carry text; new HTTP transfers
            // take the typed branch above, including wrapped failures.
            return IsExpired(error.Message) || IsExpired(error.InnerException);
        }

        // Transfer callbacks must stop their writers before throwing. Durable bytes are
        // reread each time: preallocated file length and a prior progress sample are unsafe.
        internal static long Run(string initialUrl, Func<string, long, long> transfer,
            Func<long> durableBytes, Func<bool> canRenew, Func<string> renew,
            Func<bool> canceled, Action<int> renewing = null,
            Action<int, Func<bool>> wait = null)
        {
            if (transfer == null || durableBytes == null) throw new ArgumentNullException();
            string url = initialUrl;
            int renewals = 0, stalls = 0;
            long episodeBytes = -1;
            for (;;)
            {
                CheckCanceled(canceled);
                long existing = Math.Max(0, durableBytes());
                try
                {
                    long result = transfer(url, existing);
                    CheckCanceled(canceled);
                    return result;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    CheckCanceled(canceled);
                    bool unreachable = error is DownloadLinkUnreachableException;
                    if (!unreachable && !IsExpired(error)) throw;
                    bool renewable = renew != null && canRenew != null && canRenew();
                    long now = Math.Max(0, durableBytes());
                    if (episodeBytes < 0 || now > episodeBytes) { episodeBytes = now; renewals = 0; stalls = 0; }
                    if (unreachable && !renewable)
                    {
                        if (++stalls > MaximumStallWaits) throw;
                        (wait ?? Wait)(StallWaitMs, canceled);
                        continue;
                    }
                    if (!renewable || renewals >= MaximumRenewals) throw;
                    if (renewing != null) renewing(renewals + 1);
                    if (renewals > 0) (wait ?? Wait)(1000 << (renewals - 1), canceled);
                    CheckCanceled(canceled);
                    string fresh;
                    try { fresh = renew(); }
                    catch (Exception renewFailure) when (unreachable && !(renewFailure is OperationCanceledException))
                    {
                        // The link service is unreachable too (usually the console is
                        // offline): keep the current link and wait for the network.
                        if (++stalls > MaximumStallWaits) throw;
                        (wait ?? Wait)(StallWaitMs, canceled);
                        continue;
                    }
                    CheckCanceled(canceled);
                    if (string.IsNullOrWhiteSpace(fresh))
                        throw new InvalidOperationException("Link service returned an empty download link");
                    url = fresh;
                    renewals++;
                }
            }
        }

        static void CheckCanceled(Func<bool> canceled)
        {
            if (canceled != null && canceled()) throw new OperationCanceledException();
        }

        static void Wait(int milliseconds, Func<bool> canceled)
        {
            while (milliseconds > 0)
            {
                CheckCanceled(canceled);
                int step = Math.Min(100, milliseconds);
                Thread.Sleep(step);
                milliseconds -= step;
            }
        }
    }
}
