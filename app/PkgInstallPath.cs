using System;
using System.Collections.Generic;

namespace Orbis
{
    internal static class PkgInstallPath
    {
        public static string[] AppInstCandidates(string path)
        {
            string normalized = Normalize(path);
            var candidates = new List<string>();
            if (normalized.StartsWith("/user/data/", StringComparison.OrdinalIgnoreCase))
            {
                Add(candidates, normalized.Substring(5));
                Add(candidates, normalized);
            }
            else if (normalized.StartsWith("/data/", StringComparison.OrdinalIgnoreCase))
            {
                // AppInstUtil resolves the global /data mount. /user/data is retained as a
                // compatibility probe for firmware/homebrew environments with that mapping.
                Add(candidates, normalized);
                Add(candidates, "/user" + normalized);
            }
            else
                Add(candidates, normalized);
            return candidates.ToArray();
        }

        public static string ForBgftStorage(string path)
        {
            string normalized = Normalize(path);
            // BGFT storage registration runs outside the app sandbox and uses /user/data.
            if (normalized.StartsWith("/data/", StringComparison.OrdinalIgnoreCase))
                return "/user" + normalized;
            return normalized;
        }

        static string Normalize(string path)
        {
            return string.IsNullOrEmpty(path) ? "" : path.Replace('\\', '/');
        }

        static void Add(List<string> values, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            foreach (string existing in values)
                if (string.Equals(existing, value, StringComparison.Ordinal)) return;
            values.Add(value);
        }
    }
}
