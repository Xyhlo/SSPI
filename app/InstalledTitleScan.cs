using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Orbis
{
    internal static class InstalledTitleScan
    {
        public static List<GameHit> Scan(int max)
        {
            var hits = new List<GameHit>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] roots =
            {
                "/user/app",
                "/mnt/ext0/user/app",
                "/user/appmeta",
                "/mnt/ext0/user/appmeta"
            };
            if (max < 8) max = 8;
            for (int r = 0; r < roots.Length && hits.Count < max; r++)
            {
                string root = roots[r];
                try
                {
                    if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
                    string[] dirs = Directory.GetDirectories(root);
                    for (int i = 0; i < dirs.Length && hits.Count < max; i++)
                    {
                        string id = Path.GetFileName(dirs[i] ?? "");
                        if (!IsUserTitle(id) || !seen.Add(id)) continue;
                        string name, version, icon;
                        ReadMeta(id, out name, out version, out icon);
                        hits.Add(new GameHit
                        {
                            TitleId = id.ToUpperInvariant(),
                            Name = string.IsNullOrEmpty(name) ? id : name,
                            Region = "?",
                            ImageUrl = icon ?? "",
                            Source = "installed",
                            Version = version ?? ""
                        });
                    }
                }
                catch { }
            }
            return hits;
        }

        public static bool IsUserTitle(string titleId)
        {
            if (string.IsNullOrEmpty(titleId) || titleId.Length < 8) return false;
            if (string.Equals(titleId, "SRCH00001", StringComparison.OrdinalIgnoreCase)) return false;
            return titleId.StartsWith("CUSA", StringComparison.OrdinalIgnoreCase) ||
                   titleId.StartsWith("PPSA", StringComparison.OrdinalIgnoreCase);
        }

        internal static void ReadMeta(string titleId, out string name, out string version, out string icon)
        {
            name = "";
            version = "";
            icon = FirstExisting(
                "/system_data/priv/appmeta/" + titleId + "/icon0.png",
                "/user/appmeta/" + titleId + "/icon0.png",
                "/mnt/ext0/user/appmeta/" + titleId + "/icon0.png",
                "/mnt/sandbox/pfsmnt/" + titleId + "-app0/sce_sys/icon0.png");
            string[] paths = {
                "/user/patch/" + titleId + "/sce_sys/param.sfo",
                "/mnt/ext0/user/patch/" + titleId + "/sce_sys/param.sfo",
                "/system_data/priv/appmeta/" + titleId + "/param.sfo",
                "/user/appmeta/" + titleId + "/param.sfo",
                "/mnt/ext0/user/appmeta/" + titleId + "/param.sfo",
                "/user/app/" + titleId + "/sce_sys/param.sfo",
                "/mnt/ext0/user/app/" + titleId + "/sce_sys/param.sfo",
                "/mnt/sandbox/pfsmnt/" + titleId + "-app0/sce_sys/param.sfo"
            };
            ReadMetadataPaths(paths, out name, out version);
            // App metadata can lag behind a completed patch. Read the installed
            // container's small SFO entry instead of loading a multi-GB PKG.
            foreach (string root in new[] { "/user/patch/", "/mnt/ext0/user/patch/" })
            {
                try
                {
                    string pkg = root + titleId + "/patch.pkg";
                    if (!File.Exists(pkg)) continue;
                    byte[] sfo = PkgIntegrity.Entry(pkg, 0x1000);
                    string candidate = PkgIntegrity.SfoValue(sfo, "APP_VER");
                    Version current, next;
                    if (Version.TryParse(candidate, out next) && (!Version.TryParse(version, out current) || next > current)) version = candidate;
                }
                catch { }
            }
        }

        internal static void ReadMetadataPaths(string[] paths, out string name, out string version)
        {
            name = version = "";
            foreach (string path in paths)
            {
                var map = ReadSfo(path);
                if (map == null) continue;
                string value;
                if (name.Length == 0 && map.TryGetValue("TITLE", out value)) name = value.Trim();
                if (name.Length == 0 && map.TryGetValue("TITLE_01", out value)) name = value.Trim();
                if (version.Length == 0 && (map.TryGetValue("APP_VER", out value) || map.TryGetValue("VERSION", out value))) version = value.Trim();
                if (name.Length > 0 && version.Length > 0) break;
            }
        }

        static string FirstExisting(params string[] paths)
        {
            for (int i = 0; i < paths.Length; i++)
            {
                try { if (File.Exists(paths[i])) return paths[i]; }
                catch { }
            }
            return "";
        }

        static Dictionary<string, string> ReadSfo(string path)
        {
            try
            {
                byte[] data = File.ReadAllBytes(path);
                if (data.Length < 20 || data[0] != 0 || data[1] != (byte)'P' ||
                    data[2] != (byte)'S' || data[3] != (byte)'F') return null;
                int keyOff = BitConverter.ToInt32(data, 8);
                int dataOff = BitConverter.ToInt32(data, 12);
                int count = BitConverter.ToInt32(data, 16);
                if (count < 0 || count > 128 || keyOff < 0 || dataOff < 0) return null;
                var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < count; i++)
                {
                    int e = 20 + i * 16;
                    if (e + 16 > data.Length) break;
                    int k = keyOff + BitConverter.ToUInt16(data, e);
                    int fmt = BitConverter.ToUInt16(data, e + 2);
                    int len = BitConverter.ToInt32(data, e + 4);
                    int d = dataOff + BitConverter.ToInt32(data, e + 12);
                    if (k < 0 || k >= data.Length || d < 0 || len < 0 || d + len > data.Length) continue;
                    int end = k;
                    while (end < data.Length && data[end] != 0) end++;
                    string key = Encoding.UTF8.GetString(data, k, end - k);
                    if (fmt == 0x0204 || fmt == 0x0004)
                    {
                        int n = len;
                        while (n > 0 && data[d + n - 1] == 0) n--;
                        map[key] = Encoding.UTF8.GetString(data, d, n);
                    }
                }
                return map;
            }
            catch { return null; }
        }
    }
}
