using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Orbis
{
    /// <summary>Keeps installed community sources current. A maintainer publishes new
    /// revisions under a source's permanent directory ID; SSPI asks the directory about
    /// its installed sources, downloads and verifies any newer revision, and installs it
    /// over the old one (same source ID; the enabled state is kept).
    ///
    /// A source installed from the directory is followed by its permanent ID. Any other
    /// installed source (USB, a link, or a directory install by an older SSPI build) is
    /// looked up by its file hash, which resolves only if that exact file was shared.</summary>
    internal static class CommunitySourceUpdates
    {
        internal sealed class Origin { internal string Source = "", Sha256 = ""; internal int Revision = 1; }

        internal sealed class Update { internal string SourceId = "", Name = ""; internal int Revision; }

        internal sealed class Result
        {
            internal readonly List<Update> Updated = new List<Update>();
            internal int Followed;
            internal string Error = "";
        }

        static readonly object Gate = new object();
        internal static string OriginsPath { get { return Path.Combine(AppSettings.DataDir, "sources", "community.json"); } }

        internal static Dictionary<string, Origin> LoadOrigins()
        {
            var result = new Dictionary<string, Origin>(StringComparer.Ordinal);
            try
            {
                var file = new FileInfo(OriginsPath);
                if (!file.Exists || file.Length > 512 * 1024) return result;
                var root = PackageSourceJson.Parse(File.ReadAllText(file.FullName, Encoding.UTF8)) as Dictionary<string, object>;
                object value; var sources = root != null && root.TryGetValue("sources", out value) ? value as Dictionary<string, object> : null;
                if (sources == null) return result;
                foreach (var pair in sources)
                {
                    var row = pair.Value as Dictionary<string, object>;
                    if (row == null || pair.Key.Length == 0 || pair.Key.Length > 200) continue;
                    var origin = new Origin { Source = CommunitySources.Text(row, "source", 64), Sha256 = CommunitySources.Text(row, "sha256", 64) };
                    object revision; if (row.TryGetValue("revision", out revision)) try { origin.Revision = Math.Max(1, Convert.ToInt32(revision)); } catch { }
                    if (CommunitySources.ValidId(origin.Source) && CommunitySources.ValidId(origin.Sha256)) result[pair.Key] = origin;
                }
            }
            catch (Exception ex) { Log("event=community_source_updates result=origins_unreadable exception=" + ex.GetType().Name); }
            return result;
        }

        static void SaveOrigins(Dictionary<string, Origin> origins)
        {
            var text = new StringBuilder("{\"version\":1,\"sources\":{");
            bool first = true;
            foreach (var pair in origins)
            {
                if (!first) text.Append(',');
                first = false;
                text.Append('"').Append(JsonLite.Escape(pair.Key)).Append("\":{\"source\":\"").Append(pair.Value.Source)
                    .Append("\",\"sha256\":\"").Append(pair.Value.Sha256).Append("\",\"revision\":").Append(pair.Value.Revision).Append('}');
            }
            string path = OriginsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            AtomicFile.WriteText(path, text.Append("}}").ToString());
        }

        /// <summary>Remembers which directory source an installed source came from.</summary>
        internal static void RecordInstall(string sourceId, CommunitySourceEntry entry)
        {
            if (string.IsNullOrEmpty(sourceId) || entry == null || !CommunitySources.ValidId(entry.Id)) return;
            lock (Gate)
            {
                try
                {
                    var origins = LoadOrigins();
                    origins[sourceId] = new Origin { Source = CommunitySources.ValidId(entry.Source) ? entry.Source : entry.Id, Sha256 = entry.Id, Revision = entry.Revision };
                    SaveOrigins(origins);
                }
                catch (Exception ex) { Log("event=community_source_updates result=origin_save_failed exception=" + ex.GetType().Name + ": " + ex.Message); }
            }
        }

        internal static Result Check(PackageSourceRuntimeBridge sources, Func<bool> cancel)
        {
            return Check(sources.InstalledSources, (bytes, id) => { string error; return sources.InstallUpdate(bytes, id, out error) ? null : error ?? "Install failed"; }, cancel);
        }

        /// <summary>One pass over the installed sources. Throws when the directory cannot be
        /// reached; a failure for one source is logged and the others continue.</summary>
        internal static Result Check(Func<List<PackageSourceRegistryEntry>> installed, Func<byte[], string, string> install, Func<bool> cancel)
        {
            var result = new Result();
            lock (Gate)
            {
                var origins = LoadOrigins();
                var owners = new Dictionary<string, List<PackageSourceRegistryEntry>>(StringComparer.Ordinal);
                foreach (var entry in installed())
                {
                    if (entry == null || string.IsNullOrEmpty(entry.SourceId)) continue;
                    string hash = (entry.PackageSha256 ?? "").ToLowerInvariant();
                    Origin origin;
                    // Follow the directory source only while the installed files are the ones it
                    // supplied; a source replaced by hand is looked up by its own file instead.
                    string query = origins.TryGetValue(entry.SourceId, out origin) && origin.Sha256 == hash ? origin.Source : hash;
                    if (!CommunitySources.ValidId(query)) continue;
                    List<PackageSourceRegistryEntry> list;
                    if (!owners.TryGetValue(query, out list)) owners[query] = list = new List<PackageSourceRegistryEntry>();
                    list.Add(entry);
                }
                if (owners.Count == 0) return result;
                var queries = new List<string>(owners.Keys);
                queries.Sort(StringComparer.Ordinal);
                bool changed = false;
                for (int start = 0; start < queries.Count; start += CommunitySources.MaximumStatusQueries)
                {
                    if (cancel != null && cancel()) break;
                    var page = queries.GetRange(start, Math.Min(CommunitySources.MaximumStatusQueries, queries.Count - start));
                    foreach (var status in CommunitySources.Status(page, 30000, cancel))
                    {
                        if (status.Entry == null) continue;
                        foreach (var entry in owners[status.Query])
                        {
                            if (cancel != null && cancel()) break;
                            result.Followed++;
                            string hash = (entry.PackageSha256 ?? "").ToLowerInvariant();
                            var latest = status.Entry;
                            if (latest.Id != hash)
                            {
                                try
                                {
                                    byte[] bytes = CommunitySources.Download(latest); // verifies size, SHA-256 and format
                                    string error = install(bytes, entry.SourceId);
                                    if (error != null) throw new InvalidDataException(error);
                                    result.Updated.Add(new Update { SourceId = entry.SourceId, Name = latest.Name.Length > 0 ? latest.Name : entry.Name, Revision = latest.Revision });
                                    Log("event=community_source_updates result=updated source=" + entry.SourceId + " revision=" + latest.Revision +
                                        " from=" + Short(hash) + " to=" + Short(latest.Id));
                                }
                                catch (Exception ex)
                                {
                                    result.Error = ex.Message;
                                    Log("event=community_source_updates result=update_failed source=" + entry.SourceId + " revision=" + latest.Revision +
                                        " exception=" + ex.GetType().Name + ": " + ex.Message);
                                    continue;
                                }
                            }
                            Origin known;
                            if (!origins.TryGetValue(entry.SourceId, out known) || known.Source != latest.Source || known.Sha256 != latest.Id || known.Revision != latest.Revision)
                            {
                                origins[entry.SourceId] = new Origin { Source = latest.Source, Sha256 = latest.Id, Revision = latest.Revision };
                                changed = true;
                            }
                        }
                    }
                }
                if (changed)
                {
                    try { SaveOrigins(origins); }
                    catch (Exception ex) { Log("event=community_source_updates result=origin_save_failed exception=" + ex.GetType().Name + ": " + ex.Message); }
                }
                Log("event=community_source_updates result=checked installed=" + owners.Count + " followed=" + result.Followed + " updated=" + result.Updated.Count);
            }
            return result;
        }

        static string Short(string hash) { return string.IsNullOrEmpty(hash) ? "none" : hash.Substring(0, Math.Min(12, hash.Length)); }

        static void Log(string line) { try { SspiLog.Write("network", line); } catch { } }
    }
}
