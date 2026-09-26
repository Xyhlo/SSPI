using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Orbis
{
    internal sealed class PackageMirror
    {
        internal string Url, CandidateId, AccessType, SourcePageUrl, SourceAttribution;
        internal string ExpectedSha256, ExpectedContentId, ExpiresUtc, ArchivePassword;
        internal string ArchivePasswords;
        internal long ExpectedByteSize;
    }

    internal static class PackageMirrorFallback
    {
        internal static string Encode(PackageCandidate selected, IList<PackageCandidate> candidates)
        {
            var output = new StringBuilder("{\"mirrors\":[");
            int count = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal) { selected.Url ?? "" };
            if (candidates != null)
                foreach (var candidate in candidates)
                {
                    if (!Compatible(selected, candidate) || !seen.Add(candidate.Url)) continue;
                    if (count++ > 0) output.Append(',');
                    output.Append('{');
                    Field(output, "url", candidate.Url);
                    Field(output, "candidate_id", candidate.CandidateId);
                    Field(output, "access_type", candidate.AccessType.ToString());
                    Field(output, "source_page_url", candidate.SourcePageUrl);
                    Field(output, "source_attribution", candidate.SourceAttribution);
                    Field(output, "sha256", candidate.ExpectedSha256);
                    Field(output, "content_id", candidate.ExpectedContentId);
                    Field(output, "expires_utc", candidate.ExpiresUtc.HasValue ? candidate.ExpiresUtc.Value.ToUniversalTime().ToString("o") : "");
                    Field(output, "archive_password", candidate.ArchivePassword);
                    Field(output, "archive_passwords", candidate.ArchivePasswords);
                    output.Append("\"size\":").Append((candidate.ExpectedByteSize ?? 0).ToString(CultureInfo.InvariantCulture)).Append('}');
                    if (count == 3) break;
                }
            return count == 0 ? "" : output.Append("]}").ToString();
        }

        /// <summary>Re-encodes decoded mirrors, for example after one was consumed.</summary>
        internal static string EncodeMirrors(IList<PackageMirror> mirrors)
        {
            if (mirrors == null || mirrors.Count == 0) return "";
            var output = new StringBuilder("{\"mirrors\":[");
            int count = 0;
            foreach (var mirror in mirrors)
            {
                if (mirror == null || string.IsNullOrEmpty(mirror.Url)) continue;
                if (count++ > 0) output.Append(',');
                output.Append('{');
                Field(output, "url", mirror.Url);
                Field(output, "candidate_id", mirror.CandidateId);
                Field(output, "access_type", mirror.AccessType);
                Field(output, "source_page_url", mirror.SourcePageUrl);
                Field(output, "source_attribution", mirror.SourceAttribution);
                Field(output, "sha256", mirror.ExpectedSha256);
                Field(output, "content_id", mirror.ExpectedContentId);
                Field(output, "expires_utc", mirror.ExpiresUtc);
                Field(output, "archive_password", mirror.ArchivePassword);
                Field(output, "archive_passwords", mirror.ArchivePasswords);
                output.Append("\"size\":").Append(Math.Max(0, mirror.ExpectedByteSize).ToString(CultureInfo.InvariantCulture)).Append('}');
            }
            return count == 0 ? "" : output.Append("]}").ToString();
        }

        static void Field(StringBuilder output, string name, string value)
        { output.Append('"').Append(name).Append("\":\"").Append(JsonLite.Escape(value)).Append("\","); }

        internal static bool Compatible(PackageCandidate selected, PackageCandidate candidate)
        {
            if (selected == null || candidate == null || selected == candidate ||
                string.IsNullOrWhiteSpace(selected.SourceId) || string.IsNullOrWhiteSpace(selected.PackageGroupId) ||
                string.IsNullOrWhiteSpace(selected.TitleId) || string.IsNullOrWhiteSpace(candidate.CandidateId) ||
                !string.IsNullOrEmpty(selected.ArchiveVolumes) || !string.IsNullOrEmpty(candidate.ArchiveVolumes) ||
                !string.IsNullOrWhiteSpace(candidate.ResolutionError) || candidate.ExpectedByteSize < 0 ||
                selected.AccessType == PackageAccessType.Direct || candidate.AccessType == PackageAccessType.Direct)
                return false;
            string kind = PackageCandidatePresentation.EffectiveKind(selected);
            if (kind == "unknown" || kind != PackageCandidatePresentation.EffectiveKind(candidate)) return false;
            if (!Same(selected.SourceId, candidate.SourceId) || !Same(selected.SourceVersion, candidate.SourceVersion) ||
                !Same(selected.PackageGroupId, candidate.PackageGroupId) || !Same(selected.TitleId, candidate.TitleId) ||
                !Same(selected.Region, candidate.Region) || !Same(selected.PackageKindHint, candidate.PackageKindHint) ||
                !Same(selected.PackageVersion, candidate.PackageVersion) || !Same(selected.RequiredFirmware, candidate.RequiredFirmware))
                return false;
            if (Conflict(selected.ExpectedSha256, candidate.ExpectedSha256) || Conflict(selected.ExpectedContentId, candidate.ExpectedContentId) ||
                (selected.ExpectedByteSize > 0 && candidate.ExpectedByteSize > 0 && selected.ExpectedByteSize != candidate.ExpectedByteSize)) return false;
            Uri uri;
            return Uri.TryCreate(candidate.Url, UriKind.Absolute, out uri) && uri.UserInfo.Length == 0 &&
                (uri.Scheme == "http" || uri.Scheme == "https") &&
                (!candidate.ExpiresUtc.HasValue || candidate.ExpiresUtc.Value.ToUniversalTime() > DateTime.UtcNow);
        }

        static bool Same(string a, string b) { return string.Equals(a ?? "", b ?? "", StringComparison.Ordinal); }
        static bool Conflict(string a, string b)
        { return !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && !string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }

        internal static List<PackageMirror> Decode(string encoded)
        {
            var output = new List<PackageMirror>();
            if (string.IsNullOrEmpty(encoded) || encoded.Length > 65536) return output;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string row in JsonLite.ExtractObjectArray(encoded, "mirrors"))
            {
                string url = JsonLite.GetString(row, "url"), access = JsonLite.GetString(row, "access_type");
                string expires = JsonLite.GetString(row, "expires_utc");
                Uri uri; DateTime expiry; long size;
                if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.UserInfo.Length > 0 ||
                    (uri.Scheme != "http" && uri.Scheme != "https") ||
                    (access != "HosterLanding" && access != "Unknown") ||
                    (!string.IsNullOrEmpty(expires) && (!DateTime.TryParse(expires, null, DateTimeStyles.RoundtripKind, out expiry) || expiry.ToUniversalTime() <= DateTime.UtcNow)) ||
                    !seen.Add(url)) continue;
                long.TryParse(JsonLite.GetString(row, "size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out size);
                output.Add(new PackageMirror { Url = url, CandidateId = JsonLite.GetString(row, "candidate_id") ?? "",
                    AccessType = access, SourcePageUrl = JsonLite.GetString(row, "source_page_url") ?? "",
                    SourceAttribution = JsonLite.GetString(row, "source_attribution") ?? "",
                    ExpectedSha256 = JsonLite.GetString(row, "sha256") ?? "", ExpectedContentId = JsonLite.GetString(row, "content_id") ?? "",
                    ExpectedByteSize = Math.Max(0, size), ExpiresUtc = expires ?? "", ArchivePassword = JsonLite.GetString(row, "archive_password") ?? "",
                    ArchivePasswords = JsonLite.GetString(row, "archive_passwords") ?? "" });
                if (output.Count == 3) break;
            }
            return output;
        }

        internal static string Resolve(string original, string alternatives, Func<string, string> unrestrict,
            Func<bool> canSwitch, Action<PackageMirror> selected, Action<string> progress, Func<bool> cancel)
        {
            if (cancel != null && cancel()) throw new OperationCanceledException();
            DebridResolutionError failure;
            try { return unrestrict(original); }
            catch (DebridResolutionError ex) { if (!ex.CanTryMirror || !canSwitch()) throw; failure = ex; }
            foreach (var mirror in Decode(alternatives))
            {
                if (cancel != null && cancel()) throw new OperationCanceledException();
                if (!canSwitch()) throw failure;
                if (string.Equals(mirror.Url, original, StringComparison.Ordinal)) continue;
                if (progress != null) progress("Trying another mirror · " + DebridResolutionError.HostName(mirror.Url));
                try
                {
                    string direct = unrestrict(mirror.Url);
                    if (cancel != null && cancel()) throw new OperationCanceledException();
                    selected(mirror);
                    return direct;
                }
                catch (DebridResolutionError ex) { if (!ex.CanTryMirror) throw; failure = ex; }
            }
            throw failure;
        }
    }
}
