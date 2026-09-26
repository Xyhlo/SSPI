using System;
using System.Collections.Generic;
using SDL2.Types;
using static SDL2.SDL;

namespace Orbis
{
    // Game detail page: per-package status chips and a region notice. Package
    // lists are only known for regions resolved in this session, so the notice
    // compares the shown region with regions that were already opened and
    // otherwise points to the regions that have not been checked yet.
    public partial class SearchWindow
    {
        sealed class RegionPackageSummary
        {
            public string Region;
            public int Base, Update, Dlc, Other;
            public int Total { get { return Base + Update + Dlc + Other; } }
            public int Score { get { return (Base > 0 ? 1000 : 0) + Total; } }
        }

        readonly Dictionary<string, RegionPackageSummary> _regionSummaries =
            new Dictionary<string, RegionPackageSummary>(StringComparer.OrdinalIgnoreCase);
        IList<PackageCandidatePresentation> _regionModelFor;
        GameHit _regionModelSelection;
        bool _regionModelInstalled;
        int _regionModelSupport;
        RegionPackageSummary _currentRegionSummary;
        string[] _packageStatusText = new string[0];
        SDL_Color[] _packageStatusTone = new SDL_Color[0];
        string _regionNoticeHeadline, _regionNoticeDetail, _regionNoticeAction;
        int _regionNoticeTarget = -1;

        static string RegionVariantKey(GameHit hit)
        {
            if (hit == null) return "";
            return (hit.TitleId ?? "").Trim() + "|" + (hit.Region ?? "").Trim() + "|" + (hit.Source ?? "").Trim();
        }

        static string RegionName(GameHit hit)
        {
            string region = hit == null ? "" : (hit.Region ?? "").Trim();
            return region.Length == 0 || region == "?" ? "Another region" : region;
        }

        static string PackageCountText(int count)
        {
            return count + (count == 1 ? " package" : " packages");
        }

        // Rebuilt only when the resolved package list, the selection, the library
        // state or the host-support answers change; drawing then reads cached strings.
        void RefreshDetailRegionModel(bool installed)
        {
            if (object.ReferenceEquals(_regionModelFor, _linkPresentation) &&
                object.ReferenceEquals(_regionModelSelection, _selected) &&
                _regionModelInstalled == installed && _regionModelSupport == _supportSnapshot) return;
            _regionModelFor = _linkPresentation; _regionModelSelection = _selected;
            _regionModelInstalled = installed;

            var summary = new RegionPackageSummary { Region = _selected == null ? "" : _selected.Region };
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var presentation = _linkPresentation;
            foreach (var meta in presentation)
            {
                if (!PackageSupported(meta.Candidate) || !groups.Add(DetailGroupKey(meta))) continue;
                switch (PackageKindOrder(meta.Kind))
                {
                    case 0: summary.Base++; break;
                    case 1: summary.Update++; break;
                    case 2: summary.Dlc++; break;
                    default: summary.Other++; break;
                }
            }
            // Read after the loop: PackageSupported may start a new snapshot.
            _regionModelSupport = _supportSnapshot;
            _currentRegionSummary = summary;
            if (_selected != null && string.IsNullOrEmpty(_resolveError))
            {
                if (_regionSummaries.Count > 256) _regionSummaries.Clear();
                _regionSummaries[RegionVariantKey(_selected)] = summary;
            }

            _packageStatusText = new string[presentation.Count];
            _packageStatusTone = new SDL_Color[presentation.Count];
            bool baseAvailable = installed || summary.Base > 0;
            for (int i = 0; i < presentation.Count; i++)
            {
                var meta = presentation[i]; var candidate = meta.Candidate;
                string text; SDL_Color tone;
                string required = candidate == null ? "" : candidate.RequiredFirmware;
                if (candidate != null && string.IsNullOrEmpty(required))
                    required = PkgIntegrity.FirmwareRequirement(candidate.Label + " " + candidate.DisplayName);
                if (candidate != null && !string.IsNullOrEmpty(candidate.ResolutionError)) { text = "Needs another mirror"; tone = Warning; }
                else if (!string.IsNullOrEmpty(required) &&
                    PkgIntegrity.FirmwareLabel(required, _firmwareVersion).StartsWith("Needs backport", StringComparison.Ordinal)) { text = "Needs backport"; tone = Warning; }
                else if (PackageKindOrder(meta.Kind) == 0) { text = "Ready"; tone = Ok; }
                else if (SamePackageKind(meta.Kind, "unknown")) { text = "Check details"; tone = Muted; }
                else if (baseAvailable) { text = "Ready"; tone = Ok; }
                else { text = "Needs base game"; tone = Warning; }
                _packageStatusText[i] = text; _packageStatusTone[i] = tone;
            }
            RebuildRegionNotice();
        }

        void RebuildRegionNotice()
        {
            _regionNoticeHeadline = _regionNoticeDetail = _regionNoticeAction = null;
            _regionNoticeTarget = -1;
            var current = _currentRegionSummary;
            if (_selected == null || current == null || _selected.Variants == null || _selected.Variants.Count < 2 ||
                !string.IsNullOrEmpty(_resolveError)) return;
            string currentKey = RegionVariantKey(_selected);
            var variants = _selected.Variants;
            int best = -1, currentIndex = -1, uncheckedCount = 0, firstUnchecked = -1;
            RegionPackageSummary bestSummary = null;
            for (int i = 0; i < variants.Count; i++)
            {
                string key = RegionVariantKey(variants[i]);
                if (string.Equals(key, currentKey, StringComparison.OrdinalIgnoreCase)) { if (currentIndex < 0) currentIndex = i; continue; }
                RegionPackageSummary summary;
                if (!_regionSummaries.TryGetValue(key, out summary))
                {
                    uncheckedCount++;
                    if (firstUnchecked < 0) firstUnchecked = i;
                    continue;
                }
                if (summary.Score > current.Score && (bestSummary == null || summary.Score > bestSummary.Score))
                { best = i; bestSummary = summary; }
            }
            if (bestSummary != null)
            {
                int more = bestSummary.Total - current.Total;
                string name = RegionName(variants[best]);
                if (bestSummary.Base > 0 && current.Base == 0)
                {
                    _regionNoticeHeadline = current.Total == 0 ? "No packages in this region" :
                        current.Update > 0 && current.Dlc == 0 && current.Other == 0 ? "Updates only in this region" : "No base game in this region";
                    _regionNoticeDetail = name + " has the base game" + (more > 0 ? " and " + more + " more" : "");
                }
                else
                {
                    _regionNoticeHeadline = "Other regions have " + more + " more" + (more == 1 ? " package" : " packages");
                    _regionNoticeDetail = name + " lists " + PackageCountText(bestSummary.Total);
                }
                _regionNoticeAction = "Switch to " + name;
                _regionNoticeTarget = best;
                return;
            }
            if (current.Base == 0 && firstUnchecked >= 0)
            {
                _regionNoticeHeadline = current.Total == 0 ? "No packages in this region" :
                    current.Update > 0 && current.Dlc == 0 && current.Other == 0 ? "Updates only in this region" : "No base game in this region";
                _regionNoticeDetail = uncheckedCount + (uncheckedCount == 1 ? " other region not checked yet" : " other regions not checked yet");
                _regionNoticeAction = "Check " + RegionName(variants[firstUnchecked]);
                _regionNoticeTarget = firstUnchecked;
            }
        }

        // L2 target: the most complete known region, then an unchecked region
        // when this one has no base game; -1 keeps the plain region cycle.
        int PreferredRegionVariant()
        {
            RefreshDetailRegionModel(_selected != null && _libraryGames.Exists(g =>
                string.Equals(g.TitleId, _selected.TitleId, StringComparison.OrdinalIgnoreCase)));
            return _regionNoticeTarget;
        }

        bool HasRegionNotice { get { return !string.IsNullOrEmpty(_regionNoticeHeadline); } }

        void DrawRegionNotice(IntPtr r, int x, int y, int w)
        {
            var rect = new SDL_Rect { x = x, y = y, w = w, h = 52 };
            SoftRect(r, rect, Raised);
            Fill(r, x + 10, y + 12, 3, 28, Warning);
            string action = _regionNoticeAction ?? "Switch region";
            int actionW = UiFont.MeasurePx(18, action);
            int actionX = x + w - 22 - actionW;
            TextPx(r, actionX, y + 14, 18, action, White);
            GamepadIcons.Draw(r, "l2", actionX - 36, y + 13, 26);
            int textRight = actionX - 60;
            int headW = Math.Min(textRight - (x + 28), UiFont.MeasurePx(19, _regionNoticeHeadline));
            TextFit(r, x + 28, y + 13, 19, headW, _regionNoticeHeadline, White);
            int detailX = x + 28 + headW + 20;
            if (!string.IsNullOrEmpty(_regionNoticeDetail) && textRight - detailX > 80)
                TextFit(r, detailX, y + 15, 17, textRight - detailX, "·  " + _regionNoticeDetail, Muted);
        }

        string PackageStatusText(int index, out SDL_Color tone)
        {
            tone = Muted;
            if (index < 0 || index >= _packageStatusText.Length || !object.ReferenceEquals(_regionModelFor, _linkPresentation)) return null;
            tone = _packageStatusTone[index];
            return _packageStatusText[index];
        }

        static string PackageGroupCaption(string kind)
        {
            switch (PackageKindOrder(kind))
            {
                case 0: return "BASE GAME";
                case 1: return "UPDATE";
                case 2: return "DLC";
                default: return SamePackageKind(kind, "backport") ? "BACKPORT" : "OTHER";
            }
        }
    }
}
