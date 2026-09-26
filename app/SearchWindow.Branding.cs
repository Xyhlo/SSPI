using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SDL2.Types;
using static SDL2.SDL;

namespace Orbis
{
    public partial class SearchWindow
    {
        const uint LaunchDurationMs = 900;
        IntPtr _launchCube, _launchWordmark;
        bool _launchStarted, _launchFinished;
        uint _launchStartedAt;
        bool _startupServicesReady, _startupFailed, _startupRetry;
        int _startupStep;
        string _startupStatus = "1 / 3  Checking background download settings";
        string _startupDetail = "";
        uint _startupPolledAt;
        readonly List<string> _startupLines = new List<string>();
        string _startupLastLine;
        int _startupScroll;

        internal static string StartupActivity(uint elapsed, bool reduceMotion, bool loading)
        {
            if (!loading) return "";
            if (reduceMotion) return "...";
            uint frame = elapsed / 350 % 3;
            return frame == 0 ? "." : frame == 1 ? ".." : "...";
        }

        internal static string StartupFailureGuidance(int step, bool storageWritable, bool migrationPending)
        {
            if (!storageWritable)
                return "SSPI cannot access its data folder. Check free space and folder access, then try again.";
            if (migrationPending)
                return "Data migration needs attention. Keep your existing SSPI data folders and check the details above.";
            return step == 0
                ? "Restart the PS4, enable GoldHEN and plugin support, then open SSPI."
                : "SSPI could not finish startup. Check the details above, then open SSPI again.";
        }

        void RecordStartupLine(string value)
        {
            value = SspiLog.Clean(value);
            if (value.Length == 0 || value == _startupLastLine) return;
            _startupLastLine = value;
            while (value.Length > 0)
            {
                int count = Math.Min(104, value.Length);
                if (count < value.Length) { int space = value.LastIndexOf(' ', count - 1, count); if (space > 50) count = space; }
                _startupLines.Add(value.Substring(0, count));
                value = value.Substring(count).TrimStart();
            }
            if (_startupLines.Count > 96) _startupLines.RemoveRange(0, _startupLines.Count - 96);
        }

        void PollStartup()
        {
            if (_startupServicesReady || !_firstFramePresented || _startupFailed) return;
            if (_launchStarted && UiElapsed(_launchStartedAt) < 1500) return;
            if (_startupStep == 0)
            {
                if (!_cfg.UseBgftDirect) { _startupStep = 1; return; }
                if (!_residentLaunchStarted)
                {
                    _residentLaunchStarted = true;
                    _residentLaunchReadyAt = UiTick();
                    ResidentDownloadService.StartLaunchMaintenance(true);
                }
                if (_startupPolledAt != 0 && UiElapsed(_startupPolledAt) < 500) return;
                _startupPolledAt = UiTick();
                string state = ResidentDownloadService.LaunchMaintenanceStatus ?? "checking";
                _startupStatus = "1 / 3  Loading resident and checking its services";
                _startupDetail = "State: " + state + " | " + ResidentDownloadService.ReadinessDetail;
                if (ResidentDownloadService.HasDownloader) { _startupStep = 1; _startupRetry = false; return; }
                if ((!_startupRetry && (state == "restart-required" || state == "unavailable")) ||
                    UiElapsed(_residentLaunchReadyAt) >= 90000)
                {
                    _startupFailed = true;
                    _startupStatus = "Background downloader could not become ready";
                    _startupDetail += " | " + ResidentDownloadService.LastError;
                    SspiLog.Write("startup", "resident-gate failed " + _startupDetail);
                }
                return;
            }
            try
            {
                if (_startupStep == 1)
                {
                    _startupStatus = "2 / 3  Loading settings and package sources";
                    _startupDetail = _cfg.UseBgftDirect ? "Resident confirmed. Preparing SSPI." : "In-app mode selected. Keep SSPI open while downloading.";
                    Program.StartupStage("resident-gate-passed");
                    _firmwareVersion = FirmwareInfo.Probe();
                    _covers = new CoverCache();
                    RefreshSourceUi(); LoadRecentQueries();
                    _startupStep = 2;
                    return;
                }
                _startupStatus = "3 / 3  Restoring your download queue";
                Program.StartupStage("download-manager");
                _dlMgr = new DownloadManager(_cfg);
                StartFtpInbox();
                RefreshLandingModel(); StartLibraryUpdateScan();
                _freeStorageLabel = ReadFreeStorageLabel();
                _startupServicesReady = true;
                FinishLaunchBranding();
            }
            catch (Exception ex)
            {
                _startupFailed = true;
                _startupDetail = ex.GetType().Name + " 0x" + unchecked((uint)ex.HResult).ToString("X8") + ": " + SspiLog.Clean(ex.Message);
                Program.RecordFailure(ex);
            }
        }

        void HandleStartupButton(DS4Button button)
        {
            if (button == DS4Button.SCE_PAD_BUTTON_UP) { _startupScroll = Math.Min(Math.Max(0, _startupLines.Count - 12), _startupScroll + 1); return; }
            if (button == DS4Button.SCE_PAD_BUTTON_DOWN) { _startupScroll = Math.Max(0, _startupScroll - 1); return; }
            if (!_startupFailed || _startupStep != 0) return;
            if (button == DS4Button.SCE_PAD_BUTTON_CROSS)
            {
                _startupFailed = false; _startupRetry = true;
                RecordStartupLine("Retry requested: checking resident activation again.");
                _residentLaunchReadyAt = UiTick(); _startupPolledAt = 0;
                ResidentDownloadService.RetryActivation();
            }
            else if (button == DS4Button.SCE_PAD_BUTTON_SQUARE)
            {
                _cfg.UseBgftDirect = false;
                if (!_cfg.Save())
                {
                    _cfg.UseBgftDirect = true;
                    _startupDetail += " | Could not save the download mode.";
                    return;
                }
                _startupFailed = false; _startupStep = 1;
                SspiLog.Write("startup", "resident-gate explicit-in-app-choice");
            }
        }

        static IntPtr LoadBrandTexture(IntPtr renderer, string name, int width, int height)
        {
            IntPtr texture = IntPtr.Zero;
            try
            {
                string root;
                try { root = Internals.IO.GetAppBaseDirectory(); }
                catch { root = AppDomain.CurrentDomain.BaseDirectory; }
                string path = Path.Combine(root ?? "/app0", "assets/images/" + name);
                if (!File.Exists(path)) path = "/app0/assets/images/" + name;
                // Fixed, build-generated RGBA avoids image decoding on the startup thread.
                int expected = checked(width * height * 4);
                if (!File.Exists(path) || new FileInfo(path).Length != expected) return IntPtr.Zero;
                byte[] pixels = File.ReadAllBytes(path);
                if (pixels.Length != expected) return IntPtr.Zero;
                texture = SDL_CreateTexture(renderer, SDL_PIXELFORMAT_ABGR8888,
                    (int)SDL_TextureAccess.SDL_TEXTUREACCESS_STATIC, width, height);
                if (texture == IntPtr.Zero) return IntPtr.Zero;
                var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try
                {
                    if (SDL_UpdateTexture(texture, IntPtr.Zero, pin.AddrOfPinnedObject(), width * 4) != 0)
                        throw new InvalidOperationException("Startup texture upload failed");
                }
                finally { pin.Free(); }
                if (SDL_SetTextureBlendMode(texture, SDL_BlendMode.SDL_BLENDMODE_BLEND) != 0)
                    throw new InvalidOperationException("Startup texture blend failed");
                return texture;
            }
            catch
            {
                if (texture != IntPtr.Zero) SDL_DestroyTexture(texture);
                return IntPtr.Zero;
            }
        }

        void PrepareLaunchBranding(IntPtr renderer)
        {
            _launchCube = LoadBrandTexture(renderer, "sspi-startup.rgba", 512, 512);
        }

        void PaintLaunchBranding(IntPtr renderer, int rise)
        {
            Fill(renderer, 0, 0, W, H, C(20, 20, 20));
            if (_launchCube != IntPtr.Zero)
            {
                var cube = new SDL_Rect { x = (W - 512) / 2, y = (H - 512) / 2 + rise, w = 512, h = 512 };
                SDL_RenderCopy(renderer, _launchCube, IntPtr.Zero, ref cube);
            }
        }

        bool DrawLaunchBranding(IntPtr renderer, uint now)
        {
            if (_launchFinished) return false;
            if (!_launchStarted) { _launchStartedAt = now; _launchStarted = true; }
            uint elapsed = unchecked(now - _launchStartedAt);
            Fill(renderer, 0, 0, W, H, C(20, 20, 20));
            if (elapsed < LaunchDurationMs && _launchCube != IntPtr.Zero)
            {
                int rise = 0;
                if (!_cfg.ReduceMotion)
                    rise = -(int)Math.Round(9 * (1 - Math.Cos(2 * Math.PI * elapsed / LaunchDurationMs)));
                PaintLaunchBranding(renderer, rise);
                return true;
            }
            RecordStartupLine(_startupDetail);
            Fill(renderer, 280, 180, 1360, 600, C(14, 15, 16));
            Fill(renderer, 280, 180, 1360, 2, Border);
            TextPx(renderer, 312, 202, 20, "SSPI / STARTUP", White);
            TextPx(renderer, 1370, 202, 16, "UP / DOWN  Scroll", Dim);
            TextFit(renderer, 312, 250, 24, 1180, _startupStatus, _startupFailed ? Danger : White);
            string activity = StartupActivity(elapsed, _cfg.ReduceMotion, !_startupFailed && !_startupServicesReady);
            if (activity.Length != 0) TextFit(renderer, 1520, 250, 24, 72, activity, Muted);
            int end = Math.Max(0, _startupLines.Count - _startupScroll), start = Math.Max(0, end - 12);
            // Bound rows and width without changing renderer clip/alpha state.
            for (int i = start; i < end; i++)
                TextFit(renderer, 312, 299 + (i - start) * 32, 19, 1280, _startupLines[i], Muted);
            if (_startupLines.Count > 12) {
                int thumb = Math.Max(28, 384 * 12 / _startupLines.Count);
                Fill(renderer, 1614, 299, 3, 384, Border);
                Fill(renderer, 1614, 299 + (384 - thumb) * start / Math.Max(1, _startupLines.Count - 12), 3, thumb, Muted);
            }
            if (_startupFailed)
            {
                TextCentered(renderer, new SDL_Rect { x = 280, y = 706, w = 1360, h = 26 }, 19,
                    StartupFailureGuidance(_startupStep, AppSettings.DataDirWritable, AppSettings.DataMigrationPending), White);
                TextCentered(renderer, new SDL_Rect { x = 280, y = 740, w = 1360, h = 26 }, 18, "If this repeats, share the codes above and combined.log with support.", Muted);
                if (_startupStep == 0)
                {
                    TextCentered(renderer, new SDL_Rect { x = 280, y = 814, w = 1360, h = 30 }, 21, "X  Check again     SQUARE  Switch to SSPI (in-app download)", White);
                    TextCentered(renderer, new SDL_Rect { x = 280, y = 854, w = 1360, h = 30 }, 18, "Keep SSPI open during in-app downloads. Background jobs and files are retained.", Muted);
                }
            }
            TextCentered(renderer, new SDL_Rect { x = 280, y = 916, w = 1360, h = 26 }, 18, "Logs: /data/SSPI/logs/combined.log  |  resident.log  |  startup.log", Dim);
            TextCentered(renderer, new SDL_Rect { x = 280, y = 950, w = 1360, h = 26 }, 16, BuildIdentity.Label, Dim);
            return true;
        }

        void DrawBetaWatermark(IntPtr renderer)
        {
            TextPx(renderer, 64, 14, 15, BuildIdentity.Label, C(112, 112, 112));
        }

        void FinishLaunchBranding()
        {
            _launchFinished = true;
            if (_launchCube != IntPtr.Zero) { SDL_DestroyTexture(_launchCube); _launchCube = IntPtr.Zero; }
            if (_launchWordmark != IntPtr.Zero) { SDL_DestroyTexture(_launchWordmark); _launchWordmark = IntPtr.Zero; }
        }

        public override void Dispose()
        {
            StopLinkStatusLookup();
            UiAudio.Shutdown();
            FinishLaunchBranding();
            ReleasePattern();
            foreach (var frame in _caseSizes.Values) if (frame != IntPtr.Zero) SDL_DestroyTexture(frame);
            _caseSizes.Clear();
            if (_caseFrame != IntPtr.Zero) { SDL_DestroyTexture(_caseFrame); _caseFrame = IntPtr.Zero; }
            if (_headerBrand != IntPtr.Zero) { SDL_DestroyTexture(_headerBrand); _headerBrand = IntPtr.Zero; }
            base.Dispose();
        }
    }
}
