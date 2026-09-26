using System;
using System.IO;
using System.Text;

namespace Orbis
{
    internal sealed class AppSettings
    {
        public const string RdLocationAuto = "auto";
        public const string RdLocationUs = "us";
        public const string RdLocationEu = "eu";

        public const string BackgroundAdaptive = "adaptive";
        public const string BackgroundSolid = "solid";
        public const string BackgroundBlack = "black";
        public const string BackgroundImage = "image";
        public const string BackgroundShowcase = "showcase";
        public const string BgQualityPerformance = "performance";
        public const string BgQualityQuality = "quality";
        public const string CloudDensityFew = "few";
        public const string CloudDensityNormal = "normal";
        public const string CloudDensityDense = "dense";
        public const string StyleGlass = "glass";
        public const string StyleFlat = "flat";
        public const string EtaShort = "short";
        public const string EtaClock = "clock";

        public int ConnectionMbps;
        public string StagingLocation = "ps4";
        static string _stagingLocation = "ps4";
        internal static bool ValidStaging(string value)
        { return value == "ps4" || (value != null && value.Length == 9 && value.StartsWith("/mnt/usb", StringComparison.Ordinal) && value[8] >= '0' && value[8] <= '7'); }
        internal static string StagingRoot(string location)
        { return location == "ps4" ? Path.Combine(DataDir, "downloads") : Path.Combine(location, "SSPI", "staging"); }
        internal static void RequireStaging(string directory)
        {
            string normalized = directory.Replace('\\', '/');
            if (normalized.StartsWith("/mnt/usb", StringComparison.Ordinal))
            {
                string mount = normalized.Length >= 9 ? normalized.Substring(0, 9) : "";
                string detail;
                if (!UsbVolumeLabel.IsConnected(mount, out detail)) { SspiLog.Write("download", "usb-storage " + detail); throw new IOException(detail + "; files are retained"); }
                for (string p = mount; p != null && normalized.StartsWith(p, StringComparison.Ordinal);)
                {
                    if (Directory.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked staging folders are not supported");
                    if (p == normalized) break;
                    int next = normalized.IndexOf('/', p.Length + 1);
                    p = next < 0 ? normalized : normalized.Substring(0, next);
                }
            }
            Directory.CreateDirectory(directory);
        }
        internal bool SelectStaging(string location, out string error)
        {
            error = null;
            if (!ValidStaging(location)) { error = "Choose PS4 or a connected USB drive"; return false; }
            string old = StagingLocation;
            try
            {
                string root = StagingRoot(location); RequireStaging(root);
                string probe = Path.Combine(root, ".sspi-write-" + Guid.NewGuid().ToString("N"));
                File.WriteAllText(probe, "1"); File.Delete(probe);
                StagingLocation = location;
                if (!Save()) throw new IOException("Could not save staging location");
                _stagingLocation = location; return true;
            }
            catch (Exception ex) { StagingLocation = old; error = ex.Message; return false; }
        }
        public string RealDebridToken = "";
        public bool UseRealDebrid = true;
        /// <summary>
        /// Preferred Real-Debrid CDN location. The RD API normally returns one
        /// IP-selected URL, so US/EU is best-effort when alternatives exist.
        /// </summary>
        public string RealDebridLocation = RdLocationAuto;
        public string DeepbridApiKey = "";
        public string AllDebridApiKey = "";
        public string PremiumizeApiKey = "";
        public string TorBoxApiKey = "";
        public bool ShowFirmwareHints = true;
        public bool BackgroundMusic = true;
        public bool InterfaceSounds = true;
        public int AudioVolume = 35;
        public string UnlockProviderId = UnlockProviders.RealDebridId;
        public bool UseUnlockProvider = true;
        // Null preserves the legacy single-provider choice until the first checkbox change.
        public string EnabledUnlockProviders;
        public string ApiBaseUrl = "";
        public bool UseLanProxy = false;
        public bool ForceProxy = false;
        public string ProxyBaseUrl = "";
        public string ProxyKey = "game-search-lan";
        /// <summary>0=size+speed+ETA, 1=size only, 2=size+speed, 3=size+ETA</summary>
        public int DownloadStatsMode = 0;
        /// <summary>Automatic bounded connections; each resolved provider link supplies its allowed limit.</summary>
        public int DownloadRangeCount = DownloadTransferSettings.DefaultRangeCount;
        /// <summary>Downloads terminal + speed graph (technical, no secrets).</summary>
        public bool NerdStats = false;
        public bool RetrySourceArchivePasswords = true;
        /// <summary>
        /// Validated local package URLs register with system BGFT (PS4 notifications).
        /// </summary>
        public bool UseBgftDirect = true;

        // UI V2 defaults must look complete without first-run setup.
        public string Accent = ThemePalette.DefaultAccentHex;
        public string AccentName = ThemePalette.DefaultAccentName;
        public string BackgroundMode = BackgroundSolid;
        public string BackgroundImagePath = "";
        public string BackgroundQuality = BgQualityPerformance;
        public bool CoverCloud = false;
        public string CoverCloudDensity = CloudDensityNormal;
        public string UiStyle = StyleGlass;
        public bool ReduceMotion = false;
        public bool SearchRecents = true;
        public bool SearchContinue = true;
        public string EtaFormat = EtaShort;

        /// <summary>One-shot load/save correction message suitable for a toast.</summary>
        public string SettingsNotice = "";

        static string _dataDir;
        static bool _dataDirResolved;
        static bool _dataDirWritable;
        static string _dataDirError = "";
        public static bool DataMigrationPending { get { var _ = DataDir; return SspiDataMigration.Pending; } }
        public static string DataMigrationNotice { get { var _ = DataDir; return SspiDataMigration.Notice; } }

        internal static bool RetryDataMigration()
        {
            lock (typeof(AppSettings))
            {
                var existing = DataDir;
                if (!SspiDataMigration.Pending) return true;
                string root = SspiDataMigration.RetryPending();
                ResolveDataDirectory(new[] { root });
                return !SspiDataMigration.Pending && _dataDirWritable;
            }
        }

        public static string DataDir
        {
            get
            {
                if (_dataDirResolved) return _dataDir;
                lock (typeof(AppSettings))
                {
                    if (_dataDirResolved) return _dataDir;
                    string[] candidates =
                    {
                        SspiDataMigration.PreparePrimary(),
                        "/user/data/SSPI",
                        Path.Combine(SafeAppBase(), "data")
                    };
                    return ResolveDataDirectory(candidates);
                }
            }
        }

        public static bool DataDirWritable { get { var _ = DataDir; return _dataDirWritable; } }
        public static string DataDirError { get { var _ = DataDir; return _dataDirError; } }

        internal static string ResolveDataDirectory(string[] candidates)
        {
            _dataDirWritable = false;
            _dataDirError = "";
            foreach (string candidate in candidates)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathRooted(candidate))
                        throw new IOException("Application storage requires an absolute path");
                    Directory.CreateDirectory(candidate);
                    string probe = Path.Combine(candidate, ".sspi-write-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        using (var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            stream.WriteByte(1);
                    }
                    finally { if (File.Exists(probe)) File.Delete(probe); }
                    _dataDir = candidate;
                    _dataDirWritable = true;
                    _dataDirResolved = true;
                    _dataDirError = "";
                    return _dataDir;
                }
                catch (Exception ex)
                {
                    string reason = ex.GetType().Name + " 0x" + ex.HResult.ToString("X8");
                    if (_dataDirError.Length == 0)
                        _dataDirError = "SSPI storage is unavailable at " + candidate + " (" + reason + "). Check folder access; existing files are retained.";
                    SspiLog.Write("startup", "storage_probe_failed path=" + candidate + " error=" + reason + " stack=" + ex.StackTrace);
                }
            }
            // Keep a useful absolute path when storage is unavailable. A relative
            // fallback targets the read-only application mount and hides the fault.
            _dataDir = SspiDataMigration.CurrentRoot;
            _dataDirResolved = true;
            if (_dataDirError.Length == 0) _dataDirError = "SSPI storage is unavailable at " + _dataDir + ".";
            return _dataDir;
        }

        public static string SettingsPath { get { return Path.Combine(DataDir, "settings.ini"); } }

        public static string DownloadDir
        {
            get
            {
                string d = StagingRoot(_stagingLocation);
                if (_stagingLocation == "ps4") try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        public static string CoversDir
        {
            get
            {
                string d = Path.Combine(DataDir, "covers");
                try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
                return d;
            }
        }

        static string SafeAppBase()
        {
            try { return Orbis.Internals.IO.GetAppBaseDirectory() ?? "."; }
            catch { return "."; }
        }

        public void Load()
        {
            RetrySourceArchivePasswords = true;
            try
            {
                int schedulerVersion = 0;
                // Migrate token from alternate data dirs if primary has no settings
                string path = SettingsPath;
                if (!File.Exists(path))
                {
                    string[] alt =
                    {
                        "/data/SSPI/settings.ini",
                        "/user/data/SSPI/settings.ini",
                        "/data/GameSearch/settings.ini",
                        "/user/data/GameSearch/settings.ini",
                        Path.Combine(SafeAppBase(), "data", "settings.ini")
                    };
                    foreach (var a in alt)
                    {
                        try
                        {
                            if (File.Exists(a) && !string.Equals(a, path, StringComparison.OrdinalIgnoreCase))
                            {
                                File.Copy(a, path, true);
                                break;
                            }
                        }
                        catch { }
                    }
                }
                if (!File.Exists(path)) return;
                foreach (var raw in File.ReadAllLines(path))
                {
                    string line = (raw ?? "").Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    if (Eq(key, "firmware_hints")) ShowFirmwareHints = IsTrue(val);
                    else if (Eq(key, "background_music")) BackgroundMusic = IsTrue(val);
                    else if (Eq(key, "interface_sounds")) InterfaceSounds = IsTrue(val);
                    else if (Eq(key, "audio_volume")) { int volume; if (int.TryParse(val, out volume)) AudioVolume = Math.Max(0, Math.Min(100, volume)); }
                    else if (Eq(key, "connection_mbps")) { int speed; if (int.TryParse(val, out speed)) ConnectionMbps = Math.Max(0, Math.Min(10000, speed)); }
                    else if (Eq(key, "rd_token")) RealDebridToken = val;
                    else if (Eq(key, "use_rd")) UseRealDebrid = IsTrue(val);
                    else if (Eq(key, "rd_location") || Eq(key, "rd_cdn"))
                        RealDebridLocation = NormalizeRdLocation(val);
                    else if (Eq(key, "db_key") || Eq(key, "deepbrid_key")) DeepbridApiKey = val;
                    else if (Eq(key, "ad_key") || Eq(key, "alldebrid_key")) AllDebridApiKey = val;
                    else if (Eq(key, "pm_key") || Eq(key, "premiumize_key")) PremiumizeApiKey = val;
                    else if (Eq(key, "tb_key")) TorBoxApiKey = val;
                    else if (Eq(key, "unlock_provider")) UnlockProviderId = val;
                    else if (Eq(key, "use_unlock")) UseUnlockProvider = IsTrue(val);
                    else if (Eq(key, "enabled_unlock_providers")) EnabledUnlockProviders = UnlockProviders.NormalizeIds(val);
                    else if (Eq(key, "api_base")) ApiBaseUrl = val;
                    else if (Eq(key, "use_proxy")) UseLanProxy = IsTrue(val);
                    else if (Eq(key, "force_proxy")) ForceProxy = IsTrue(val);
                    else if (Eq(key, "proxy_base")) ProxyBaseUrl = val;
                    else if (Eq(key, "proxy_key")) ProxyKey = val;
                    else if (Eq(key, "dl_stats"))
                    {
                        int m;
                        if (int.TryParse(val, out m) && m >= 0 && m <= 3) DownloadStatsMode = m;
                    }
                    else if (Eq(key, "download_range_count"))
                    {
                        int count;
                        if (int.TryParse(val, out count))
                            DownloadRangeCount = DownloadTransferSettings.ClampRangeCount(count);
                    }
                    else if (Eq(key, "download_scheduler_version")) int.TryParse(val, out schedulerVersion);
                    else if (Eq(key, "bgft_direct") || Eq(key, "use_bgft"))
                        UseBgftDirect = IsTrue(val);
                    else if (Eq(key, "nerd_stats") || Eq(key, "stats_for_nerds"))
                        NerdStats = IsTrue(val);
                    else if (Eq(key, "retry_source_archive_passwords"))
                        RetrySourceArchivePasswords = IsTrue(val);
                    else if (Eq(key, "staging_location")) StagingLocation = ValidStaging(val) ? val : "ps4";
                    else if (Eq(key, "accent")) Accent = val;
                    else if (Eq(key, "accent_name")) AccentName = val;
                    else if (Eq(key, "bg_mode")) BackgroundMode = val;
                    else if (Eq(key, "bg_image")) BackgroundImagePath = val;
                    else if (Eq(key, "bg_quality")) BackgroundQuality = val;
                    else if (Eq(key, "cloud")) CoverCloud = ReadToggle(val, CoverCloud);
                    else if (Eq(key, "cloud_density")) CoverCloudDensity = val;
                    else if (Eq(key, "style")) UiStyle = val;
                    else if (Eq(key, "reduce_motion")) ReduceMotion = ReadToggle(val, ReduceMotion);
                    else if (Eq(key, "search_recents")) SearchRecents = ReadToggle(val, SearchRecents);
                    else if (Eq(key, "search_continue")) SearchContinue = ReadToggle(val, SearchContinue);
                    else if (Eq(key, "eta_format")) EtaFormat = val;
                }
                // Migrate legacy use_rd into Link Service selection.
                // Earlier releases always persisted their lane default (3, 4, 8,
                // then 5 and 10) even when the user never changed it. Move those
                // defaults to the current 25-connection allowance and preserve an
                // intentional single-lane mode.
                if ((schedulerVersion < 2 && DownloadRangeCount == 3) ||
                    (schedulerVersion < 3 && DownloadRangeCount == 4) ||
                    (schedulerVersion < 4 && DownloadRangeCount == 8) ||
                    (schedulerVersion < 6 && (DownloadRangeCount == 5 || DownloadRangeCount == 10)))
                    DownloadRangeCount = DownloadTransferSettings.DefaultRangeCount;
                if (string.IsNullOrEmpty(UnlockProviderId))
                    UnlockProviderId = UseRealDebrid ? UnlockProviders.RealDebridId : UnlockProviders.NoneId;
                if (EnabledUnlockProviders == null && !UseRealDebrid && string.Equals(UnlockProviderId, UnlockProviders.RealDebridId, StringComparison.OrdinalIgnoreCase))
                    UseUnlockProvider = false;
                if (UseLanProxy) ForceProxy = true;
                _stagingLocation = StagingLocation;
                ValidateAppearance(true);
                ApplyToNetHttp();
            }
            catch { }
        }

        public bool Save()
        {
            try
            {
                var sb = new StringBuilder();
                DownloadRangeCount = DownloadTransferSettings.ClampRangeCount(DownloadRangeCount);
                ValidateAppearance(false);
                // Keep the Link Service selection and RD flag in sync for legacy readers.
                if (string.Equals(UnlockProviderId, UnlockProviders.RealDebridId, StringComparison.OrdinalIgnoreCase))
                    UseRealDebrid = UseUnlockProvider && HasRealDebrid;
                else
                    UseRealDebrid = false;

                sb.AppendLine("# SSPI settings");
                sb.AppendLine("connection_mbps=" + ConnectionMbps);
                sb.AppendLine("rd_token=" + (RealDebridToken ?? ""));
                sb.AppendLine("use_rd=" + (UseRealDebrid ? "1" : "0"));
                sb.AppendLine("rd_location=" + NormalizeRdLocation(RealDebridLocation));
                sb.AppendLine("db_key=" + (DeepbridApiKey ?? ""));
                sb.AppendLine("ad_key=" + (AllDebridApiKey ?? ""));
                sb.AppendLine("pm_key=" + (PremiumizeApiKey ?? ""));
                sb.AppendLine("tb_key=" + (TorBoxApiKey ?? ""));
                sb.AppendLine("unlock_provider=" + (UnlockProviderId ?? UnlockProviders.RealDebridId));
                sb.AppendLine("use_unlock=" + (UseUnlockProvider ? "1" : "0"));
                if (EnabledUnlockProviders != null) sb.AppendLine("enabled_unlock_providers=" + UnlockProviders.NormalizeIds(EnabledUnlockProviders));
                sb.AppendLine("api_base=" + (ApiBaseUrl ?? ""));
                sb.AppendLine("use_proxy=" + (UseLanProxy ? "1" : "0"));
                sb.AppendLine("force_proxy=" + (ForceProxy ? "1" : "0"));
                sb.AppendLine("proxy_base=" + (ProxyBaseUrl ?? ""));
                sb.AppendLine("proxy_key=" + (ProxyKey ?? ""));
                sb.AppendLine("dl_stats=" + DownloadStatsMode);
                sb.AppendLine("download_range_count=" + DownloadRangeCount);
                sb.AppendLine("download_scheduler_version=6");
                sb.AppendLine("bgft_direct=" + (UseBgftDirect ? "1" : "0"));
                sb.AppendLine("nerd_stats=" + (NerdStats ? "1" : "0"));
                sb.AppendLine("retry_source_archive_passwords=" + (RetrySourceArchivePasswords ? "1" : "0"));
                sb.AppendLine("accent=" + Accent);
                sb.AppendLine("accent_name=" + AccentName);
                sb.AppendLine("bg_mode=" + BackgroundMode);
                sb.AppendLine("bg_image=" + (BackgroundImagePath ?? ""));
                sb.AppendLine("bg_quality=" + (BackgroundQuality ?? BgQualityPerformance));
                sb.AppendLine("staging_location=" + StagingLocation);
                sb.AppendLine("cloud=" + (CoverCloud ? "on" : "off"));
                sb.AppendLine("cloud_density=" + CoverCloudDensity);
                sb.AppendLine("style=" + UiStyle);
                sb.AppendLine("reduce_motion=" + (ReduceMotion ? "on" : "off"));
                sb.AppendLine("search_recents=" + (SearchRecents ? "on" : "off"));
                sb.AppendLine("search_continue=" + (SearchContinue ? "on" : "off"));
                sb.AppendLine("eta_format=" + EtaFormat);
                sb.AppendLine("firmware_hints=" + (ShowFirmwareHints ? "1" : "0"));
                sb.AppendLine("background_music=" + (BackgroundMusic ? "1" : "0"));
                sb.AppendLine("interface_sounds=" + (InterfaceSounds ? "1" : "0"));
                sb.AppendLine("audio_volume=" + Math.Max(0, Math.Min(100, AudioVolume)));
                // Migration retry rewrites persisted paths under this same lock.
                lock (typeof(AppSettings))
                {
                    string path = SettingsPath;
                    AtomicFile.WriteText(path, sb.ToString());
                }
                ApplyToNetHttp();
                return true;
            }
            catch
            {
                try { User.NotifyToast("Settings save fail"); } catch { }
                return false;
            }
        }

        public void ApplyToNetHttp()
        {
            DownloadRangeCount = DownloadTransferSettings.ClampRangeCount(DownloadRangeCount);
            UseLanProxy = false; ForceProxy = false; RealDebridLocation = RdLocationAuto;
            NetHttp.ForceProxy = false;
            NetHttp.ProxyBase = ProxyBaseUrl ?? "";
            NetHttp.ProxyKey = ProxyKey ?? "game-search-lan";
            NetHttp.DownloadRangeCount = DownloadRangeCount;
        }

        public bool HasRealDebrid
        {
            get { return !string.IsNullOrEmpty(RealDebridToken) && RealDebridToken.Trim().Length >= 8; }
        }

        public bool HasDeepbrid
        {
            get { return !string.IsNullOrEmpty(DeepbridApiKey) && DeepbridApiKey.Trim().Length >= 8; }
        }

        public bool HasAllDebrid
        {
            get { return !string.IsNullOrEmpty(AllDebridApiKey) && AllDebridApiKey.Trim().Length >= 8; }
        }

        public bool HasTorBox
        {
            get { return !string.IsNullOrEmpty(TorBoxApiKey) && TorBoxApiKey.Trim().Length >= 8; }
        }

        public bool HasPremiumize
        {
            get { return !string.IsNullOrEmpty(PremiumizeApiKey) && PremiumizeApiKey.Trim().Length >= 8; }
        }

        public int TokenLen
        {
            get { return string.IsNullOrEmpty(RealDebridToken) ? 0 : RealDebridToken.Trim().Length; }
        }

        public bool HasActiveUnlock
        {
            get
            {
                return UnlockProviders.EnabledIds(this).Length != 0;
            }
        }

        static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static bool IsTrue(string v)
        {
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }

        static bool ReadToggle(string value, bool fallback)
        {
            if (value == "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "on", StringComparison.OrdinalIgnoreCase)) return true;
            if (value == "0" || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "off", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }

        public static string NormalizeRdLocation(string value)
        {
            if (string.Equals(value, RdLocationUs, StringComparison.OrdinalIgnoreCase)) return RdLocationUs;
            if (string.Equals(value, RdLocationEu, StringComparison.OrdinalIgnoreCase)) return RdLocationEu;
            return RdLocationAuto;
        }

        public static string RdLocationLabel(string value)
        {
            value = NormalizeRdLocation(value);
            if (value == RdLocationUs) return "Prefer US";
            if (value == RdLocationEu) return "Prefer EU";
            return "Auto";
        }

        public bool TrySelectDownloadService(string id, out string error)
        {
            error = null;
            if (!UnlockProviders.IsSupported(id) && id != UnlockProviders.NoneId)
            { error = "Choose an available link service or direct links"; return false; }
            if (!UnlockProviders.IsConfigured(this, id))
            { error = "Link " + UnlockProviders.DisplayName(id) + " from the QR page first"; return false; }
            string previous = UnlockProviderId, previousEnabled = EnabledUnlockProviders;
            bool use = UseUnlockProvider, rd = UseRealDebrid;
            UnlockProviderId = id; UseUnlockProvider = id != UnlockProviders.NoneId; UseRealDebrid = id == UnlockProviders.RealDebridId;
            EnabledUnlockProviders = id == UnlockProviders.NoneId ? "" : id;
            if (Save()) return true;
            UnlockProviderId = previous; UseUnlockProvider = use; UseRealDebrid = rd;
            EnabledUnlockProviders = previousEnabled;
            error = "Could not save the download service. Try again."; return false;
        }

        public bool TryToggleDownloadService(string id, out string error)
        {
            if (id == UnlockProviders.NoneId) return TrySelectDownloadService(id, out error);
            error = null;
            if (!UnlockProviders.IsSupported(id) || !UnlockProviders.IsConfigured(this, id))
            { error = "Link " + UnlockProviders.DisplayName(id) + " from the QR page first"; return false; }
            string previous = UnlockProviderId, previousEnabled = EnabledUnlockProviders;
            bool previousUse = UseUnlockProvider, previousRd = UseRealDebrid;
            var enabled = new System.Collections.Generic.List<string>(UnlockProviders.EnabledIds(this));
            if (!enabled.Remove(id)) enabled.Add(id);
            EnabledUnlockProviders = UnlockProviders.NormalizeIds(string.Join(",", enabled.ToArray()));
            UseUnlockProvider = enabled.Count != 0;
            if (!enabled.Contains(UnlockProviderId)) UnlockProviderId = enabled.Count == 0 ? UnlockProviders.NoneId : enabled[0];
            if (Save()) return true;
            UnlockProviderId = previous; EnabledUnlockProviders = previousEnabled;
            UseUnlockProvider = previousUse; UseRealDebrid = previousRd;
            error = "Could not save the enabled services. Try again.";
            return false;
        }

        public void ValidateAppearance(bool notify)
        {
            SettingsNotice = "";
            BackgroundMode = BackdropPattern.Modes[BackdropPattern.Index(BackgroundMode)]; BackgroundImagePath = ""; CoverCloud = false;
            BackgroundQuality = BgQualityPerformance; UiStyle = StyleFlat;
            SearchRecents = true; SearchContinue = true; EtaFormat = EtaShort;

            ThemeAccentPreset preset;
            ThemeColor color;
            if (!ThemePalette.TryResolveAccent(Accent, out color, out preset))
            {
                Accent = ThemePalette.DefaultAccentHex;
                AccentName = ThemePalette.DefaultAccentName;
                SetSettingsNotice("Accent reset", notify);
            }
            else
            {
                Accent = color.ToHex();
                AccentName = preset == null ? "Custom" : preset.Name;
            }

            BackgroundQuality = NormalizeChoice(BackgroundQuality, BgQualityPerformance,
                BgQualityPerformance, BgQualityQuality);
            CoverCloudDensity = NormalizeChoice(CoverCloudDensity, CloudDensityFew,
                CloudDensityFew, CloudDensityNormal, CloudDensityDense);
            UiStyle = NormalizeChoice(UiStyle, StyleGlass, StyleGlass, StyleFlat);
            EtaFormat = NormalizeChoice(EtaFormat, EtaShort, EtaShort, EtaClock);
        }

        void SetSettingsNotice(string message, bool notify)
        {
            SettingsNotice = message;
            if (!notify) return;
            try { User.NotifyToast(message); } catch { }
        }

        static string NormalizeChoice(string value, string fallback, params string[] choices)
        {
            foreach (string choice in choices)
                if (string.Equals(value, choice, StringComparison.OrdinalIgnoreCase)) return choice;
            return fallback;
        }
    }
}
