using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal sealed partial class DownloadManager
    {
        static string ClipMsg(string s, int n)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace('\r', ' ').Replace('\n', ' ');
            return s.Length <= n ? s : s.Substring(0, n);
        }

        internal static void UpdateLiveTransferStats(DlItem item, long done, long total, long received, string phase, long nowMs)
        {
            if (received < 0 || !IsTransferStatsPhase(phase))
            {
                item.LiveMeter = null;
                UpdateTransferStats(item, done, total, phase, nowMs, 0);
                return;
            }
            if (!item.StatsInitialized || item.LiveMeter == null) item.LiveMeter = new LiveTransferMeter();
            item.LiveMeter.Sample(received, done, total, phase, item.AttemptId, nowMs);
            item.Done = Math.Max(0, done);item.Total = Math.Max(0, total);
            item.BytesPerSec = item.LiveMeter.Rate;item.EtaSeconds = item.LiveMeter.Eta;
            item.StatsInitialized = true;item.StatsPhase = phase;item.StatsAttempt = item.AttemptId;
            item.StatsAt = nowMs;item.StatsAdvancedAt = item.LiveMeter.AdvancedAt;
        }

        internal static void UpdateTransferStats(DlItem item, long done, long total, string phase, long nowMs, int nativeEta)
        {
            done = Math.Max(0, done); total = Math.Max(0, total); nowMs = Math.Max(0, nowMs);
            item.Done = done; item.Total = total;
            bool transferring = IsTransferStatsPhase(phase) || phase == "extracting";
            long elapsed = nowMs - item.StatsAt;
            if (!transferring || !item.StatsInitialized || elapsed < 0 || elapsed > 15000 || done < item.StatsObservedDone ||
                item.StatsPhase != phase || item.StatsAttempt != item.AttemptId)
            {
                item.StatsAt = item.StatsAdvancedAt = nowMs; item.StatsDone = done; item.StatsTotal = total;
                item.StatsObservedDone = done; item.StatsInitialized = true;
                item.StatsPhase = phase; item.StatsAttempt = item.AttemptId;
                item.BytesPerSec = 0; item.EtaSeconds = 0;
                return;
            }
            if (done > item.StatsObservedDone) item.StatsAdvancedAt = nowMs;
            item.StatsObservedDone = done; item.StatsTotal = total;
            if ((total > 0 && done >= total) || nowMs - item.StatsAdvancedAt >= 5000)
            {
                item.StatsAt = nowMs; item.StatsDone = done;
                item.BytesPerSec = 0; item.EtaSeconds = 0;
                return;
            }
            // Warm up for two seconds, then smooth by elapsed time rather than callback frequency.
            if (elapsed >= (item.BytesPerSec > 0 ? 1000 : 2000))
            {
                double rate = (done - item.StatsDone) * 1000.0 / elapsed;
                double weight = 1.0 - Math.Exp(-elapsed / 5000.0);
                item.BytesPerSec = item.BytesPerSec > 0
                    ? item.BytesPerSec + (rate - item.BytesPerSec) * weight : rate;
                item.StatsAt = nowMs; item.StatsDone = done;
            }
            // BGFT's restSec values may describe a different phase or be stale. Only recent
            // observed byte movement can support the download estimate shown by SSPI.
            item.EtaSeconds = TransferEtaSeconds(done, total, item.BytesPerSec);
        }

        static bool IsTransferStatsPhase(string phase)
        {
            return phase == "downloading" || phase == "feeding" || phase == "bgft" ||
                phase == "foreground" || (phase != null && phase.StartsWith("volume:", StringComparison.Ordinal));
        }

        internal static bool HasCurrentTransferStats(DlItem item, long nowMs)
        {
            return item != null && item.StatsInitialized &&
                (item.State == DlState.Downloading || (item.State == DlState.Finalizing && item.StatsPhase == "extracting")) &&
                !item.PauseRequested && !item.CancelRequested && item.StatsAttempt == item.AttemptId &&
                (IsTransferStatsPhase(item.StatsPhase) || item.StatsPhase == "extracting") && nowMs >= item.StatsAdvancedAt &&
                nowMs - item.StatsAdvancedAt < 5000;
        }

        internal static int TransferEtaSeconds(long done, long total, double bytesPerSec)
        {
            if (done < 0 || total <= done || bytesPerSec <= 0 ||
                double.IsNaN(bytesPerSec) || double.IsInfinity(bytesPerSec)) return 0;
            double seconds = Math.Ceiling((total - done) / bytesPerSec);
            return seconds >= int.MaxValue ? int.MaxValue : Math.Max(1, (int)seconds);
        }

        static long TransferClockMs()
        {
            return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1000.0 / System.Diagnostics.Stopwatch.Frequency));
        }

        static string UrlTag(string value)
        {
            byte[] digest;
            using (var sha = SHA256.Create())
                digest = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
            var tag = new StringBuilder(12);
            for (int i = 0; i < 6; i++) tag.Append(digest[i].ToString("x2"));
            return tag.ToString();
        }

        static string BgftStallSize(long bytes)
        {
            return (bytes / 1000000000.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " GB";
        }

        public static string Human(long bytes)
        {
            double v = bytes;
            string[] u = { "B", "KB", "MB", "GB" };
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? ((long)v).ToString() : v.ToString("0.0")) + " " + u[i];
        }

        public static string FormatProgress(AppSettings cfg, long done, long total, double bps, int etaSec)
        {
            int mode = cfg != null ? cfg.DownloadStatsMode : 0;
            string size = Human(done) + (total > 0 ? " / " + Human(total) : "");
            string speed = bps > 0 ? FormatSpeed(bps, mode) : "";
            string eta = etaSec > 0 ? FormatEta(etaSec) : "";
            if (mode == 1) // size only
                return size;
            if (mode == 2) // size + speed
                return string.IsNullOrEmpty(speed) ? size : size + "  ·  " + speed;
            if (mode == 3) // size + eta
                return string.IsNullOrEmpty(eta) ? size : size + "  ·  " + eta;
            // 0 = all
            var parts = new List<string>();
            parts.Add(size);
            if (!string.IsNullOrEmpty(speed)) parts.Add(speed);
            if (!string.IsNullOrEmpty(eta)) parts.Add(eta);
            return string.Join("  ·  ", parts.ToArray());
        }

        static string FormatSpeed(double bps, int mode)
        {
            // mode 0/2 use MB/s by default; Mbps if bit mode bit set via mode==4 later
            if (bps >= 1024 * 1024)
                return (bps / (1024.0 * 1024.0)).ToString("0.00") + " MB/s";
            if (bps >= 1024)
                return (bps / 1024.0).ToString("0.0") + " KB/s";
            return ((long)bps).ToString() + " B/s";
        }

        void BeginNerdSession(DlItem job, string directUrl)
        {
            lock (_lock)
            {
                _nerd.Reset();
                _nerd.TitleId = job.TitleId ?? "";
                try
                {
                    _nerd.FileName = string.IsNullOrEmpty(job.DestPath)
                        ? (job.Kind ?? "pkg")
                        : Path.GetFileName(job.DestPath);
                }
                catch { _nerd.FileName = job.Kind ?? "pkg"; }
                _nerd.CdnHost = SafeHost(directUrl);
                job.CdnHost = _nerd.CdnHost;
                _nerd.RegionHint = RegionFromHost(_nerd.CdnHost, _cfg != null ? _cfg.RealDebridLocation : "auto");
                _nerd.TransferMode = "in-app";
                job.TransferMode = _nerd.TransferMode;
                string provider = string.IsNullOrEmpty(job.ResolvedProviderId) ?
                    (job.AccessType == "Cloud" ? CloudCatalog.OwningProvider(job.HosterUrl) : "direct") : job.ResolvedProviderId;
                SspiLog.Write("download", "event=provider-selected job=" + (job.Id ?? "") +
                    " attempt=" + job.AttemptId + " provider=" + provider + " route=" + (job.AccessType ?? "") +
                    " cdn=" + _nerd.CdnHost + " allowance=" + DownloadTransferSettings.ConnectionsFor(directUrl, NetHttp.DownloadRangeCount));
            }
        }

        static string SafeHost(string url)
        {
            try
            {
                if (string.IsNullOrEmpty(url)) return "—";
                var u = new Uri(url);
                string h = u.Host ?? "";
                if (h.Length > 48) h = h.Substring(0, 48);
                return h;
            }
            catch { return "—"; }
        }

        static string RegionFromHost(string host, string pref)
        {
            string h = (host ?? "").ToLowerInvariant();
            string[] us = { "lax", "sfo", "sea", "dal", "dfw", "chi", "ord", "nyc", "iad", "mia", "atl", ".us." };
            string[] eu = { "ams", "fra", "lhr", "lon", "par", "rbx", "waw", "hel", "sto", "mad", ".eu." };
            foreach (var t in us) if (h.Contains(t)) return "US-cdn";
            foreach (var t in eu) if (h.Contains(t)) return "EU-cdn";
            pref = AppSettings.NormalizeRdLocation(pref);
            if (pref == AppSettings.RdLocationUs) return "pref-US";
            if (pref == AppSettings.RdLocationEu) return "pref-EU";
            return "auto";
        }

        public static string FormatMbps(double mbps)
        {
            if (mbps >= 100) return mbps.ToString("0") + " Mbps";
            if (mbps >= 10) return mbps.ToString("0.0") + " Mbps";
            return mbps.ToString("0.00") + " Mbps";
        }

        public static string FormatMBpsFromMbps(double mbps)
        {
            double mib = (mbps * 1000.0 * 1000.0 / 8.0) / (1024.0 * 1024.0);
            return mib.ToString("0.00") + " MB/s";
        }

        static string FormatEta(int sec)
        {
            if (sec < 60) return sec + "s left";
            if (sec < 3600) return (sec / 60) + "m " + (sec % 60) + "s left";
            return (sec / 3600) + "h " + ((sec % 3600) / 60) + "m left";
        }

        static string Clip(string s, int n)
        {
            if (s == null) return "";
            return s.Length <= n ? s : s.Substring(0, n);
        }

        static string Hex(int value)
        {
            return "0x" + unchecked((uint)value).ToString("X8");
        }
    }
}
