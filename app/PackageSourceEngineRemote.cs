using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Orbis
{
    /// <summary>
    /// Restricted remote-api-v1 executor. Configuration is read only from the installed
    /// source's declared entry file and the executor exposes neither settings nor Link Services.
    /// </summary>
    internal static class PackageSourceEngineRemote
    {
        const int MaxRequests = 8;
        const int MaxResults = 100;
        internal const int MaxPackages = 512;
        const int MaxResponseChars = 2 * 1024 * 1024;
        const int MaxConfigBytes = 256 * 1024;
        const int DeadlineSeconds = 60;

        public static List<SourceTitleResult> Search(InstalledPackageSource source,
            string installedVersionPath, string query)
        {
            if (source == null) throw new ArgumentNullException("source");
            return Search(source.Descriptor, installedVersionPath, query);
        }

        public static List<SourceTitleResult> Search(PackageSourceDescriptor descriptor,
            string installedVersionPath, string query)
        {
            ValidateDescriptor(descriptor);
            Dictionary<string, object> config = LoadConfig(descriptor, installedVersionPath);
            Dictionary<string, object> operation = Operation(config, "search");
            string template = Text(operation, "url", Text(config, "searchUrl", null));
            if (string.IsNullOrEmpty(template))
                throw new PackageSourceRemoteException(SourceFailureCode.InvalidRequest,
                    "remote-api-v1 search URL is missing");

            DateTime deadline = DateTime.UtcNow.AddSeconds(DeadlineSeconds);
            int requests = 0;
            string url = Expand(template, query, "", "", "", 100, "");
            string json = Get(descriptor, url, deadline, ref requests);
            object root = StrictJson.Parse(json);
            string resultPath = Text(operation, "resultsPath", Text(config, "resultsPath", "results"));
            IList rows = ArrayAt(root, resultPath);
            if (rows == null && resultPath == "results") rows = ArrayAt(root, "titles");

            Dictionary<string, object> fields = Object(operation, "fields") ?? Object(config, "fields");
            var output = new List<SourceTitleResult>();
            if (rows == null) return output;
            foreach (object row in rows)
            {
                if (output.Count >= MaxResults) break;
                string titleId = Value(row, Field(fields, "titleId", "titleid"), "titleId", "titleid", "title_id");
                string name = Value(row, Field(fields, "name", "name"), "name", "title", "displayName");
                if (string.IsNullOrEmpty(titleId) || string.IsNullOrEmpty(name)) continue;
                output.Add(new SourceTitleResult
                {
                    SourceId = descriptor.SourceId ?? "",
                    SourceVersion = descriptor.Version ?? "",
                    StableResultId = Value(row, Field(fields, "id", "id"), "id", "stableId") ?? titleId,
                    TitleId = titleId,
                    DisplayName = name,
                    Region = Value(row, Field(fields, "region", "region"), "region") ?? "",
                    ImageUrl = AbsoluteHttpOrEmpty(Value(row, Field(fields, "icon", "icon"),
                        "icon", "image", "imageUrl")),
                    Rating = Value(row, Field(fields, "rating", "rating"), "rating", "score") ?? "",
                    Genres = JoinedValue(row, Field(fields, "genres", "genres"), "genres", "genre"),
                    Backport = Value(row, Field(fields, "backport", "backport"),
                        "backport", "isBackport") ?? "",
                    SourceAttribution = descriptor.DisplayName ?? descriptor.SourceId ?? ""
                });
            }
            return output;
        }

        public static List<PackageCandidate> Resolve(InstalledPackageSource source,
            string installedVersionPath, string titleId, string name)
        {
            if (source == null) throw new ArgumentNullException("source");
            return Resolve(source.Descriptor, installedVersionPath, titleId, name, "");
        }

        public static List<PackageCandidate> Resolve(PackageSourceDescriptor descriptor,
            string installedVersionPath, string titleId, string name, string region)
        {
            ValidateDescriptor(descriptor);
            Dictionary<string, object> config = LoadConfig(descriptor, installedVersionPath);
            Dictionary<string, object> operation = Operation(config, "resolve");
            string template = Text(operation, "url", Text(config, "resolveUrl", null));
            if (string.IsNullOrEmpty(template))
                throw new PackageSourceRemoteException(SourceFailureCode.InvalidRequest,
                    "remote-api-v1 resolve URL is missing");

            DateTime deadline = DateTime.UtcNow.AddSeconds(DeadlineSeconds);
            int requests = 0;
            string url = Expand(template, "", titleId, name, region, 100, "");
            string json = Get(descriptor, url, deadline, ref requests);
            object root = StrictJson.Parse(json);
            string resultPath = Text(operation, "resultsPath",
                Text(operation, "packagesPath", Text(config, "packagesPath", "packages")));
            IList rows = ArrayAt(root, resultPath);
            if (rows == null && resultPath != "results") rows = ArrayAt(root, "results");
            Dictionary<string, object> fields = Object(operation, "fields") ?? Object(config, "packageFields");

            var output = new List<PackageCandidate>();
            if (rows == null) return output;
            foreach (object row in rows)
            {
                if (output.Count >= MaxPackages) break;
                string packageUrl = Value(row, Field(fields, "url", "url"), "url", "packageUrl", "link");
                Uri parsed;
                if (!TryAbsoluteHttp(packageUrl, out parsed)) continue;
                string kind = Value(row, Field(fields, "kind", "kind"), "kind", "packageKind") ?? "unknown";
                string candidateId = Value(row, Field(fields, "id", "id"), "id", "candidateId");
                if (string.IsNullOrEmpty(candidateId)) candidateId = packageUrl;
                string access = Value(row, Field(fields, "accessType", "accessType"), "accessType", "access");
                long expectedSize;
                string sizeText = Value(row, Field(fields, "size", "size"), "size", "byteSize");
                long? size = long.TryParse(sizeText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out expectedSize) && expectedSize >= 0 ? (long?)expectedSize : null;
                DateTime expires;
                string expiresText = Value(row, Field(fields, "expiresUtc", "expiresUtc"), "expiresUtc", "expires");
                DateTime? expiry = DateTime.TryParse(expiresText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out expires)
                    ? (DateTime?)expires.ToUniversalTime() : null;

                output.Add(new PackageCandidate
                {
                    SourceId = descriptor.SourceId ?? "",
                    SourceVersion = descriptor.Version ?? "",
                    CandidateId = candidateId,
                    ArchiveVolumes = ReadVolumes(row, access),
                    ArchivePassword = Value(row, "archivePassword", "archivePassword") ?? "",
                    ResolutionError = Value(row, "resolutionError", "resolutionError") ?? "",
                    TitleId = Value(row, Field(fields, "titleId", "titleid"), "titleId", "titleid") ?? titleId ?? "",
                    DisplayName = Value(row, Field(fields, "name", "name"), "name", "displayName") ?? name ?? "",
                    Region = Value(row, Field(fields, "region", "region"), "region") ?? region ?? "",
                    PackageKindHint = kind,
                    PackageVersion = Value(row, Field(fields, "version", "version"),
                        "version", "packageVersion", "appVersion", "app_version", "app_ver",
                        "ver", "updateVersion", "update_version") ?? "",
                    PackageGroupId = Value(row, Field(fields, "groupId", "groupId"),
                        "groupId", "packageGroupId", "mirrorGroupId") ?? "",
                    HosterName = Value(row, Field(fields, "hoster", "hoster"),
                        "hoster", "hosterName", "mirrorName") ?? "",
                    Label = Value(row, Field(fields, "label", "label"), "label") ?? kind,
                    SourceAttribution = descriptor.DisplayName ?? descriptor.SourceId ?? "",
                    Url = parsed.AbsoluteUri,
                    AccessType = ParseAccess(access, parsed),
                    SourcePageUrl = AbsoluteHttpOrEmpty(Value(row, Field(fields, "sourcePageUrl", "sourcePageUrl"),
                        "sourcePageUrl", "pageUrl")),
                    ExpectedByteSize = size,
                    RequiredFirmware = Value(row, Field(fields, "requiredFirmware", "requiredFirmware"), "requiredFirmware", "firmware", "minFirmware") ?? "",
                    ExpectedSha256 = Value(row, Field(fields, "sha256", "sha256"), "sha256") ?? "",
                    ExpectedContentId = Value(row, Field(fields, "contentId", "contentId"), "contentId") ?? "",
                    ExpiresUtc = expiry
                });
            }
            return output;
        }

        static string ReadVolumes(object row, string defaultAccess)
        {
            IList parts = ArrayAt(row, "volumes");
            if (parts == null || parts.Count == 0) return "";
            var volumes = new List<ArchiveVolume>();
            foreach (object part in parts)
            {
                long size;
                string value = Value(part, "size", "size", "byteSize");
                if (!string.IsNullOrEmpty(value) && (!long.TryParse(value, out size) || size < 0))
                    throw new InvalidDataException("Invalid archive volume size");
                long.TryParse(value, out size);
                volumes.Add(new ArchiveVolume
                {
                    Url = Value(part, "url", "url"), Name = Value(part, "name", "name", "filename"),
                    AccessType = Value(part, "accessType", "accessType") ?? defaultAccess ?? "Unknown",
                    Size = size, Sha256 = Value(part, "sha256", "sha256") ?? ""
                });
            }
            return ArchiveVolumeSet.Encode(volumes);
        }

        static void ValidateDescriptor(PackageSourceDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException("descriptor");
            if (descriptor.Engine == null ||
                !string.Equals(descriptor.Engine.Type, "remote-api-v1", StringComparison.OrdinalIgnoreCase))
                throw new PackageSourceRemoteException(SourceFailureCode.Incompatible,
                    "Source is not a remote-api-v1 engine");
        }

        static Dictionary<string, object> LoadConfig(PackageSourceDescriptor descriptor, string root)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(descriptor.Engine.EntryFile))
                throw new PackageSourceRemoteException(SourceFailureCode.InvalidRequest,
                    "remote-api-v1 entry file is missing");
            string basePath = Path.GetFullPath(root);
            string path = Path.GetFullPath(Path.Combine(basePath, descriptor.Engine.EntryFile));
            string prefix = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new PackageSourceRemoteException(SourceFailureCode.PermissionDenied,
                    "Engine entry file escapes the installed source directory");
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 2 || info.Length > MaxConfigBytes)
                throw new PackageSourceRemoteException(SourceFailureCode.InvalidRequest,
                    "remote-api-v1 entry file is missing or oversized");
            object parsed = StrictJson.Parse(File.ReadAllText(path));
            var config = parsed as Dictionary<string, object>;
            if (config == null) throw new PackageSourceRemoteException(SourceFailureCode.InvalidResponse,
                "remote-api-v1 entry must be a JSON object");
            // Permit the entry to be source.json itself, with request/mapping configuration
            // nested in its engine object, as well as a dedicated engine JSON entry file.
            Dictionary<string, object> nested = Object(config, "engine");
            return nested ?? config;
        }

        static string Get(PackageSourceDescriptor descriptor, string url, DateTime deadline, ref int requests)
        {
            if (++requests > MaxRequests)
                throw new PackageSourceRemoteException(SourceFailureCode.LimitExceeded, "HTTP request limit exceeded");
            if (DateTime.UtcNow >= deadline)
                throw new PackageSourceRemoteException(SourceFailureCode.TimedOut, "Source deadline exceeded");
            Uri uri;
            if (!TryAbsoluteHttp(url, out uri) || !OriginAllowed(descriptor, uri))
                throw new PackageSourceRemoteException(SourceFailureCode.PermissionDenied,
                    "Request origin is not permitted");
            int remaining = (int)Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds);
            string result;
            try { result = NetHttp.GetString(uri.AbsoluteUri, remaining); }
            catch (Exception ex)
            {
                throw new PackageSourceRemoteException(SourceFailureCode.NetworkFailure,
                    "Source request failed: " + ex.Message);
            }
            if (result == null || result.Length > MaxResponseChars)
                throw new PackageSourceRemoteException(SourceFailureCode.LimitExceeded,
                    "Source response exceeds 2 MiB");
            return result;
        }

        static bool OriginAllowed(PackageSourceDescriptor descriptor, Uri target)
        {
            if (descriptor.Permissions == null || descriptor.Permissions.NetworkOrigins == null) return false;
            foreach (string value in descriptor.Permissions.NetworkOrigins)
            {
                Uri allowed;
                if (!Uri.TryCreate(value, UriKind.Absolute, out allowed)) continue;
                if (string.Equals(allowed.Scheme, target.Scheme, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(allowed.IdnHost, target.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                    allowed.Port == target.Port) return true;
            }
            return false;
        }

        static string Expand(string value, string query, string titleId, string name, string region,
            int limit, string cursor)
        {
            return (value ?? "")
                .Replace("{query}", Uri.EscapeDataString(query ?? ""))
                .Replace("{titleId}", Uri.EscapeDataString(titleId ?? ""))
                .Replace("{titleid}", Uri.EscapeDataString(titleId ?? ""))
                .Replace("{name}", Uri.EscapeDataString(name ?? ""))
                .Replace("{region}", Uri.EscapeDataString(region ?? ""))
                .Replace("{limit}", limit.ToString(CultureInfo.InvariantCulture))
                .Replace("{cursor}", Uri.EscapeDataString(cursor ?? ""));
        }

        static Dictionary<string, object> Operation(Dictionary<string, object> config, string name)
        {
            return Object(config, name) ?? config;
        }

        static Dictionary<string, object> Object(Dictionary<string, object> value, string key)
        {
            object found;
            return value != null && value.TryGetValue(key, out found) ? found as Dictionary<string, object> : null;
        }

        static string Text(Dictionary<string, object> value, string key, string fallback)
        {
            object found;
            return value != null && value.TryGetValue(key, out found) && found != null
                ? Convert.ToString(found, CultureInfo.InvariantCulture) : fallback;
        }

        static string Field(Dictionary<string, object> fields, string name, string fallback)
        {
            return Text(fields, name, fallback);
        }

        static IList ArrayAt(object root, string path)
        {
            return At(root, path) as IList;
        }

        static object At(object root, string path)
        {
            object current = root;
            foreach (string part in (path ?? "").Split(new[] { '.' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var map = current as Dictionary<string, object>;
                object next;
                if (map == null || !map.TryGetValue(part, out next)) return null;
                current = next;
            }
            return current;
        }

        static string Value(object row, string configuredPath, params string[] conventions)
        {
            object found = At(row, configuredPath);
            if (found == null)
                foreach (string key in conventions)
                {
                    found = At(row, key);
                    if (found != null) break;
                }
            return found == null || found is IDictionary || found is IList
                ? null : Convert.ToString(found, CultureInfo.InvariantCulture);
        }

        static string JoinedValue(object row, string configuredPath, params string[] conventions)
        {
            object found = At(row, configuredPath);
            if (found == null)
                foreach (string key in conventions)
                {
                    found = At(row, key);
                    if (found != null) break;
                }
            IList values = found as IList;
            if (values == null)
                return found == null || found is IDictionary
                    ? "" : Convert.ToString(found, CultureInfo.InvariantCulture);
            var parts = new List<string>();
            foreach (object value in values)
                if (value != null && !(value is IDictionary) && !(value is IList))
                    parts.Add(Convert.ToString(value, CultureInfo.InvariantCulture));
            return string.Join(", ", parts.ToArray());
        }

        static bool TryAbsoluteHttp(string value, out Uri uri)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.UserInfo.Length == 0 &&
                (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);
        }

        static string AbsoluteHttpOrEmpty(string value)
        {
            Uri uri;
            return TryAbsoluteHttp(value, out uri) ? uri.AbsoluteUri : "";
        }

        static PackageAccessType ParseAccess(string value, Uri uri)
        {
            if (string.Equals(value, "Direct", StringComparison.OrdinalIgnoreCase)) return PackageAccessType.Direct;
            if (string.Equals(value, "HosterLanding", StringComparison.OrdinalIgnoreCase)) return PackageAccessType.HosterLanding;
            string path = uri.AbsolutePath;
            return path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
                ? PackageAccessType.Direct : PackageAccessType.Unknown;
        }
    }

    internal sealed class PackageSourceRemoteException : Exception
    {
        public readonly SourceFailureCode FailureCode;
        public PackageSourceRemoteException(SourceFailureCode code, string message) : base(message)
        { FailureCode = code; }
    }

    /// <summary>Bounded JSON parser used for hostile remote responses; rejects duplicate keys.</summary>
    internal sealed class StrictJson
    {
        readonly string _text;
        int _at;
        const int MaxDepth = 32;

        StrictJson(string text) { _text = text ?? ""; }

        public static object Parse(string text)
        {
            if (text == null || text.Length > 2 * 1024 * 1024)
                throw new PackageSourceRemoteException(SourceFailureCode.LimitExceeded, "JSON is oversized");
            var parser = new StrictJson(text);
            object value = parser.ReadValue(0);
            parser.White();
            if (parser._at != parser._text.Length)
                throw new PackageSourceRemoteException(SourceFailureCode.InvalidResponse, "Trailing JSON data");
            return value;
        }

        object ReadValue(int depth)
        {
            if (depth > MaxDepth) Fail("JSON nesting limit exceeded");
            White();
            if (_at >= _text.Length) Fail("Unexpected end of JSON");
            char c = _text[_at];
            if (c == '{') return ReadObject(depth + 1);
            if (c == '[') return ReadArray(depth + 1);
            if (c == '"') return ReadString();
            if (Take("true")) return true;
            if (Take("false")) return false;
            if (Take("null")) return null;
            return ReadNumber();
        }

        Dictionary<string, object> ReadObject(int depth)
        {
            _at++;
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            White();
            if (Eat('}')) return result;
            while (true)
            {
                White();
                if (_at >= _text.Length || _text[_at] != '"') Fail("Object key expected");
                string key = ReadString();
                if (result.ContainsKey(key)) Fail("Duplicate JSON key: " + key);
                White();
                if (!Eat(':')) Fail("Colon expected");
                result.Add(key, ReadValue(depth));
                White();
                if (Eat('}')) return result;
                if (!Eat(',')) Fail("Comma expected");
            }
        }

        IList ReadArray(int depth)
        {
            _at++;
            var result = new List<object>();
            White();
            if (Eat(']')) return result;
            while (true)
            {
                if (result.Count >= 10000) Fail("JSON array item limit exceeded");
                result.Add(ReadValue(depth));
                White();
                if (Eat(']')) return result;
                if (!Eat(',')) Fail("Comma expected");
            }
        }

        string ReadString()
        {
            if (!Eat('"')) Fail("String expected");
            var sb = new StringBuilder();
            while (_at < _text.Length)
            {
                char c = _text[_at++];
                if (c == '"') return sb.ToString();
                if (c < 32) Fail("Control character in string");
                if (c != '\\') { sb.Append(c); continue; }
                if (_at >= _text.Length) Fail("Bad string escape");
                c = _text[_at++];
                if (c == '"' || c == '\\' || c == '/') sb.Append(c);
                else if (c == 'b') sb.Append('\b');
                else if (c == 'f') sb.Append('\f');
                else if (c == 'n') sb.Append('\n');
                else if (c == 'r') sb.Append('\r');
                else if (c == 't') sb.Append('\t');
                else if (c == 'u')
                {
                    if (_at + 4 > _text.Length) Fail("Bad unicode escape");
                    int code;
                    if (!int.TryParse(_text.Substring(_at, 4), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out code)) Fail("Bad unicode escape");
                    sb.Append((char)code); _at += 4;
                }
                else Fail("Bad string escape");
            }
            Fail("Unterminated string");
            return null;
        }

        object ReadNumber()
        {
            int start = _at;
            if (_at < _text.Length && _text[_at] == '-') _at++;
            while (_at < _text.Length && char.IsDigit(_text[_at])) _at++;
            if (_at < _text.Length && _text[_at] == '.')
            { _at++; while (_at < _text.Length && char.IsDigit(_text[_at])) _at++; }
            if (_at < _text.Length && (_text[_at] == 'e' || _text[_at] == 'E'))
            {
                _at++;
                if (_at < _text.Length && (_text[_at] == '+' || _text[_at] == '-')) _at++;
                while (_at < _text.Length && char.IsDigit(_text[_at])) _at++;
            }
            string token = _text.Substring(start, _at - start);
            long integer;
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer)) return integer;
            double number;
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out number) &&
                !double.IsInfinity(number) && !double.IsNaN(number)) return number;
            Fail("Invalid JSON number");
            return null;
        }

        bool Take(string token)
        {
            if (_at + token.Length > _text.Length ||
                string.CompareOrdinal(_text, _at, token, 0, token.Length) != 0) return false;
            _at += token.Length;
            return true;
        }

        bool Eat(char c)
        {
            if (_at >= _text.Length || _text[_at] != c) return false;
            _at++; return true;
        }

        void White() { while (_at < _text.Length && char.IsWhiteSpace(_text[_at])) _at++; }

        static void Fail(string message)
        {
            throw new PackageSourceRemoteException(SourceFailureCode.InvalidResponse, message);
        }
    }
}
