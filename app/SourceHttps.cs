using System;
using System.IO;
namespace Orbis
{
    internal static class SourceHttps
    {
        public const int MaxBytes = 2 * 1024 * 1024;
        internal static Func<DateTime> Clock;
        internal static Func<string,int,string,string,string,string> PlainFetch;
        internal static Func<string,int,string,string,string> ModuleFetch;
        internal static Action ModuleInit;
        internal static Func<bool> ModuleReady;
        internal static Func<string> ModuleDetail;
        public static string GetString(string url,int timeoutMs,string referer,string userAgent,
            Func<bool> cancel = null, Func<Uri, bool> allowOrigin = null)
        {
            if(cancel!=null&&cancel())throw new OperationCanceledException();
            Uri uri;
            if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||!string.IsNullOrEmpty(uri.UserInfo)||
                (uri.Scheme!="https"&&uri.Scheme!="http"))throw new IOException("Unsupported source URL");
            if(allowOrigin!=null&&!allowOrigin(uri))throw new IOException("Source request origin is not permitted");
            if(uri.Scheme=="http")return PlainFetch!=null?PlainFetch(url,timeoutMs,referer,null,userAgent):NetHttp.GetStringDirect(url,timeoutMs,referer,null,userAgent,MaxBytes,cancel,allowOrigin);
            if(ModuleFetch!=null)return ModuleFetch(url,timeoutMs,referer,userAgent);
            return NativeHttp.GetString(url,timeoutMs,referer,null,MaxBytes,userAgent,cancel,allowOrigin);
        }
    }
}
