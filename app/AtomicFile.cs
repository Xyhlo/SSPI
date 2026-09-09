using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Orbis
{
    internal static class AtomicFile
    {
        internal static void WriteText(string path, string body)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(body ?? "");
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                try
                {
                    if (Rename(temp, path) != 0) throw new IOException("Atomic file replacement failed");
                }
                catch (DllNotFoundException) { ReplaceManaged(temp, path); }
                catch (EntryPointNotFoundException) { ReplaceManaged(temp, path); }
            }
            finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        static void ReplaceManaged(string temp, string path)
        {
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        [DllImport("libkernel", EntryPoint = "sceKernelRename", CallingConvention = CallingConvention.Cdecl)]
        static extern int Rename(string source, string destination);
    }
}
