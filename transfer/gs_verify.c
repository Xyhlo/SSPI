#include "gs_internal.h"
void gs_digest(const void *data,size_t size,unsigned char output[32]) {SHA256_CTX c;sha256_init(&c);sha256_update(&c,data,size);sha256_final(&c,output);}
int gs_unhex(const char *text,unsigned char output[32])
{
    if(!text || strlen(text)!=64)return -1;
    for(int i=0;i<32;i++){unsigned v=0;for(int j=0;j<2;j++){char c=text[i*2+j];int d=c>='0'&&c<='9'?c-'0':c>='a'&&c<='f'?c-'a'+10:c>='A'&&c<='F'?c-'A'+10:-1;if(d<0)return -1;v=v*16+(unsigned)d;}output[i]=(unsigned char)v;}return 0;
}
static uint64_t be64(const unsigned char *p) {uint64_t n=0;for(int i=0;i<8;i++)n=n*256+p[i];return n;}
static int invalid(GsJobState *j,int code,const char *stage,const unsigned char *expected,const unsigned char *actual)
{
    char a[17]="none",b[17]="none";
    if(expected)for(int i=0;i<8;i++)snprintf(a+i*2,3,"%02x",expected[i]);
    if(actual)for(int i=0;i<8;i++)snprintf(b+i*2,3,"%02x",actual[i]);
    snprintf(j->verification_error,sizeof(j->verification_error),"Verification: %s; size=%lld expected_sha=%s actual_sha=%s",stage,(long long)j->status.total,a,b);
    return code;
}
int gs_verify_header(const unsigned char *h,size_t length,int64_t total,const char *title,const char *content)
{
    if(length<0x1000)return -1;
    if(memcmp(h,"\177CNT",4))return (title&&*title)||(content&&*content) ? -1:0;
    if(be64(h+0x430)!=(uint64_t)total)return -1;
    char id[49];memcpy(id,h+0x40,48);id[48]=0;
    if(strlen(id)<16 || id[6]!='-' || id[16]!='_')return -1;
    if(title&&*title && (strlen(title)!=9||strncmp(id+7,title,9)))return -1;
    if(content&&*content && strcmp(content,id))return -1;
    return 1;
}
int gs_verify_file(GsJobState *job,unsigned char *buffer)
{
    SHA256_CTX hash,chunk_hash;unsigned char digest[32],header[0x1000];sha256_init(&hash);sha256_init(&chunk_hash);
    if(gs_store_read(job->fd,header,sizeof(header),0)||gs_verify_header(header,sizeof(header),job->status.total,job->title,job->content)<0)return invalid(job,-27,"PKG header failed during audit",NULL,NULL);
    gs_digest(header,sizeof(header),digest);if(memcmp(digest,job->header.identity,32))return invalid(job,-28,"header identity changed",job->header.identity,digest);
    for(uint64_t at=0;at<(uint64_t)job->status.total;) {
        if(__atomic_load_n(&job->stop,__ATOMIC_ACQUIRE))return -3;
        size_t n=(uint64_t)job->status.total-at;if(n>GS_XFER_BUFFER)n=GS_XFER_BUFFER;
        if(gs_store_read(job->fd,buffer,n,at))return invalid(job,-29,"file read failed during SHA-256 audit",NULL,NULL);
        if(job->expected[0])sha256_update(&hash,buffer,n);
        if(!job->single && !job->expected[0])sha256_update(&chunk_hash,buffer,n);
        at+=n;
        __atomic_store_n(&job->validation_done,at,__ATOMIC_RELEASE);
        if(!job->single && !job->expected[0] && (at%GS_XFER_CHUNK==0 || at==(uint64_t)job->status.total)) {
            uint32_t index=(uint32_t)((at-1)/GS_XFER_CHUNK);
            sha256_final(&chunk_hash,digest);
            if(!job->chunks[index].done || memcmp(digest,job->chunks[index].hash,32))return invalid(job,-2,"chunk SHA-256 mismatch",job->chunks[index].hash,digest);
            sha256_init(&chunk_hash);
        }
    }
    sha256_final(&hash,digest);
    if(job->expected[0] && memcmp(digest,job->header.expected,32))return invalid(job,-2,"SHA-256 mismatch",job->header.expected,digest);
    return 0;
}

static int checksum_receipt(GsJobState *job, int write_receipt)
{
    static char recent[GS_XFER_JOBS * 2][320];static unsigned next;static volatile int receipt_gate;
    char path[1200], temporary[1220], record[320], stored[320];
    char identity[65], stamp[65];unsigned char raw_stamp[32]={0}, digest[32];
    if(write_receipt==1 && fsync(job->fd))return -1;
#ifdef _WIN32
    BY_HANDLE_FILE_INFORMATION info;
    if(!GetFileInformationByHandle((HANDLE)_get_osfhandle(job->fd),&info))return write_receipt?-1:0;
    memcpy(raw_stamp,&info.dwVolumeSerialNumber,4);
    memcpy(raw_stamp+4,&info.nFileIndexHigh,4);memcpy(raw_stamp+8,&info.nFileIndexLow,4);
    memcpy(raw_stamp+12,&info.ftLastWriteTime,8);
    memcpy(raw_stamp+20,&info.nFileSizeHigh,4);memcpy(raw_stamp+24,&info.nFileSizeLow,4);
#elif defined(GS_HOST_TEST)
    struct stat info;if(fstat(job->fd,&info))return write_receipt?-1:0;
    memcpy(raw_stamp,&info.st_ino,8);memcpy(raw_stamp+8,&info.st_mtim,16);memcpy(raw_stamp+24,&info.st_size,8);
#else
    /* PS4 kernel stat ABI, independent of the bundled libc's struct stat.
     * Bind mtime (not rename-changing ctime), file identity and exact size. */
    unsigned char info[256] __attribute__((aligned(8)))={0};int64_t size=0;
    if(sceKernelFstat(job->fd,(OrbisKernelStat *)info))return write_receipt?-1:0;
    memcpy(&size,info+72,8);if(size!=job->status.total)return write_receipt?-1:0;
    memcpy(raw_stamp,info,8);memcpy(raw_stamp+8,info+40,16);memcpy(raw_stamp+24,info+72,8);
#endif
    gs_digest(raw_stamp,sizeof(raw_stamp),digest);
    for(int i=0;i<32;i++)snprintf(stamp+i*2,3,"%02x",digest[i]);
    for(int i=0;i<32;i++)snprintf(identity+i*2,3,"%02x",job->header.identity[i]);
    snprintf(path,sizeof(path),"%s.sha256-ok",job->dest);
    snprintf(record,sizeof(record),"2 %lld %s %s %s\n",(long long)job->status.total,identity,job->expected,stamp);
    if(!write_receipt) {
        int cached=0;gs_lock(&receipt_gate);
        for(unsigned i=0;i<GS_XFER_JOBS*2;i++)if(!strcmp(recent[i],record))cached=1;
        gs_unlock(&receipt_gate);if(cached)return 1;
        FILE *file=fopen(path,"rb");if(!file)return 0;
        size_t n=fread(stored,1,sizeof(stored)-1,file);stored[n]=0;
        int valid=!ferror(file)&&!strcmp(stored,record);fclose(file);return valid;
    }
    gs_lock(&receipt_gate);snprintf(recent[next++%(GS_XFER_JOBS*2)],sizeof(recent[0]),"%s",record);gs_unlock(&receipt_gate);
    /* A failed sidecar write need not reject a verified package. This process
     * retains the same file-stamp proof; a future process verifies it locally. */
    snprintf(temporary,sizeof(temporary),"%s.tmp",path);
    FILE *file=fopen(temporary,"wb");if(!file)return 0;
    int rc=fputs(record,file)<0||fflush(file)||fsync(fileno(file));
    if(fclose(file))rc=1;
    if(!rc) {
#ifdef _WIN32
        if(!MoveFileExA(temporary,path,MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH))rc=1;
#else
        if(rename(temporary,path))rc=1;
#endif
    }
    if(rc)unlink(temporary);return 0;
}

int sspi_xfer_verify_local(const char *path,const char *destination,int64_t total,
    const char *title,const char *content,const char *sha,char *error,size_t error_size)
{
    if(error&&error_size)error[0]=0;
    if(!sha||!*sha)return 0;
    GsJobState job;memset(&job,0,sizeof(job));job.fd=-1;job.status.total=total;
    snprintf(job.dest,sizeof(job.dest),"%s",destination);
    snprintf(job.title,sizeof(job.title),"%s",title?title:"");
    snprintf(job.content,sizeof(job.content),"%s",content?content:"");
    snprintf(job.expected,sizeof(job.expected),"%s",sha);
    unsigned char header[0x1000];int64_t size=0;int rc=-1;
    int flags=O_RDONLY;
#ifdef _WIN32
    flags|=_O_BINARY;
#else
    flags|=O_NOFOLLOW;
#endif
    job.fd=open(path,flags);
    if(job.fd<0||gs_store_size(job.fd,&size)||size!=total||gs_unhex(sha,job.header.expected)||
        gs_store_read(job.fd,header,sizeof(header),0))goto done;
    gs_digest(header,sizeof(header),job.header.identity);
    if(checksum_receipt(&job,0)){rc=0;goto done;}
    unsigned char *buffer=malloc(GS_XFER_BUFFER);if(!buffer)goto done;
    rc=gs_verify_file(&job,buffer);free(buffer);
    if(!rc)rc=checksum_receipt(&job,2);
done:
    if(job.fd>=0)close(job.fd);
    if(rc&&error&&error_size)snprintf(error,error_size,"%s",job.verification_error[0]?job.verification_error:"Publisher checksum could not be verified or saved; complete file retained");
    return rc;
}

int gs_finalize_file(GsJobState *job,unsigned char *buffer)
{
    int64_t file_size=0;unsigned char header[0x1000],digest[32];job->verification_error[0]=0;
    if(__atomic_load_n(&job->stop,__ATOMIC_ACQUIRE))return -3;
    if(job->status.total<=0 || job->status.done!=job->status.total || job->active)
        return invalid(job,-21,"incomplete transfer or active writers",NULL,NULL);
    if(gs_store_size(job->fd,&file_size))return invalid(job,-22,"kernel file-size query failed",NULL,NULL);
    if(file_size!=job->status.total){char stage[80];snprintf(stage,sizeof(stage),"file size mismatch actual=%lld",(long long)file_size);return invalid(job,-23,stage,NULL,NULL);}
    if(!job->chunks || !job->claims || job->header.count!=((uint64_t)job->status.total+GS_XFER_CHUNK-1)/GS_XFER_CHUNK)
        return invalid(job,-24,"invalid completion map",NULL,NULL);
    for(uint32_t i=0;i<job->header.count;i++)if(!job->chunks[i].done || job->claims[i])return invalid(job,-25,"uncommitted or claimed chunk",NULL,NULL);
    if(gs_store_read(job->fd,header,sizeof(header),0))return invalid(job,-26,"header read failed",NULL,NULL);
    if(gs_verify_header(header,sizeof(header),job->status.total,job->title,job->content)<0)return invalid(job,-27,"PKG size/title/content mismatch",NULL,NULL);
    gs_digest(header,sizeof(header),digest);
    if(memcmp(digest,job->header.identity,32))return invalid(job,-28,"header identity changed",job->header.identity,digest);
    // Fresh chunks were hashed during transfer; resumed chunks were reread by
    // gs_store_open. Rehashing our own digests adds a full disk pass without an
    // independent source checksum. BGFT still validates the package on install.
    // A publisher-supplied whole-file SHA-256 always gets its required pass.
    if(!job->expected[0])return 0;
    int rc=gs_verify_file(job,buffer);
    if(!rc && checksum_receipt(job,1))return invalid(job,-30,"publisher checksum receipt could not be saved",NULL,NULL);
    return rc;
}
