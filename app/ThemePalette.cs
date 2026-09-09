using System;
using System.Collections.Generic;
using System.Globalization;

namespace Orbis
{
    internal struct ThemeColor
    {
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;
        public readonly byte A;

        public ThemeColor(byte r, byte g, byte b, byte a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public ThemeColor(byte r, byte g, byte b) : this(r, g, b, 255) { }

        public string ToHex()
        {
            return string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", R, G, B);
        }
    }

    internal sealed class ThemeAccentPreset
    {
        public readonly string Name;
        public readonly string Hex;
        public readonly ThemeColor Color;

        public ThemeAccentPreset(string name, string hex)
        {
            Name = name;
            Hex = hex;
            ThemeColor color;
            if (!ThemePalette.TryParseHex(hex, out color))
                throw new ArgumentException("Invalid preset color", "hex");
            Color = color;
        }
    }

    /// <summary>Global chrome accent plus status colors that never follow it.</summary>
    internal static class ThemePalette
    {
        public const string DefaultAccentName = "Charcoal";
        public const string DefaultAccentHex = "#E4E4E1";

        static readonly ThemeAccentPreset[] _presets =
        {
            new ThemeAccentPreset("Charcoal", "#E4E4E1"),
            new ThemeAccentPreset("Steel Ice", "#7EB6FF"),
            new ThemeAccentPreset("Arctic", "#E8EEF7"),
            new ThemeAccentPreset("Violet", "#A78BFA"),
            new ThemeAccentPreset("Crimson", "#FF4D4D"),
            new ThemeAccentPreset("Amber", "#F5A623"),
            new ThemeAccentPreset("Teal", "#2EE6C5")
        };

        public static IList<ThemeAccentPreset> Presets
        {
            get { return Array.AsReadOnly(_presets); }
        }

        public static readonly ThemeColor Good = new ThemeColor(58, 203, 114);
        public static readonly ThemeColor Degraded = new ThemeColor(245, 166, 35);
        public static readonly ThemeColor Fail = new ThemeColor(255, 77, 77);
        public static readonly ThemeColor Dim = new ThemeColor(112, 118, 128);

        public static ThemeColor Accent(AppSettings settings)
        {
            ThemeColor color;
            ThemeAccentPreset preset;
            return settings != null && TryResolveAccent(settings.Accent, out color, out preset)
                ? color : _presets[0].Color;
        }

        public static bool TryResolveAccent(string value, out ThemeColor color,
            out ThemeAccentPreset preset)
        {
            preset = null;
            if (!TryParseHex(value, out color) || IsRejectedMidGray(color)) return false;
            foreach (ThemeAccentPreset candidate in _presets)
            {
                if (string.Equals(candidate.Hex, color.ToHex(), StringComparison.OrdinalIgnoreCase))
                {
                    preset = candidate;
                    break;
                }
            }
            return true;
        }

        public static bool TryParseHex(string value, out ThemeColor color)
        {
            color = new ThemeColor();
            if (string.IsNullOrEmpty(value) || value.Length != 7 || value[0] != '#') return false;
            int rgb;
            if (!int.TryParse(value.Substring(1), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out rgb)) return false;
            color = new ThemeColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }

        public static bool IsRejectedMidGray(ThemeColor color)
        {
            return color.R == color.G && color.G == color.B && color.R >= 0x55 && color.R <= 0x99;
        }
    }
}
