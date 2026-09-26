using System;
using System.IO;

namespace Orbis
{
    internal sealed class ServiceHttpException : IOException
    {
        const int MaxRetryAfterSeconds = 24 * 60 * 60;
        internal readonly int StatusCode, RetryAfterSeconds;
        internal readonly string Body;
        internal ServiceHttpException(int status, string retryAfter, string body)
            : base("HTTP " + status)
        {
            StatusCode = status;
            RetryAfterSeconds = ParseRetryAfter(retryAfter, DateTime.UtcNow);
            Body = string.IsNullOrEmpty(body) ? "" : body.Substring(0, Math.Min(4096, body.Length));
        }
        internal static int ParseRetryAfter(string value, DateTime now)
        {
            long seconds;
            if (long.TryParse((value ?? "").Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out seconds))
                return (int)Math.Min(MaxRetryAfterSeconds, Math.Max(0, seconds));
            // Consoles without a set clock can report 1970. An absolute server
            // date is unusable then; let the caller apply its status-based delay.
            if (now.Year < 2020) return 0;
            DateTimeOffset date;
            if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out date))
                return (int)Math.Min(MaxRetryAfterSeconds, Math.Max(0, Math.Ceiling((date.UtcDateTime - now).TotalSeconds)));
            return 0;
        }
    }

}
