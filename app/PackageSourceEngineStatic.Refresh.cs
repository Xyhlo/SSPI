using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal static partial class PackageSourceEngineStatic
    {
        const int RefreshCacheBytes = 8 * 1024 * 1024;
        const int RefreshWorkBytes = 2 * 1024 * 1024;
        sealed class RefreshConfiguration
        {
            internal string PostsUrl, DownloadPostsUrl, Since;
            internal int Category, PageSize, MaxPosts, MaxRequests, Seconds, RequestSeconds;
            internal readonly List<Dictionary<string, object>> Aliases = new List<Dictionary<string, object>>();
            internal readonly HashSet<string> KnownPages = new HashSet<string>(StringComparer.Ordinal);
            internal readonly HashSet<string> FileHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<object> Passwords = new List<object>();
        }
        sealed class RefreshState
        {
            internal readonly object Gate = new object(), RunGate = new object();
            internal string Path;
            internal int Page = 1;
            internal DateTime NextListing;
            internal readonly Dictionary<string, object> Work = new Dictionary<string, object>(StringComparer.Ordinal);
            internal readonly Dictionary<string, DateTime> Queries = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            internal readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);
            internal readonly List<object> Pending = new List<object>(), Items = new List<object>();
            internal readonly Dictionary<string, SourceTitleResult> Titles = new Dictionary<string, SourceTitleResult>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, List<PackageCandidate>> Packages = new Dictionary<string, List<PackageCandidate>>(StringComparer.OrdinalIgnoreCase);
        }
        sealed class RefreshRun
        {
            internal Catalog Catalog;
            internal DateTime Deadline;
            internal Func<bool> Cancel;
            internal int Requests, Epoch;
            internal Dictionary<string, object> Work;
            internal string Get(string url)
            {
                Check();
                if (Requests >= Catalog.Refresh.MaxRequests) throw new TimeoutException("Refresh request limit reached");
                Requests++;
                RequireRefreshOrigin(Catalog.Descriptor, url);
                int timeout = (int)Math.Min(Catalog.Refresh.RequestSeconds * 1000, Math.Max(1, (Deadline - DateTime.UtcNow).TotalMilliseconds));
                string value = SourceHttps.GetString(url, timeout, null, "Mozilla/5.0 SSPI-SourceRefresh/1", Cancel,
                    target => { RequireRefreshOrigin(Catalog.Descriptor, target.AbsoluteUri); return true; });
                Check();
                if (value == null || Encoding.UTF8.GetByteCount(value) > 2 * 1024 * 1024) throw Bad("Refresh response exceeds 2 MiB");
                return value;
            }
            internal string GetIntermediate(string url)
            {
                Check(); RequireRefreshOrigin(Catalog.Descriptor, url);
                object cached;
                if (Work != null && Object(Work["pages"], "saved pages").TryGetValue(url, out cached)) return (string)cached;
                return Get(url);
            }
            internal void SaveIntermediate(string url, string body)
            {
                if (Work == null) return;
                var pages = Object(Work["pages"], "saved pages");
                if (pages.Count >= 32) return;
                pages[url] = body;
                if (Encoding.UTF8.GetByteCount(RefreshJson(Catalog.Updates.Work)) > RefreshWorkBytes) pages.Remove(url);
            }
            internal void Check()
            {
                CheckCanceled(Cancel);
                lock (Gate) if (Epoch != PackageSourceEngineStatic.Epoch) throw new OperationCanceledException("Sources changed");
                if (DateTime.UtcNow >= Deadline) throw new TimeoutException("Refresh time limit reached");
            }
        }

        static RefreshConfiguration ReadRefreshConfiguration(Dictionary<string, object> root, PackageSourceDescriptor descriptor)
        {
            var data = Object(Value(root, "refresh", true), "refresh configuration");
            if (Text(data, "format", 64, true) != "wordpress-ps4-delta-v1") throw Bad("Unsupported refresh format");
            var config = new RefreshConfiguration { PostsUrl = Text(data, "postsUrl", 2048, true),
                DownloadPostsUrl = Text(data, "downloadPostsUrl", 2048, true), Since = Text(data, "sinceUtc", 32, true),
                Category = RefreshInt(data, "categoryId", 1, 1000000), PageSize = RefreshInt(data, "pageSize", 1, 20),
                MaxPosts = RefreshInt(data, "maxNewPosts", 1, 3), MaxRequests = RefreshInt(data, "maxRequests", 1, 12),
                Seconds = RefreshInt(data, "deadlineSeconds", 1, 20) };
            config.RequestSeconds = data.ContainsKey("requestTimeoutSeconds") ? RefreshInt(data, "requestTimeoutSeconds", 1, 10) : 10;
            if (data.ContainsKey("titleAliases")) foreach (object value in Array(data, "titleAliases", 32, true))
            {
                var alias = Object(value, "title aliases"); TitleId(Text(alias, "titleId", 16, true)); Text(alias, "name", 512, true);
                foreach (object name in Array(alias, "aliases", 8, true))
                    if (!(name is string) || ((string)name).Length < 1 || ((string)name).Length > 512) throw Bad("Invalid title alias");
                config.Aliases.Add(alias);
            }
            DateTime since;
            if (!DateTime.TryParseExact(config.Since, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out since)) throw Bad("Refresh timestamp must be UTC");
            foreach (string endpoint in new[] { config.PostsUrl, config.DownloadPostsUrl })
            {
                Uri uri = RequireRefreshOrigin(descriptor, endpoint);
                if (uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.EndsWith("/wp-json/wp/v2/posts", StringComparison.Ordinal)) throw Bad("Refresh needs a WordPress posts endpoint");
            }
            if (descriptor.Permissions.RedirectOrigins.Count != 0) throw Bad("Refresh has no redirect-origin extension");
            foreach (object value in Array(data, "knownPages", 15000, true))
            {
                string hash = value as string;
                if (hash == null || hash.Length != 64 || Sha(hash).Length != 64 || !config.KnownPages.Add(hash)) throw Bad("Invalid refresh page fingerprint");
            }
            foreach (object value in Array(data, "fileHosts", 64, true))
            {
                string hostname = value as string; Uri hostUri;
                if (string.IsNullOrEmpty(hostname) || !Uri.TryCreate("https://" + hostname, UriKind.Absolute, out hostUri) ||
                    hostUri.Host != hostname || hostUri.AbsolutePath != "/" || hostUri.Query.Length != 0 || hostUri.UserInfo.Length != 0) throw Bad("Invalid refresh file host");
                config.FileHosts.Add(hostname);
            }
            foreach (object value in Array(data, "archivePasswords", 4, true)) config.Passwords.Add(value);
            ArchivePasswordDefaults.Encode(config.Passwords);
            return config;
        }
        static int RefreshInt(Dictionary<string, object> data, string name, int minimum, int maximum)
        { long value = Integer(data, name, true); if (value < minimum || value > maximum) throw Bad("Refresh bound out of range: " + name); return (int)value; }
        static Uri RequireRefreshOrigin(PackageSourceDescriptor descriptor, string url)
        {
            Uri uri = new Uri(Url(url, true), UriKind.Absolute);
            if (uri.Scheme != "https") throw Bad("Refresh endpoints require HTTPS");
            string origin = uri.GetLeftPart(UriPartial.Authority);
            foreach (string allowed in descriptor.Permissions.NetworkOrigins)
                if (string.Equals(origin, allowed.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return uri;
            throw Bad("Refresh origin is not declared");
        }
        static string PageHash(string url) { return Hash(Encoding.UTF8.GetBytes(url.TrimEnd('/'))); }

        static void InitializeRefresh(Catalog catalog)
        {
            if (catalog.Refresh == null) return;
            var state = new RefreshState(); catalog.Updates = state;
            string identity = catalog.Descriptor.SourceId + "\n" + catalog.Descriptor.Version;
            state.Path = Path.Combine(AppSettings.DataDir, "sources", "refresh", Hash(Encoding.UTF8.GetBytes(identity)) + ".json");
            if (!File.Exists(state.Path)) return;
            try
            {
                var info = new FileInfo(state.Path);
                if (info.Length < 2 || info.Length > RefreshCacheBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw Bad("Invalid refresh cache");
                var saved = Parse(File.ReadAllBytes(state.Path), RefreshCacheBytes);
                if (Text(saved, "sourceId", 128, true) != catalog.Descriptor.SourceId || Text(saved, "version", 64, true) != catalog.Descriptor.Version) throw Bad("Refresh cache belongs to another source");
                state.Page = RefreshInt(saved, "page", 1, 100000);
                foreach (object value in Array(saved, "seen", 15000, true)) { string hash = value as string; if (hash == null || Sha(hash).Length != 64) throw Bad("Invalid cached page identity"); state.Seen.Add(hash); }
                foreach (object value in Array(saved, "pending", 100, true)) { ValidatePending(Object(value, "pending post"), catalog); state.Pending.Add(value); }
                foreach (object value in Array(saved, "items", 1000, true)) AddRefreshItem(catalog, state, Object(value, "cached title"));
                // Optional work is disposable; a stale partial lookup must not erase completed results.
                try
                {
                    if (saved.ContainsKey("work")) foreach (var entry in Object(saved["work"], "saved work"))
                    {
                        var work = Object(entry.Value, "saved post work");
                        if (ReadRefreshDate(work, "expiresUtc") <= DateTime.UtcNow) continue;
                        Sha(Text(work, "fingerprint", 64, true));
                        var pages = Object(Value(work, "pages", true), "saved pages");
                        if (pages.Count > 32 || state.Work.Count >= 8) continue;
                        foreach (var page in pages) { RequireRefreshOrigin(catalog.Descriptor, page.Key); if (!(page.Value is string)) throw Bad("Invalid saved response"); }
                        state.Work[entry.Key] = work;
                        if (Encoding.UTF8.GetByteCount(RefreshJson(state.Work)) > RefreshWorkBytes) { state.Work.Remove(entry.Key); break; }
                    }
                }
                catch { state.Work.Clear(); }
            }
            catch (Exception ex)
            {
                state.Page = 1; state.Seen.Clear(); state.Pending.Clear(); state.Items.Clear(); state.Titles.Clear(); state.Packages.Clear();
                SspiLog.Write("source-refresh", "Saved refresh ignored: " + ex.GetType().Name);
            }
        }
        static void ValidatePending(Dictionary<string, object> post, Catalog catalog)
        {
            if (Integer(post, "id", true) < 1) throw Bad("Invalid pending post ID");
            RequireRefreshOrigin(catalog.Descriptor, Text(post, "link", 2048, true));
            Text(post, "name", 512, true);
        }
        static void AddRefreshItem(Catalog catalog, RefreshState state, Dictionary<string, object> item)
        {
            string id = TitleId(Text(item, "titleId", 16, true));
            if (catalog.ById.ContainsKey(id) || state.Titles.ContainsKey(id)) return;
            if (state.Items.Count >= 1000) throw Bad("Refresh cache is full; install a newer base catalog");
            var title = new SourceTitleResult { TitleId = id, DisplayName = Text(item, "name", 512, true),
                Region = NormalizeRegion(Text(item, "region", 32, false)), ImageUrl = Url(Text(item, "icon", 2048, false), false),
                SourceId = catalog.Descriptor.SourceId, SourceVersion = catalog.Descriptor.Version, SourceAttribution = catalog.Descriptor.DisplayName };
            title.StableResultId = id + "|" + title.Region;
            IList downloads = Array(item, "downloads", 512, true);
            if (downloads.Count == 0) throw Bad("New title has no usable package choices");
            byte[] payload = Encoding.UTF8.GetBytes(RefreshJson(RefreshObject("titles", RefreshObject(id, downloads))));
            var validation = new Catalog { Descriptor = catalog.Descriptor, Read = ignored => payload };
            var record = new Record { Title = title, PackagesFile = "delta.json", ReleaseCount = downloads.Count };
            validation.Records.Add(record); validation.ById.Add(id, record);
            List<PackageCandidate> packages = ReadShard(validation, "delta.json", null)[id];
            state.Items.Add(item); state.Titles.Add(id, title); state.Packages.Add(id, packages);
        }
        static void AppendRefreshMatches(Catalog catalog, SourceSearchRequest request, List<SourceTitleResult> result)
        {
            if (catalog.Updates == null) return;
            string region = NormalizeRegion(request.Region);
            lock (catalog.Updates.Gate) foreach (var entry in catalog.Updates.Titles.Values)
            {
                CheckCanceled(request.Cancel);
                if (region.Length > 0 && entry.Region != region) continue;
                int rank = RefreshAliasRank(catalog, entry.TitleId, entry.DisplayName, request.Query, SearchRank(entry, request.Query)); if (rank < 0) continue;
                var title = CloneTitle(entry); title.SearchRankHint = rank; result.Add(title);
            }
            result.Sort((a, b) => { int order = a.SearchRankHint.CompareTo(b.SearchRankHint); return order != 0 ? order : CompareTitle(a, b); });
        }
        static List<PackageCandidate> ResolveRefresh(Catalog catalog, SourceResolveRequest request)
        {
            var result = new List<PackageCandidate>(); if (catalog.Updates == null) return result;
            string region = NormalizeRegion(request.Region);
            lock (catalog.Updates.Gate)
            {
                List<PackageCandidate> rows;
                if (catalog.Updates.Packages.TryGetValue((request.TitleId ?? "").Trim(), out rows))
                    foreach (var row in rows) if (region.Length == 0 || row.Region == region) result.Add(CloneCandidate(row));
            }
            return result;
        }

        static DateTime ReadRefreshDate(Dictionary<string, object> value, string key)
        {
            DateTime date; object text;
            return value.TryGetValue(key, out text) && text is string && DateTime.TryParse((string)text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date) ? date : DateTime.MinValue;
        }
        static int RefreshAliasRank(Catalog catalog, string id, string name, string query, int rank)
        {
            if (catalog.Refresh == null || string.IsNullOrWhiteSpace(query)) return rank;
            foreach (var entry in catalog.Refresh.Aliases)
            {
                string canonical = NormalizeSearch((string)entry["name"]), actual = NormalizeSearch(name);
                if (id != (string)entry["titleId"] && actual != canonical && actual != canonical + " ps4 pkg") continue;
                foreach (string alias in (IList)entry["aliases"])
                {
                    int candidate = SearchRank(new SourceTitleResult { TitleId = id, DisplayName = alias }, query);
                    if (candidate >= 0 && (rank < 0 || candidate < rank)) rank = candidate;
                }
            }
            return rank;
        }
        static int PendingRank(Catalog catalog, Dictionary<string, object> post, string query)
        {
            string name = Text(post, "name", 512, true);
            return RefreshAliasRank(catalog, "", name, query, SearchRank(new SourceTitleResult { TitleId = "", DisplayName = name }, query));
        }
        static void QueueRefreshPosts(RefreshRun run, string body, string query)
        {
            Catalog catalog = run.Catalog; RefreshState state = catalog.Updates;
            IList posts = StrictJson.Parse(body) as IList;
            if (posts == null || posts.Count > catalog.Refresh.PageSize) throw Bad("Refresh listing is not a bounded post array");
            foreach (object value in posts)
            {
                var post = Object(value, "post"); long id = Integer(post, "id", true);
                string link = Text(post, "link", 2048, true); RequireRefreshOrigin(catalog.Descriptor, link);
                string hash = PageHash(link);
                if (catalog.Refresh.KnownPages.Contains(hash) || state.Seen.Contains(hash)) continue;
                bool pending = false; foreach (object old in state.Pending) if (Integer(Object(old, "pending"), "id", true) == id) { pending = true; break; }
                if (pending || state.Pending.Count >= 100) continue;
                string name = RefreshText(Text(Object(Value(post, "title", true), "post title"), "rendered", 2048, true));
                var entry = RefreshObject("id", id, "link", link, "name", name); ValidatePending(entry, catalog);
                if (!string.IsNullOrWhiteSpace(query) && PendingRank(catalog, entry, query) < 0) continue;
                state.Pending.Add(entry);
            }
        }
        static void PrepareRefreshWork(RefreshRun run, Dictionary<string, object> post, string html)
        {
            var state = run.Catalog.Updates; string key = PageHash((string)post["link"]), fingerprint = Hash(Encoding.UTF8.GetBytes(html));
            var expired = new List<string>();
            foreach (var entry in state.Work) if (ReadRefreshDate(Object(entry.Value, "post work"), "expiresUtc") <= DateTime.UtcNow) expired.Add(entry.Key);
            foreach (string old in expired) state.Work.Remove(old);
            object saved;
            if (state.Work.TryGetValue(key, out saved) && (string)Object(saved, "post work")["fingerprint"] == fingerprint) run.Work = (Dictionary<string, object>)saved;
            else
            {
                if (state.Work.Count >= 8 && !state.Work.ContainsKey(key)) { foreach (string old in state.Work.Keys) { state.Work.Remove(old); break; } }
                run.Work = RefreshObject("fingerprint", fingerprint, "expiresUtc", DateTime.UtcNow.AddHours(24).ToString("o"), "pages", RefreshObject());
                state.Work[key] = run.Work;
            }
        }

        internal static bool RefreshOnce(InstalledPackageSource source, string path, Func<bool> cancel, string query = "")
        {
            Catalog catalog = GetCatalog(source, path, cancel);
            if (catalog.Refresh == null) return false;
            RefreshState state = catalog.Updates;
            if (!Monitor.TryEnter(state.RunGate)) return false;
            var run = new RefreshRun { Catalog = catalog, Cancel = cancel, Deadline = DateTime.UtcNow.AddSeconds(catalog.Refresh.Seconds) };
            lock (Gate) run.Epoch = Epoch;
            int previousCount = state.Items.Count; string error = "";
            try
            {
                RefreshConfiguration config = catalog.Refresh;
                if (state.Pending.Count < 80 && DateTime.UtcNow >= state.NextListing)
                {
                    try
                    {
                        string url = config.PostsUrl + "?categories=" + config.Category + "&after=" + Uri.EscapeDataString(config.Since) +
                            "&orderby=id&order=asc&per_page=" + config.PageSize + "&page=" + state.Page + "&_fields=id,link,title";
                        state.NextListing = DateTime.UtcNow.AddSeconds(60);
                        string body = run.Get(url);
                        IList posts = StrictJson.Parse(body) as IList;
                        if (posts == null || posts.Count > config.PageSize) throw Bad("Refresh listing is not a bounded post array");
                        QueueRefreshPosts(run, body, "");
                        if (posts.Count == config.PageSize) state.Page++;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { error = ex.Message; }
                }
                string normalized = NormalizeSearch(query); bool pendingMatch = false, localMatch = false;
                foreach (object value in state.Pending) if (PendingRank(catalog, Object(value, "pending"), query) >= 0) { pendingMatch = true; break; }
                foreach (var record in catalog.Records) if (RefreshAliasRank(catalog, record.Title.TitleId, record.Title.DisplayName, query,
                    Rank(record.NormalizedId, record.NormalizedName, record.SearchText, normalized, normalized.Split(' '))) >= 0) { localMatch = true; break; }
                if (!localMatch) foreach (var title in state.Titles.Values) if (RefreshAliasRank(catalog, title.TitleId, title.DisplayName, query, SearchRank(title, query)) >= 0) { localMatch = true; break; }
                DateTime checkedAt;
                if (normalized.Length > 1 && normalized.Length <= 200 && !pendingMatch && !localMatch &&
                    (!state.Queries.TryGetValue(normalized, out checkedAt) || DateTime.UtcNow >= checkedAt.AddMinutes(5)))
                {
                    try
                    {
                        if (state.Queries.Count >= 64) state.Queries.Clear();
                        string[] words = normalized.Split(' ');
                        for (int i = 0; i < words.Length; i++) words[i] = NumberWord(words[i]);
                        string search = string.Join(" ", words);
                        foreach (var alias in config.Aliases) foreach (string name in (IList)alias["aliases"])
                            if (NormalizeSearch(name) == normalized) search = (string)alias["name"];
                        string url = config.PostsUrl + "?categories=" + config.Category + "&after=" + Uri.EscapeDataString(config.Since) +
                            "&search=" + Uri.EscapeDataString(search) + "&per_page=" + config.PageSize + "&_fields=id,link,title";
                        QueueRefreshPosts(run, run.Get(url), query);
                        state.Queries[normalized] = DateTime.UtcNow;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { error = ex.Message; }
                }
                int attempts = Math.Min(config.MaxPosts, state.Pending.Count);
                var attempted = new HashSet<long>();
                for (int number = 0; number < attempts; number++)
                {
                    run.Check(); int selected = -1, best = int.MaxValue;
                    for (int i = 0; i < state.Pending.Count; i++)
                    {
                        var candidate = Object(state.Pending[i], "pending post");
                        if (attempted.Contains(Integer(candidate, "id", true)) || ReadRefreshDate(candidate, "retryUtc") > DateTime.UtcNow) continue;
                        int rank = number == 0 && normalized.Length > 0 ? PendingRank(catalog, candidate, query) : -1;
                        if (rank < 0) rank = 100;
                        if (selected < 0 || rank < best) { selected = i; best = rank; }
                    }
                    if (selected < 0) break;
                    var post = Object(state.Pending[selected], "pending post"); state.Pending.RemoveAt(selected); attempted.Add(Integer(post, "id", true));
                    try
                    {
                        string body = run.Get(config.PostsUrl + "/" + Integer(post, "id", true) + "?_fields=id,link,content");
                        var detail = Object(StrictJson.Parse(body), "post detail");
                        if (Integer(detail, "id", true) != Integer(post, "id", true) || Text(detail, "link", 2048, true).TrimEnd('/') != Text(post, "link", 2048, true).TrimEnd('/')) throw Bad("Post identity changed");
                        string html = RefreshHtml(Object(Value(detail, "content", true), "post content"));
                        PrepareRefreshWork(run, post, html);
                        List<Dictionary<string, object>> items = RefreshParsePost(run, post, html);
                        if (items.Count == 0) throw Bad("New post has no resolved regional package choices");
                        run.Check();
                        lock (state.Gate)
                        {
                            int oldCount = state.Items.Count;
                            try
                            {
                                if (state.Seen.Count >= 15000) throw Bad("Refresh history is full; update the base source");
                                foreach (var item in items) AddRefreshItem(catalog, state, item);
                                if (Encoding.UTF8.GetByteCount(RefreshJson(SaveRefreshState(catalog))) > RefreshCacheBytes - 1024) throw Bad("Refresh cache is full; update the base source");
                                state.Seen.Add(PageHash(Text(post, "link", 2048, true)));
                                state.Work.Remove(PageHash(Text(post, "link", 2048, true)));
                            }
                            catch
                            {
                                while (state.Items.Count > oldCount)
                                {
                                    var added = Object(state.Items[state.Items.Count - 1], "new title");
                                    string id = Text(added, "titleId", 16, true);
                                    state.Titles.Remove(id); state.Packages.Remove(id); state.Items.RemoveAt(state.Items.Count - 1);
                                }
                                throw;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!(ex is OperationCanceledException)) post["retryUtc"] = DateTime.UtcNow.AddSeconds(30).ToString("o");
                        state.Pending.Add(post); error = ex.Message;
                        if (ex is OperationCanceledException || ex is TimeoutException) throw;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { error = ex.Message; }
            finally
            {
                try
                {
                    lock (state.Gate)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(state.Path));
                        var saved = SaveRefreshState(catalog); saved["lastCheckUtc"] = DateTime.UtcNow.ToString("o");
                        saved["lastError"] = error.Length > 180 ? error.Substring(0, 180) : error;
                        saved["lastRequests"] = run.Requests;
                        string json = RefreshJson(saved);
                        if (Encoding.UTF8.GetByteCount(json) <= RefreshCacheBytes) AtomicFile.WriteText(state.Path, json);
                    }
                    SspiLog.Write("source-refresh", "Check stopped; requests=" + run.Requests + "; new regional titles=" + (state.Items.Count - previousCount) + "; pending=" + state.Pending.Count + (error.Length > 0 ? "; incomplete" : ""));
                }
                catch (Exception ex) { SspiLog.Write("source-refresh", "Cache save failed: " + ex.GetType().Name); }
                Monitor.Exit(state.RunGate);
            }
            return state.Items.Count != previousCount;
        }
        static Dictionary<string, object> SaveRefreshState(Catalog catalog)
        {
            var seen = new List<object>(); foreach (string value in catalog.Updates.Seen) seen.Add(value);
            return RefreshObject("sourceId", catalog.Descriptor.SourceId, "version", catalog.Descriptor.Version,
                "page", catalog.Updates.Page, "seen", seen, "pending", catalog.Updates.Pending, "items", catalog.Updates.Items, "work", catalog.Updates.Work);
        }
        static string RefreshHtml(Dictionary<string, object> content)
        {
            object raw = Value(content, "rendered", true); string html = raw as string;
            if (html == null || Encoding.UTF8.GetByteCount(html) > 2 * 1024 * 1024) throw Bad("Invalid refresh HTML");
            return html;
        }
        static Dictionary<string, object> RefreshObject(params object[] values)
        { var result = new Dictionary<string, object>(StringComparer.Ordinal); for (int i = 0; i < values.Length; i += 2) result.Add((string)values[i], values[i + 1]); return result; }
        static string RefreshJson(object value)
        {
            if (value == null) return "null";
            if (value is string) return "\"" + JsonLite.Escape((string)value) + "\"";
            if (value is bool) return (bool)value ? "true" : "false";
            var dictionary = value as Dictionary<string, object>;
            var list = value as IList;
            if (dictionary != null) { var fields = new List<string>(); foreach (var pair in dictionary) fields.Add(RefreshJson(pair.Key) + ":" + RefreshJson(pair.Value)); return "{" + string.Join(",", fields.ToArray()) + "}"; }
            if (list != null) { var items = new List<string>(); foreach (object item in list) items.Add(RefreshJson(item)); return "[" + string.Join(",", items.ToArray()) + "]"; }
            if (value is long || value is int) return Convert.ToString(value, CultureInfo.InvariantCulture);
            throw Bad("Unsupported refresh JSON value");
        }
    }
}
