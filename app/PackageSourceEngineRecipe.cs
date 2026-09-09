using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbis
{
    /// <summary>
    /// Deliberately small recipe-v1 interpreter. It operates on strings and immutable package
    /// data only; it has no access to settings, Link Services, sockets, or general file paths.
    /// Supports separate searchSteps/resolveSteps with bounded HTTP GET, JSON title selection,
    /// query seeding, article/table parsing, ranking, filtering, URL transforms, and typed emits.
    /// </summary>
    internal static partial class PackageSourceEngineRecipe
    {
        const int MaxSteps = 24;
        const int MaxRequests = 20;
        const int MaxItems = 512;
        const int MaxResults = 512;
        const int MaxResponseChars = 2 * 1024 * 1024;
        const int MaxRecipeChars = 256 * 1024;
        const int DeadlineSeconds = 60;

        static readonly TimeSpan RegexLimit = TimeSpan.FromMilliseconds(250);
        static readonly Regex AnchorRegex = new Regex(
            @"<a\b[^>]*?href\s*=\s*(?:[""'](?<url>[^""']*)[""']|(?<url>[^\s>]+))[^>]*>(?<text>[\s\S]*?)</a\s*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);
        static readonly Regex RowRegex = new Regex(@"<tr\b[^>]*>[\s\S]*?</tr\s*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);
        static readonly Regex TableRegex = new Regex(@"<table\b[^>]*>[\s\S]*?</table\s*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);
        static readonly Regex ArticleRegex = new Regex(@"<article\b[^>]*>[\s\S]*?</article\s*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);
        static readonly Regex HeadingRegex = new Regex(@"<h[23]\b(?<attrs>[^>]*)>(?<body>[\s\S]*?)</h[23]\s*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);
        static readonly Regex CellRegex = new Regex(@"<t[dh]\b[^>]*>(?<body>[\s\S]*?)</t[dh]\s*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);
        static readonly Regex TagRegex = new Regex(@"<[^>]+>",
            RegexOptions.CultureInvariant, RegexLimit);
        static readonly Regex TitleIdRegex = new Regex(@"\bCUSA\d{5}\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);
        static readonly Regex ExactTitleIdRegex = new Regex(@"^CUSA\d{5}$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);

        sealed class WorkItem
        {
            public string Url = "";
            public string Html = "";
            public string Text = "";
            public string DocumentText = "";
            public string Value = "";
            public string Name = "";
            public string TitleId = "";
            public string Region = "";
            public string Image = "";
            public string Kind = "unknown";
            public string PackageVersion = "";
            public string RequiredFirmware = "";
            public string ArchivePassword = "";
            public string PackageGroupId = "";
            public string HosterName = "";
            public string Label = "";
            public string SourcePage = "";
            public string ParentUrl = "";
            public double Score;
            public int Order;
        }

        public static List<SourceTitleResult> Search(PackageSourceDescriptor descriptor,
            string packageDirectory, SourceSearchRequest request, out string error)
        {
            var output = new List<SourceTitleResult>();
            error = null;
            try
            {
                string recipe = LoadRecipe(descriptor, packageDirectory);
                var steps = ExtractObjectArray(recipe, "searchSteps");
                if (steps.Count == 0) return output;
                if (steps.Count > MaxSteps) throw new Exception("Recipe exceeds 24 steps");
                ExecuteSearch(descriptor, request ?? new SourceSearchRequest(), steps, output);
                return output;
            }
            catch (Exception ex)
            {
                error = "Recipe: " + SafeMessage(ex.Message);
                output.Clear();
                return output;
            }
        }

        /// <summary>Resolve using the canonical installed-source directory layout.</summary>
        public static List<PackageCandidate> Resolve(InstalledPackageSource source,
            SourceResolveRequest request, out string error)
        {
            error = null;
            if (source == null || source.Descriptor == null)
            {
                error = "Recipe source is missing";
                return new List<PackageCandidate>();
            }
            string version = source.Versions == null ? "" : source.Versions.ActiveVersion;
            if (string.IsNullOrEmpty(version)) version = source.Descriptor.Version;
            string root = Path.Combine(Path.Combine(Path.Combine(AppSettings.DataDir, "sources"),
                "installed"), source.SourceId ?? "");
            root = Path.Combine(root, version ?? "");
            return Resolve(source.Descriptor, root, request, out error);
        }

        /// <summary>
        /// Resolve with an explicit validated package directory. Returning an empty list plus an
        /// error is intentional so one broken community source does not discard other results.
        /// </summary>
        public static List<PackageCandidate> Resolve(PackageSourceDescriptor descriptor,
            string packageDirectory, SourceResolveRequest request, out string error)
        {
            var results = new List<PackageCandidate>();
            error = null;
            try
            {
                string recipe = LoadRecipe(descriptor, packageDirectory);
                var steps = ExtractObjectArray(recipe, "resolveSteps");
                if (steps.Count == 0) steps = ExtractObjectArray(recipe, "steps");
                if (steps.Count == 0) steps = ExtractObjectArray(recipe, "operations");
                if (steps.Count == 0) throw new Exception("Recipe contains no steps");
                if (steps.Count > MaxSteps) throw new Exception("Recipe exceeds 24 steps");

                Execute(descriptor, request ?? new SourceResolveRequest(), steps, results);
                return results;
            }
            catch (Exception ex)
            {
                error = "Recipe: " + SafeMessage(ex.Message);
                results.Clear();
                return results;
            }
        }

        static string LoadRecipe(PackageSourceDescriptor descriptor, string packageDirectory)
        {
            if (descriptor == null) throw new Exception("Recipe descriptor is missing");
            if (descriptor.Engine == null || (!Eq(descriptor.Engine.Type, "recipe-v1") && !Eq(descriptor.Engine.Type, "recipe-v2")))
                throw new Exception("Unsupported recipe engine");
            string entry = descriptor.Engine == null ? "" : descriptor.Engine.EntryFile;
            if (string.IsNullOrEmpty(entry)) entry = "recipe.json";
            string path = ContainedFile(packageDirectory, entry);
            if (!File.Exists(path)) throw new Exception("Recipe file is missing: " + entry);
            long length = new FileInfo(path).Length;
            if (length < 2 || length > MaxRecipeChars)
                throw new Exception("Recipe file exceeds the 256 KiB limit");
            return File.ReadAllText(path, Encoding.UTF8);
        }

        static void ExecuteSearch(PackageSourceDescriptor descriptor, SourceSearchRequest request,
            List<string> steps, List<SourceTitleResult> output)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(DeadlineSeconds);
            int requests = 0;
            string json = "";
            var items = new List<WorkItem>();
            int limit = request.Limit <= 0 ? 20 : Math.Min(MaxResults, request.Limit);
            foreach (string step in steps)
            {
                CheckDeadline(deadline);
                if (JsonLite.GetBool(step, "onlyIfEmpty", false) && output.Count > 0)
                    continue;
                string op = First(step, "op", "type", "operation");
                if (Eq(op, "input.queries"))
                {
                    var seeded = new List<WorkItem>();
                    foreach (string template in ExtractStringArray(step, "templates"))
                    {
                        string value = (template ?? "")
                            .Replace("{query}", request.Query ?? "")
                            .Replace("{titleId}", request.TitleId ?? "")
                            .Replace("{name}", request.Name ?? "")
                            .Replace("{normalizedName}", NormalizeName(request.Query, false))
                            .Replace("{region}", request.Region ?? "");
                        if (string.IsNullOrWhiteSpace(value)) continue;
                        bool duplicate = false;
                        foreach (WorkItem old in seeded)
                            if (Eq(old.Value, value)) { duplicate = true; break; }
                        if (!duplicate) seeded.Add(new WorkItem { Value = value, Order = seeded.Count });
                    }
                    items = seeded;
                }
                else if (Eq(op, "http.get"))
                {
                    string template = First(step, "url", "template");
                    if (string.IsNullOrEmpty(template)) throw new Exception("http.get has no URL");
                    bool needsItem = template.IndexOf("{url}", StringComparison.Ordinal) >= 0 ||
                        template.IndexOf("{value}", StringComparison.Ordinal) >= 0;
                    if (needsItem)
                    {
                        var fetched = new List<WorkItem>();
                        Exception fetchError = null;
                        for (int i = 0; i < items.Count && i < MaxItems; i++)
                        {
                            if (++requests > MaxRequests)
                                throw new Exception("Recipe exceeds " + MaxRequests + " HTTP requests");
                            CheckDeadline(deadline);
                            string url = ExpandSearchItem(template, request, items[i]);
                            url = MakeAbsolute(FirstOrigin(descriptor), url);
                            Uri uri = RequireHttpUrl(url);
                            if (!OriginAllowed(descriptor, uri))
                                throw new Exception("HTTP origin is not permitted: " + uri.Host);
                            string referer = ExpandSearchItem(First(step, "referer"), request, items[i]);
                            if (!string.IsNullOrEmpty(referer)) RequireAllowed(descriptor, referer, "Referer");
                            string ua = Eq(First(step, "userAgent"), "browser") ? NetHttp.BrowserUserAgent : null;
                            string body;
                            try
                            {
                                int remaining = (int)Math.Max(1000,
                                    Math.Min(30000, (deadline - DateTime.UtcNow).TotalMilliseconds));
                                body = ResolverPageCache.Get(descriptor.SourceId + "@" + descriptor.Version, uri.AbsoluteUri, remaining,
                                    string.IsNullOrEmpty(referer) ? null : referer, null, ua);
                            }
                            catch (Exception ex)
                            {
                                fetchError = ex;
                                if (JsonLite.GetBool(step, "continueOnError", false)) continue;
                                throw;
                            }
                            if (body != null && body.Length > MaxResponseChars)
                                throw new Exception("HTTP response exceeds 2 MiB");
                            WorkItem got = Copy(items[i]);
                            got.Url = uri.AbsoluteUri;
                            got.Html = body ?? "";
                            got.Text = StripTags(body);
                            got.DocumentText = got.Text;
                            if (string.IsNullOrEmpty(got.Name))
                                got.Name = ExtractHeading(got.Html);
                            if (string.IsNullOrEmpty(got.Image))
                                got.Image = MetaContent(got.Html, "og:image");
                            fetched.Add(got);
                        }
                        if (fetched.Count == 0 && fetchError != null && JsonLite.GetBool(step, "failIfAllFailed", false)) throw fetchError;
                    items = fetched;
                    }
                    else
                    {
                        bool variants = JsonLite.GetBool(step, "queryVariants", false) ||
                            template.IndexOf("{query}", StringComparison.Ordinal) >= 0;
                        List<string> queries = variants
                            ? SearchQueries(request.Query) : new List<string> { request.Query ?? "" };
                        if (queries.Count == 0) queries.Add(request.Query ?? "");
                        json = "";
                        bool continueOnError = JsonLite.GetBool(step, "continueOnError", false);
                        for (int q = 0; q < queries.Count && q < 4; q++)
                        {
                            if (++requests > MaxRequests)
                                throw new Exception("Recipe exceeds " + MaxRequests + " HTTP requests");
                            var probe = new SourceSearchRequest
                            {
                                Query = queries[q], TitleId = request.TitleId, Name = request.Name,
                                Region = request.Region, Limit = request.Limit, Cursor = request.Cursor
                            };
                            string url = ExpandSearch(template, probe);
                            Uri uri = RequireHttpUrl(MakeAbsolute(FirstOrigin(descriptor), url));
                            if (!OriginAllowed(descriptor, uri))
                                throw new Exception("HTTP origin is not permitted: " + uri.Host);
                            string referer = ExpandSearch(First(step, "referer"), probe);
                            if (!string.IsNullOrEmpty(referer)) RequireAllowed(descriptor, referer, "Referer");
                            string ua = Eq(First(step, "userAgent"), "browser") ? NetHttp.BrowserUserAgent : null;
                            string body;
                            try
                            {
                                body = ResolverPageCache.Get(descriptor.SourceId + "@" + descriptor.Version, uri.AbsoluteUri, 30000,
                                    string.IsNullOrEmpty(referer) ? null : referer, null, ua) ?? "";
                            }
                            catch
                            {
                                // The first query still reports transport failure. Later alias
                                // probes are best-effort so "nier automata" can become "nier:automata".
                                if (continueOnError || q > 0) continue;
                                throw;
                            }
                            if (body.Length > MaxResponseChars)
                                throw new Exception("HTTP response exceeds 2 MiB");
                            json = body;
                            if (JsonHasTitleIds(body)) break;
                        }
                    }
                }
                else if (Eq(op, "html.articles"))
                    items = ParseArticlesV2(descriptor, items, step);
                else if (Eq(op, "items.dedupe"))
                    items = Dedupe(items, First(step, "field", "by"));
                else if (Eq(op, "rank.title-match"))
                    items = Rank(items,
                        string.IsNullOrEmpty(request.Name) ? request.Query : request.Name,
                        request.TitleId,
                        ReadDouble(step, "minimumScore", 0.35), ReadInt(step, "limit", 8));
                else if (Eq(op, "html.decode-base64-fragments"))
                    items = DecodeFragments(items, step);
                else if (Eq(op, "html.download-link"))
                    items = ParseDownloadLinks(descriptor, items, step);
                else if (Eq(op, "html.extract-scoped-titles"))
                    ExtractScopedTitles(descriptor, items, step, output, limit);
                else if (Eq(op, "html.extract-titles"))
                    ExtractTitles(descriptor, items, output, Math.Min(limit, ReadInt(step, "limit", limit)));
                else if (Eq(op, "json.select"))
                {
                    string required = First(step, "requireBool");
                    if (!string.IsNullOrEmpty(required) && !JsonLite.GetBool(json, required, false))
                        throw new Exception(JsonLite.GetString(json, "message") ?? "JSON response rejected");
                    string array = First(step, "array"); if (string.IsNullOrEmpty(array)) array = "results";
                    string titleField = First(step, "titleId"); if (string.IsNullOrEmpty(titleField)) titleField = "titleId";
                    string nameField = First(step, "name"); if (string.IsNullOrEmpty(nameField)) nameField = "name";
                    string regionField = First(step, "region"); if (string.IsNullOrEmpty(regionField)) regionField = "region";
                    string imageField = First(step, "image"); if (string.IsNullOrEmpty(imageField)) imageField = "image";
                    string ratingField = First(step, "rating"); if (string.IsNullOrEmpty(ratingField)) ratingField = "rating";
                    string genresField = First(step, "genres"); if (string.IsNullOrEmpty(genresField)) genresField = "genres";
                    string backportField = First(step, "backport"); if (string.IsNullOrEmpty(backportField)) backportField = "backport";
                    foreach (string row in JsonLite.ExtractObjectArray(json, array))
                    {
                        string id = (JsonLite.GetString(row, titleField) ?? "").Trim().ToUpperInvariant();
                        if (!ExactTitleIdRegex.IsMatch(id)) continue;
                        string image = JsonLite.GetString(row, imageField) ?? "";
                        string region = NormalizeRegion(JsonLite.GetString(row, regionField) ?? "");
                        output.Add(new SourceTitleResult
                        {
                            SourceId = descriptor.SourceId ?? "", SourceVersion = descriptor.Version ?? "",
                            StableResultId = id, TitleId = id,
                            DisplayName = JsonLite.GetString(row, nameField) ?? id,
                            Region = string.IsNullOrEmpty(region) ? "?" : region,
                            ImageUrl = AbsoluteHttpOrEmpty(image, FirstOrigin(descriptor)),
                            Rating = JsonLite.GetString(row, ratingField) ?? "",
                            Genres = StringOrArray(row, genresField),
                            Backport = JsonLite.GetString(row, backportField) ?? "",
                            SourceAttribution = descriptor.DisplayName ?? descriptor.SourceId ?? ""
                        });
                        if (output.Count >= limit) break;
                    }
                }
                else if (Eq(op, "items.take"))
                {
                    int take = Math.Max(0, Math.Min(MaxItems, ReadInt(step, "count", ReadInt(step, "limit", 8))));
                    if (items.Count > take) items.RemoveRange(take, items.Count - take);
                }
                else if (Eq(op, "emit.title"))
                {
                    foreach (WorkItem item in items)
                    {
                        if (output.Count >= limit) break;
                        if (string.IsNullOrEmpty(item.TitleId) ||
                            !ExactTitleIdRegex.IsMatch(item.TitleId)) continue;
                        output.Add(TitleFromItem(descriptor, item));
                    }
                }
                else throw new Exception("Unsupported search recipe operation: " + op);
            }
        }

        static void Execute(PackageSourceDescriptor descriptor, SourceResolveRequest request,
            List<string> steps, List<PackageCandidate> output)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(DeadlineSeconds);
            int requests = 0;
            var items = new List<WorkItem>();
            string titleId = (request.TitleId ?? "").Trim().ToUpperInvariant();

            foreach (string step in steps)
            {
                CheckDeadline(deadline);
                string op = First(step, "op", "type", "operation");
                if (string.IsNullOrEmpty(op)) throw new Exception("Recipe step has no operation");

                if (Eq(op, "input.queries"))
                {
                    var seeded = new List<WorkItem>();
                    foreach (string template in ExtractStringArray(step, "templates"))
                    {
                        if (template.IndexOf("{normalizedName}", StringComparison.Ordinal) >= 0 &&
                            string.IsNullOrEmpty(NormalizeName(request.Name, false))) continue;
                        string value = (template ?? "")
                            .Replace("{titleId}", request.TitleId ?? "")
                            .Replace("{name}", request.Name ?? "")
                            .Replace("{normalizedName}", NormalizeName(request.Name, false))
                            .Replace("{region}", request.Region ?? "");
                        if (string.IsNullOrWhiteSpace(value)) continue;
                        bool duplicate = false;
                        foreach (WorkItem old in seeded) if (Eq(old.Value, value)) { duplicate = true; break; }
                        if (!duplicate) seeded.Add(new WorkItem { Value = value, Order = seeded.Count });
                    }
                    items = seeded;
                }
                else if (Eq(op, "http.get"))
                {
                    string template = First(step, "url", "template");
                    if (string.IsNullOrEmpty(template)) throw new Exception("http.get has no URL");
                    bool needsItem = template.IndexOf("{url}", StringComparison.Ordinal) >= 0 ||
                        template.IndexOf("{value}", StringComparison.Ordinal) >= 0;
                    var inputs = items.Count == 0 && !needsItem
                        ? new List<WorkItem> { new WorkItem() } : items;
                    var fetched = new List<WorkItem>();
                        Exception fetchError = null;
                    for (int i = 0; i < inputs.Count && i < MaxItems; i++)
                    {
                        CheckDeadline(deadline);
                        string url = ExpandItem(template, request, inputs[i]);
                        url = MakeAbsolute(FirstOrigin(descriptor), url);
                        Uri uri = RequireHttpUrl(url);
                        if (!OriginAllowed(descriptor, uri))
                        {
                            if (Eq(descriptor.Engine.Type, "recipe-v2") && !string.IsNullOrEmpty(inputs[i].PackageGroupId)) { var final = Copy(inputs[i]); final.Html = ""; fetched.Add(final); continue; }
                            throw new Exception("HTTP origin is not permitted: " + uri.Host);
                        }
                        if (++requests > MaxRequests) throw new Exception("Recipe exceeds " + MaxRequests + " HTTP requests");
                        int remaining = (int)Math.Max(1000,
                            Math.Min(30000, (deadline - DateTime.UtcNow).TotalMilliseconds));
                        string referer = ExpandItem(First(step, "referer"), request, inputs[i]);
                        if (!string.IsNullOrEmpty(referer)) RequireAllowed(descriptor, referer, "Referer");
                        string ua = Eq(First(step, "userAgent"), "browser") ? NetHttp.BrowserUserAgent : null;
                        string body;
                        try
                        {
                            body = ResolverPageCache.Get(descriptor.SourceId + "@" + descriptor.Version, uri.AbsoluteUri, remaining,
                                string.IsNullOrEmpty(referer) ? null : referer, null, ua);
                        }
                        catch (Exception ex)
                        {
                            fetchError = ex;
                            if (JsonLite.GetBool(step, "continueOnError", false)) continue;
                            throw;
                        }
                        // NetHttp currently buffers strings, so this is a post-read memory guard;
                        // the dedicated bounded transport must replace it before public release.
                        if (body != null && body.Length > MaxResponseChars)
                            throw new Exception("HTTP response exceeds 2 MiB");
                        WorkItem got = Copy(inputs[i]);
                        got.Url = uri.AbsoluteUri; got.Html = body ?? "";
                        got.Text = StripTags(body); got.DocumentText = got.Text;
                        fetched.Add(got);
                    }
                    if (fetched.Count == 0 && fetchError != null && JsonLite.GetBool(step, "failIfAllFailed", false)) throw fetchError;
                    items = fetched;
                }
                else if (Eq(op, "html.articles"))
                    items = ParseArticlesV2(descriptor, items, step);
                else if (Eq(op, "items.dedupe"))
                    items = Dedupe(items, First(step, "field", "by"));
                else if (Eq(op, "rank.title-match"))
                    items = Rank(items, request.Name, titleId,
                        ReadDouble(step, "minimumScore", 0.35), ReadInt(step, "limit", 8));
                else if (Eq(op, "items.take"))
                {
                    int take = Math.Max(0, Math.Min(MaxItems, ReadInt(step, "limit", 12)));
                    if (items.Count > take) items.RemoveRange(take, items.Count - take);
                }
                else if (Eq(op, "html.decode-base64-fragments"))
                    items = DecodeFragments(items, step);
                else if (Eq(op, "html.download-link"))
                    items = ParseDownloadLinks(descriptor, items, step);
                else if (Eq(op, "html.scoped-packages"))
                    items = ParseScopedPackages(items, titleId, step);
                else if (Eq(op, "html.package-sections"))
                    items = ParseSectionsV2(items, titleId, step);
                else if (Eq(op, "html.hoster-links"))
                    items = ParseHostersV2(items, step);
                else if (Eq(op, "html.package-table-links"))
                    items = ParsePackageTables(items, titleId, step);
                else if (Eq(op, "html.links"))
                {
                    var links = new List<WorkItem>();
                    foreach (WorkItem document in items)
                    {
                        CheckDeadline(deadline);
                        MatchCollection rows = RowRegex.Matches(document.Html ?? "");
                        if (rows.Count == 0)
                            AddAnchors(document.Html, document.Text, document, links);
                        else
                        {
                            // A title/version row commonly scopes the package rows following it.
                            // Carry that CUSA into each link's context so filter.title-id does not
                            // require the anchor and title ID to occur in the same table row.
                            string runningTitleId = "";
                            foreach (Match row in rows)
                            {
                                if (links.Count >= MaxItems) break;
                                string rowText = StripTags(row.Value);
                                Match titleMatch = TitleIdRegex.Match(rowText);
                                if (titleMatch.Success)
                                    runningTitleId = titleMatch.Value.ToUpperInvariant();
                                AddAnchors(row.Value, runningTitleId + " " + rowText, document, links);
                            }
                        }
                        if (links.Count >= MaxItems) break;
                    }
                    items = links;
                }
                else if (Eq(op, "filter.title-id"))
                {
                    if (titleId.Length == 0) throw new Exception("filter.title-id needs a title ID");
                    var filtered = new List<WorkItem>();
                    foreach (WorkItem item in items)
                    {
                        if (ContainsToken(item.Text, titleId) ||
                            (ContainsToken(item.DocumentText, titleId) &&
                             CountToken(item.DocumentText, titleId) == 1))
                            filtered.Add(item);
                    }
                    items = filtered;
                }
                else if (Eq(op, "url.resolve"))
                {
                    string baseTemplate = First(step, "base", "baseUrl");
                    foreach (WorkItem item in items)
                    {
                        string baseUrl = string.IsNullOrEmpty(baseTemplate)
                            ? item.Html : ExpandItem(baseTemplate, request, item);
                        item.Url = MakeAbsolute(baseUrl, item.Url);
                        RequireHttpUrl(item.Url);
                    }
                }
                else if (Eq(op, "emit.package"))
                {
                    string fixedKind = First(step, "kind", "packageKind");
                    string fixedLabel = First(step, "label");
                    string access = First(step, "accessType", "access");
                    foreach (WorkItem item in items)
                    {
                        if (output.Count >= MaxResults) break;
                        Uri uri = RequireHttpUrl(item.Url);
                        string kind = string.IsNullOrEmpty(fixedKind) || Eq(fixedKind, "auto")
                            ? NormalizeKind(string.IsNullOrEmpty(item.Kind) || Eq(item.Kind, "unknown")
                                ? InferKind(item.Text) : item.Kind) : NormalizeKind(fixedKind);
                        PackageAccessType accessType = ParseAccess(access, uri);
                        string label = string.IsNullOrEmpty(fixedLabel) ? Clip(
                            string.IsNullOrEmpty(item.Label) ? item.Text : item.Label, 80) :
                            ExpandItem(fixedLabel, request, item);
                        if (string.IsNullOrEmpty(label)) label = uri.Host;
                        output.Add(new PackageCandidate
                        {
                            SourceId = descriptor.SourceId ?? "",
                            SourceVersion = descriptor.Version ?? "",
                            CandidateId = StableId(descriptor.SourceId, uri.AbsoluteUri, kind),
                            TitleId = titleId,
                            DisplayName = string.IsNullOrEmpty(item.Name) ? request.Name ?? "" : item.Name,
                            Region = string.IsNullOrEmpty(item.Region) ? request.Region ?? "" : item.Region,
                            PackageKindHint = kind,
                            PackageVersion = item.PackageVersion ?? "",
                            RequiredFirmware = string.IsNullOrEmpty(item.RequiredFirmware) ? PkgIntegrity.FirmwareRequirement(item.Text + " " + item.Label) : item.RequiredFirmware,
                            ArchivePassword = item.ArchivePassword ?? "",
                            PackageGroupId = item.PackageGroupId ?? "",
                            HosterName = string.IsNullOrEmpty(item.HosterName) ? uri.Host : item.HosterName,
                            Label = label,
                            SourceAttribution = descriptor.DisplayName ?? descriptor.SourceId ?? "",
                            Url = uri.AbsoluteUri,
                            AccessType = accessType,
                            SourcePageUrl = item.SourcePage ?? ""
                        });
                    }
                }
                else
                    throw new Exception("Unsupported recipe operation: " + op);
            }
        }

        static List<WorkItem> ParseArticles(PackageSourceDescriptor descriptor,
            List<WorkItem> documents, int perDocumentLimit)
        {
            var output = new List<WorkItem>();
            foreach (WorkItem document in documents)
            {
                int count = 0;
                foreach (Match article in ArticleRegex.Matches(document.Html ?? ""))
                {
                    string href = "", title = "";
                    foreach (Match heading in HeadingRegex.Matches(article.Value))
                    {
                        string cls = Attribute(heading.Groups["attrs"].Value, "class");
                        if (!HasClass(cls, "entry-title") && !HasClass(cls, "grid-title")) continue;
                        Match a = AnchorRegex.Match(heading.Groups["body"].Value);
                        if (!a.Success) continue;
                        href = HtmlDecode(a.Groups["url"].Value.Trim());
                        title = StripTags(a.Groups["text"].Value); break;
                    }
                    if (href.Length == 0 || title.Length == 0) continue;
                    Uri uri; try { uri = RequireHttpUrl(MakeAbsolute(document.Url, href)); } catch { continue; }
                    if (!OriginAllowed(descriptor, uri)) continue;
                    Uri documentUri; if (!Uri.TryCreate(document.Url, UriKind.Absolute, out documentUri) ||
                        !Eq(HostKey(documentUri.Host), HostKey(uri.Host))) continue;
                    string[] path = uri.AbsolutePath.Trim('/').Split('/');
                    string slug = path.Length == 0 ? "" : path[path.Length - 1].ToLowerInvariant();
                    if (!Regex.IsMatch(slug, @"^[a-z0-9][a-z0-9-]{1,180}$")) continue;
                    string clean = new UriBuilder(uri) { Query = "", Fragment = "" }.Uri.AbsoluteUri;
                    bool duplicate = false; foreach (WorkItem x in output) if (Eq(x.Url, clean)) { duplicate = true; break; }
                    if (duplicate) continue;
                    output.Add(new WorkItem { Url = clean, ParentUrl = document.Url, Value = slug,
                        Name = title, Text = title, Order = output.Count });
                    if (++count >= Math.Max(1, perDocumentLimit) || output.Count >= MaxItems) break;
                }
            }
            return output;
        }

        static List<WorkItem> Rank(List<WorkItem> items, string target, string titleId,
            double minimum, int limit)
        {
            foreach (WorkItem item in items)
            {
                item.Score = MatchScore(target, item.Name);
                string blob = item.Value + " " + item.Name + " " + item.Url;
                if (!string.IsNullOrEmpty(titleId) && blob.IndexOf(titleId, StringComparison.OrdinalIgnoreCase) >= 0)
                    item.Score = Math.Round(Math.Min(1, item.Score + .35), 4);
            }
            items.Sort(delegate(WorkItem a, WorkItem b) {
                int score = b.Score.CompareTo(a.Score); return score != 0 ? score : a.Order.CompareTo(b.Order); });
            var output = new List<WorkItem>();
            foreach (WorkItem item in items) if (item.Score >= minimum && output.Count < limit) output.Add(item);
            return output;
        }

        static void ExtractTitles(PackageSourceDescriptor descriptor, List<WorkItem> documents,
            List<SourceTitleResult> output, int limit)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SourceTitleResult existing in output)
                if (!string.IsNullOrEmpty(existing.TitleId)) seen.Add(existing.TitleId);
            foreach (WorkItem doc in documents)
            {
                if (output.Count >= limit) break;
                string pageName = string.IsNullOrEmpty(doc.Name) ? ExtractHeading(doc.Html) : doc.Name;
                string pageImage = string.IsNullOrEmpty(doc.Image)
                    ? MetaContent(doc.Html, "og:image") : doc.Image;
                bool fromTable = false;
                foreach (Match table in TableRegex.Matches(doc.Html ?? ""))
                {
                    string text = StripTags(table.Value);
                    List<string> ids = CollectTitleIds(text);
                    if (ids.Count == 0) continue;
                    fromTable = true;
                    string region = InferRegion(text);
                    foreach (string id in ids)
                    {
                        if (!seen.Add(id) || output.Count >= limit) continue;
                        output.Add(TitleFromItem(descriptor, new WorkItem
                        {
                            TitleId = id, Name = pageName, Region = region, Image = pageImage
                        }));
                    }
                }
                if (fromTable) continue;
                foreach (string id in CollectTitleIds(doc.Text + " " + StripTags(doc.Html)))
                {
                    if (!seen.Add(id) || output.Count >= limit) continue;
                    output.Add(TitleFromItem(descriptor, new WorkItem
                    {
                        TitleId = id, Name = pageName,
                        Region = InferRegion(doc.Text), Image = pageImage
                    }));
                }
            }
        }

        static SourceTitleResult TitleFromItem(PackageSourceDescriptor descriptor, WorkItem item)
        {
            string region = NormalizeRegion(item.Region);
            string image = AbsoluteHttpOrEmpty(item.Image, FirstOrigin(descriptor));
            return new SourceTitleResult
            {
                SourceId = descriptor.SourceId ?? "",
                SourceVersion = descriptor.Version ?? "",
                StableResultId = item.TitleId ?? "",
                TitleId = item.TitleId ?? "",
                DisplayName = string.IsNullOrEmpty(item.Name) ? item.TitleId : item.Name,
                Region = string.IsNullOrEmpty(region) ? "?" : region,
                ImageUrl = image,
                SourceAttribution = descriptor.DisplayName ?? descriptor.SourceId ?? ""
            };
        }

        static List<WorkItem> ParseDownloadLinks(PackageSourceDescriptor descriptor,
            List<WorkItem> documents, string step)
        {
            var output = new List<WorkItem>();
            var imageContains = ExtractStringArray(step, "downloadImageContains");
            var anchorTexts = ExtractStringArray(step, "downloadAnchorTexts");
            var pathPrefixes = ExtractStringArray(step, "downloadPathPrefixes");
            bool sameOrigin = JsonLite.GetBool(step, "sameOrigin", true);
            foreach (WorkItem doc in documents)
            foreach (Match anchor in AnchorRegex.Matches(doc.Html ?? ""))
            {
                Uri uri; try { uri = RequireHttpUrl(MakeAbsolute(doc.Url, HtmlDecode(anchor.Groups["url"].Value))); } catch { continue; }
                if (!OriginAllowed(descriptor, uri)) continue;
                Uri docUri; if (!Uri.TryCreate(doc.Url, UriKind.Absolute, out docUri) ||
                    (sameOrigin && !Eq(HostKey(docUri.Host), HostKey(uri.Host)))) continue;
                string body = anchor.Groups["text"].Value;
                string text = StripTags(body).ToLowerInvariant();
                bool matched = ContainsAny(body, imageContains) || EqualsAny(text, anchorTexts) ||
                    StartsWithAny(uri.AbsolutePath, pathPrefixes);
                if (!matched) continue;
                WorkItem item = Copy(doc); item.ParentUrl = doc.Url; item.SourcePage = doc.Url;
                item.Url = new UriBuilder(uri) { Query = "", Fragment = "" }.Uri.AbsoluteUri;
                output.Add(item); break;
            }
            return output;
        }

        static List<WorkItem> ParsePackageTables(List<WorkItem> documents, string titleId,
            string step)
        {
            if (string.IsNullOrEmpty(titleId)) throw new Exception("Package table parsing needs a title ID");
            var output = new List<WorkItem>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var blockedHosts = ExtractStringArray(step, "blockedHosts");
            var blockedSuffixes = ExtractStringArray(step, "blockedHostSuffixes");
            var dropKeys = ExtractStringArray(step, "dropQueryKeys");
            var labels = ParseMap(ExtractStringArray(step, "hostLabels"));
            foreach (WorkItem doc in documents)
            foreach (Match table in TableRegex.Matches(doc.Html ?? ""))
            {
                string tableText = StripTags(table.Value);
                List<string> tableIds = CollectTitleIds(tableText);
                if (tableIds.Count == 0 || !ContainsIgnoreCase(tableIds, titleId)) continue;
                string tableRegion = InferRegion(tableText);
                string runningId = tableIds.Count == 1 ? titleId : "";
                foreach (Match row in RowRegex.Matches(table.Value))
                {
                    string rowText = StripTags(row.Value);
                    List<string> rowIds = CollectTitleIds(rowText);
                    if (rowIds.Count > 0)
                    {
                        if (!ContainsIgnoreCase(rowIds, titleId))
                        {
                            runningId = rowIds[0];
                            continue;
                        }
                        runningId = titleId;
                    }
                    else if (runningId.Length > 0 && !Eq(runningId, titleId))
                        continue;
                    else if (rowIds.Count == 0 && tableIds.Count > 1 && runningId.Length == 0)
                        continue;
                    MatchCollection cells = CellRegex.Matches(row.Value);
                    string first = cells.Count == 0 ? "" : StripTags(cells[0].Groups["body"].Value);
                    string lower = first.ToLowerInvariant();
                    if (lower.StartsWith("version") || lower.StartsWith("voice") || lower.StartsWith("password") || lower.StartsWith("note") || lower.StartsWith("info")) continue;
                    string kind = InferKind(first); if (kind == "unknown") kind = InferKind(rowText);
                    if (kind == "unknown" && cells.Count == 0) continue;
                    string packageVersion = InferVersion(first);
                    if (packageVersion.Length == 0) packageVersion = InferVersion(rowText);
                    string rowRegion = InferRegion(rowText);
                    if (rowRegion.Length == 0) rowRegion = tableRegion;
                    if (rowRegion.Length == 0) rowRegion = doc.Region;
                    string packageGroupId = StableId("package-row",
                        titleId + "\n" + doc.Url + "\n" + Collapse(first),
                        kind + "\n" + packageVersion);
                    foreach (Match anchor in AnchorRegex.Matches(row.Value))
                    {
                        string raw = HtmlDecode(anchor.Groups["url"].Value.Trim());
                        if (raw.Length == 0 || raw[0] == '#' || raw.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) continue;
                        Uri uri; try { uri = RequireHttpUrl(MakeAbsolute(doc.Url, raw)); } catch { continue; }
                        string host = HostKey(uri.Host);
                        if (!PackageHost(host, blockedHosts, blockedSuffixes)) continue;
                        string clean = StripQueryKeys(uri, dropKeys); if (!seen.Add(clean)) continue;
                        string anchorLabel = StripTags(anchor.Groups["text"].Value);
                        string hoster = MapValue(labels, host,
                            string.IsNullOrEmpty(anchorLabel) ? host : anchorLabel);
                        output.Add(new WorkItem { Url = clean, Kind = kind,
                            PackageVersion = packageVersion, PackageGroupId = packageGroupId,
                            HosterName = hoster, Label = hoster,
                            TitleId = titleId, Name = doc.Name, Region = rowRegion,
                            SourcePage = doc.Url, Text = first });
                    }
                }
            }
            return output;
        }

        static List<WorkItem> Dedupe(List<WorkItem> items, string field)
        {
            var output = new List<WorkItem>(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (WorkItem item in items)
            {
                string key = Eq(field, "value") ? item.Value : Eq(field, "titleId") ? item.TitleId : item.Url;
                if (!string.IsNullOrEmpty(key) && seen.Add(key)) output.Add(item);
            }
            return output;
        }

        static WorkItem Copy(WorkItem x)
        {
            return new WorkItem { Url=x.Url, Html=x.Html, Text=x.Text, DocumentText=x.DocumentText,
                Value=x.Value, Name=x.Name, TitleId=x.TitleId, Region=x.Region, Image=x.Image,
                Kind=x.Kind, PackageVersion=x.PackageVersion, PackageGroupId=x.PackageGroupId,
                RequiredFirmware=x.RequiredFirmware, ArchivePassword=x.ArchivePassword,
                HosterName=x.HosterName, Label=x.Label, SourcePage=x.SourcePage,
                ParentUrl=x.ParentUrl, Score=x.Score, Order=x.Order };
        }

        static double MatchScore(string target, string candidate)
        {
            string a = NormalizeName(target, true), b = NormalizeName(candidate, true);
            var at = new HashSet<string>(a.Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries));
            var bt = new HashSet<string>(b.Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries));
            int common = 0; foreach (string t in at) if (bt.Contains(t)) common++;
            double coverage = at.Count == 0 ? 0 : (double)common / at.Count;
            double ratio = SequenceRatio(a, b);
            var candTokens = new HashSet<string>((candidate ?? "").ToLowerInvariant().Split((char[])null,
                StringSplitOptions.RemoveEmptyEntries));
            var targetTokens = new HashSet<string>((target ?? "").ToLowerInvariant().Split((char[])null,
                StringSplitOptions.RemoveEmptyEntries));
            double platform = candTokens.Contains("ps4") ? .12 : 0;
            if (candTokens.Contains("ps5") && !targetTokens.Contains("ps5"))
                platform -= .22;
            return Math.Round(Math.Max(0, Math.Min(1, coverage * .68 + ratio * .20 + platform)), 4);
        }

        static string NormalizeName(string value, bool editions)
        {
            string s = (value ?? "").Replace("™", " ").Replace("®", " ").Replace("©", " ")
                .Normalize(NormalizationForm.FormD).ToLowerInvariant();
            var b = new StringBuilder(); foreach (char c in s)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    b.Append(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' ? c : ' ');
            var words = new List<string>();
            foreach (string w in Collapse(b.ToString()).Split(' '))
            {
                if (w == "" || w == "ps4" || w == "ps5" || w == "pkg" || w == "fpkg" || w == "iso" || w == "game") continue;
                if (editions && (w == "edition" || w == "premium" || w == "deluxe" || w == "complete" || w == "goty")) continue;
                words.Add(w);
            }
            return CanonicalTokens(string.Join(" ", words.ToArray()));
        }

        static string CanonicalTokens(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var words = new List<string>(value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
            for (int i = 0; i + 2 < words.Count; i++)
            {
                if (words[i] == "grand" && words[i + 1] == "theft" && words[i + 2] == "auto")
                {
                    words[i] = "gta";
                    words.RemoveAt(i + 1);
                    words.RemoveAt(i + 1);
                }
            }
            for (int i = 0; i < words.Count; i++)
            {
                if (words[i] == "viii") words[i] = "8";
                else if (words[i] == "vii") words[i] = "7";
                else if (words[i] == "vi") words[i] = "6";
                else if (words[i] == "iv") words[i] = "4";
                else if (words[i] == "iii") words[i] = "3";
                else if (words[i] == "ii") words[i] = "2";
                else if (words[i] == "v") words[i] = "5";
            }
            return string.Join(" ", words.ToArray());
        }

        static double SequenceRatio(string a, string b)
        {
            if (a.Length + b.Length == 0) return 1; int m = Matching(a,0,a.Length,b,0,b.Length);
            return 2.0 * m / (a.Length + b.Length);
        }

        static int Matching(string a,int al,int ah,string b,int bl,int bh)
        {
            int ba=al,bb=bl,best=0; var previous=new Dictionary<int,int>();
            for(int i=al;i<ah;i++) { var next=new Dictionary<int,int>(); for(int j=bl;j<bh;j++) if(a[i]==b[j])
            { int p; previous.TryGetValue(j-1,out p); int n=p+1; next[j]=n; if(n>best){best=n;ba=i-n+1;bb=j-n+1;} } previous=next; }
            return best==0 ? 0 : best+Matching(a,al,ba,b,bl,bb)+Matching(a,ba+best,ah,b,bb+best,bh);
        }

        static void AddAnchors(string html, string context, WorkItem document, List<WorkItem> output)
        {
            foreach (Match match in AnchorRegex.Matches(html ?? ""))
            {
                if (output.Count >= MaxItems) return;
                string href = HtmlDecode(match.Groups["url"].Value.Trim());
                if (href.Length == 0 || href[0] == '#') continue;
                string absolute;
                try { absolute = MakeAbsolute(document.Url, href); }
                catch { continue; }
                Uri ignored;
                if (!Uri.TryCreate(absolute, UriKind.Absolute, out ignored) ||
                    (ignored.Scheme != Uri.UriSchemeHttp && ignored.Scheme != Uri.UriSchemeHttps)) continue;
                output.Add(new WorkItem
                {
                    Url = ignored.AbsoluteUri,
                    Html = document.Url, // retain the page URL as url.resolve's default base
                    Text = Collapse(context + " " + StripTags(match.Groups["text"].Value)),
                    DocumentText = document.DocumentText
                });
            }
        }

        static bool OriginAllowed(PackageSourceDescriptor descriptor, Uri target)
        {
            if (descriptor.Permissions == null || descriptor.Permissions.NetworkOrigins == null)
                return false;
            foreach (string value in descriptor.Permissions.NetworkOrigins)
            {
                Uri allowed;
                if (!Uri.TryCreate(value, UriKind.Absolute, out allowed)) continue;
                if (!Eq(allowed.Scheme, target.Scheme) ||
                    !Eq(allowed.IdnHost, target.IdnHost)) continue;
                int ap = allowed.IsDefaultPort ? DefaultPort(allowed.Scheme) : allowed.Port;
                int tp = target.IsDefaultPort ? DefaultPort(target.Scheme) : target.Port;
                if (ap == tp) return true;
            }
            return false;
        }

        static string FirstOrigin(PackageSourceDescriptor descriptor)
        {
            if (descriptor != null && descriptor.Permissions != null &&
                descriptor.Permissions.NetworkOrigins != null &&
                descriptor.Permissions.NetworkOrigins.Count > 0)
                return descriptor.Permissions.NetworkOrigins[0];
            return "";
        }

        static string ContainedFile(string root, string relative)
        {
            if (string.IsNullOrEmpty(root)) throw new Exception("Installed package path is missing");
            if (string.IsNullOrEmpty(relative) || Path.IsPathRooted(relative) ||
                relative.IndexOf('\0') >= 0) throw new Exception("Invalid recipe path");
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(fullRoot, relative));
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Recipe path escapes its package");
            return full;
        }

        static List<string> ExtractObjectArray(string json, string key)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(json)) return result;
            int keyAt = FindJsonKey(json, key);
            if (keyAt < 0) return result;
            int array = SkipToValue(json, keyAt + key.Length + 2);
            if (array < 0 || json[array] != '[') return result;
            bool inString = false, escape = false;
            int objectDepth = 0, arrayDepth = 1, start = -1;
            for (int i = array + 1; i < json.Length; i++)
            {
                char c = json[i];
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '[') arrayDepth++;
                else if (c == ']')
                {
                    arrayDepth--;
                    if (arrayDepth == 0) break;
                }
                else if (c == '{')
                {
                    if (objectDepth++ == 0) start = i;
                }
                else if (c == '}' && objectDepth > 0)
                {
                    if (--objectDepth == 0 && start >= 0)
                    {
                        result.Add(json.Substring(start, i - start + 1));
                        if (result.Count > MaxSteps) return result;
                        start = -1;
                    }
                }
            }
            return result;
        }

        static int FindJsonKey(string json, string key)
        {
            string wanted = "\"" + key + "\"";
            bool inString = false, escape = false;
            for (int i = 0; i <= json.Length - wanted.Length; i++)
            {
                char c = json[i];
                if (!inString && string.CompareOrdinal(json, i, wanted, 0, wanted.Length) == 0)
                    return i;
                if (inString)
                {
                    if (escape) escape = false;
                    else if (c == '\\') escape = true;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') inString = true;
            }
            return -1;
        }

        static int SkipToValue(string json, int start)
        {
            int colon = json.IndexOf(':', start);
            if (colon < 0) return -1;
            int p = colon + 1;
            while (p < json.Length && char.IsWhiteSpace(json[p])) p++;
            return p < json.Length ? p : -1;
        }

        static string First(string json, params string[] keys)
        {
            foreach (string key in keys)
            {
                string value = JsonLite.GetString(json, key);
                if (value != null) return value;
            }
            return "";
        }

        static string StringOrArray(string json, string key)
        {
            int keyAt = FindJsonKey(json, key);
            if (keyAt < 0) return "";
            int valueAt = SkipToValue(json, keyAt + key.Length + 2);
            if (valueAt < 0) return "";
            if (json[valueAt] != '[') return JsonLite.GetString(json, key) ?? "";
            return string.Join(", ", ExtractStringArray(json, key).ToArray());
        }

        static string Expand(string value, SourceResolveRequest request, string currentUrl)
        {
            return (value ?? "")
                .Replace("{titleId}", Uri.EscapeDataString(request.TitleId ?? ""))
                .Replace("{name}", Uri.EscapeDataString(request.Name ?? ""))
                .Replace("{normalizedName}", Uri.EscapeDataString(NormalizeName(request.Name, false)))
                .Replace("{region}", Uri.EscapeDataString(request.Region ?? ""))
                .Replace("{cursor}", Uri.EscapeDataString(request.Cursor ?? ""))
                .Replace("{url}", currentUrl ?? "");
        }

        static string ExpandItem(string value, SourceResolveRequest request, WorkItem item)
        {
            return Expand(value, request, item == null ? "" : item.Url)
                .Replace("{value}", Uri.EscapeDataString(item == null ? "" : item.Value ?? ""))
                .Replace("{parentUrl}", item == null ? "" : item.ParentUrl ?? "")
                .Replace("{sourcePage}", item == null ? "" : item.SourcePage ?? "");
        }

        static string ExpandSearch(string value, SourceSearchRequest request)
        {
            return (value ?? "").Replace("{query}", Uri.EscapeDataString(request.Query ?? ""))
                .Replace("{titleId}", Uri.EscapeDataString(request.TitleId ?? ""))
                .Replace("{name}", Uri.EscapeDataString(request.Name ?? ""))
                .Replace("{region}", Uri.EscapeDataString(request.Region ?? ""))
                .Replace("{cursor}", Uri.EscapeDataString(request.Cursor ?? ""));
        }

        static string ExpandSearchItem(string value, SourceSearchRequest request, WorkItem item)
        {
            return ExpandSearch(value, request)
                .Replace("{url}", item == null ? "" : item.Url ?? "")
                .Replace("{value}", Uri.EscapeDataString(item == null ? "" : item.Value ?? ""))
                .Replace("{parentUrl}", item == null ? "" : item.ParentUrl ?? "");
        }

        internal static List<string> SearchQueries(string query)
        {
            var list = new List<string>();
            string q = Collapse((query ?? "").Trim());
            if (q.Length == 0) return list;
            AddQuery(list, q);
            int space = q.IndexOf(' ');
            if (space > 0 && q.IndexOf(':') < 0)
                AddQuery(list, q.Substring(0, space) + ":" + q.Substring(space + 1));
            string folded = q.ToLowerInvariant();
            if (folded == "gta 5" || folded == "gta5" || folded == "gta v")
                AddQuery(list, "Grand Theft Auto V");
            if (folded.IndexOf("san andreas", StringComparison.Ordinal) >= 0 &&
                folded.IndexOf("definitive", StringComparison.Ordinal) >= 0)
                AddQuery(list, "Grand Theft Auto: San Andreas");
            if (folded == "nier automata" || folded == "nier: automata" || folded == "nier:automata")
                AddQuery(list, "NieR:Automata");
            string swapped = SwapStandaloneRoman(q);
            if (!string.Equals(swapped, q, StringComparison.OrdinalIgnoreCase))
                AddQuery(list, swapped);
            return list;
        }

        static void AddQuery(List<string> list, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            foreach (string existing in list)
                if (string.Equals(existing, value, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(value.Trim());
        }

        static string SwapStandaloneRoman(string query)
        {
            var words = new List<string>(Collapse(query).Split(new[] { ' ' },
                StringSplitOptions.RemoveEmptyEntries));
            for (int i = 0; i < words.Count; i++)
            {
                string w = words[i].ToLowerInvariant();
                if (w == "5") words[i] = "V";
                else if (w == "v") words[i] = "5";
                else if (w == "4") words[i] = "IV";
                else if (w == "3") words[i] = "III";
                else if (w == "2") words[i] = "II";
            }
            return string.Join(" ", words.ToArray());
        }

        static bool JsonHasTitleIds(string json)
        {
            foreach (string row in JsonLite.ExtractObjectArray(json ?? "", "results"))
            {
                string id = (JsonLite.GetString(row, "titleid") ?? JsonLite.GetString(row, "titleId") ?? "")
                    .Trim();
                if (ExactTitleIdRegex.IsMatch(id)) return true;
            }
            return false;
        }

        static List<string> CollectTitleIds(string text)
        {
            var ids = new List<string>();
            foreach (Match match in TitleIdRegex.Matches(text ?? ""))
            {
                string id = match.Value.ToUpperInvariant();
                if (!ContainsIgnoreCase(ids, id)) ids.Add(id);
            }
            return ids;
        }

        static bool ContainsIgnoreCase(List<string> values, string wanted)
        {
            foreach (string value in values)
                if (Eq(value, wanted)) return true;
            return false;
        }

        static string InferRegion(string text)
        {
            string s = " " + (text ?? "").ToUpperInvariant().Replace('-', ' ').Replace('_', ' ') + " ";
            s = Regex.Replace(s, @"\s+", " ");
            if (s.IndexOf(" USA ", StringComparison.Ordinal) >= 0 ||
                s.IndexOf(" US ", StringComparison.Ordinal) >= 0 ||
                s.IndexOf(" NTSC ", StringComparison.Ordinal) >= 0)
                return "US";
            if (s.IndexOf(" EUR ", StringComparison.Ordinal) >= 0 ||
                s.IndexOf(" EU ", StringComparison.Ordinal) >= 0 ||
                s.IndexOf(" PAL ", StringComparison.Ordinal) >= 0 ||
                s.IndexOf(" EUROPE ", StringComparison.Ordinal) >= 0)
                return "EU";
            if (s.IndexOf(" JPN ", StringComparison.Ordinal) >= 0 ||
                s.IndexOf(" JP ", StringComparison.Ordinal) >= 0 ||
                s.IndexOf(" JAPAN ", StringComparison.Ordinal) >= 0)
                return "JP";
            if (s.IndexOf(" ASIA ", StringComparison.Ordinal) >= 0 ||
                s.IndexOf(" AS ", StringComparison.Ordinal) >= 0 ||
                s.IndexOf(" HK ", StringComparison.Ordinal) >= 0)
                return "AS";
            return "";
        }

        static string NormalizeRegion(string value)
        {
            string s = (value ?? "").Trim().ToUpperInvariant();
            if (s == "USA" || s == "US" || s == "NTSC" || s == "NTSC-U" || s == "NTSC-U/C") return "US";
            if (s == "EUR" || s == "EU" || s == "PAL" || s == "EUROPE") return "EU";
            if (s == "JPN" || s == "JP" || s == "JAPAN") return "JP";
            if (s == "ASIA" || s == "AS" || s == "HK" || s == "HKG") return "AS";
            if (s == "?" || s.Length == 0) return "";
            return s.Length <= 4 ? s : s.Substring(0, 4);
        }

        static string ExtractHeading(string html)
        {
            Match match = Regex.Match(html ?? "", @"<h1\b[^>]*>(?<body>[\s\S]*?)</h1\s*>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);
            if (!match.Success) return "";
            return StripTags(match.Groups["body"].Value);
        }

        static string MetaContent(string html, string property)
        {
            Match match = Regex.Match(html ?? "",
                @"<meta\b[^>]*property\s*=\s*[""']" + Regex.Escape(property) +
                @"[""'][^>]*content\s*=\s*[""'](?<v>[^""']*)[""'][^>]*>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);
            if (!match.Success)
                match = Regex.Match(html ?? "",
                    @"<meta\b[^>]*content\s*=\s*[""'](?<v>[^""']*)[""'][^>]*property\s*=\s*[""']" +
                    Regex.Escape(property) + @"[""'][^>]*>",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);
            return match.Success ? HtmlDecode(match.Groups["v"].Value.Trim()) : "";
        }

        static List<string> ExtractStringArray(string json, string key)
        {
            var output = new List<string>();
            int k = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return output; int p = json.IndexOf('[', k); if (p < 0) return output;
            p++;
            while (p < json.Length)
            {
                while (p < json.Length && (char.IsWhiteSpace(json[p]) || json[p] == ',')) p++;
                if (p >= json.Length || json[p] == ']') break;
                if (json[p] != '"') throw new Exception(key + " must contain strings");
                int start = p++; bool escaped = false;
                while (p < json.Length) { char c=json[p++]; if(escaped) escaped=false; else if(c=='\\') escaped=true; else if(c=='"') break; }
                output.Add(JsonLite.GetString("{\"v\":" + json.Substring(start,p-start) + "}", "v") ?? "");
            }
            return output;
        }

        static int ReadInt(string json, string key, int fallback)
        { int value; return int.TryParse(JsonLite.GetString(json,key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) ? value : fallback; }
        static double ReadDouble(string json, string key, double fallback)
        { double value; return double.TryParse(JsonLite.GetString(json,key), NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback; }

        static string Attribute(string attrs, string name)
        {
            Match m = Regex.Match(attrs ?? "", @"(?:^|\s)" + Regex.Escape(name) + @"\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);
            return m.Success ? HtmlDecode(m.Groups["v"].Value) : "";
        }

        static bool HasClass(string classes, string wanted)
        { foreach(string x in (classes??"").Split((char[])null,StringSplitOptions.RemoveEmptyEntries)) if(Eq(x,wanted)) return true; return false; }

        static bool ContainsTitle(string text, string id)
        { foreach(Match m in TitleIdRegex.Matches(text??"")) if(Eq(m.Value,id)) return true; return false; }

        static string HostKey(string host)
        { string h=(host??"").ToLowerInvariant(); return h.StartsWith("www.") ? h.Substring(4) : h; }

        static bool PackageHost(string host, List<string> blockedHosts,
            List<string> blockedSuffixes)
        {
            if (string.IsNullOrEmpty(host) || host.IndexOf('.') < 0) return false;
            foreach (string value in blockedHosts) if (Eq(host, HostKey(value))) return false;
            foreach (string suffix in blockedSuffixes)
                if (!string.IsNullOrEmpty(suffix) && host.EndsWith(suffix,
                    StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        static Dictionary<string, string> ParseMap(List<string> values)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in values)
            {
                int split = value.IndexOf('=');
                if (split <= 0 || split == value.Length - 1) continue;
                string key = HostKey(value.Substring(0, split).Trim());
                string label = value.Substring(split + 1).Trim();
                if (key.Length > 0 && label.Length > 0 && !result.ContainsKey(key))
                    result.Add(key, label);
            }
            return result;
        }

        static string MapValue(Dictionary<string, string> values, string key, string fallback)
        {
            string value; return values.TryGetValue(key ?? "", out value) ? value : fallback;
        }

        static string StripQueryKeys(Uri uri, List<string> drop)
        {
            var kept=new List<string>();
            foreach(string part in uri.Query.TrimStart('?').Split('&')) { if(part.Length==0) continue; string key=part.Split('=')[0]; bool no=false; foreach(string x in drop) if(Eq(key,x)){no=true;break;} if(!no) kept.Add(part); }
            return new UriBuilder(uri){Query=string.Join("&",kept.ToArray()),Fragment=""}.Uri.AbsoluteUri;
        }

        static bool ContainsAny(string value, List<string> needles)
        { foreach (string needle in needles) if (!string.IsNullOrEmpty(needle) && (value ?? "").IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0) return true; return false; }
        static bool EqualsAny(string value, List<string> candidates)
        { foreach (string candidate in candidates) if (Eq(value, candidate)) return true; return false; }
        static bool StartsWithAny(string value, List<string> prefixes)
        { foreach (string prefix in prefixes) if (!string.IsNullOrEmpty(prefix) && (value ?? "").StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true; return false; }

        static string AbsoluteHttpOrEmpty(string value, string basis)
        { try { return string.IsNullOrWhiteSpace(value)?"":RequireHttpUrl(MakeAbsolute(basis,value)).AbsoluteUri; } catch { return ""; } }

        static void RequireAllowed(PackageSourceDescriptor descriptor, string value, string label)
        { Uri uri=RequireHttpUrl(value); if(!OriginAllowed(descriptor,uri)) throw new Exception(label+" origin is not permitted: "+uri.Host); }

        static string MakeAbsolute(string baseUrl, string value)
        {
            Uri absolute;
            if (Uri.TryCreate(value, UriKind.Absolute, out absolute)) return absolute.AbsoluteUri;
            Uri basis;
            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out basis))
                throw new Exception("Relative URL has no valid base");
            return new Uri(basis, value).AbsoluteUri;
        }

        static Uri RequireHttpUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                !string.IsNullOrEmpty(uri.UserInfo))
                throw new Exception("Recipe emitted an invalid HTTP URL");
            return uri;
        }

        static bool ContainsToken(string text, string token)
        {
            return CountToken(text, token) > 0;
        }

        static int CountToken(string text, string token)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)) return 0;
            int count = 0, at = 0;
            while ((at = text.IndexOf(token, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                bool left = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
                int end = at + token.Length;
                bool right = end == text.Length || !char.IsLetterOrDigit(text[end]);
                if (left && right) count++;
                at = end;
            }
            return count;
        }

        static PackageAccessType ParseAccess(string value, Uri url)
        {
            if (Eq(value, "direct")) return PackageAccessType.Direct;
            if (Eq(value, "hosterlanding") || Eq(value, "hoster-landing"))
                return PackageAccessType.HosterLanding;
            string path = url.AbsolutePath;
            return path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
                ? PackageAccessType.Direct : PackageAccessType.HosterLanding;
        }

        static string InferKind(string text)
        {
            string s = (text ?? "").ToLowerInvariant();
            if (s.Contains("backport")) return "backport";
            if (s.Contains("dlc") || s.Contains("add-on") || s.Contains("addon")) return "dlc";
            // "Game + Update 1.xx" / "Game Update v1.06" is a merged base dump.
            // Installing that row as an update corrupts an already-installed title.
            bool hasGame = s.Contains("game") || s.Contains("base");
            bool hasUpdate = s.Contains("update") || s.Contains("patch");
            if (hasGame && hasUpdate) return "base";
            if (hasUpdate) return "update";
            if (hasGame) return "base";
            return "unknown";
        }

        static string InferVersion(string text)
        {
            Match match = Regex.Match(text ?? "",
                @"(?:^|\b)(?:v(?:er(?:sion)?)?\.?\s*)?(?<v>\d{1,2}(?:\.\d{1,3}){1,3})(?:\b|$)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);
            string version = match.Success ? match.Groups["v"].Value : "";
            string lower = (text ?? "").ToLowerInvariant();
            Match fw = Regex.Match(text ?? "",
                @"\((?:fix\s*)?(?<fw>\d\.\d{2}(?:\s*/\s*\d\.\d{2})*)\+?\)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexLimit);
            if (fw.Success)
            {
                string tag = lower.Contains("fix") ? "fix " + fw.Groups["fw"].Value : fw.Value.Trim();
                version = version.Length == 0 ? tag : version + " " + tag;
            }
            return version;
        }

        static string NormalizeKind(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            if (v == "game") return "base";
            if (v == "patch") return "update";
            if (v == "bp") return "backport";
            if (v == "base" || v == "update" || v == "dlc" || v == "backport") return v;
            return "unknown";
        }

        static string StableId(string sourceId, string url, string kind)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(
                    (sourceId ?? "") + "\n" + (kind ?? "") + "\n" + (url ?? "")));
                var sb = new StringBuilder(24);
                for (int i = 0; i < 12; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }

        static string StripTags(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return Collapse(HtmlDecode(TagRegex.Replace(value, " ")));
        }

        static string HtmlDecode(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                .Replace("&quot;", "\"").Replace("&#39;", "'").Replace("&apos;", "'")
                .Replace("&nbsp;", " ");
        }

        static string Collapse(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            bool space = false;
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        static string Clip(string value, int max)
        {
            value = Collapse(value);
            return value.Length <= max ? value : value.Substring(0, max - 1) + "…";
        }

        static string SafeMessage(string value)
        {
            value = Collapse(value ?? "Recipe failed");
            return value.Length <= 160 ? value : value.Substring(0, 160);
        }

        static void CheckDeadline(DateTime deadline)
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Recipe exceeded 60 seconds");
        }

        static int DefaultPort(string scheme) { return Eq(scheme, "https") ? 443 : 80; }
        static bool Eq(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }
}
