using System;
using System.IO;

namespace Orbis
{
    internal sealed class ServiceHttpException : IOException
    {
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
                return (int)Math.Min(int.MaxValue, Math.Max(0, seconds));
            DateTimeOffset date;
            if (DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out date))
                return (int)Math.Min(int.MaxValue, Math.Max(0, Math.Ceiling((date.UtcDateTime - now).TotalSeconds)));
            return 0;
        }
    }

}
