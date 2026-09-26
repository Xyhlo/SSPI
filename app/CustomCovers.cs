using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Formats.Png;

namespace Orbis
{
    internal static class CustomCovers
    {
        internal const int Width = 384, Height = 432, ByteCount = Width * Height * 4;
        internal const int IconSize = 512, IconByteCount = IconSize * IconSize * 4, UploadByteCount = ByteCount + IconByteCount;
        static readonly object Gate = new object();
        static volatile Dictionary<string, string> _paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static bool _loaded;
        static string Root { get { return Path.Combine(AppSettings.DataDir, "custom-covers"); } }

        internal static bool ValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length != 9) return false;
            for (int i = 0; i < id.Length; i++)
                if (i < 4 ? id[i] < 'A' || id[i] > 'Z' : id[i] < '0' || id[i] > '9') return false;
            return true;
        }

        internal static void Load()
        {
            lock (Gate) {
                if (_loaded) return;
                var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try {
                    if (Directory.Exists(Root)) foreach (string index in Directory.EnumerateFiles(Root, "*.ref")) {
                        string id = Path.GetFileNameWithoutExtension(index);
                        if (!ValidId(id)) continue;
                        try {
                            if (new FileInfo(index).Length > 64) continue;
                            string name = File.ReadAllText(index).Trim(); Guid version;
                            if (!name.EndsWith(".png", StringComparison.Ordinal) || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out version) ||
                                name != version.ToString("N") + ".png") continue;
                            string path = Path.Combine(Root, name);
                            if (File.Exists(path)) paths[id] = path;
                        } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    }
                } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                _paths = paths; _loaded = true;
            }
        }

        // Immutable snapshots make navigation independent of filesystem I/O and saves.
        internal static string Resolve(string id, string original)
        {
            string path;
            return id != null && _paths.TryGetValue(id, out path) ? path : original;
        }

        internal static string Save(string id, byte[] rgba)
        {
            if (!ValidId(id) || rgba == null || rgba.Length != ByteCount) return "Choose a game and upload a fitted cover from Library.";
            lock (Gate) {
                Load();
                string path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".png");
                try {
                    Directory.CreateDirectory(Root);
                    for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;
                    using (var image = Image.LoadPixelData<Rgba32>(CoverImageDecoder.CreateConfiguration(), rgba, Width, Height))
                    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                        image.Save(stream, new PngEncoder()); stream.Flush(true);
                    }
                    AtomicFile.WriteText(Path.Combine(Root, id + ".ref"), Path.GetFileName(path));
                    var next = new Dictionary<string, string>(_paths, StringComparer.OrdinalIgnoreCase);
                    string old = Resolve(id, null); next[id] = path; _paths = next;
                    if (old != null) try { File.Delete(old); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    return null;
                } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) {
                    try { File.Delete(path); } catch { }
                    return "Could not save cover. Check free space and try again.";
                }
            }
        }

        internal static string Restore(string id)
        {
            if (!ValidId(id)) return "Unknown game.";
            lock (Gate) {
                Load();
                try {
                    Directory.CreateDirectory(Root);
                    AtomicFile.WriteText(Path.Combine(Root, id + ".ref"), "");
                    string old = Resolve(id, null);
                    var next = new Dictionary<string, string>(_paths, StringComparer.OrdinalIgnoreCase);
                    next.Remove(id); _paths = next;
                    if (old != null) try { File.Delete(old); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                    return null;
                } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return "Could not restore the original cover. Try again."; }
            }
        }
    }

    internal sealed class Ps4CoverResult
    {
        internal bool Changed;
        internal string Status, Message;
    }

    internal static class Ps4HomeCovers
    {
        static readonly object Gate = new object();
        static readonly string[] MetadataRoots = { "/user/appmeta", "/user/appmeta/external" };
        static string BackupRoot { get { return Path.Combine(AppSettings.DataDir, "custom-covers", "ps4-originals"); } }

        internal static bool ValidTitle(string id)
        {
            return CustomCovers.ValidId(id) && id.StartsWith("CUSA", StringComparison.Ordinal);
        }

        internal static Ps4CoverResult Change(GameHit game, byte[] icon, bool restore)
        {
            if (game == null || !ValidTitle(game.TitleId) || game.Source != "installed")
                return Result(false, "unavailable", "Refresh Library to confirm this game is installed before changing its PS4 icon.");
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                return Result(false, "unavailable", "PS4 homescreen icons can only be changed on the console.");
            if (!restore && (icon == null || icon.Length != CustomCovers.IconByteCount))
                return Result(false, "unavailable", "Reload this phone page and upload the cover again to include the PS4 icon.");

            lock (Gate) {
                int changed = 0, failed = 0;
                for (int slot = 0; slot < MetadataRoots.Length; slot++) {
                    string directory = MetadataRoots[slot] + "/" + game.TitleId;
                    string target = directory + "/icon0.png";
                    if (!File.Exists(target)) continue;
                    try {
                        // Never follow an icon or title-directory link out of the known app metadata roots.
                        if (IsLink(MetadataRoots[slot]) || IsLink(directory) || IsLink(target)) throw new IOException("Linked metadata");
                        string backup = Path.Combine(BackupRoot, game.TitleId + "-" + slot + ".png");
                        ChangeFile(target, backup, icon, restore);
                        changed++;
                    } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException) {
                        failed++;
                        SspiLog.Write("startup", "PS4 cover " + (restore ? "restore" : "apply") + " title=" + game.TitleId + " slot=" + slot + " failed=" + ex.GetType().Name);
                    }
                }
                if (changed == 0)
                    return Result(false, "failed", restore ? "PS4 icon was not restored. Its original backup or installed metadata is unavailable." :
                        "PS4 icon was not changed. Check console storage access, then refresh Library and retry.");
                SspiLog.Write("startup", "PS4 cover " + (restore ? "restored" : "applied") + " title=" + game.TitleId + " copies=" + changed + " failed=" + failed + " shell=restart_required");
                // Itemzflow's CHANGE_ICON uses the same metadata files and requests a
                // system restart. There is no evidenced live invalidation API here.
                return Result(true, failed == 0 ? "restart_required" : "partial", (restore ? "PS4 original icon restored." : "PS4 homescreen icon saved.") +
                    (failed == 0 ? " Restart your PS4 to refresh the homescreen." : " Another metadata copy could not be changed; retry before restarting your PS4."));
            }
        }

        static Ps4CoverResult Result(bool changed, string status, string message)
        { return new Ps4CoverResult { Changed = changed, Status = status, Message = message }; }

        static bool IsLink(string path)
        { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }

        // The caller supplies only installed title paths from MetadataRoots. Keep
        // one immutable original per title/location; repeated uploads never replace it.
        internal static void ChangeFile(string target, string backup, byte[] icon, bool restore)
        {
            if (!File.Exists(target) || IsLink(target)) throw new IOException("Installed icon unavailable");
            byte[] bytes;
            if (restore) {
                bytes = ReadPng(backup);
            } else {
                if (icon == null || icon.Length != CustomCovers.IconByteCount) throw new ArgumentException("Invalid icon dimensions");
                if (!File.Exists(backup)) {
                    byte[] original = ReadPng(target);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    ReplaceBytes(backup, original, false);
                } else ReadPng(backup); // Refuse replacement if the original backup was damaged.
                for (int i = 3; i < icon.Length; i += 4) icon[i] = 255;
                using (var image = Image.LoadPixelData<Rgba32>(CoverImageDecoder.CreateConfiguration(), icon, CustomCovers.IconSize, CustomCovers.IconSize))
                using (var stream = new MemoryStream()) {
                    image.Save(stream, new PngEncoder { BitDepth = PngBitDepth.Bit8, ColorType = PngColorType.Rgb, IgnoreMetadata = true });
                    bytes = stream.ToArray();
                }
            }
            ReplaceBytes(target, bytes, true);
        }

        static byte[] ReadPng(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists || IsLink(path) || info.Length < 33 || info.Length > CoverImageDecoder.MaximumEncodedBytes)
                throw new IOException("Original icon unavailable");
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 33 || bytes.Length > CoverImageDecoder.MaximumEncodedBytes) throw new IOException("Invalid original icon size");
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            for (int i = 0; i < signature.Length; i++) if (bytes[i] != signature[i]) throw new IOException("Invalid original icon");
            if (bytes[12] != 'I' || bytes[13] != 'H' || bytes[14] != 'D' || bytes[15] != 'R' ||
                bytes[16] != 0 || bytes[17] != 0 || bytes[18] != 2 || bytes[19] != 0 ||
                bytes[20] != 0 || bytes[21] != 0 || bytes[22] != 2 || bytes[23] != 0)
                throw new IOException("Original icon must be 512 by 512");
            try {
                using (var image = Image.Load<Rgba32>(CoverImageDecoder.CreateConfiguration(), bytes))
                    if (image.Width != 512 || image.Height != 512 || image.Frames.Count != 1) throw new IOException("Invalid original icon image");
            } catch (Exception ex) when (!(ex is IOException) && !(ex is OutOfMemoryException)) {
                throw new IOException("Original icon is damaged", ex);
            }
            return bytes;
        }

        static void ReplaceBytes(string target, byte[] bytes, bool replace)
        {
            string temp = target + ".sspi-" + Guid.NewGuid().ToString("N") + ".tmp";
            try {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                if (!replace) { File.Move(temp, target); return; }
                try {
                    // ShellUI must be able to read the new inode after atomic rename.
                    if (Chmod(temp, 0x1A4) != 0 || Rename(temp, target) != 0) throw new IOException("Atomic icon replacement failed");
                } catch (DllNotFoundException) { File.Replace(temp, target, null); }
                  catch (EntryPointNotFoundException) { File.Replace(temp, target, null); }
            } finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
        }

        [DllImport("libkernel", EntryPoint = "sceKernelRename", CallingConvention = CallingConvention.Cdecl)]
        static extern int Rename(string source, string destination);
        [DllImport("libkernel", EntryPoint = "sceKernelChmod", CallingConvention = CallingConvention.Cdecl)]
        static extern int Chmod(string path, int mode);
    }
}
