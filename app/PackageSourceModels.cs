using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Orbis
{
    /// <summary>
    /// Platform-neutral contracts for user-installed package sources. These types deliberately
    /// contain no transport, filesystem, SDL, PS4, or Link Service dependencies.
    /// </summary>
    [Flags]
    internal enum PackageSourceCapability
    {
        None = 0,
        TitlesSearch = 1,
        TitlesCatalog = 2,
        PackagesResolve = 4
    }

    internal enum PackageAccessType
    {
        Unknown = 0,
        Direct = 1,
        HosterLanding = 2
    }

    internal enum SourceFailureCode
    {
        None = 0,
        Disabled,
        Incompatible,
        PermissionDenied,
        InvalidRequest,
        InvalidResponse,
        NetworkFailure,
        LimitExceeded,
        TimedOut,
        Canceled,
        InternalError
    }

    internal sealed class PackageSourcePermission
    {
        public List<string> NetworkOrigins = new List<string>();
        public List<string> RedirectOrigins = new List<string>();
        public List<string> Methods = new List<string>();
    }

    internal sealed class PackageSourceEngineDescriptor
    {
        public string Type = "";
        public string EntryFile = "";
    }

    internal sealed class PackageSourcePublisher
    {
        public string Name = "";
        public string PublicKeyId = "";
        public string PublicKeyFingerprint = "";
    }

    internal sealed class PackageSourceDescriptor
    {
        public string Schema = "";
        public string SourceId = "";
        public string DisplayName = "";
        public string Description = "";
        public string Version = "";
        public string MinimumApiVersion = "";
        public string MaximumApiVersion = "";
        public PackageSourceCapability Capabilities;
        public PackageSourceEngineDescriptor Engine = new PackageSourceEngineDescriptor();
        public PackageSourcePermission Permissions = new PackageSourcePermission();
        public PackageSourcePublisher Publisher = new PackageSourcePublisher();
        public string UpdateDescriptorUrl = "";
        public string HomepageUrl = "";
        public string SupportUrl = "";
    }

    internal sealed class PackageSourceInstallOrigin
    {
        public string RequestedUrl = "";
        public string ResolvedPackageUrl = "";
        public string PackageSha256 = "";
        public DateTime InstalledUtc;
    }

    internal sealed class PackageSourceVersionState
    {
        public string ActiveVersion = "";
        public string PreviousVersion = "";
        public List<string> InstalledVersions = new List<string>();
    }

    internal sealed class InstalledPackageSource
    {
        public string SourceId = "";
        public bool Enabled = true;
        public int Priority;
        public PackageSourceDescriptor Descriptor = new PackageSourceDescriptor();
        public PackageSourceInstallOrigin InstallOrigin = new PackageSourceInstallOrigin();
        public PackageSourceVersionState Versions = new PackageSourceVersionState();
        public DateTime? LastSuccessfulUseUtc;
        public SourceFailureCode LastFailureCode;
        public string LastFailureMessage = "";
    }

    internal sealed class SourceSearchRequest
    {
        public string Query = "";
        public string TitleId = "";
        public string Name = "";
        public string Region = "";
        public int Limit;
        public string Cursor = "";
    }

    internal sealed class SourceResolveRequest
    {
        public string TitleId = "";
        public string Name = "";
        public string Region = "";
        public string CatalogUrl = "";
        public int Limit;
        public string Cursor = "";
    }

    internal sealed class SourceTitleResult
    {
        public string SourceId = "";
        public string SourceVersion = "";
        public string StableResultId = "";
        public string TitleId = "";
        public string DisplayName = "";
        public string Region = "";
        public string ImageUrl = "";
        public string Rating = "";
        public string Genres = "";
        public string Backport = "";
        public string SourceAttribution = "";
        public string CatalogUrl = "";
    }

    internal sealed class PackageCandidate
    {
        public string ArchiveVolumes = "";
        public string ArchivePassword = "";
        public string ResolutionError = "";
        public string SourceId = "";
        public string SourceVersion = "";
        public string CandidateId = "";
        public string TitleId = "";
        public string DisplayName = "";
        public string Region = "";
        /// <summary>base, update, dlc, or unknown.</summary>
        public string PackageKindHint = "unknown";
        /// <summary>Package/app version from source metadata, for example 1.20.</summary>
        public string PackageVersion = "";
        public string RequiredFirmware = "";
        /// <summary>Stable source-scoped identity shared by alternate mirrors of one package.</summary>
        public string PackageGroupId = "";
        /// <summary>Human-readable hoster or mirror name.</summary>
        public string HosterName = "";
        public string Label = "";
        public string SourceAttribution = "";
        public string Url = "";
        public PackageAccessType AccessType = PackageAccessType.Unknown;
        public string SourcePageUrl = "";
        public long? ExpectedByteSize;
        public string ExpectedSha256 = "";
        public string ExpectedContentId = "";
        public DateTime? ExpiresUtc;
    }

    /// <summary>
    /// Display-only normalization for resolved candidates. A missing group id deliberately
    /// produces a one-item group: it is safer to show two unknown DLCs separately than to imply
    /// that they are interchangeable mirrors.
    /// </summary>
    internal sealed class PackageCandidatePresentation
    {
        static readonly Regex VersionPattern = new Regex(
            @"(?:^|\b)(?:v(?:er(?:sion)?)?\.?\s*)?(?<v>\d{1,2}(?:\.\d{1,3}){1,3})(?:\b|$)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public PackageCandidate Candidate;
        public string Kind = "unknown";
        public string KindLabel = "OTHER";
        public string PackageTitle = "Other package";
        public string Version = "";
        public string TitleId = "UNKNOWN TITLE";
        public string Region = "REGION —";
        public string Hoster = "Unknown host";
        public string Source = "Package Source";
        public string GroupId = "";
        public int MirrorIndex = 1;
        public int MirrorCount = 1;

        public static List<PackageCandidatePresentation> Build(
            IList<PackageCandidate> candidates, string fallbackTitleId, string fallbackRegion)
        {
            var output = new List<PackageCandidatePresentation>();
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (candidates == null) return output;

            for (int i = 0; i < candidates.Count; i++)
            {
                PackageCandidate candidate = candidates[i] ?? new PackageCandidate();
                var row = Create(candidate, fallbackTitleId, fallbackRegion, i);
                output.Add(row);
                int count;
                counts.TryGetValue(row.GroupId, out count);
                counts[row.GroupId] = count + 1;
            }

            var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (PackageCandidatePresentation row in output)
            {
                int ordinal;
                ordinals.TryGetValue(row.GroupId, out ordinal);
                row.MirrorIndex = ordinal + 1;
                ordinals[row.GroupId] = row.MirrorIndex;
                row.MirrorCount = counts[row.GroupId];
            }
            return output;
        }

        static PackageCandidatePresentation Create(PackageCandidate candidate,
            string fallbackTitleId, string fallbackRegion, int index)
        {
            var row = new PackageCandidatePresentation { Candidate = candidate };
            row.Kind = EffectiveKind(candidate);
            if (row.Kind == "base") { row.KindLabel = "BASE"; row.PackageTitle = "Base game"; }
            else if (row.Kind == "update") { row.KindLabel = "UPDATE"; row.PackageTitle = "Game update"; }
            else if (row.Kind == "dlc") { row.KindLabel = "DLC"; row.PackageTitle = "Add-on content"; }
            else if (row.Kind == "backport") { row.KindLabel = "BP"; row.PackageTitle = "Backport"; }

            row.Version = NormalizeVersion(candidate.PackageVersion);
            if (row.Version.Length == 0) row.Version = ExtractVersion(candidate.Label);
            row.TitleId = First(candidate.TitleId, fallbackTitleId, "UNKNOWN TITLE").ToUpperInvariant();
            string region = NormalizeRegion(First(candidate.Region, fallbackRegion, ""));
            row.Region = region.Length == 0 ? "REGION —" : region;
            row.Hoster = First(candidate.HosterName, candidate.Label, Host(candidate.Url), "Unknown host");
            row.Source = First(candidate.SourceAttribution, candidate.SourceId, "Package Source");

            // Explicit source metadata is the only basis for declaring alternatives. Candidate
            // id / URL keeps metadata-poor rows separate instead of inventing relationships.
            string group = (candidate.PackageGroupId ?? "").Trim();
            if (group.Length > 0)
                row.GroupId = "group:" + (candidate.SourceId ?? "") + ":" + group + ":" + row.TitleId + ":" + row.Kind + ":" + row.Version + ":" + (candidate.RequiredFirmware ?? "");
            else
                row.GroupId = "single:" + First(candidate.CandidateId, candidate.Url,
                    index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return row;
        }

        static string NormalizeKind(string value)
        {
            string kind = (value ?? "").Trim().ToLowerInvariant();
            if (kind == "game") kind = "base";
            if (kind == "patch") kind = "update";
            if (kind == "addon" || kind == "add-on") kind = "dlc";
            if (kind == "bp") kind = "backport";
            return kind == "base" || kind == "update" || kind == "dlc" || kind == "backport"
                ? kind : "unknown";
        }
        internal static string EffectiveKind(PackageCandidate candidate)
        {
            string kind = NormalizeKind(candidate.PackageKindHint);
            string label = candidate.Label ?? "";
            if ((kind == "update" || kind == "unknown") && Regex.IsMatch(label, @"\b(?:backport(?:ed)?|bp)\b", RegexOptions.IgnoreCase)) return "backport";
            if (kind == "unknown" && Regex.IsMatch(label, @"\b(?:dlc|add-on|addon)\b", RegexOptions.IgnoreCase)) return "dlc";
            if (kind == "unknown" && Regex.IsMatch(label, @"\b(?:update|patch)\b", RegexOptions.IgnoreCase))
            {
                // "Game + Update 1.xx" is a merged base dump, not an update.
                // Installing that row as an update corrupts an installed title.
                if (Regex.IsMatch(label, @"\b(?:game|base)\b", RegexOptions.IgnoreCase)) return "base";
                return "update";
            }
            return kind;
        }

        static string NormalizeRegion(string value)
        {
            string s = (value ?? "").Trim().ToUpperInvariant();
            if (s == "USA" || s == "US" || s == "NTSC" || s == "NTSC-U" || s == "NTSC-U/C") return "US";
            if (s == "EUR" || s == "EU" || s == "PAL" || s == "EUROPE") return "EU";
            if (s == "JPN" || s == "JP" || s == "JAPAN") return "JP";
            if (s == "ASIA" || s == "AS" || s == "HK" || s == "HKG") return "AS";
            if (s == "?" || s == "REGION —" || s.Length == 0) return "";
            return s;
        }

        static string NormalizeVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            Match match = VersionPattern.Match(value.Trim());
            return match.Success ? match.Groups["v"].Value : "";
        }

        static string ExtractVersion(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            // Labels also contain firmware numbers; only explicit app-version markers count.
            Match match = Regex.Match(value,
                @"(?<![A-Za-z0-9])(?:v(?:er(?:sion)?)?\.?\s*|(?:update|patch)\s*[:=_-]?\s*v?)(?<v>\d{1,3}(?:\.\d{1,3}){1,3})(?![\d.])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? match.Groups["v"].Value : "";
        }

        static string Host(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)) return "";
            string host = uri.Host ?? "";
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
                ? host.Substring(4) : host;
        }

        static string First(params string[] values)
        {
            foreach (string value in values)
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            return "";
        }
    }

    internal sealed class SourceExecutionReport
    {
        public string SourceId = "";
        public string SourceVersion = "";
        public DateTime StartedUtc;
        public DateTime CompletedUtc;
        public bool Success;
        public SourceFailureCode FailureCode;
        public string Message = "";
        public int RequestCount;
        public int ResultCount;
    }

    /// <summary>
    /// Temporary compatibility bridge. Unknown preserves the legacy download routing behavior;
    /// source engines must set Direct or HosterLanding when they have authoritative knowledge.
    /// </summary>
    internal static class PackageCandidateAdapter
    {
        public static PackageCandidate FromPkgLink(PkgLink link)
        {
            if (link == null) return null;
            return new PackageCandidate
            {
                PackageKindHint = string.IsNullOrEmpty(link.Kind) ? "unknown" : link.Kind,
                Label = link.Label ?? "",
                Url = link.Url ?? "",
                AccessType = PackageAccessType.Unknown
            };
        }

        public static PkgLink ToPkgLink(PackageCandidate candidate)
        {
            if (candidate == null) return null;
            return new PkgLink
            {
                Kind = PackageCandidatePresentation.EffectiveKind(candidate),
                Label = candidate.Label ?? "",
                Url = candidate.Url ?? ""
            };
        }
    }
}
