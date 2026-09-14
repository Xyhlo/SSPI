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
    }

    internal static class CloudCatalog
    {
        const string Rd = "https://api.real-debrid.com/rest/1.0";
        const string Tb = "https://api.torbox.app/v1/api";
        static readonly object Gate = new object();
        static DateTime NextRequest;
        internal static Func<string, string, string> Fetch;
        static object Get(string url, string token)
        {
            if (string.IsNullOrWhiteSpace(token)) throw new IOException("Connect this service in Connections first");
            lock (Gate)
            {
                if (NextRequest > DateTime.UtcNow) System.Threading.Thread.Sleep((int)Math.Min(1000, (NextRequest-DateTime.UtcNow).TotalMilliseconds));
                NextRequest = DateTime.UtcNow.AddMilliseconds(750);
                string json;
                try { json = Fetch != null ? Fetch(url, token) : NetHttp.GetString(url, 20000, null, token.Trim()); }
                catch(Exception ex) {
                    var http = DownloadHttpException.Find(ex);
                    if(http != null) throw new DownloadHttpException(http.StatusCode, http.RetryAfterSeconds.ToString(), "accessing cloud files");
                    throw new IOException("Cloud service temporarily unavailable. Retry shortly; your files are retained.");
                }
                object result = PackageSourceJson.Parse(json);
                var row = result as Dictionary<string, object>;
                if (row != null && (Value(row,"error_code") != null || Text(row,"success") == "False" || Text(row,"success") == "false"))
                    throw new IOException("Service could not list this item; check account status and retry shortly");
                return result;
            }
        }
        static object Value(Dictionary<string, object> row, string name) { object v; return row != null && row.TryGetValue(name,out v) ? v : null; }
        static string Text(Dictionary<string, object> row,string name) { return Convert.ToString(Value(row,name),System.Globalization.CultureInfo.InvariantCulture); }
        static bool True(Dictionary<string,object> row,string name) { return string.Equals(Text(row,name),"true",StringComparison.OrdinalIgnoreCase); }
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
                    if(ValidLink(link)) output.Add(new CloudFile{Name=Text(r,"filename"), Locator=route+Uri.EscapeDataString(link),Detail="Real-Debrid file"});
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
                int n=0;foreach(var link in Rows(Value(r,"links")))if(ValidLink(Convert.ToString(link)))
                    output.Add(new CloudFile{Name=Text(r,"filename")+" - file "+(++n),Locator="rd-link/"+Uri.EscapeDataString(Convert.ToString(link)),Detail="Ready in Real-Debrid"});
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
                    foreach(var f in Rows(Value(r,"files"))){var file=f as Dictionary<string,object>;
                        bool safe=!True(file,"infected");
                        output.Add(new CloudFile{Name=Text(file,"name"),Locator="tb-file/"+parts[1]+"/"+Id(parts[2])+"/"+Id(Text(file,"id")),Detail=safe?TorBoxReason(r):"Unavailable: provider flagged this file",Ready=safe&&TorBoxReady(r)});}}
            }
            else throw new IOException("Unknown cloud folder");
            return output;
        }
        public static string Resolve(AppSettings cfg,string locator)
        {
            if(locator.StartsWith("rd-direct/",StringComparison.Ordinal))
            {string link=Uri.UnescapeDataString(locator.Substring(10));if(!ValidLink(link))throw new IOException("Invalid cloud link");return link;}
            if(locator.StartsWith("rd-link/",StringComparison.Ordinal))
            {string link=Uri.UnescapeDataString(locator.Substring(8));if(!ValidLink(link))throw new IOException("Invalid cloud link");return RealDebridClient.Unrestrict(cfg.RealDebridToken,link,cfg.RealDebridLocation);}
            string[] p=locator.Split('/');
            if(p.Length!=4||p[0]!="tb-file"||(p[1]!="torrents"&&p[1]!="webdl"))throw new IOException("Invalid cloud file");
            string key=p[1]=="torrents"?"torrent_id":"web_id";
            var response=Get(Tb+"/"+p[1]+"/requestdl?token="+Uri.EscapeDataString(cfg.TorBoxApiKey.Trim())+"&"+key+"="+Id(p[2])+"&file_id="+Id(p[3])+"&zip_link=false",cfg.TorBoxApiKey);
            string url=response as string ?? Text(response as Dictionary<string,object>,"data");
            if(!ValidLink(url))throw new IOException("Cloud file is not ready to download; retry after it finishes preparing");
            DownloadTransferSettings.RememberProviderLimit(url,DownloadTransferSettings.MaxRangeCount);return url;
        }
    }
}
