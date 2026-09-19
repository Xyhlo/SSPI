using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbis
{
    internal static partial class PackageSourceEngineStatic
    {
        static readonly TimeSpan RefreshRegexLimit = TimeSpan.FromMilliseconds(100);
        static Regex RefreshRegex(string pattern) { return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RefreshRegexLimit); }
        static readonly Regex RefreshIds = RefreshRegex(@"(?<![a-z0-9])(?:CUSA|SLUS|SCUS|SLES|SCES|SLPS|SLPM|SCPS)\d{5}(?![a-z0-9])");
        static readonly Regex RefreshAnchors = RefreshRegex(@"<a\b(?<attrs>[^>]*)>(?<text>[\s\S]*?)</a\s*>");
        static readonly Regex RefreshRows = RefreshRegex(@"<(?:p|tr|li|h[234])\b[^>]*>(?<body>[\s\S]*?)(?:</(?:p|tr|li|h[234])\s*>|(?=<(?:p|tr|li|h[234])\b))");
        static readonly Regex RefreshBlocks = RefreshRegex(@"<div\b[^>]*class=[""'][^""']*\bsu-spoiler-content\b[^""']*[""'][^>]*>");
        sealed class RefreshLink
        {
            internal Dictionary<string, object> Data;
            internal string Name = "";
            internal int Part;
            internal long Intermediate;
        }
        static string RefreshText(string html)
        { return RefreshRegex(@"\s+").Replace(WebUtility.HtmlDecode(RefreshRegex(@"<[^>]*>").Replace(html ?? "", "")), " ").Trim(); }
        static string RefreshAttribute(string attributes, string name)
        {
            Match match = RefreshRegex(@"(?:^|\s)" + Regex.Escape(name) + @"\s*=\s*([""'])(?<value>[\s\S]*?)\1").Match(attributes);
            return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value) : "";
        }
        static string RefreshDecode(string html)
        {
            if (html.Length > 2 * 1024 * 1024) throw Bad("Refresh HTML exceeds limit");
            html = RefreshRegex(@"<(?:script|style|blockquote)\b[^>]*>[\s\S]*?</(?:script|style|blockquote)\s*>").Replace(html, "");
            for (int pass = 0; pass < 3; pass++)
            {
                html = RefreshRegex(@"<div\b[^>]*data-payload=([""'])(?<data>[A-Za-z0-9+/=\s]+)\1[^>]*>[\s\S]*?</div\s*>").Replace(html, match => {
                    byte[] bytes = Convert.FromBase64String(match.Groups["data"].Value);
                    if (bytes.Length > 2 * 1024 * 1024) throw Bad("Decoded refresh fragment exceeds limit");
                    return new UTF8Encoding(false, true).GetString(bytes);
                });
                if (html.Length > 2 * 1024 * 1024) throw Bad("Decoded refresh HTML exceeds limit");
            }
            return html;
        }
        static string RefreshOneId(string text)
        {
            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in RefreshIds.Matches(text)) values.Add(match.Value.ToUpperInvariant());
            foreach (string value in values) if (values.Count == 1) return value;
            return "";
        }
        static string RefreshRegion(string text)
        {
            Match match = RefreshRegex(@"\b(USA|US|EUR|EU|JPN|JAP|JP|ASIA|AS|KOR|KR|HK|TW|CN)\b").Match(text);
            string value = match.Value.ToUpperInvariant();
            return value == "ASIA" ? "AS" : value == "KOR" ? "KR" : NormalizeRegion(value);
        }
        static string RefreshFileName(string url, string text)
        {
            string[] segments = Uri.UnescapeDataString(Uri.UnescapeDataString(new Uri(url).AbsolutePath)).Split('/');
            var pattern = RefreshRegex(@"^[^/\\]+\.(?:pkg|rar|zip|7z|r\d{2,3}|(?:rar|zip|7z)\.\d{3})$");
            for (int i = segments.Length - 1; i >= 0; i--) if (pattern.IsMatch(segments[i])) return segments[i];
            return pattern.IsMatch(text) ? text : "";
        }
        static string RefreshHost(string url) { return new Uri(url).Host.ToLowerInvariant().Replace("www.", ""); }
        static long RefreshIntermediate(RefreshConfiguration config, string url)
        {
            Uri uri = new Uri(url); Match match = RefreshRegex(@"^/archives/(\d+)/?$").Match(uri.AbsolutePath);
            long id;
            return uri.Host == new Uri(config.DownloadPostsUrl).Host && match.Success && long.TryParse(match.Groups[1].Value, out id) ? id : 0;
        }
        static List<object> RefreshPasswords(string block, RefreshConfiguration config, Dictionary<string, object> inherited)
        {
            Match match = RefreshRegex(@"\bPass?word\s*[:=]\s*(?<value>[^<\r\n]+)").Match(block);
            if (!match.Success)
            {
                var result = new List<object>();
                foreach (object value in inherited != null && inherited.ContainsKey("archivePasswords") ? (IList)inherited["archivePasswords"] : config.Passwords) result.Add(value);
                return result;
            }
            string password = WebUtility.HtmlDecode(match.Groups["value"].Value).Trim();
            if (password.Length == 0 || password.Length > 128) return new List<object>(config.Passwords);
            foreach (string candidate in config.Passwords)
                if (string.Equals(password.Trim('[', ']'), candidate.Trim('[', ']'), StringComparison.OrdinalIgnoreCase)) return new List<object>(config.Passwords);
            return new List<object> { password };
        }
        static string RefreshValue(Dictionary<string, object> item, string key)
        { object value; return item != null && item.TryGetValue(key, out value) ? value as string ?? "" : ""; }
        static List<RefreshLink> RefreshParseLinks(RefreshConfiguration config, string html, string source, Dictionary<string, object> inherited, string blockKey)
        {
            html = RefreshDecode(html);
            var result = new List<RefreshLink>(); var seen = new HashSet<string>(StringComparer.Ordinal);
            string id = RefreshValue(inherited, "titleId"), region = RefreshValue(inherited, "region");
            List<object> passwords = RefreshPasswords(html, config, inherited);
            MatchCollection paragraphs = RefreshRows.Matches(html);
            foreach (Match paragraph in paragraphs)
            {
                string body = paragraph.Groups["body"].Value, text = RefreshText(body);
                MatchCollection anchors = RefreshAnchors.Matches(body);
                if (anchors.Count == 0)
                {
                    if (text.Length < 350 && RefreshIds.IsMatch(text)) { id = RefreshOneId(text); region = RefreshRegion(text); }
                    if (RefreshRegex(@"^(?:PS5|PlayStation\s*5|PPSA\d{5})\b").IsMatch(text)) { id = ""; region = ""; }
                    continue;
                }
                string label = RefreshText(body.Substring(0, anchors[0].Index)).Trim(' ', ':', '-', '–');
                if (label.Length == 0 || RefreshRegex(@"^(?:part[. _-]*\d+|link|download|hspeed|mediafire|1file|akia|data|rootz|viki)$").IsMatch(label))
                    label = RefreshValue(inherited, "label");
                if (label.Length == 0) label = "Game";
                if (RefreshRegex(@"\b(?:guide|how to|tool|trailer|gameplay|game play|soundtrack|artbook|theme|save)\b|\bPPSA\d{5}\b|\(PS5\)").IsMatch(label)) continue;
                string kind = RefreshRegex(@"\b(?:game|base)\b").IsMatch(label) ? "base" :
                    RefreshRegex(@"update|patch|backport|\bfix\b").IsMatch(label) ? "update" :
                    RefreshRegex(@"\bdlcs?\b|add.?on").IsMatch(label) ? "dlc" : RefreshValue(inherited, "kind");
                if (kind.Length == 0) kind = "unknown";
                Match version = RefreshRegex(@"(?:\bv(?:ersion)?[\s:=_-]*|\b(?:update|patch)\s+v?)(\d{1,3}(?:\.\d{1,3}){0,2})(?![\d.])").Match(label);
                Match firmware = RefreshRegex(@"[\[(]\s*(?:fix[.\s_]*|backport[.\s_]*)?(\d{1,2}\.\d{2})(?=[+/, \])])").Match(label);
                string group = Hash(Encoding.UTF8.GetBytes(blockKey + "\n" + id + "\n" + label)).Substring(0, 24);
                foreach (Match anchor in anchors)
                {
                    string attrs = anchor.Groups["attrs"].Value, anchorText = RefreshText(anchor.Groups["text"].Value);
                    if (RefreshRegex(@"guide|how to|tool|trailer").IsMatch(anchorText)) continue;
                    string raw = RefreshAttribute(attrs, "href");
                    if (RefreshAttribute(attrs, "data-d1").Length > 0) raw = RefreshAttribute(attrs, "data-d1") + RefreshAttribute(attrs, "data-d2") + RefreshAttribute(attrs, "data-path");
                    else if (RefreshAttribute(attrs, "data-domain").Length > 0) raw = RefreshAttribute(attrs, "data-domain") + RefreshAttribute(attrs, "data-path");
                    Uri uri;
                    if (string.IsNullOrEmpty(raw) || raw.StartsWith("#") || !Uri.TryCreate(new Uri(source), raw, out uri) ||
                        (uri.Scheme != "https" && uri.Scheme != "http") || uri.UserInfo.Length > 0) continue;
                    string url = uri.AbsoluteUri, hostname = RefreshHost(url); long intermediate = RefreshIntermediate(config, url);
                    if (intermediate == 0 && !config.FileHosts.Contains(hostname)) continue;
                    string name = RefreshFileName(url, anchorText), ownId = RefreshOneId(name);
                    string title = id.Length > 0 ? id : ownId;
                    if (title.Length == 0 || (ownId.Length > 0 && ownId != title) || RefreshRegex(@"\bPPSA\d{5}\b|(?:^|[._ -])PS5(?:[._ -]|$)").IsMatch(name)) continue;
                    if (RefreshRegex(@"\.(?:jpg|png|mp4|pdf)(?:$|\?)").IsMatch(url)) continue;
                    string key = title + "\n" + url + "\n" + label;
                    if (!seen.Add(key)) continue;
                    var row = RefreshObject("titleId", title, "region", region, "url", url, "kind", kind, "label", label.Length > 240 ? label.Substring(0, 240) : label,
                        "hoster", hostname, "accessType", "HosterLanding", "sourcePageUrl", RefreshValue(inherited, "sourcePageUrl").Length > 0 ? inherited["sourcePageUrl"] : source,
                        "groupId", group, "version", version.Success ? version.Groups[1].Value : RefreshValue(inherited, "version"),
                        "requiredFirmware", firmware.Success ? firmware.Groups[1].Value : RefreshValue(inherited, "requiredFirmware"),
                        "archivePasswords", passwords, "archivePassword", passwords.Count > 0 ? passwords[0] : "", "name", name);
                    Match part = RefreshRegex(@"\bpart[. _-]*(\d+)\b").Match(anchorText); int number = 0;
                    if (part.Success) int.TryParse(part.Groups[1].Value, out number);
                    result.Add(new RefreshLink { Data = row, Name = name, Part = number, Intermediate = intermediate });
                    if (result.Count > 1024) throw Bad("New post exceeds the link limit");
                }
            }
            return result;
        }

        static List<Dictionary<string, object>> RefreshParsePost(RefreshRun run, Dictionary<string, object> post, string html)
        {
            RefreshConfiguration config = run.Catalog.Refresh;
            string source = Text(post, "link", 2048, true), name = Text(post, "name", 512, true);
            html = RefreshDecode(html);
            string icon = ""; Match image = RefreshRegex(@"<img\b(?<attrs>[^>]*)>").Match(html); Uri imageUri;
            if (image.Success && Uri.TryCreate(new Uri(source), RefreshAttribute(image.Groups["attrs"].Value, "src"), out imageUri) &&
                (imageUri.Scheme == "http" || imageUri.Scheme == "https")) icon = imageUri.AbsoluteUri;
            MatchCollection blocks = RefreshBlocks.Matches(html); var rows = new List<RefreshLink>();
            if (blocks.Count == 0) rows.AddRange(RefreshParseLinks(config, html, source, null, source));
            for (int i = 0; i < blocks.Count; i++)
            {
                int start = blocks[i].Index + blocks[i].Length, end = i + 1 < blocks.Count ? blocks[i + 1].Index : html.Length;
                rows.AddRange(RefreshParseLinks(config, html.Substring(start, end - start), source, null, source + "#" + i));
            }
            for (int depth = 0; depth < 3; depth++)
            {
                run.Check(); var wanted = new List<long>();
                foreach (RefreshLink row in rows) if (row.Intermediate > 0 && !wanted.Contains(row.Intermediate)) wanted.Add(row.Intermediate);
                if (wanted.Count == 0) break;
                var pages = new Dictionary<long, string>();
                for (int offset = 0; offset < wanted.Count; offset += 20)
                {
                    var numbers = new List<string>(); for (int j = offset; j < wanted.Count && j < offset + 20; j++) numbers.Add(wanted[j].ToString(System.Globalization.CultureInfo.InvariantCulture));
                    string url = config.DownloadPostsUrl + "?include=" + string.Join(",", numbers.ToArray()) + "&per_page=20&_fields=id,content";
                    string body = run.GetIntermediate(url);
                    IList posts = StrictJson.Parse(body) as IList;
                    if (posts == null || posts.Count > 20) throw Bad("Download API returned an invalid post list");
                    foreach (object value in posts)
                    {
                        var document = Object(value, "download post"); long id = Integer(document, "id", true);
                        if (!wanted.Contains(id)) throw Bad("Download API returned an unrequested post");
                        pages[id] = RefreshHtml(Object(Value(document, "content", true), "download content"));
                    }
                    foreach (string number in numbers) if (!pages.ContainsKey(long.Parse(number, System.Globalization.CultureInfo.InvariantCulture)))
                        throw Bad("Download API omitted a required page; retry is available");
                    run.SaveIntermediate(url, body);
                }
                var expanded = new List<RefreshLink>();
                foreach (RefreshLink row in rows)
                {
                    string document;
                    if (row.Intermediate == 0) expanded.Add(row);
                    else if (pages.TryGetValue(row.Intermediate, out document))
                        expanded.AddRange(RefreshParseLinks(config, document, RefreshValue(row.Data, "url"), row.Data, RefreshValue(row.Data, "groupId")));
                }
                rows = expanded;
            }
            var byId = new Dictionary<string, List<RefreshLink>>(StringComparer.Ordinal);
            foreach (RefreshLink row in rows)
            {
                if (row.Intermediate > 0) throw Bad("New post still has unresolved download pages");
                string id = RefreshValue(row.Data, "titleId"); List<RefreshLink> entries;
                if (!byId.TryGetValue(id, out entries)) { entries = new List<RefreshLink>(); byId.Add(id, entries); }
                entries.Add(row);
            }
            var items = new List<Dictionary<string, object>>();
            foreach (var entry in byId)
            {
                List<object> downloads = RefreshGroupVolumes(entry.Value);
                if (downloads.Count == 0) continue;
                string region = RefreshValue((Dictionary<string, object>)downloads[0], "region");
                downloads.RemoveAll(value => RefreshValue((Dictionary<string, object>)value, "region") != region);
                items.Add(RefreshObject("titleId", entry.Key, "name", name, "region", region, "icon", icon, "downloads", downloads));
            }
            return items;
        }
        static List<object> RefreshGroupVolumes(List<RefreshLink> rows)
        {
            var names = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                string set; int part;
                if (!ArchiveVolumeSet.TryIndex(row.Name, out set, out part)) continue;
                string key = RefreshValue(row.Data, "groupId") + "|" + part; HashSet<string> known;
                if (!names.TryGetValue(key, out known)) { known = new HashSet<string>(StringComparer.OrdinalIgnoreCase); names.Add(key, known); }
                known.Add(row.Name);
            }
            var singles = new List<object>(); var groups = new Dictionary<string, List<RefreshLink>>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row.Part > 0 && row.Name.Length == 0)
                {
                    HashSet<string> known;
                    if (names.TryGetValue(RefreshValue(row.Data, "groupId") + "|" + row.Part, out known) && known.Count == 1)
                        foreach (string value in known) { row.Name = value; row.Data["name"] = value; }
                }
                string set; int part;
                bool archive = ArchiveVolumeSet.TryIndex(row.Name, out set, out part);
                if (row.Part > 0 && (!archive || row.Part != part)) continue;
                if (RefreshRegex(@"\.(?:rar|zip|7z)\.\d{3}$").IsMatch(row.Name)) continue;
                string unique = RefreshValue(row.Data, "url") + "\n" + RefreshValue(row.Data, "groupId");
                if (!seen.Add(unique)) continue;
                if (archive)
                {
                    string key = RefreshValue(row.Data, "groupId") + "|" + RefreshValue(row.Data, "hoster") + "|" + set;
                    List<RefreshLink> group; if (!groups.TryGetValue(key, out group)) { group = new List<RefreshLink>(); groups.Add(key, group); }
                    group.Add(row);
                }
                else singles.Add(row.Data);
            }
            foreach (var group in groups.Values)
            {
                if (group.Count == 1 && group[0].Part == 0 && !RefreshRegex(@"\.part\d+\.rar$").IsMatch(group[0].Name)) { singles.Add(group[0].Data); continue; }
                if (group.Count < 2) continue;
                var volumes = new List<ArchiveVolume>();
                foreach (var row in group) volumes.Add(new ArchiveVolume { Name = row.Name, Url = RefreshValue(row.Data, "url"), AccessType = "HosterLanding" });
                try
                {
                    volumes = ArchiveVolumeSet.Validate(volumes);
                    var first = new Dictionary<string, object>(group[0].Data); var parts = new List<object>();
                    foreach (var volume in volumes) parts.Add(RefreshObject("url", volume.Url, "name", volume.Name, "accessType", volume.AccessType));
                    first["volumes"] = parts; first["url"] = volumes[0].Url; first["name"] = volumes[0].Name; singles.Add(first);
                }
                catch (System.IO.InvalidDataException) { }
            }
            foreach (Dictionary<string, object> row in singles)
                row["id"] = Hash(Encoding.UTF8.GetBytes(RefreshValue(row, "titleId") + "\n" + RefreshValue(row, "url") + "\n" + RefreshValue(row, "groupId"))).Substring(0, 24);
            return singles;
        }
    }
}
