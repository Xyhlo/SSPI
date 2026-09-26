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
                    path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".7zip", StringComparison.OrdinalIgnoreCase);
                if (!package && !FreeHosterClient.IsSupportedHoster(uri)) continue;
                if (path.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)) continue;
                string absolute = uri.AbsoluteUri;
                if (seen.Add(absolute)) result.Add(absolute);
                if (result.Count >= MaximumLinks) break;
            }
            return result;
        }

        /// <summary>
        /// Derive a CUSA title ID and display title from a personal file name or link,
        /// e.g. "Game Name-CUSA01234.pkg". Artwork only needs the title ID; the PKG
        /// header still decides the package identity during validation.
        /// </summary>
        internal static bool TryPackageIdentity(string name, string link, out string titleId, out string title)
        {
            titleId = title = "";
            foreach (string candidate in new[] { name, FileNameFromLink(link) })
            {
                string file = candidate ?? "";
                int slash = Math.Max(file.LastIndexOf('/'), file.LastIndexOf('\\'));
                if (slash >= 0) file = file.Substring(slash + 1);
                int at = FindTitleId(file);
                if (at < 0) continue;
                titleId = file.Substring(at, 9).ToUpperInvariant();
                title = file.Substring(0, at).Trim(' ', '-', '_', '.', '[', '(', '\t');
                if (title.Length == 0)
                {
                    string rest = file.Substring(at + 9);
                    int dot = rest.LastIndexOf('.');
                    if (dot >= 0 && rest.Length - dot <= 6) rest = rest.Substring(0, dot);
                    title = rest.Trim(' ', '-', '_', '.', ']', ')', '\t');
                }
                return true;
            }
            return false;
        }

        static int FindTitleId(string text)
        {
            for (int i = 0; i + 9 <= text.Length; i++)
            {
                if (string.Compare(text, i, "CUSA", 0, 4, StringComparison.OrdinalIgnoreCase) != 0) continue;
                if (i > 0 && char.IsLetterOrDigit(text[i - 1])) continue;
                bool digits = true;
                for (int j = i + 4; j < i + 9; j++) if (text[j] < '0' || text[j] > '9') { digits = false; break; }
                if (digits && (i + 9 == text.Length || !char.IsDigit(text[i + 9]))) return i;
            }
            return -1;
        }

        static string FileNameFromLink(string link)
        {
            string value = link ?? "";
            try
            {
                if (value.StartsWith("rd-link/", StringComparison.Ordinal) ||
                    value.StartsWith("rd-direct/", StringComparison.Ordinal) ||
                    value.StartsWith("ad-link/", StringComparison.Ordinal))
                    value = Uri.UnescapeDataString(value.Substring(value.IndexOf('/') + 1));
                Uri uri;
                if (!Uri.TryCreate(value, UriKind.Absolute, out uri)) return "";
                string path = uri.AbsolutePath ?? "";
                return Uri.UnescapeDataString(path.Substring(path.LastIndexOf('/') + 1));
            }
            catch { return ""; }
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
