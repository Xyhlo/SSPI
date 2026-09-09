using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Orbis
{
    internal static class DownloadLinkParser
    {
        const int MaximumTextLength = 2 * 1024 * 1024;
        const int MaximumLinks = 100;
        static readonly Regex UrlPattern = new Regex(@"https?://[^\s<>""']+",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(250));

        public static List<string> Extract(string text)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
            if (text.Length > MaximumTextLength)
                throw new InvalidDataException("Link response exceeds 2 MiB");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in UrlPattern.Matches(text))
            {
                string value = Clean(match.Value);
                Uri uri;
                if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                    !string.IsNullOrEmpty(uri.UserInfo) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) continue;
                string path = uri.AbsolutePath ?? "";
                bool package = path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase);
                if (!package && !FreeHosterClient.IsSupportedHoster(uri)) continue;
                if (path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) continue;
                string absolute = uri.AbsoluteUri;
                if (seen.Add(absolute)) result.Add(absolute);
                if (result.Count >= MaximumLinks) break;
            }
            return result;
        }

        static string Clean(string value)
        {
            string text = (value ?? "").Replace("\\/", "/")
                .Replace("&amp;", "&").Replace("&#38;", "&")
                .Replace("\\u0026", "&");
            int end = text.IndexOfAny(new[] { '<', '>', '"', '\'', '\r', '\n', '\t', ' ' });
            if (end >= 0) text = text.Substring(0, end);
            return text.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}');
        }
    }
}
