using System;
using System.Collections.Generic;
using SDL2.Types;
using static SDL2.SDL;

namespace Orbis
{
    public partial class SearchWindow
    {
        DebridMultiProviderStatusLookup _linkStatusLookup;
        IList<PackageCandidate> _linkStatusCandidates;
        string _linkStatusProvider, _linkStatusRdKey, _linkStatusTbKey, _linkStatusAdKey, _linkStatusPmKey;
        uint _linkStatusStartedAt;

        bool ShowsProviderStatus
        {
            get
            {
                return _cfg.HasActiveUnlock;
            }
        }

        void RefreshLinkStatusLookup()
        {
            if (_linkLookupPolled != 0 && UiElapsed(_linkLookupPolled) < 250) return;
            _linkLookupPolled = UiTick();
            lock (_lock)
            {
                if (!_launchFinished || _screen != BrowseScreen.Detail || !ShowsProviderStatus ||
                    _linkCandidates.Count == 0)
                {
                    StopLinkStatusLookup();
                    return;
                }
                if (object.ReferenceEquals(_linkStatusCandidates, _linkCandidates) &&
                    _linkStatusProvider == string.Join(",", UnlockProviders.EnabledIds(_cfg)) &&
                    _linkStatusRdKey == _cfg.RealDebridToken && _linkStatusTbKey == _cfg.TorBoxApiKey &&
                    _linkStatusAdKey == _cfg.AllDebridApiKey && _linkStatusPmKey == _cfg.PremiumizeApiKey &&
                    (UiElapsed(_linkStatusStartedAt) < 120000 || _tab != TopTab.Search || _settingsOpen))
                    return;

                StopLinkStatusLookup();
                _linkStatusCandidates = _linkCandidates;
                _linkStatusProvider = string.Join(",", UnlockProviders.EnabledIds(_cfg));
                _linkStatusRdKey = _cfg.RealDebridToken;
                _linkStatusTbKey = _cfg.TorBoxApiKey;
                _linkStatusAdKey = _cfg.AllDebridApiKey;
                _linkStatusPmKey = _cfg.PremiumizeApiKey;
                _linkStatusStartedAt = UiTick();
                _linkStatusLookup = new DebridMultiProviderStatusLookup(_cfg, _linkCandidates, () => { Invalidated = true; });
            }
        }

        void StopLinkStatusLookup()
        {
            var lookup = _linkStatusLookup;
            _linkStatusLookup = null;
            _linkStatusCandidates = null;
            _linkStatusProvider = _linkStatusRdKey = _linkStatusTbKey = _linkStatusAdKey = _linkStatusPmKey = null;
            if (lookup != null) lookup.Dispose();
        }

        sealed class LinkDisplay
        {
            internal DebridHostState State;
            internal string Title, Hint;
        }
        readonly Dictionary<PackageCandidate, LinkDisplay> _linkDisplays = new Dictionary<PackageCandidate, LinkDisplay>();
        readonly Dictionary<string, DebridHostState> _groupSupport = new Dictionary<string, DebridHostState>(StringComparer.OrdinalIgnoreCase);
        DebridMultiProviderStatusLookup _displayLookup;
        IList<PackageCandidatePresentation> _displayPresentation;
        int _displayRevision = -1;
        uint _linkLookupPolled;

        void RefreshLinkDisplayCache()
        {
            int revision = _linkStatusLookup == null ? -1 : _linkStatusLookup.Revision;
            if (_displayLookup == _linkStatusLookup && _displayRevision == revision &&
                object.ReferenceEquals(_displayPresentation, _linkPresentation)) return;
            _displayLookup = _linkStatusLookup; _displayRevision = revision; _displayPresentation = _linkPresentation;
            _linkDisplays.Clear(); _groupSupport.Clear();
        }

        LinkDisplay LinkDisplayFor(PackageCandidate candidate)
        {
            RefreshLinkDisplayCache();
            if (candidate == null) return BuildLinkDisplay(null, null);
            LinkDisplay display;
            if (!_linkDisplays.TryGetValue(candidate, out display))
            {
                display = BuildLinkDisplay(candidate, _linkStatusLookup == null ? null : _linkStatusLookup.GetAll(candidate));
                _linkDisplays[candidate] = display;
            }
            return display;
        }

        static LinkDisplay BuildLinkDisplay(PackageCandidate candidate, DebridProviderStatus[] services)
        {
            var display = new LinkDisplay { State = DebridHostState.Unknown, Title = "Checking enabled services", Hint = "All mirrors remain available." };
            if (candidate == null) return display;
            if (!string.IsNullOrEmpty(candidate.ResolutionError) ||
                (string.IsNullOrWhiteSpace(candidate.Url) && string.IsNullOrWhiteSpace(candidate.ArchiveVolumes)))
            {
                display.State = DebridHostState.Unsupported; display.Title = "This mirror needs attention";
                display.Hint = "Refresh this package or choose another mirror."; return display;
            }
            if (candidate.AccessType == PackageAccessType.Direct && string.IsNullOrEmpty(candidate.ArchiveVolumes))
            {
                display.State = DebridHostState.Supported; display.Title = "Direct file";
                display.Hint = "A link service is not required for this mirror."; return display;
            }
            if (services == null || services.Length == 0) return display;
            var labels = new List<string>(); var supported = new List<string>(); var notes = new List<string>();
            bool unknown = false;
            foreach (var service in services)
            {
                var state = service.Status == null ? DebridHostState.Unknown : service.Status.HostState;
                string name = UnlockProviders.DisplayName(service.ProviderId);
                string label = state == DebridHostState.Supported ? "supported" : state == DebridHostState.Unknown ? "unverified" :
                    state == DebridHostState.Unavailable ? "host offline" : "unsupported";
                labels.Add(name + ": " + label);
                if (state == DebridHostState.Supported) supported.Add(name);
                if (state == DebridHostState.Unknown) unknown = true;
                if (service.Status != null && !string.IsNullOrEmpty(service.Status.AccountNote)) notes.Add(name + ": " + service.Status.AccountNote);
            }
            display.Title = string.Join("  ·  ", labels);
            display.State = supported.Count > 0 ? DebridHostState.Supported : unknown ? DebridHostState.Unknown : DebridHostState.Unsupported;
            display.Hint = notes.Count > 0 ? string.Join("  ·  ", notes) : supported.Count > 0 ?
                "Use " + string.Join(" or ", supported) + ". Host support is confirmed; account and file limits still apply." : unknown ?
                "Support is unconfirmed. You can try this mirror or select another." :
                "Choose another mirror or enable a service that supports this host in Connections.";
            return display;
        }

        DebridHostState PackageLinkState(int index, bool child)
        {
            var meta = _linkPresentation[index];
            if (child) return LinkDisplayFor(meta.Candidate).State;
            RefreshLinkDisplayCache();
            string group = DetailGroupKey(meta); DebridHostState result;
            if (_groupSupport.TryGetValue(group, out result)) return result;
            result = DebridHostState.Unsupported;
            foreach (var mirror in _linkPresentation)
            {
                if (!string.Equals(DetailGroupKey(mirror), group, StringComparison.OrdinalIgnoreCase)) continue;
                var state = LinkDisplayFor(mirror.Candidate).State;
                if (state == DebridHostState.Supported) { result = state; break; }
                if (state == DebridHostState.Unknown) result = state;
            }
            _groupSupport[group] = result;
            return result;
        }

        static SDL_Color LinkHostTone(DebridHostState state)
        {
            return state == DebridHostState.Supported ? C(131, 207, 164) : state == DebridHostState.Unknown ? C(134, 140, 148) : C(229, 119, 134);
        }

        void DrawSelectedLinkGuidance(IntPtr r, int x, int y, int width, PackageCandidate candidate)
        {
            var display = LinkDisplayFor(candidate);
            if (display.State == DebridHostState.Unknown && (_linkStatusLookup == null || _linkStatusLookup.IsChecking))
            {
                TextCentered(r, new SDL_Rect { x = x, y = y, w = width, h = 26 }, 17, "Checking enabled services", Muted);
                TextCentered(r, new SDL_Rect { x = x, y = y + 28, w = width, h = 30 }, 16, "All mirrors remain available while host support is checked.", Muted);
                int phase = (int)(UiTick() / 250 % 3);
                for (int i = 0; i < 3; i++) Fill(r, x + 12 + i * 9, y + 10, 4, 4, i == phase ? White : Dim);
                return;
            }
            TextCentered(r, new SDL_Rect { x = x, y = y, w = width, h = 26 }, 17, display.Title, LinkHostTone(display.State));
            TextCentered(r, new SDL_Rect { x = x, y = y + 28, w = width, h = 30 }, 16, display.Hint, Muted);
        }
    }
}
