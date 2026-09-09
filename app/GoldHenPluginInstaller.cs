using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Orbis
{
    internal static class GoldHenPluginInstaller
    {
        const string Version = "5.10-r6";
        const string GoldHenRoot = "/data/GoldHEN";
        const string PluginPath = "/data/GoldHEN/plugins/gs_resident_plugin.prx";
        const string PluginLine = PluginPath + "=true";
        const string ShellPath = "/data/GameSearch/resident/gs_resident_shell_" + Version + ".prx";

        public static bool TryEnsureInstalled(out string status)
        {
            status = null;
            if (!Directory.Exists(GoldHenRoot))
            {
                status = "no-goldhen";
                return false;
            }

            string source = ResidentAppRoot() + "/resident/plugin/gs_resident_plugin.prx";
            string shellSource = ResidentAppRoot() + "/resident/plugin/gs_resident_shell.prx";
            if (!File.Exists(source) || !File.Exists(shellSource))
            {
                status = "plugin-not-bundled";
                return false;
            }

            try
            {
                string certDirectory = Path.Combine(AppSettings.DataDir, "resident", "certs");
                Directory.CreateDirectory(certDirectory);
                foreach (string name in new[] { "ca-certificates.crt", "real-debrid-geotrust-tls-rsa-ca-g1.pem" })
                {
                    string certSource = ResidentAppRoot() + "/assets/certs/" + name;
                    if (!File.Exists(certSource)) throw new FileNotFoundException("Resident certificate is missing: " + name);
                    File.Copy(certSource, Path.Combine(certDirectory, name), true);
                }
                string pluginDirectory = Path.GetDirectoryName(PluginPath);
                Directory.CreateDirectory(pluginDirectory);
                string versionPath = pluginDirectory + "/gs_resident_plugin.version";
                bool current = File.Exists(PluginPath) && File.Exists(ShellPath) && File.Exists(versionPath) &&
                    string.Equals(File.ReadAllText(versionPath).Trim(), Version + "-shell1",
                        StringComparison.Ordinal);
                if (!current)
                {
                    StagePrx(shellSource, ShellPath);
                    string stagedPlugin = PluginPath + ".new";
                    File.Copy(source, stagedPlugin, true);
                    int chmodResult = gs_resident_chmod(stagedPlugin, 493); // 0755
                    if (chmodResult != 0)
                        throw new IOException("plugin chmod failed: " + chmodResult);
                    if (sceKernelRename(stagedPlugin, PluginPath) != 0) throw new IOException("Plugin atomic replace failed");
                    WriteAtomic(versionPath, Version + "-shell1\n");
                }

                string iniPath = GoldHenRoot + "/plugins.ini";
                byte[] originalBytes = File.Exists(iniPath) ? File.ReadAllBytes(iniPath) : new byte[0];
                char[] characters = new char[originalBytes.Length];
                for (int i = 0; i < characters.Length; i++) characters[i] = (char)originalBytes[i];
                string original = new string(characters);
                string merged = MergePluginsIni(original);
                if (!string.Equals(original, merged, StringComparison.Ordinal))
                {
                    byte[] mergedBytes = new byte[merged.Length];
                    for (int i = 0; i < merged.Length; i++) mergedBytes[i] = (byte)merged[i];
                    WriteAtomicBytes(iniPath, mergedBytes);
                }
                int loadResult = gs_resident_load_shell_worker(ShellPath);
                status = loadResult >= 0 ? "shell-load-requested" :
                    loadResult == -6 ? "shell-loader-busy" : "shell-load-failed: " + loadResult;
                return true;
            }
            catch (Exception ex)
            {
                // GoldHEN was detected. Returning true prevents the dead daemon chain
                // from running if staging failed after the PRX was copied.
                status = "plugin-stage-failed: " + ex.Message;
                return true;
            }
        }

        static void StagePrx(string source, string destination)
        {
            string staged = destination + ".new";
            File.Copy(source, staged, true);
            int rc = gs_resident_chmod(staged, 493);
            if (rc != 0 || sceKernelRename(staged, destination) != 0)
                throw new IOException("Shell worker staging failed: " + rc);
        }

        [DllImport("libGameSearchResident.prx", CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_resident_load_shell_worker(string path);

        static string MergePluginsIni(string text)
        {
            text = text ?? "";
            if (text.StartsWith("\u00ef\u00bb\u00bf", StringComparison.Ordinal))
                return text.Substring(0, 3) + MergePluginsIni(text.Substring(3));
            string newline = text.IndexOf("\r\n", StringComparison.Ordinal) >= 0
                ? "\r\n" : "\n";
            List<IniLine> lines = SplitLines(text);
            bool inDefault = false;
            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Content.Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                    inDefault = string.Equals(trimmed, "[default]", StringComparison.OrdinalIgnoreCase);
                if (!inDefault) continue;
                bool enabled;
                string path;
                if (!TryPluginPath(trimmed, out path, out enabled) ||
                    !string.Equals(path, PluginPath, StringComparison.Ordinal)) continue;
                if (enabled && trimmed.EndsWith("=true", StringComparison.OrdinalIgnoreCase))
                    return text;
                string leading = lines[i].Content.Substring(0,
                    lines[i].Content.Length - lines[i].Content.TrimStart().Length);
                string trailing = lines[i].Content.Substring(
                    lines[i].Content.TrimEnd().Length);
                lines[i].Content = leading + PluginLine + trailing;
                return JoinLines(lines);
            }

            int defaultHeader = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (string.Equals(lines[i].Content.Trim(), "[default]",
                    StringComparison.OrdinalIgnoreCase))
                {
                    defaultHeader = i;
                    break;
                }
            }

            if (defaultHeader >= 0)
            {
                int insertAt = lines.Count;
                for (int i = defaultHeader + 1; i < lines.Count; i++)
                {
                    if (lines[i].Content.TrimStart().StartsWith("[",
                        StringComparison.Ordinal))
                    {
                        insertAt = i;
                        break;
                    }
                }
                EnsureLineEnding(lines, insertAt - 1, newline);
                lines.Insert(insertAt, new IniLine(PluginLine, newline));
                return JoinLines(lines);
            }

            if (lines.Count > 0)
                EnsureLineEnding(lines, lines.Count - 1, newline);
            lines.Add(new IniLine("[default]", newline));
            lines.Add(new IniLine(PluginLine, newline));
            return JoinLines(lines);
        }

        static bool TryPluginPath(string line, out string path, out bool enabled)
        {
            path = line ?? "";
            enabled = false;
            if (path.EndsWith("=true", StringComparison.OrdinalIgnoreCase))
            {
                enabled = true;
                path = path.Substring(0, path.Length - 5).TrimEnd();
            }
            else if (path.EndsWith("=false", StringComparison.OrdinalIgnoreCase))
            {
                path = path.Substring(0, path.Length - 6).TrimEnd();
            }
            return path.Length > 0;
        }

        static List<IniLine> SplitLines(string text)
        {
            var result = new List<IniLine>();
            int at = 0;
            while (at < text.Length)
            {
                int end = at;
                while (end < text.Length && text[end] != '\r' && text[end] != '\n') end++;
                string ending = "";
                if (end < text.Length)
                {
                    if (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n')
                        ending = "\r\n";
                    else
                        ending = text[end].ToString();
                }
                result.Add(new IniLine(text.Substring(at, end - at), ending));
                at = end + ending.Length;
            }
            return result;
        }

        static void EnsureLineEnding(List<IniLine> lines, int index, string newline)
        {
            if (index >= 0 && index < lines.Count && lines[index].Ending.Length == 0)
                lines[index].Ending = newline;
        }

        static string JoinLines(List<IniLine> lines)
        {
            var result = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
                result.Append(lines[i].Content).Append(lines[i].Ending);
            return result.ToString();
        }

        static void WriteAtomic(string path, string body)
        {
            WriteAtomicBytes(path, new UTF8Encoding(false).GetBytes(body));
        }

        static void WriteAtomicBytes(string path, byte[] body)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, body);
            if (sceKernelRename(tmp, path) != 0) throw new IOException("Atomic rename failed: " + path);
        }

        [DllImport("libkernel", EntryPoint = "sceKernelRename", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceKernelRename(string source, string destination);

        static string ResidentAppRoot()
        {
            try
            {
                string dir = Orbis.Internals.IO.GetAppBaseDirectory();
                if (!string.IsNullOrEmpty(dir)) return dir.TrimEnd('/', '\\');
            }
            catch { }
            return "/app0";
        }

        sealed class IniLine
        {
            public string Content;
            public string Ending;

            public IniLine(string content, string ending)
            {
                Content = content;
                Ending = ending;
            }
        }

        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_chmod",
            CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        static extern int gs_resident_chmod(string path, int mode);
    }
}
