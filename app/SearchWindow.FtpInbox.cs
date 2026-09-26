using System;
using System.Collections.Generic;
using SDL2.Types;
using static SDL2.SDL;

namespace Orbis
{
    public partial class SearchWindow
    {
        void StartFtpInbox()
        {
            FtpInbox.Start(
                name => User.NotifyToast(Clip("FTP transfer detected: " + name, 160)),
                HandOffFtpInbox,
                () => { lock (_lock) Invalidated = true; },
                name => User.NotifyToast(Clip("FTP upload sent to background installation: " + name, 160)));
        }

        // Runs on the inbox thread with the PKGs, ZIP or 7z archives and first RAR
        // volumes this app has claimed, at most 256 per call (FtpInbox.HandoffBatch).
        // Completed uploads use the same local queue as USB installs; RAR volumes are
        // grouped into one row per archive set. Background installs read inbox files
        // from internal staging, so the selected install mode applies.
        bool HandOffFtpInbox(List<string> paths)
        {
            var manager = _dlMgr;
            if (manager == null) return false;
            List<string> sets = LocalInstallSource.FirstVolumes(paths);
            var queue = new List<string>();
            var notes = new List<string>();
            foreach (string path in sets)
            {
                string set; int index;
                string name = System.IO.Path.GetFileName(path);
                // Continuation volumes are added with their first volume.
                if (ArchiveVolumeSet.TryIndex(name, out set, out index) && index != 1) continue;
                if (name.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
                {
                    // Install state is read now, not taken from an older queue row.
                    string conflict = null; bool installed = false;
                    try { conflict = DownloadManager.LocalInstallConflict(LocalInstallSource.Read(path, false), out installed); }
                    catch { conflict = null; } // QueueLocalFiles reports unreadable packages.
                    if (conflict != null)
                    {
                        notes.Add(installed ? name + " is already installed" : "PS4 is already installing " + name + "; SSPI skipped it");
                        continue;
                    }
                }
                queue.Add(path);
            }
            int queued = 0; string error = null;
            if (queue.Count > 0)
            {
                queued = manager.QueueLocalFiles(queue, out error);
                // A queue that could not be saved changed nothing; retry the upload later.
                if (queued == 0 && error != null && error.StartsWith("USB queue could not be saved", StringComparison.Ordinal)) return false;
                if (queued > 0) manager.AssignLocalArchiveTitles(queue);
            }
            string message = queue.Count == 0 ? "" : queued == 0 ? "FTP files could not be queued." :
                queued + (queued == 1 ? " FTP file queued." : " FTP files queued.");
            if (!string.IsNullOrEmpty(error)) message += " " + error;
            foreach (string note in notes) message += (message.Length > 0 ? " " : "") + note + ".";
            if (message.Length > 0) User.NotifyToast(Clip(message, 160));
            lock (_lock) Invalidated = true;
            return true;
        }

        static string FtpMegabytes(double bytes)
        { return (bytes / 1048576.0).ToString(bytes >= 104857600 ? "0" : "0.0") + " MB"; }

        // Draws the live FTP section above the queue and returns the next row y.
        int DrawFtpInboxSection(IntPtr renderer, int y)
        {
            FtpInbox.Snapshot ftp = FtpInbox.Current;
            if (ftp == null || ftp.Files == 0) return y;
            var strip = new SDL_Rect { x = ContentX, y = y, w = ContentWidth, h = 96 };
            SoftRect(renderer, strip, Panel);
            DesignIcon(renderer, "chevron", strip.x + 24, y + 20, 22, Accent);
            TextPx(renderer, strip.x + 58, y + 16, 24, "FTP", White);
            int column = Math.Max(160, (strip.w - 380) / 3);
            string[] labels = { "Files", "Received", "Speed" };
            string[] values = { ftp.Files.ToString(), FtpMegabytes(ftp.ReceivedBytes),
                (ftp.BytesPerSecond / 1048576.0).ToString("0.0") + " MB/s" };
            for (int i = 0; i < 3; i++)
            {
                int x = strip.x + 170 + i * column;
                TextPx(renderer, x, y + 14, 17, labels[i], Muted);
                TextFit(renderer, x, y + 38, 22, column - 20, values[i], White);
            }
            string names = ftp.Names.Length == 0 ? "" : string.Join("  ·  ", ftp.Names);
            string state = ftp.BytesPerSecond > 0 ? "Receiving" :
                ftp.Background ? "Background installation starts when the upload finishes" : "Waiting for the upload to finish";
            TextFit(renderer, strip.x + 58, y + 66, 17, strip.w - 90, state + "  ·  " + names, Dim);
            return y + strip.h + 20;
        }
    }
}
