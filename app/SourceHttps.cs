using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Orbis
{
    /// <summary>
    /// Source-only HTTPS transport. The bundled native bridge is used only by
    /// the GameSource page/API reader; package and debrid transfers stay on
    /// NetHttp/NativeHttp. Source requests use the bridge's isolated transport
    /// policy so a jailbroken console trust store or clock cannot block source
    /// discovery. Redirects are still bounded and HTTPS is never downgraded.
    /// </summary>
    internal static class SourceHttps
    {
        public const int MaxBytes = 2 * 1024 * 1024;
        const int MaxRedirects = 8;
        static readonly object Gate = new object();
        static RequestFn _request;
        static string _certificates;

        // Test seams. Production leaves these null and uses the native bridge.
        internal static Func<DateTime> Clock;
        internal static Func<string, int, string, string, string, string> PlainFetch;
        internal static Func<string, int, string, string, string> ModuleFetch;
        internal static Action ModuleInit;
        internal static Func<bool> ModuleReady;
        internal static Func<string> ModuleDetail;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        delegate int RequestFn(string ca, string url, string referer, string agent, int timeout,
            long utc, IntPtr body, int capacity, out int length, out int status,
            StringBuilder location, int locationCapacity, StringBuilder error, int errorCapacity);

        public static string GetString(string url, int timeoutMs, string referer, string userAgent)
        {
            Uri requested;
            try { requested = new Uri(url, UriKind.Absolute); }
            catch { throw new IOException("Unsupported source URL"); }
            if (!string.IsNullOrEmpty(requested.UserInfo) ||
                (requested.Scheme != Uri.UriSchemeHttp && requested.Scheme != Uri.UriSchemeHttps))
                throw new IOException("Unsupported source URL");

            if (requested.Scheme == Uri.UriSchemeHttp)
            {
                if (PlainFetch != null) return PlainFetch(url, timeoutMs, referer, null, userAgent);
                return NetHttp.GetStringDirect(url, timeoutMs, referer, null, userAgent);
            }

            lock (Gate)
            {
                if (ModuleFetch != null)
                    return ModuleFetch(requested.AbsoluteUri, timeoutMs, referer, userAgent);
                EnsureLoaded();
                var current = requested;
                var timer = Stopwatch.StartNew();
                IntPtr body = Marshal.AllocHGlobal(MaxBytes);
                try
                {
                    long utc = ToUnixSeconds(Clock != null ? Clock() : DateTime.UtcNow);
                    for (int hop = 0; hop < MaxRedirects; hop++)
                    {
                        int remaining = Math.Max(1000, timeoutMs <= 0 ? 45000 : timeoutMs) -
                            (int)timer.ElapsedMilliseconds;
                        if (remaining <= 0) throw new TimeoutException("Source request timed out");
                        var location = new StringBuilder(4096);
                        var error = new StringBuilder(512);
                        int length, status;
                        int rc = _request(_certificates, current.AbsoluteUri, referer,
                            userAgent ?? NetHttp.UserAgent, remaining,
                            utc + (long)(timer.ElapsedMilliseconds / 1000), body, MaxBytes,
                            out length, out status, location, location.Capacity,
                            error, error.Capacity);
                        Log(current.Host + " source-module rc=" + rc + " http=" + status + " bytes=" + length);
                        if (rc != 0) throw new IOException("Source HTTPS: " + error);
                        if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                        {
                            if (location.Length == 0) throw new IOException("Source redirect has no destination");
                            Uri next = new Uri(current, location.ToString());
                            if (next.Scheme != Uri.UriSchemeHttps)
                                throw new IOException("Source redirected HTTPS to an insecure URL");
                            current = next;
                            continue;
                        }
                        if (status < 200 || status >= 300) throw new IOException("Source HTTP " + status);
                        if (length < 0 || length > MaxBytes) throw new IOException("Invalid source response size");
                        byte[] data = new byte[length];
                        Marshal.Copy(body, data, 0, length);
                        return Encoding.UTF8.GetString(data);
                    }
                    throw new IOException("Too many source redirects");
                }
                finally { Marshal.FreeHGlobal(body); }
            }
        }

        static long ToUnixSeconds(DateTime value)
        {
            try { return (long)(value.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }
            catch { return 0; }
        }

        static void EnsureLoaded()
        {
            if (_request != null) return;
            if (ModuleInit != null) ModuleInit();
            string root = null;
            try { root = Orbis.Internals.IO.GetAppBaseDirectory(); }
            catch { }
            if (!string.IsNullOrWhiteSpace(root))
                _certificates = Path.Combine(root, "assets", "certs", "source-ca.pem");
            else
                _certificates = Path.Combine("/app0", "assets", "certs", "source-ca.pem");
            // Jailbroken mounts can expose app0 through different roots. Try
            // the resolved base, the canonical /app0 mount, then the runtime
            // module name, matching the resident bridge loader.
            string[] candidates = string.IsNullOrWhiteSpace(root)
                ? new string[] { "/app0/sce_module/libSspiHttps.prx", "libSspiHttps.prx" }
                : new string[]
                {
                    Path.Combine(root, "sce_module", "libSspiHttps.prx"),
                    "/app0/sce_module/libSspiHttps.prx",
                    "libSspiHttps.prx"
                };
            int handle = unchecked((int)0x80000001);
            foreach (string candidate in candidates)
            {
                handle = Load(candidate);
                if (handle >= 0) break;
                Log("libSspiHttps candidate " + candidate + " -> 0x" + unchecked((uint)handle).ToString("X"));
            }
            if (handle < 0) throw new IOException("Cannot load source HTTPS module: 0x" + unchecked((uint)handle).ToString("X"));
            IntPtr symbol;
            int result = sceKernelDlsym(handle, "gs_https_get", out symbol);
            if (result != 0 || symbol == IntPtr.Zero) throw new IOException("Source HTTPS entry point is unavailable");
            _request = (RequestFn)Marshal.GetDelegateForFunctionPointer(symbol, typeof(RequestFn));
            if (ModuleReady != null && !ModuleReady())
                throw new IOException("Source HTTPS module is unavailable (" + (ModuleDetail != null ? ModuleDetail() : "unknown") + ")");
        }

        static int Load(string path)
        {
            int handle = unchecked((int)0x80000001);
            try { handle = sceKernelLoadStartModule(path, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero); }
            catch { }
            if (handle >= 0) return handle;
            try { return Orbis.Internals.Kernel.TryLoadStartModule(path); }
            catch { return handle; }
        }

        static void Log(string message)
        {
            try { File.AppendAllText(Path.Combine(AppSettings.DataDir, "source-https.log"), message + "\n"); } catch { }
        }

        [DllImport("libkernel", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceKernelLoadStartModule(string path, uint argc, IntPtr argv, uint flags, IntPtr options, IntPtr result);
        [DllImport("libkernel", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceKernelDlsym(int handle, string name, out IntPtr symbol);
    }
}
