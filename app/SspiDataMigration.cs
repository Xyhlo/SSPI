using System;
using System.IO;
using System.Diagnostics;
using System.Text;

namespace Orbis
{
    internal static class SspiDataMigration
    {
        internal const string LegacyRoot = "/data/GameSearch";
        internal const string CurrentRoot = "/data/SSPI";
        internal static string Notice = "";
        internal static bool Pending;
        static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        static readonly object Gate = new object();
        static string LastOldRoot, LastNewRoot;
        static string Operation, OperationPath;

        internal static string PreparePrimary()
        {
            string legacy = Directory.Exists(LegacyRoot) ? LegacyRoot : "/user/data/GameSearch";
            return Prepare(legacy, CurrentRoot);
        }

        internal static bool IsLive(string root, int waitMilliseconds = 1600)
        {
            if (string.IsNullOrEmpty(root)) return false;
            string resident = Path.Combine(root, "resident");
            string heartbeat = Path.Combine(root, "resident", "heartbeat.txt");
            FileAttributes rootAttributes;
            try
            {
                rootAttributes = File.GetAttributes(root);
                if ((rootAttributes & FileAttributes.Directory) == 0) return false;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch { return true; }

            FileAttributes residentAttributes;
            try
            {
                residentAttributes = File.GetAttributes(resident);
                if ((residentAttributes & FileAttributes.Directory) == 0) return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch { return true; }

            FileAttributes heartbeatAttributes;
            try
            {
                heartbeatAttributes = File.GetAttributes(heartbeat);
                if ((heartbeatAttributes & FileAttributes.Directory) != 0) return true;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch { return true; }

            try
            {
                string first = File.ReadAllText(heartbeat);
                bool processKnown;
                return IsHeartbeatProcessLive(first, out processKnown) || !processKnown;
            }
            catch { return true; }
        }

        static bool IsHeartbeatProcessLive(string contents, out bool processKnown)
        {
            string[] lines = (contents ?? "").Split('\n');
            processKnown = false;
            if (lines.Length < 3) return true;
            int processId = 0;
            foreach (string field in lines[2].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!field.StartsWith("pid=", StringComparison.Ordinal)) continue;
                if (!int.TryParse(field.Substring(4), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out processId) || processId <= 0)
                    return true;
                processKnown = true;
                break;
            }
            // Older native heartbeats have only version and timestamp lines. They
            // carry no process identity, so age alone cannot prove the writer stopped.
            if (!processKnown) return true;
            try
            {
                using (Process process = Process.GetProcessById(processId)) return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch { return true; }
        }

        internal static string Prepare(string oldRoot, string newRoot, bool checkWorker = true)
        {
            lock (Gate)
            {
                LastOldRoot = oldRoot; LastNewRoot = newRoot;
                return PrepareLocked(oldRoot, newRoot, checkWorker);
            }
        }

        internal static string RetryPending()
        {
            lock (Gate)
                return Pending && LastOldRoot != null && LastNewRoot != null
                    ? PrepareLocked(LastOldRoot, LastNewRoot, true) : LastNewRoot ?? CurrentRoot;
        }

        static void At(string operation, string path) { Operation = operation; OperationPath = path; }

        static string PrepareLocked(string oldRoot, string newRoot, bool checkWorker)
        {
            Pending = true; Notice = "Checking data migration at " + newRoot + "; existing files are retained.";
            try
            {
                At("inspect legacy storage", oldRoot);
                bool exists = Directory.Exists(oldRoot);
                if (!exists && !File.Exists(Path.Combine(newRoot, ".migration-active")))
                { Pending = false; Notice = ""; return newRoot; }
                if (exists) RequirePlain(oldRoot);
                if (exists && checkWorker && IsLive(oldRoot))
                {
                    Pending = true;
                    Notice = "An older resident may still be active. SSPI will keep using its existing data location until the worker is confirmed stopped. New plugin files can still be staged.";
                    SspiLog.Write("startup", "data_migration deferred_old_resident_or_uncertain");
                    return oldRoot;
                }
                At("create destination", newRoot);
                Directory.CreateDirectory(newRoot); RequirePlain(newRoot);
                string marker = Path.Combine(newRoot, ".migration-active");
                At("write migration marker", marker);
                File.WriteAllText(marker, "SSPI data migration\n");
                if (exists) Merge(oldRoot, newRoot);
                RewriteTree(newRoot, oldRoot, newRoot);
                At("retire empty legacy folder", oldRoot);
                if (Directory.Exists(oldRoot) && Directory.GetFileSystemEntries(oldRoot).Length == 0) Directory.Delete(oldRoot, false);
                At("finish migration", marker);
                File.Delete(marker);
                Notice = Directory.Exists(oldRoot) ? "SSPI data moved. Conflicting legacy files were retained for review; no files were overwritten." : "Settings and stored files moved to SSPI.";
                SspiLog.Write("startup", "data_migration completed legacy_remaining=" + Directory.Exists(oldRoot));
                // Old diagnostics are housekeeping, not a prerequisite for launching
                // the resident. No compression assembly is needed during startup.
                try { ArchiveOldDiagnostics(newRoot); }
                catch (Exception ex)
                {
                    SspiLog.Write("startup", "legacy_diagnostics deferred files_retained=1 operation=" + Operation +
                        " path=" + OperationPath + " error=" + ex.GetType().Name + " code=0x" + ex.HResult.ToString("X8"));
                }
                Pending = false;
                return newRoot;
            }
            catch (Exception ex)
            {
                Pending = true;
                Notice = "Data migration needs attention: " + Operation + " at " + SspiLog.Clean(OperationPath) +
                    "; files retained. " + ex.GetType().Name + " 0x" + ex.HResult.ToString("X8");
                var missing = ex as FileNotFoundException;
                SspiLog.Write("startup", "data_migration paused operation=" + Operation + " path=" + OperationPath +
                    " missing=" + (missing == null ? "" : missing.FileName) + " error=" + ex.GetType().Name + " code=0x" + ex.HResult.ToString("X8") + " stack=" + ex.StackTrace);
                // Some files may already have moved. Keep the new root and the marker;
                // the next launch resumes the same non-overwriting merge.
                return newRoot;
            }
        }

        static void RequirePlain(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked migration paths are not supported");
        }

        static void Merge(string source, string destination)
        {
            At("merge folder", source);
            RequirePlain(source); Directory.CreateDirectory(destination); RequirePlain(destination);
            foreach (string file in Directory.GetFiles(source))
            {
                At("move legacy file", file);
                RequirePlain(file);
                string target = Path.Combine(destination, Path.GetFileName(file));
                if (!File.Exists(target) && !Directory.Exists(target)) File.Move(file, target);
            }
            foreach (string folder in Directory.GetDirectories(source))
            {
                At("merge legacy folder", folder);
                RequirePlain(folder);
                string target = Path.Combine(destination, Path.GetFileName(folder));
                if (File.Exists(target)) continue;
                Merge(folder, target);
                if (Directory.GetFileSystemEntries(folder).Length == 0) Directory.Delete(folder, false);
            }
        }

        internal static string Rewrite(string text, string oldRoot, string newRoot)
        {
            // IPC paths are base64 fields; JSON manifests/settings use ordinary paths.
            string[] lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string value = lines[i].TrimEnd('\r');
                if (value.Length > 0 && value.Length <= 8192 && value.Length % 4 == 0)
                {
                    try
                    {
                        string decoded = StrictUtf8.GetString(Convert.FromBase64String(value));
                        if (decoded == oldRoot || decoded.StartsWith(oldRoot + "/", StringComparison.Ordinal))
                            lines[i] = Convert.ToBase64String(Encoding.UTF8.GetBytes(newRoot + decoded.Substring(oldRoot.Length))) + (lines[i].EndsWith("\r") ? "\r" : "");
                    }
                    catch { }
                }
            }
            text = string.Join("\n", lines);
            text = text.Replace(oldRoot + "/", newRoot + "/");
            text = text.Replace(oldRoot + "\"", newRoot + "\"");
            text = text.Replace(oldRoot.Replace("/", "\\/") + "\\/", newRoot.Replace("/", "\\/") + "\\/");
            return text;
        }

        static void RewriteTree(string root, string oldRoot, string newRoot)
        {
            At("inspect stored paths", root);
            foreach (string file in Directory.GetFiles(root))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext != ".json" && ext != ".ini" && ext != ".txt" && ext != ".job" && ext != ".status" && ext != ".control" && ext != ".resume") continue;
                At("update stored paths", file);
                RequirePlain(file);
                if (new FileInfo(file).Length > 16 * 1024 * 1024) continue;
                string original;
                try { original = StrictUtf8.GetString(File.ReadAllBytes(file)); } catch (DecoderFallbackException) { continue; }
                string updated = Rewrite(original, oldRoot, newRoot);
                if (original == updated) continue;
                // AtomicFile uses native rename on console, preserving the old file
                // until the complete replacement is ready.
                AtomicFile.WriteText(file, updated);
            }
            foreach (string folder in Directory.GetDirectories(root))
            {
                At("inspect stored folder", folder);
                RequirePlain(folder);
                if (Path.GetFileName(folder) != "logs") RewriteTree(folder, oldRoot, newRoot);
            }
        }

        static bool IsOldDiagnostic(string path)
        {
            string name = Path.GetFileName(path);
            return name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".log.1", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".log.previous", StringComparison.OrdinalIgnoreCase) || name == "native-download-metrics.txt" || name == "managed-download-metrics.txt";
        }

        static void ArchiveOldDiagnostics(string root)
        {
            var files = new System.Collections.Generic.List<string>();
            CollectDiagnostics(root, files);
            if (files.Count == 0) return;
            string state = Path.Combine(root, "state");
            At("preserve old diagnostics", state);
            Directory.CreateDirectory(state); RequirePlain(state);
            string archive = Path.Combine(state, "legacy-diagnostics-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(archive);
            int moved = 0;
            foreach (string file in files)
            {
                At("preserve old diagnostic", file);
                try
                {
                    RequirePlain(file);
                    string target = Path.Combine(archive, file.Substring(root.Length + 1));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Move(file, target); moved++;
                }
                catch (FileNotFoundException) { SspiLog.Write("startup", "legacy_diagnostic disappeared path=" + file); }
                catch (DirectoryNotFoundException) { SspiLog.Write("startup", "legacy_diagnostic disappeared path=" + file); }
            }
            SspiLog.Write("startup", "legacy_diagnostics preserved count=" + moved + " share=logs/combined.log");
        }

        static void CollectDiagnostics(string root, System.Collections.Generic.List<string> files)
        {
            At("find old diagnostics", root);
            foreach (string file in Directory.GetFiles(root)) if (IsOldDiagnostic(file)) { RequirePlain(file); files.Add(file); }
            foreach (string folder in Directory.GetDirectories(root))
                if (Path.GetFileName(folder) != "logs" && Path.GetFileName(folder) != "state") { RequirePlain(folder); CollectDiagnostics(folder, files); }
        }
    }
}
