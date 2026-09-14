#include "gs_internal.h"
static int store_io_failure(int64_t result)
{
#if !defined(GS_HOST_TEST) || defined(GS_STORE_TEST_NATIVE_ERRORS)
    // sceKernel positioned I/O returns 0x8002xxxx directly; unlike libc
    // wrappers, it does not promise to update __error/errno.
    uint32_t code=(uint32_t)result;
    errno=(code&0xffff0000U)==0x80020000U?(int)(code&0xffffU):EIO;
#else
    if(result>=0 || !errno)errno=EIO;
#endif
    return -1;
}
static int storage_failure(GsJobState *j,const char *stage,int error)
{
    const char *reason="storage operation failed";
    switch(error) {
        case ENOSPC:reason="not enough free space";break;
        case EFBIG:reason="package exceeds the filesystem file-size limit";break;
        case EROFS:reason="storage is read-only";break;
        case EACCES:case EPERM:reason="storage access denied";break;
        case ENOENT:reason="staging folder is missing or storage disconnected";break;
        case ENOTDIR:case EISDIR:reason="staging path has the wrong file type";break;
        case EEXIST:reason="an existing transfer lock must be released before retrying";break;
        case ENOMEM:reason="not enough memory";break;
        case EIO:reason="storage I/O error";break;
#ifdef ELOOP
        case ELOOP:reason="staging path is a symbolic link";break;
#endif
    }
    j->storage_errno=error;
    snprintf(j->storage_stage,sizeof(j->storage_stage),"%s",stage);
    snprintf(j->storage_error,sizeof(j->storage_error),"Staging %s failed: %s (errno %d)",stage,reason,error);
    return -1;
}
int gs_store_size(int fd,int64_t *size)
{
#ifdef GS_HOST_TEST
    struct stat st;if(fstat(fd,&st))return -1;*size=st.st_size;
#else
    // Transfer files use positioned IO. Query the kernel directly, avoiding
    // a libc stat structure crossing the PRX/runtime ABI boundary.
    int64_t end=sceKernelLseek(fd,0,SEEK_END);if(end<0)return store_io_failure(end);*size=end;
#endif
    return 0;
}
#include <signal.h>
#ifndef _WIN32
#include <sys/file.h>
#endif
#ifndef _WIN32
_Static_assert(sizeof(off_t)>=8,"Large package offsets require 64-bit off_t");
#endif
#ifdef _WIN32
static volatile int host_io_gate;
static int host_alive(long pid) {HANDLE process=OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION,FALSE,(DWORD)pid);if(process){DWORD status=STILL_ACTIVE;int alive=!GetExitCodeProcess(process,&status)||status==STILL_ACTIVE;CloseHandle(process);return alive;}return GetLastError()!=ERROR_INVALID_PARAMETER;}
#endif
uint32_t gs_crc(const void *data,size_t length,uint32_t crc)
{
    const unsigned char *p=data;crc=~crc;
    while(length--){crc^=*p++;for(int i=0;i<8;i++)crc=(crc>>1)^(0xedb88320U & (0U-(crc&1)));}return ~crc;
}
int gs_store_write(int fd,const void *data,size_t length,uint64_t offset)
{
    const unsigned char *p=data;
    while(length) {
#ifdef _WIN32
        gs_lock(&host_io_gate);int64_t n=_lseeki64(fd,offset,SEEK_SET)<0?-1:_write(fd,p,(unsigned)length);gs_unlock(&host_io_gate);
#elif defined(GS_HOST_TEST)
        ssize_t n=pwrite(fd,p,length,(off_t)offset);
#else
        int64_t n=(int64_t)sceKernelPwrite(fd,p,length,(off_t)offset);
#endif
        if(n<=0 || (uint64_t)n>length)return store_io_failure(n);p+=n;length-=(size_t)n;offset+=(uint64_t)n;
    }return 0;
}
int gs_store_read(int fd,void *data,size_t length,uint64_t offset)
{
    unsigned char *p=data;
    while(length) {
#ifdef _WIN32
        gs_lock(&host_io_gate);int64_t n=_lseeki64(fd,offset,SEEK_SET)<0?-1:_read(fd,p,(unsigned)length);gs_unlock(&host_io_gate);
#elif defined(GS_HOST_TEST)
        ssize_t n=pread(fd,p,length,(off_t)offset);
#else
        int64_t n=(int64_t)sceKernelPread(fd,p,length,(off_t)offset);
#endif
        if(n<=0 || (uint64_t)n>length)return store_io_failure(n);p+=n;length-=(size_t)n;offset+=(uint64_t)n;
    }return 0;
}
static int lock_exclusive(int fd)
{
#ifdef _WIN32
    OVERLAPPED position;memset(&position,0,sizeof(position));
    if(LockFileEx((HANDLE)_get_osfhandle(fd),LOCKFILE_EXCLUSIVE_LOCK|LOCKFILE_FAIL_IMMEDIATELY,0,1,0,&position))return 0;
    errno=GetLastError()==ERROR_LOCK_VIOLATION?EEXIST:EACCES;return -1;
#else
    if(!flock(fd,LOCK_EX|LOCK_NB))return 0;
    if(errno==EWOULDBLOCK)errno=EEXIST;return -1;
#endif
}
static int acquire(GsJobState *j)
{
    snprintf(j->lock_path,sizeof(j->lock_path),"%s.xfer-lock",j->dest);
    int flags=O_RDWR|O_CREAT|O_EXCL;
#ifdef _WIN32
    flags|=_O_BINARY;
#else
    flags|=O_NOFOLLOW|O_NONBLOCK;
#endif
    int fd=open(j->lock_path,flags,0600),created=fd>=0;
    if(fd<0 && errno==EEXIST)fd=open(j->lock_path,flags&~(O_CREAT|O_EXCL),0600);
    if(fd<0)return storage_failure(j,"lock creation",errno);
    if(lock_exclusive(fd)){int error=errno;close(fd);return storage_failure(j,"lock acquisition",error);}
    if(!created) {
        char previous[96]={0};int64_t length=0;long pid=0;int consumed=0;
        if(gs_store_size(fd,&length)){int error=errno;close(fd);return storage_failure(j,"lock size query",error);}
        if(length<=0 || length>=(int64_t)sizeof(previous)){close(fd);return storage_failure(j,"lock format",EEXIST);}
        int n=(int)length;
        if(gs_store_read(fd,previous,(size_t)n,0)){int error=errno;close(fd);return storage_failure(j,"lock read",error);}
        int parsed=sscanf(previous,"%ld\n%n",&pid,&consumed)==1 && pid>0 && pid<=INT32_MAX;
        parsed=parsed && !memchr(previous,0,(size_t)n);
        int modern=parsed && n-consumed==(int)strlen("SSPI-XFER-LOCK 1\n") && !strcmp(previous+consumed,"SSPI-XFER-LOCK 1\n");
        // A versioned lock is owned by its open descriptor, not its PID. A
        // legacy PID-only record remains protected unless its owner is gone.
        int legacy=parsed && consumed==n;
        int dead=0;
        if(legacy) {
#ifdef _WIN32
            dead=!host_alive(pid);
#else
            dead=kill((pid_t)pid,0)<0 && errno==ESRCH;
#endif
        }
        if(!modern && !dead){close(fd);return storage_failure(j,"lock ownership",EEXIST);}
    }
    j->lock_fd=fd;
    char text[64];int n=snprintf(text,sizeof(text),"%ld\nSSPI-XFER-LOCK 1\n",(long)getpid());
    if(gs_store_write(fd,text,(size_t)n,0))return storage_failure(j,"lock write",errno);
    if(ftruncate(fd,n))return storage_failure(j,"lock resize",errno);
    if(fsync(j->lock_fd))return storage_failure(j,"lock flush",errno);
    return 0;
}
int gs_store_snapshot(GsJobState *j,GsMapHeader header,const GsChunk *chunks)
{
    char temporary[1150];header.crc=0;
    header.crc=gs_crc(&header,sizeof(header),0);header.crc=gs_crc(chunks,header.count*sizeof(GsChunk),header.crc);
#ifdef GS_HOST_TEST
    extern void gs_test_checkpoint_delay(void);
    gs_test_checkpoint_delay();
#endif
    if(fsync(j->fd))return storage_failure(j,"package flush",errno);
    snprintf(temporary,sizeof(temporary),"%s.tmp",j->map);
    int fd=open(temporary,O_WRONLY|O_CREAT|O_TRUNC,0600);if(fd<0)return storage_failure(j,"resume-map creation",errno);
    int rc=0;
    if(gs_store_write(fd,&header,sizeof(header),0)||gs_store_write(fd,chunks,header.count*sizeof(GsChunk),sizeof(header)))rc=storage_failure(j,"resume-map write",errno);
    else if(fsync(fd))rc=storage_failure(j,"resume-map flush",errno);
    if(close(fd) && !rc)rc=storage_failure(j,"resume-map close",errno);
#ifdef _WIN32
    if(!rc && !MoveFileExA(temporary,j->map,MOVEFILE_REPLACE_EXISTING|MOVEFILE_WRITE_THROUGH)) {
        DWORD error=GetLastError();
        rc=storage_failure(j,"resume-map commit",error==ERROR_ACCESS_DENIED||error==ERROR_SHARING_VIOLATION?EACCES:EIO);
    }
#else
    if(!rc && rename(temporary,j->map))rc=storage_failure(j,"resume-map commit",errno);
#endif
    if(rc)unlink(temporary);
    return rc;
}
int gs_store_checkpoint(GsJobState *j)
{
    int rc=gs_store_snapshot(j,j->header,j->chunks);
    if(!rc){j->dirty=0;j->checkpoint_at=gs_clock();}return rc;
}
int gs_store_open(GsJobState *j,const unsigned char identity[32])
{
    j->storage_error[0]=j->storage_stage[0]=0;j->storage_errno=0;
    if(acquire(j))return -1;
    snprintf(j->part,sizeof(j->part),"%s.part",j->dest);snprintf(j->map,sizeof(j->map),"%s.map",j->dest);
#ifdef GS_HOST_TEST
    struct stat st;if(!lstat(j->part,&st) && !S_ISREG(st.st_mode))return storage_failure(j,"package type check",EISDIR);
#endif
    int flags=O_RDWR|O_CREAT;
#ifndef _WIN32
    // The bundled native libc lstat is an ENOSYS stub. Enforce the existing
    // no-symlink policy atomically when opening, without a libc stat ABI.
    flags|=O_NOFOLLOW|O_NONBLOCK;
#endif
    j->fd=open(j->part,flags,0600);if(j->fd<0)return storage_failure(j,"package open",errno);
    j->header.magic=0x33585347;j->header.version=3;j->header.chunk_size=GS_XFER_CHUNK;
    j->header.total=(uint64_t)j->status.total;
    j->header.count=(uint32_t)((j->header.total+GS_XFER_CHUNK-1)/GS_XFER_CHUNK);
    j->header.single=(uint32_t)j->single;memcpy(j->header.identity,identity,32);
    if(j->expected[0] && gs_unhex(j->expected,j->header.expected))return storage_failure(j,"checksum validation",EINVAL);
    if(!j->header.count || j->header.count>65536)return storage_failure(j,"package size validation",EFBIG);
    j->checkpoint_chunks=calloc(j->header.count,sizeof(GsChunk));
    j->chunks=calloc(j->header.count,sizeof(GsChunk));j->claims=calloc(j->header.count,1);j->attempts=calloc(j->header.count,1);
    j->retry_at=calloc(j->header.count,sizeof(uint64_t));
    if(!j->chunks||!j->checkpoint_chunks||!j->claims||!j->attempts||!j->retry_at)return storage_failure(j,"resume-map allocation",ENOMEM);
    FILE *f=fopen(j->map,"rb");int valid=0;
    if(f) {
        GsMapHeader old;
        if(fread(&old,1,sizeof(old),f)==sizeof(old) && old.magic==j->header.magic && old.version==3 && old.count==j->header.count &&
           old.total==j->header.total && old.chunk_size==GS_XFER_CHUNK && old.single==j->header.single &&
           !memcmp(old.identity,identity,32)&&!memcmp(old.expected,j->header.expected,32) &&
           fread(j->chunks,sizeof(GsChunk),old.count,f)==old.count && fgetc(f)==EOF) {
            uint32_t crc=old.crc;old.crc=0;valid=crc==gs_crc(j->chunks,old.count*sizeof(GsChunk),gs_crc(&old,sizeof(old),0));
        }fclose(f);
    }
    int64_t file_size=0;if(gs_store_size(j->fd,&file_size))return storage_failure(j,"package size query",errno);
    if(file_size!=j->status.total)valid=0;
    if(!valid || j->single)memset(j->chunks,0,j->header.count*sizeof(GsChunk));
    if(ftruncate(j->fd,j->status.total))return storage_failure(j,"package resize",errno);
    unsigned char *buffer=malloc(GS_XFER_BUFFER);if(!buffer)return storage_failure(j,"verification-buffer allocation",ENOMEM);
    for(uint32_t i=0;i<j->header.count;i++) if(j->chunks[i].done) {
        SHA256_CTX hash;unsigned char digest[32];sha256_init(&hash);
        uint64_t start=(uint64_t)i*GS_XFER_CHUNK, end=start+GS_XFER_CHUNK;if(end>j->header.total)end=j->header.total;
        int bad=0;
        for(uint64_t at=start;at<end;){size_t n=(size_t)(end-at);if(n>GS_XFER_BUFFER)n=GS_XFER_BUFFER;if(gs_store_read(j->fd,buffer,n,at)){bad=1;break;}sha256_update(&hash,buffer,n);at+=n;}
        sha256_final(&hash,digest);
        if(bad||memcmp(digest,j->chunks[i].hash,32))memset(&j->chunks[i],0,sizeof(GsChunk));
        else j->status.done+=(int64_t)(end-start);
    }
    free(buffer);return gs_store_checkpoint(j);
}
void gs_store_close(GsJobState *j)
{
    if(j->fd>=0)close(j->fd);j->fd=-1;
    // Keep the versioned inode stable. Unlinking it could let another process
    // lock a replacement while a waiter still holds this original inode.
    if(j->lock_fd>=0)close(j->lock_fd);j->lock_fd=-1;
    free(j->checkpoint_chunks);j->checkpoint_chunks=NULL;
    free(j->chunks);free(j->claims);free(j->attempts);j->chunks=NULL;j->claims=j->attempts=NULL;
    free(j->retry_at);j->retry_at=NULL;
}
int64_t sspi_xfer_durable(const char *destination)
{
    char path[1100];snprintf(path,sizeof(path),"%s.map",destination);FILE *f=fopen(path,"rb");if(!f)return 0;
    GsMapHeader h;int64_t done=0;
    if(fread(&h,1,sizeof(h),f)!=sizeof(h)||h.magic!=0x33585347||h.version!=3||h.chunk_size!=GS_XFER_CHUNK||!h.count||h.count>65536||h.total>1099511627776ULL||h.count!=(h.total+GS_XFER_CHUNK-1)/GS_XFER_CHUNK){fclose(f);return 0;}
    GsChunk *chunks=calloc(h.count,sizeof(GsChunk));if(!chunks){fclose(f);return 0;}
    uint32_t crc=h.crc;h.crc=0;
    if(fread(chunks,sizeof(GsChunk),h.count,f)==h.count && fgetc(f)==EOF && crc==gs_crc(chunks,h.count*sizeof(GsChunk),gs_crc(&h,sizeof(h),0)))
        for(uint32_t i=0;i<h.count;i++)if(chunks[i].done){uint64_t n=h.total-(uint64_t)i*GS_XFER_CHUNK;done+=(int64_t)(n>GS_XFER_CHUNK?GS_XFER_CHUNK:n);}
    free(chunks);fclose(f);return done;
}
