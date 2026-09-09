using Orbis.String;
using System;
using System.Runtime.CompilerServices;

namespace Orbis.Internals
{
    internal class UserService
    {
        // Must match eboot main.c mono_add_internal_call names exactly:
        // Initialize, Terminate, GetInitialUser, GetForegroundUser, HideSplashScreen, NativeLoadExec
        const int AlreadyInitialized = unchecked((int)0x80960004);

        static bool _triedInit;
        static int _cachedUserId = -1;

        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern bool HideSplashScreen();

        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int Initialize(IntPtr parameters);

        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int Terminate();

        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int GetInitialUser(out int userId);

        [MethodImpl(MethodImplOptions.InternalCall)]
        public static extern int GetForegroundUser(out int userId);

        /// <summary>Initialize UserService via eboot IC and resolve a user id for IME/pad.</summary>
        public static bool TryGetUserId(out int userId, out string error)
        {
            userId = 0;
            error = null;
            try
            {
                if (!_triedInit)
                {
                    _triedInit = true;
                    try
                    {
                        int irc = Initialize(IntPtr.Zero);
                        // 0 ok; already-init ok; other codes still probe
                        if (irc != 0 && irc != AlreadyInitialized)
                            error = "Initialize 0x" + unchecked((uint)irc).ToString("X8");
                    }
                    catch (Exception ex)
                    {
                        error = "Initialize IC missing: " + ex.GetType().Name;
                    }
                }

                int rc = GetForegroundUser(out userId);
                if (rc == 0 && userId != 0)
                {
                    _cachedUserId = userId;
                    return true;
                }

                rc = GetInitialUser(out userId);
                if (rc == 0 && userId != 0)
                {
                    _cachedUserId = userId;
                    return true;
                }

                if (_cachedUserId > 0)
                {
                    userId = _cachedUserId;
                    return true;
                }

                // Homebrew often still works with primary user = 1
                userId = 1;
                if (error == null)
                    error = "probe fg/init failed; using userId=1";
                return true;
            }
            catch (Exception ex)
            {
                userId = 1;
                error = "UserService: " + ex.GetType().Name + " (fallback user=1)";
                return true;
            }
        }

        public unsafe static bool LoadExec(string Path, params string[] Args)
        {
            if (Args.Length > 0)
            {
                void*[] pArgs = new void*[Args.Length + 1];
                for (int i = 0; i < Args.Length; i++)
                    pArgs[i] = (CString)Args[i];
                pArgs[Args.Length] = null;
                fixed (void* ppArgs = &pArgs[0])
                    return NativeLoadExec((CString)Path, ppArgs);
            }
            return NativeLoadExec((CString)Path, null);
        }

        [MethodImpl(MethodImplOptions.InternalCall)]
        unsafe static extern bool NativeLoadExec(void* Path, void* Args);
    }
}
