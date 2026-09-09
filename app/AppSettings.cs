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
        public string RealDebridToken = "";
        public bool UseRealDebrid = true;
        /// <summary>
        /// Preferred Real-Debrid CDN location. The RD API normally returns one
        /// IP-selected URL, so US/EU is best-effort when alternatives exist.
        /// </summary>
        public string RealDebridLocation = RdLocationAuto;
        public string DeepbridApiKey = "";
        public string AllDebridApiKey = "";
        public string TorBoxApiKey = "";
        public bool ShowFirmwareHints = true;
        public string UnlockProviderId = UnlockProviders.RealDebridId;
        public bool UseUnlockProvider = true;
        public string ApiBaseUrl = "";
        public bool UseLanProxy = false;
        public bool ForceProxy = false;
        public string ProxyBaseUrl = "";
        public string ProxyKey = "game-search-lan";
        /// <summary>0=size+speed+ETA, 1=size only, 2=size+speed, 3=size+ETA</summary>
        public int DownloadStatsMode = 0;
        /// <summary>Legacy setting retained for checkpoint migration; new transfers use one stream.</summary>
        public int DownloadRangeCount = DownloadTransferSettings.DefaultRangeCount;
        public int DownloadLimitMBps;
        /// <summary>Downloads terminal + speed graph (technical, no secrets).</summary>
        public bool NerdStats = false;
        /// <summary>
        /// Validated local package URLs register with system BGFT (PS4 notifications).
        /// </summary>
        public bool UseBgftDirect = true;
        /// <summary>
        /// Experimental: register the preflight-resolved origin URL directly with
        /// system BGFT (PS4 notification download). settings.ini only, no UI row.
        /// </summary>
        public bool BgftDirectUrl = false;

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
                        "/data/GameSearch",
                        "/user/data/GameSearch",
                        Path.Combine(SafeAppBase(), "data")
                    };
                    foreach (var c in candidates)
                    {
                        try
                        {
                            if (!Directory.Exists(c)) Directory.CreateDirectory(c);
                            string probe = Path.Combine(c, ".write_test");
                            File.WriteAllText(probe, "1");
                            File.Delete(probe);
                            _dataDir = c;
                            _dataDirWritable = true;
                            _dataDirResolved = true;
                            return _dataDir;
                        }
                        catch { }
                    }
                    _dataDir = ".";
                    _dataDirWritable = false;
                    _dataDirResolved = true;
                    return _dataDir;
                }
            }
        }

        public static bool DataDirWritable { get { var _ = DataDir; return _dataDirWritable; } }

        public static string SettingsPath { get { return Path.Combine(DataDir, "settings.ini"); } }

        public static string DownloadDir
        {
            get
            {
                string d = Path.Combine(DataDir, "downloads");
                try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); } catch { }
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
            try
            {
                // Migrate token from alternate data dirs if primary has no settings
                string path = SettingsPath;
                if (!File.Exists(path))
                {
                    string[] alt =
                    {
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
                    else if (Eq(key, "connection_mbps")) { int speed; if (int.TryParse(val, out speed)) ConnectionMbps = Math.Max(0, Math.Min(10000, speed)); }
                    else if (Eq(key, "rd_token")) RealDebridToken = val;
                    else if (Eq(key, "use_rd")) UseRealDebrid = IsTrue(val);
                    else if (Eq(key, "rd_location") || Eq(key, "rd_cdn"))
                        RealDebridLocation = NormalizeRdLocation(val);
                    else if (Eq(key, "db_key") || Eq(key, "deepbrid_key")) DeepbridApiKey = val;
                    else if (Eq(key, "ad_key")) AllDebridApiKey = val;
                    else if (Eq(key, "tb_key")) TorBoxApiKey = val;
                    else if (Eq(key, "unlock_provider")) UnlockProviderId = val;
                    else if (Eq(key, "use_unlock")) UseUnlockProvider = IsTrue(val);
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
                    else if (Eq(key, "download_limit_mb_s")) { int limit; if (int.TryParse(val, out limit)) DownloadLimitMBps = Math.Max(0, Math.Min(1000, limit)); }
                    else if (Eq(key, "download_range_count"))
                    {
                        int count;
                        if (int.TryParse(val, out count))
                            DownloadRangeCount = DownloadTransferSettings.ClampRangeCount(count);
                    }
                    else if (Eq(key, "bgft_direct") || Eq(key, "use_bgft"))
                        UseBgftDirect = IsTrue(val);
                    else if (Eq(key, "bgft_direct_url"))
                        BgftDirectUrl = IsTrue(val);
                    else if (Eq(key, "nerd_stats") || Eq(key, "stats_for_nerds"))
                        NerdStats = IsTrue(val);
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
                if (string.IsNullOrEmpty(UnlockProviderId))
                    UnlockProviderId = UseRealDebrid ? UnlockProviders.RealDebridId : UnlockProviders.NoneId;
                if (!UseRealDebrid && string.Equals(UnlockProviderId, UnlockProviders.RealDebridId, StringComparison.OrdinalIgnoreCase))
                    UseUnlockProvider = false;
                if (UseLanProxy) ForceProxy = true;
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
                sb.AppendLine("tb_key=" + (TorBoxApiKey ?? ""));
                sb.AppendLine("unlock_provider=" + (UnlockProviderId ?? UnlockProviders.RealDebridId));
                sb.AppendLine("use_unlock=" + (UseUnlockProvider ? "1" : "0"));
                sb.AppendLine("api_base=" + (ApiBaseUrl ?? ""));
                sb.AppendLine("use_proxy=" + (UseLanProxy ? "1" : "0"));
                sb.AppendLine("force_proxy=" + (ForceProxy ? "1" : "0"));
                sb.AppendLine("proxy_base=" + (ProxyBaseUrl ?? ""));
                sb.AppendLine("proxy_key=" + (ProxyKey ?? ""));
                sb.AppendLine("dl_stats=" + DownloadStatsMode);
                sb.AppendLine("download_range_count=1");
                sb.AppendLine("download_limit_mb_s=" + DownloadLimitMBps);
                sb.AppendLine("bgft_direct=" + (UseBgftDirect ? "1" : "0"));
                sb.AppendLine("bgft_direct_url=" + (BgftDirectUrl ? "1" : "0"));
                sb.AppendLine("nerd_stats=" + (NerdStats ? "1" : "0"));
                sb.AppendLine("accent=" + Accent);
                sb.AppendLine("accent_name=" + AccentName);
                sb.AppendLine("bg_mode=" + BackgroundMode);
                sb.AppendLine("bg_image=" + (BackgroundImagePath ?? ""));
                sb.AppendLine("bg_quality=" + (BackgroundQuality ?? BgQualityPerformance));
                sb.AppendLine("cloud=" + (CoverCloud ? "on" : "off"));
                sb.AppendLine("cloud_density=" + CoverCloudDensity);
                sb.AppendLine("style=" + UiStyle);
                sb.AppendLine("reduce_motion=" + (ReduceMotion ? "on" : "off"));
                sb.AppendLine("search_recents=" + (SearchRecents ? "on" : "off"));
                sb.AppendLine("search_continue=" + (SearchContinue ? "on" : "off"));
                sb.AppendLine("eta_format=" + EtaFormat);
                sb.AppendLine("firmware_hints=" + (ShowFirmwareHints ? "1" : "0"));
                string path = SettingsPath;
                AtomicFile.WriteText(path, sb.ToString());
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
            UseBgftDirect = true; UseLanProxy = false; ForceProxy = false; RealDebridLocation = RdLocationAuto;
            NetHttp.ForceProxy = false;
            NetHttp.ProxyBase = ProxyBaseUrl ?? "";
            NetHttp.ProxyKey = ProxyKey ?? "game-search-lan";
            NetHttp.DownloadRangeCount = 1;
            SequentialDownloadEngine.LimitBytesPerSecond = (long)DownloadLimitMBps * 1000000;
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

        public int TokenLen
        {
            get { return string.IsNullOrEmpty(RealDebridToken) ? 0 : RealDebridToken.Trim().Length; }
        }

        public bool HasActiveUnlock
        {
            get
            {
                if (!UseUnlockProvider) return false;
                return UnlockProviders.IsConfigured(this, UnlockProviderId);
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
            if (id != UnlockProviders.RealDebridId && id != UnlockProviders.TorBoxId && id != UnlockProviders.NoneId)
            { error = "Choose Real-Debrid, TorBox or direct links"; return false; }
            if (!UnlockProviders.IsConfigured(this, id))
            { error = "Link " + UnlockProviders.DisplayName(id) + " from the QR page first"; return false; }
            string previous = UnlockProviderId;
            bool use = UseUnlockProvider, rd = UseRealDebrid;
            UnlockProviderId = id; UseUnlockProvider = id != UnlockProviders.NoneId; UseRealDebrid = id == UnlockProviders.RealDebridId;
            if (Save()) return true;
            UnlockProviderId = previous; UseUnlockProvider = use; UseRealDebrid = rd;
            error = "Could not save the download service. Try again."; return false;
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
