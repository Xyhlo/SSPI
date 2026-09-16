using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Orbis
{
    internal static class ArchivePasswordDefaults
    {
        static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static string Encode(IList values)
        {
            if (values == null) return "";
            if (values.Count > 4) throw new FormatException("At most four source archive passwords are supported");
            var encoded = new List<string>();
            foreach (object entry in values)
            {
                string value = entry as string;
                if (value == null || value.IndexOf('\0') >= 0 || Utf8.GetByteCount(value) > 256)
                    throw new FormatException("Invalid source archive password");
                if (value.Length == 0) continue;
                string part = Convert.ToBase64String(Utf8.GetBytes(value));
                if (!encoded.Contains(part)) encoded.Add(part);
            }
            return string.Join(",", encoded.ToArray());
        }

        internal static string[] Decode(string encoded)
        {
            if (string.IsNullOrEmpty(encoded)) return new string[0];
            if (encoded.Length > 1400) throw new FormatException("Source archive password list is too large");
            string[] parts = encoded.Split(',');
            if (parts.Length > 4) throw new FormatException("Too many source archive passwords");
            var result = new List<string>();
            foreach (string part in parts)
            {
                string value = Utf8.GetString(Convert.FromBase64String(part));
                if (value.IndexOf('\0') >= 0 || Utf8.GetByteCount(value) > 256)
                    throw new FormatException("Invalid source archive password");
                if (value.Length > 0 && !result.Contains(value)) result.Add(value);
            }
            return result.ToArray();
        }
    }
}
