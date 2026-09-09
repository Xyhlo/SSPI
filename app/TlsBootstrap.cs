using System;
using System.IO;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Orbis.Internals;

namespace Orbis
{
    /// <summary>
    /// Fix MonoBTLS SecureChannelFailure: writable mono home + import CA bundle
    /// before any HTTPS. Still may fail if native MonoBTLS is broken — then use LanProxy.
    /// </summary>
    internal static class TlsBootstrap
    {
        public static string LastLog = "";
        public static bool StoreReady;
        public static bool ProbeOk;
        public static string ProbeDetail = "";

        public static void Initialize()
        {
            var sb = new StringBuilder();
            try
            {
                string dataRoot = AppSettings.DataDir;
                string home = Path.Combine(dataRoot, "mono-home");
                string xdg = Path.Combine(home, ".config");
                Directory.CreateDirectory(home);
                Directory.CreateDirectory(xdg);
                Directory.CreateDirectory(Path.Combine(xdg, ".mono", "certs", "Trust"));

                try
                {
                    Environment.SetEnvironmentVariable("HOME", home);
                    Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", xdg);
                    sb.Append("HOME=").Append(home).Append("; ");
                }
                catch (Exception ex)
                {
                    sb.Append("env-fail=").Append(ex.GetType().Name).Append("; ");
                }

                try
                {
                    ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS1.2
                }
                catch
                {
                    try { ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls; } catch { }
                }

                // Use the platform trust store (including the CA bundle imported below).
                // Never replace certificate validation with an accept-all callback.
                ServicePointManager.ServerCertificateValidationCallback = null;
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.DefaultConnectionLimit = 8;

                string certPath = FindCaBundle();
                sb.Append("ca=").Append(certPath ?? "none").Append("; ");
                if (!string.IsNullOrEmpty(certPath) && File.Exists(certPath))
                {
                    int n = ImportPemBundle(certPath, sb);
                    sb.Append("imported=").Append(n).Append("; ");
                    StoreReady = n >= 0;
                }

                ProbeOk = ProbeHttps(sb);
            }
            catch (Exception ex)
            {
                sb.Append("init-ex=").Append(ex.GetType().Name).Append(":").Append(ex.Message);
            }
            LastLog = sb.ToString();
            try { File.WriteAllText(Path.Combine(AppSettings.DataDir, "tls.log"), LastLog); } catch { }
        }

        static string FindCaBundle()
        {
            string baseDir = ".";
            try { baseDir = IO.GetAppBaseDirectory() ?? "."; } catch { }
            string[] paths =
            {
                Path.Combine(baseDir, "assets", "certs", "ca-certificates.crt"),
                Path.Combine(baseDir, "certs", "ca-certificates.crt"),
                "/app0/assets/certs/ca-certificates.crt",
                "/app0/certs/ca-certificates.crt"
            };
            foreach (var p in paths)
                if (File.Exists(p)) return p;
            return null;
        }

        static int ImportPemBundle(string pemPath, StringBuilder sb)
        {
            int imported = 0;
            X509Store store = null;
            try
            {
                store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);
                string text = File.ReadAllText(pemPath);
                const string begin = "-----BEGIN CERTIFICATE-----";
                const string end = "-----END CERTIFICATE-----";
                int pos = 0;
                while (true)
                {
                    int first = text.IndexOf(begin, pos, StringComparison.Ordinal);
                    if (first < 0) break;
                    first += begin.Length;
                    int last = text.IndexOf(end, first, StringComparison.Ordinal);
                    if (last < 0) break;
                    string b64 = text.Substring(first, last - first);
                    b64 = b64.Replace("\r", "").Replace("\n", "").Replace(" ", "");
                    try
                    {
                        byte[] der = Convert.FromBase64String(b64);
                        var cert = new X509Certificate2(der);
                        var existing = store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, false);
                        if (existing.Count == 0)
                        {
                            store.Add(cert);
                            imported++;
                        }
                        cert.Reset();
                    }
                    catch
                    {
                    }
                    pos = last + end.Length;
                    // Cap import time on console
                    if (imported > 200) break;
                }
                sb.Append("storeCount=").Append(store.Certificates.Count).Append("; ");
                return imported;
            }
            catch (Exception ex)
            {
                sb.Append("store-fail=").Append(ex.GetType().Name).Append("; ");
                return -1;
            }
            finally
            {
                if (store != null) try { store.Close(); } catch { }
            }
        }

        static bool ProbeHttps(StringBuilder sb)
        {
            try
            {
                // Prefer a lightweight endpoint
                string body = NetHttp.GetStringDirect("https://api.real-debrid.com/rest/1.0/time", 12000);
                sb.Append("probe=ok len=").Append((body ?? "").Length).Append("; ");
                ProbeDetail = "HTTPS OK";
                return true;
            }
            catch (Exception ex)
            {
                string msg = ex.ToString();
                if (msg.Length > 300) msg = msg.Substring(0, 300);
                sb.Append("probe-fail=").Append(msg).Append("; ");
                ProbeDetail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }
    }
}
