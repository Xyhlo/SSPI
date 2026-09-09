namespace Orbis
{
    internal static class AppSettings
    {
        public const string DataDir = "/data/GameSearch";
    }

    internal static class PkgInstaller
    {
        public static bool TryGetTitleId(string path, out string titleId, out string error)
        {
            titleId = "";
            error = "Native title parser is owned by Game Search";
            return false;
        }
    }
}
