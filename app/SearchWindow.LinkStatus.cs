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
        int _supportRowsRevision = int.MinValue;
        readonly Dictionary<PackageCandidate, bool> _supportedPackages = new Dictionary<PackageCandidate, bool>();
        IList<PackageCandidate> _supportedCandidates;

        bool PackageSupported(PackageCandidate candidate)
        {
            if (!object.ReferenceEquals(_supportedCandidates, _linkCandidates)) {
                _supportedCandidates = _linkCandidates;
                _supportedPackages.Clear();
            }
            if (candidate == null) return false;
            bool supported;
            if (!_supportedPackages.TryGetValue(candidate, out supported))
                _supportedPackages[candidate] = supported = DebridHostSupport.IsSupported(_cfg, candidate);
            return supported;
        }

        void RefreshSupportedRows()
        {
            int revision = _linkStatusLookup == null ? -1 : _linkStatusLookup.Revision;
            if (_supportRowsRevision == revision) return;
            _supportRowsRevision = revision;
            _supportedPackages.Clear();
            RebuildDetailRows();
            _detailScroll = Math.Min(_detailScroll, Math.Max(0, _detailRows.Count - 1));
            Invalidated = true;
        }

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
                    RefreshSupportedRows();
                    return;
                }
                if (object.ReferenceEquals(_linkStatusCandidates, _linkCandidates) &&
                    _linkStatusProvider == string.Join(",", UnlockProviders.EnabledIds(_cfg)) &&
                    _linkStatusRdKey == _cfg.RealDebridToken && _linkStatusTbKey == _cfg.TorBoxApiKey &&
                    _linkStatusAdKey == _cfg.AllDebridApiKey && _linkStatusPmKey == _cfg.PremiumizeApiKey &&
                    (UiElapsed(_linkStatusStartedAt) < 120000 || _tab != TopTab.Search || _settingsOpen))
                { RefreshSupportedRows(); return; }

                StopLinkStatusLookup();
                _linkStatusCandidates = _linkCandidates;
                _linkStatusProvider = string.Join(",", UnlockProviders.EnabledIds(_cfg));
                _linkStatusRdKey = _cfg.RealDebridToken;
                _linkStatusTbKey = _cfg.TorBoxApiKey;
                _linkStatusAdKey = _cfg.AllDebridApiKey;
                _linkStatusPmKey = _cfg.PremiumizeApiKey;
                _linkStatusStartedAt = UiTick();
                _linkStatusLookup = new DebridMultiProviderStatusLookup(_cfg, _linkCandidates, () => { Invalidated = true; });
                RefreshSupportedRows();
            }
        }

        void StopLinkStatusLookup()
        {
            var lookup = _linkStatusLookup;
            _linkStatusLookup = null;
            if (lookup != null) _supportRowsRevision = int.MinValue;
            _linkStatusCandidates = null;
            _linkStatusProvider = _linkStatusRdKey = _linkStatusTbKey = _linkStatusAdKey = _linkStatusPmKey = null;
            if (lookup != null) lookup.Dispose();
        }

        sealed class LinkDisplay
        {
            internal DebridHostState State;
            internal string Title, Hint;
            internal DebridProviderStatus[] Services;
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
            var display = new LinkDisplay { State = DebridHostState.Unknown, Title = "Checking enabled services", Hint = "Only supported mirrors are shown." };
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
            display.Services = services;
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
                "Support could not be confirmed. Retry the check or choose another mirror." :
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
                TextCentered(r, new SDL_Rect { x = x, y = y + 28, w = width, h = 30 }, 16, "Only mirrors supported by your enabled services are shown.", Muted);
                DrawProviderCheckingDots(r, x + width / 2 - 14, y - 13);
                return;
            }
            if (display.Services == null)
                TextCentered(r, new SDL_Rect { x = x, y = y, w = width, h = 26 }, 17, display.Title, LinkHostTone(display.State));
            else
            {
                // Colour each service independently: an available provider must
                // not paint another provider's unsupported result green.
                int count = display.Services.Length, cell = width / count;
                for (int i = 0; i < count; i++)
                {
                    var service = display.Services[i];
                    var state = service.Status == null ? DebridHostState.Unknown : service.Status.HostState;
                    bool checking = state == DebridHostState.Unknown && _linkStatusLookup != null && _linkStatusLookup.IsChecking;
                    string label = state == DebridHostState.Supported ? "supported" : state == DebridHostState.Unavailable ? "unavailable" :
                        state == DebridHostState.Unsupported ? "unsupported" : checking ? "checking" : "unverified";
                    string text = UnlockProviders.DisplayName(service.ProviderId) + ": " + label;
                    int px = count > 3 ? 14 : 17;
                    text = UiFont.EllipsizePx(text, px, cell - 12);
                    TextCentered(r, new SDL_Rect { x = x + cell * i, y = y, w = cell, h = 26 }, px, text, LinkHostTone(state));
                    if (checking) DrawProviderCheckingDots(r, x + cell * i + cell / 2 - 14, y - 13);
                }
            }
            TextCentered(r, new SDL_Rect { x = x, y = y + 28, w = width, h = 30 }, 16, display.Hint, Muted);
        }

        void DrawProviderCheckingDots(IntPtr r, int x, int y)
        {
            int phase = (int)(UiTick() / 250 % 3);
            for (int i = 0; i < 3; i++)
                Fill(r, x + i * 11, y + (i == phase ? 0 : 2), 6, 6, i == phase ? White : Dim);
        }
    }
}
