namespace Orbis
{
    internal enum UiBackgroundSurface
    {
        SearchLanding,
        Content,
        Settings
    }

    internal struct UiSurfacePolicy
    {
        public bool Glass;
        public int Radius;
        public byte FillAlpha;
        public byte TopHighlightAlpha;
        public int BlurRadius;
    }

    internal struct UiMotionPolicy
    {
        public int TabFadeMs;
        public int CoverCrossfadeMs;
        public int FocusMs;
        public int FocusLiftPx;
        public int ToastMs;
        public bool AnimateCloud;
    }

    internal struct UiBackgroundPolicy
    {
        public string Mode;
        public string ImagePath;
        public bool UseFocusedCover;
        public bool ShowCoverCloud;
        public byte DarkenPercent;
        public byte AccentWashPercent;
        public int ImageBlurRadius;
    }

    /// <summary>
    /// Cheap, allocation-free rendering decisions. The renderer can skip blur and
    /// intermediate animation surfaces entirely when Flat or Reduce Motion is active.
    /// </summary>
    internal static class UiVisualPolicies
    {
        public static UiSurfacePolicy Surface(AppSettings settings, bool blurSupported)
        {
            bool glass = false;
            return new UiSurfacePolicy
            {
                Glass = glass,
                Radius = 12,
                FillAlpha = 255,
                TopHighlightAlpha = glass ? (byte)34 : (byte)0,
                BlurRadius = glass ? 12 : 0
            };
        }

        public static UiMotionPolicy Motion(AppSettings settings)
        {
            bool reduced = settings != null && settings.ReduceMotion;
            return new UiMotionPolicy
            {
                TabFadeMs = reduced ? 0 : 120,
                CoverCrossfadeMs = reduced ? 0 : 200,
                FocusMs = reduced ? 0 : 120,
                FocusLiftPx = reduced ? 0 : 4,
                ToastMs = 2000,
                AnimateCloud = !reduced
            };
        }

        public static UiBackgroundPolicy Background(AppSettings settings,
            UiBackgroundSurface surface)
        {
            string mode = settings == null ? AppSettings.BackgroundAdaptive : settings.BackgroundMode;
            bool adaptive = mode == AppSettings.BackgroundAdaptive;
            bool customImage = mode == AppSettings.BackgroundImage ||
                mode == AppSettings.BackgroundShowcase;
            return new UiBackgroundPolicy
            {
                Mode = mode,
                ImagePath = settings == null ? "" : settings.BackgroundImagePath,
                UseFocusedCover = adaptive && surface == UiBackgroundSurface.Content,
                ShowCoverCloud = false,
                DarkenPercent = customImage ? (byte)60 : (byte)0,
                AccentWashPercent = mode == AppSettings.BackgroundBlack ? (byte)0 :
                    surface == UiBackgroundSurface.Settings ? (byte)6 : (byte)10,
                ImageBlurRadius = 0
            };
        }

        public static int CloudCoverCount(AppSettings settings)
        {
            if (settings == null || settings.CoverCloudDensity == AppSettings.CloudDensityNormal) return 12;
            if (settings.CoverCloudDensity == AppSettings.CloudDensityFew) return 6;
            if (settings.CoverCloudDensity == AppSettings.CloudDensityDense) return 18;
            return 12;
        }
    }
}
