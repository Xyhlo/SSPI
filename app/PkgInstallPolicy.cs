using System;

namespace Orbis
{
    /// <summary>Pure package-kind decisions shared by native install paths and tests.</summary>
    internal static class PkgInstallPolicy
    {
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
