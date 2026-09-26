using System;
using System.Collections.Generic;
using SDL2.Types;
using static SDL2.SDL;

namespace Orbis
{
    public partial class SearchWindow
    {
        // Short, ease-out motion only: focus glides and fades, no bounces or loops.
        // Everything here is opaque fills and copies: the PS4 renderer is
        // software, and per-pixel blending of large areas would cost more frame
        // time than the motion is worth.
        const uint GlideMs = 140;
        const uint RevealMs = 180;
        const uint GlideStaleMs = 300;
        const uint PressMs = 140;
        const uint SwitchMs = 120;
        const uint ProgressSmoothMs = 160;

        public const int GlideLibrary = 0, GlidePoster = 1, GlideOverlay = 2, GlideList = 3, GlideKey = 4,
            GlideSettings = 5, GlideChips = 6, GlideTabs = 7, GlideHalo = 8;
        const int GlideSlots = 9;
        readonly SDL_Rect[] _glideFrom = new SDL_Rect[GlideSlots];
        readonly SDL_Rect[] _glideTo = new SDL_Rect[GlideSlots];
        readonly uint[] _glideAt = new uint[GlideSlots];
        readonly uint[] _glideSeen = new uint[GlideSlots];

        internal static float EaseOut(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;
            float inv = 1f - t;
            return 1f - inv * inv * inv;
        }

        // 0..1 progress of a reveal that started at startedAt; 1 when motion is reduced.
        float Reveal(uint startedAt, uint durationMs)
        {
            if (_cfg.ReduceMotion || startedAt == 0) return 1f;
            return EaseOut(UiElapsed(startedAt) / (float)durationMs);
        }

        // Returns the rect to draw this frame for a focus ring that moves between targets.
        SDL_Rect Glide(int slot, SDL_Rect target)
        {
            uint now = UiTick();
            bool stale = _glideSeen[slot] == 0 || unchecked(now - _glideSeen[slot]) > GlideStaleMs;
            _glideSeen[slot] = now;
            if (_cfg.ReduceMotion || stale) {
                _glideFrom[slot] = _glideTo[slot] = target; _glideAt[slot] = 0;
                return target;
            }
            var to = _glideTo[slot];
            if (to.x != target.x || to.y != target.y || to.w != target.w || to.h != target.h) {
                _glideFrom[slot] = CurrentGlide(slot);
                _glideTo[slot] = target;
                _glideAt[slot] = now;
            }
            return CurrentGlide(slot);
        }

        SDL_Rect CurrentGlide(int slot)
        {
            var a = _glideFrom[slot]; var b = _glideTo[slot];
            if (_glideAt[slot] == 0) return b;
            float t = EaseOut(UiElapsed(_glideAt[slot]) / (float)GlideMs);
            if (t >= 1f) { _glideAt[slot] = 0; return b; }
            return new SDL_Rect {
                x = a.x + (int)Math.Round((b.x - a.x) * t), y = a.y + (int)Math.Round((b.y - a.y) * t),
                w = a.w + (int)Math.Round((b.w - a.w) * t), h = a.h + (int)Math.Round((b.h - a.h) * t)
            };
        }

        // Fades content in by drawing a shrinking veil of the surface colour over it.
        void RevealVeil(IntPtr r, SDL_Rect area, SDL_Color surface, uint startedAt, uint durationMs)
        {
            float t = Reveal(startedAt, durationMs);
            if (t >= 1f) return;
            FillAlpha(r, area.x, area.y, area.w, area.h, surface, (byte)Math.Round((1f - t) * 235));
        }

        // Cross on a focused card or row: a brief brighter press before the action lands.
        uint _pressAt;
        void MarkPress() { _pressAt = UiTick(); }
        bool PressActive() { return !_cfg.ReduceMotion && _pressAt != 0 && UiElapsed(_pressAt) < PressMs; }

        // Displayed progress eases toward the reported value, so a bar that is
        // updated once a second moves smoothly instead of stepping. Decreases (a
        // retry or a new attempt) and first sightings are shown immediately.
        struct SmoothState { public double Shown; public uint At; }
        readonly Dictionary<string, SmoothState> _smoothed = new Dictionary<string, SmoothState>(StringComparer.Ordinal);
        double SmoothedProgress(string id, double target)
        {
            if (_cfg.ReduceMotion || string.IsNullOrEmpty(id)) return target;
            uint now = UiTick();
            SmoothState state;
            if (!_smoothed.TryGetValue(id, out state) || target < state.Shown || unchecked(now - state.At) > 2000)
            {
                if (_smoothed.Count >= 64 && !_smoothed.ContainsKey(id)) _smoothed.Clear();
                _smoothed[id] = new SmoothState { Shown = target, At = now };
                return target;
            }
            double dt = unchecked(now - state.At);
            state.Shown += (target - state.Shown) * (1 - Math.Exp(-dt / ProgressSmoothMs));
            if (target - state.Shown < 0.0005) state.Shown = target;
            state.At = now;
            _smoothed[id] = state;
            return state.Shown;
        }

        // Switch knobs slide to their new side. Keyed by settings page and position.
        struct SwitchState { public bool On; public uint At; }
        readonly Dictionary<int, SwitchState> _switches = new Dictionary<int, SwitchState>();
        float SwitchPosition(int x, int y, bool enabled)
        {
            int key = (_settingsPage & 0xFF) << 22 | (x & 0x7FF) << 11 | (y & 0x7FF);
            SwitchState state;
            if (!_switches.TryGetValue(key, out state)) { _switches[key] = new SwitchState { On = enabled }; return enabled ? 1f : 0f; }
            if (state.On != enabled) { state = new SwitchState { On = enabled, At = UiTick() }; _switches[key] = state; }
            float t = Reveal(state.At, SwitchMs);
            return enabled ? t : 1f - t;
        }

        // Wipes: a box opens from its bottom edge as it enters and closes as it
        // leaves. The visible part is drawn as a shorter box, and callers draw a
        // line of content only once its top is inside it. SDL clip rectangles are
        // not used: a separately flushed clip command trips a viewport assertion
        // in the PS4 software renderer, which stops the UI thread.
        static SDL_Rect WipeRect(SDL_Rect box, float shown)
        {
            if (shown >= 1f) return box;
            int h = shown <= 0f ? 0 : Math.Max(1, (int)Math.Round(box.h * shown));
            return new SDL_Rect { x = box.x, y = box.y + box.h - h, w = box.w, h = h };
        }
    }
}
