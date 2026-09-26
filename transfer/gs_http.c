#include "gs_internal.h"
#include <limits.h>
static int context_id=-1;
static int context_module=-1;
void gs_http_set_module(int module) {context_module=module;}
#if !defined(GS_HOST_TEST) || defined(GS_HTTP_TEST)
static int (*http_template)(int,const char*,int,int), (*http_connection)(int,const char*,int);
static int (*http_request)(int,int,const char*,uint64_t), (*http_header)(int,const char*,const char*,int);
static int (*http_send)(int,const void*,size_t), (*http_status)(int,int*), (*http_headers)(int,char**,size_t*);
static int (*http_read)(int,void*,uint32_t), (*http_abort)(int), (*http_delete_request)(int);
static int (*http_delete_connection)(int), (*http_delete_template)(int), (*http_redirect)(int,int);
static int (*http_connect_timeout)(int,uint32_t), (*http_recv_timeout)(int,uint32_t), (*http_send_timeout)(int,uint32_t);
static int (*http_ssl_error)(int,int*,unsigned*), (*http_errno)(int,int*);
static int (*http_recv_block)(int,uint32_t);
#define GS_HTTP_OPEN_PERMITS 2
#define GS_HTTP_OPEN_WAIT_MS 60000U
static int pending_opens;
int gs_http_init(int context)
{
#ifdef GS_HTTP_TEST
    context_id=context;return 0;
#else
    if(context<0) return -1;
    if(context_id>=0) return context_id==context ? 0 : -1;
    // SceShellUI's module list omits loaded system libraries, so a host that
    // already resolved libSceHttp passes its handle before initialization.
    int module=context_module;
    if(module<0) {
        OrbisKernelModule modules[256]; size_t count=0;
        if(sceKernelGetModuleList(modules,256,&count)) return -1;
        for(size_t i=0;i<count && i<256;i++) {
            void *symbol=NULL;
            if(!sceKernelDlsym(modules[i],"sceHttpCreateTemplate",&symbol) && symbol) {module=modules[i];break;}
        }
    }
    if(module<0) return module;
#define BIND(name,field) if(sceKernelDlsym(module,name,(void**)&field) || !field) return -2
    BIND("sceHttpCreateTemplate",http_template); BIND("sceHttpCreateConnectionWithURL",http_connection);
    BIND("sceHttpCreateRequestWithURL",http_request); BIND("sceHttpAddRequestHeader",http_header);
    BIND("sceHttpSendRequest",http_send); BIND("sceHttpGetStatusCode",http_status);
    BIND("sceHttpGetAllResponseHeaders",http_headers); BIND("sceHttpReadData",http_read);
    BIND("sceHttpAbortRequest",http_abort); BIND("sceHttpDeleteRequest",http_delete_request);
    BIND("sceHttpDeleteConnection",http_delete_connection); BIND("sceHttpDeleteTemplate",http_delete_template);
    BIND("sceHttpSetAutoRedirect",http_redirect); BIND("sceHttpSetConnectTimeOut",http_connect_timeout);
    BIND("sceHttpSetRecvTimeOut",http_recv_timeout); BIND("sceHttpSetSendTimeOut",http_send_timeout);
#undef BIND
    // Optional diagnostics must not prevent older firmware from loading.
    if(sceKernelDlsym(module,"sceHttpsGetSslError",(void**)&http_ssl_error))http_ssl_error=NULL;
    if(sceKernelDlsym(module,"sceHttpGetLastErrno",(void**)&http_errno))http_errno=NULL;
    if(sceKernelDlsym(module,"sceHttpSetRecvBlockSize",(void**)&http_recv_block))http_recv_block=NULL;
    context_id=context; return 0;
#endif
}
#endif
int gs_http_origin(const char *url,char *output,size_t capacity)
{
    const char *at, *end; size_t n;
    if(!url || (strncasecmp(url,"http://",7) && strncasecmp(url,"https://",8))) return -1;
    at=strstr(url,"://")+3; end=at+strcspn(at,"/?#");
    if(end==at || memchr(at,'@',(size_t)(end-at))) return -1;
    for(const char *p=url;*p;p++) if((unsigned char)*p<=32 || *p=='\\') return -1;
    n=(size_t)(end-url); if(n+1>capacity) return -1;
    for(size_t i=0;i<n;i++) output[i]=(url[i]>='A' && url[i]<='Z') ? url[i]+32 : url[i];
    output[n]=0;return 0;
}
static int number(const char **p,int64_t *value)
{
    uint64_t v=0; const char *s=*p;
    if(*s<'0'||*s>'9') return -1;
    while(*s>='0'&&*s<='9') {unsigned d=(unsigned)(*s++-'0');if(v>((uint64_t)INT64_MAX-d)/10) return -1;v=v*10+d;}
    *p=s;*value=(int64_t)v;return 0;
}
int gs_range_parse(const char *p,int64_t *start,int64_t *end,int64_t *total)
{
    if(strncmp(p,"bytes ",6)) return -1;p+=6;
    if(number(&p,start)||*p++!='-'||number(&p,end)||*p++!='/'||number(&p,total)) return -1;
    while(*p==' '||*p=='\t') p++;
    return *p || *start<0 || *end<*start || *total<=*end ? -1:0;
}
static int64_t http_days_from_civil(int year,unsigned month,unsigned day)
{
    year-=month<=2;int era=(year>=0?year:year-399)/400;
    unsigned yoe=(unsigned)(year-era*400),mp=month>2?month-3:month+9;
    unsigned doy=(153*mp+2)/5+day-1,doe=yoe*365+yoe/4-yoe/100+doy;
    return (int64_t)era*146097+(int64_t)doe-719468;
}
static int digits(const char *value,size_t at,size_t count,int *out)
{
    int result=0;for(size_t i=0;i<count;i++){char c=value[at+i];if(c<'0'||c>'9')return -1;result=result*10+c-'0';}
    *out=result;return 0;
}
static int parse_http_date(const char *value,int64_t *epoch)
{
    static const char *weekdays[]={"Sun","Mon","Tue","Wed","Thu","Fri","Sat"};
    static const char *months[]={"Jan","Feb","Mar","Apr","May","Jun","Jul","Aug","Sep","Oct","Nov","Dec"};
    if(!value||strlen(value)!=29||value[3]!=','||value[4]!=' '||value[7]!=' '||value[11]!=' '||
       value[16]!=' '||value[19]!=':'||value[22]!=':'||value[25]!=' '||memcmp(value+26,"GMT",3))return -1;
    int weekday=-1,month=0,day,year,hour,minute,second;
    for(int i=0;i<7;i++)if(!memcmp(value,weekdays[i],3)){weekday=i;break;}
    for(int i=0;i<12;i++)if(!memcmp(value+8,months[i],3)){month=i+1;break;}
    if(weekday<0||!month||digits(value,5,2,&day)||digits(value,12,4,&year)||
       digits(value,17,2,&hour)||digits(value,20,2,&minute)||digits(value,23,2,&second)||
       year<1601||hour>23||minute>59||second>59)return -1;
    static const unsigned month_days[]={31,28,31,30,31,30,31,31,30,31,30,31};
    unsigned max=month_days[month-1];
    if(month==2&&(year%4==0&&(year%100!=0||year%400==0)))max++;
    if(day<1||(unsigned)day>max)return -1;
    int64_t days=http_days_from_civil(year,(unsigned)month,(unsigned)day);
    int actual=(int)((days+4)%7);if(actual<0)actual+=7;if(actual!=weekday)return -1;
    *epoch=days*86400+(int64_t)hour*3600+minute*60+second;return 0;
}
static int header_once(const char *block,size_t length,const char *name,char *out,size_t capacity)
{
    int found=0,previous_target=0;size_t n=strlen(name),i=0;if(!block||!name||!out||!capacity)return -1;out[0]=0;
    while(i<length) {
        size_t begin=i;while(i<length&&block[i]!='\r'&&block[i]!='\n'&&block[i])i++;
        size_t end=i;if(i<length&&block[i]){if(block[i]=='\r')i++;if(i<length&&block[i]=='\n')i++;}else if(i<length)i++;
        if(end==begin){previous_target=0;continue;}
        if(block[begin]==' '||block[begin]=='\t') {
            if(previous_target)return -1;
            previous_target=0;continue;
        }
        int matches=end-begin>n&&!strncasecmp(block+begin,name,n)&&block[begin+n]==':';
        previous_target=matches;
        if(matches) {
            if(found)return -1;
            begin+=n+1;while(begin<end&&(block[begin]==' '||block[begin]=='\t'))begin++;
            while(end>begin&&(block[end-1]==' '||block[end-1]=='\t'))end--;
            size_t size=end-begin;if(!size||size>=capacity)return -1;
            for(size_t at=begin;at<end;at++)if((unsigned char)block[at]<0x20||(unsigned char)block[at]==0x7f)return -1;
            memcpy(out,block+begin,size);out[size]=0;found=1;
        }
    }
    return found;
}
int gs_http_parse_response_validators(GsHttp *h,const char *headers,size_t length)
{
    if(!h||(!headers&&length))return -1;
    h->etag[0]=h->etag_value[0]=h->date[0]=h->last_modified[0]=0;
    h->etag_present=h->etag_single=h->date_present=h->date_valid=h->last_modified_present=h->last_modified_single=h->last_modified_valid=0;
    h->date_epoch=h->last_modified_epoch=0;
    char value[512];int got=header_once(headers,length,"ETag",value,sizeof(value));
    h->etag_present=got!=0;h->etag_single=got==1;
    if(got==1){snprintf(h->etag_value,sizeof(h->etag_value),"%s",value);if(gs_http_strong_etag(value))snprintf(h->etag,sizeof(h->etag),"%s",value);}
    got=header_once(headers,length,"Date",value,sizeof(value));
    h->date_present=got!=0;
    if(got==1&&!parse_http_date(value,&h->date_epoch)){snprintf(h->date,sizeof(h->date),"%s",value);h->date_valid=1;}
    got=header_once(headers,length,"Last-Modified",value,sizeof(value));
    h->last_modified_present=got!=0;h->last_modified_single=got==1;
    if(got==1&&!parse_http_date(value,&h->last_modified_epoch)){snprintf(h->last_modified,sizeof(h->last_modified),"%s",value);h->last_modified_valid=1;}
    return 0;
}
int gs_http_range_validator(const GsHttp *h,char *output,size_t capacity)
{
    if(!h||!output||!capacity)return 0;output[0]=0;
    if(h->etag_present)return h->etag_single&&h->etag[0]&&strlen(h->etag)<capacity?(snprintf(output,capacity,"%s",h->etag),1):0;
    if(h->date_valid&&h->last_modified_valid&&h->date_epoch>=h->last_modified_epoch&&
       h->date_epoch-h->last_modified_epoch>=60&&strlen(h->last_modified)<capacity) {
        snprintf(output,capacity,"%s",h->last_modified);return 2;
    }
    return 0;
}
#ifdef GS_HOST_TEST
__declspec(dllexport) int gs_test_range_validator(const char *headers,char *output,unsigned capacity)
{
    GsHttp h;memset(&h,0,sizeof(h));if(!headers||gs_http_parse_response_validators(&h,headers,strlen(headers)))return -1;
    return gs_http_range_validator(&h,output,capacity);
}
#endif
#if !defined(GS_HOST_TEST) || defined(GS_HTTP_TEST)
void gs_http_reset(GsHttp *h) {memset(h,0,sizeof(*h));h->template_id=h->connection=h->request=-1;}
static void request_lock(GsHttp *h) {while(__sync_lock_test_and_set(&h->request_gate,1))gs_sleep(1);}
static void request_unlock(GsHttp *h) {__sync_lock_release(&h->request_gate);}
void gs_http_clear_abort(GsHttp *h)
{
    request_lock(h);__atomic_store_n(&h->aborted,0,__ATOMIC_RELEASE);request_unlock(h);
}
void gs_http_abort(GsHttp *h)
{
    // Pin the native ID through abort: deleting it first can let libSceHttp
    // recycle the ID while cancellation is still using it. Send/read stay
    // outside this lock so abort can interrupt their blocking calls.
    request_lock(h);
    __atomic_store_n(&h->aborted,1,__ATOMIC_RELEASE);
    int r=__atomic_load_n(&h->request,__ATOMIC_ACQUIRE);if(r>=0)http_abort(r);
    request_unlock(h);
}
static int64_t days_from_civil(int year,unsigned month,unsigned day)
{
    year-=month<=2;int era=(year>=0?year:year-399)/400;
    unsigned yoe=(unsigned)(year-era*400);
    unsigned mp=month>2?month-3:month+9;
    unsigned doy=(153*mp+2)/5+day-1;
    unsigned doe=yoe*365+yoe/4-yoe/100+doy;
    return (int64_t)era*146097+(int64_t)doe-719468;
}
static int parse_retry_after(const char *value,int *invalid_time)
{
    const char *at=value;int64_t seconds=0;
    *invalid_time=0;
    if(!number(&at,&seconds)&&!*at)return seconds>GS_RETRY_AFTER_MAX_SECONDS?GS_RETRY_AFTER_MAX_SECONDS:(int)seconds;
    char week[4]={0},month_name[4]={0},zone[4]={0};int day,year,hour,minute,second;
    if(sscanf(value,"%3[^,], %d %3s %d %d:%d:%d %3s",week,&day,month_name,&year,&hour,&minute,&second,zone)!=8 ||
       strcmp(zone,"GMT") || day<1 || day>31 || year<1970 || hour<0 || hour>23 || minute<0 || minute>59 || second<0 || second>60)return 0;
    static const char *months[] = {"Jan","Feb","Mar","Apr","May","Jun","Jul","Aug","Sep","Oct","Nov","Dec"};
    unsigned month=0;for(unsigned i=0;i<12;i++)if(!strcmp(month_name,months[i])){month=i+1;break;}if(!month)return 0;
    int64_t target=days_from_civil(year,month,(unsigned)day)*86400+(int64_t)hour*3600+minute*60+second;
    time_t wall=time(NULL);
    // A console with an unset epoch cannot translate an HTTP date to a
    // monotonic delay. Mark it explicitly and let recovery use its bounded
    // default rather than interpreting decades as a server cooldown.
    if((int64_t)wall<1577836800){*invalid_time=1;return 0;}
    if(target<=(int64_t)wall)return 0;
    int64_t delay=target-(int64_t)wall;return delay>GS_RETRY_AFTER_MAX_SECONDS?GS_RETRY_AFTER_MAX_SECONDS:(int)delay;
}
static void response_close(GsHttp *h)
{
    request_lock(h);
    int r=__atomic_exchange_n(&h->request,-1,__ATOMIC_ACQ_REL);if(r>=0)http_delete_request(r);
    request_unlock(h);
}
void gs_http_close(GsHttp *h)
{
    response_close(h);
    if(h->connection>=0)http_delete_connection(h->connection);
    if(h->template_id>=0)http_delete_template(h->template_id);
    request_lock(h);
    int aborted=__atomic_load_n(&h->aborted,__ATOMIC_ACQUIRE);
    // Preserve the live lock, cancellation state and active-open clock while
    // clearing the connection cache. Redirect/setup cleanup must not turn time
    // queued for an open permit into active network time in the watchdog.
    memset(h,0,offsetof(GsHttp,request_gate));
    h->template_id=h->connection=h->request=-1;h->aborted=aborted;
    request_unlock(h);
}
static int header(const char *block,size_t length,const char *name,char *out,size_t cap)
{
    int found=0;size_t n=strlen(name),i=0;out[0]=0;
    while(i<length) {
        size_t begin=i;while(i<length && block[i]!='\r'&&block[i]!='\n'&&block[i])i++;
        size_t end=i;while(i<length && (block[i]=='\r'||block[i]=='\n'||!block[i]))i++;
        if(end-begin>n && !strncasecmp(block+begin,name,n) && block[begin+n]==':') {
            begin+=n+1;while(begin<end&&(block[begin]==' '||block[begin]=='\t'))begin++;
            while(end>begin&&(block[end-1]==' '||block[end-1]=='\t'))end--;
            size_t size=end-begin;if(size>=cap)return -1;
            if(found && (strlen(out)!=size || memcmp(out,block+begin,size)))return -1;
            memcpy(out,block+begin,size);out[size]=0;found=1;
        }
    }return found;
}
int gs_http_strong_etag(const char *etag)
{
    size_t n=etag?strlen(etag):0;if(n<2||etag[0]!='"'||etag[n-1]!='"')return 0;
    for(size_t i=1;i+1<n;i++) {
        unsigned char c=(unsigned char)etag[i];
        if(c=='"'||c<0x21||c==0x7f)return 0;
    }
    return 1;
}
static int http_open_request(GsHttp *h,const char *url,const char *bearer,int64_t start,int64_t end,const char *if_range)
{
    char current[8192],initial[512],origin[512],value[8192];int rc=-1;
    int64_t if_range_date;
    if(strlen(url)>=sizeof(current)||gs_http_origin(url,initial,sizeof(initial))||
       (if_range&&*if_range&&!gs_http_strong_etag(if_range)&&parse_http_date(if_range,&if_range_date)))return -2;
    int cached=h->effective[0] && strcmp(h->effective,url) && !strcmp(h->source,url);
    snprintf(current,sizeof(current),"%s",cached?h->effective:url);
    for(int hop=0;hop<=8;hop++) {
        if(__atomic_load_n(&h->aborted,__ATOMIC_ACQUIRE))return -1;
        if(h->retire)gs_http_close(h);
        response_close(h);
        h->status=0;h->retry_after=0;h->location[0]=0;
        h->start=h->end=h->total=h->length=-1;h->reused=0;h->etag[0]=0;h->etag_value[0]=0;
        h->etag_present=h->etag_single=h->date_present=h->date_valid=h->last_modified_present=h->last_modified_single=h->last_modified_valid=0;h->stage="origin";
        if(gs_http_origin(current,origin,sizeof(origin)))return -2;
        if(strcmp(origin,h->origin)) {
            gs_http_close(h);
            h->stage="template";
            h->template_id=http_template(context_id,"SSPI/PS4",2,0);
            if(h->template_id<0)return h->template_id;
            // The HTTP library has its own receive buffer, separate from our
            // 1 MiB writer buffers. Tune it before connection creation; keep
            // older firmware usable when this optional setting is unavailable.
            // 1 MiB blocks make libSceHttp hold data until a whole block fills,
            // capping each lane near 1.5 MiB/s; 256 KiB keeps reads streaming.
            // With 25 lanes sharing the HTTP pool, fall back through 128 KiB
            // before the 64 KiB floor when the larger block is refused.
            h->recv_block=0;h->recv_block_rc=0;
            if(http_recv_block) {
                static const uint32_t sizes[]={256*1024,128*1024,64*1024};
                for(unsigned i=0;i<sizeof(sizes)/sizeof(sizes[0]);i++) {
                    h->recv_block_rc=http_recv_block(h->template_id,sizes[i]);
                    if(!h->recv_block_rc){h->recv_block=(int)sizes[i];break;}
                }
            }
            h->stage="timeouts";
            // Backstop only: the transfer watchdog aborts a silent body read after
            // 8 s. A two-minute firmware wait froze lanes and the shared open permit.
            if((rc=http_connect_timeout(h->template_id,10000000))<0 ||
               (rc=http_recv_timeout(h->template_id,30000000))<0 ||
               (rc=http_send_timeout(h->template_id,30000000))<0)return rc;
            h->stage="connect";h->connection=http_connection(h->template_id,current,1);
            if(h->connection<0)return h->connection;
            snprintf(h->origin,sizeof(h->origin),"%s",origin);
        } else h->reused=1;
        h->stage="request";
        int request=http_request(h->connection,0,current,0);
        request_lock(h);__atomic_store_n(&h->request,request,__ATOMIC_RELEASE);request_unlock(h);
        if(request<0)return request;
        h->stage="request-headers";
        if((rc=http_redirect(request,0))<0)return rc;
        if((rc=http_header(request,"Accept-Encoding","identity",0))<0)return rc;
        if(start>=0) {
            snprintf(value,sizeof(value),"bytes=%lld-%lld",(long long)start,(long long)end);
            if((rc=http_header(request,"Range",value,0))<0)return rc;
            if(if_range&&*if_range&&(rc=http_header(request,"If-Range",if_range,0))<0)return rc;
        }
        if(bearer && *bearer && !strcmp(origin,initial)) {
            if(strpbrk(bearer,"\r\n")||strlen(bearer)>2040)return -2;
            snprintf(value,sizeof(value),"Bearer %s",bearer);if((rc=http_header(request,"Authorization",value,0))<0)return rc;
        }
        h->stage="send";
        // An interrupted GET has no request body to replay. Retry the interrupted
        // API call briefly before replacing a healthy connection and its range.
        for(unsigned attempt=0;attempt<8;attempt++) {
            if(__atomic_load_n(&h->aborted,__ATOMIC_ACQUIRE))return -1;
            rc=http_send(request,NULL,0);
            if((unsigned)rc!=0x80410104U)break;
            h->interruptions++;gs_sleep(1);
        }
        if(rc<0)return rc;
        h->stage="status";if((rc=http_status(request,&h->status))<0)return rc;
        char *block=NULL;size_t length=0;
        h->stage="response-headers";
        if((rc=http_headers(request,&block,&length))<0)return rc;
        if(!block||length>65536)return -2;
        int connection_header=header(block,length,"Connection",value,sizeof(value));
        if(connection_header<0)return -2;
        if(connection_header)for(const char *p=value;*p;p++)
            if((p==value||p[-1]==','||p[-1]==' ')&&!strncasecmp(p,"close",5)&&(p[5]==0||p[5]==','||p[5]==' ')){h->retire=1;break;}
        if(header(block,length,"Location",h->location,sizeof(h->location))<0)return -2;
        h->retry_after=0;h->retry_after_invalid_time=0;
        if(header(block,length,"Retry-After",value,sizeof(value))>0)
            h->retry_after=parse_retry_after(value,&h->retry_after_invalid_time);
        if(h->status==301||h->status==302||h->status==303||h->status==307||h->status==308) {
            if(hop==8||!h->location[0])return -2;
            char next[8192];int written;
            if(!strncmp(h->location,"//",2))written=snprintf(next,sizeof(next),"%.*s:%s",(int)(strchr(current,':')-current),current,h->location);
            else if(h->location[0]=='/')written=snprintf(next,sizeof(next),"%s%s",origin,h->location);
            else if(strstr(h->location,"://"))written=snprintf(next,sizeof(next),"%s",h->location);
            else {char *slash=strrchr(current,'/');if(!slash||slash<current+strlen(origin))written=snprintf(next,sizeof(next),"%s/%s",origin,h->location);else written=snprintf(next,sizeof(next),"%.*s/%s",(int)(slash-current),current,h->location);}
            if(written<0 || written>=(int)sizeof(next) || (!strncasecmp(current,"https://",8)&&strncasecmp(next,"https://",8)))return -2;
            snprintf(current,sizeof(current),"%s",next);continue;
        }
        // A cached redirect is scoped to this exact input URL. If that target
        // expires or its replica fails, visit the original URL once for a fresh
        // redirect; never forward its bearer cross-origin.
        if(cached && (h->status==401||h->status==403||h->status==404||h->status==410||h->status==500||h->status==502||h->status==504)) {
            cached=0;snprintf(current,sizeof(current),"%s",url);gs_http_close(h);continue;
        }
        snprintf(h->effective,sizeof(h->effective),"%s",current);
        snprintf(h->source,sizeof(h->source),"%s",url);
        if(gs_http_parse_response_validators(h,block,length))return -2;
        h->start=h->end=h->total=h->length=-1;
        int got=header(block,length,"Content-Range",value,sizeof(value));
        if(got<0 || (got && gs_range_parse(value,&h->start,&h->end,&h->total)))return -2;
        got=header(block,length,"Content-Length",value,sizeof(value));
        if(got<0)return -2;
        if(got){const char *v=value;if(number(&v,&h->length)||*v)return -2;}
        got=header(block,length,"Content-Encoding",value,sizeof(value));
        if(got<0 || (got && *value && strcasecmp(value,"identity")))return -2;
        return 0;
    }return -2;
}
static void failure_details(GsHttp *h)
{
    if(h->request<0)return;
    if(http_ssl_error)http_ssl_error(h->request,&h->ssl_error,&h->ssl_verify);
    if(http_errno)http_errno(h->request,&h->native_errno);
}
int gs_http_open(GsHttp *h,const char *url,const char *bearer,int64_t start,int64_t end,const char *if_range)
{
    __atomic_store_n(&h->open_started,0,__ATOMIC_RELEASE);
    h->ssl_error=h->native_errno=0;h->ssl_verify=h->open_wait_ms=h->interruptions=0;
    h->status=h->retry_after=h->retry_after_invalid_time=h->reused=0;h->stage="request-slot";
    h->etag[0]=0;
    if(__atomic_load_n(&h->aborted,__ATOMIC_ACQUIRE))return -1;
    uint64_t began=gs_clock();
    // Bound TLS/request setup pressure on the shared firmware HTTP heap.
    // Release the permit at headers; the transfer scheduler bounds body streams.
    // Two permits let 25 lanes ramp up without serializing every handshake;
    // a lane queued behind the others waits up to GS_HTTP_OPEN_WAIT_MS.
    for(;;) {
        h->open_wait_ms=(unsigned)(gs_clock()-began);
        if(__atomic_load_n(&h->aborted,__ATOMIC_ACQUIRE))return -1;
        int active=__atomic_load_n(&pending_opens,__ATOMIC_ACQUIRE);
        if(active<GS_HTTP_OPEN_PERMITS && __atomic_compare_exchange_n(&pending_opens,&active,active+1,0,__ATOMIC_ACQ_REL,__ATOMIC_ACQUIRE))break;
        if(h->open_wait_ms>=GS_HTTP_OPEN_WAIT_MS)return -15;
        gs_sleep(5);
    }
    unsigned waited=(unsigned)(gs_clock()-began);
    uint64_t started=gs_clock();__atomic_store_n(&h->open_started,started?started:1,__ATOMIC_RELEASE);
    int rc=http_open_request(h,url,bearer,start,end,if_range);
    h->open_wait_ms=waited;
    if(rc<0){failure_details(h);h->retire=1;}
    __atomic_sub_fetch(&pending_opens,1,__ATOMIC_RELEASE);
    __atomic_store_n(&h->open_started,0,__ATOMIC_RELEASE);
    return rc;
}
int gs_http_read(GsHttp *h,void *data,unsigned length)
{
    int rc=-3;
    for(unsigned attempt=0;attempt<8;attempt++) {
        if(__atomic_load_n(&h->aborted,__ATOMIC_ACQUIRE))return -3;
        rc=http_read(h->request,data,length);
        // SceNet EINTR reports an interrupted call, not a consumed body or a
        // failed TLS handshake. Retry the read without replacing the connection.
        if((unsigned)rc!=0x80410104U)break;
        h->interruptions++;
        gs_sleep(1);
    }
    if(rc<0){failure_details(h);h->retire=1;}
    return rc;
}
#endif
