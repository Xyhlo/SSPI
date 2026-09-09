using System;
using System.Runtime.InteropServices;

namespace Orbis
{
    internal static class FirmwareInfo
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Size = 0x28)]
        struct SystemSoftwareVersion
        {
            public UIntPtr Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 0x1C)]
            public string VersionString;
            public uint Version;
        }

        public static string Probe()
        {
            try
            {
                var value = new SystemSoftwareVersion { Size = (UIntPtr)0x28 };
                if (sceKernelGetSystemSwVersion(ref value) != 0) return "unknown";
                return Format(value.Version);
            }
            catch
            {
                return "unknown";
            }
        }

        internal static string Format(uint version)
        {
            string hex = version.ToString("X8");
            int major, minor;
            if (version == 0 || !int.TryParse(hex.Substring(0, 2), out major) ||
                !int.TryParse(hex.Substring(2, 2), out minor)) return "unknown";
            return major + "." + minor.ToString("00");
        }

        [DllImport("libkernel", EntryPoint = "sceKernelGetSystemSwVersion",
            CallingConvention = CallingConvention.Cdecl)]
        static extern int sceKernelGetSystemSwVersion(ref SystemSoftwareVersion version);
    }
}
