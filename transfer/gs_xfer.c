#include "gs_internal.h"
#ifndef SSPI_OWNER_DEBUG
#define SSPI_OWNER_DEBUG 0
#endif
#ifndef GS_HOST_TEST
#include "../native/sspi_log.h"
#include <orbis/_types/user.h>
#endif
static volatile int gate, init_gate, log_gate, shutting_down;
static int initialized, next_handle=1, turn;
#define GS_COOLDOWN_SLOTS 32
typedef struct {char origin[512];uint64_t until;} OriginCooldown;
static OriginCooldown origin_cooldowns[GS_COOLDOWN_SLOTS];
// Lane counts a server held without reset bursts, so the next file from the
// same host starts there instead of rediscovering the limit with failures.
typedef struct {char origin[512];int ceiling;unsigned hold;uint64_t until,seen;} OriginLanes;
static OriginLanes origin_lanes[GS_COOLDOWN_SLOTS];
// A reset burst, or a raise that added no speed, holds a job at the level that
// worked before one lane is probed above it; the hold doubles while that
// repeats. Speed is judged only over real time, so a short sample (or a host
// test advancing the clock) never reverts a useful lane.
#define LANE_HOLD_MIN_MS 60000U
#define LANE_HOLD_MAX_MS 600000U
#define LANE_RATE_MIN_MS 4000U
// A server with no history starts at six lanes and climbs while speed rises,
// instead of opening all 25 at once and learning its limit from resets.
#define LANE_START 6
#ifdef GS_HOST_TEST
static uint64_t test_clock_offset;
__declspec(dllexport) void gs_test_advance(unsigned ms){__atomic_add_fetch(&test_clock_offset,ms,__ATOMIC_RELAXED);}
#endif
static GsJobState jobs[GS_XFER_JOBS];
// Twenty-five lanes double-buffer 2 MiB blocks: a 100 MiB ceiling (was 40 MiB
// for ten lanes). Only lanes that are transferring hold their 4 MiB; an idle
// lane returns it, so a quiet worker does not keep the ceiling allocated.
#define WRITE_BLOCK (2U * 1024U * 1024U)
#define READ_BLOCK (1024U * 1024U)
#define WRITE_RUN (16U * 1024U * 1024U)
#define LANE_BUFFER_BYTES (2U * WRITE_BLOCK)
#define BUFFER_CEILING_MIB (GS_XFER_LANES * LANE_BUFFER_BYTES / (1024U * 1024U))
#define LANE_IDLE_RELEASE_MS 30000U
#define LANE_ALLOCATION_RETRY_MS 2000U
#define LANE_IDLE_POLL_MS 20U
#define LANE_IDLE_MAX_POLL_MS 160U
#define TRANSFER_LOG_SIZE 1280
typedef struct { uint64_t offset; size_t length; int state; } WriteBlock;
typedef struct {
    GsThread thread; GsHttp http; int job, started; unsigned span;
    int64_t written, received; unsigned char *buffer; uint64_t idle_since;
    WriteBlock writes[2]; unsigned producer, consumer, hash_consumer; int write_error, hashing;
    SHA256_CTX hash; int source_handle, source_generation;
    uint64_t retry_after;
    // Start of the current blocking open/read (0 when idle). The poll-driven
    // watchdog aborts a call that stalls; `stalled` marks that abort as retryable.
    uint64_t io_since; int io_kind, stalled;
#if SSPI_OWNER_DEBUG
    uint64_t owner_filling, owner_wait_started;
#endif
} Lane;
#define GS_STALL_READ_MS 8000U
#define GS_STALL_OPEN_MS 15000U
enum { GS_IO_OPEN=1, GS_IO_READ, GS_IO_EOF };
static Lane lanes[GS_XFER_LANES];
static unsigned allocated_lane_buffers;
#if SSPI_OWNER_DEBUG
typedef struct {
    uint64_t sampled_at, network_bytes, write_bytes, write_ms, read_ms, wait_ms;
    uint64_t write_started, network_progress_at, buffer_highwater;
    int sampled_state;
} OwnerMetrics;
static OwnerMetrics owner_metrics[GS_XFER_JOBS];
#endif
static GsThread writer_thread;
static int writer_started, writer_stopping;
static GsThread checkpoint_thread;
static int checkpoint_started, checkpoint_stopping;
static GsThread hash_threads[2];static int hash_started;
#ifdef GS_HOST_TEST
static unsigned test_write_delay; static int test_write_failure;
static unsigned test_idle_waits;
__declspec(dllexport) void gs_test_disk(unsigned delay,int fail){test_write_delay=delay;test_write_failure=fail;}
__declspec(dllexport) unsigned gs_test_idle_waits(void){return __atomic_load_n(&test_idle_waits,__ATOMIC_RELAXED);}
#endif
void gs_lock(volatile int *p) {while(__sync_lock_test_and_set(p,1))gs_sleep(1);}
void gs_unlock(volatile int *p) {__sync_lock_release(p);}
static uint64_t origin_cooldown_locked(const char *origin)
{
    if(!origin||!*origin)return 0;
    for(unsigned i=0;i<GS_COOLDOWN_SLOTS;i++)
        if(origin_cooldowns[i].origin[0]&&!strcmp(origin_cooldowns[i].origin,origin))return origin_cooldowns[i].until;
    return 0;
}
static void origin_backoff_locked(const char *origin,uint64_t until)
{
    if(!origin||!*origin||until<=gs_clock())return;
    uint64_t now=gs_clock();unsigned slot=GS_COOLDOWN_SLOTS;uint64_t earliest=UINT64_MAX;unsigned replace=0;
    for(unsigned i=0;i<GS_COOLDOWN_SLOTS;i++) {
        if(origin_cooldowns[i].origin[0]&&!strcmp(origin_cooldowns[i].origin,origin)){slot=i;break;}
    }
    if(slot==GS_COOLDOWN_SLOTS)for(unsigned i=0;i<GS_COOLDOWN_SLOTS;i++) {
        if(!origin_cooldowns[i].origin[0]){slot=i;break;}
        if(origin_cooldowns[i].until<=now){slot=i;break;}
        if(origin_cooldowns[i].until<earliest){earliest=origin_cooldowns[i].until;replace=i;}
    }
    if(slot==GS_COOLDOWN_SLOTS)slot=replace;
    if(strcmp(origin_cooldowns[slot].origin,origin)) {
        snprintf(origin_cooldowns[slot].origin,sizeof(origin_cooldowns[slot].origin),"%s",origin);
        origin_cooldowns[slot].until=0;
    }
    if(origin_cooldowns[slot].until<until)origin_cooldowns[slot].until=until;
    for(int i=0;i<GS_XFER_JOBS;i++)if(jobs[i].handle&&!strcmp(jobs[i].origin,origin)) {
        if(jobs[i].limit>2)jobs[i].limit=2;
        jobs[i].clean_chunks=0;jobs[i].recovery_at=origin_cooldowns[slot].until+10000;
    }
}
static uint64_t origin_cooldown(const char *origin)
{
    gs_lock(&gate);uint64_t until=origin_cooldown_locked(origin);gs_unlock(&gate);return until;
}
void gs_sleep(unsigned ms) {
#ifdef _WIN32
    Sleep(ms);
#elif defined(GS_HOST_TEST)
    usleep(ms*1000);
#else
    sceKernelUsleep(ms*1000);
#endif
}
static void idle_wait(unsigned *delay)
{
    if(*delay<8)*delay=*delay?*delay*2:1;
#ifdef GS_HOST_TEST
    __atomic_add_fetch(&test_idle_waits,1,__ATOMIC_RELAXED);
#endif
    gs_sleep(*delay);
}
static void background_thread(const char *role)
{
#ifndef GS_HOST_TEST
    OrbisPthread self=scePthreadSelf();int32_t before=0;
    int rc=scePthreadGetprio(self,&before);
    // The SDK names larger FIFO values as lower priority. Adjust this engine
    // thread only, preserving its policy and any already lower priority.
    // Socket readers keep their inherited priority: at the lowest FIFO level the
    // renderer starves them, TCP windows close and each lane falls to ~1.5 MiB/s.
    // Every received block must then pass the hash threads and the single disk
    // writer before its lane may read again; below the readers, those consumers
    // lose the CPU to 25 TLS readers and the renderer, lanes park in wait_write
    // and throughput collapses until they drain. The checkpoint thread holds the
    // package vnode during fsync, so it must not stall the writer behind it.
    // Only source preparation stays at the lowest level.
    int pipeline=!strcmp(role,"reader")||!strcmp(role,"writer")||!strcmp(role,"hash")||!strcmp(role,"checkpoint");
    int32_t target=pipeline?before:ORBIS_KERNEL_PRIO_FIFO_LOWEST;
    if(!rc && before<target)rc=scePthreadSetprio(self,target);
    gs_log_write("download","event=thread-priority role=%s before=%d target=%d rc=0x%08X",role,before,target,(unsigned)rc);
#else
    (void)role;
#endif
}
uint64_t gs_clock(void) {
#ifdef _WIN32
    return GetTickCount64()
#ifdef GS_HOST_TEST
        +__atomic_load_n(&test_clock_offset,__ATOMIC_RELAXED)
#endif
        ;
#elif defined(GS_HOST_TEST)
    struct timespec t;clock_gettime(CLOCK_MONOTONIC,&t);return (uint64_t)t.tv_sec*1000+t.tv_nsec/1000000;
#else
    return sceKernelGetProcessTime()/1000;
#endif
}
// Real elapsed milliseconds for speed windows; host tests advance gs_clock.
static uint64_t real_clock(void)
{
#if defined(GS_HOST_TEST) && defined(_WIN32)
    return gs_clock()-__atomic_load_n(&test_clock_offset,__ATOMIC_RELAXED);
#else
    return gs_clock();
#endif
}
#ifdef _WIN32
typedef struct {void *(*run)(void *);void *argument;} HostThread;
static DWORD WINAPI host_thread(void *pointer) {HostThread t=*(HostThread*)pointer;free(pointer);t.run(t.argument);return 0;}
#endif
int gs_thread_start(GsThread *t,void *(*run)(void *),void *p) {
#ifdef _WIN32
    HostThread *argument=malloc(sizeof(*argument));if(!argument)return -1;
    argument->run=run;argument->argument=p;*t=CreateThread(NULL,0,host_thread,argument,0,NULL);
    if(!*t){free(argument);return -1;}return 0;
#elif defined(GS_HOST_TEST)
    return pthread_create(t,NULL,run,p);
#else
    return scePthreadCreate(t,NULL,run,p,"sspi-xfer");
#endif
}
void gs_thread_join(GsThread t) {
#ifdef _WIN32
    WaitForSingleObject(t,INFINITE);CloseHandle(t);
#elif defined(GS_HOST_TEST)
    pthread_join(t,NULL);
#else
    scePthreadJoin(t,NULL);
#endif
}
static void log_line(const char *destination,const char *line)
{
#ifndef GS_HOST_TEST
    (void)destination;
    gs_log_write("download", "%s", line);
#else
    char path[1100],old[1120];snprintf(path,sizeof(path),"%s",destination);
    char *slash=strrchr(path,'/'),*back=strrchr(path,'\\');if(back&&(!slash||back>slash))slash=back;
    if(!slash)return;snprintf(slash+1,sizeof(path)-(size_t)(slash+1-path),"transfer.log");
    // Separate append handles can overwrite concurrent records on host runtimes.
    // Serialize logging and rotation without taking the scheduler lock here.
    gs_lock(&log_gate);
    struct stat st;if(!stat(path,&st)&&st.st_size>1024*1024){snprintf(old,sizeof(old),"%s.1",path);unlink(old);rename(path,old);}
    FILE *f=fopen(path,"ab");if(f){fputs(line,f);fclose(f);}
    gs_unlock(&log_gate);
#endif
}
static void format_log(const GsJobState *j,const char *event,char *line,size_t size)
{
    snprintf(line,size,"ms=%llu api=3 engine=sceHttp-chunks revision=twenty-five-lane-background-1 event=%s handle=%d state=%d lanes=%d done=%lld total=%lld network=%lld retries=%d code=%d limit=%d target=%d checkpoint_ms=%llu request_ms=%llu read_ms=%llu write_ms=%llu hash_ms=%llu read_calls=%llu write_calls=%llu write_bytes=%llu buffer_wait_ms=%llu read_interruptions=%llu write_jumps=%llu write_jump_bytes=%llu write_switches=%llu write_max_ms=%llu span_chunks=8 write_run_mib=%u write_block_kib=%u read_block_kib=%u buffer_mib=%u buffer_allocated_mib=%u ceiling=%d hold_ms=%u\n",
        (unsigned long long)gs_clock(),event,j->handle,j->status.state,j->status.lanes,
        (long long)j->status.done,(long long)j->status.total,(long long)j->status.network_bytes,j->status.retries,j->status.error_code,j->limit,j->target_limit,
        (unsigned long long)j->checkpoint_ms,(unsigned long long)j->request_ms,(unsigned long long)j->read_ms,
        (unsigned long long)j->write_ms,(unsigned long long)j->hash_ms,
        (unsigned long long)j->read_calls,(unsigned long long)j->write_calls,
        (unsigned long long)j->write_bytes,(unsigned long long)j->buffer_wait_ms,(unsigned long long)j->read_interruptions,
        (unsigned long long)j->write_jumps,(unsigned long long)j->write_jump_bytes,
        (unsigned long long)j->write_switches,(unsigned long long)j->write_max_ms,
        WRITE_RUN/(1024U*1024U),WRITE_BLOCK/1024U,READ_BLOCK/1024U,(unsigned)BUFFER_CEILING_MIB,
        (unsigned)(__atomic_load_n(&allocated_lane_buffers,__ATOMIC_RELAXED)*LANE_BUFFER_BYTES/(1024U*1024U)),
        j->ceiling,j->ceiling_hold);
}
void gs_transfer_log(const GsJobState *j,const char *event)
{
    char line[TRANSFER_LOG_SIZE];format_log(j,event,line,sizeof(line));log_line(j->dest,line);
}
static void gs_transfer_admission_log(const GsJobState *j,int etag_present,int etag_usable,
    int date_present,int date_valid,int modified_present,int modified_valid,int64_t date_modified_gap,
    const unsigned char *header,size_t header_length)
{
    // The first eight bytes name the container format (PKG, RAR, ZIP, 7z) only.
    char line[416],magic[17]={0};
    for(size_t i=0;header&&i<8&&i<header_length;i++)snprintf(magic+i*2,3,"%02x",header[i]);
    snprintf(line,sizeof(line),"ms=%llu event=admission handle=%d requested=%d effective=%d initial=%d validator=%s fallback=%s etag_present=%d etag_usable=%d date_present=%d date_valid=%d last_modified_present=%d last_modified_valid=%d date_modified_gap_s=%lld magic=%s\n",
        (unsigned long long)gs_clock(),j->handle,j->requested_limit,j->target_limit,j->limit,
        j->validator_kind[0]?j->validator_kind:"none",j->fallback_reason[0]?j->fallback_reason:"none",
        etag_present,etag_usable,date_present,date_valid,modified_present,modified_valid,
        (long long)date_modified_gap,magic[0]?magic:"none");
    log_line(j->dest,line);
}
#if SSPI_OWNER_DEBUG
static uint64_t owner_buffer_bytes(const GsJobState *j)
{
    uint64_t bytes=0;
    for(int i=0;i<GS_XFER_LANES;i++)if(lanes[i].job>=0 && &jobs[lanes[i].job]==j) {
        bytes+=lanes[i].owner_filling;
        for(int s=0;s<2;s++)if(lanes[i].writes[s].state)bytes+=lanes[i].writes[s].length;
    }
    return bytes;
}
static void owner_buffer_peak(const GsJobState *j)
{
    uint64_t bytes=owner_buffer_bytes(j);OwnerMetrics *m=&owner_metrics[j-jobs];
    if(bytes>m->buffer_highwater)m->buffer_highwater=bytes;
}
// Called by the existing poll, under gate. No sampler thread or filesystem
// probe is needed; these are this engine's I/O timings, not HDD utilization.
static void owner_sample(const GsJobState *j,char *line,size_t capacity)
{
    OwnerMetrics *m=&owner_metrics[j-jobs];uint64_t now=gs_clock();
    int terminal=j->status.state==GS_COMPLETE||j->status.state==GS_FAILED||j->status.state==GS_CANCELED;
    if(now-m->sampled_at<2000 && !(terminal && m->sampled_state!=j->status.state))return;
    uint64_t elapsed=now-m->sampled_at;if(!elapsed)elapsed=1;
    uint64_t filling=0,hashing=0,ready=0,writing=0,waiting=j->buffer_wait_ms;unsigned waiters=0,active_jobs=0;
    for(int i=0;i<GS_XFER_JOBS;i++)if(jobs[i].active||jobs[i].finishing||jobs[i].preparing)active_jobs++;
    for(int i=0;i<GS_XFER_LANES;i++)if(lanes[i].job>=0 && &jobs[lanes[i].job]==j) {
        Lane *lane=&lanes[i];filling+=lane->owner_filling;
        if(lane->owner_wait_started){waiters++;waiting+=now-lane->owner_wait_started;}
        for(int s=0;s<2;s++) {
            WriteBlock *block=&lane->writes[s];
            if(block->state==1||block->state==4)hashing+=block->length;
            else if(block->state==3)ready+=block->length;
            else if(block->state==2)writing+=block->length;
        }
    }
    uint64_t write_started=__atomic_load_n(&m->write_started,__ATOMIC_ACQUIRE);
    uint64_t active_ms=write_started?now-write_started:0,write_ms=j->write_ms+active_ms;
    uint64_t network=(uint64_t)j->status.network_bytes;
    snprintf(line,capacity,"ms=%llu event=owner-disk handle=%d state=%d window_ms=%llu write_bps=%llu network_bps=%llu write_elapsed_ms=%llu write_max_ms=%llu write_active_ms=%llu read_elapsed_ms=%llu buffer_bytes=%llu buffer_total_bytes=%u buffer_highwater_bytes=%llu fill_bytes=%llu hash_bytes=%llu ready_bytes=%llu writing_bytes=%llu buffer_waiters=%u buffer_wait_ms=%llu network_quiet_ms=%llu lanes=%d limit=%d target=%d active_jobs=%u pipeline=caller-serial writer_threads=1 hash_threads=2 checkpoint_threads=1 lane_ceiling=%d\n",
        (unsigned long long)now,j->handle,j->status.state,(unsigned long long)elapsed,
        (unsigned long long)((j->write_bytes-m->write_bytes)*1000/elapsed),(unsigned long long)((network-m->network_bytes)*1000/elapsed),
        (unsigned long long)(write_ms>=m->write_ms?write_ms-m->write_ms:0),(unsigned long long)j->write_max_ms,(unsigned long long)active_ms,
        (unsigned long long)(j->read_ms-m->read_ms),(unsigned long long)(filling+hashing+ready+writing),GS_XFER_LANES*2*WRITE_BLOCK,
        (unsigned long long)m->buffer_highwater,(unsigned long long)filling,(unsigned long long)hashing,(unsigned long long)ready,(unsigned long long)writing,waiters,
        (unsigned long long)(waiting>=m->wait_ms?waiting-m->wait_ms:0),(unsigned long long)(now-m->network_progress_at),
        j->active,j->limit,j->target_limit,active_jobs,GS_XFER_LANES);
    m->sampled_at=now;m->sampled_state=j->status.state;m->network_bytes=network;m->write_bytes=j->write_bytes;
    m->write_ms=write_ms;m->read_ms=j->read_ms;m->wait_ms=waiting;
}
#endif
static GsJobState *lookup(int handle) {for(int i=0;i<GS_XFER_JOBS;i++)if(jobs[i].handle==handle)return &jobs[i];return NULL;}
static void fail(GsJobState *j,int code,const char *message) {j->status.state=GS_FAILED;j->status.error_code=code;snprintf(j->status.error,sizeof(j->status.error),"%s (0x%08X)",message,(unsigned)code);__atomic_store_n(&j->stop,3,__ATOMIC_RELEASE);gs_transfer_log(j,"failed");}
static uint64_t add_deadline(uint64_t now,uint64_t delay)
{
    return UINT64_MAX-now<delay?UINT64_MAX:now+delay;
}
// Debrid CDN links name one cached file for the life of the link; the
// provider never swaps bytes behind it. The exact Content-Range total then
// binds every range, so these links may use parallel lanes without an
// ETag or Last-Modified (Real-Debrid sends neither).
static int immutable_debrid_link(const char *url)
{
    static const char *const suffixes[]={".download.real-debrid.com",".debrid.it",".alldebrid.com",".tb-cdn.st",".tb-cdn.io",".torbox.app"};
    const char *host=url?strstr(url,"://"):NULL;if(!host)return 0;host+=3;
    size_t n=strcspn(host,":/?#");
    for(unsigned i=0;i<sizeof(suffixes)/sizeof(suffixes[0]);i++) {
        size_t m=strlen(suffixes[i]);
        if(n>m&&!strncasecmp(host+n-m,suffixes[i],m))return 1;
    }
    return 0;
}
static void representation_identity(const char *requested,const char *effective,const char *last_modified,unsigned char output[32])
{
    SHA256_CTX hash;const unsigned char separator=0;
    if(!effective||!*effective)effective=requested;
    sha256_init(&hash);sha256_update(&hash,(const unsigned char*)requested,strlen(requested));
    sha256_update(&hash,&separator,1);sha256_update(&hash,(const unsigned char*)effective,strlen(effective));
    if(last_modified&&*last_modified) {
        static const char marker[]="\0last-modified:";
        sha256_update(&hash,(const unsigned char*)marker,sizeof(marker)-1);sha256_update(&hash,(const unsigned char*)last_modified,strlen(last_modified));
    }
    sha256_final(&hash,output);
}
#ifdef GS_HOST_TEST
__declspec(dllexport) void gs_test_origin_backoff_ms(const char *origin,unsigned ms)
{
    gs_lock(&gate);origin_backoff_locked(origin,add_deadline(gs_clock(),ms));gs_unlock(&gate);
}
__declspec(dllexport) uint64_t gs_test_origin_cooldown_ms(const char *origin)
{
    uint64_t until=origin_cooldown(origin),now=gs_clock();return until>now?until-now:0;
}
#endif
static uint64_t retry_delay(unsigned attempts,uint64_t base)
{
    unsigned shift=attempts?attempts-1:0;if(shift>6)shift=6;
    uint64_t delay=base<<shift;
    delay+=(gs_clock()^(uintptr_t)&delay^((uint64_t)attempts<<17))%201U;
    return delay>60000?60000:delay;
}
// Called with gate held. The checkpoint worker owns this immutable snapshot;
// network lanes keep reading while data and then its resume map are flushed.
static int checkpoint(GsJobState *j)
{
    if(j->checkpointing || !j->chunks || !j->dirty)return 0;
    memcpy(j->checkpoint_chunks,j->chunks,j->header.count*sizeof(GsChunk));
    j->checkpoint_dirty=j->dirty;j->checkpointing=1;
    return 0;
}
static void *checkpoint_worker(void *unused)
{
    (void)unused;background_thread("checkpoint");unsigned idle=0;
    for(;;) {
        GsJobState *j=NULL;GsMapHeader header;
        gs_lock(&gate);
        for(int i=0;i<GS_XFER_JOBS;i++)if(jobs[i].checkpointing==1) {
            j=&jobs[i];j->checkpointing=2;header=j->header;break;
        }
        int stopping=checkpoint_stopping;gs_unlock(&gate);
        if(!j){if(stopping)break;idle_wait(&idle);continue;}idle=0;
        uint64_t started=gs_clock();int rc=gs_store_snapshot(j,header,j->checkpoint_chunks);
        gs_lock(&gate);j->checkpoint_ms+=gs_clock()-started;
        if(rc) {
            j->checkpoint_failed=1;
            fail(j,-12,j->storage_error[0]?j->storage_error:"Chunk checkpoint could not be committed");
            for(int i=0;i<GS_XFER_LANES;i++)if(lanes[i].job>=0 && &jobs[lanes[i].job]==j)gs_http_abort(&lanes[i].http);
        }
        else {j->dirty-=j->checkpoint_dirty;j->checkpoint_at=gs_clock();}
        j->checkpoint_dirty=0;j->checkpointing=0;gs_unlock(&gate);
    }
    return NULL;
}
// Pause/completion must include chunks finished after the queued snapshot.
// Pin the caller too: a paused job can otherwise be destroyed between wakeups.
static void checkpoint_drain(GsJobState *j)
{
    j->checkpoint_waiters++;
    for(;;) {
        if(!j->checkpointing) {
            if(!j->dirty || j->checkpoint_failed)break;
            checkpoint(j);
        }
        gs_unlock(&gate);gs_sleep(1);gs_lock(&gate);
    }
    j->checkpoint_waiters--;
}
// Two bounded blocks per lane let network reads overlap storage without lending
// unbounded memory to a slow disk. The lane owns its job until this queue drains.
static int ready_writer(unsigned turn,int previous_handle,uint64_t previous_end,uint64_t run_bytes)
{
    int first=-1;
    for(unsigned n=0;n<GS_XFER_LANES;n++) {
        unsigned i=(turn+n)%GS_XFER_LANES;Lane *candidate=&lanes[i];
        if(candidate->job<0 || candidate->writes[candidate->consumer].state!=3)continue;
        if(first<0)first=(int)i;
        WriteBlock *block=&candidate->writes[candidate->consumer];
        // Prefer a ready contiguous block, but never wait for its network lane.
        // After 16 MiB the normal rotation gives other ready lanes their turn.
        if(run_bytes<WRITE_RUN && block->length<=WRITE_RUN-run_bytes &&
           jobs[candidate->job].handle==previous_handle && block->offset==previous_end)return (int)i;
    }
    return first;
}
static void *disk_writer(void *unused)
{
    (void)unused;background_thread("writer");unsigned turn=0,idle=0;int previous_handle=0;
    uint64_t previous_end=0,run_bytes=0;
    while(!__atomic_load_n(&writer_stopping,__ATOMIC_ACQUIRE)) {
        Lane *lane=NULL;WriteBlock block={0};unsigned slot=0;GsJobState *j=NULL;
        gs_lock(&gate);
        int selected=ready_writer(turn,previous_handle,previous_end,run_bytes);
        if(selected>=0) {
            lane=&lanes[selected];slot=lane->consumer;block=lane->writes[slot];
            lane->writes[slot].state=2;j=&jobs[lane->job];turn=((unsigned)selected+1)%GS_XFER_LANES;
        }
        int previous_error=lane?lane->write_error:0;gs_unlock(&gate);
        if(!lane){idle_wait(&idle);continue;}idle=0;
        uint64_t before=gs_clock();
#if SSPI_OWNER_DEBUG
        __atomic_store_n(&owner_metrics[j-jobs].write_started,before,__ATOMIC_RELEASE);
#endif
#ifdef GS_HOST_TEST
        if(test_write_delay)gs_sleep(test_write_delay);
        if(test_write_failure)previous_error=-12;
#endif
        int rc=previous_error?previous_error:gs_store_write(j->fd,lane->buffer+slot*WRITE_BLOCK,block.length,block.offset);
        uint64_t elapsed=gs_clock()-before;
        gs_lock(&gate);j->write_ms+=elapsed;j->write_calls++;
#if SSPI_OWNER_DEBUG
        __atomic_store_n(&owner_metrics[j-jobs].write_started,0,__ATOMIC_RELEASE);
#endif
        if(elapsed>j->write_max_ms)j->write_max_ms=elapsed;
        if(rc){lane->write_error=-12;gs_http_abort(&lane->http);}
        else {
            lane->written+=(int64_t)block.length;j->write_bytes+=block.length;
            int same_file=previous_handle==j->handle;
            if(previous_handle && (!same_file || block.offset!=previous_end)) {
                // Logical write-position changes, not measured physical seeks.
                // Offsets from different files have no comparable distance.
                j->write_jumps++;
                if(same_file)j->write_jump_bytes+=block.offset>previous_end?block.offset-previous_end:previous_end-block.offset;
                else j->write_switches++;
            }
            run_bytes=same_file && block.offset==previous_end?run_bytes+block.length:block.length;
            if(run_bytes>WRITE_RUN)run_bytes=WRITE_RUN;
            previous_handle=j->handle;previous_end=block.offset+block.length;
        }
        lane->writes[slot].state=0;lane->consumer^=1;gs_unlock(&gate);
    }
    return NULL;
}
static void *hash_worker(void *unused)
{
    (void)unused;background_thread("hash");unsigned turn=0,idle=0;
    while(!__atomic_load_n(&writer_stopping,__ATOMIC_ACQUIRE)) {
        Lane *lane=NULL;unsigned slot=0;size_t length=0;GsJobState *j=NULL;
        gs_lock(&gate);
        for(unsigned n=0;n<GS_XFER_LANES;n++) {
            Lane *c=&lanes[(turn+n)%GS_XFER_LANES];
            if(c->job>=0&&!c->hashing&&c->writes[c->hash_consumer].state==1) {
                lane=c;slot=c->hash_consumer;length=c->writes[slot].length;
                c->hashing=1;c->writes[slot].state=4;j=&jobs[c->job];turn=(unsigned)(c-lanes+1)%GS_XFER_LANES;break;
            }
        }
        gs_unlock(&gate);if(!lane){idle_wait(&idle);continue;}idle=0;
        uint64_t before=gs_clock();sha256_update(&lane->hash,lane->buffer+slot*WRITE_BLOCK,length);
        gs_lock(&gate);j->hash_ms+=gs_clock()-before;lane->writes[slot].state=3;
        lane->hash_consumer^=1;lane->hashing=0;gs_unlock(&gate);
    }
    return NULL;
}
static int wait_write(Lane *lane,GsJobState *j,int drain)
{
    // Refilling a 2 MiB block takes a lane hundreds of milliseconds even at full
    // speed, so an 8 ms wake cap costs little. A fixed 1 ms poll let 25 parked lanes take the scheduler
    // lock ~25,000 times a second, exactly when the writer and hash threads need
    // that lock and the CPU to drain the backlog.
    uint64_t before=gs_clock();unsigned delay=0;
    for(;;) {
        gs_lock(&gate);
        int busy=drain?(lane->writes[0].state||lane->writes[1].state):lane->writes[lane->producer].state;
        int error=lane->write_error;
#if SSPI_OWNER_DEBUG
        if(busy && !lane->owner_wait_started)lane->owner_wait_started=before;
        if(!busy)lane->owner_wait_started=0;
#endif
        if(!busy) {j->buffer_wait_ms+=gs_clock()-before;gs_unlock(&gate);return error;}
        gs_unlock(&gate);delay=delay?(delay<8?delay*2:8):1;gs_sleep(delay);
    }
}
static int host_active(const GsJobState *job) {int count=0;for(int i=0;i<GS_XFER_JOBS;i++)if(jobs[i].handle&&!strcmp(jobs[i].origin,job->origin))count+=jobs[i].active;return count;}
static int total_active(void) {int n=0;for(int i=0;i<GS_XFER_JOBS;i++)n+=jobs[i].active;return n;}
static int source_available(const GsJobState *job)
{
    int active=0,limit=job->target_limit;
    for(int i=0;i<GS_XFER_JOBS;i++) {
        const GsJobState *other=&jobs[i];
        if(!other->handle || strcmp(other->url,job->url))continue;
        if(job->status.state==GS_DOWNLOADING && other!=job && other->status.state==GS_QUEUED && !other->stop && !other->active)return 0;
        active+=other->active;
        if(other->active || (!other->stop && (other->status.state==GS_QUEUED || other->status.state==GS_DOWNLOADING))) {
            if(other->target_limit<limit)limit=other->target_limit;
        }
    }
    return active<limit;
}
static int available_limit(const GsJobState *job)
{
    int reserve=0;
    for(int i=0;i<GS_XFER_JOBS;i++) {
        const GsJobState *other=&jobs[i];
        if(other==job || !other->handle || other->stop)continue;
        if(other->status.state==GS_QUEUED)reserve++;
        else if(other->status.state==GS_DOWNLOADING)reserve+=other->limit<GS_XFER_LANES/2?other->limit:GS_XFER_LANES/2;
    }
    int share=GS_XFER_LANES-reserve;
    return job->limit<share?job->limit:share;
}
static OriginLanes *origin_lanes_locked(const char *origin,int create)
{
    if(!origin||!*origin)return NULL;
    for(unsigned i=0;i<GS_COOLDOWN_SLOTS;i++)
        if(origin_lanes[i].origin[0]&&!strcmp(origin_lanes[i].origin,origin))return &origin_lanes[i];
    if(!create)return NULL;
    OriginLanes *slot=&origin_lanes[0];
    for(unsigned i=0;i<GS_COOLDOWN_SLOTS;i++) {
        if(!origin_lanes[i].origin[0]){slot=&origin_lanes[i];break;}
        if(origin_lanes[i].seen<slot->seen)slot=&origin_lanes[i];
    }
    memset(slot,0,sizeof(*slot));snprintf(slot->origin,sizeof(slot->origin),"%s",origin);
    return slot;
}
// Hold the job at `level` before one lane is probed above it again. Reset
// bursts describe the server and are kept for its next file; a raise that
// added no speed describes this console's drive or line and stays with the job.
static void hold_at(GsJobState *j,int level,uint64_t now,int server)
{
    if(level<1)level=1;
    unsigned hold=j->ceiling_hold;
    hold=!hold?LANE_HOLD_MIN_MS:hold>=LANE_HOLD_MAX_MS/2?LANE_HOLD_MAX_MS:hold*2;
    j->ceiling=level;j->ceiling_hold=hold;j->ceiling_until=add_deadline(now,hold);j->probe_from=0;
    if(server) {
        OriginLanes *o=origin_lanes_locked(j->origin,1);
        if(o){o->ceiling=level;o->hold=hold;o->until=j->ceiling_until;o->seen=now;}
    }
}
// Once per quiet interval (10 s without a provider failure). A raise is kept
// only if total speed rose by at least a third of what the added lanes would
// carry at the previous per-lane speed (and at least 5%); otherwise the job
// returns to the previous level and holds there. A PS4 Pro with an SSD keeps
// climbing while every raise pays off; an original or Slim settles where its
// drive or line tops out. Up to the learned ceiling the job restores quickly
// (half again per interval); past a hold it probes one lane at a time.
static void govern(GsJobState *j,uint64_t now)
{
    uint64_t real=real_clock(),window=real-j->rate_at,rate=0;
    int measured=j->rate_at&&window>=LANE_RATE_MIN_MS;
    if(measured)rate=(j->useful_bytes-j->rate_bytes)*1000U/window;
    if(j->probe_from) {
        // Judge the raise only while it is still in effect: a cooldown or a
        // reset burst may have lowered the allowance since.
        if(measured&&j->probe_rate&&j->limit>j->probe_from) {
            uint64_t added=(uint64_t)(j->limit-j->probe_from);
            uint64_t need=j->probe_rate+j->probe_rate*added/(3U*(uint64_t)j->probe_from);
            if(need<j->probe_rate+j->probe_rate/20)need=j->probe_rate+j->probe_rate/20;
            if(rate<need){j->limit=j->probe_from;hold_at(j,j->limit,now,0);}
            else {
                // The raise paid off: the next file from this server starts here.
                if(j->ceiling&&j->limit>j->ceiling)j->ceiling=j->limit;
                OriginLanes *o=origin_lanes_locked(j->origin,1);
                if(o&&o->ceiling<j->limit){o->ceiling=j->limit;o->seen=now;}
            }
        }
        j->probe_from=0;
    }
    int cap=j->target_limit;
    if(j->ceiling) {
        if(now<j->ceiling_until||j->limit<j->ceiling){if(j->ceiling<cap)cap=j->ceiling;}
        else if(j->limit+1<cap)cap=j->limit+1;
    }
    if(j->limit<cap) {
        int step=j->limit/2;if(step<1)step=1;
        j->probe_from=j->limit;j->probe_rate=measured?rate:0;
        j->limit=j->limit>cap-step?cap:j->limit+step;
    }
    j->rate_at=real;j->rate_bytes=j->useful_bytes;
    j->clean_chunks=0;j->recovery_at=add_deadline(now,10000);
}
static void clean_chunk(GsJobState *j)
{
    j->clean_chunks++;
    uint64_t now=gs_clock();
    if(j->clean_chunks>=4 && now>=j->recovery_at)govern(j,now);
}
static void useful_progress(GsJobState *j,uint64_t offset,uint64_t length)
{
    uint64_t useful=0;
    while(length && j->accepted) {
        uint32_t chunk=(uint32_t)(offset/GS_XFER_CHUNK);if(chunk>=j->header.count)break;
        uint64_t within=offset%GS_XFER_CHUNK;
        uint64_t available=GS_XFER_CHUNK-within;if(available>length)available=length;
        uint64_t high=within+available;
        if(high>j->accepted[chunk]){useful+=high-j->accepted[chunk];j->accepted[chunk]=high;j->attempts[chunk]=0;j->retry_at[chunk]=0;}
        offset+=available;length-=available;
    }
    if(!useful)return;
    j->useful_bytes=UINT64_MAX-j->useful_bytes<useful?UINT64_MAX:j->useful_bytes+useful;
    uint64_t now=gs_clock();j->useful_progress_at=now;
    // A quiet connection that is still accepting new offsets is healthy even
    // before another 16 MiB checkpoint chunk completes. Govern lanes each
    // quiet interval so tiny reads and short final spans can recover the
    // allowance instead of finishing permanently throttled.
    if(now>=j->recovery_at)govern(j,now);
    j->tls_failure_started=0;j->tls_failures=0;j->tls_failed_lanes=0;
    j->recovery_class=GS_FAILURE_NONE;j->recovery_deadline=0;j->recovery_detail[0]=0;
}
static void note_tls_failure(GsJobState *j,unsigned lane)
{
    uint64_t now=gs_clock();
    if(!j->tls_failure_started || (j->useful_progress_at && j->useful_progress_at>=j->tls_failure_started)) {
        j->tls_failure_started=now;j->tls_failures=0;j->tls_failed_lanes=0;
    }
    if(j->tls_failures<UINT32_MAX)j->tls_failures++;
    if(lane<32)j->tls_failed_lanes|=1U<<lane;
}
static unsigned bit_count(unsigned value){unsigned n=0;for(;value;value>>=1)n+=value&1U;return n;}
static int tls_episode_terminal(const GsJobState *j,uint64_t now)
{
    unsigned required=(unsigned)(j->limit<1?1:j->limit);if(required>GS_XFER_LANES)required=GS_XFER_LANES;
    if(j->tls_failures<8 || !j->tls_failure_started || now-j->tls_failure_started<120000 ||
       bit_count(j->tls_failed_lanes)<required || (j->useful_progress_at&&j->useful_progress_at>=j->tls_failure_started))return 0;
    // An active lane is only considered healthy until it has itself produced a
    // trust failure in this episode. This avoids two synchronized bad handshakes
    // keeping each other alive forever while preserving a genuinely untested lane.
    for(unsigned i=0;i<GS_XFER_LANES;i++)if(lanes[i].job>=0 && &jobs[lanes[i].job]==j && !(j->tls_failed_lanes&(1U<<i)))return 0;
    return 1;
}
static const char *retry_chunk(GsJobState *j,uint32_t chunk,unsigned lane,int rc,int retry_after,uint64_t *delay)
{
    uint64_t now=gs_clock();
    j->clean_chunks=0;j->recovery_at=now+10000;
    int transient=rc==408||rc==425||rc==429||rc==500||rc==502||rc==503||rc==504;
    int tls=(unsigned)rc==0x8095F00CU;
    *delay=retry_delay(j->attempts[chunk],tls?1000U:transient||rc==-15?1000U:250U);
    *delay+=lane*23U;
    if(retry_after>0) {
        unsigned seconds=(unsigned)retry_after>GS_RETRY_AFTER_MAX_SECONDS?GS_RETRY_AFTER_MAX_SECONDS:(unsigned)retry_after;
        *delay=(uint64_t)seconds*1000U;
    }
    j->retry_at[chunk]=add_deadline(now,*delay);
    j->recovery_class=(rc==429||rc==503)?GS_FAILURE_COOLDOWN:tls?GS_FAILURE_TLS:GS_FAILURE_TRANSPORT;
    j->recovery_deadline=j->retry_at[chunk];
    snprintf(j->recovery_detail,sizeof(j->recovery_detail),"%s; retrying in %llu s",
        tls?"Reconnecting after TLS trust failure":rc==429||rc==503?"Provider cooldown":"Reconnecting after transport interruption",
        (unsigned long long)((*delay+999)/1000));

    // An isolated failure retires only its connection. Healthy lanes can keep
    // claiming work; the failed chunk remains eligible after its own delay.
    if(!j->failure_window || now-j->failure_window>10000) {
        j->failure_window=now;j->failed_lanes=0;
    }
    // A single bad chunk can move between workers on retry; that is not a burst.
    if(j->attempts[chunk]==1)j->failed_lanes|=1U<<lane;
    unsigned count=0;for(unsigned mask=j->failed_lanes;mask;mask>>=1)count+=mask&1U;
    unsigned threshold=(unsigned)(j->limit+1)/2;if(threshold<3)threshold=3;
    int server=rc==429||rc==503;
    int burst=count>=threshold;
    const char *scope=server?"server":burst?"burst":"chunk";
    if(server || burst) {
        // Simultaneous failures must not repeatedly halve the same allowance.
        if(now>=j->backoff_until) {
            j->limit=server?j->limit/2:j->limit-(j->limit+3)/4;
            if(j->limit<1)j->limit=1;
            // A throttling reply has its own cooldown and a stalled read is not a
            // refusal; a burst of resets marks the most connections this server holds.
            if(!server&&rc!=-16)hold_at(j,j->limit,now,1);
            j->clean_chunks=0;j->recovery_at=j->backoff_until=now+10000;
            j->failed_lanes=0;j->failure_window=now;
        }
        uint64_t until=now+(server?*delay:250U);
        if(j->next_retry<until)j->next_retry=until;
    }
    return scope;
}
static int recover_stream(Lane *lane,GsJobState *j,int rc,unsigned attempt,uint64_t offset,int streamed)
{
    GsHttp *h=&lane->http;
    if(rc==429||rc==503) {
        uint64_t now=gs_clock();unsigned seconds=h->retry_after>0?(unsigned)h->retry_after:15U;
        if(seconds>GS_RETRY_AFTER_MAX_SECONDS)seconds=GS_RETRY_AFTER_MAX_SECONDS;
        const char *origin=h->origin[0]?h->origin:j->origin;
        gs_lock(&gate);
        if(h->origin[0])snprintf(j->origin,sizeof(j->origin),"%s",h->origin);
        origin_backoff_locked(origin,add_deadline(now,(uint64_t)seconds*1000U));
        gs_unlock(&gate);
        // Keep the partial chunk uncommitted and let the scheduler lend this
        // lane to another origin while the throttled origin cools down.
        gs_http_close(h);return 0;
    }
    int retryable=rc<0 && rc!=-3 && rc!=-10 && rc!=-12 && rc!=-13;
    retryable|=rc==408||rc==425||rc==429||rc==500||rc==502||rc==503||rc==504;
    if(!retryable || attempt>8 || j->single)return 0;
    uint64_t now=gs_clock();unsigned shift=attempt-1;if(shift>5)shift=5;
    uint64_t delay=250U<<shift;
    delay+=(unsigned)((now^(uintptr_t)lane^(offset>>9))%201U);
    if(delay>60000)delay=60000;
    gs_lock(&gate);
    int old_limit=j->limit;int burst=0;
    j->status.retries++;
    if((unsigned)rc==0x8095F00CU)note_tls_failure(j,(unsigned)(lane-lanes));
    // Waiting behind other lanes for a local connection-setup permit (-15) says
    // nothing about the provider, so it never shrinks the lane budget. Neither
    // does the first drop of a body that was already streaming on this socket:
    // CDNs recycle long-lived connections, often several in the same instant,
    // and the reconnect normally succeeds at once. Counting those drops as
    // provider distress ratcheted a 25-lane allowance down to four and kept
    // pushing its recovery out. A reconnect that fails again (attempt 2+), a
    // stalled read (-16) and every setup failure still count.
    int recycled=streamed&&attempt==1&&rc!=-16;
    int provider_failure=rc!=-15&&!recycled;
    // A recovered body is still a recent connection failure. Require new clean
    // data and a quiet interval before adding another lane, even when isolated.
    if(provider_failure){j->clean_chunks=0;j->recovery_at=now+10000;}
    if(!j->failure_window || now-j->failure_window>10000){j->failure_window=now;j->stream_failed_lanes=0;}
    if(provider_failure)j->stream_failed_lanes|=1U<<(unsigned)(lane-lanes);
    unsigned failed=0;for(unsigned mask=j->stream_failed_lanes;mask;mask>>=1)failed+=mask&1U;
    // A single dropped body belongs to its socket. Reduce the job's budget only
    // when distinct lanes fail in a short interval, then recover on clean data.
    // With 25 lanes a few independent socket drops are routine, so the burst
    // threshold scales with the current allowance (3 lanes at ten, 7 at 25).
    unsigned burst_threshold=(unsigned)(j->limit+3)/4;if(burst_threshold<3)burst_threshold=3;
    if(provider_failure && failed>=burst_threshold && now>=j->backoff_until) {
        burst=1;if(j->limit>2)j->limit--;
        // Several connections reset together: the server will not hold that
        // many. Hold the reduced level for this server instead of climbing
        // straight back into the same resets 10 s later. A stalled read is not
        // a refusal and keeps the plain reduction.
        if(rc!=-16)hold_at(j,j->limit,now,1);
        j->clean_chunks=0;j->backoff_until=j->recovery_at=now+10000;j->stream_failed_lanes=0;
    }
    // A lane beyond a reduced allowance returns its span instead of waiting out
    // a backoff, so the lanes still streaming claim its unread chunks at once.
    // A body EOF (-11) keeps its bounded retries: the worker fails the job on it.
    int shed=provider_failure&&rc!=-11&&j->active>j->limit;
    int new_limit=j->limit,ceiling=j->ceiling;unsigned hold=j->ceiling_hold;gs_unlock(&gate);
    char line[768];
    snprintf(line,sizeof(line),"ms=%llu api=3 revision=supervised-streams-1 event=stream-retry handle=%d lane=%u attempt=%u hex=0x%08X http=%d stage=%s offset=%llu delay_ms=%llu scope=%s ssl=0x%08X verify=0x%X errno=0x%08X host=%s\n",
        (unsigned long long)now,j->handle,(unsigned)(lane-lanes),attempt,(unsigned)rc,h->status,h->stage?h->stage:"open",
        (unsigned long long)offset,(unsigned long long)delay,burst?"burst":"stream",(unsigned)h->ssl_error,h->ssl_verify,(unsigned)h->native_errno,strstr(h->origin,"://")?strstr(h->origin,"://")+3:"unknown");
    log_line(j->dest,line);
    snprintf(line,sizeof(line),"ms=%llu event=lane-budget handle=%d before=%d after=%d scope=%s ceiling=%d hold_ms=%u shed=%d\n",(unsigned long long)now,j->handle,old_limit,new_limit,burst?"burst":"stream",ceiling,hold,shed);log_line(j->dest,line);
    gs_http_close(h);
    if(shed)return 0;
    uint64_t until=now+delay;
    for(;;) {
        if(__atomic_load_n(&j->stop,__ATOMIC_ACQUIRE))return 0;
        uint64_t shared=origin_cooldown(h->origin[0]?h->origin:j->origin);
        if(shared>until)until=shared;
        if(gs_clock()>=until)return 1;
        gs_sleep(25);
    }
}
static uint64_t begin_io(Lane *lane,int kind)
{
    gs_lock(&gate);uint64_t now=gs_clock();lane->io_kind=kind;lane->io_since=now?now:1;gs_unlock(&gate);
    return now;
}
static int chunk_transfer(Lane *lane,GsJobState *j,uint32_t index,unsigned char digest[32])
{
    uint64_t start=j->single?0:(uint64_t)index*GS_XFER_CHUNK;
    uint64_t length=j->single?j->header.total:j->header.total-start;
    if(!j->single&&length>(uint64_t)lane->span*GS_XFER_CHUNK)length=(uint64_t)lane->span*GS_XFER_CHUNK;
    uint64_t done=0;
    unsigned failures=0;
    // streamed: this socket delivered body bytes since it was opened.
    // stale_retried: the free replay for a kept-alive socket was used.
    int streamed=0,stale_retried=0;
    sha256_init(&lane->hash);
    GsHttp *h=&lane->http;
reopen:
    streamed=0;
    if(__atomic_load_n(&j->stop,__ATOMIC_ACQUIRE))return -3;
    // Keep the hash and accepted bytes alive while replacing a failed connection.
    // Only the unread tail is requested; completed chunks remain checkpointable.
    gs_lock(&gate);uint64_t cooldown=origin_cooldown_locked(j->origin);gs_unlock(&gate);
    while(gs_clock()<cooldown) {
        if(__atomic_load_n(&j->stop,__ATOMIC_ACQUIRE))return -3;
        gs_sleep(25);gs_lock(&gate);cooldown=origin_cooldown_locked(j->origin);gs_unlock(&gate);
    }
    uint64_t before;
#ifdef GS_HOST_TEST
    extern void gs_test_before_http_open(int handle);
    gs_test_before_http_open(j->handle);
#endif
    // Mirror networks (Internet Archive) redirect each request to any replica
    // node. Pin every lane to the node the probe validated; an expired pin
    // falls back to the original URL inside gs_http_open.
    if(!h->effective[0]&&j->effective[0]&&strcmp(j->effective,j->url)) {
        snprintf(h->effective,sizeof(h->effective),"%s",j->effective);snprintf(h->source,sizeof(h->source),"%s",j->url);
    }
    before=begin_io(lane,GS_IO_OPEN);
    int rc=gs_http_open(h,j->url,j->bearer,j->single?-1:(int64_t)(start+done),(int64_t)(start+length-1),j->single?NULL:j->if_range);
    gs_lock(&gate);lane->io_since=0;j->request_ms+=gs_clock()-before;gs_unlock(&gate);
    if(rc)goto interrupted;
    if(!h->reused) {
        char transport[768];
        snprintf(transport,sizeof(transport),"ms=%llu event=http-receive-buffer handle=%d lane=%u host=%s bytes=%d rc=0x%08X interrupted_calls=%u\n",
            (unsigned long long)gs_clock(),j->handle,(unsigned)(lane-lanes),strstr(h->origin,"://")?strstr(h->origin,"://")+3:"unknown",h->recv_block,(unsigned)h->recv_block_rc,h->interruptions);
        log_line(j->dest,transport);
    }
    h->stage="validate-headers";
    if(h->status!=200&&h->status!=206){rc=h->status?h->status:-10;goto interrupted;}
    if(j->single) {if(h->status!=200 || (h->length>=0 && (uint64_t)h->length!=length))return -10;}
    else if(h->status!=206||h->start!=(int64_t)(start+done)||h->end!=(int64_t)(start+length-1)||h->total!=(int64_t)j->header.total||
            (h->length>=0 && h->length!=(int64_t)(length-done)))return -10;
    // A strong ETag plus the exact total proves the representation on any
    // replica a mirror redirects to; another replica may tag the same bytes
    // differently, so a mismatch there is retried re-pinned to the probed node
    // (-17), and only a mismatch on that node proves a changed file (-13).
    // Weaker proofs stay bound to the probed URL unless every byte is verified
    // at the end (publisher SHA-256 or PKG payload digests).
    if(!j->single) {
        int other_replica=j->effective[0]&&strcmp(h->effective[0]?h->effective:j->url,j->effective);
        if(j->etag[0]&&h->etag_present&&(!h->etag_single||strcmp(h->etag_value,j->etag)))return other_replica?-17:-13;
        if(!j->etag[0]&&other_replica&&!j->expected[0]&&!j->pkg_integrity)return -13;
        if(j->last_modified[0]&&h->last_modified_present&&
           (!h->last_modified_single||!h->last_modified_valid||strcmp(h->last_modified,j->last_modified)))return -13;
    }
    while(done<length) {
        if(__atomic_load_n(&j->stop,__ATOMIC_ACQUIRE))return -3;
        if(wait_write(lane,j,0))return -12;
        unsigned slot=lane->producer;unsigned char *buffer=lane->buffer+slot*WRITE_BLOCK;
        size_t want=(size_t)(length-done);if(want>WRITE_BLOCK)want=WRITE_BLOCK;
        if(!j->single) {
            uint64_t remaining_chunk=GS_XFER_CHUNK-done%GS_XFER_CHUNK;
            if(want>remaining_chunk)want=(size_t)remaining_chunk;
        }
        size_t filled=0;rc=0;
        while(filled<want) {
            if(__atomic_load_n(&j->stop,__ATOMIC_ACQUIRE))return -3;
            h->stage="read";unsigned interruptions=h->interruptions;
            unsigned read_size=(unsigned)(want-filled);if(read_size>READ_BLOCK)read_size=READ_BLOCK;
            before=begin_io(lane,GS_IO_READ);
            int n=gs_http_read(h,buffer+filled,read_size);uint64_t read_ms=gs_clock()-before;
            gs_lock(&gate);lane->io_since=0;j->read_ms+=read_ms;j->read_calls++;j->read_interruptions+=h->interruptions-interruptions;
            if(n>0&&(unsigned)n<=read_size) {
                j->status.network_bytes+=n;
                useful_progress(j,start+done+filled,(uint64_t)n);
#if SSPI_OWNER_DEBUG
                owner_metrics[j-jobs].network_progress_at=before+read_ms;
                lane->owner_filling=filled+(unsigned)n;owner_buffer_peak(j);
#endif
            }gs_unlock(&gate);
            if(n<=0){rc=n<0?n:-11;break;}if((unsigned)n>read_size)return -10;
            filled+=(unsigned)n;lane->received+=n;failures=0;streamed=1;
        }
        if(!filled)goto interrupted;
        gs_lock(&gate);
        lane->writes[slot].offset=start+done;lane->writes[slot].length=filled;
#if SSPI_OWNER_DEBUG
        lane->owner_filling=0;
#endif
        lane->writes[slot].state=1;lane->producer^=1;gs_unlock(&gate);
        done+=filled;
        if(!j->single && done<length && done%GS_XFER_CHUNK==0) {
            if(wait_write(lane,j,1))return -12;
            gs_lock(&gate);
            if(!j->stop) {
                uint32_t completed=index+(uint32_t)(done/GS_XFER_CHUNK)-1;
                sha256_final(&lane->hash,j->chunks[completed].hash);sha256_init(&lane->hash);
                j->chunks[completed].done=1;j->status.done+=GS_XFER_CHUNK;lane->written-=GS_XFER_CHUNK;
                j->dirty++;clean_chunk(j);
                if(j->dirty>=32||gs_clock()-j->checkpoint_at>=15000)checkpoint(j);
            }
            gs_unlock(&gate);
        }
        if(rc)goto interrupted;
    }
    if(wait_write(lane,j,1))return -12;
    h->stage="eof";begin_io(lane,GS_IO_EOF);int extra=gs_http_read(h,lane->buffer,1);
    gs_lock(&gate);lane->io_since=0;gs_unlock(&gate);
    if(extra!=0){rc=extra<0?extra:-10;goto interrupted;}
    sha256_final(&lane->hash,digest);return 0;
interrupted:
    // A watchdog abort is a stalled socket, not a user stop: clear the abort and
    // reconnect the unread tail through the normal stream-recovery backoff.
    if(__atomic_exchange_n(&lane->stalled,0,__ATOMIC_ACQ_REL) && !__atomic_load_n(&j->stop,__ATOMIC_ACQUIRE)) {
        gs_http_clear_abort(h);rc=-16;
    }
    // A kept-alive socket the server already closed fails on its first use,
    // before any status arrives. gs_http_open keeps the connection cached after
    // a send failure, so close it and replay the GET once at once on a fresh
    // one, without backoff or a budget penalty. A second failure takes the
    // normal recovery path.
    if(!stale_retried && rc<0 && rc!=-1 && rc!=-3 && rc!=-15 && rc!=-16 && h->reused && !h->status && h->stage &&
       (!strcmp(h->stage,"send")||!strcmp(h->stage,"status")||!strcmp(h->stage,"response-headers")) &&
       done<length && !__atomic_load_n(&j->stop,__ATOMIC_ACQUIRE)) {
        stale_retried=1;
        char line[320];
        snprintf(line,sizeof(line),"ms=%llu event=stale-connection-replay handle=%d lane=%u hex=0x%08X stage=%s offset=%llu\n",
            (unsigned long long)gs_clock(),j->handle,(unsigned)(lane-lanes),(unsigned)rc,h->stage,(unsigned long long)(start+done));
        log_line(j->dest,line);
        gs_http_close(h);
        goto reopen;
    }
    if(done<length && recover_stream(lane,j,rc,++failures,start+done,streamed))goto reopen;
    return rc;
}
// Poll-driven stall supervisor. sceHttp reads block until the firmware receive
// timeout, so a provider that stops sending would freeze a lane (and an open that
// holds the shared request permit) for that long. Abort only a call that made no
// progress; its lane reconnects the unread tail and the governor adapts.
static unsigned stall_watchdog_locked(GsJobState *j,char *line,size_t capacity)
{
    unsigned aborted=0;uint64_t now=gs_clock();line[0]=0;
    if(j->stop||j->status.state!=GS_DOWNLOADING)return 0;
    for(int i=0;i<GS_XFER_LANES;i++) {
        Lane *lane=&lanes[i];
        if(lane->job<0||&jobs[lane->job]!=j||__atomic_load_n(&lane->stalled,__ATOMIC_ACQUIRE))continue;
        uint64_t since=lane->io_since;
        if(!since)continue;
        int reading=lane->io_kind!=GS_IO_OPEN;
        // This clock begins only after admission and survives redirect/connection
        // cleanup. Queued time can never consume the active setup allowance.
        if(!reading)since=__atomic_load_n(&lane->http.open_started,__ATOMIC_ACQUIRE);
        if(!since||now<=since)continue;
        uint64_t idle=now-since;
        if(idle<(reading?GS_STALL_READ_MS:GS_STALL_OPEN_MS))continue;
        __atomic_store_n(&lane->stalled,1,__ATOMIC_RELEASE);gs_http_abort(&lane->http);aborted++;
        if(!line[0])snprintf(line,capacity,"ms=%llu event=stall-abort handle=%d lane=%d stage=%s idle_ms=%llu limit=%d target=%d\n",
            (unsigned long long)now,j->handle,i,reading?(lane->io_kind==GS_IO_EOF?"eof":"read"):"open",(unsigned long long)idle,j->limit,j->target_limit);
    }
    return aborted;
}
static void apply_resume(GsJobState *j)
{
    if(j->resume_requested && j->status.state==GS_PAUSED && j->stop==1 &&
       j->chunks && !j->active && !j->finishing && !j->preparing && !j->checkpointing && !j->checkpoint_waiters) {
        j->resume_requested=0;__atomic_store_n(&j->stop,0,__ATOMIC_RELEASE);j->status.state=GS_DOWNLOADING;
        // Paused time is not a speed sample: judge the next raise afresh.
        j->rate_at=real_clock();j->rate_bytes=j->useful_bytes;j->probe_from=0;
    }
}
static int claim(int *job,uint32_t *chunk,unsigned *span)
{
    uint64_t now=gs_clock();
    for(int k=0;k<GS_XFER_JOBS;k++) {
        int i=(turn+k)%GS_XFER_JOBS;GsJobState *j=&jobs[i];
        apply_resume(j);
        if(!j->handle||j->status.state!=GS_DOWNLOADING||j->stop||j->finishing||j->preparing)continue;
        // A pause can interrupt verification after every chunk is durable.
        if(!j->active&&!j->checkpointing&&!j->checkpoint_waiters&&j->status.total>0&&j->status.done==j->status.total) {
            j->finishing=1;j->status.state=GS_VALIDATING;*job=i;gs_transfer_log(j,"validating");return 2;
        }
        if(now<origin_cooldown_locked(j->origin)||j->active>=available_limit(j)||!source_available(j)||host_active(j)>=GS_XFER_LANES||total_active()>=GS_XFER_LANES||now<j->next_retry)continue;
        // A server ignoring ranges has one whole-file retry budget. Scanning
        // other map slots would restart byte zero before slot zero's delay.
        uint32_t count=j->single?1:j->header.count;
        for(uint32_t c=0;c<count;c++)if(!j->chunks[c].done&&!j->claims[c]&&now>=j->retry_at[c]) {
            if(j->single && j->active)break;
            unsigned wanted=j->single?1:(j->header.count+(unsigned)j->target_limit-1)/(unsigned)j->target_limit;
            if(wanted>8)wanted=8; // Up to 128 MiB per request; still commit/hash each 16 MiB chunk.
            // A claimed span is never re-split. Near the end of a file, a lane
            // that took a full span while the others were almost done fetched
            // its last ~100 MiB alone (~9 MB/s) with the allowance idle. Once
            // less than a full round of spans is unclaimed, size the claim to
            // an even share of the work still outstanding (unclaimed plus held
            // chunks) whenever that share is under half a span. Even splits and
            // mid-file claims keep their full request size.
            if(!j->single&&wanted>1) {
                unsigned share=(unsigned)(j->limit>0?j->limit:1),open=0,held=0;
                for(uint32_t k=c;k<j->header.count&&open<wanted*share;k++)open+=!j->chunks[k].done&&!j->claims[k];
                if(open<wanted*share) {
                    for(uint32_t k=0;k<j->header.count;k++)held+=j->claims[k]&&!j->chunks[k].done;
                    unsigned balanced=(open+held+share-1)/share;if(balanced<1)balanced=1;
                    if(balanced*2<wanted)wanted=balanced;
                }
            }
            *span=1;
            while(*span<wanted && c+*span<j->header.count && !j->chunks[c+*span].done && !j->claims[c+*span] && now>=j->retry_at[c+*span])(*span)++;
            for(unsigned n=0;n<*span;n++)j->claims[c+n]=1;
            j->active++;j->status.lanes=j->active;*job=i;*chunk=c;turn=(i+1)%GS_XFER_JOBS;return 1;
        }
    }return 0;
}
// Lane buffers are owned by their worker thread. The writer and hash threads
// only touch a buffer while its lane owns a job, and a lane drains both before
// releasing that job, so an idle worker may free its own buffer.
static int lane_buffer_acquire(Lane *lane)
{
    if(lane->buffer)return 0;
    unsigned char *buffer=malloc(LANE_BUFFER_BYTES);
    if(!buffer)return -1;
    lane->buffer=buffer;__atomic_add_fetch(&allocated_lane_buffers,1,__ATOMIC_RELAXED);
    return 0;
}
static void lane_buffer_release(Lane *lane)
{
    if(!lane->buffer)return;
    free(lane->buffer);lane->buffer=NULL;__atomic_sub_fetch(&allocated_lane_buffers,1,__ATOMIC_RELAXED);
}
static void *worker(void *argument)
{
    background_thread("reader");Lane *lane=argument;gs_http_reset(&lane->http);lane->job=-1;lane->idle_since=gs_clock();
    unsigned idle_ms=LANE_IDLE_POLL_MS;
    while(!__atomic_load_n(&shutting_down,__ATOMIC_ACQUIRE)) {
        if(gs_clock()<lane->retry_after){gs_sleep(20);continue;}
        int slot=-1;uint32_t chunk=0;gs_lock(&gate);int found=claim(&slot,&chunk,&lane->span);if(found==1){lane->job=slot;lane->written=lane->received=0;__atomic_store_n(&lane->stalled,0,__ATOMIC_RELEASE);gs_http_clear_abort(&lane->http);}gs_unlock(&gate);
        if(!found) {
            // Lane 0 keeps its buffer so one transfer can always progress.
            if(lane!=lanes&&lane->buffer&&gs_clock()-lane->idle_since>=LANE_IDLE_RELEASE_MS)lane_buffer_release(lane);
            // 25 idle lanes polling every 20 ms woke the host (SceShellUI for the
            // resident) about 1,250 times a second. Back off to a bounded interval;
            // a new job is still claimed within LANE_IDLE_MAX_POLL_MS.
            gs_sleep(idle_ms);
            idle_ms=idle_ms*2>LANE_IDLE_MAX_POLL_MS?LANE_IDLE_MAX_POLL_MS:idle_ms*2;
            continue;
        }
        idle_ms=LANE_IDLE_POLL_MS;
        if(lane_buffer_acquire(lane)) {
            // Under memory pressure, return the claim untouched. Lanes that
            // already hold a buffer (always lane 0) continue the transfer.
            char line[256],destination[1024];GsJobState *j=&jobs[slot];
            gs_lock(&gate);
            if(found==2){j->finishing=0;if(j->status.state==GS_VALIDATING)j->status.state=GS_DOWNLOADING;}
            else {for(unsigned n=0;n<lane->span;n++)j->claims[chunk+n]=0;j->active--;j->status.lanes=j->active;lane->job=-1;}
            snprintf(line,sizeof(line),"ms=%llu event=lane-buffer-unavailable handle=%d lane=%u bytes=%u allocated_mib=%u ceiling_mib=%u\n",
                (unsigned long long)gs_clock(),j->handle,(unsigned)(lane-lanes),LANE_BUFFER_BYTES,
                (unsigned)(__atomic_load_n(&allocated_lane_buffers,__ATOMIC_RELAXED)*LANE_BUFFER_BYTES/(1024U*1024U)),(unsigned)BUFFER_CEILING_MIB);
            snprintf(destination,sizeof(destination),"%s",j->dest);
            gs_unlock(&gate);log_line(destination,line);
            lane->retry_after=add_deadline(gs_clock(),LANE_ALLOCATION_RETRY_MS);continue;
        }
        GsJobState *j=&jobs[slot];unsigned char digest[32];
        if(found==2)goto validate;
        if(lane->source_handle!=j->handle || lane->source_generation!=j->verification_retries) {
            gs_http_close(&lane->http);lane->source_handle=j->handle;lane->source_generation=j->verification_retries;
        }
        uint32_t claimed_chunk=chunk;int rc=chunk_transfer(lane,j,claimed_chunk,digest);
        int retry_after=lane->http.retry_after,status=lane->http.status,reused=lane->http.reused;
        int ssl_error=lane->http.ssl_error,native_errno=lane->http.native_errno;
        unsigned ssl_verify=lane->http.ssl_verify,open_wait_ms=lane->http.open_wait_ms;
        const char *stage=lane->http.stage?lane->http.stage:"open";
        char response_origin[512];snprintf(response_origin,sizeof(response_origin),"%s",lane->http.origin[0]?lane->http.origin:j->origin);
        uint64_t range_start=j->single?0:(uint64_t)claimed_chunk*GS_XFER_CHUNK;
        uint64_t range_end=j->single?j->header.total:(uint64_t)(claimed_chunk+lane->span)*GS_XFER_CHUNK;
        if(range_end>j->header.total)range_end=j->header.total;
        uint64_t unread_offset=range_start+(uint64_t)(lane->received<0?0:lane->received);if(unread_offset>range_end)unread_offset=range_end;
        char retry_line[1024]={0},destination[1024];
        if(rc)gs_http_close(&lane->http);
        // Drain even on cancellation/failure before releasing ownership or reusing
        // buffers. Only written, hashed chunks are eligible for the resume map.
        if(wait_write(lane,j,1))rc=-12;
        gs_lock(&gate);lane->job=-1;lane->written=0;lane->write_error=0;j->active--;j->status.lanes=j->active;
#if SSPI_OWNER_DEBUG
        lane->owner_filling=lane->owner_wait_started=0;
#endif
        for(unsigned n=0;n<lane->span;n++)j->claims[claimed_chunk+n]=0;
        unsigned last=claimed_chunk+lane->span-1;
        chunk=claimed_chunk;while(chunk<last && j->chunks[chunk].done)chunk++;
        if(!rc && !j->stop) {
            if(j->single) {
                // A range-ignoring server cannot safely resume. The complete body is
                // verified before publication, and no partial map bits are committed.
                for(uint32_t c=0;c<j->header.count;c++)j->chunks[c].done=1;
                j->status.done=j->status.total;
            } else {
                j->chunks[chunk].done=1;memcpy(j->chunks[chunk].hash,digest,32);
                uint64_t n=j->header.total-(uint64_t)chunk*GS_XFER_CHUNK;j->status.done+=(int64_t)(n>GS_XFER_CHUNK?GS_XFER_CHUNK:n);
                j->accepted[chunk]=n>GS_XFER_CHUNK?GS_XFER_CHUNK:n;
            }
            j->dirty++;clean_chunk(j);
        } else if(rc && !j->stop) {
            j->status.retries++;if(j->attempts[chunk]<255)j->attempts[chunk]++;
            int old_limit=j->limit;uint64_t delay=0;const char *scope="fatal";
            if(rc==401||rc==403||rc==410) {
                char message[160];snprintf(message,sizeof(message),"Provider rejected the signed link with HTTP %d; refresh it and resume",rc);fail(j,rc,message);
            }
            else if(rc==-12)fail(j,rc,"Positioned disk write failed; committed chunks retained");
            else if(rc==-11)fail(j,rc,"Provider repeatedly ended the response before the validated Content-Length; completed chunks retained");
            else if(rc==-10||rc==-13)fail(j,rc,rc==-13?"Package identity validation failed; retained data was not replaced":"Provider returned an invalid range, length, encoding or EOF response");
            else if(rc>=400&&rc<=499&&rc!=408&&rc!=425&&rc!=429) {
                char message[160];snprintf(message,sizeof(message),"Provider returned permanent HTTP %d; choose another source or refresh the link",rc);fail(j,rc,message);
            }
            else {
                uint64_t now=gs_clock();int tls=(unsigned)rc==0x8095F00CU,server=rc==429||rc==503;
                if(tls&&tls_episode_terminal(j,now))
                    fail(j,rc,"Provider TLS certificate not trusted on fresh sessions for two minutes with no useful progress; completed chunks retained");
                else {
                    if((rc==429||rc==503)&&response_origin[0])snprintf(j->origin,sizeof(j->origin),"%s",response_origin);
                    scope=retry_chunk(j,chunk,(unsigned)(lane-lanes),rc,retry_after,&delay);
                    lane->retry_after=server?0:add_deadline(now,30000);
                    if(tls)scope="tls-quarantine";
                    if(rc==429||rc==503)origin_backoff_locked(response_origin,j->retry_at[chunk]);
                }
            }
            snprintf(destination,sizeof(destination),"%s",j->dest);
            snprintf(retry_line,sizeof(retry_line),"ms=%llu api=3 revision=supervised-streams-2 event=retry handle=%d lane=%u claim_chunk=%u failed_chunk=%u attempt=%u raw=%d hex=0x%08X http=%d stage=%s reused=%d claimed_start=%llu range_end=%llu unread_offset=%llu received=%lld delay_ms=%llu scope=%s limit_before=%d limit=%d target=%d ssl=0x%08X verify=0x%X errno=0x%08X open_wait_ms=%u\n",
                (unsigned long long)gs_clock(),j->handle,(unsigned)(lane-lanes),claimed_chunk,chunk,(unsigned)j->attempts[chunk],rc,(unsigned)rc,status,stage,reused,
                (unsigned long long)range_start,(unsigned long long)(range_end-1),(unsigned long long)unread_offset,(long long)lane->received,(unsigned long long)delay,scope,old_limit,j->limit,j->target_limit,
                (unsigned)ssl_error,ssl_verify,(unsigned)native_errno,open_wait_ms);
        }
        if(j->chunks && (j->dirty>=32||gs_clock()-j->checkpoint_at>=15000))checkpoint(j);
        if(!j->active && (j->stop || j->status.done==j->status.total))checkpoint_drain(j);
        int finish=!j->stop&&!j->finishing&&!j->checkpointing&&j->status.done==j->status.total&&!j->active;
        if(finish){j->finishing=1;j->status.state=GS_VALIDATING;gs_transfer_log(j,"validating");}
        if(j->stop) for(int i=0;i<GS_XFER_LANES;i++)if(lanes[i].job==slot)gs_http_abort(&lanes[i].http);
        gs_unlock(&gate);
        if(retry_line[0])log_line(destination,retry_line);
        if(finish) {
validate:
            rc=gs_finalize_file(j,lane->buffer);
            if(!rc && fsync(j->fd))rc=-12;
            gs_lock(&gate);
            if(!j->stop) {
                if(rc==-2 && j->expected[0] && j->verification_retries==0) {
                    char detail[512];snprintf(detail,sizeof(detail),"ms=%llu event=verification-retry handle=%d %s\n",(unsigned long long)gs_clock(),j->handle,j->verification_error);log_line(j->dest,detail);
                    j->verification_retries++;j->status.retries++;j->status.done=0;j->validation_done=0;
                    memset(j->chunks,0,j->header.count*sizeof(GsChunk));memset(j->attempts,0,j->header.count);
                    memset(j->accepted,0,j->header.count*sizeof(uint64_t)); // re-fetched bytes count as progress again
                    memset(j->retry_at,0,j->header.count*sizeof(uint64_t));j->dirty++;checkpoint(j);
                    if(!j->stop)j->status.state=GS_DOWNLOADING;
                }
                else if(rc) {
                    char detail[512];snprintf(detail,sizeof(detail),"ms=%llu event=verification-failed handle=%d code=%d %s\n",(unsigned long long)gs_clock(),j->handle,rc,j->verification_error);log_line(j->dest,detail);
                    fail(j,rc,j->verification_error[0]?j->verification_error:"Final file verification failed");
                }
                else {j->status.state=GS_COMPLETE;gs_transfer_log(j,j->expected[0]?"complete-sha256-verified":"complete-structure-verified");}
            }
            j->finishing=0;gs_unlock(&gate);
        }
        lane->idle_since=gs_clock();
    }
    gs_http_close(&lane->http);return NULL;
}
int sspi_xfer_api(void) {return GS_XFER_API;}
void sspi_xfer_set_module(int module) {gs_http_set_module(module);}
int sspi_xfer_init(int http_context)
{
    gs_lock(&init_gate);if(initialized){gs_unlock(&init_gate);return 0;}
    int rc=gs_http_init(http_context);if(rc){gs_unlock(&init_gate);return rc;}
    shutting_down=writer_stopping=checkpoint_stopping=0;
    memset(origin_cooldowns,0,sizeof(origin_cooldowns));
    for(int i=0;i<GS_XFER_LANES;i++) {
        memset(&lanes[i],0,sizeof(lanes[i]));lanes[i].job=-1;
        gs_http_reset(&lanes[i].http);
    }
    // Only lane 0 is allocated up front; other lanes allocate when they claim
    // work and release after LANE_IDLE_RELEASE_MS without any.
    allocated_lane_buffers=0;
    if(lane_buffer_acquire(&lanes[0]))goto init_failed;
    if(gs_thread_start(&writer_thread,disk_writer,NULL))goto init_failed;
    writer_started=1;
    if(gs_thread_start(&checkpoint_thread,checkpoint_worker,NULL))goto init_failed;
    checkpoint_started=1;
    for(int i=0;i<2;i++){if(gs_thread_start(&hash_threads[i],hash_worker,NULL))goto init_failed;hash_started++;}
    for(int i=0;i<GS_XFER_LANES;i++) {
        if(gs_thread_start(&lanes[i].thread,worker,&lanes[i]))goto init_failed;
        lanes[i].started=1;
    }
    initialized=1;gs_unlock(&init_gate);return 0;
init_failed:
    __atomic_store_n(&shutting_down,1,__ATOMIC_RELEASE);
    for(int i=0;i<GS_XFER_LANES;i++)if(lanes[i].started){gs_thread_join(lanes[i].thread);lanes[i].started=0;}
    gs_lock(&gate);checkpoint_stopping=1;gs_unlock(&gate);
    if(checkpoint_started){gs_thread_join(checkpoint_thread);checkpoint_started=0;}
    __atomic_store_n(&writer_stopping,1,__ATOMIC_RELEASE);
    if(writer_started){gs_thread_join(writer_thread);writer_started=0;}
    for(int i=0;i<hash_started;i++)gs_thread_join(hash_threads[i]);hash_started=0;
    for(int i=0;i<GS_XFER_LANES;i++)lane_buffer_release(&lanes[i]);
    gs_unlock(&init_gate);return -4;
}
static int preparation_transient(int rc)
{
    return (rc<0&&rc!=-3&&rc!=-10&&rc!=-12&&rc!=-13)||rc==408||rc==425||rc==429||rc==500||rc==502||rc==503||rc==504;
}
static void *prepare_job(void *argument)
{
    GsJobState *j=argument;background_thread("prepare");unsigned round=0;gs_http_reset(&j->preparation_http);
restart:
    unsigned char header[0x1000],identity[32],source_identity[32];size_t got=0;
    char preparation_error[220]={0},probe_origin[512]={0},probe_etag[512]={0},probe_effective[8192]={0},probe_if_range[512]={0},probe_last_modified[64]={0};
    const char *probe_stage="response";int rc=0,complete=0,store_opened=0,probe_validator_kind=0,probe_etag_present=0,probe_range_unsupported=0;
    int probe_etag_usable=0,probe_date_present=0,probe_date_valid=0,probe_modified_present=0,probe_modified_valid=0;
    int64_t probe_date_modified_gap=-1;
    // A resumed preparation rechecks every retained chunk from the beginning.
    // Its prior partial accounting and representation metadata are not a new map.
    gs_lock(&gate);j->status.done=0;j->validation_done=0;gs_unlock(&gate);
    memset(&j->header,0,sizeof(j->header));
    for(;;) {
        for(;;) {
            gs_lock(&gate);uint64_t now=gs_clock();
            if(j->stop||__atomic_load_n(&shutting_down,__ATOMIC_ACQUIRE)){gs_unlock(&gate);rc=-3;goto prepared;}
            uint64_t cooldown=origin_cooldown_locked(j->origin);
            if(now>=cooldown&&source_available(j)&&host_active(j)<GS_XFER_LANES&&total_active()<GS_XFER_LANES) {
                gs_http_clear_abort(&j->preparation_http);
                j->active=1;j->recovery_class=GS_FAILURE_NONE;j->recovery_detail[0]=0;gs_unlock(&gate);break;
            }
            j->recovery_class=now<cooldown?GS_FAILURE_COOLDOWN:GS_FAILURE_ADMISSION;
            j->recovery_deadline=now<cooldown?cooldown:add_deadline(now,1000);
            snprintf(j->recovery_detail,sizeof(j->recovery_detail),"%s",now<cooldown?"Waiting for provider cooldown":"Waiting for transfer admission");
            gs_unlock(&gate);gs_sleep(25);
        }
        got=0;j->single=0;j->pkg_integrity=0;probe_if_range[0]=probe_last_modified[0]=0;probe_validator_kind=probe_etag_present=probe_range_unsupported=0;
        probe_etag_usable=probe_date_present=probe_date_valid=probe_modified_present=probe_modified_valid=0;probe_date_modified_gap=-1;
        memset(header,0,sizeof(header));probe_stage="response";
        if(j->stop){rc=-3;}else rc=gs_http_open(&j->preparation_http,j->url,j->bearer,0,0xfff,NULL);
        GsHttp *h=&j->preparation_http;
        if(!rc) {
            if(h->status==206&&h->start==0&&h->end==0xfff&&h->total>=0x1000&&(h->length<0||h->length==0x1000))j->status.total=h->total;
            else if(h->status==200&&h->length>=0x1000){j->status.total=h->length;j->single=1;}
            else rc=h->status>=400?h->status:-10;
            if(!rc) {
                probe_validator_kind=gs_http_range_validator(h,probe_if_range,sizeof(probe_if_range));
                if(probe_validator_kind==2)snprintf(probe_last_modified,sizeof(probe_last_modified),"%s",h->last_modified);
                if(!probe_validator_kind&&h->status==206&&immutable_debrid_link(h->effective[0]?h->effective:j->url))probe_validator_kind=3;
                if(!h->etag[0]&&!j->expected[0]&&probe_validator_kind!=2&&probe_validator_kind!=3)j->single=1;
                probe_etag_present=h->etag_present;probe_etag_usable=h->etag[0]!=0;
                probe_date_present=h->date_present;probe_date_valid=h->date_valid;
                probe_modified_present=h->last_modified_present;probe_modified_valid=h->last_modified_valid;
                if(h->date_valid&&h->last_modified_valid)probe_date_modified_gap=h->date_epoch-h->last_modified_epoch;
                probe_range_unsupported=h->status==200;
            }
        }
        if(!rc)probe_stage="header read";
        while(!rc&&got<sizeof(header)){if(j->stop){rc=-3;break;}int n=gs_http_read(h,header+got,(unsigned)(sizeof(header)-got));if(n<=0||(size_t)n>sizeof(header)-got)rc=n<0?n:-11;else got+=(size_t)n;}
        if(!rc&&!j->single){probe_stage="range completion";unsigned char extra;int n=gs_http_read(h,&extra,1);if(n)rc=n<0?n:-10;}
        int status=h->status,retry_after=h->retry_after,ssl=h->ssl_error,native_errno=h->native_errno;
        unsigned ssl_verify=h->ssl_verify;
        snprintf(probe_origin,sizeof(probe_origin),"%s",h->origin);snprintf(probe_etag,sizeof(probe_etag),"%s",h->etag);snprintf(probe_effective,sizeof(probe_effective),"%s",h->effective);
        const char *native_stage=h->stage?h->stage:"unknown";
        gs_http_abort(h);gs_http_close(h);
        gs_lock(&gate);j->active=0;
        if(probe_origin[0])snprintf(j->origin,sizeof(j->origin),"%s",probe_origin);
        if((unsigned)rc==0x8095F00CU)note_tls_failure(j,round%GS_XFER_LANES);
        if(status==429||status==503) {
            unsigned seconds=retry_after>0?(unsigned)retry_after:15U;if(seconds>GS_RETRY_AFTER_MAX_SECONDS)seconds=GS_RETRY_AFTER_MAX_SECONDS;
            uint64_t delay=(uint64_t)seconds*1000U;origin_backoff_locked(probe_origin,add_deadline(gs_clock(),delay));
        }
        int stop=j->stop||__atomic_load_n(&shutting_down,__ATOMIC_ACQUIRE);
        gs_unlock(&gate);if(stop){rc=-3;goto prepared;}
        if(!rc)break;
        snprintf(preparation_error,sizeof(preparation_error),"Package identity probe failed at %s/%s (HTTP %d, errno %d, TLS 0x%08X)",probe_stage,native_stage,status,native_errno,(unsigned)ssl);
        char diagnostic[1024];
        snprintf(diagnostic,sizeof(diagnostic),"ms=%llu event=preparation-probe-failed handle=%d attempt=%u hex=0x%08X http=%d phase=%s stage=%s ssl=0x%08X verify=0x%X errno=0x%08X host=%s\n",
            (unsigned long long)gs_clock(),j->handle,round<UINT32_MAX?round+1:round,(unsigned)rc,status,
            probe_stage,native_stage,(unsigned)ssl,ssl_verify,(unsigned)native_errno,
            strstr(probe_origin,"://")?strstr(probe_origin,"://")+3:"unknown");
        log_line(j->dest,diagnostic);
        if(!preparation_transient(rc))goto prepared;
        gs_lock(&gate);uint64_t now=gs_clock();
        if((unsigned)rc==0x8095F00CU&&tls_episode_terminal(j,now)){gs_unlock(&gate);goto prepared;}
        if(round<UINT32_MAX)round++;
        unsigned seconds=retry_after>0?(unsigned)retry_after:0;if(seconds>GS_RETRY_AFTER_MAX_SECONDS)seconds=GS_RETRY_AFTER_MAX_SECONDS;
        uint64_t delay=seconds?(uint64_t)seconds*1000U:retry_delay(round,status==500||status==502||status==504?250U:1000U);
        j->recovery_class=status==429||status==503?GS_FAILURE_COOLDOWN:(unsigned)rc==0x8095F00CU?GS_FAILURE_TLS:GS_FAILURE_TRANSPORT;
        j->recovery_deadline=add_deadline(now,delay);
        snprintf(j->recovery_detail,sizeof(j->recovery_detail),"%s during source probe; retrying in %llu s",
            (unsigned)rc==0x8095F00CU?"TLS trust failure":status==429||status==503?"Provider cooldown":"Transport interruption",
            (unsigned long long)((delay+999)/1000));
        j->status.retries++;uint64_t until=j->recovery_deadline;gs_unlock(&gate);
        while(gs_clock()<until&&!j->stop)gs_sleep(25);
    }
    if(gs_verify_header(header,got,j->status.total,j->title,j->content)<0){rc=-13;snprintf(preparation_error,sizeof(preparation_error),"Package header failed size, title or content verification");goto prepared;}
    int pkg_capable=!probe_range_unsupported&&gs_pkg_integrity_header(header,got,j->status.total);
    if(j->single&&pkg_capable) {
        j->pkg_integrity=1;j->single=0;
    }
    j->pkg_capable=pkg_capable&&!j->single;
    // Archive magic alone proves no payload checksum: RAR5 permits entries
    // without CRC32/BLAKE2. Such sources retain the whole-file stream unless
    // HTTP validators or a supplied SHA-256 bind their ranges.
    gs_digest(header,sizeof(header),identity);representation_identity(j->url,j->url,probe_last_modified,source_identity);
    snprintf(j->etag,sizeof(j->etag),"%s",probe_etag);snprintf(j->if_range,sizeof(j->if_range),"%s",probe_if_range);
    snprintf(j->effective,sizeof(j->effective),"%s",probe_effective);
    snprintf(j->last_modified,sizeof(j->last_modified),"%s",probe_last_modified);
    rc=gs_store_open(j,identity,source_identity,probe_etag);store_opened=!rc;
    if(rc==-2&&j->stop)rc=-3;
    complete=!rc&&j->status.done==j->status.total;
    if(complete){unsigned char *buffer=malloc(GS_XFER_BUFFER);rc=buffer?gs_finalize_file(j,buffer):-4;if(!buffer)snprintf(preparation_error,sizeof(preparation_error),"Not enough memory to verify the staged package");free(buffer);}
    if(complete&&rc==-2&&j->expected[0]) {
        char detail[512];snprintf(detail,sizeof(detail),"ms=%llu event=verification-retry handle=%d resumed=1 %s\n",(unsigned long long)gs_clock(),j->handle,j->verification_error);log_line(j->dest,detail);
        j->verification_retries=1;j->status.retries++;j->status.done=0;j->validation_done=0;
        memset(j->chunks,0,j->header.count*sizeof(GsChunk));memset(j->accepted,0,j->header.count*sizeof(uint64_t));j->dirty++;complete=0;
        rc=gs_store_checkpoint(j)?-12:0;
    }
prepared:
    // Keep preparation ownership until incomplete store setup has released its
    // arrays and descriptors; resume must never observe those temporary chunks.
    if(!store_opened&&(j->fd>=0||j->lock_fd>=0||j->chunks))gs_store_close(j);
    gs_lock(&gate);j->active=0;
    int stopping=j->stop||__atomic_load_n(&shutting_down,__ATOMIC_ACQUIRE);
    int restart=stopping&&j->stop==1&&j->resume_requested&&!__atomic_load_n(&shutting_down,__ATOMIC_ACQUIRE);
    if(restart){j->resume_requested=0;__atomic_store_n(&j->stop,0,__ATOMIC_RELEASE);j->status.state=GS_QUEUED;j->status.error_code=0;j->status.error[0]=0;}
    else if(stopping) { /* control() already published PAUSED or CANCELED */ }
    else if(rc) {
        const char *reason=j->storage_error[0]?j->storage_error:j->verification_error[0]?j->verification_error:preparation_error[0]?preparation_error:"Staged package verification failed";
        if((unsigned)rc==0x8095F00CU)reason="Provider TLS certificate not trusted on fresh probe sessions for two minutes with no useful progress";
        fail(j,rc,reason);
        char detail[512];snprintf(detail,sizeof(detail),"ms=%llu event=preparation-failed handle=%d storage_stage=%s storage_errno=%d code=%d reason=%s\n",(unsigned long long)gs_clock(),j->handle,j->storage_stage[0]?j->storage_stage:"none",j->storage_errno,rc,reason);log_line(j->dest,detail);
    } else {
        if(j->single)j->target_limit=1;j->limit=j->target_limit;
        // Start where this server last settled (six lanes without history) and
        // climb while speed rises. A server that reset keeps its learned hold.
        if(!j->single) {
            OriginLanes *o=origin_lanes_locked(j->origin,0);
            int start=o&&o->ceiling?o->ceiling:LANE_START;
            if(j->limit>start)j->limit=start;
            if(o&&o->hold){j->ceiling=o->ceiling;j->ceiling_hold=o->hold;j->ceiling_until=o->until;}
        }
        j->rate_at=real_clock();j->rate_bytes=j->useful_bytes;
        snprintf(j->validator_kind,sizeof(j->validator_kind),"%s",probe_validator_kind==1?"strong-etag":probe_validator_kind==2?"last-modified":probe_validator_kind==3?"immutable-link":j->expected[0]?"publisher-sha256":j->pkg_integrity?"pkg-payload-sha256":"none");
        snprintf(j->fallback_reason,sizeof(j->fallback_reason),"%s",!j->single?"none":probe_range_unsupported?"range-unsupported":probe_etag_present?"etag-not-usable":"no-safe-validator");
        j->recovery_at=add_deadline(gs_clock(),10000);j->recovery_class=GS_FAILURE_NONE;j->recovery_detail[0]=0;
        j->status.state=complete?GS_COMPLETE:GS_DOWNLOADING;
        gs_transfer_admission_log(j,probe_etag_present,probe_etag_usable,probe_date_present,
            probe_date_valid,probe_modified_present,probe_modified_valid,probe_date_modified_gap,header,got);
        gs_transfer_log(j,j->single?"single-representation-stream":j->etag[0]?"range-ready-etag":j->last_modified[0]?"range-ready-last-modified":!strcmp(j->validator_kind,"immutable-link")?"range-ready-immutable-link":j->pkg_integrity?"range-ready-pkg-sha":"range-ready-publisher-sha");
    }
    if(!restart)j->preparing=0;gs_unlock(&gate);
    if(restart){if(j->fd>=0||j->lock_fd>=0||j->chunks)gs_store_close(j);goto restart;}
    return NULL;
}
int sspi_xfer_start(const char *url,const char *bearer,const char *destination,const char *title,const char *content,const char *sha,int requested)
{
    if(!initialized||!url||!destination||strlen(url)>=8192||strlen(destination)>=1024||
       (bearer&&strlen(bearer)>=2048)||(title&&strlen(title)>=16)||(content&&strlen(content)>=49)||(sha&&*sha&&strlen(sha)!=64))return -2;
    char origin[512];if(gs_http_origin(url,origin,sizeof(origin)))return -2;
    gs_lock(&gate);GsJobState *j=NULL;
    if(__atomic_load_n(&shutting_down,__ATOMIC_ACQUIRE)){gs_unlock(&gate);return -3;}
    for(int i=0;i<GS_XFER_JOBS;i++){if(jobs[i].handle&&!strcmp(jobs[i].dest,destination)){gs_unlock(&gate);return -5;}if(!jobs[i].handle)j=&jobs[i];}
    if(!j){gs_unlock(&gate);return -6;}
    memset(j,0,sizeof(*j));j->fd=j->lock_fd=-1;j->handle=next_handle++;if(next_handle<=0)next_handle=1;
#if SSPI_OWNER_DEBUG
    memset(&owner_metrics[j-jobs],0,sizeof(owner_metrics[0]));owner_metrics[j-jobs].sampled_at=owner_metrics[j-jobs].network_progress_at=gs_clock();
#endif
    j->status.state=GS_QUEUED;j->limit=1;j->preparing=1;j->requested_limit=requested;j->target_limit=requested<1?1:requested>GS_XFER_LANES?GS_XFER_LANES:requested;
    snprintf(j->url,sizeof(j->url),"%s",url);snprintf(j->bearer,sizeof(j->bearer),"%s",bearer?bearer:"");
    snprintf(j->dest,sizeof(j->dest),"%s",destination);snprintf(j->origin,sizeof(j->origin),"%s",origin);
    snprintf(j->title,sizeof(j->title),"%s",title?title:"");snprintf(j->content,sizeof(j->content),"%s",content?content:"");snprintf(j->expected,sizeof(j->expected),"%s",sha?sha:"");
    int handle=j->handle;if(gs_thread_start(&j->preparation_thread,prepare_job,j)){memset(j,0,sizeof(*j));gs_unlock(&gate);return -4;}
    j->preparation_started=1;gs_unlock(&gate);return handle;
}
int sspi_xfer_probe(int http_context,const char *url,int64_t total,int64_t offset,void *data,unsigned length)
{
    if(!url||!data||!length||length>8U*1024U*1024U||offset<0||total<4096||offset>total-length)return -2;
    gs_lock(&init_gate);int rc=gs_http_init(http_context);gs_unlock(&init_gate);
    if(rc)return rc;
    GsHttp h;gs_http_reset(&h);size_t got=0;
    rc=gs_http_open(&h,url,NULL,offset,offset+length-1,NULL);
    if(!rc && (h.status!=206||h.start!=offset||h.end!=offset+length-1||h.total!=total||
        (h.length>=0&&h.length!=length)))rc=h.status>=400?h.status:-10;
    while(!rc&&got<length){int n=gs_http_read(&h,(unsigned char*)data+got,length-(unsigned)got);if(n<=0||(unsigned)n>length-got)rc=n<0?n:-11;else got+=(unsigned)n;}
    if(!rc){unsigned char extra;int n=gs_http_read(&h,&extra,1);if(n)rc=n<0?n:-10;}
    gs_http_abort(&h);gs_http_close(&h);return rc;
}

int64_t sspi_xfer_readable(int handle,int64_t offset)
{
    int64_t result=-1;
    gs_lock(&gate);GsJobState *j=lookup(handle);
    if(j && offset>=0 && offset<j->status.total && j->chunks &&
       j->status.state!=GS_FAILED && j->status.state!=GS_CANCELED && !j->verification_retries) {
        if(j->pkg_integrity && j->status.state!=GS_COMPLETE) {gs_unlock(&gate);return 0;}
        uint64_t end=(uint64_t)offset;uint32_t chunk=(uint32_t)(end/GS_XFER_CHUNK);
        while(chunk<j->header.count && j->chunks[chunk].done) {
            end=(uint64_t)(++chunk)*GS_XFER_CHUNK;
            if(end>j->header.total)end=j->header.total;
        }
        result=(int64_t)end-offset;
    }
    gs_unlock(&gate);return result;
}
int sspi_xfer_poll(int handle,GsXferStatus *status)
{
    if(!status)return -1;char line[TRANSFER_LOG_SIZE]={0},stall_line[256]={0},destination[1024];
#if SSPI_OWNER_DEBUG
    char owner_line[TRANSFER_LOG_SIZE]={0};
#endif
    gs_lock(&gate);GsJobState *j=lookup(handle);if(j) {
        if(stall_watchdog_locked(j,stall_line,sizeof(stall_line)))snprintf(destination,sizeof(destination),"%s",j->dest);
        *status=j->status;
        if(status->state==GS_VALIDATING) {
            uint64_t verified=__atomic_load_n(&j->validation_done,__ATOMIC_ACQUIRE);
            if((j->expected[0]||j->pkg_integrity) && verified<(uint64_t)status->total)
                snprintf(status->error,sizeof(status->error),"Verifying SHA-256: %u%%",(unsigned)(verified*100/(uint64_t)status->total));
            else snprintf(status->error,sizeof(status->error),"Saving package for installation...");
        }
        // GS_QUEUED is the preparation phase (admission, source probe, staging
        // file). Publish its detail on every poll so a cold or slow provider is
        // never reported as a silent 0% transfer. No byte count is implied.
        else if(status->state==GS_QUEUED) {
            if(j->recovery_detail[0])snprintf(status->error,sizeof(status->error),"%s",j->recovery_detail);
            else gs_preparation_detail(status->error,sizeof(status->error),j->title);
        }
        else if(status->state==GS_DOWNLOADING&&j->recovery_detail[0]&&gs_clock()<j->recovery_deadline)
            snprintf(status->error,sizeof(status->error),"%s",j->recovery_detail);
        // Live accepted bytes are not a durability claim. Resume uses the map.
        for(int i=0;i<GS_XFER_LANES;i++)if(lanes[i].job>=0 && &jobs[lanes[i].job]==j)status->done+=lanes[i].written;
        if(status->done>status->total)status->done=status->total;
        // The final body byte precedes the strict EOF check. Only signal 100%
        // once that check has passed and the job enters validation/completion.
        if(status->state==GS_DOWNLOADING && status->total>0 && status->done==status->total)status->done--;
        if(gs_clock()-j->log_at>=5000) {
            j->log_at=gs_clock();format_log(j,"sample",line,sizeof(line));snprintf(destination,sizeof(destination),"%s",j->dest);
        }
#if SSPI_OWNER_DEBUG
        owner_sample(j,owner_line,sizeof(owner_line));
        if(owner_line[0])snprintf(destination,sizeof(destination),"%s",j->dest);
#endif
    }gs_unlock(&gate);if(stall_line[0])log_line(destination,stall_line);if(line[0])log_line(destination,line);
#if SSPI_OWNER_DEBUG
    if(owner_line[0])log_line(destination,owner_line);
#endif
    return j?0:-1;
}
#ifdef GS_HOST_TEST
__declspec(dllexport) int gs_test_waiting_probes(void){gs_lock(&gate);int waiting=0;for(int i=0;i<GS_XFER_JOBS;i++)if(jobs[i].preparing&&jobs[i].status.state==GS_QUEUED&&!jobs[i].active)waiting++;gs_unlock(&gate);return waiting;}
__declspec(dllexport) int gs_test_limit(int handle){gs_lock(&gate);GsJobState *j=lookup(handle);int limit=j?j->limit:0;gs_unlock(&gate);return limit;}
__declspec(dllexport) unsigned gs_test_lane_buffers(void){return __atomic_load_n(&allocated_lane_buffers,__ATOMIC_RELAXED);}
__declspec(dllexport) void gs_test_set_limit(int handle,int limit){gs_lock(&gate);GsJobState *j=lookup(handle);if(j)j->limit=limit>j->target_limit?j->target_limit:limit;gs_unlock(&gate);}
__declspec(dllexport) uint64_t gs_test_io(int handle,int metric){gs_lock(&gate);GsJobState *j=lookup(handle);uint64_t v=!j?0:metric==0?j->read_calls:metric==1?j->write_calls:metric==2?j->write_bytes:metric==4?(uint64_t)j->status.network_bytes-j->write_bytes:j->buffer_wait_ms;gs_unlock(&gate);return v;}
#endif
static int control(int handle,int cancel)
{
    gs_lock(&gate);GsJobState *j=lookup(handle);if(!j){gs_unlock(&gate);return -1;}
    j->resume_requested=0;
    if(j->status.state!=GS_COMPLETE&&j->status.state!=GS_FAILED&&j->status.state!=GS_CANCELED){__atomic_store_n(&j->stop,cancel?2:1,__ATOMIC_RELEASE);j->status.state=cancel?GS_CANCELED:GS_PAUSED;}
    for(int i=0;i<GS_XFER_LANES;i++)if(lanes[i].job>=0 && &jobs[lanes[i].job]==j)gs_http_abort(&lanes[i].http);
    if(j->preparing)gs_http_abort(&j->preparation_http);
    if(!j->active&&!j->finishing)checkpoint(j);
    gs_unlock(&gate);return 0;
}
int sspi_xfer_pause(int handle){return control(handle,0);}
int sspi_xfer_cancel(int handle){return control(handle,1);}
int sspi_xfer_resume(int handle)
{
    GsThread old_thread;int join_old=0;
    gs_lock(&gate);GsJobState *j=lookup(handle);int rc=-1;
    if(j&&j->status.state==GS_PAUSED&&j->stop==1) {
        if(j->preparing){j->resume_requested=1;rc=0;gs_unlock(&gate);return rc;}
        if(j->chunks){j->resume_requested=1;apply_resume(j);rc=0;gs_unlock(&gate);return rc;}
        if(j->preparation_started&&!j->preparation_joining){old_thread=j->preparation_thread;j->preparation_joining=1;join_old=1;}
    } else if(j&&(j->status.state==GS_DOWNLOADING||j->status.state==GS_VALIDATING||j->status.state==GS_COMPLETE))rc=0;
    gs_unlock(&gate);
    if(!join_old)return rc;
    gs_thread_join(old_thread);
    gs_lock(&gate);j=lookup(handle);
    if(j){j->preparation_started=0;j->preparation_joining=0;}
    if(!j||j->status.state!=GS_PAUSED||j->stop!=1){gs_unlock(&gate);return -1;}
    __atomic_store_n(&j->stop,0,__ATOMIC_RELEASE);j->status.state=GS_QUEUED;j->status.error_code=0;j->status.error[0]=0;j->preparing=1;
    if(gs_thread_start(&j->preparation_thread,prepare_job,j)){j->preparing=0;fail(j,-4,"Could not restart transfer preparation");rc=-1;}
    else {j->preparation_started=1;rc=0;}
    gs_unlock(&gate);return rc;
}
int sspi_xfer_destroy(int handle)
{
    GsThread preparation;int join=0;
    gs_lock(&gate);GsJobState *j=lookup(handle);if(!j){gs_unlock(&gate);return -1;}
    if(j->active||j->finishing||j->preparing||j->preparation_joining||j->checkpointing||j->checkpoint_waiters||j->resume_requested||j->status.state==GS_DOWNLOADING||j->status.state==GS_QUEUED){gs_unlock(&gate);return -2;}
    if(j->preparation_started&&!j->preparation_joining){preparation=j->preparation_thread;j->preparation_joining=1;join=1;}
    gs_unlock(&gate);if(join)gs_thread_join(preparation);
    gs_lock(&gate);j=lookup(handle);if(!j){gs_unlock(&gate);return -1;}
    j->preparation_started=0;j->preparation_joining=0;
    if(j->active||j->finishing||j->preparing||j->checkpointing||j->checkpoint_waiters){gs_unlock(&gate);return -2;}
    gs_store_close(j);memset(j,0,sizeof(*j));gs_unlock(&gate);return 0;
}
void sspi_xfer_shutdown(void)
{
    gs_lock(&init_gate);if(!initialized){gs_unlock(&init_gate);return;}
    __atomic_store_n(&shutting_down,1,__ATOMIC_RELEASE);
    for(int i=0;i<GS_XFER_JOBS;i++)if(jobs[i].handle)sspi_xfer_pause(jobs[i].handle);
    for(;;) {
        gs_lock(&gate);int preparing=0;
        for(int i=0;i<GS_XFER_JOBS;i++)preparing+=jobs[i].preparing;
        gs_unlock(&gate);if(!preparing)break;gs_sleep(1);
    }
    for(int i=0;i<GS_XFER_JOBS;i++)if(jobs[i].handle&&jobs[i].preparation_started) {
        gs_thread_join(jobs[i].preparation_thread);jobs[i].preparation_started=0;jobs[i].preparation_joining=0;
    }
    for(int i=0;i<GS_XFER_LANES;i++){if(lanes[i].started)gs_thread_join(lanes[i].thread);lanes[i].started=0;}
    gs_lock(&gate);checkpoint_stopping=1;gs_unlock(&gate);
    if(checkpoint_started){gs_thread_join(checkpoint_thread);checkpoint_started=0;}
    __atomic_store_n(&writer_stopping,1,__ATOMIC_RELEASE);
    if(writer_started){gs_thread_join(writer_thread);writer_started=0;}
    for(int i=0;i<hash_started;i++)gs_thread_join(hash_threads[i]);hash_started=0;
    for(int i=0;i<GS_XFER_LANES;i++)lane_buffer_release(&lanes[i]);
    for(int i=0;i<GS_XFER_JOBS;i++)if(jobs[i].handle)sspi_xfer_destroy(jobs[i].handle);
    initialized=0;gs_unlock(&init_gate);
}
