using System;

namespace Orbis
{
    /// <summary>Pure package-kind decisions shared by native install paths and tests.</summary>
    internal static class PkgInstallPolicy
    {
        // Final installed containers must match the validated input, not merely
        // a base title with the same APP_VER. Staging/preallocated files don't count.
        internal static bool MatchesInstalledContainer(string source, string installed)
        {
            try
            {
                if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(installed)) return false;
                using (var a = System.IO.File.OpenRead(source))
                using (var b = System.IO.File.OpenRead(installed))
                {
                    if (a.Length < 4096 || a.Length != b.Length) return false;
                    var left = new byte[4096]; var right = new byte[4096];
                    if (a.Read(left, 0, left.Length) != left.Length || b.Read(right, 0, right.Length) != right.Length) return false;
                    if (left[0] != 0x7f || left[1] != 'C' || left[2] != 'N' || left[3] != 'T') return false;
                    for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
                    a.Position = a.Length - left.Length; b.Position = b.Length - right.Length;
                    if (a.Read(left, 0, left.Length) != left.Length || b.Read(right, 0, right.Length) != right.Length) return false;
                    for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
                    return true;
                }
            }
            catch { return false; }
        }

        public static bool RequiresInstalledBase(PkgContentKind kind)
        {
            return kind == PkgContentKind.Patch || kind == PkgContentKind.AddOn;
        }

        public static bool UseStorageBgft(PkgContentKind kind)
        {
            // Storage BGFT identifies an update by the already-installed base title and can
            // return SAME_APPLICATION_ALREADY_INSTALLED. AppInstUtil reads the local PKG's
            // own patch/add-on identity and installs it against that base correctly.
            return kind == PkgContentKind.BaseGame;
        }

        internal struct BgftStartProgress
        {
            internal bool Readable;
            internal int Error;
            internal int Preparing, Copy;
            internal ulong Done, Total;
        }

        // A rejected start is recoverable only with evidence from this owned task.
        // A readable, idle task (or stale nonzero counters) is not an acknowledgement.
        internal static bool TryStartBgftTask(Func<int> start, Func<int> internalStart,
            Func<BgftStartProgress> progress, Action<int> delay, out string detail)
        {
            int publicRc = start();
            if (publicRc == 0) { detail = "start=public rc=0"; return true; }
            int internalRc = publicRc;
            bool internalAvailable = true;
            try { internalRc = internalStart(); }
            catch (EntryPointNotFoundException) { internalAvailable = false; }
            if (internalAvailable && internalRc == 0)
            { detail = "start=internal public=" + DescribeBgftError(publicRc) + " rc=0"; return true; }
            detail = "start=unconfirmed public=" + DescribeBgftError(publicRc) +
                " internal=" + (internalAvailable ? DescribeBgftError(internalRc) : "unavailable");
            BgftStartProgress prior = new BgftStartProgress();
            for (int probe = 0; probe <= 10; probe++)
            {
                if (probe != 0) delay(500);
                BgftStartProgress current = progress();
                if (!current.Readable) continue;
                if (current.Error != 0) { detail += " progress=" + DescribeBgftError(current.Error); return false; }
                bool complete = current.Total > 0 && current.Done >= current.Total && current.Copy >= 100;
                bool advanced = prior.Readable && (current.Done > prior.Done ||
                    current.Preparing > prior.Preparing || current.Copy > prior.Copy);
                if (complete || advanced) { detail += " acknowledged=progress"; return true; }
                prior = current;
            }
            detail += " acknowledgement-timeout";
            return false;
        }

        internal static string DescribeBgftError(int code)
        {
            string reason;
            switch (unchecked((uint)code))
            {
                case 0: reason = "success"; break;
                case 0x80990001: reason = "service already initialized"; break;
                case 0x80990004: reason = "BGFT rejected the registration arguments"; break;
                case 0x80990015: reason = "duplicate task"; break;
                case 0x80990019: reason = "task not found"; break;
                case 0x8099002C: reason = "PS4 could not receive the package data (HTTP receive error)"; break;
                case 0x80990086: reason = "content already queued"; break;
                case 0x80990088: reason = "installed content conflict"; break;
                case 0x80F00633: reason = "NP environment rejected registration"; break;
                case 0x80991404: reason = "package source unavailable"; break;
                case 0x80020012: reason = "cross-device path rejected"; break;
                default: reason = "system error"; break;
            }
            return "0x" + unchecked((uint)code).ToString("X8") + " (" + reason + ")";
        }

        internal static bool IsBgftDuplicate(int code)
        {
            return unchecked((uint)code) == 0x80990015u || unchecked((uint)code) == 0x80990086u;
        }

        // A task registered against another URL cannot be attached to this new
        // package. Retire only a failed task whose current identity and durable
        // ownership both match, then let the caller register the current route.
        internal static bool TryRetireFailedBgftTask(Func<int> find, Func<int, bool> owned,
            Func<int, BgftStartProgress> progress, Func<int, int> stop,
            Func<int, int> unregister, Action<int> release, out string detail)
        {
            int task = find();
            if (task < 0 || !owned(task))
            { detail = "Existing PS4 download is not owned by this installation"; return false; }
            BgftStartProgress state = progress(task);
            if (!state.Readable || state.Error == 0)
            { detail = "Existing PS4 download is active or its state is unconfirmed"; return false; }
            if (find() != task || !owned(task))
            { detail = "Existing PS4 download changed during recovery"; return false; }
            int stopRc = stop(task), removeRc = unregister(task);
            if (removeRc != 0 && unchecked((uint)removeRc) != 0x80990019u)
            {
                detail = "Failed owned task retained: stop=" + DescribeBgftError(stopRc) +
                    " unregister=" + DescribeBgftError(removeRc);
                return false;
            }
            release(task);
            detail = "Retired failed owned task " + task;
            return true;
        }

        public static bool IsBaseBgftSubType(int subType)
        {
            return subType == 6;
        }

        internal static bool CompleteBgftPayload(long expected, long done, long total, long tolerance)
        {
            if (expected <= 0 || done <= 0 || total <= 0 || tolerance < 0) return false;
            long minimum = expected > tolerance ? expected - tolerance : 0;
            long maximum = expected > long.MaxValue - tolerance ? long.MaxValue : expected + tolerance;
            return total >= minimum && total <= maximum && done >= total &&
                done >= minimum && done <= maximum;
        }

        public static bool IsAddonOrPatchName(string kind)
        {
            if (string.IsNullOrEmpty(kind)) return false;
            string k = kind.Trim();
            return string.Equals(k, "dlc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, "addon", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, "add-on", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, "update", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, "patch", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(k, "backport", StringComparison.OrdinalIgnoreCase);
        }
    }
}
