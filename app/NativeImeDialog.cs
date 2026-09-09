using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Orbis.Internals;

namespace Orbis
{
    /// <summary>
    /// System PS4 IME keyboard. Uses eboot UserService InternalCalls for user id
    /// (DllImport UserService fails with 0x80960002 on this Mono host).
    /// </summary>
    internal static class NativeImeDialog
    {
        const int ImeDialogSysmodule = 0x0096;
        const int CommonDialogAlreadyInitialized = unchecked((int)0x80B80002);
        static bool _ready;
        static string _initError;
        static int _userId = 1;

        public static string LastInitError { get { return _initError; } }

        public static bool Show(string initialText, string title, string placeholder, int maxLength,
            bool urlMode, out string text, out string error)
        {
            text = initialText ?? "";
            error = null;
            if (!EnsureReady())
            {
                error = _initError ?? "IME not ready";
                return false;
            }

            maxLength = Math.Max(1, Math.Min(512, maxLength));
            if (text.Length > maxLength) text = text.Substring(0, maxLength);
            IntPtr input = IntPtr.Zero;
            IntPtr titlePtr = IntPtr.Zero;
            IntPtr placeholderPtr = IntPtr.Zero;
            bool opened = false;
            try
            {
                // refresh user each open
                string uerr;
                int uid;
                if (UserService.TryGetUserId(out uid, out uerr) && uid != 0)
                    _userId = uid;

                input = AllocUtf16(text, maxLength + 1);
                titlePtr = AllocUtf16(title ?? "", (title ?? "").Length + 1);
                placeholderPtr = AllocUtf16(placeholder ?? "", (placeholder ?? "").Length + 1);

                var setting = new ImeDialogSetting
                {
                    UserId = _userId,
                    Type = urlMode ? ImeType.Url : ImeType.Default,
                    SupportedLanguages = 0,
                    EnterLabel = urlMode ? ImeEnterLabel.Go : ImeEnterLabel.Search,
                    InputMethod = 0,
                    Filter = IntPtr.Zero,
                    Option = 0,
                    MaxTextLength = (uint)maxLength,
                    InputTextBuffer = input,
                    PosX = 960,
                    PosY = 540,
                    HorizontalAlignment = 1, // center
                    VerticalAlignment = 1,
                    Placeholder = placeholderPtr,
                    Title = titlePtr
                };
                int rc = sceImeDialogInit(ref setting, IntPtr.Zero);
                if (rc != 0)
                {
                    // retry once after re-init common dialog + user
                    try { sceCommonDialogInitialize(); } catch { }
                    UserService.TryGetUserId(out uid, out uerr);
                    if (uid != 0) { _userId = uid; setting.UserId = uid; }
                    rc = sceImeDialogInit(ref setting, IntPtr.Zero);
                }
                if (rc != 0)
                {
                    error = "IME init 0x" + unchecked((uint)rc).ToString("X8") + " user=" + _userId;
                    return false;
                }
                opened = true;

                int pendingFrames = 0;
                for (;;)
                {
                    ImeDialogStatus status = sceImeDialogGetStatus();
                    if (status == ImeDialogStatus.Finished) break;
                    if (status != ImeDialogStatus.Running &&
                        (status != ImeDialogStatus.None || pendingFrames++ >= 600))
                    {
                        error = "IME status " + status;
                        return false;
                    }
                    try { sceSystemServicePowerTick(); } catch { }
                    Thread.Sleep(16);
                }

                ImeDialogResult result;
                rc = sceImeDialogGetResult(out result);
                if (rc != 0)
                {
                    error = "IME result 0x" + unchecked((uint)rc).ToString("X8");
                    return false;
                }
                rc = sceImeDialogTerm();
                opened = false;
                if (rc != 0)
                {
                    error = "IME term 0x" + unchecked((uint)rc).ToString("X8");
                    return false;
                }
                if (result.EndStatus != ImeDialogEndStatus.Ok) return false;
                text = ReadUtf16(input, maxLength);
                return true;
            }
            catch (Exception ex)
            {
                error = "IME " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                if (opened)
                {
                    try { sceImeDialogAbort(); } catch { }
                    try { sceImeDialogTerm(); } catch { }
                }
                if (input != IntPtr.Zero) Marshal.FreeHGlobal(input);
                if (titlePtr != IntPtr.Zero) Marshal.FreeHGlobal(titlePtr);
                if (placeholderPtr != IntPtr.Zero) Marshal.FreeHGlobal(placeholderPtr);
            }
        }

        static bool EnsureReady()
        {
            if (_ready) return true;
            try
            {
                TryLoad("/system/common/lib/libSceCommonDialog.sprx");
                TryLoad("/system/common/lib/libSceImeDialog.sprx");
                TryLoad("/system/common/lib/libSceIme.sprx");
                try { sceSysmoduleLoadModule(ImeDialogSysmodule); } catch { }

                string uerr;
                int uid;
                if (!UserService.TryGetUserId(out uid, out uerr))
                {
                    _initError = "User " + (uerr ?? "fail");
                    return false;
                }
                _userId = uid != 0 ? uid : 1;

                int rc = 0;
                try { rc = sceCommonDialogInitialize(); }
                catch (Exception ex)
                {
                    _initError = "CommonDialog: " + ex.Message;
                    return false;
                }
                if (rc != 0 && rc != CommonDialogAlreadyInitialized)
                {
                    _initError = "Common dialog 0x" + unchecked((uint)rc).ToString("X8");
                    // still try IME — some FW already inited
                }

                _ready = true;
                _initError = null;
                return true;
            }
            catch (Exception ex)
            {
                _initError = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        static void TryLoad(string path)
        {
            try { Kernel.TryLoadStartModule(path); } catch { }
            try
            {
                sceKernelLoadStartModule(path, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        static IntPtr AllocUtf16(string value, int charCapacity)
        {
            int bytes = checked(Math.Max(1, charCapacity) * 2);
            IntPtr ptr = Marshal.AllocHGlobal(bytes);
            byte[] zero = new byte[bytes];
            Marshal.Copy(zero, 0, ptr, bytes);
            byte[] encoded = Encoding.Unicode.GetBytes((value ?? "") + "\0");
            Marshal.Copy(encoded, 0, ptr, Math.Min(encoded.Length, bytes));
            return ptr;
        }

        static string ReadUtf16(IntPtr ptr, int maxLength)
        {
            int length = 0;
            while (length < maxLength && Marshal.ReadInt16(ptr, length * 2) != 0) length++;
            byte[] data = new byte[length * 2];
            if (data.Length > 0) Marshal.Copy(ptr, data, 0, data.Length);
            return Encoding.Unicode.GetString(data);
        }

        enum ImeDialogStatus : uint { None = 0, Running = 1, Finished = 2 }
        enum ImeDialogEndStatus : uint { Ok = 0, UserCanceled = 1, Aborted = 2 }
        enum ImeType : uint { Default = 0, BasicLatin = 1, Url = 2, Mail = 3, Number = 4 }
        enum ImeEnterLabel : uint { Default = 0, Send = 1, Search = 2, Go = 3 }

        [StructLayout(LayoutKind.Explicit, Size = 0x60)]
        struct ImeDialogSetting
        {
            [FieldOffset(0x00)] public int UserId;
            [FieldOffset(0x04)] public ImeType Type;
            [FieldOffset(0x08)] public ulong SupportedLanguages;
            [FieldOffset(0x10)] public ImeEnterLabel EnterLabel;
            [FieldOffset(0x14)] public uint InputMethod;
            [FieldOffset(0x18)] public IntPtr Filter;
            [FieldOffset(0x20)] public uint Option;
            [FieldOffset(0x24)] public uint MaxTextLength;
            [FieldOffset(0x28)] public IntPtr InputTextBuffer;
            [FieldOffset(0x30)] public float PosX;
            [FieldOffset(0x34)] public float PosY;
            [FieldOffset(0x38)] public uint HorizontalAlignment;
            [FieldOffset(0x3C)] public uint VerticalAlignment;
            [FieldOffset(0x40)] public IntPtr Placeholder;
            [FieldOffset(0x48)] public IntPtr Title;
            [FieldOffset(0x50)] public ulong Reserved0;
            [FieldOffset(0x58)] public ulong Reserved1;
        }

        [StructLayout(LayoutKind.Explicit, Size = 0x10)]
        struct ImeDialogResult
        {
            [FieldOffset(0x00)] public ImeDialogEndStatus EndStatus;
            [FieldOffset(0x04)] public uint Reserved0;
            [FieldOffset(0x08)] public ulong Reserved1;
        }

        [DllImport("libkernel", EntryPoint = "sceKernelLoadStartModule", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceKernelLoadStartModule(
            [MarshalAs(UnmanagedType.LPStr)] string path,
            int argc, IntPtr argv, int flags, IntPtr pOpt, IntPtr pRes);

        [DllImport("libSceImeDialog", EntryPoint = "sceImeDialogInit", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceImeDialogInit(ref ImeDialogSetting setting, IntPtr extended);

        [DllImport("libSceImeDialog", EntryPoint = "sceImeDialogGetStatus", CallingConvention = CallingConvention.Cdecl)]
        static extern ImeDialogStatus sceImeDialogGetStatus();

        [DllImport("libSceImeDialog", EntryPoint = "sceImeDialogGetResult", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceImeDialogGetResult(out ImeDialogResult result);

        [DllImport("libSceImeDialog", EntryPoint = "sceImeDialogTerm", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceImeDialogTerm();

        [DllImport("libSceImeDialog", EntryPoint = "sceImeDialogAbort", CallingConvention = CallingConvention.Cdecl)]
        static extern void sceImeDialogAbort();

        [DllImport("libSceCommonDialog", EntryPoint = "sceCommonDialogInitialize", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceCommonDialogInitialize();

        [DllImport("libSceSysmodule", EntryPoint = "sceSysmoduleLoadModule", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceSysmoduleLoadModule(int moduleId);

        [DllImport("libSceSystemService", EntryPoint = "sceSystemServicePowerTick", CallingConvention = CallingConvention.Cdecl)]
        static extern void sceSystemServicePowerTick();
    }
}
