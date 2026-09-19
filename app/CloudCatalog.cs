using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

namespace Orbis
{
    internal sealed class CloudFile
    {
        public string Name, Locator, Detail;
        public bool Folder;
        public bool Ready = true;
        public long Size;
    }

    internal static class CloudCatalog
    {
        const string Rd = "https://api.real-debrid.com/rest/1.0";
        const string Tb = "https://api.torbox.app/v1/api";
        const string Ad = "https://api.alldebrid.com";
        static readonly object Gate = new object();
        static DateTime NextRequest;
        internal static Func<string, string, string> Fetch;
        internal static Func<string, string, string, string> Submit;
        static object Get(string url, string token, string form = null)
        {
            if (string.IsNullOrWhiteSpace(token)) throw new IOException("Connect this service in Connections first");
            lock (Gate)
            {
                if (NextRequest > DateTime.UtcNow) System.Threading.Thread.Sleep((int)Math.Min(1000, (NextRequest-DateTime.UtcNow).TotalMilliseconds));
                NextRequest = DateTime.UtcNow.AddMilliseconds(750);
                string json;
                try { json = form == null
                    ? (Fetch != null ? Fetch(url, token) : NetHttp.GetString(url, 20000, null, token.Trim()))
                    : (Submit != null ? Submit(url, token, form) : NetHttp.PostForm(url, form, 20000, null, token.Trim())); }
                catch(Exception ex) {
                    var http = DownloadHttpException.Find(ex);
                    if(http != null) throw new DownloadHttpException(http.StatusCode, http.RetryAfterSeconds.ToString(), "accessing cloud files");
                    throw new IOException("Cloud service temporarily unavailable. Retry shortly; your files are retained.");
                }
                object result = PackageSourceJson.Parse(json);
                var row = result as Dictionary<string, object>;
                if (row != null && (Value(row,"error_code") != null || Text(row,"status") == "error" || Text(row,"success") == "False" || Text(row,"success") == "false"))
                    throw new IOException("Service could not list this item; check account status and retry shortly");
                return result;
            }
        }
        static object Value(Dictionary<string, object> row, string name) { object v; return row != null && row.TryGetValue(name,out v) ? v : null; }
        static string Text(Dictionary<string, object> row,string name) { return Convert.ToString(Value(row,name),System.Globalization.CultureInfo.InvariantCulture); }
        static bool True(Dictionary<string,object> row,string name) { return string.Equals(Text(row,name),"true",StringComparison.OrdinalIgnoreCase); }
        static long Bytes(Dictionary<string,object> row,string name) { long n; return long.TryParse(Text(row,name),out n) ? Math.Max(0,n) : 0; }
        static string FileDetail(string provider,long size)
        { return "Ready in " + provider + (size > 0 ? " · " + (size / 1073741824.0).ToString("0.##",System.Globalization.CultureInfo.InvariantCulture) + " GB" : ""); }
        static bool TorBoxReady(Dictionary<string,object> row) { return True(row,"download_present") && (True(row,"download_finished") || True(row,"cached")); }
        static string TorBoxReason(Dictionary<string,object> row) { return TorBoxReady(row)?"Ready in TorBox":"Not ready: "+(string.IsNullOrEmpty(Text(row,"download_state"))?"provider has not confirmed stored files":Text(row,"download_state")); }
        static IEnumerable<object> Rows(object value)
        {
            var list = value as IList; if(list!=null) foreach(object item in list) yield return item;
        }
        static string Id(string id)
        {
            if(string.IsNullOrEmpty(id)||id.Length>128) throw new IOException("Invalid cloud item ID");
            foreach(char c in id) if(!char.IsLetterOrDigit(c)&&c!='-'&&c!='_')throw new IOException("Invalid cloud item ID");
            return id;
        }
        internal static bool ValidLink(string url)
        { Uri u;return !string.IsNullOrWhiteSpace(url) && url.Length<=8192 && Uri.TryCreate(url,UriKind.Absolute,out u) &&
            (u.Scheme=="https"||u.Scheme=="http") && u.UserInfo.Length==0 && url.IndexOfAny(new[]{'\r','\n','\0'})<0; }
        internal static string PersonalLocator(string url)
        {
            if (!ValidLink(url)) return null;
            var uri = new Uri(url); string host = uri.Host.ToLowerInvariant();
            if ((host == "real-debrid.com" || host == "www.real-debrid.com") && uri.AbsolutePath.StartsWith("/d/",StringComparison.OrdinalIgnoreCase))
                return "rd-link/" + Uri.EscapeDataString(url);
            if ((host == "alldebrid.com" || host == "www.alldebrid.com") && uri.AbsolutePath.StartsWith("/f/",StringComparison.OrdinalIgnoreCase))
                return "ad-link/" + Uri.EscapeDataString(url);
            if (host == "api.torbox.app" && uri.Scheme == "https" && uri.IsDefaultPort)
            {
                string kind = uri.AbsolutePath == "/v1/api/torrents/requestdl" ? "torrents" :
                    uri.AbsolutePath == "/v1/api/webdl/requestdl" ? "webdl" : "";
                if (kind.Length == 0) return null;
                var query = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                foreach (string part in uri.Query.TrimStart('?').Split('&'))
                {
                    int split = part.IndexOf('='); if (split < 1) continue;
                    string key = Uri.UnescapeDataString(part.Substring(0,split));
                    if (key != "torrent_id" && key != "web_id" && key != "file_id" && key != "zip_link") continue;
                    if (query.ContainsKey(key)) return null;
                    query.Add(key,Uri.UnescapeDataString(part.Substring(split+1)));
                }
                string job, file, zip;
                if (!query.TryGetValue(kind == "torrents" ? "torrent_id" : "web_id",out job) ||
                    !query.TryGetValue("file_id",out file) || !NumericId(job) || !NumericId(file) ||
                    (query.TryGetValue("zip_link",out zip) && zip != "false" && zip != "0")) return null;
                // Persist account item IDs, never a pasted API key or temporary CDN grant.
                return "tb-file/" + kind + "/" + job + "/" + file;
            }
            return null;
        }
        static bool NumericId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 20) return false;
            foreach (char c in id) if (c < '0' || c > '9') return false;
            return true;
        }
        internal static string ImportLocator(string url)
        {
            string locator = PersonalLocator(url);
            if (locator != null) return locator;
            if (ValidLink(url))
            {
                var uri = new Uri(url);
                if (uri.Host.Equals("api.torbox.app",StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.Equals("torbox.app",StringComparison.OrdinalIgnoreCase) ||
                    uri.Host.Equals("www.torbox.app",StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Use a single-file TorBox download link, or Downloads > Browse stored debrid files > TorBox torrents.");
            }
            return null;
        }
        static void AddAllDebridFiles(List<CloudFile> output, object entries, string prefix, int depth)
        {
            if (depth > 16 || output.Count >= 2000) return;
            foreach (var entry in Rows(entries))
            {
                if (output.Count >= 2000) break;
                var file = entry as Dictionary<string,object>;
                string name = prefix + Text(file,"n"), link = Text(file,"l");
                if (ValidLink(link)) output.Add(new CloudFile { Name=name, Locator="ad-link/"+Uri.EscapeDataString(link), Size=Bytes(file,"s"), Detail=FileDetail("AllDebrid",Bytes(file,"s")) });
                else AddAllDebridFiles(output,Value(file,"e"),name+"/",depth+1);
            }
        }
        public static List<CloudFile> List(AppSettings cfg,string folder,int page)
        {
            var output=new List<CloudFile>();
            string[] parts=folder.Split('/'); string type=parts[0]; page=Math.Max(0,page);
            if(type=="rd-downloads")
            {
                foreach(var o in Rows(Get(Rd+"/downloads?limit=50&page="+(page+1),cfg.RealDebridToken)))
                {
                    var r=o as Dictionary<string,object>;string link=Text(r,"link");string route="rd-link/";
                    if(!ValidLink(link)){link=Text(r,"download");route="rd-direct/";}
                    if(ValidLink(link)) output.Add(new CloudFile{Name=Text(r,"filename"), Locator=route+Uri.EscapeDataString(link),Size=Bytes(r,"filesize"),Detail=FileDetail("Real-Debrid",Bytes(r,"filesize"))});
                }
            }
            else if(type=="rd-torrents")
            {
                foreach(var o in Rows(Get(Rd+"/torrents?limit=50&page="+(page+1),cfg.RealDebridToken)))
                {var r=o as Dictionary<string,object>;bool ready=Text(r,"status")=="downloaded";output.Add(new CloudFile{Name=Text(r,"filename"),Locator="rd-torrent/"+Id(Text(r,"id")),Detail=ready?"Ready in Real-Debrid":"Not ready: "+Text(r,"status"),Folder=true,Ready=ready});}
            }
            else if(type=="rd-torrent"&&parts.Length==2)
            {
                var r=Get(Rd+"/torrents/info/"+Id(parts[1]),cfg.RealDebridToken) as Dictionary<string,object>;
                if(Text(r,"status")!="downloaded")throw new IOException("This torrent is still preparing in Real-Debrid");
                var selected = new List<Dictionary<string,object>>();
                foreach (var file in Rows(Value(r,"files"))) {
                    var f = file as Dictionary<string,object>;
                    if (Text(f,"selected") == "1" || True(f,"selected")) selected.Add(f);
                }
                var links = new List<object>(Rows(Value(r,"links")));
                for (int n=0;n<links.Count;n++) {
                    string link=Convert.ToString(links[n]); if(!ValidLink(link))continue;
                    var f=selected.Count==links.Count?selected[n]:null;
                    string name=Text(f,"path").TrimStart('/');
                    if(string.IsNullOrEmpty(name))name=Text(r,"filename")+(links.Count>1?" - file "+(n+1):"");
                    output.Add(new CloudFile{Name=name,Locator="rd-link/"+Uri.EscapeDataString(link),Size=Bytes(f,"bytes"),Detail=FileDetail("Real-Debrid",Bytes(f,"bytes"))});
                }
            }
            else if(type=="ad-torrents")
            {
                var response=Get(Ad+"/v4.1/magnet/status",cfg.AllDebridApiKey,"") as Dictionary<string,object>;
                var data=Value(response,"data") as Dictionary<string,object>; int index=0;
                foreach(var o in Rows(Value(data,"magnets"))) {
                    if(index++<page*50)continue; if(output.Count>=50)break;
                    var r=o as Dictionary<string,object>;bool ready=Text(r,"statusCode")=="4";
                    output.Add(new CloudFile{Name=Text(r,"filename"),Locator="ad-torrent/"+Id(Text(r,"id")),Folder=true,Ready=ready,Detail=ready?"Ready in AllDebrid":"Not ready: "+Text(r,"status")});
                }
            }
            else if(type=="ad-torrent"&&parts.Length==2)
            {
                var response=Get(Ad+"/v4/magnet/files",cfg.AllDebridApiKey,"id%5B%5D="+Id(parts[1])) as Dictionary<string,object>;
                var data=Value(response,"data") as Dictionary<string,object>;
                foreach(var o in Rows(Value(data,"magnets"))) {
                    var r=o as Dictionary<string,object>;
                    if(Text(r,"id")==parts[1])AddAllDebridFiles(output,Value(r,"files"),"",0);
                }
            }
            else if(type=="tb-torrents"||type=="tb-webdl")
            {
                string api=type=="tb-torrents"?"torrents":"webdl";
                var response=Get(Tb+"/"+api+"/mylist?limit=50&offset="+(page*50),cfg.TorBoxApiKey) as Dictionary<string,object>;
                foreach(var o in Rows(Value(response,"data")))
                {
                    var r=o as Dictionary<string,object>;string job=Id(Text(r,"id"));
                    output.Add(new CloudFile{Name=Text(r,"name"),Locator="tb-folder/"+api+"/"+job,Detail=TorBoxReason(r),Folder=true,Ready=TorBoxReady(r)});
                }
            }
            else if(type=="tb-folder"&&parts.Length==3&&(parts[1]=="torrents"||parts[1]=="webdl"))
            {
                var response=Get(Tb+"/"+parts[1]+"/mylist?id="+Id(parts[2]),cfg.TorBoxApiKey) as Dictionary<string,object>;
                object data=Value(response,"data");var jobs=new List<object>();if(data is Dictionary<string,object>)jobs.Add(data);else foreach(var o in Rows(data))jobs.Add(o);
                foreach(var o in jobs){var r=o as Dictionary<string,object>;
                    if (Text(r,"id") != parts[2]) continue;
                    foreach(var f in Rows(Value(r,"files"))){var file=f as Dictionary<string,object>;
                        bool safe=!True(file,"infected");
                        output.Add(new CloudFile{Name=Text(file,"name"),Size=Bytes(file,"size"),Locator="tb-file/"+parts[1]+"/"+Id(parts[2])+"/"+Id(Text(file,"id")),Detail=safe?TorBoxReason(r):"Unavailable: provider flagged this file",Ready=safe&&TorBoxReady(r)});}}
            }
            else throw new IOException("Unknown cloud folder");
            return output;
        }
        public static string Resolve(AppSettings cfg,string locator,Action<string> progress=null,Func<bool> cancel=null)
        {
            if(cancel!=null&&cancel())throw new OperationCanceledException();
            locator=PersonalLocator(locator)??locator;
            if(locator.StartsWith("ad-link/",StringComparison.Ordinal))
            {string link=Uri.UnescapeDataString(locator.Substring(8));if(!ValidLink(link))throw new IOException("Invalid cloud link");return AllDebridClient.Unrestrict(cfg.AllDebridApiKey,link,progress,cancel);}
            if(locator.StartsWith("rd-direct/",StringComparison.Ordinal))
            {string link=Uri.UnescapeDataString(locator.Substring(10));if(!ValidLink(link))throw new IOException("Invalid cloud link");return link;}
            if(locator.StartsWith("rd-link/",StringComparison.Ordinal))
            {string link=Uri.UnescapeDataString(locator.Substring(8));if(!ValidLink(link))throw new IOException("Invalid cloud link");return RealDebridClient.Unrestrict(cfg.RealDebridToken,link,cfg.RealDebridLocation);}
            string[] p=locator.Split('/');
            if(p.Length!=4||p[0]!="tb-file"||(p[1]!="torrents"&&p[1]!="webdl"))throw new IOException("Invalid cloud file");
            if(string.IsNullOrWhiteSpace(cfg.TorBoxApiKey))throw new IOException("Connect TorBox in Connections first");
            if(!NumericId(p[2])||!NumericId(p[3]))throw new IOException("Invalid TorBox file ID");
            string key=p[1]=="torrents"?"torrent_id":"web_id";
            var response=Get(Tb+"/"+p[1]+"/requestdl?token="+Uri.EscapeDataString(cfg.TorBoxApiKey.Trim())+"&"+key+"="+Id(p[2])+"&file_id="+Id(p[3])+"&zip_link=false",cfg.TorBoxApiKey);
            string url=response as string ?? Text(response as Dictionary<string,object>,"data");
            if(!ValidLink(url))throw new IOException("Cloud file is not ready to download; retry after it finishes preparing");
            if(cancel!=null&&cancel())throw new OperationCanceledException();
            DownloadTransferSettings.RememberProviderLimit(url,4);return url;
        }
        internal static string OwningProvider(string locator)
        {
            locator=PersonalLocator(locator)??locator??"";
            if(locator.StartsWith("ad-link/",StringComparison.Ordinal))return UnlockProviders.AllDebridId;
            if(locator.StartsWith("rd-link/",StringComparison.Ordinal))return UnlockProviders.RealDebridId;
            if(locator.StartsWith("tb-file/",StringComparison.Ordinal))return UnlockProviders.TorBoxId;
            return "";
        }
        internal static bool CanRenew(AppSettings cfg,string locator)
        {
            string provider=OwningProvider(locator);
            // The user explicitly chose this account file. Renewal must use its
            // owner even when another service is preferred for source links.
            return cfg!=null&&!string.IsNullOrEmpty(provider)&&
                !string.IsNullOrWhiteSpace(UnlockProviders.ApiKey(cfg,provider));
        }
    }
}
