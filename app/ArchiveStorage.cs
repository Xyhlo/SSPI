using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Orbis
{
    internal static class ArchiveStorage
    {
        [DllImport("libGameSearchResident.prx", EntryPoint = "gs_resident_available_bytes", CallingConvention = CallingConvention.Cdecl)]
        static extern long AvailableBytes(string path);

        public static void RequireFreeSpace(string directory, long required)
        {
            long available;
            if (!File.Exists("/system/common/lib/libSceBgft.sprx"))
                available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))).AvailableFreeSpace;
            else
            {
                available = AvailableBytes(directory);
                if (available < 0) throw new IOException("Cannot read extraction free space");
            }
            if (available < required)
                throw new IOException("Extraction needs " + Math.Ceiling(required / 1073741824.0) + " GiB free");
        }
    }
}
