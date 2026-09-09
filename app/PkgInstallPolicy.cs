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

        public static bool IsBaseBgftSubType(int subType)
        {
            return subType == 6;
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
