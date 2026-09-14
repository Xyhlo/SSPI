using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Orbis
{
    internal static class UsbVolumeLabel
    {
        [DllImport("libkernel", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceKernelOpen(string path, int flags, int mode);
        [DllImport("libkernel", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceKernelClose(int fd);
        [DllImport("libkernel", CallingConvention = CallingConvention.Cdecl)]
        static extern long sceKernelPread(int fd, [Out] byte[] data, ulong size, long offset);
        [DllImport("libkernel", CallingConvention = CallingConvention.Cdecl)]
        static extern int _fstatfs(int fd, [Out] byte[] data);
        [DllImport("libkernel", CallingConvention = CallingConvention.Cdecl)]
        static extern int sceKernelFstat(int fd, [Out] byte[] data);
        internal const int StatFsBufferSize = 4096;

        internal static string Clean(string value)
        {
            var result = new StringBuilder();
            foreach (char c in value ?? "") {
                if (c == '\0') break;
                if (char.IsControl(c) || c == '/' || c == '\\' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) continue;
                result.Append(c); if (result.Length >= 48) break;
            }
            string label = result.ToString().Trim();
            return label.Equals("NO NAME", StringComparison.OrdinalIgnoreCase) ? "" : label;
        }

        internal static string Read(string mount)
        {
            if (mount == null || mount.Length != 9 || !mount.StartsWith("/mnt/usb", StringComparison.Ordinal) || mount[8] < '0' || mount[8] > '7') return "";
            int directory = -1, device = -1;
            try {
                directory = sceKernelOpen(mount, 0, 0);
                if (directory < 0) return "";
                // PS4 statfs may contain two 1024-byte mount names. A 1024-byte
                // destination lets the kernel overwrite the managed array.
                var stats = new byte[StatFsBufferSize];
                string mounted, path;
                if (_fstatfs(directory, stats) != 0 || !TryMountRecord(stats, out mounted, out path) || mounted != mount) return "";
                if (!path.StartsWith("/dev/", StringComparison.Ordinal) || path.Length <= 5 || path.IndexOf('/', 5) >= 0 || path.Contains("..")) return "";
                device = sceKernelOpen(path, 0, 0);
                if (device < 0) return "";
                int fd = device;
                return Parse((offset, count) => {
                    var data = new byte[count];
                    if (sceKernelPread(fd, data, (ulong)count, offset) != count) throw new IOException("USB label read unavailable");
                    return data;
                });
            } catch { return ""; }
            finally { if (device >= 0) sceKernelClose(device); if (directory >= 0) sceKernelClose(directory); }
        }

        internal static bool? MountRecordPresent(byte[] stats, string mount)
        {
            string mounted, device;
            if (!TryMountRecord(stats, out mounted, out device)) return null;
            return string.Equals(mounted, mount, StringComparison.Ordinal);
        }

        static bool TryMountRecord(byte[] stats, out string mounted, out string device)
        {
            mounted = device = "";
            if (stats == null || stats.Length < 472 || U32(stats, 0) != 0x20030518) return false;
            // Sony long names with either SDK fsid width, then FreeBSD 9 names.
            int[,] layouts = { { 292, 1316, 1024 }, { 296, 1320, 1024 }, { 296, 384, 88 } };
            for (int i = 0; i < layouts.GetLength(0); i++)
            {
                string candidate = CString(stats, layouts[i, 1], layouts[i, 2]);
                if (!candidate.StartsWith("/", StringComparison.Ordinal)) continue;
                mounted = candidate; device = CString(stats, layouts[i, 0], layouts[i, 2]); return true;
            }
            return false;
        }

        internal static bool IsConnected(string mount)
        { string detail; return IsConnected(mount, out detail); }

        internal static bool IsConnected(string mount, out string detail)
        {
            detail = "Invalid USB mount path";
            if (mount == null || mount.Length != 9 || !mount.StartsWith("/mnt/usb", StringComparison.Ordinal) ||
                mount[8] < '0' || mount[8] > '7') return false;
            try { return ProbeConnected(mount, (path, flags) => sceKernelOpen(path, flags, 0),
                (fd, data) => sceKernelFstat(fd, data), fd => { sceKernelClose(fd); }, out detail); }
            catch (Exception ex) { detail = "USB access check failed: " + ex.GetType().Name + " " + ex.Message; return false; }
        }

        internal static bool ProbeConnected(string mount, Func<string, int, int> open,
            Func<int, byte[], int> stat, Action<int> close)
        { string detail; return ProbeConnected(mount, open, stat, close, out detail); }

        internal static bool ProbeConnected(string mount, Func<string, int, int> open,
            Func<int, byte[], int> stat, Action<int> close, out string detail)
        {
            detail = "";
            int directory = -1, parent = -1;
            try {
                directory = open(mount, 0x20000 | 0x100);
                if (directory < 0) { detail = "USB open failed " + mount + " (0x" + unchecked((uint)directory).ToString("X8") + ")"; return false; }
                parent = open("/mnt", 0x20000 | 0x100);
                if (parent < 0) { detail = "USB parent open failed /mnt (0x" + unchecked((uint)parent).ToString("X8") + ")"; return false; }
                var driveStat = new byte[256]; var parentStat = new byte[256];
                int driveResult = stat(directory, driveStat), parentResult = stat(parent, parentStat);
                if (driveResult != 0 || parentResult != 0) {
                    detail = "USB stat failed " + mount + " (0x" + unchecked((uint)driveResult).ToString("X8") + "), /mnt (0x" + unchecked((uint)parentResult).ToString("X8") + ")"; return false;
                }
                // Match the resident's mount proof. A leftover empty usbN directory
                // belongs to /mnt's device; a mounted USB has its own st_dev.
                uint driveDevice = U32(driveStat, 0), parentDevice = U32(parentStat, 0);
                if (driveDevice != parentDevice) return true;
                detail = "Reconnect the USB drive: " + mount + " has no mounted device (drive=" + driveDevice + ", parent=" + parentDevice + ")";
                return false;
            }
            finally { if (parent >= 0) close(parent); if (directory >= 0) close(directory); }
        }

        static string CString(byte[] data, int offset, int count)
        {
            if (data == null || offset < 0 || count <= 0 || offset > data.Length - count) return "";
            int end = offset; while (end < offset + count && data[end] != 0) end++;
            if (end == offset + count) return "";
            return Encoding.UTF8.GetString(data, offset, end - offset);
        }
        static uint U32(byte[] data, int at) { return BitConverter.ToUInt32(data, at); }
        static ushort U16(byte[] data, int at) { return BitConverter.ToUInt16(data, at); }
        static string FatLabel(byte[] data, int at)
        {
            Encoding encoding;
            try { encoding = Encoding.GetEncoding(437); } catch { encoding = Encoding.ASCII; }
            return Clean(encoding.GetString(data, at, 11));
        }

        // Metadata only, never a filesystem mount or write. Bound reads to 512 KiB
        // and 16 root clusters so a damaged USB cannot trigger an unbounded scan.
        internal static string Parse(Func<long, int, byte[]> reader)
        {
            try {
                int remaining = 512 * 1024;
                Func<long, int, byte[]> read = (offset, size) => {
                    if (offset < 0 || size < 1 || size > remaining) throw new InvalidDataException();
                    remaining -= size; var data = reader(offset, size);
                    if (data == null || data.Length != size) throw new InvalidDataException();
                    return data;
                };
                byte[] boot = read(0, 4096);
                if (U16(boot, 510) != 0xaa55) return "";
                bool exfat = Encoding.ASCII.GetString(boot, 3, 8) == "EXFAT   ";
                int sector, clusterBytes; long fat, heap, volumeBytes; uint cluster, clusterCount; string backup = "";
                if (exfat) {
                    int shift = boot[108], clusterShift = boot[109];
                    if (shift < 9 || shift > 12 || clusterShift > 25 - shift || boot[110] < 1 || boot[110] > 2) return "";
                    sector = 1 << shift; clusterBytes = 1 << (shift + clusterShift);
                    ulong sectors = BitConverter.ToUInt64(boot, 72);
                    if (sectors > (ulong)(long.MaxValue / sector)) return "";
                    volumeBytes = (long)sectors * sector;
                    fat = (long)U32(boot, 80) * sector;
                    if (boot[110] == 2 && (U16(boot, 106) & 1) != 0) fat += (long)U32(boot, 84) * sector;
                    heap = (long)U32(boot, 88) * sector; cluster = U32(boot, 96); clusterCount = U32(boot, 92);
                } else {
                    sector = U16(boot, 11); int perCluster = boot[13], fats = boot[16], reserved = U16(boot, 14);
                    if (sector < 512 || sector > 4096 || (sector & (sector - 1)) != 0 || perCluster < 1 || perCluster > 128 || (perCluster & (perCluster - 1)) != 0 || fats < 1 || fats > 2 || reserved < 1) return "";
                    uint fatSectors = U16(boot, 22); bool fat32 = fatSectors == 0;
                    if (fat32) fatSectors = U32(boot, 36);
                    uint total = U16(boot, 19); if (total == 0) total = U32(boot, 32);
                    volumeBytes = (long)total * sector; fat = (long)reserved * sector;
                    long root = (reserved + (long)fats * fatSectors) * sector;
                    long rootBytes = ((U16(boot, 17) * 32L + sector - 1) / sector) * sector;
                    heap = root + rootBytes; clusterBytes = sector * perCluster;
                    if (fatSectors == 0 || heap >= volumeBytes) return "";
                    clusterCount = (uint)((volumeBytes - heap) / clusterBytes); cluster = fat32 ? U32(boot, 44) : 0;
                    backup = boot[fat32 ? 66 : 38] == 0x29 ? FatLabel(boot, fat32 ? 71 : 43) : "";
                    if (!fat32) { string label=Scan(read, root, (int)Math.Min(rootBytes, remaining), false); return string.IsNullOrEmpty(label)?backup:label; }
                    ushort flags = U16(boot, 40);
                    if ((flags & 0x80) != 0) { int active = flags & 0x0f; if (active >= fats) return ""; fat += (long)active * fatSectors * sector; }
                }
                if (heap <= 0 || heap >= volumeBytes || fat < sector || fat >= heap || clusterCount == 0) return "";
                var seen = new HashSet<uint>();
                for (int n = 0; n < 16 && cluster >= 2 && cluster < (ulong)clusterCount + 2 && seen.Add(cluster); n++) {
                    long offset = checked(heap + ((long)cluster - 2) * clusterBytes);
                    int size = Math.Min(clusterBytes, Math.Min(64 * 1024, remaining - sector));
                    if (size <= 0 || offset > volumeBytes - size) break;
                    string label = Scan(read, offset, size, exfat);
                    if (label != null) return label.Length > 0 ? label : backup;
                    if (size < clusterBytes) break;
                    long entry = (long)cluster * 4, fatOffset = fat + entry / sector * sector;
                    if (fatOffset > heap - sector) break;
                    cluster = U32(read(fatOffset, sector), (int)(entry % sector));
                    if (!exfat) cluster &= 0x0fffffff;
                }
                return backup;
            } catch { return ""; }
        }
        static string Scan(Func<long, int, byte[]> read, long offset, int count, bool exfat)
        {
            if (count < 32) return null;
            byte[] data = read(offset, count);
            for (int i = 0; i + 32 <= data.Length; i += 32) {
                if (data[i] == 0) return "";
                if (exfat && data[i] == 0x83 && data[i + 1] <= 11) return Clean(Encoding.Unicode.GetString(data, i + 2, data[i + 1] * 2));
                if (!exfat && data[i] != 0xe5 && (data[i + 11] & 0x3f) == 8) return FatLabel(data, i);
            }
            return null;
        }
    }
}
