using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Orbis
{
    internal static class BgftOwnershipJournal
    {
        internal static Dictionary<int, string> Read(string text)
        {
            var result = new Dictionary<int, string>();
            var lines = (text ?? "").Replace("\r", "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var fields = lines[i].Split('\t');
                int task, subtype;
                if (fields.Length < 2 || !int.TryParse(fields[0], out task) || task < 0) continue;
                string content = fields[1];
                // Previous builds wrote content + newline + subtype. Recover only
                // an explicit adjacent subtype; never guess ownership for old rows.
                string type = fields.Length == 3 ? fields[2] :
                    (i + 1 < lines.Length && lines[i + 1].IndexOf('\t') < 0 ? lines[i + 1] : "");
                if (!int.TryParse(type, out subtype) || subtype <= 0 || string.IsNullOrWhiteSpace(content)) continue;
                result[task] = content + "\n" + subtype.ToString(CultureInfo.InvariantCulture);
            }
            return result;
        }

        internal static string Write(IDictionary<int, string> tasks)
        {
            var result = new StringBuilder();
            foreach (var item in tasks)
            {
                string identity = item.Value ?? "";
                var fields = identity.Split('\n');
                int subtype;
                if (item.Key < 0 || fields.Length != 2 || fields[0].IndexOfAny(new[] { '\t', '\r' }) >= 0 ||
                    !int.TryParse(fields[1], out subtype) || subtype <= 0) continue;
                result.Append(item.Key.ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(fields[0]).Append('\t').Append(subtype.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            return result.ToString();
        }
    }
}
