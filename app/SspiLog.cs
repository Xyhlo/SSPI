using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;

namespace Orbis
{
    internal static class SspiLog
    {
        static readonly object Gate = new object();
        static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        static readonly Regex Url = new Regex(@"https?://[^\s\""']+", RegexOptions.IgnoreCase);
        static readonly Regex Secret = new Regex(@"(?i)(authorization[\""']?\s*[:=]\s*[\""']?(?:(?:bearer|basic)\s+)?|bearer\s+|(?:access_token|refresh_token|token|api_key|apikey|password|secret|proxy_key|pairing_token)[\""']?\s*[:=]\s*[\""']?)[^\s&\""',;]+", RegexOptions.CultureInvariant);
        static bool NativeUnavailable;
        internal static string TestRoot;
        internal static string Root
        {
            get
            {
                if (TestRoot != null) return TestRoot;
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    return Path.Combine(Path.GetDirectoryName(typeof(SspiLog).Assembly.Location), "logs");
                return "/data/SSPI/logs";
            }
        }

        [DllImport("libGameSearchResident.prx", CallingConvention = CallingConvention.Cdecl)]
        static extern int gs_resident_log(string category, string message);

        public static void Initialize()
        {
            try
            {
                Directory.CreateDirectory(Root);
                foreach (string name in new[] { "network", "download", "startup", "resident", "combined" })
                    using (var stream = new FileStream(Path.Combine(Root, name + ".log"), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite)) { }
            }
            catch { }
        }

        internal static string Clean(string message)
        {
            message = message ?? "";
            if (message.Length > 8192) message = message.Substring(0, 8192);
            message = Url.Replace(message, "[url]");
            message = Secret.Replace(message, "$1[redacted]");
            var text = new StringBuilder(message.Length);
            foreach (char c in message) text.Append(char.IsControl(c) ? ' ' : c);
            return text.ToString();
        }

        public static void Write(string category, string message)
        {
            if (category != "network" && category != "download" && category != "startup" && category != "resident") return;
            try
            {
                string body = "managed mono_ms=" + Clock.ElapsedMilliseconds + " " + Clean(message);
                if (!NativeUnavailable && TestRoot == null)
                {
                    try { if (gs_resident_log(category, body) == 0) return; }
                    catch (DllNotFoundException) { NativeUnavailable = true; }
                    catch (EntryPointNotFoundException) { NativeUnavailable = true; }
                    catch (BadImageFormatException) { NativeUnavailable = true; }
                }
                lock (Gate)
                {
                    Directory.CreateDirectory(Root);
                    string line = "utc=" + DateTime.UtcNow.ToString("o") + " category=" + category + " " + body + "\n";
                    Append(Path.Combine(Root, category + ".log"), line, 8 * 1024 * 1024);
                    Append(Path.Combine(Root, "combined.log"), line, 16 * 1024 * 1024);
                }
            }
            catch { }
        }

        static void Append(string path, string line, long limit)
        {
            using (var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite))
            {
                if (file.Length >= limit) file.SetLength(0);
                file.Seek(0, SeekOrigin.End);
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                file.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
