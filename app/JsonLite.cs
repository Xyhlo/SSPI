using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Orbis
{
    /// <summary>Tiny JSON helpers for known provider shapes (no external deps).</summary>
    internal static class JsonLite
    {
        public static string Escape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 32) sb.AppendFormat("\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string GetString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key))
                return null;

            // "key" : "value"
            string pat = "\"" + key + "\"";
            int i = 0;
            while (true)
            {
                int k = json.IndexOf(pat, i, StringComparison.Ordinal);
                if (k < 0) return null;
                int colon = json.IndexOf(':', k + pat.Length);
                if (colon < 0) return null;
                int p = colon + 1;
                while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
                if (p >= json.Length) return null;
                if (json[p] == 'n' && json.IndexOf("null", p, StringComparison.Ordinal) == p)
                    return null;
                if (json[p] != '"')
                {
                    // number/bool
                    int e = p;
                    while (e < json.Length && ",}] \r\n\t".IndexOf(json[e]) < 0) e++;
                    return json.Substring(p, e - p).Trim();
                }
                p++;
                var sb = new StringBuilder();
                while (p < json.Length)
                {
                    char c = json[p++];
                    if (c == '\\' && p < json.Length)
                    {
                        char n = json[p++];
                        if (n == 'n') sb.Append('\n');
                        else if (n == 'r') sb.Append('\r');
                        else if (n == 't') sb.Append('\t');
                        else if (n == 'u' && p + 3 < json.Length)
                        {
                            int code;
                            if (int.TryParse(json.Substring(p, 4), NumberStyles.HexNumber, null, out code))
                                sb.Append((char)code);
                            p += 4;
                        }
                        else sb.Append(n);
                    }
                    else if (c == '"')
                        return sb.ToString();
                    else
                        sb.Append(c);
                }
                return null;
            }
        }

        public static bool GetBool(string json, string key, bool defaultValue = false)
        {
            string v = GetString(json, key);
            if (v == null) return defaultValue;
            if (v == "true" || v == "1") return true;
            if (v == "false" || v == "0") return false;
            return defaultValue;
        }

        /// <summary>Extract top-level array objects under "results" or "titles".</summary>
        public static List<string> ExtractObjectArray(string json, string arrayKey)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(json)) return list;
            string marker = "\"" + arrayKey + "\"";
            int k = json.IndexOf(marker, StringComparison.Ordinal);
            if (k < 0) return list;
            int lb = json.IndexOf('[', k);
            if (lb < 0) return list;
            int depth = 0;
            int start = -1;
            for (int i = lb + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '{')
                {
                    if (depth == 0) start = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        list.Add(json.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
                else if (c == ']' && depth == 0)
                    break;
            }
            return list;
        }
    }
}
