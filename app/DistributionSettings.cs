using System;
using System.Collections.Generic;
using System.IO;

namespace Orbis
{
    // Distribution endpoints are supplied by the local build, not the source export.
    internal static class DistributionSettings
    {
        static readonly Dictionary<string, string> Values = Load();

        static Dictionary<string, string> Load()
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            using (var stream = typeof(DistributionSettings).Assembly.GetManifestResourceStream("Orbis.Distribution"))
            {
                if (stream == null) return values;
                using (var reader = new StreamReader(stream))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        int split = line.IndexOf('=');
                        if (split > 0) values[line.Substring(0, split).Trim()] = line.Substring(split + 1).Trim();
                    }
                }
            }
            return values;
        }

        static string Get(string key)
        {
            string value;
            return Values.TryGetValue(key, out value) ? value : "";
        }

        static bool IsHttps(string value)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) &&
                uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) &&
                string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
        }

        internal static string DirectoryEndpoint
        {
            get
            {
                string value = Get("directory");
                if (!IsHttps(value)) throw new InvalidOperationException("Community directory is not configured in this build");
                return value.TrimEnd('/') + "/";
            }
        }

        internal static string EmptyDirectoryMessage
        {
            get
            {
                string value = Get("submit");
                return IsHttps(value) ? "No community sources yet. Share one at " + value + "." : "No community sources yet.";
            }
        }

        internal static string ReportLabel
        {
            get { string value = Get("contact"); return value.Length > 0 ? "Report: " + value + " · ID " : "Source ID "; }
        }

        internal static bool IsPreferredHost(string host)
        {
            string preferred = Get("preferredHost").TrimEnd('.');
            return preferred.Length > 0 && !string.IsNullOrEmpty(host) &&
                (host.Equals(preferred, StringComparison.OrdinalIgnoreCase) ||
                 host.EndsWith("." + preferred, StringComparison.OrdinalIgnoreCase));
        }

        internal static string ArchivePassword(string sourcePageUrl, string supplied)
        {
            if (!string.IsNullOrEmpty(supplied)) return supplied;
            Uri page;
            if (!Uri.TryCreate(sourcePageUrl, UriKind.Absolute, out page) ||
                page.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(page.UserInfo) || !page.IsDefaultPort)
                return "";
            return Get("archivePassword." + page.DnsSafeHost.ToLowerInvariant());
        }

        /// <summary>Archive passwords configured for a source host that a bracketed tag in an
        /// uploaded file's name names ("[host] Title.part1.rar"): the values a download from that
        /// host receives, in the same order. Empty when no tag names a configured host.</summary>
        internal static string[] ArchivePasswordsForFileName(string name)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(name)) return result.ToArray();
            int tags = 0;
            for (int open = name.IndexOf('['); open >= 0 && result.Count == 0 && ++tags <= 8; open = name.IndexOf('[', open + 1))
            {
                int close = name.IndexOf(']', open + 1);
                if (close < 0) break;
                string host = name.Substring(open + 1, close - open - 1).Trim().TrimEnd('.').ToLowerInvariant();
                if (host.Length > 253 || host.IndexOf('.') <= 0 || Uri.CheckHostName(host) != UriHostNameType.Dns) continue;
                for (int i = 0; i < 4; i++)
                {
                    string value = Get("archivePassword." + host + (i == 0 ? "" : "." + (i + 1)));
                    if (value.Length > 0 && !result.Contains(value)) result.Add(value);
                }
            }
            return result.ToArray();
        }

        internal static string[] ArchivePasswordFallbacks(string sourcePageUrl, string primary, string encoded)
        {
            var values = new List<string>(ArchivePasswordDefaults.Decode(encoded));
            Uri page;
            if (Uri.TryCreate(sourcePageUrl, UriKind.Absolute, out page) && page.Scheme == Uri.UriSchemeHttps &&
                string.IsNullOrEmpty(page.UserInfo) && page.IsDefaultPort)
                for (int i = 0; i < 4; i++)
                    values.Add(Get("archivePassword." + page.DnsSafeHost.ToLowerInvariant() + (i == 0 ? "" : "." + (i + 1))));
            var result = new List<string>();
            foreach (string value in values)
                if (!string.IsNullOrEmpty(value) && value != primary && !result.Contains(value) && result.Count < 3)
                    result.Add(value);
            return result.ToArray();
        }
    }
}
