using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Orbis
{
    internal sealed partial class PairServer
    {
        // Logs for the phone: SSPI's own logs, other payloads' logs under /data and /user/data, crash reports and a
        // kernel log snapshot. Files are named by an opaque ID from the latest scan, so a request never names a path.
        internal static string[] LogRoots = { "/data", "/user/data" };
        internal static Func<byte[]> KernelLogSource;
        const int MaxLogFiles = 200, MaxScanEntries = 4000, MaxScanDepth = 3;
        const int LogTailBytes = 768 * 1024, MaxRawBytes = 16 * 1024 * 1024, MaxKernelBytes = 512 * 1024;
        static readonly string[] SkippedFolders = { "pkg", "covers", "cache", ".cache", "thumbnails", "artwork", "sources", "tmp", "temp" };

        internal sealed class LogFile
        {
            public string Id, Group, Name, Path, Kind;
            public long Size, Modified;
            public bool Text;
        }

        LogFile[] _logFiles = new LogFile[0];
        DateTime _logsReadAt;

        bool HandleLogs(NetworkStream stream, string method, string path, string prefix)
        {
            if (method != "GET" || !path.StartsWith(prefix + "/logs", StringComparison.Ordinal)) return false;
            string rest = path.Substring(prefix.Length + 5);
            if (rest == "" || rest == "/") {
                if (DateTime.UtcNow - _logsReadAt > TimeSpan.FromSeconds(10)) { _logFiles = ScanLogs(); _logsReadAt = DateTime.UtcNow; }
                WriteResponse(stream, 200, "application/json", LogListJson(_logFiles)); return true;
            }
            if (rest == "/kernel") { WriteResponse(stream, 200, "application/json", KernelLogJson()); return true; }
            bool raw = rest.StartsWith("/raw/", StringComparison.Ordinal);
            if (!raw && !rest.StartsWith("/file/", StringComparison.Ordinal)) return false;
            LogFile file = FindLog(rest.Substring(raw ? 5 : 6));
            if (file == null) { WriteResponse(stream, 404, "text/plain", "That log is no longer on your PS4. Refresh Logs and try again."); return true; }
            try {
                if (raw) {
                    long size = new FileInfo(file.Path).Length;
                    if (size > MaxRawBytes) { WriteResponse(stream, 400, "text/plain", "This file is too large to save from the phone. It is on your PS4 at " + file.Path + "."); return true; }
                    WriteResponseBytes(stream, 200, "application/octet-stream", ReadShared(file.Path, 0, (int)size));
                    return true;
                }
                long length; bool cut;
                string text = ReadLogTail(file.Path, LogTailBytes, out length, out cut);
                WriteResponse(stream, 200, "application/json", "{\"id\":\"" + file.Id + "\",\"name\":\"" + JsonLite.Escape(file.Name) +
                    "\",\"group\":\"" + JsonLite.Escape(file.Group) + "\",\"path\":\"" + JsonLite.Escape(file.Path) + "\",\"size\":" + length +
                    ",\"truncated\":" + (cut ? "true" : "false") + ",\"text\":\"" + JsonLite.Escape(text) + "\"}");
            } catch (Exception e) {
                WriteResponse(stream, 400, "text/plain", "Your PS4 could not read " + file.Name + ": " + e.Message);
            }
            return true;
        }

        LogFile FindLog(string id)
        {
            foreach (var pass in new[] { false, true }) {
                if (pass) { _logFiles = ScanLogs(); _logsReadAt = DateTime.UtcNow; }
                foreach (var file in _logFiles) if (file.Id == id) return file;
            }
            return null;
        }

        static string LogListJson(LogFile[] files)
        {
            var json = new StringBuilder("{\"build\":\"").Append(JsonLite.Escape(BuildIdentity.Label)).Append("\",\"firmware\":\"")
                .Append(JsonLite.Escape(FirmwareInfo.Probe())).Append("\",\"files\":[");
            for (int i = 0; i < files.Length; i++) {
                var f = files[i];
                if (i > 0) json.Append(',');
                json.Append("{\"id\":\"").Append(f.Id).Append("\",\"group\":\"").Append(JsonLite.Escape(f.Group))
                    .Append("\",\"name\":\"").Append(JsonLite.Escape(f.Name)).Append("\",\"path\":\"").Append(JsonLite.Escape(f.Path))
                    .Append("\",\"kind\":\"").Append(f.Kind).Append("\",\"size\":").Append(f.Size).Append(",\"modified\":").Append(f.Modified)
                    .Append(",\"text\":").Append(f.Text ? "true" : "false").Append('}');
            }
            return json.Append("]}").ToString();
        }

        internal static LogFile[] ScanLogs()
        {
            var found = new List<LogFile>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string sspi = SspiLog.Root;
            try {
                if (Directory.Exists(sspi))
                    foreach (string file in Directory.GetFiles(sspi)) Consider(found, seen, file, "SSPI", false);
            } catch { }
            int budget = MaxScanEntries;
            foreach (string root in LogRoots) {
                try {
                    if (!Directory.Exists(root)) continue;
                    foreach (string file in Directory.GetFiles(root)) {
                        if (--budget < 0) break;
                        Consider(found, seen, file, System.IO.Path.GetFileName(root), false);
                    }
                    foreach (string dir in Directory.GetDirectories(root)) {
                        string name = System.IO.Path.GetFileName(dir);
                        if (SameFolder(dir, sspi) || Skipped(name)) continue;
                        if (string.Equals(name, "SSPI", StringComparison.OrdinalIgnoreCase)) continue;
                        Walk(found, seen, dir, name, 1, Crashy(name), ref budget);
                    }
                } catch { }
            }
            found.Sort((a, b) => {
                int ga = a.Group == "SSPI" ? 0 : 1, gb = b.Group == "SSPI" ? 0 : 1;
                if (ga != gb) return ga - gb;
                int g = string.Compare(a.Group, b.Group, StringComparison.OrdinalIgnoreCase);
                return g != 0 ? g : b.Modified.CompareTo(a.Modified);
            });
            if (found.Count > MaxLogFiles) found.RemoveRange(MaxLogFiles, found.Count - MaxLogFiles);
            return found.ToArray();
        }

        static void Walk(List<LogFile> found, HashSet<string> seen, string dir, string group, int depth, bool crashFolder, ref int budget)
        {
            if (depth > MaxScanDepth || budget <= 0) return;
            try {
                foreach (string file in Directory.GetFiles(dir)) {
                    if (--budget < 0) return;
                    Consider(found, seen, file, group, crashFolder);
                }
                if (depth == MaxScanDepth) return;
                foreach (string sub in Directory.GetDirectories(dir)) {
                    if (--budget < 0) return;
                    string name = System.IO.Path.GetFileName(sub);
                    if (Skipped(name)) continue;
                    Walk(found, seen, sub, group, depth + 1, crashFolder || Crashy(name), ref budget);
                }
            } catch { }
        }

        static void Consider(List<LogFile> found, HashSet<string> seen, string file, string group, bool crashFolder)
        {
            string name = System.IO.Path.GetFileName(file).ToLowerInvariant();
            bool crash = crashFolder || Crashy(name) || name.EndsWith(".orbisdmp", StringComparison.Ordinal) || name.EndsWith(".dmp", StringComparison.Ordinal) || name.EndsWith(".core", StringComparison.Ordinal);
            bool log = name.EndsWith(".log", StringComparison.Ordinal) || name.Contains(".log.") || name.Contains("klog") ||
                (name.EndsWith(".txt", StringComparison.Ordinal) && name.Contains("log"));
            if (crash || log) Add(found, seen, file, group, crash);
        }

        static void Add(List<LogFile> found, HashSet<string> seen, string file, string group, bool crash)
        {
            if (!seen.Add(file)) return;
            try {
                var info = new FileInfo(file);
                if (!info.Exists) return;
                found.Add(new LogFile {
                    Id = LogId(file), Group = group, Name = info.Name, Path = file, Kind = crash ? "crash" : "log",
                    Size = info.Length, Modified = (long)(info.LastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
                    Text = !crash || LooksLikeText(file)
                });
            } catch { }
        }

        static bool Crashy(string name)
        {
            name = name.ToLowerInvariant();
            return name.Contains("crash") || name.Contains("panic") || name.Contains("coredump");
        }

        static bool Skipped(string name)
        {
            foreach (string skip in SkippedFolders) if (string.Equals(name, skip, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool SameFolder(string a, string b)
        {
            try { return string.Equals(System.IO.Path.GetFullPath(a).TrimEnd('/', '\\'), System.IO.Path.GetFullPath(b).TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        static string LogId(string path)
        {
            uint hash = 2166136261;
            foreach (char c in path) { hash ^= c; hash *= 16777619; }
            uint second = 5381;
            foreach (char c in path) second = second * 33 + c;
            return hash.ToString("x8") + second.ToString("x8");
        }

        static bool LooksLikeText(string file)
        {
            try {
                byte[] head = ReadShared(file, 0, 4096);
                foreach (byte b in head) if (b == 0) return false;
                return true;
            } catch { return false; }
        }

        static byte[] ReadShared(string file, long offset, int count)
        {
            using (var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
                if (offset > input.Length) offset = input.Length;
                input.Position = offset;
                count = (int)Math.Min(count, input.Length - offset);
                var data = new byte[count];
                int read = 0;
                while (read < count) { int n = input.Read(data, read, count - read); if (n <= 0) break; read += n; }
                if (read < count) Array.Resize(ref data, read);
                return data;
            }
        }

        internal static string ReadLogTail(string file, int limit, out long length, out bool truncated)
        {
            length = new FileInfo(file).Length;
            truncated = length > limit;
            byte[] data = ReadShared(file, truncated ? length - limit : 0, limit);
            string text = CleanLogText(data);
            if (truncated) { int nl = text.IndexOf('\n'); if (nl >= 0 && nl < 4096) text = text.Substring(nl + 1); }
            return text;
        }

        internal static string CleanLogText(byte[] data)
        {
            string text = Encoding.UTF8.GetString(data);
            var clean = new StringBuilder(text.Length);
            foreach (char c in text) {
                if (c == '\r') continue;
                clean.Append(c == '\n' || c == '\t' || !char.IsControl(c) ? c : ' ');
            }
            return clean.ToString();
        }

        // The kernel message buffer holds messages since the last restart. A panic restarts the console, so the
        // panic itself is usually gone; payload crash files and logs written before the restart are what remain.
        static string KernelLogJson()
        {
            string reason;
            byte[] data = ReadKernelLog(out reason);
            if (data == null) return "{\"available\":false,\"reason\":\"" + JsonLite.Escape(reason) + "\"}";
            int start = 0;
            if (data.Length > MaxKernelBytes) start = data.Length - MaxKernelBytes;
            int end = data.Length;
            while (end > start && data[end - 1] == 0) end--;
            var slice = new byte[end - start];
            Buffer.BlockCopy(data, start, slice, 0, slice.Length);
            string text = CleanLogText(slice).Trim('\n', ' ');
            if (text.Length == 0) return "{\"available\":false,\"reason\":\"The kernel log is empty.\"}";
            return "{\"available\":true,\"taken\":" + (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds +
                ",\"text\":\"" + JsonLite.Escape(text) + "\"}";
        }

        static byte[] ReadKernelLog(out string reason)
        {
            reason = null;
            if (KernelLogSource != null) {
                byte[] test = KernelLogSource();
                if (test == null) reason = "The PS4 kernel did not share its log.";
                return test;
            }
            try {
                UIntPtr size = UIntPtr.Zero;
                if (sysctlbyname("kern.msgbuf", null, ref size, IntPtr.Zero, UIntPtr.Zero) != 0 || size == UIntPtr.Zero) {
                    reason = "The PS4 kernel did not share its log. Your exploit or firmware may not allow it."; return null;
                }
                ulong wanted = size.ToUInt64() + 4096;
                if (wanted > 4 * 1024 * 1024) wanted = 4 * 1024 * 1024;
                var buffer = new byte[wanted];
                size = new UIntPtr(wanted);
                if (sysctlbyname("kern.msgbuf", buffer, ref size, IntPtr.Zero, UIntPtr.Zero) != 0) {
                    reason = "The PS4 kernel did not share its log. Your exploit or firmware may not allow it."; return null;
                }
                ulong got = size.ToUInt64();
                if (got < (ulong)buffer.Length) Array.Resize(ref buffer, (int)got);
                return buffer;
            } catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException || e is BadImageFormatException) {
                reason = "The kernel log can only be read on a PS4.";
            } catch (Exception e) {
                reason = "The kernel log could not be read: " + e.Message;
            }
            return null;
        }

        [DllImport("libkernel", EntryPoint = "sysctlbyname", CallingConvention = CallingConvention.Cdecl)]
        static extern int sysctlbyname(string name, byte[] oldp, ref UIntPtr oldlenp, IntPtr newp, UIntPtr newlen);
    }
}
