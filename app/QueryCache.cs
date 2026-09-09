using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Orbis
{
    internal static class QueryCache
    {
        const int MaxAgeHours = 6;

        public static bool TrySearch(string query, out List<GameHit> hits, string sourceStamp = "")
        {
            hits = null;
            string body;
            if (!TryRead("s-" + Key(sourceStamp + "|" + query), out body)) return false;
            hits = new List<GameHit>();
            using (var reader = new StringReader(body))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] p = Split(line);
                    if (p.Length < 5) continue;
                    hits.Add(new GameHit
                    {
                        TitleId = p[0], Name = p[1], Region = p[2], ImageUrl = p[3], Source = p[4],
                        Rating = p.Length > 5 ? p[5] : "",
                        Genres = p.Length > 6 ? p[6] : "",
                        Backport = p.Length > 7 ? p[7] : ""
                    });
                }
            }
            return hits.Count > 0;
        }

        public static void PutSearch(string query, List<GameHit> hits, string sourceStamp = "")
        {
            if (hits == null) return;
            var sb = new StringBuilder();
            for (int i = 0; i < hits.Count; i++)
            {
                GameHit h = hits[i];
                if (h == null) continue;
                sb.Append(Esc(h.TitleId)).Append('\t').Append(Esc(h.Name)).Append('\t')
                    .Append(Esc(h.Region)).Append('\t').Append(Esc(h.ImageUrl)).Append('\t')
                    .Append(Esc(h.Source)).Append('\t').Append(Esc(h.Rating)).Append('\t')
                    .Append(Esc(h.Genres)).Append('\t').Append(Esc(h.Backport)).Append('\n');
            }
            Write("s-" + Key(sourceStamp + "|" + query), sb.ToString());
        }

        public static bool TryResolve(string titleId, out List<PackageCandidate> candidates, string sourceStamp = "")
        {
            candidates = null;
            string body;
            if (!TryRead("r-" + Key(sourceStamp + "|" + titleId), out body)) return false;
            candidates = new List<PackageCandidate>();
            using (var reader = new StringReader(body))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] p = Split(line);
                    if (p.Length < 20) continue; // Older cached candidates lost integrity and volume metadata.
                    long size;
                    var cand = new PackageCandidate
                    {
                        SourceId = p[0],
                        CandidateId = p[1],
                        TitleId = p[2],
                        DisplayName = p[3],
                        Region = p[4],
                        PackageKindHint = p[5],
                        PackageVersion = p[6],
                        PackageGroupId = p[7],
                        HosterName = p[8],
                        Label = p[9],
                        SourceAttribution = p[10],
                        Url = p[11],
                        AccessType = ParseAccess(p[12]), SourceVersion = p[14],
                        ExpectedSha256 = p[15], ExpectedContentId = p[16], SourcePageUrl = p[17],
                        ArchiveVolumes = p[18], RequiredFirmware = p.Length > 20 ? p[20] : "",
                        ArchivePassword = p.Length > 21 ? p[21] : ""
                    };
                    if (long.TryParse(p[13], NumberStyles.Integer, CultureInfo.InvariantCulture, out size) && size > 0)
                        cand.ExpectedByteSize = size;
                    DateTime expires;
                    if (DateTime.TryParse(p[19], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expires))
                    {
                        cand.ExpiresUtc = expires.ToUniversalTime();
                        if (cand.ExpiresUtc <= DateTime.UtcNow) continue;
                    }
                    candidates.Add(cand);
                }
            }
            return candidates.Count > 0;
        }

        public static void PutResolve(string titleId, List<PackageCandidate> candidates, string sourceStamp = "")
        {
            if (candidates == null) return;
            var sb = new StringBuilder();
            for (int i = 0; i < candidates.Count; i++)
            {
                PackageCandidate c = candidates[i];
                if (c == null) continue;
                sb.Append(Esc(c.SourceId)).Append('\t').Append(Esc(c.CandidateId)).Append('\t')
                    .Append(Esc(c.TitleId)).Append('\t').Append(Esc(c.DisplayName)).Append('\t')
                    .Append(Esc(c.Region)).Append('\t').Append(Esc(c.PackageKindHint)).Append('\t')
                    .Append(Esc(c.PackageVersion)).Append('\t').Append(Esc(c.PackageGroupId)).Append('\t')
                    .Append(Esc(c.HosterName)).Append('\t').Append(Esc(c.Label)).Append('\t')
                    .Append(Esc(c.SourceAttribution)).Append('\t').Append(Esc(c.Url)).Append('\t')
                    .Append(((int)c.AccessType).ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append((c.ExpectedByteSize ?? 0).ToString(CultureInfo.InvariantCulture)).Append('\t')
                    .Append(Esc(c.SourceVersion)).Append('\t').Append(Esc(c.ExpectedSha256)).Append('\t')
                    .Append(Esc(c.ExpectedContentId)).Append('\t').Append(Esc(c.SourcePageUrl)).Append('\t')
                    .Append(Esc(c.ArchiveVolumes)).Append('\t')
                    .Append(c.ExpiresUtc.HasValue ? c.ExpiresUtc.Value.ToUniversalTime().ToString("o") : "").Append('\t')
                    .Append(Esc(c.RequiredFirmware)).Append('\t').Append(Esc(c.ArchivePassword)).Append('\n');
            }
            Write("r-" + Key(sourceStamp + "|" + titleId), sb.ToString());
        }

        internal static bool TryUpdate(string titleId, string version, out bool update, out string info)
        { return TryUpdateKey(UpdateKey(titleId, version), out update, out info); }

        internal static string UpdateKey(string titleId, string version)
        { return "u-" + Key(titleId + "|" + version); }

        internal static bool TryUpdateKey(string key, out bool update, out string info)
        {
            update = false; info = "";
            try {
                string path = Path.Combine(CacheDir(), key + ".txt");
                if (!File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromHours(6)) return false;
                if (new FileInfo(path).Length > 16384) return false;
                string[] text = File.ReadAllText(path).Split('\n');
                if (text.Length != 2 || (text[0] != "0" && text[0] != "1")) return false;
                update = text[0] == "1"; info = Encoding.UTF8.GetString(Convert.FromBase64String(text[1])); return true;
            } catch { return false; }
        }
        internal static void PutUpdate(string titleId, string version, bool update, string info)
        { PutUpdateKey(UpdateKey(titleId, version), update, info); }

        internal static void PutUpdateKey(string key, bool update, string info)
        { Write(key, (update ? "1" : "0") + "\n" + Convert.ToBase64String(Encoding.UTF8.GetBytes(info ?? ""))); }

        static PackageAccessType ParseAccess(string value)
        {
            int n;
            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) &&
                n == (int)PackageAccessType.Direct) return PackageAccessType.Direct;
            if (n == (int)PackageAccessType.HosterLanding) return PackageAccessType.HosterLanding;
            return PackageAccessType.Unknown;
        }

        static bool TryRead(string name, out string body)
        {
            body = null;
            try
            {
                string path = Path.Combine(CacheDir(), name + ".txt");
                if (!File.Exists(path)) return false;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > TimeSpan.FromHours(MaxAgeHours))
                {
                    try { File.Delete(path); } catch { }
                    return false;
                }
                if (new FileInfo(path).Length > 4 * 1024 * 1024) return false;
                body = File.ReadAllText(path);
                return !string.IsNullOrEmpty(body);
            }
            catch { return false; }
        }

        static void Write(string name, string body)
        {
            try
            {
                string dir = CacheDir();
                lock (typeof(QueryCache))
                {
                    string path = Path.Combine(dir, name + ".txt");
                    AtomicFile.WriteText(path, body);
                    var files = new DirectoryInfo(dir).GetFiles("*.txt");
                    if (files.Length > 256) { Array.Sort(files, (a,b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc)); for (int i = 0; i < files.Length - 256; i++) files[i].Delete(); }
                }
            }
            catch { }
        }

        static string CacheDir()
        {
            string d = Path.Combine(AppSettings.DataDir, "query-cache");
            try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
            return d;
        }

        static string Key(string value)
        {
            string registry = "";
            try { registry = File.ReadAllText(Path.Combine(AppSettings.DataDir, "sources/registry.json")); } catch { }
            using (var sha = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes("v5|" + registry + "|" + (value ?? "").Trim().ToLowerInvariant()))).Replace("-", "").ToLowerInvariant();
        }

        static string Esc(string value)
        {
            return (value ?? "").Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\n", "\\n");
        }

        static string[] Split(string line)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\\' && i + 1 < line.Length)
                {
                    char n = line[++i];
                    if (n == 't') sb.Append('\t');
                    else if (n == 'n') sb.Append('\n');
                    else sb.Append(n);
                }
                else if (c == '\t')
                {
                    parts.Add(sb.ToString());
                    sb.Length = 0;
                }
                else sb.Append(c);
            }
            parts.Add(sb.ToString());
            return parts.ToArray();
        }
    }
}
