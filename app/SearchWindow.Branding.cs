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
        // Launch intro: the cube continues where the system splash left it and
        // settles upward, then the wordmark, an accent progress track and a
        // caption follow. Startup work never waits for it; the track follows the
        // real startup stages and opens over the live UI when they finish.
        const uint LaunchCubeMs = 560, LaunchWordAtMs = 180, LaunchWordMs = 380, LaunchTrackAtMs = 360, LaunchTrackMs = 340, LaunchCaptionAtMs = 520, LaunchCaptionMs = 320, LaunchHandoffMs = 340, LaunchProgressMs = 240, LaunchDetailsAfterMs = 8000;
        const int LaunchLift = 100, LaunchWordY = 626, LaunchTrackY = 794, LaunchTrackW = 360;
        IntPtr _launchCube, _launchWordmark;
        bool _launchStarted, _launchFinished, _launchStill, _launchIntroShown, _launchHandoff, _launchProgressSeen;
        uint _launchStartedAt, _launchHandoffAt, _launchProgressAt;
        float _launchProgress;
        SDL_Color _launchBg = C(20, 20, 20), _launchAccent = C(228, 228, 225), _launchTrack = C(62, 62, 61);
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

        // Startup begins once the first intro frame is presented. A former 1.5 s wait
        // compared the SDL frame clock with Environment.TickCount, two unrelated clocks,
        // so it did not reliably wait at all; startup keeps the timing it actually had.
        void PollStartup()
        {
            if (_startupServicesReady || !_firstFramePresented || _startupFailed) return;
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
                byte[] pixels;
                if (!File.Exists(path) && name.StartsWith("ps4-case-", StringComparison.Ordinal)) {
                    // Unlisted view sizes are resized once, never on every draw.
                    string original = Path.Combine(root ?? "/app0", "assets/images/ps4-case.rgba");
                    if (!File.Exists(original)) original = "/app0/assets/images/ps4-case.rgba";
                    if (!File.Exists(original) || new FileInfo(original).Length != 556 * 704 * 4) return IntPtr.Zero;
                    byte[] source = File.ReadAllBytes(original);
                    pixels = new byte[expected];
                    for (int y = 0; y < height; y++) for (int x = 0; x < width; x++) {
                        int src = ((y * 704 / height) * 556 + x * 556 / width) * 4, dst = (y * width + x) * 4;
                        for (int channel = 0; channel < 4; channel++) pixels[dst + channel] = source[src + channel];
                    }
                } else {
                    if (!File.Exists(path) || new FileInfo(path).Length != expected) return IntPtr.Zero;
                    pixels = File.ReadAllBytes(path);
                }
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

        // Unconfigured settings resolve to plain charcoal and the Charcoal accent,
        // which is the monochrome launch identity.
        void ResolveLaunchTheme(string accentHex, string backgroundMode, bool reduceMotion)
        {
            ThemeColor accent = ThemePalette.ResolveAccent(accentHex);
            ThemeColor surface = ThemePalette.LaunchSurface(backgroundMode, accent);
            ThemeColor track = ThemePalette.Mix(surface, accent, 0.18);
            _launchBg = C(surface.R, surface.G, surface.B);
            _launchAccent = C(accent.R, accent.G, accent.B);
            _launchTrack = C(track.R, track.G, track.B);
            _launchStill = reduceMotion;
        }

        void PrepareLaunchBranding(IntPtr renderer)
        {
            // The constructor paints before Load(): read only the saved appearance.
            string accent, background;
            bool reduceMotion;
            AppSettings.ReadLaunchAppearance(out accent, out background, out reduceMotion);
            ResolveLaunchTheme(accent, background, reduceMotion);
            _launchCube = LoadBrandTexture(renderer, "sspi-startup.rgba", 512, 512);
            _launchWordmark = LoadBrandTexture(renderer, "sspi-wordmark.rgba", 360, 186);
        }

        // Constructor paint, before fonts load. With motion the cube stays where
        // the system splash showed it; reduced motion starts settled, so the
        // static intro never moves. rise offsets the cube vertically.
        void PaintLaunchBranding(IntPtr renderer, int rise)
        {
            Fill(renderer, 0, 0, W, H, _launchBg);
            if (_launchCube != IntPtr.Zero)
            {
                var cube = new SDL_Rect { x = (W - 512) / 2, y = (H - 512) / 2 + rise - (_launchStill ? LaunchLift : 0), w = 512, h = 512 };
                SDL_RenderCopy(renderer, _launchCube, IntPtr.Zero, ref cube);
            }
            if (_launchStill) PaintLaunchWordmark(renderer, 1f, 0);
        }

        static float LaunchPhase(uint elapsed, uint at, uint duration)
        {
            return elapsed <= at ? 0f : EaseOut((elapsed - at) / (float)duration);
        }

        static SDL_Color LaunchMix(SDL_Color from, SDL_Color to, float amount)
        {
            amount = Math.Max(0f, Math.Min(1f, amount));
            return C((byte)Math.Round(from.r + (to.r - from.r) * amount),
                (byte)Math.Round(from.g + (to.g - from.g) * amount),
                (byte)Math.Round(from.b + (to.b - from.b) * amount));
        }

        // One target per real startup stage: the same states that record
        // Program.StartupStage. Display easing never runs ahead of a stage.
        float LaunchProgressTarget()
        {
            if (_startupServicesReady) return 1f;
            if (_startupStep >= 2) return 0.84f;
            if (_startupStep == 1) return 0.6f;
            return _firstFramePresented ? 0.3f : 0.12f;
        }

        float SmoothLaunchProgress(uint now)
        {
            float target = LaunchProgressTarget();
            if (_launchStill || target < _launchProgress) _launchProgress = target;
            else if (_launchProgressSeen)
                _launchProgress += (target - _launchProgress) *
                    (float)(1 - Math.Exp(-unchecked(now - _launchProgressAt) / (double)LaunchProgressMs));
            if (target - _launchProgress < 0.002f) _launchProgress = target;
            _launchProgressSeen = true;
            _launchProgressAt = now;
            return _launchProgress;
        }

        string LaunchCaption()
        {
            if (_startupServicesReady) return "Ready";
            if (_startupStep >= 2) return "Restoring your download queue";
            if (_startupStep == 1) return "Loading settings and package sources";
            return _cfg.UseBgftDirect ? "Checking the background downloader" : "Starting SSPI";
        }

        // The wordmark fades by an opaque colour ramp up from the surface and a
        // short rise, never by texture alpha. offset moves it with the hand-off.
        void PaintLaunchWordmark(IntPtr renderer, float shown, int offset)
        {
            int y = LaunchWordY + (int)Math.Round(18 * (1 - shown)) + offset;
            if (y + 186 <= 0) return;
            if (_launchWordmark != IntPtr.Zero)
            {
                int floor = Math.Max(_launchBg.r, Math.Max(_launchBg.g, _launchBg.b));
                byte ink = (byte)Math.Round(floor + (255 - floor) * shown);
                SDL_SetTextureColorMod(_launchWordmark, ink, ink, ink);
                var mark = new SDL_Rect { x = W / 2 - 150, y = y, w = 360, h = 186 };
                SDL_RenderCopy(renderer, _launchWordmark, IntPtr.Zero, ref mark);
            }
        }

        // Two 2 px rows form the 4 px track; the hand-off pulls them apart.
        void PaintLaunchTrackRow(IntPtr renderer, int from, int to, int fill, int y)
        {
            int split = Math.Max(from, Math.Min(to, fill));
            Fill(renderer, from, y, split - from, 2, _launchAccent);
            Fill(renderer, split, y, to - split, 2, _launchTrack);
        }

        // One composition for the intro and its hand-off. open > 0 splits the
        // surface at the progress track: the upper panel carries the cube and
        // wordmark up and the lower panel the caption down, revealing the UI.
        void PaintLaunchIntro(IntPtr renderer, uint elapsed, int open, float progress)
        {
            float settle = _launchStill ? 1f : LaunchPhase(elapsed, 0, LaunchCubeMs);
            float word = _launchStill ? 1f : LaunchPhase(elapsed, LaunchWordAtMs, LaunchWordMs);
            float track = _launchStill ? 1f : LaunchPhase(elapsed, LaunchTrackAtMs, LaunchTrackMs);
            float caption = _launchStill ? 1f : LaunchPhase(elapsed, LaunchCaptionAtMs, LaunchCaptionMs);
            int seam = LaunchTrackY + 2, top = seam - open, bottom = seam + open;
            if (open <= 0) Fill(renderer, 0, 0, W, H, _launchBg);
            else
            {
                Fill(renderer, 0, 0, W, top, _launchBg);
                Fill(renderer, 0, bottom, W, H - bottom, _launchBg);
            }
            if (_launchCube != IntPtr.Zero)
            {
                var cube = new SDL_Rect { x = (W - 512) / 2, y = (H - 512) / 2 - (int)Math.Round(LaunchLift * settle) - open, w = 512, h = 512 };
                if (cube.y + cube.h > 0) SDL_RenderCopy(renderer, _launchCube, IntPtr.Zero, ref cube);
            }
            if (word > 0f)
            {
                if (_launchWordmark != IntPtr.Zero) PaintLaunchWordmark(renderer, word, -open);
                else TextCentered(renderer, new SDL_Rect { x = W / 2 - 300, y = LaunchWordY + 60 + (int)Math.Round(18 * (1 - word)) - open, w = 600, h = 64 },
                    48, "SSPI", LaunchMix(_launchBg, White, word));
            }
            if (track > 0f)
            {
                int left = (W - LaunchTrackW) / 2, half = (int)Math.Round(LaunchTrackW / 2 * track);
                int fill = left + (int)Math.Round(LaunchTrackW * Math.Max(0f, Math.Min(1f, progress)));
                if (top > 0) PaintLaunchTrackRow(renderer, W / 2 - half, W / 2 + half, fill, top - 2);
                if (bottom < H) PaintLaunchTrackRow(renderer, W / 2 - half, W / 2 + half, fill, bottom);
            }
            if (caption > 0f && bottom + 20 < H)
            {
                string text = LaunchCaption();
                var area = new SDL_Rect { x = W / 2 - 500, y = LaunchTrackY + 20 + open, w = 1000, h = 30 };
                TextCentered(renderer, area, 18, text, LaunchMix(_launchBg, Muted, caption));
                string dots = StartupActivity(elapsed, _launchStill, !_startupFailed && !_startupServicesReady);
                if (dots.Length != 0)
                {
                    int width = UiFont.MeasurePx(18, text);
                    TextPx(renderer, area.x + Math.Max(8, (area.w - width) / 2) + width + 2, area.y + 2, 18, dots,
                        LaunchMix(_launchBg, _launchAccent, caption));
                }
            }
        }

        // Opens the finished intro from its progress track over the live UI.
        bool DrawLaunchHandoff(IntPtr renderer, uint now)
        {
            uint t = unchecked(now - _launchHandoffAt);
            if (!_launchHandoff || t >= LaunchHandoffMs) return false;
            float p = t / (float)LaunchHandoffMs, ease = p * p * (3f - 2f * p);
            int open = (int)Math.Round((LaunchTrackY + 2) * ease);
            float progress = _launchProgress + (1f - _launchProgress) * Math.Min(1f, p * 3f);
            PaintLaunchIntro(renderer, unchecked(now - _launchStartedAt), open, progress);
            return true;
        }

        // Runs after DrawFrame (and its scene-cache capture), so the opening
        // panels never enter cached frames. Any failure simply ends the intro.
        public override void OnDraw(uint Tick)
        {
            base.OnDraw(Tick);
            if (!_launchHandoff) return;
            try { if (DrawLaunchHandoff(Renderer.Handler, Tick)) return; }
            catch { }
            ReleaseLaunchBranding();
        }

        bool DrawLaunchBranding(IntPtr renderer, uint now)
        {
            if (_launchFinished) return false;
            if (!_launchStarted)
            {
                _launchStartedAt = now; _launchStarted = true;
                // Settings are loaded by the first animated frame; no file access here.
                ResolveLaunchTheme(_cfg.Accent, _cfg.BackgroundMode, _cfg.ReduceMotion);
            }
            uint elapsed = unchecked(now - _launchStartedAt);
            RecordStartupLine(_startupDetail);
            float progress = SmoothLaunchProgress(now);
            // The intro covers normal startups. A failure, or a wait long enough
            // to need an explanation, shows the diagnostic startup panel.
            _launchIntroShown = _launchCube != IntPtr.Zero && !_startupFailed && elapsed < LaunchDetailsAfterMs;
            if (_launchIntroShown)
            {
                PaintLaunchIntro(renderer, elapsed, 0, progress);
                return true;
            }
            Fill(renderer, 0, 0, W, H, _launchBg);
            Fill(renderer, 280, 180, 1360, 600, C(14, 15, 16));
            Fill(renderer, 280, 180, 1360, 2, _launchAccent);
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
            if (_launchFinished) return;
            _launchFinished = true;
            // Only the animated intro opens over the UI; reduced motion and the
            // diagnostic panel hand over at once. The UI is live either way.
            _launchHandoff = _launchIntroShown && !_launchStill;
            _launchHandoffAt = _frameTime;
            if (!_launchHandoff) ReleaseLaunchBranding();
        }

        void ReleaseLaunchBranding()
        {
            _launchHandoff = false;
            if (_launchCube != IntPtr.Zero) { SDL_DestroyTexture(_launchCube); _launchCube = IntPtr.Zero; }
            if (_launchWordmark != IntPtr.Zero) { SDL_DestroyTexture(_launchWordmark); _launchWordmark = IntPtr.Zero; }
        }

        public override void Dispose()
        {
            StopLinkStatusLookup();
            UiAudio.Shutdown();
            _launchFinished = true;
            ReleaseLaunchBranding();
            ReleasePattern();
            foreach (var frame in _caseSizes.Values) if (frame != IntPtr.Zero) SDL_DestroyTexture(frame);
            _caseSizes.Clear();
            if (_caseFrame != IntPtr.Zero) { SDL_DestroyTexture(_caseFrame); _caseFrame = IntPtr.Zero; }
            if (_headerBrand != IntPtr.Zero) { SDL_DestroyTexture(_headerBrand); _headerBrand = IntPtr.Zero; }
            base.Dispose();
        }
    }
}
