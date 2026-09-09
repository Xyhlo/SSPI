using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;

namespace Orbis
{
    internal sealed class PackageSourcePackageEntry
    {
        public string Path = "";
        public long CompressedSize;
        public long UncompressedSize;
        public string Sha256 = "";
    }

    /// <summary>
    /// A completely materialized and validated .gssource archive. Materializing is intentional:
    /// the expanded archive is capped at 16 MiB, so extraction never depends on a mutable input.
    /// This class only reads declarative data; it never loads an assembly or executes package data.
    /// </summary>
    internal sealed class PackageSourcePackage
    {
        public const int MaximumCompressedBytes = 4 * 1024 * 1024;
        public const int MaximumExpandedBytes = 16 * 1024 * 1024;
        public const int MaximumEntryBytes = 4 * 1024 * 1024;
        public const int MaximumEntries = 64;

        readonly Dictionary<string, byte[]> _files =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);

        public readonly List<PackageSourcePackageEntry> Entries =
            new List<PackageSourcePackageEntry>();
        public PackageSourceDescriptor Descriptor { get; private set; }
        public string Trust { get; private set; }
        public string SourceJson { get; private set; }
        public string PackageSha256 { get; private set; }

        PackageSourcePackage() { }

        public static PackageSourcePackage Open(string path)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("Package path is empty");
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("Package source not found", path);
            if (info.Length <= 0 || info.Length > MaximumCompressedBytes)
                throw new InvalidDataException("Package source must be between 1 byte and 4 MiB");
            byte[] bytes = new byte[checked((int)info.Length)];
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                ReadExactly(input, bytes);
            return Open(bytes);
        }

        public static PackageSourcePackage Open(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > MaximumCompressedBytes)
                throw new InvalidDataException("Package source must be between 1 byte and 4 MiB");

            var package = new PackageSourcePackage();
            package.PackageSha256 = Hash(bytes);
            package.ReadArchive(bytes);
            return package;
        }

        public byte[] GetFile(string path)
        {
            byte[] value;
            if (path == null || !_files.TryGetValue(path, out value)) return null;
            return (byte[])value.Clone();
        }

        public void ExtractTo(string destination)
        {
            if (string.IsNullOrEmpty(destination)) throw new ArgumentException("Destination is empty");
            string root = Path.GetFullPath(destination);
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
                root += Path.DirectorySeparatorChar;

            foreach (var pair in _files)
            {
                string relative = pair.Key.Replace('/', Path.DirectorySeparatorChar);
                string output = Path.GetFullPath(Path.Combine(root, relative));
                if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Archive path escaped extraction root");
                string dir = Path.GetDirectoryName(output);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                using (var stream = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    stream.Write(pair.Value, 0, pair.Value.Length);
            }
        }

        void ReadArchive(byte[] archiveBytes)
        {
            var pathKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalizedKeys = new HashSet<string>(StringComparer.Ordinal);
            long expanded = 0;

            try
            {
                using (var memory = new MemoryStream(archiveBytes, false))
                {
                    ZipFile zip = null;
                    try
                    {
                        // The PS4 Mono profile already ships SharpZipLib. Its framework
                        // System.IO.Compression build depends on facade assemblies that are
                        // absent on retail consoles, so keep package extraction on the
                        // runtime-native ZIP implementation.
                        zip = new ZipFile(memory);
                        if (zip.Size == 0 || zip.Size > MaximumEntries)
                            throw new InvalidDataException("Package source entry count is outside the allowed range");

                        foreach (ZipEntry entry in zip)
                        {
                            string path = ValidatePath(entry.Name);
                            if (entry.IsCrypted) throw new InvalidDataException("Encrypted entries are not allowed: " + path);
                            if (IsLink(entry)) throw new InvalidDataException("Links are not allowed in source packages");
                            bool directory = entry.IsDirectory || path.EndsWith("/", StringComparison.Ordinal);
                            if (directory)
                            {
                                if (entry.Size > 0) throw new InvalidDataException("Invalid directory entry: " + path);
                                continue;
                            }
                            if (!pathKeys.Add(path)) throw new InvalidDataException("Duplicate or case-colliding path: " + path);
                            string normalized = path.Normalize(NormalizationForm.FormC).ToUpperInvariant();
                            if (!normalizedKeys.Add(normalized))
                                throw new InvalidDataException("Unicode-colliding path: " + path);
                            RejectExecutablePath(path);
                            if (entry.Size < 0 || entry.Size > MaximumEntryBytes)
                                throw new InvalidDataException("Entry exceeds 4 MiB: " + path);
                            if (entry.CompressedSize < 0)
                                throw new InvalidDataException("Entry compressed size is invalid: " + path);
                            expanded += entry.Size;
                            if (expanded > MaximumExpandedBytes)
                                throw new InvalidDataException("Expanded package exceeds 16 MiB");
                            if (entry.CompressedSize == 0 && entry.Size > 0)
                                throw new InvalidDataException("Invalid compression ratio: " + path);
                            if (entry.CompressedSize > 0 && entry.Size > entry.CompressedSize * 20L)
                                throw new InvalidDataException("Compression ratio exceeds 20:1: " + path);

                            byte[] data = new byte[checked((int)entry.Size)];
                            using (var input = zip.GetInputStream(entry)) ReadExactly(input, data);
                            RejectExecutableMagic(path, data);
                            string hash = Hash(data);
                            _files.Add(path, data);
                            Entries.Add(new PackageSourcePackageEntry
                            {
                                Path = path,
                                CompressedSize = entry.CompressedSize,
                                UncompressedSize = entry.Size,
                                Sha256 = hash
                            });
                        }
                    }
                    finally
                    {
                        if (zip != null) zip.Close();
                    }
                }
            }
            catch (InvalidDataException) { throw; }
            catch (Exception ex) { throw new InvalidDataException("Invalid .gssource ZIP: " + ex.Message, ex); }

            byte[] descriptorBytes;
            if (!_files.TryGetValue("source.json", out descriptorBytes))
                throw new InvalidDataException("Package source is missing source.json");
            if (descriptorBytes.Length == 0 || descriptorBytes.Length > 256 * 1024)
                throw new InvalidDataException("source.json is empty or too large");
            SourceJson = StrictUtf8(descriptorBytes);
            object rootValue = PackageSourceJson.Parse(SourceJson);
            var root = rootValue as Dictionary<string, object>;
            if (root == null) throw new InvalidDataException("source.json must be a JSON object");
            Descriptor = ParseDescriptor(root);
            if (Descriptor.Engine.EntryFile != "source.json" && !_files.ContainsKey(Descriptor.Engine.EntryFile))
                throw new InvalidDataException("Engine entry file is missing: " + Descriptor.Engine.EntryFile);
            VerifyDeclaredFiles(root);

            // Wave 2 has no audited Ed25519 implementation. Never represent this as verified:
            // signed-unverified and unsigned-dev are explicit trust states consumed by the UI/store.
            Trust = _files.ContainsKey("signature.ed25519") ? "signed-unverified" : "unsigned-dev";
        }

        void VerifyDeclaredFiles(Dictionary<string, object> root)
        {
            object value;
            if (!root.TryGetValue("files", out value))
            {
                foreach (string path in _files.Keys)
                    if (path != "source.json" && path != "signature.ed25519")
                        throw new InvalidDataException("A files hash manifest is required for payload files");
                return;
            }
            var declared = value as List<object>;
            if (declared == null) throw new InvalidDataException("files must be an array");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in declared)
            {
                var obj = item as Dictionary<string, object>;
                if (obj == null) throw new InvalidDataException("Each files item must be an object");
                string path = RequiredString(obj, "path");
                ValidatePath(path);
                if (path == "signature.ed25519") throw new InvalidDataException("Signature must not hash itself");
                if (!seen.Add(path)) throw new InvalidDataException("Duplicate file declaration: " + path);
                byte[] data;
                if (!_files.TryGetValue(path, out data)) throw new InvalidDataException("Declared file is missing: " + path);
                string expected = RequiredString(obj, "sha256").Replace("-", "").Trim();
                if (!HexEquals(expected, Hash(data))) throw new InvalidDataException("SHA-256 mismatch: " + path);
                long declaredSize;
                if (TryLong(obj, "size", out declaredSize) || TryLong(obj, "uncompressedSize", out declaredSize))
                    if (declaredSize != data.LongLength) throw new InvalidDataException("Size mismatch: " + path);
            }

            // If a manifest is supplied it is authoritative: no hidden payload files.
            foreach (string path in _files.Keys)
                if (path != "source.json" && path != "signature.ed25519" && !seen.Contains(path))
                    throw new InvalidDataException("Undeclared package file: " + path);
        }

        static PackageSourceDescriptor ParseDescriptor(Dictionary<string, object> root)
        {
            string schema = OptionalString(root, "schema");
            if (schema.Length > 0 && schema != "gamesearch.source/v1")
                throw new InvalidDataException("Unsupported package source schema: " + schema);
            string id = FirstString(root, "id", "sourceId", "source_id");
            ValidateId(id);
            string version = RequiredAnyString(root, "version");
            ValidateVersion(version);
            string name = FirstString(root, "name", "displayName", "display_name");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 96)
                throw new InvalidDataException("Source display name is missing or too long");

            var descriptor = new PackageSourceDescriptor
            {
                Schema = schema.Length == 0 ? "gamesearch.source/v1" : schema,
                SourceId = id,
                DisplayName = name,
                Description = FirstString(root, "description"),
                Version = version,
                MinimumApiVersion = FirstString(root, "minimumApiVersion", "minApiVersion"),
                MaximumApiVersion = FirstString(root, "maximumApiVersion", "maxApiVersion"),
                UpdateDescriptorUrl = FirstString(root, "updateDescriptorUrl", "update_url"),
                HomepageUrl = FirstString(root, "homepage", "homepageUrl"),
                SupportUrl = FirstString(root, "support", "supportUrl")
            };

            object engineValue;
            var engine = root.TryGetValue("engine", out engineValue)
                ? engineValue as Dictionary<string, object> : null;
            string engineType = engine != null ? FirstString(engine, "type") : (engineValue as string ?? "");
            if (engineType.Length == 0) engineType = FirstString(root, "engineType");
            if (engineType != "remote-api-v1" && engineType != "recipe-v1" && engineType != "recipe-v2")
                throw new InvalidDataException("Engine must be recipe-v2, recipe-v1 or remote-api-v1");
            string entry = engine != null ? FirstString(engine, "entryFile", "entry")
                                          : FirstString(root, "entryFile", "entry");
            if (entry.Length == 0) entry = "source.json"; // embedded remote-api configuration
            entry = ValidatePath(entry);
            if (entry.EndsWith("/", StringComparison.Ordinal))
                throw new InvalidDataException("Engine entry must be a file");
            descriptor.Engine.Type = engineType;
            descriptor.Engine.EntryFile = entry;

            object capabilitiesValue;
            var capabilities = root.TryGetValue("capabilities", out capabilitiesValue)
                ? capabilitiesValue as List<object> : null;
            if (capabilitiesValue != null && capabilities == null)
                throw new InvalidDataException("capabilities must be an array");
            if (capabilities != null) foreach (object capabilityValue in capabilities)
            {
                string capability = capabilityValue as string;
                if (capability == "titles.search") descriptor.Capabilities |= PackageSourceCapability.TitlesSearch;
                else if (capability == "titles.catalog") descriptor.Capabilities |= PackageSourceCapability.TitlesCatalog;
                else if (capability == "packages.resolve") descriptor.Capabilities |= PackageSourceCapability.PackagesResolve;
                else throw new InvalidDataException("Unknown Package Source capability");
            }

            var origins = ReadOrigins(root, "origins");
            AddOrigins(origins, ReadOrigins(root, "networkOrigins"));
            object permissionsValue;
            var permissions = root.TryGetValue("permissions", out permissionsValue)
                ? permissionsValue as Dictionary<string, object> : null;
            if (permissions != null)
            {
                AddOrigins(origins, ReadOrigins(permissions, "origins"));
                AddOrigins(origins, ReadOrigins(permissions, "networkOrigins"));
                AddOrigins(descriptor.Permissions.RedirectOrigins, ReadOrigins(permissions, "redirectOrigins"));
                AddOrigins(descriptor.Permissions.Methods, ReadStringArray(permissions, "methods"));
            }
            foreach (string origin in origins) descriptor.Permissions.NetworkOrigins.Add(ValidateOrigin(origin));

            object publisherValue;
            var publisher = root.TryGetValue("publisher", out publisherValue)
                ? publisherValue as Dictionary<string, object> : null;
            if (publisher != null)
            {
                descriptor.Publisher.Name = FirstString(publisher, "name");
                descriptor.Publisher.PublicKeyId = FirstString(publisher, "publicKeyId", "keyId");
                descriptor.Publisher.PublicKeyFingerprint = FirstString(publisher, "fingerprint");
            }
            return descriptor;
        }

        static List<string> ReadOrigins(Dictionary<string, object> obj, string key)
        {
            return ReadStringArray(obj, key);
        }

        static List<string> ReadStringArray(Dictionary<string, object> obj, string key)
        {
            var result = new List<string>();
            object value;
            if (!obj.TryGetValue(key, out value)) return result;
            var array = value as List<object>;
            if (array == null) throw new InvalidDataException(key + " must be an array");
            foreach (object item in array)
            {
                string text = item as string;
                if (text == null) throw new InvalidDataException(key + " entries must be strings");
                if (!result.Contains(text)) result.Add(text);
            }
            return result;
        }

        static void AddOrigins(List<string> target, List<string> values)
        {
            foreach (string value in values) if (!target.Contains(value)) target.Add(value);
        }

        static string ValidateOrigin(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
                string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo) ||
                uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidDataException("Invalid network origin: " + value);
            return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        }

        static string ValidatePath(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 240 || value.IndexOf('\0') >= 0)
                throw new InvalidDataException("Invalid archive path");
            string path = value.Replace('\\', '/');
            if (path[0] == '/' || path.IndexOf(':') >= 0 || path.StartsWith("//", StringComparison.Ordinal))
                throw new InvalidDataException("Absolute archive paths are forbidden: " + value);
            string[] segments = path.Split('/');
            foreach (string segment in segments)
                if (segment == "." || segment == ".." || (segment.Length == 0 && path[path.Length - 1] != '/'))
                    throw new InvalidDataException("Invalid archive path segment: " + value);
            return path;
        }

        static void ValidateId(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 128)
                throw new InvalidDataException("Source id is missing or too long");
            foreach (char c in value)
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                    throw new InvalidDataException("Source id contains an unsafe character");
        }

        static void ValidateVersion(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64)
                throw new InvalidDataException("Source version is missing or too long");
            foreach (char c in value)
                if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '+'))
                    throw new InvalidDataException("Source version contains an unsafe character");
        }

        static bool IsLink(ZipEntry entry)
        {
            int unixType = (entry.ExternalFileAttributes >> 16) & 0xF000;
            return unixType == 0xA000;
        }

        static void RejectExecutablePath(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            string[] forbidden = { ".dll", ".exe", ".prx", ".sprx", ".elf", ".cs", ".py", ".pyc",
                ".js", ".mjs", ".sh", ".bash", ".bat", ".cmd", ".ps1", ".com", ".msi" };
            foreach (string item in forbidden)
                if (ext == item) throw new InvalidDataException("Executable/code payload is forbidden: " + path);
        }

        static void RejectExecutableMagic(string path, byte[] data)
        {
            if (data.Length >= 2 && data[0] == (byte)'M' && data[1] == (byte)'Z')
                throw new InvalidDataException("Executable payload magic is forbidden: " + path);
            if (data.Length >= 4 && data[0] == 0x7f && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'F')
                throw new InvalidDataException("ELF payload magic is forbidden: " + path);
            if (data.Length >= 2 && data[0] == (byte)'#' && data[1] == (byte)'!')
                throw new InvalidDataException("Script payload magic is forbidden: " + path);
        }

        static string StrictUtf8(byte[] data)
        {
            try { return new UTF8Encoding(false, true).GetString(data); }
            catch (Exception ex) { throw new InvalidDataException("source.json is not valid UTF-8", ex); }
        }

        static string Hash(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(data);
                var text = new StringBuilder(64);
                foreach (byte b in digest) text.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return text.ToString();
            }
        }

        static bool HexEquals(string expected, string actual)
        {
            if (expected == null || expected.Length != actual.Length) return false;
            int difference = 0;
            for (int i = 0; i < actual.Length; i++)
                difference |= char.ToLowerInvariant(expected[i]) ^ actual[i];
            return difference == 0;
        }

        static void ReadExactly(Stream input, byte[] data)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                int read = input.Read(data, offset, data.Length - offset);
                if (read <= 0) throw new EndOfStreamException("Unexpected end of package source");
                offset += read;
            }
            if (input.ReadByte() >= 0) throw new InvalidDataException("Entry exceeds its declared size");
        }

        static string RequiredString(Dictionary<string, object> obj, string key)
        {
            string value = OptionalString(obj, key);
            if (value.Length == 0) throw new InvalidDataException("Missing string: " + key);
            return value;
        }

        static string RequiredAnyString(Dictionary<string, object> obj, params string[] keys)
        {
            string value = FirstString(obj, keys);
            if (value.Length == 0) throw new InvalidDataException("Missing string: " + keys[0]);
            return value;
        }

        static string FirstString(Dictionary<string, object> obj, params string[] keys)
        {
            foreach (string key in keys)
            {
                string value = OptionalString(obj, key);
                if (value.Length > 0) return value;
            }
            return "";
        }

        static string OptionalString(Dictionary<string, object> obj, string key)
        {
            object value;
            if (!obj.TryGetValue(key, out value) || value == null) return "";
            string text = value as string;
            if (text == null) throw new InvalidDataException(key + " must be a string");
            return text.Trim();
        }

        static bool TryLong(Dictionary<string, object> obj, string key, out long result)
        {
            result = 0;
            object value;
            if (!obj.TryGetValue(key, out value)) return false;
            if (value is long)
            {
                result = (long)value;
                if (result < 0) throw new InvalidDataException(key + " must be non-negative");
                return true;
            }
            throw new InvalidDataException(key + " must be a non-negative integer");
        }
    }

    /// <summary>Bounded JSON parser used only for hostile Package Source metadata.</summary>
    internal static class PackageSourceJson
    {
        public static object Parse(string json)
        {
            if (json == null || json.Length > 512 * 1024) throw new InvalidDataException("JSON is too large");
            var parser = new Parser(json);
            object value = parser.Value(0);
            parser.White();
            if (!parser.End) throw new InvalidDataException("Trailing JSON data");
            return value;
        }

        sealed class Parser
        {
            readonly string _text;
            int _at;
            public Parser(string text) { _text = text; }
            public bool End { get { return _at == _text.Length; } }
            public void White() { while (_at < _text.Length && char.IsWhiteSpace(_text[_at])) _at++; }

            public object Value(int depth)
            {
                if (depth > 32) throw new InvalidDataException("JSON nesting exceeds 32");
                White();
                if (_at >= _text.Length) throw new InvalidDataException("Unexpected end of JSON");
                char c = _text[_at];
                if (c == '{') return Object(depth + 1);
                if (c == '[') return Array(depth + 1);
                if (c == '"') return String();
                if (c == 't') { Literal("true"); return true; }
                if (c == 'f') { Literal("false"); return false; }
                if (c == 'n') { Literal("null"); return null; }
                return Number();
            }

            Dictionary<string, object> Object(int depth)
            {
                _at++;
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                White();
                if (Take('}')) return result;
                while (true)
                {
                    White();
                    if (_at >= _text.Length || _text[_at] != '"') throw new InvalidDataException("Object key expected");
                    string key = String();
                    if (result.ContainsKey(key)) throw new InvalidDataException("Duplicate JSON key: " + key);
                    White();
                    Need(':');
                    result.Add(key, Value(depth));
                    White();
                    if (Take('}')) return result;
                    Need(',');
                }
            }

            List<object> Array(int depth)
            {
                _at++;
                var result = new List<object>();
                White();
                if (Take(']')) return result;
                while (true)
                {
                    if (result.Count >= 1024) throw new InvalidDataException("JSON array is too large");
                    result.Add(Value(depth));
                    White();
                    if (Take(']')) return result;
                    Need(',');
                }
            }

            string String()
            {
                Need('"');
                var result = new StringBuilder();
                while (_at < _text.Length)
                {
                    char c = _text[_at++];
                    if (c == '"') return result.ToString();
                    if (c < 0x20) throw new InvalidDataException("Control character in JSON string");
                    if (c != '\\') { result.Append(c); continue; }
                    if (_at >= _text.Length) throw new InvalidDataException("Bad JSON escape");
                    c = _text[_at++];
                    if (c == '"' || c == '\\' || c == '/') result.Append(c);
                    else if (c == 'b') result.Append('\b');
                    else if (c == 'f') result.Append('\f');
                    else if (c == 'n') result.Append('\n');
                    else if (c == 'r') result.Append('\r');
                    else if (c == 't') result.Append('\t');
                    else if (c == 'u')
                    {
                        if (_at + 4 > _text.Length) throw new InvalidDataException("Bad Unicode escape");
                        int code;
                        if (!int.TryParse(_text.Substring(_at, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out code)) throw new InvalidDataException("Bad Unicode escape");
                        result.Append((char)code); _at += 4;
                    }
                    else throw new InvalidDataException("Bad JSON escape");
                    if (result.Length > 256 * 1024) throw new InvalidDataException("JSON string is too large");
                }
                throw new InvalidDataException("Unterminated JSON string");
            }

            object Number()
            {
                int start = _at;
                if (_at < _text.Length && _text[_at] == '-') _at++;
                int integerStart = _at;
                if (_at < _text.Length && _text[_at] == '0') _at++;
                else while (_at < _text.Length && char.IsDigit(_text[_at])) _at++;
                if (_at == integerStart) throw new InvalidDataException("Invalid JSON number");
                bool integral = true;
                if (_at < _text.Length && _text[_at] == '.')
                {
                    integral = false; _at++;
                    int fractionStart = _at;
                    while (_at < _text.Length && char.IsDigit(_text[_at])) _at++;
                    if (_at == fractionStart) throw new InvalidDataException("Invalid JSON fraction");
                }
                if (_at < _text.Length && (_text[_at] == 'e' || _text[_at] == 'E'))
                {
                    integral = false; _at++;
                    if (_at < _text.Length && (_text[_at] == '+' || _text[_at] == '-')) _at++;
                    int exponentStart = _at;
                    while (_at < _text.Length && char.IsDigit(_text[_at])) _at++;
                    if (_at == exponentStart) throw new InvalidDataException("Invalid JSON exponent");
                }
                string token = _text.Substring(start, _at - start);
                if (integral)
                {
                    long integer;
                    if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
                        throw new InvalidDataException("JSON integer is out of range");
                    return integer;
                }
                double real;
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out real) ||
                    double.IsNaN(real) || double.IsInfinity(real))
                    throw new InvalidDataException("Invalid JSON number");
                return real;
            }

            void Literal(string value)
            {
                if (_at + value.Length > _text.Length ||
                    string.CompareOrdinal(_text, _at, value, 0, value.Length) != 0)
                    throw new InvalidDataException("Invalid JSON literal");
                _at += value.Length;
            }
            bool Take(char c) { if (_at < _text.Length && _text[_at] == c) { _at++; return true; } return false; }
            void Need(char c) { if (!Take(c)) throw new InvalidDataException("Expected '" + c + "'"); }
        }
    }
}
