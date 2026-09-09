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
            AppDomain.CurrentDomain.AssemblyResolve += ResolveBundledAssembly;
            AppDomain.CurrentDomain.UnhandledException += (sender, args) => RecordFailure(args.ExceptionObject as Exception);

            // Keep the branded PS4 launch image visible until SearchWindow has
            // painted its first frame, including while the resident plugin is staged.
            for (int i = 0; i < 3; i++)
            {
                try
                {
                    int uid;
                    string uerr;
                    UserService.TryGetUserId(out uid, out uerr);
                }
                catch { }
            }

            // Defer TLS/HTTPS off the pre-UI path (lazy + background).
            try
            {
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { TlsBootstrap.Initialize(); } catch { }
                    try { NativeHttp.EnsureInit(); } catch { }
                });
            }
            catch
            {
            }

            Util.PrepareAsemblies();

            // Stage the GoldHEN resident plugin independently of the selected
            // download path. Direct BGFT can fall back before the resident
            // service is entered, so relying on that service left the plugin
            // absent on first launch.
            try
            {
                string pluginStatus;
                GoldHenPluginInstaller.TryEnsureInstalled(out pluginStatus);
                try { Kernel.Log("GoldHEN plugin startup: " + pluginStatus); } catch { }
            }
            catch (Exception ex)
            {
                try { Kernel.Log("GoldHEN plugin startup failed: " + ex.Message); } catch { }
            }

            try
            {
                Window = new SearchWindow();
                Window.JoyButtonEvent += OnJoy;
            }
            catch (Exception ex)
            {
                try { Kernel.Log("UI FAIL: " + ex); } catch { }
                for (int i = 0; i < 40; i++)
                    System.Threading.Thread.Sleep(500);
                return;
            }

            try { Window.Run(); }
            catch (Exception ex) { RecordFailure(ex); throw; }
        }

        static readonly object FailureLock = new object();
        static void RecordFailure(Exception exception)
        {
            if (exception == null) return;
            try
            {
                lock (FailureLock)
                {
                    Directory.CreateDirectory(AppSettings.DataDir);
                    string path = Path.Combine(AppSettings.DataDir, "managed-errors.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                    // Exception messages can contain signed URLs; keep only type and stack.
                    File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + exception.GetType().FullName + "\n" + exception.StackTrace + "\n");
                }
            }
            catch { }
        }

        private static Assembly ResolveBundledAssembly(object sender, ResolveEventArgs args)
        {
            try
            {
                string name = new AssemblyName(args.Name).Name;
                if (name != "System.Buffers" && name != "System.Memory" &&
                    name != "System.Numerics.Vectors" && name != "System.Runtime.CompilerServices.Unsafe" &&
                    name != "System.ValueTuple" && name != "SixLabors.ImageSharp")
                    return null;
                string root = Path.GetDirectoryName(typeof(Program).Assembly.Location);
                string path = Path.Combine(Path.Combine(root, "mono"), Path.Combine("4.5", name + ".dll"));
                return File.Exists(path) ? Assembly.LoadFrom(path) : null;
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
    }
}
