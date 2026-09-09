using System;
using SDL2.Types;
using static SDL2.SDL;

namespace Orbis
{
    public partial class SearchWindow
    {
        IntPtr _caseFrame;
        bool _caseFrameTried;
        float _navUnderlineX = -1;
        uint _navUnderlineAt;

        static string FriendlyTransferFailure(string error)
        {
            string text = error ?? "Download failed";
            if (text.IndexOf("hoster_unsupported", StringComparison.OrdinalIgnoreCase) >= 0) return "This host is not supported by your link service. Try another mirror.";
            if (text.IndexOf("80991404", StringComparison.OrdinalIgnoreCase) >= 0) return text + " — package link expired (HTTP 404). Files kept; retry refreshes the local link.";
            return text;
        }

        void DesignCard(IntPtr r, SDL_Rect rect, bool selected)
        {
            byte level = selected ? (byte)(_cfg.ReduceMotion ? 41 : 33 + Math.Min(8, UiElapsed(_lastInputAt) / 20)) : Panel.r;
            SoftRect(r, rect, selected ? C(level, level, level) : Panel);
            StrokeRect(r, rect, selected ? Accent : Border, selected ? 2 : 1);
        }

        void DrawSwitch(IntPtr r, int x, int y, bool enabled)
        {
            SoftRect(r, new SDL_Rect { x = x, y = y, w = 52, h = 28 }, enabled ? PrimaryFill : C(66, 66, 66));
            SoftRect(r, new SDL_Rect { x = x + (enabled ? 27 : 4), y = y + 4, w = 21, h = 20 }, enabled ? PrimaryInk : C(174, 174, 174));
        }

        void DrawSettingsSave(IntPtr r, SDL_Rect sheet, int index)
        {
            int x = sheet.x + sheet.w - 258, y = sheet.y + 634;
            bool focused = _settingsFocus == index;
            var rect = new SDL_Rect { x = x, y = y, w = 258, h = 56 };
            SoftRect(r, rect, focused ? White : PrimaryFill);
            if (focused) StrokeRect(r, new SDL_Rect { x = x - 5, y = y - 5, w = 268, h = 66 }, Accent, 2);
            DesignIcon(r, "check", x + 24, y + 19, 20, PrimaryInk);
            TextPx(r, x + 62, y + 13, 21, "Save and close", PrimaryInk);
            TextPx(r, sheet.x, y + 16, 18, _settingsDirty ? "You have unsaved changes." : "Your settings are up to date.", Dim);
        }

        void DrawActivityRail(IntPtr r, SDL_Rect rect)
        {
            Fill(r, rect.x, rect.y, rect.w, rect.h, Raised);
            int length = rect.w / 4;
            int travel = _cfg.ReduceMotion ? (rect.w - length) / 2 : (int)((_frameTime % 1600) * (rect.w + length) / 1600) - length;
            int begin = Math.Max(0, travel), end = Math.Min(rect.w, travel + length);
            if (end > begin) Fill(r, rect.x + begin, rect.y, end - begin, rect.h, Accent);
        }

        void TextWrapped(IntPtr r, int x, int y, int px, int width, string text, SDL_Color ink)
        {
            string remaining = text ?? "";
            for (int line = 0; line < 3 && remaining.Length > 0; line++)
            {
                if (UiFont.MeasurePx(px, remaining) <= width) { TextPx(r, x, y, px, remaining, ink); break; }
                int split = remaining.Length;
                do { split = remaining.LastIndexOf(' ', Math.Max(0, split - 1)); }
                while (split > 0 && UiFont.MeasurePx(px, remaining.Substring(0, split)) > width);
                if (split <= 0 || line == 2) { TextFit(r, x, y, px, width, remaining, ink); break; }
                TextPx(r, x, y, px, remaining.Substring(0, split), ink);
                remaining = remaining.Substring(split + 1); y += px + 12;
            }
        }

        void TextWrappedCentered(IntPtr r, int x, int y, int px, int width, string text, SDL_Color ink)
        {
            string remaining = text ?? "";
            for (int line = 0; line < 3 && remaining.Length > 0; line++)
            {
                if (UiFont.MeasurePx(px, remaining) <= width)
                {
                    TextCentered(r, new SDL_Rect { x = x, y = y, w = width, h = px + 12 }, px, remaining, ink);
                    break;
                }
                int split = remaining.Length;
                do { split = remaining.LastIndexOf(' ', Math.Max(0, split - 1)); }
                while (split > 0 && UiFont.MeasurePx(px, remaining.Substring(0, split)) > width);
                if (split <= 0 || line == 2) { TextFit(r, x, y, px, width, remaining, ink); break; }
                TextCentered(r, new SDL_Rect { x = x, y = y, w = width, h = px + 12 }, px, remaining.Substring(0, split), ink);
                remaining = remaining.Substring(split + 1); y += px + 12;
            }
        }

        // Small line icons use cached integer geometry, not font substitutes or per-frame images.
        void DesignIcon(IntPtr r, string kind, int x, int y, int size, SDL_Color ink)
        {
            SDL_SetRenderDrawColor(r, ink.r, ink.g, ink.b, 255);
            if (kind == "search")
            {
                int radius = size * 31 / 100, cx = x + radius + 1, cy = y + radius + 1;
                CircleLine(r, cx, cy, radius);
                for (int n = 0; n < 2; n++) SDL_RenderDrawLine(r, x + size * 62 / 100, y + size * 62 / 100 + n, x + size - 1, y + size - 1 + n);
            }
            else if (kind == "check") { SDL_RenderDrawLine(r, x, y + size / 2, x + size / 3, y + size - 2); SDL_RenderDrawLine(r, x + size / 3, y + size - 2, x + size, y + 1); }
            else if (kind == "chevron") { SDL_RenderDrawLine(r, x + size / 3, y, x + size * 2 / 3, y + size / 2); SDL_RenderDrawLine(r, x + size * 2 / 3, y + size / 2, x + size / 3, y + size); }
            else if (kind == "error") { CircleLine(r, x + size / 2, y + size / 2, size / 2 - 1); Fill(r, x + size / 2 - 1, y + size / 4, 2, size / 3, ink); Fill(r, x + size / 2 - 1, y + size * 3 / 4, 2, 2, ink); }
            else if (kind == "settings")
            {
                CircleLine(r, x + size / 2, y + size / 2, size / 4);
                CircleLine(r, x + size / 2, y + size / 2, size * 2 / 5);
                for (int i = 0; i < 8; i++) { double a = i * Math.PI / 4; SDL_RenderDrawLine(r, x + size / 2 + (int)(Math.Cos(a) * size * .38), y + size / 2 + (int)(Math.Sin(a) * size * .38), x + size / 2 + (int)(Math.Cos(a) * size * .5), y + size / 2 + (int)(Math.Sin(a) * size * .5)); }
            }
            else if (kind == "phone")
            {
                StrokeRect(r, new SDL_Rect { x = x + size / 5, y = y, w = size * 3 / 5, h = size }, ink, 2);
                Fill(r, x + size * 2 / 5, y + size - 7, size / 5, 2, ink);
            }
            else if (kind == "library")
            {
                for (int i = 0; i < 3; i++) StrokeRect(r, new SDL_Rect { x = x + i * size / 3, y = y + i * 3, w = size / 4, h = size - 6 }, ink, 2);
            }
            else
            {
                SDL_RenderDrawLine(r, x + size / 2, y, x + size / 2, y + size * 2 / 3);
                SDL_RenderDrawLine(r, x + size / 4, y + size * 5 / 12, x + size / 2, y + size * 2 / 3);
                SDL_RenderDrawLine(r, x + size * 3 / 4, y + size * 5 / 12, x + size / 2, y + size * 2 / 3);
                SDL_RenderDrawLine(r, x + 2, y + size * 3 / 4, x + 2, y + size);
                SDL_RenderDrawLine(r, x + size - 2, y + size * 3 / 4, x + size - 2, y + size);
                SDL_RenderDrawLine(r, x + 2, y + size, x + size - 2, y + size);
            }
        }

        static void CircleLine(IntPtr r, int cx, int cy, int radius)
        {
            int x = radius, y = 0, error = 1 - radius;
            while (x >= y)
            {
                SDL_RenderDrawPoint(r, cx + x, cy + y); SDL_RenderDrawPoint(r, cx + y, cy + x);
                SDL_RenderDrawPoint(r, cx - y, cy + x); SDL_RenderDrawPoint(r, cx - x, cy + y);
                SDL_RenderDrawPoint(r, cx - x, cy - y); SDL_RenderDrawPoint(r, cx - y, cy - x);
                SDL_RenderDrawPoint(r, cx + y, cy - x); SDL_RenderDrawPoint(r, cx + x, cy - y);
                y++;
                if (error < 0) error += 2 * y + 1;
                else { x--; error += 2 * (y - x) + 1; }
            }
        }
    }
}
