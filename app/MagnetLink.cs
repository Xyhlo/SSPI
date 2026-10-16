using System;
using System.Text;

namespace Orbis
{
    /// <summary>BitTorrent magnet links ("magnet:?xt=urn:btih:..."). The PS4 never joins a
    /// swarm: a link service fetches the torrent and SSPI downloads its files over HTTPS.</summary>
    internal static class MagnetLink
    {
        internal const int MaximumLength = 8192;

        internal static bool IsMagnet(string text)
        {
            return text != null && text.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>A well-formed magnet with one BitTorrent v1 info hash.</summary>
        internal static bool IsValid(string text)
        {
            return InfoHash(text).Length == 40;
        }

        /// <summary>The v1 info hash as 40 lowercase hex digits, or "" when the link has none.
        /// Base32 hashes (32 characters) are converted, because link services report hex.</summary>
        internal static string InfoHash(string text)
        {
            if (!IsMagnet(text) || text.Length > MaximumLength ||
                text.IndexOfAny(new[] { '\r', '\n', '\0', ' ', '\t' }) >= 0) return "";
            string found = "";
            foreach (string value in Values(text, "xt"))
            {
                if (!value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase)) continue;
                string hash = Normalize(value.Substring(9));
                if (hash.Length == 0) return "";
                if (found.Length > 0 && found != hash) return "";
                found = hash;
            }
            return found;
        }

        /// <summary>The "dn" display name, or "" when absent.</summary>
        internal static string DisplayName(string text)
        {
            if (!IsMagnet(text)) return "";
            foreach (string value in Values(text, "dn"))
            {
                var name = new StringBuilder();
                foreach (char c in value) if (!char.IsControl(c)) name.Append(c);
                string clean = name.ToString().Trim();
                if (clean.Length > 0) return clean.Length > 180 ? clean.Substring(0, 180) : clean;
            }
            return "";
        }

        static System.Collections.Generic.IEnumerable<string> Values(string text, string key)
        {
            foreach (string part in text.Substring(8).Split('&'))
            {
                int split = part.IndexOf('=');
                if (split <= 0 || !string.Equals(part.Substring(0, split), key, StringComparison.OrdinalIgnoreCase)) continue;
                string value;
                try { value = Uri.UnescapeDataString(part.Substring(split + 1).Replace('+', ' ')); }
                catch (UriFormatException) { continue; }
                yield return value;
            }
        }

        static string Normalize(string hash)
        {
            if (hash.Length == 40)
            {
                foreach (char c in hash) if (!Uri.IsHexDigit(c)) return "";
                return hash.ToLowerInvariant();
            }
            if (hash.Length != 32) return "";
            const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            var hex = new StringBuilder(40);
            int buffer = 0, bits = 0;
            foreach (char c in hash.ToUpperInvariant())
            {
                int value = Alphabet.IndexOf(c);
                if (value < 0) return "";
                buffer = (buffer << 5) | value; bits += 5;
                if (bits >= 8) { bits -= 8; hex.Append(((buffer >> bits) & 0xFF).ToString("x2")); }
            }
            return hex.Length == 40 ? hex.ToString() : "";
        }
    }
}
