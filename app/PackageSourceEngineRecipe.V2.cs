using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbis
{
    internal static partial class PackageSourceEngineRecipe
    {
        sealed class HtmlNode
        {
            public string Tag, Attrs;
            public int Start, Body, End;
            public HtmlNode Parent;
        }

        static List<HtmlNode> Nodes(string html)
        {
            var nodes = new List<HtmlNode>(); var stack = new List<HtmlNode>();
            foreach (Match m in Regex.Matches(html ?? "", @"<(?<close>/)?(?<tag>[a-zA-Z][a-zA-Z0-9:-]*)\b(?<attrs>[^>]*)>", RegexOptions.CultureInvariant, RegexLimit))
            {
                string tag = m.Groups["tag"].Value.ToLowerInvariant();
                if (m.Groups["close"].Success)
                {
                    for (int i = stack.Count - 1; i >= 0; i--)
                        if (stack[i].Tag == tag) { for (int j = stack.Count - 1; j >= i; j--) { stack[j].End = m.Index; stack.RemoveAt(j); } break; }
                    continue;
                }
                var n = new HtmlNode { Tag = tag, Attrs = m.Groups["attrs"].Value, Start = m.Index, Body = m.Index + m.Length,
                    End = html.Length, Parent = stack.Count > 0 ? stack[stack.Count - 1] : null };
                nodes.Add(n); if (nodes.Count >= 30000) break;
                if (!m.Value.EndsWith("/>") && !Regex.IsMatch(tag, "^(img|br|hr|meta|link|input|source|wbr|area|base|embed|param)$")) stack.Add(n);
                else n.End = n.Body;
            }
            return nodes;
        }

        static bool Selects(HtmlNode node, string selector)
        {
            if (string.IsNullOrWhiteSpace(selector) || selector.Length > 160) return false;
            string[] path = selector.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (path.Length > 1)
            {
                if (!Selects(node, path[path.Length - 1])) return false;
                var parent = node.Parent;
                for (int i = path.Length - 2; i >= 0; i--) { while (parent != null && !Selects(parent, path[i])) parent = parent.Parent; if (parent == null) return false; parent = parent.Parent; }
                return true;
            }
            Match tag = Regex.Match(selector, @"^[a-zA-Z][a-zA-Z0-9:-]*");
            if (tag.Success && !Eq(tag.Value, node.Tag)) return false;
            foreach (Match cls in Regex.Matches(selector, @"\.([\w-]+)")) if (!HasClass(Attribute(node.Attrs, "class"), cls.Groups[1].Value)) return false;
            Match id = Regex.Match(selector, @"#([\w-]+)"); if (id.Success && Attribute(node.Attrs, "id") != id.Groups[1].Value) return false;
            foreach (Match attr in Regex.Matches(selector, @"\[([\w-]+)(?:=['""]?([^'""\]]+)['""]?)?\]"))
            {
                string value = Attribute(node.Attrs, attr.Groups[1].Value);
                if (value.Length == 0 || (attr.Groups[2].Success && value != attr.Groups[2].Value)) return false;
            }
            return true;
        }

        static List<HtmlNode> Roots(string html, List<string> selectors)
        {
            var nodes = Nodes(html); var result = new List<HtmlNode>();
            foreach (string selector in selectors)
            {
                foreach (var node in nodes) if (Selects(node, selector)) result.Add(node);
                if (result.Count > 0) return result;
            }
            if (selectors.Count == 0) result.Add(new HtmlNode { Start = 0, Body = 0, End = html.Length });
            return result;
        }

        static List<WorkItem> DecodeFragments(List<WorkItem> items, string step)
        {
            int max = Math.Max(1, Math.Min(262144, ReadInt(step, "maximumDecodedBytes", 262144)));
            foreach (var item in items)
            {
                string html = item.Html ?? ""; var replacements = new List<KeyValuePair<HtmlNode, string>>(); int total = 0;
                foreach (string entry in ExtractObjectArray(step, "selectors"))
                    foreach (var node in Roots(html, new List<string> { First(entry, "selector") }))
                    {
                        string value = HtmlDecode(Attribute(node.Attrs, First(entry, "attribute")));
                        if (value.Length > max * 2) throw new Exception("Encoded fragment exceeds size limit");
                        byte[] decoded;
                        try { decoded = Convert.FromBase64String(value); } catch (FormatException) { throw new Exception("Invalid base64 recipe fragment"); }
                        total = checked(total + decoded.Length); if (total > max) throw new Exception("Decoded fragments exceed size limit");
                        replacements.Add(new KeyValuePair<HtmlNode,string>(node, new UTF8Encoding(false, true).GetString(decoded)));
                    }
                replacements.Sort((a,b) => b.Key.Body.CompareTo(a.Key.Body));
                int previous = html.Length + 1;
                foreach (var pair in replacements)
                {
                    if (pair.Key.End > previous) continue;
                    html = html.Substring(0, pair.Key.Body) + pair.Value + html.Substring(pair.Key.End); previous = pair.Key.Body;
                }
                if (html.Length > MaxResponseChars) throw new Exception("Decoded document exceeds size limit");
                item.Html = html; item.Text = item.DocumentText = StripTags(html);
            }
            return items;
        }

        static List<WorkItem> ParseArticlesV2(PackageSourceDescriptor descriptor, List<WorkItem> docs, string step)
        {
            var output = new List<WorkItem>(); var selectors = ExtractStringArray(step, "containerSelectors");
            if (selectors.Count == 0) return ParseArticles(descriptor, docs, ReadInt(step, "limit", 40));
            foreach (var doc in docs)
            {
                int count = 0;
                foreach (var node in Roots(doc.Html ?? "", selectors))
                {
                    string fragment = doc.Html.Substring(node.Body, Math.Max(0, node.End - node.Body));
                    Match heading = HeadingRegex.Match(fragment); Match anchor = AnchorRegex.Match(heading.Success ? heading.Value : fragment);
                    if (!anchor.Success) continue;
                    string url = MakeAbsolute(doc.Url, HtmlDecode(anchor.Groups["url"].Value)); Uri uri;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !OriginAllowed(descriptor, uri)) continue;
                    string pattern = First(step, "urlPattern");
                    if (pattern.Length > 0 && !Regex.IsMatch(uri.AbsolutePath, pattern, RegexOptions.CultureInvariant, RegexLimit)) continue;
                    output.Add(new WorkItem { Url = url, ParentUrl = doc.Url, Name = StripTags(anchor.Groups["text"].Value), Text = StripTags(fragment), Order = output.Count });
                    if (++count >= ReadInt(step, "limit", 40) || output.Count >= MaxItems) break;
                }
            }
            return output;
        }

        static bool HostAllowed(string url, List<string> hosts)
        {
            Uri uri; if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || (uri.Scheme != "https" && uri.Scheme != "http")) return false;
            foreach (string host in hosts) if (Eq(HostKey(uri.Host), HostKey(host))) return true;
            return false;
        }

        static string LastMatch(string text, string pattern, int group)
        {
            if (string.IsNullOrEmpty(pattern) || pattern.Length > 512) return "";
            MatchCollection matches = Regex.Matches(text ?? "", pattern, RegexOptions.CultureInvariant, RegexLimit);
            return matches.Count == 0 ? "" : matches[matches.Count - 1].Groups[group].Value;
        }

        static List<WorkItem> ParseSectionsV2(List<WorkItem> docs, string titleId, string step)
        {
            var output = new List<WorkItem>(); var hosts = ExtractStringArray(step, "allowedLinkHosts");
            var rules = ExtractObjectArray(step, "kindRules");
            foreach (var doc in docs)
                foreach (var root in Roots(doc.Html ?? "", ExtractStringArray(step, "rootSelectors")))
                {
                    string fragment = doc.Html.Substring(root.Body, Math.Max(0, root.End - root.Body));
                    foreach (Match anchor in AnchorRegex.Matches(fragment))
                    {
                        string url = MakeAbsolute(doc.Url, HtmlDecode(anchor.Groups["url"].Value)); if (!HostAllowed(url, hosts)) continue;
                        if (new Uri(url).AbsolutePath.StartsWith("/guides", StringComparison.OrdinalIgnoreCase)) continue;
                        string before = StripTags(fragment.Substring(0, anchor.Index));
                        string id = LastMatch(before, @"\b(?:CUSA|PPSA)\d{5}\b", 0);
                        if (!Eq(id, titleId)) continue;
                        int scopeStart = before.LastIndexOf(id, StringComparison.OrdinalIgnoreCase); string scope = before.Substring(Math.Max(0, scopeStart));
                        string precedingHtml = fragment.Substring(0, anchor.Index);
                        string lastHeading = "", kind = "base";
                        // Use the nearest package paragraph, not all previous package headings.
                        var blocks = Regex.Split(precedingHtml + anchor.Value, @"(?i)<(?:p|h[1-6]|div|br)\b[^>]*>|</(?:p|h[1-6]|div)>", RegexOptions.CultureInvariant, RegexLimit);
                        for (int block = blocks.Length - 1; block >= 0; block--)
                        {
                            string line = StripTags(AnchorRegex.Replace(blocks[block], " "));
                            if (line.Length == 0) continue;
                            string found = "";
                            if (Regex.IsMatch(line, @"(?i)^\s*(?:game|base game)\b(?!\s*update)", RegexOptions.CultureInvariant, RegexLimit)) found = "base";
                            else foreach (string rule in rules) if (LastMatch(line, First(rule, "pattern"), 0).Length > 0) { found = First(rule, "kind"); break; }
                            if (found.Length > 0) { lastHeading = line; kind = found; break; }
                            if (Regex.IsMatch(line, @"\b(?:CUSA|PPSA)\d{5}\b", RegexOptions.CultureInvariant, RegexLimit)) break;
                        }
                        string local = lastHeading.Length > 0 ? lastHeading : scope.Substring(Math.Max(0, scope.Length - 200));
                        string version = LastMatch(local, First(step, "versionPattern"), 1);
                        if (version.Length == 0) version = LastMatch(scope, First(step, "versionPattern"), 1);
                        string group = StableId(doc.Url, titleId + "|" + kind + "|" + version + "|" + lastHeading, "section");
                        var item = Copy(doc); item.Url = url; item.Html = ""; item.Text = local; item.TitleId = titleId;
                        item.Kind = kind; item.PackageVersion = version; item.PackageGroupId = group;
                        item.ParentUrl = doc.Url; item.SourcePage = doc.Url; item.Label = StripTags(anchor.Groups["text"].Value);
                        output.Add(item); if (output.Count >= MaxItems) break;
                    }
                }
            return output;
        }

        static string NearbyArchiveName(string html, int at, int length)
        {
            string pattern = @"(?i)(?:\[[^\]]+\]-?)?[A-Za-z0-9._-]+\.(?:part\d+\.rar|r\d{2,3}|rar(?:\.\d{3})?|zip|7z(?:\.\d{3})?)";
            int start = Math.Max(0, at - 450);
            string before = StripTags(html.Substring(start, at - start));
            string after = StripTags(html.Substring(at, Math.Min(html.Length - at, length + 200)));
            string name = LastMatch(before, pattern, 0);
            // Text inside the anchor is more specific than the preceding link's filename.
            string inside = StripTags(html.Substring(at, length)); string current = LastMatch(inside, pattern, 0);
            if (current.Length > 0) return current;
            if (name.Length > 0) return name;
            return LastMatch(after, pattern, 0);
        }

        static string SourceLink(HtmlNode node, string page)
        {
            string raw = HtmlDecode(Attribute(node.Attrs, "href"));
            string split = Attribute(node.Attrs, "data-d1");
            if (split.Length > 0) raw = split + Attribute(node.Attrs, "data-d2") + Attribute(node.Attrs, "data-path");
            else if (Attribute(node.Attrs, "data-domain").Length > 0)
                raw = Attribute(node.Attrs, "data-domain") + Attribute(node.Attrs, "data-path");
            else
            {
                // Accept literal concatenation only; this is data extraction, not JavaScript execution.
                string script = HtmlDecode(Attribute(node.Attrs, "onclick"));
                Match assignment = Regex.Match(script, @"this\.href\s*=\s*((?:'[^']*'|""[^""]*"")(?:\s*\+\s*(?:'[^']*'|""[^""]*""))*)\s*(?:;|$)", RegexOptions.CultureInvariant, RegexLimit);
                if (assignment.Success)
                {
                    var value = new StringBuilder();
                    foreach (Match literal in Regex.Matches(assignment.Groups[1].Value, @"'([^']*)'|""([^""]*)""", RegexOptions.CultureInvariant, RegexLimit))
                        value.Append(literal.Groups[1].Success ? literal.Groups[1].Value : literal.Groups[2].Value);
                    raw = value.ToString();
                }
            }
            if (raw.Length == 0 || raw == "#" || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return "";
            Uri uri;
            if (!Uri.TryCreate(new Uri(page), HtmlDecode(raw), out uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0 || !uri.IsDefaultPort) return "";
            return new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
        }

        static List<HtmlNode> LeafBlocks(string html)
        {
            var nodes = Nodes(html); var candidates = new HashSet<HtmlNode>(); var parents = new HashSet<HtmlNode>();
            foreach (var n in nodes)
                if (n.Tag == "p" || n.Tag == "li" || n.Tag == "tr" || Regex.IsMatch(n.Tag, "^h[1-6]$")) candidates.Add(n);
            foreach (var n in candidates)
                for (var p = n.Parent; p != null; p = p.Parent) if (candidates.Contains(p)) parents.Add(p);
            var result = new List<HtmlNode>();
            foreach (var n in nodes) if (candidates.Contains(n) && !parents.Contains(n)) result.Add(n);
            return result;
        }

        static List<WorkItem> ParseScopedPackages(List<WorkItem> docs, string titleId, string step)
        {
            var output = new List<WorkItem>(); var hosts = ExtractStringArray(step, "allowedLinkHosts");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var doc in docs)
            foreach (var root in Roots(doc.Html ?? "", ExtractStringArray(step, "rootSelectors")))
            {
                string html = doc.Html.Substring(root.Body, Math.Max(0, root.End - root.Body));
                var current = Copy(doc); current.TitleId = ""; current.Kind = "unknown";
                foreach (var block in LeafBlocks(html))
                {
                    string fragment = html.Substring(block.Body, Math.Max(0, block.End - block.Body));
                    string text = StripTags(fragment);
                    Match id = TitleIdRegex.Match(text);
                    if (id.Success)
                    {
                        if (!Eq(current.TitleId, id.Value)) { current = Copy(doc); current.Kind = "unknown"; current.PackageVersion = current.RequiredFirmware = current.ArchivePassword = ""; }
                        current.TitleId = id.Value.ToUpperInvariant(); current.Region = InferRegion(text);
                    }
                    if (!Eq(current.TitleId, titleId)) continue;
                    string password = LastMatch(text, First(step, "passwordPattern"), 1);
                    if (password.Length > 0) { current.ArchivePassword = Clip(password, 128); continue; }
                    string kind = "";
                    foreach (string rule in ExtractObjectArray(step, "kindRules"))
                        if (LastMatch(text, First(rule, "pattern"), 0).Length > 0) { kind = First(rule, "kind"); break; }
                    if (kind.Length > 0)
                    {
                        current.Kind = kind; current.Label = Clip(StripTags(AnchorRegex.Replace(fragment, " ")).Trim(' ', ':'), 180);
                        // A new package section must never inherit the previous update's version.
                        current.PackageVersion = LastMatch(text, First(step, "versionPattern"), 1);
                        current.RequiredFirmware = LastMatch(text, First(step, "firmwarePattern"), 1);
                        if (current.RequiredFirmware.Length == 0)
                            current.RequiredFirmware = LastMatch(text, @"\((\d{1,2}\.\d{2})\+\)", 1);
                        current.PackageGroupId = StableId(doc.Url, titleId + "|" + kind + "|" + current.PackageVersion + "|" + current.RequiredFirmware + "|" + current.Label, "section");
                    }
                    if (Eq(current.Kind, "unknown")) continue;
                    foreach (var anchor in Nodes(fragment))
                    {
                        if (anchor.Tag != "a") continue;
                        string url = SourceLink(anchor, doc.Url);
                        if (!HostAllowed(url, hosts)) continue;
                        Match explicitId = TitleIdRegex.Match(Uri.UnescapeDataString(url));
                        if (explicitId.Success && !Eq(explicitId.Value, titleId)) continue;
                        if (!seen.Add(current.PackageGroupId + "|" + url)) continue;
                        var item = Copy(current); item.Url = url; item.Html = ""; item.Text = text;
                        item.SourcePage = item.ParentUrl = doc.Url; item.HosterName = new Uri(url).Host;
                        output.Add(item); if (output.Count >= MaxItems) return output;
                    }
                }
            }
            return output;
        }

        static void ExtractScopedTitles(PackageSourceDescriptor descriptor, List<WorkItem> docs,
            string step, List<SourceTitleResult> output, int limit)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var doc in docs)
            foreach (var root in Roots(doc.Html ?? "", ExtractStringArray(step, "rootSelectors")))
            {
                string html = doc.Html.Substring(root.Body, Math.Max(0, root.End - root.Body));
                string name = string.IsNullOrEmpty(doc.Name) ? ExtractHeading(doc.Html) : doc.Name;
                name = Regex.Replace(name, @"(?i)\s+PS4\b.*$", "", RegexOptions.CultureInvariant, RegexLimit).Trim();
                string image = "";
                foreach (var node in Nodes(html))
                    if (node.Tag == "img" && Attribute(node.Attrs, "src").Length > 0)
                    { image = MakeAbsolute(doc.Url, HtmlDecode(Attribute(node.Attrs, "src"))); break; }
                if (image.Length == 0) image = MetaContent(doc.Html, "og:image");
                foreach (var block in LeafBlocks(html))
                {
                    string text = StripTags(html.Substring(block.Body, Math.Max(0, block.End - block.Body)));
                    foreach (Match id in TitleIdRegex.Matches(text))
                    {
                        if (!seen.Add(id.Value)) continue;
                        var title = TitleFromItem(descriptor, new WorkItem { TitleId = id.Value.ToUpperInvariant(), Name = name, Image = image, Region = InferRegion(text) });
                        title.CatalogUrl = doc.Url; output.Add(title);
                        if (output.Count >= limit) return;
                    }
                }
            }
        }

        static List<WorkItem> ParseHostersV2(List<WorkItem> docs, string step)
        {
            var result = new List<WorkItem>(); var hosts = ExtractStringArray(step, "allowedHosts");
            var expands = ExtractStringArray(step, "expandHosts"); var seen = new HashSet<string>(StringComparer.Ordinal);
            int max = Math.Min(MaxItems, ReadInt(step, "maximumLinks", MaxItems));
            foreach (var doc in docs)
            {
                if (HostAllowed(doc.Url, hosts) && string.IsNullOrEmpty(doc.Html)) { if (seen.Add(doc.Url)) result.Add(doc); continue; }
                string html = doc.Html ?? "";
                var roots = Roots(html, ExtractStringArray(step, "rootSelectors"));
                string secure = Regex.Match(step, @"""secureLinkAttributes""\s*:\s*(\{[^}]*\})", RegexOptions.CultureInvariant, RegexLimit).Groups[1].Value;
                string domainAttr = First(secure, "domain"); if (domainAttr.Length == 0) domainAttr = "data-domain";
                string pathAttr = First(secure, "path"); if (pathAttr.Length == 0) pathAttr = "data-path";
                foreach (var node in Nodes(html))
                {
                    if (node.Tag != "a" || !roots.Exists(root => node.Start >= root.Body && node.End <= root.End)) continue;
                    string url; try { url = SourceLink(node, doc.Url); } catch { continue; }
                    if ((!HostAllowed(url, hosts) && !HostAllowed(url, expands)) || !seen.Add(url)) continue;
                    var item = Copy(doc); item.Url = url; item.Html = ""; item.ParentUrl = doc.Url;
                    string password = LastMatch(StripTags(html), @"(?i)\bPassword\s*:\s*(\S+)", 1);
                    if (password.Length > 0) item.ArchivePassword = Clip(password, 128);
                    item.HosterName = new Uri(url).Host; string filename = NearbyArchiveName(html, node.Start, Math.Max(1, node.End - node.Start));
                    string set; int index;
                    if (ArchiveVolumeSet.TryIndex(filename, out set, out index)) item.Label = filename;
                    else {
                        item.Label = StripTags(html.Substring(node.Body, Math.Max(0, node.End - node.Body)));
                        string part = LastMatch(item.Label, @"(?i)\bpart[. _-]*(\d{1,3})\b", 1);
                        if (part.Length > 0) item.Label = (item.TitleId ?? "archive") + "-" + item.PackageGroupId + ".part" + part + ".rar";
                    }
                    item.Text = item.Label; result.Add(item); if (result.Count >= max) break;
                }
                if (result.Count >= max) break;
            }
            return result;
        }
    }
}
