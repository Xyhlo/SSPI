using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal static class GoldHenPluginInstaller
    {
        const string Version = "5.11-api3";
        const string GoldHenRoot = "/data/GoldHEN";
        const string PluginPath = "/data/GoldHEN/plugins/gs_resident_plugin.prx";
        const string PluginLine = PluginPath + "=true";
        const string StaleShellPrefix = "gs_resident_shell_" + Version;
        const string StaleProbePrefix = "gs_resident_shell_probe_" + Version;
        static readonly string BuildTag = ResolveBuildTag();
        static readonly string ShellPath = "/data/SSPI/resident/gs_resident_shell_" + Version +
            (BuildTag == null ? "" : "-" + BuildTag) + ".prx";
        const string LoaderTracePath = "/data/SSPI/resident/shell-load-app.txt";
        static readonly string ProbePath = "/data/SSPI/resident/gs_resident_shell_probe_" + Version +
            (BuildTag == null ? "" : "-" + BuildTag) + ".prx";
        const string ProbeTracePath = "/data/SSPI/resident/shell-load-probe.txt";
        static int _diagnosticProbeAttempted;

        static string ResolveBuildTag()
        {
            try
            {
                const int tagLength = 12;
                string hash = BuildIdentity.SourceHash;
                if (string.IsNullOrEmpty(hash) || hash.Length < tagLength) return null;
                for (int i = 0; i < tagLength; i++)
                {
                    char c = hash[i];
                    bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                    if (!hex) return null;
                }
                return hash.Substring(0, tagLength).ToLowerInvariant();
            }
            catch { return null; }
        }

        public static bool TryEnsureInstalled(out string status)
        {
            bool repaired;
            return TryEnsureInstalled(true, out status, out repaired);
        }

        internal static bool TryEnsureInstalled(bool requestActivation, out string status, out bool repaired)
        {
            status = null;
            repaired = false;
            string operation = "validate application data directory";
            string operationPath = AppSettings.DataDir;
            try
            {
                if (!AppSettings.DataDirWritable)
                    throw new IOException(AppSettings.DataDirError ?? "SSPI data directory is not writable: " + AppSettings.DataDir);
                SspiLog.Write("resident", "plugin staging_begin activation_requested=" + requestActivation + " build=" + BuildIdentity.Label);
                string source = ResidentAppRoot() + "/resident/plugin/gs_resident_plugin.prx";
                string shellSource = ResidentAppRoot() + "/resident/plugin/gs_resident_shell.prx";
                operation = "read bundled worker"; operationPath = source;
                using (File.OpenRead(source)) { }
                operationPath = shellSource;
                using (File.OpenRead(shellSource)) { }
                if (!AppSettings.DataMigrationPending && !string.Equals(AppSettings.DataDir.TrimEnd('/', '\\'), "/data/SSPI", StringComparison.Ordinal))
                    throw new IOException("Resident coordination requires /data/SSPI; the application data directory is unavailable at that path");
                string certDirectory = "/data/SSPI/resident/certs";
                operation = "create resident certificate directory"; operationPath = certDirectory;
                Directory.CreateDirectory(certDirectory);
                foreach (string name in ResidentCertificateNames)
                {
                    string certSource = ResidentAppRoot() + "/assets/certs/" + name;
                    operation = "stage resident certificate"; operationPath = certSource;
                    if (!File.Exists(certSource)) throw new FileNotFoundException("Resident certificate is missing: " + name);
                    string destination = Path.Combine(certDirectory, name);
                    if (!FilesMatch(certSource, destination))
                    { StageFile(certSource, destination, false); repaired = true; }
                }
                MirrorResidentCertificates(certDirectory);
                string pluginDirectory = Path.GetDirectoryName(PluginPath);
                operation = "create GoldHEN plugin directory"; operationPath = pluginDirectory;
                Directory.CreateDirectory(pluginDirectory);
                string versionPath = pluginDirectory + "/gs_resident_plugin.version";
                string stagedIdentity = Version + "-shell1\nbuild=" + BuildIdentity.SourceHash;
                bool current = FilesMatch(source, PluginPath) && FilesMatch(shellSource, ShellPath) && File.Exists(versionPath) &&
                    string.Equals(File.ReadAllText(versionPath).Trim(), stagedIdentity,
                        StringComparison.Ordinal);
                if (!current)
                {
                    operation = "stage resident worker"; operationPath = ShellPath;
                    StagePrx(shellSource, ShellPath);
                    operationPath = PluginPath;
                    StagePrx(source, PluginPath);
                    operationPath = versionPath;
                    WriteAtomic(versionPath, stagedIdentity + "\n");
                    repaired = true;
                }
                RemoveStaleResidentModules();

                string iniPath = GoldHenRoot + "/plugins.ini";
                operation = "update GoldHEN plugin configuration"; operationPath = iniPath;
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
                    repaired = true;
                }
                if (ResidentDownloadService.RunningWorkerRequiresRestart)
                {
                    status = "shell-restart-required";
                    return true;
                }
                if (ResidentDownloadService.HasDownloader)
                {
                    status = "shell-already-running";
                    return true;
                }
                if (!requestActivation)
                {
                    status = "shell-activation-cooldown";
                    return true;
                }
                operation = "activate resident worker"; operationPath = ShellPath;
                TryStageSharedAlias(ShellPath, "/user" + ShellPath, "/user", StagePrx, MakeSharedDirectoryWritable);
                bool freshTrace = PrepareLoaderTrace(LoaderTracePath);
                SspiLog.Write("resident", "loader begin worker=" + ShellPath);
                int loadResult = gs_resident_load_shell_worker(ShellPath);
                status = FormatLoaderResult(loadResult, freshTrace ? ReadLoaderTrace(LoaderTracePath) : null);
                SspiLog.Write("resident", "loader result=" + loadResult + " state=" + status);
                ProbeWorkerFailure(loadResult, RunDiagnosticProbe);
                return true;
            }
            catch (Exception ex)
            {
                // A filesystem failure is not evidence that GoldHEN is disabled.
                status = DescribeFailure(operation, operationPath, ex);
                SspiLog.Write("resident", "operation=" + operation + " path=" + operationPath +
                    " exception=" + ex.GetType().FullName + " hresult=0x" + ex.HResult.ToString("X8") + " " + ex);
                return true;
            }
        }

        static void RemoveStaleResidentModules()
        {
            try
            {
                string currentShell = Path.GetFileName(ShellPath);
                string currentProbe = Path.GetFileName(ProbePath);
                foreach (string directory in new[] { "/data/SSPI/resident", "/user/data/SSPI/resident" })
                {
                    string[] files;
                    try { files = Directory.GetFiles(directory); }
                    catch { continue; }
                    foreach (string file in files)
                    {
                        string name = Path.GetFileName(file);
                        bool stale = (name.StartsWith(StaleShellPrefix, StringComparison.Ordinal) ||
                            name.StartsWith(StaleProbePrefix, StringComparison.Ordinal)) &&
                            name.EndsWith(".prx", StringComparison.Ordinal);
                        if (!stale ||
                            string.Equals(name, currentShell, StringComparison.Ordinal) ||
                            string.Equals(name, currentProbe, StringComparison.Ordinal)) continue;
                        try
                        {
                            File.Delete(file);
                            SspiLog.Write("resident", "stale module removed path=" + file);
                        }
                        catch (Exception ex)
                        {
                            SspiLog.Write("resident", "stale module removal failed path=" + file +
                                " exception=" + ex.GetType().FullName + " hresult=0x" + unchecked((uint)ex.HResult).ToString("X8") + " " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                SspiLog.Write("resident", "stale module cleanup unavailable exception=" + ex.GetType().FullName + " " + ex.Message);
            }
        }

        internal static bool ProbeWorkerFailure(int workerResult, Action probe)
        {
            if (workerResult != unchecked((int)0x80020002) ||
                Interlocked.CompareExchange(ref _diagnosticProbeAttempted, 1, 0) != 0) return false;
            try { probe(); }
            catch (Exception ex)
            {
                // This optional diagnostic must never replace the real worker result.
                SspiLog.Write("resident", "probe unavailable exception=" + ex.GetType().FullName +
                    " hresult=0x" + unchecked((uint)ex.HResult).ToString("X8") + " worker_result=0x80020002 unchanged");
            }
            return true;
        }

        static void RunDiagnosticProbe()
        {
            string source = ResidentAppRoot() + "/resident/plugin/gs_resident_probe.prx";
            bool staged = StageOptionalProbe(source, ProbePath, StagePrx);
            SspiLog.Write("resident", "probe staged=" + staged + " path=" + ProbePath);
            TryStageSharedAlias(ProbePath, "/user" + ProbePath, "/user", StagePrx, MakeSharedDirectoryWritable);
            bool freshTrace = PrepareLoaderTrace(ProbeTracePath);
            int result = gs_resident_probe_shell_worker(ProbePath);
            string state = FormatLoaderResult(result, freshTrace ? ReadLoaderTrace(ProbeTracePath) : null);
            SspiLog.Write("resident", "probe result=" + result + " state=" + state + " clue=" + DescribeProbeResult(result));
        }

        internal static bool StageOptionalProbe(string source, string destination, Action<string, string> stage)
        {
            if (FilesMatch(source, destination)) return false;
            stage(source, destination);
            return true;
        }

        internal static bool TryStageSharedAlias(string source, string destination, string storageRoot, Action<string, string> stage, Action<string> directoryCreated = null)
        {
            try
            {
                storageRoot = Path.GetFullPath(storageRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                destination = Path.GetFullPath(destination);
                if (!destination.StartsWith(storageRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new IOException("Resident shared module is outside its storage root");
                EnsureSharedDirectory(Path.GetDirectoryName(destination), storageRoot, directoryCreated);
                ValidateSharedFile(destination);
                if (FilesMatch(source, destination)) return false;
                ValidateSharedFile(destination + ".new");
                stage(source, destination);
                SspiLog.Write("resident", "shared module alias staged path=" + destination);
                return true;
            }
            catch (Exception ex)
            {
                SspiLog.Write("resident", "shared module alias unavailable path=" + destination +
                    " exception=" + ex.GetType().FullName + " hresult=0x" + unchecked((uint)ex.HResult).ToString("X8") +
                    " detail=" + ex.Message + "; canonical worker attempt unchanged");
                return false;
            }
        }

        static void MakeSharedDirectoryWritable(string directory)
        {
            if (directory != "/user/data/SSPI" && directory != "/user/data/SSPI/resident") return;
            int rc = gs_resident_chmod(directory, 511);
            if (rc != 0) throw new IOException("Resident shared module directory chmod failed: " + rc);
        }

        static void EnsureSharedDirectory(string directory, string storageRoot, Action<string> directoryCreated)
        {
            bool atRoot = string.Equals(directory, storageRoot, StringComparison.Ordinal);
            if (!atRoot) EnsureSharedDirectory(Path.GetDirectoryName(directory), storageRoot, directoryCreated);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(directory); }
            catch (FileNotFoundException) { if (atRoot) throw; Directory.CreateDirectory(directory); if (directoryCreated != null) directoryCreated(directory); attributes = File.GetAttributes(directory); }
            catch (DirectoryNotFoundException) { if (atRoot) throw; Directory.CreateDirectory(directory); if (directoryCreated != null) directoryCreated(directory); attributes = File.GetAttributes(directory); }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new IOException("Resident shared module directory is a link or file: " + directory);
        }

        static void ValidateSharedFile(string path)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("Resident shared module path is a link or directory: " + path);
        }

        internal static string DescribeProbeResult(int result)
        {
            if (result >= 0) return "Diagnostic module loaded; worker-specific imports or metadata remain to check. Worker readiness is unconfirmed";
            if (result == unchecked((int)0x80020002)) return "Diagnostic module also returned ENOENT; target path visibility remains unverified";
            if (result == -6) return "Diagnostic module skipped while another loader holds the lock";
            if (result == -12) return "Diagnostic module was already attempted in this process";
            return "Diagnostic module load failed; target path visibility and worker requirements remain unverified";
        }

        internal static bool PrepareLoaderTrace(string path)
        {
            // Early native failures may not write a trace. Never reuse the previous
            // attempt's API/module stage merely because its numeric result matches.
            try { File.Delete(path); return !File.Exists(path); }
            catch { return false; }
        }

        static string ReadLoaderTrace(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length > 4096) return null;
                return File.ReadAllText(path);
            }
            catch { return null; }
        }

        internal static string FormatLoaderResult(int loadResult, string trace)
        {
            string exact = FormatLoaderTrace(loadResult, trace);
            if (exact != null) return exact;
            return loadResult >= 0 ? "shell-load-requested" :
                    loadResult == -11 ? "shell-data-directory-unavailable" :
                    loadResult == -10 ? "shell-migration-pending" :
                    loadResult == -9 ? "shell-capabilities-unavailable" :
                    loadResult == -8 ? "shell-restart-required: a worker still owns the previous data directory" :
                    loadResult == -7 ? "shell-no-heartbeat" :
                    loadResult == -6 ? "shell-loader-busy" :
                    loadResult == -5 ? "shell-loader-lock-unavailable" :
                    "shell-load-failed: " + loadResult;
        }

        static string FormatLoaderTrace(int result, string trace)
        {
            if (result >= 0 || string.IsNullOrEmpty(trace) || trace.Length > 4096) return null;
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in trace.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                int split = line.IndexOf('=');
                if (split <= 0 || fields.Count >= 32) return null;
                string name = line.Substring(0, split);
                if (fields.ContainsKey(name)) return null;
                fields.Add(name, line.Substring(split + 1));
            }
            string version, stage, recorded, apiReturned, called, returned, rawCode;
            int recordedResult;
            if (!fields.TryGetValue("format", out version) || version != "1" ||
                !fields.TryGetValue("stage", out stage) || !fields.TryGetValue("result", out recorded) ||
                !int.TryParse(recorded, NumberStyles.Integer, CultureInfo.InvariantCulture, out recordedResult) || recordedResult != result) return null;
            if (result == -9 && stage == "worker-shared-storage-unavailable")
                return "shell-canonical-storage-unavailable";
            if (
                !fields.TryGetValue("api_returned", out apiReturned) || apiReturned != "1" ||
                !fields.TryGetValue("command6_called", out called) || !fields.TryGetValue("command6_returned", out returned)) return null;
            long code;
            string callDetail;
            if (result == -2 && stage == "api-rejected" && called == "0" && returned == "0" &&
                fields.TryGetValue("command0", out rawCode) && long.TryParse(rawCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out code) && code != 0x100 &&
                TryFormatCallTrace(fields, "command0", code, out callDetail))
                return "shell-api-rejected: " + callDetail;
            string processCalled, processValid;
            if (result == -2 && stage == "process-query-rejected" && called == "0" && returned == "0" &&
                fields.TryGetValue("command4_called", out processCalled) && processCalled == "1" &&
                fields.TryGetValue("command4_valid", out processValid) && processValid == "0" &&
                fields.TryGetValue("command4_rc", out rawCode) && long.TryParse(rawCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
                return "shell-process-query-rejected: " + FormatNativeCode(code);
            if (stage == "load-command-rejected" && called == "1" && returned == "1" &&
                fields.TryGetValue("command6_rc", out rawCode) && long.TryParse(rawCode, NumberStyles.Integer, CultureInfo.InvariantCulture, out code) &&
                TryFormatCallTrace(fields, "command6", code, out callDetail))
                return "shell-load-command-rejected: " + callDetail + (code == result ? "" : " loader_result=" + FormatNativeCode(result));
            ulong requestResult;
            if (stage == "load-result-rejected" && called == "1" && returned == "1" &&
                fields.TryGetValue("request_result", out rawCode) && rawCode.StartsWith("0x", StringComparison.Ordinal) &&
                ulong.TryParse(rawCode.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out requestResult))
                return "shell-load-result-rejected: 0x" + requestResult.ToString("X16", CultureInfo.InvariantCulture) +
                    " (" + result.ToString(CultureInfo.InvariantCulture) + ")";
            return null;
        }

        static string FormatNativeCode(long code)
        { return "0x" + unchecked((ulong)code).ToString("X16", CultureInfo.InvariantCulture) + " (" + code.ToString(CultureInfo.InvariantCulture) + ")"; }

        static bool TryFormatCallTrace(Dictionary<string, string> fields, string command, long code, out string detail)
        {
            detail = FormatNativeCode(code);
            string raw, carry;
            bool hasRaw = fields.TryGetValue(command + "_raw_hex", out raw);
            bool hasCarry = fields.TryGetValue(command + "_carry", out carry);
            if (!hasRaw && !hasCarry) return true; // Earlier format1 traces omit these fields.
            ulong bits;
            if (!hasRaw || !hasCarry || (carry != "0" && carry != "1") ||
                !raw.StartsWith("0x", StringComparison.Ordinal) ||
                !ulong.TryParse(raw.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out bits)) return false;
            detail += " raw=0x" + bits.ToString("X16", CultureInfo.InvariantCulture) + " carry=" + carry;
            return true;
        }

        internal static string DescribeFailure(string operation, string path, Exception failure)
        {
            string prefix = failure is DllNotFoundException || failure is EntryPointNotFoundException || failure is BadImageFormatException
                ? "native-binding-failed: " + failure.GetType().Name + " " : "plugin-stage-failed: ";
            return prefix + operation + " (" + path + "): " + failure.Message;
        }

        static void StagePrx(string source, string destination)
        { StageFile(source, destination, true); }

        static void StageFile(string source, string destination, bool executable)
        {
            string staged = destination + ".new";
            File.Copy(source, staged, true);
            if (!FilesMatch(source, staged)) throw new IOException("Resident staged file differs from the bundled file: " + Path.GetFileName(destination));
            int rc = executable ? gs_resident_chmod(staged, 493) : 0;
            if (rc != 0) throw new IOException("Resident file chmod failed: " + rc);
            rc = sceKernelRename(staged, destination);
            if (rc != 0) throw new IOException("Resident atomic file replace failed: " + rc);
        }

        static bool FilesMatch(string source, string destination)
        {
            if (!File.Exists(source) || !File.Exists(destination) ||
                new FileInfo(source).Length != new FileInfo(destination).Length) return false;
            using (var sha = SHA256.Create())
            using (var input = File.OpenRead(source))
            using (var installed = File.OpenRead(destination))
            {
                byte[] wanted = sha.ComputeHash(input), actual = sha.ComputeHash(installed);
                for (int i = 0; i < wanted.Length; i++) if (wanted[i] != actual[i]) return false;
                return true;
            }
        }

        static readonly string[] ResidentCertificateNames =
        {
            "ca-certificates.crt", "real-debrid-geotrust-tls-rsa-ca-g1.pem", "letsencrypt-generation-y-roots.pem",
            "letsencrypt-generation-y-intermediates.pem"
        };

        /// <summary>The shell worker loads resident certificates from its own
        /// IPC root. Mirror the staged copies there best-effort: a failure must
        /// never fail worker installation, which still succeeds under the
        /// application data directory.</summary>
        static void MirrorResidentCertificates(string certDirectory)
        {
            try
            {
                const string sharedDirectory = "/user/data/SSPI/resident/certs";
                if (string.Equals(certDirectory, sharedDirectory, StringComparison.Ordinal)) return;
                Directory.CreateDirectory(sharedDirectory);
                foreach (string name in ResidentCertificateNames)
                {
                    string source = Path.Combine(certDirectory, name);
                    if (!File.Exists(source)) continue;
                    string destination = Path.Combine(sharedDirectory, name);
                    if (FilesMatch(source, destination)) continue;
                    File.Copy(source, destination, true);
                }
            }
            catch (Exception ex)
            {
                SspiLog.Write("resident", "shared certificate mirror unavailable exception=" +
                    ex.GetType().FullName + " hresult=0x" + unchecked((uint)ex.HResult).ToString("X8") +
                    " detail=" + ex.Message);
            }
        }

        [DllImport("libGameSearchResident.prx", CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_resident_load_shell_worker(string path);

        [DllImport("libGameSearchResident.prx", CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_resident_probe_shell_worker(string path);

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
