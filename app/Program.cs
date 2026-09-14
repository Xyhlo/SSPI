using System;
using System.IO;
using System.Reflection;
using Orbis.Internals;
using SDL2.Events;
using SDL2.Types;

namespace Orbis
{
    internal class Program
    {
        private static SearchWindow Window;

        public static void Main()
        {
            // Keep the entry point small so a dependency failure while JITting
            // the UI startup method is caught before native invocation returns.
            try { RunManaged(); }
            catch (Exception ex) { RecordFailure(ex); StartupStage("managed-start-failed"); throw; }
        }

        static void RunManaged()
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveBundledAssembly;
            AppDomain.CurrentDomain.UnhandledException += (sender, args) => RecordFailure(args.ExceptionObject as Exception);
            StartupStage("managed-entry");
            SspiLog.Initialize();

            try
            {
                // Record entry before resolving storage: migration may perform I/O.
                StartupStage("data-directory-resolve");
                string dataRoot = AppSettings.DataDir;
                SspiLog.Write("startup", "build=" + BuildIdentity.Label + " stage=data-directory-ready data=" + dataRoot);
            }
            catch (Exception ex) { RecordFailure(ex); StartupStage("data-directory-unavailable"); }

            try
            {
                StartupStage("user-service");
                int uid;
                string uerr;
                StartupStage(UserService.TryGetUserId(out uid, out uerr) ? "user-service-ready" : "user-service-unavailable");
            }
            catch (Exception ex) { RecordFailure(ex); }

            // HTTPS remains on demand. Resident maintenance starts on a background
            // thread after normal menu frames render, never before the first screen.

            Util.PrepareAsemblies();

            try
            {
                StartupStage("window-create");
                Window = new SearchWindow();
                Window.JoyButtonEvent += OnJoy;
                StartupStage("window-ready");
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
                StartupStage("window-create-failed");
                // The native bootstrap captures this exception and reports the failed
                // startup stage instead of treating it as a successful app exit.
                throw;
            }

            try { StartupStage("window-run"); Window.Run(); StartupStage("window-exit"); }
            catch (Exception ex) { RecordFailure(ex); StartupStage("window-run-failed"); throw; }
            finally
            {
                ResidentDownloadService.StopLaunchMaintenance();
                try { Window.Dispose(); } catch { }
            }
        }

        static readonly object FailureLock = new object();
        internal static void StartupStage(string stage)
        {
            try
            {
                lock (FailureLock)
                {
                    SspiLog.Write("startup", "build=" + BuildIdentity.Label + " stage=" + stage);
                }
            }
            catch { }
        }

        internal static void RecordFailure(Exception exception)
        {
            if (exception == null) return;
            try
            {
                lock (FailureLock)
                {
                    for (int depth = 0; exception != null && depth < 8; depth++, exception = exception.InnerException)
                    {
                        string identity = "exception=" + exception.GetType().FullName + " depth=" + depth +
                            " hresult=0x" + unchecked((uint)exception.HResult).ToString("X8");
                        // Preserve basic identity before any optional formatter or
                        // SDL type resolution can fail on the console runtime.
                        SspiLog.Write("startup", identity);
                        try
                        {
                            string message = StartupFailureMessage(exception);
                            if (!string.IsNullOrEmpty(message)) SspiLog.Write("startup", identity + " " + BoundedDiagnostic(message, 1024));
                        }
                        catch { }
                        try
                        {
                            string stack = exception.StackTrace;
                            if (!string.IsNullOrEmpty(stack)) SspiLog.Write("startup", identity + " stack=" + BoundedDiagnostic(stack, 2048));
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        static string BoundedDiagnostic(string value, int length)
        { return value.Length <= length ? value : value.Substring(0, length); }

        static string StartupFailureMessage(Exception exception)
        {
            // Binding/type failures name the missing runtime member or library.
            // Arbitrary application exception messages can contain user secrets.
            if (exception is TypeLoadException || exception is TypeInitializationException ||
                exception is MissingMemberException || exception is DllNotFoundException ||
                exception is EntryPointNotFoundException || exception is BadImageFormatException ||
                exception is FileNotFoundException || exception is FileLoadException)
                return "message=" + exception.Message;
            return SdlFailureMessage(exception);
        }

        static string SdlFailureMessage(Exception exception)
        {
            // SDLException hides Exception.Message in this binding. Isolate that
            // dependency from the basic exception logger's JIT compilation.
            var sdl = exception as SDL2.Exceptions.SDLException;
            return sdl == null ? null : "sdl_code=" + sdl.ErrorCode + " sdl_error=" + sdl.Message;
        }

        private static Assembly ResolveBundledAssembly(object sender, ResolveEventArgs args)
        {
            try
            {
                string name = new AssemblyName(args.Name).Name;
                if (name != "SharpCompress" && name != "Microsoft.Bcl.AsyncInterfaces" &&
                    name != "System.Text.Encoding.CodePages" && name != "System.Threading.Tasks.Extensions" &&
                    name != "System.Buffers" && name != "System.Memory" &&
                    name != "System.Numerics.Vectors" && name != "System.Runtime.CompilerServices.Unsafe" &&
                    name != "System.ValueTuple" && name != "SixLabors.ImageSharp")
                    return null;
                string location = null, mountedRoot = null, domainRoot = null;
                try { location = typeof(Program).Assembly.Location; } catch { }
                try { mountedRoot = Orbis.Internals.IO.GetAppBaseDirectory(); } catch { }
                try { domainRoot = AppDomain.CurrentDomain.BaseDirectory; } catch { }
                string path = FindBundledAssembly(name, location, mountedRoot, domainRoot);
                return path != null ? Assembly.LoadFrom(path) : null;
            }
            catch { return null; }
        }

        private static void OnJoy(object sender, JoyButtonEvent e)
        {
            if (e.ButtonState != JoyButtonEvent.Type.Up)
                return;
            try { Window.HandleButton(e.Button); }
            catch (Exception ex) { RecordFailure(ex); }
        }

        internal static string FindBundledAssembly(string name, string location, string mountedRoot, string domainRoot)
        {
            string assemblyRoot = null;
            try { if (!string.IsNullOrWhiteSpace(location)) assemblyRoot = Path.GetDirectoryName(location); } catch { }
            foreach (string root in new[] { mountedRoot, assemblyRoot, domainRoot, "/app0" })
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                try
                {
                    string path = Path.Combine(root, "mono", "4.5", name + ".dll");
                    if (File.Exists(path)) return path;
                }
                catch { }
            }
            return null;
        }
    }
}
