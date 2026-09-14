using Orbis.String;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Orbis.Internals
{
    internal class UserService
    {
        const int AlreadyInitialized = unchecked((int)0x80960004);
        static readonly object InitLock = new object();
        static bool _triedInit;

        // Older bootstrap binaries registered this symbol from UserService,
        // although the export belongs to SystemService. Resolve it from its
        // owning module so a missing alias is a catchable P/Invoke error.
        public static bool HideSplashScreen() { int result; return TryHideSplashScreen(out result); }

        internal static bool TryHideSplashScreen(out int result)
        {
            result = HideSplashScreenNative();
            return result == 0;
        }

        [DllImport("libSceSystemService", EntryPoint = "sceSystemServiceHideSplashScreen",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int HideSplashScreenNative();

        [DllImport("libSceUserService", EntryPoint = "sceUserServiceInitialize", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Initialize(IntPtr parameters);

        [DllImport("libSceUserService", EntryPoint = "sceUserServiceTerminate", CallingConvention = CallingConvention.Cdecl)]
        public static extern int Terminate();

        [DllImport("libSceUserService", EntryPoint = "sceUserServiceGetInitialUser", CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetInitialUser(out int userId);

        [DllImport("libSceUserService", EntryPoint = "sceUserServiceGetForegroundUser", CallingConvention = CallingConvention.Cdecl)]
        public static extern int GetForegroundUser(out int userId);

        /// <summary>Resolve an actual signed-in user; the old bootstrap did not register user-service calls.</summary>
        public static bool TryGetUserId(out int userId, out string error)
        {
            userId = -1;
            error = null;
            lock (InitLock)
            {
                if (!_triedInit)
                {
                    try
                    {
                        int irc = Initialize(IntPtr.Zero);
                        _triedInit = irc == 0 || irc == AlreadyInitialized;
                        if (irc != 0 && irc != AlreadyInitialized)
                            error = "Initialize 0x" + unchecked((uint)irc).ToString("X8");
                    }
                    catch (Exception ex)
                    {
                        error = "User service unavailable: " + ex.GetType().Name;
                    }
                }
            }
            try
            {
                int rc = GetForegroundUser(out userId);
                if (rc == 0 && IsValidUserId(userId)) { error = null; return true; }
            }
            catch (Exception ex) { error = "Foreground user unavailable: " + ex.GetType().Name; }
            // Some firmware does not expose the foreground-user query. Probe
            // initial-user separately so a missing optional export cannot skip it.
            try
            {
                int rc = GetInitialUser(out userId);
                if (rc == 0 && IsValidUserId(userId)) { error = null; return true; }
                error = "No signed-in user (0x" + unchecked((uint)rc).ToString("X8") + ")";
            }
            catch (Exception ex) { error = "Initial user unavailable: " + ex.GetType().Name; }
            userId = -1;
            return false;
        }

        internal static bool IsValidUserId(int userId) { return userId >= 0 && userId != 0xFF; }

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
