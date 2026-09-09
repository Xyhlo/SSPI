using System.Runtime.CompilerServices;
using Orbis.String;

namespace Orbis.Internals
{
    public unsafe class Kernel
    {

        public static void Log(string Line, params object[] Format)
        {
            LogStr((CString)string.Format(Line, Format));
        }
        static void LogStr(CString Line)
        {
            Log((void*)Line);
        }

        public static bool Jailbreak(long AuthID = 0)
        {
            return JailbreakCred(AuthID);
        }

        /// <summary>
        /// Eboot: mono_add_internal_call("Orbis.Internals.Kernel::LoadStartModule", hinted_dlopen).
        /// hinted_dlopen expects char* — pass UTF-8 CString bytes.
        /// </summary>
        public static int TryLoadStartModule(string path)
        {
            if (string.IsNullOrEmpty(path)) return 0;
            try
            {
                CString c = path;
                void* h = LoadStartModule((void*)c);
                if (h == null) return 0;
                return (int)(long)h;
            }
            catch
            {
                return 0;
            }
        }
        
        [MethodImpl(MethodImplOptions.InternalCall)]
        static extern void Log(void* Line);
        
        [MethodImpl(MethodImplOptions.InternalCall)]
        static extern bool JailbreakCred(long AuthID);
        
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool Unjailbreak();
        
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void* malloc(int Size);
        
        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern void free(void* Size);

        [MethodImpl(MethodImplOptions.InternalCall)]
        static extern void* LoadStartModule(void* utf8Path);
    }
}
