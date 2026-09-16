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
static uint64_t server_cooldown;
#ifdef GS_HOST_TEST
static uint64_t test_clock_offset;
__declspec(dllexport) void gs_test_advance(unsigned ms){__atomic_add_fetch(&test_clock_offset,ms,__ATOMIC_RELAXED);}
#endif
static GsJobState jobs[GS_XFER_JOBS];
#define WRITE_BLOCK (4U * 1024U * 1024U)
#define READ_BLOCK (1024U * 1024U)
#define WRITE_RUN (16U * 1024U * 1024U)
#define TRANSFER_LOG_SIZE 1280
typedef struct { uint64_t offset; size_t length; int state; } WriteBlock;
typedef struct {
    GsThread thread; GsHttp http; int job, started; unsigned span;
    int64_t written, received; unsigned char *buffer;
    WriteBlock writes[2]; unsigned producer, consumer, hash_consumer; int write_error, hashing;
    SHA256_CTX hash; int source_handle, source_generation;
#if SSPI_OWNER_DEBUG
    uint64_t owner_filling, owner_wait_started;
#endif
} Lane;
static Lane lanes[GS_XFER_LANES];
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
    if(!rc && before<ORBIS_KERNEL_PRIO_FIFO_LOWEST)rc=scePthreadSetprio(self,ORBIS_KERNEL_PRIO_FIFO_LOWEST);
    gs_log_write("download","event=thread-priority role=%s before=%d target=%d rc=0x%08X",role,before,ORBIS_KERNEL_PRIO_FIFO_LOWEST,(unsigned)rc);
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
    snprintf(line,size,"ms=%llu api=3 engine=sceHttp-chunks revision=five-lane-background-1 event=%s handle=%d state=%d lanes=%d done=%lld total=%lld network=%lld retries=%d code=%d limit=%d target=%d checkpoint_ms=%llu request_ms=%llu read_ms=%llu write_ms=%llu hash_ms=%llu read_calls=%llu write_calls=%llu write_bytes=%llu buffer_wait_ms=%llu read_interruptions=%llu write_jumps=%llu write_jump_bytes=%llu write_switches=%llu write_max_ms=%llu span_chunks=2 write_run_mib=16 write_block_kib=4096 read_block_kib=1024 buffer_mib=%u\n",
        (unsigned long long)gs_clock(),event,j->handle,j->status.state,j->status.lanes,
        (long long)j->status.done,(long long)j->status.total,(long long)j->status.network_bytes,j->status.retries,j->status.error_code,j->limit,j->target_limit,
        (unsigned long long)j->checkpoint_ms,(unsigned long long)j->request_ms,(unsigned long long)j->read_ms,
        (unsigned long long)j->write_ms,(unsigned long long)j->hash_ms,
        (unsigned long long)j->read_calls,(unsigned long long)j->write_calls,
        (unsigned long long)j->write_bytes,(unsigned long long)j->buffer_wait_ms,(unsigned long long)j->read_interruptions,
        (unsigned long long)j->write_jumps,(unsigned long long)j->write_jump_bytes,
        (unsigned long long)j->write_switches,(unsigned long long)j->write_max_ms,(unsigned)(GS_XFER_LANES*2*WRITE_BLOCK/(1024U*1024U)));
}
void gs_transfer_log(const GsJobState *j,const char *event)
{
    char line[TRANSFER_LOG_SIZE];format_log(j,event,line,sizeof(line));log_line(j->dest,line);
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
            fail(j,-12,"Chunk checkpoint could not be committed");
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
            if(!j->dirty || j->status.state==GS_FAILED)break;
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
    uint64_t before=gs_clock();
    for(;;) {
        gs_lock(&gate);
        int busy=drain?(lane->writes[0].state||lane->writes[1].state):lane->writes[lane->producer].state;
        int error=lane->write_error;
#if SSPI_OWNER_DEBUG
        if(busy && !lane->owner_wait_started)lane->owner_wait_started=before;
        if(!busy)lane->owner_wait_started=0;
#endif
        if(!busy) {j->buffer_wait_ms+=gs_clock()-before;gs_unlock(&gate);return error;}
        gs_unlock(&gate);gs_sleep(1);
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
static void clean_chunk(GsJobState *j)
{
    j->clean_chunks++;
    if(j->limit<j->target_limit && j->clean_chunks>=4 && gs_clock()>=j->recovery_at) {
        j->limit++;j->clean_chunks=0;j->recovery_at=gs_clock()+10000;
    }
}
static const char *retry_chunk(GsJobState *j,uint32_t chunk,unsigned lane,int rc,int retry_after,uint64_t *delay)
{
    uint64_t now=gs_clock();
    j->clean_chunks=0;j->recovery_at=now+10000;
    int transient=rc==408||rc==425||rc==429||rc==500||rc==502||rc==503||rc==504;
    int tls=(unsigned)rc==0x8095F00CU;
    *delay=(tls?2000U:transient||rc==-15?1000U:250U)<<(j->attempts[chunk]-1);
    *delay+=lane*23U;
    if(retry_after>0)*delay=(uint64_t)retry_after*1000U;
    j->retry_at[chunk]=now+*delay;

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
            j->clean_chunks=0;j->recovery_at=j->backoff_until=now+10000;
            j->failed_lanes=0;j->failure_window=now;
        }
        uint64_t until=now+(server?*delay:250U);
        if(j->next_retry<until)j->next_retry=until;
    }
    return scope;
}
static void server_backoff(uint64_t until)
{
    if(server_cooldown<until)server_cooldown=until;
    for(int i=0;i<GS_XFER_JOBS;i++)if(jobs[i].handle) {
        if(jobs[i].limit>2)jobs[i].limit=2;
        jobs[i].clean_chunks=0;jobs[i].recovery_at=server_cooldown+10000;
    }
}
static int recover_stream(Lane *lane,GsJobState *j,int rc,unsigned attempt,uint64_t offset)
{
    GsHttp *h=&lane->http;
    int retryable=rc<0 && rc!=-3 && rc!=-10 && rc!=-12 && rc!=-13;
    retryable|=rc==408||rc==425||rc==429||rc==500||rc==502||rc==503||rc==504;
    if(!retryable || attempt>8 || j->single)return 0;
    uint64_t now=gs_clock();unsigned shift=attempt-1;if(shift>5)shift=5;
    uint64_t delay=250U<<shift;
    delay+=(unsigned)((now^(uintptr_t)lane^(offset>>9))%201U);
    if(delay>10000)delay=10000;
    int server=rc==429||rc==503;
    if(server)delay=h->retry_after>0?(uint64_t)h->retry_after*1000U:15000U;
    gs_lock(&gate);
    int old_limit=j->limit;int burst=0;
    j->status.retries++;
    // A recovered body is still a recent connection failure. Require new clean
    // data and a quiet interval before adding another lane, even when isolated.
    j->clean_chunks=0;j->recovery_at=now+10000;
    if(!j->failure_window || now-j->failure_window>10000){j->failure_window=now;j->stream_failed_lanes=0;}
    if(!server)j->stream_failed_lanes|=1U<<(unsigned)(lane-lanes);
    unsigned failed=0;for(unsigned mask=j->stream_failed_lanes;mask;mask>>=1)failed+=mask&1U;
    // A single dropped body belongs to its socket. Reduce the job's budget only
    // when distinct lanes fail in a short interval, then recover on clean data.
    if(!server && failed>=3 && now>=j->backoff_until) {
        burst=1;if(j->limit>2)j->limit--;
        j->clean_chunks=0;j->backoff_until=j->recovery_at=now+10000;j->stream_failed_lanes=0;
    }
    if(server)server_backoff(now+delay);
    int new_limit=j->limit;gs_unlock(&gate);
    char line[768];
    snprintf(line,sizeof(line),"ms=%llu api=3 revision=supervised-streams-1 event=stream-retry handle=%d lane=%u attempt=%u hex=0x%08X http=%d stage=%s offset=%llu delay_ms=%llu scope=%s ssl=0x%08X verify=0x%X errno=0x%08X\n",
        (unsigned long long)now,j->handle,(unsigned)(lane-lanes),attempt,(unsigned)rc,h->status,h->stage?h->stage:"open",
        (unsigned long long)offset,(unsigned long long)delay,server?"global":burst?"burst":"stream",(unsigned)h->ssl_error,h->ssl_verify,(unsigned)h->native_errno);
    log_line(j->dest,line);
    snprintf(line,sizeof(line),"ms=%llu event=lane-budget handle=%d before=%d after=%d scope=%s\n",(unsigned long long)now,j->handle,old_limit,new_limit,server?"global":burst?"burst":"stream");log_line(j->dest,line);
    gs_http_close(h);
    uint64_t until=now+delay;
    for(;;) {
        if(__atomic_load_n(&j->stop,__ATOMIC_ACQUIRE))return 0;
        gs_lock(&gate);uint64_t shared=server_cooldown;gs_unlock(&gate);
        if(shared>until)until=shared;
        if(gs_clock()>=until)return 1;
        gs_sleep(25);
    }
}
static int chunk_transfer(Lane *lane,GsJobState *j,uint32_t index,unsigned char digest[32])
{
    uint64_t start=j->single?0:(uint64_t)index*GS_XFER_CHUNK;
    uint64_t length=j->single?j->header.total:j->header.total-start;
    if(!j->single&&length>(uint64_t)lane->span*GS_XFER_CHUNK)length=(uint64_t)lane->span*GS_XFER_CHUNK;
    uint64_t done=0,progress_at=0;
    unsigned failures=0;
    sha256_init(&lane->hash);
    GsHttp *h=&lane->http;
reopen:
    if(__atomic_load_n(&j->stop,__ATOMIC_ACQUIRE))return -3;
    // Keep the hash and accepted bytes alive while replacing a failed connection.
    // Only the unread tail is requested; completed chunks remain checkpointable.
    gs_lock(&gate);uint64_t cooldown=server_cooldown;gs_unlock(&gate);
    while(gs_clock()<cooldown) {
        if(__atomic_load_n(&j->stop,__ATOMIC_ACQUIRE))return -3;
        gs_sleep(25);gs_lock(&gate);cooldown=server_cooldown;gs_unlock(&gate);
    }
    uint64_t before=gs_clock();
    int rc=gs_http_open(h,j->url,j->bearer,j->single?-1:(int64_t)(start+done),(int64_t)(start+length-1));
    gs_lock(&gate);j->request_ms+=gs_clock()-before;gs_unlock(&gate);
    if(rc)goto interrupted;
    if(!h->reused) {
        char transport[768];
        snprintf(transport,sizeof(transport),"ms=%llu event=http-receive-buffer handle=%d lane=%u origin=%s bytes=%d rc=0x%08X interrupted_calls=%u\n",
            (unsigned long long)gs_clock(),j->handle,(unsigned)(lane-lanes),h->origin,h->recv_block,(unsigned)h->recv_block_rc,h->interruptions);
        log_line(j->dest,transport);
    }
    h->stage="validate-headers";
    if(h->status!=200&&h->status!=206){rc=h->status?h->status:-10;goto interrupted;}
    if(j->single) {if(h->status!=200 || (h->length>=0 && (uint64_t)h->length!=length))return -10;}
    else if(h->status!=206||h->start!=(int64_t)(start+done)||h->end!=(int64_t)(start+length-1)||h->total!=(int64_t)j->header.total||
            (h->length>=0 && h->length!=(int64_t)(length-done)))return -10;
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
            h->stage="read";before=gs_clock();unsigned interruptions=h->interruptions;
            unsigned read_size=(unsigned)(want-filled);if(read_size>READ_BLOCK)read_size=READ_BLOCK;
            int n=gs_http_read(h,buffer+filled,read_size);uint64_t read_ms=gs_clock()-before;
            gs_lock(&gate);j->read_ms+=read_ms;j->read_calls++;j->read_interruptions+=h->interruptions-interruptions;
            if(n>0&&(unsigned)n<=read_size) {
                j->status.network_bytes+=n;
#if SSPI_OWNER_DEBUG
                owner_metrics[j-jobs].network_progress_at=before+read_ms;
                lane->owner_filling=filled+(unsigned)n;owner_buffer_peak(j);
#endif
            }gs_unlock(&gate);
            if(n<=0){rc=n<0?n:-11;break;}if((unsigned)n>read_size)return -10;
            filled+=(unsigned)n;lane->received+=n;
        }
        if(!filled)goto interrupted;
        gs_lock(&gate);
        lane->writes[slot].offset=start+done;lane->writes[slot].length=filled;
#if SSPI_OWNER_DEBUG
        lane->owner_filling=0;
#endif
        lane->writes[slot].state=1;lane->producer^=1;gs_unlock(&gate);
        done+=filled;
        if(done-progress_at>=1024*1024){failures=0;progress_at=done;}
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
    h->stage="eof";int extra=gs_http_read(h,lane->buffer,1);if(extra!=0)return extra<0?extra:-10;
    sha256_final(&lane->hash,digest);return 0;
interrupted:
    if(done<length && recover_stream(lane,j,rc,++failures,start+done))goto reopen;
    return rc;
}
static void apply_resume(GsJobState *j)
{
    if(j->resume_requested && j->status.state==GS_PAUSED && j->stop==1 &&
       !j->active && !j->finishing && !j->preparing && !j->checkpointing && !j->checkpoint_waiters) {
        j->resume_requested=0;__atomic_store_n(&j->stop,0,__ATOMIC_RELEASE);j->status.state=GS_DOWNLOADING;
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
        if(now<server_cooldown||j->active>=available_limit(j)||!source_available(j)||host_active(j)>=GS_XFER_LANES||total_active()>=GS_XFER_LANES||now<j->next_retry)continue;
        // A server ignoring ranges has one whole-file retry budget. Scanning
        // other map slots would restart byte zero before slot zero's delay.
        uint32_t count=j->single?1:j->header.count;
        for(uint32_t c=0;c<count;c++)if(!j->chunks[c].done&&!j->claims[c]&&now>=j->retry_at[c]) {
            if(j->single && j->active)break;
            unsigned wanted=j->single?1:(j->header.count+(unsigned)j->target_limit-1)/(unsigned)j->target_limit;
            if(wanted>2)wanted=2; // 32 MiB on one reusable connection, bounded disk interleaving.
            *span=1;
            while(*span<wanted && c+*span<j->header.count && !j->chunks[c+*span].done && !j->claims[c+*span] && now>=j->retry_at[c+*span])(*span)++;
            for(unsigned n=0;n<*span;n++)j->claims[c+n]=1;
            j->active++;j->status.lanes=j->active;*job=i;*chunk=c;turn=(i+1)%GS_XFER_JOBS;return 1;
        }
    }return 0;
}
static void *worker(void *argument)
{
    background_thread("reader");Lane *lane=argument;gs_http_reset(&lane->http);lane->job=-1;
    while(!__atomic_load_n(&shutting_down,__ATOMIC_ACQUIRE)) {
        int slot=-1;uint32_t chunk=0;gs_lock(&gate);int found=claim(&slot,&chunk,&lane->span);if(found==1){lane->job=slot;lane->written=lane->received=0;}gs_unlock(&gate);
        if(!found){gs_sleep(20);continue;}
        GsJobState *j=&jobs[slot];unsigned char digest[32];
        if(found==2)goto validate;
        if(lane->source_handle!=j->handle || lane->source_generation!=j->verification_retries) {
            gs_http_close(&lane->http);lane->source_handle=j->handle;lane->source_generation=j->verification_retries;
        }
        int rc=chunk_transfer(lane,j,chunk,digest);
        int retry_after=lane->http.retry_after,status=lane->http.status,reused=lane->http.reused;
        int ssl_error=lane->http.ssl_error,native_errno=lane->http.native_errno;
        unsigned ssl_verify=lane->http.ssl_verify,open_wait_ms=lane->http.open_wait_ms;
        const char *stage=lane->http.stage?lane->http.stage:"open";
        uint64_t range_start=j->single?0:(uint64_t)chunk*GS_XFER_CHUNK;
        uint64_t range_end=j->single?j->header.total:(uint64_t)(chunk+lane->span)*GS_XFER_CHUNK;
        if(range_end>j->header.total)range_end=j->header.total;
        char retry_line[1024]={0},destination[1024];
        if(rc)gs_http_close(&lane->http);
        // Drain even on cancellation/failure before releasing ownership or reusing
        // buffers. Only written, hashed chunks are eligible for the resume map.
        if(wait_write(lane,j,1))rc=-12;
        gs_lock(&gate);lane->job=-1;lane->written=0;lane->write_error=0;j->active--;j->status.lanes=j->active;
#if SSPI_OWNER_DEBUG
        lane->owner_filling=lane->owner_wait_started=0;
#endif
        for(unsigned n=0;n<lane->span;n++)j->claims[chunk+n]=0;
        unsigned last=chunk+lane->span-1;
        while(chunk<last && j->chunks[chunk].done)chunk++;
        if(!rc && !j->stop) {
            if(j->single) {
                // A range-ignoring server cannot safely resume. The complete body is
                // verified before publication, and no partial map bits are committed.
                for(uint32_t c=0;c<j->header.count;c++)j->chunks[c].done=1;
                j->status.done=j->status.total;
            } else {
                j->chunks[chunk].done=1;memcpy(j->chunks[chunk].hash,digest,32);
                uint64_t n=j->header.total-(uint64_t)chunk*GS_XFER_CHUNK;j->status.done+=(int64_t)(n>GS_XFER_CHUNK?GS_XFER_CHUNK:n);
            }
            j->dirty++;clean_chunk(j);
        } else if(rc && !j->stop) {
            j->status.retries++;j->attempts[chunk]++;
            int old_limit=j->limit;uint64_t delay=0;const char *scope="fatal";
            if(rc==401||rc==403||rc==410)fail(j,rc,"Provider rejected link; resolve it again and resume");
            else if(rc==-12)fail(j,rc,"Positioned disk write failed; committed chunks retained");
            else if(!j->single || j->attempts[chunk]>3)fail(j,rc,"Transfer recovery exhausted; completed chunks retained");
            else {
                scope=retry_chunk(j,chunk,(unsigned)(lane-lanes),rc,retry_after,&delay);
                if(rc==429||rc==503)server_backoff(j->retry_at[chunk]);
            }
            snprintf(destination,sizeof(destination),"%s",j->dest);
            snprintf(retry_line,sizeof(retry_line),"ms=%llu api=3 revision=supervised-streams-1 event=retry handle=%d lane=%u chunk=%u attempt=%u raw=%d hex=0x%08X http=%d stage=%s reused=%d range_start=%llu range_end=%llu received=%lld delay_ms=%llu scope=%s limit_before=%d limit=%d target=%d ssl=0x%08X verify=0x%X errno=0x%08X open_wait_ms=%u\n",
                (unsigned long long)gs_clock(),j->handle,(unsigned)(lane-lanes),chunk,(unsigned)j->attempts[chunk],rc,(unsigned)rc,status,stage,reused,
                (unsigned long long)range_start,(unsigned long long)(range_end-1),(long long)lane->received,(unsigned long long)delay,scope,old_limit,j->limit,j->target_limit,
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
    for(int i=0;i<GS_XFER_LANES;i++) {
        memset(&lanes[i],0,sizeof(lanes[i]));lanes[i].job=-1;
        gs_http_reset(&lanes[i].http);lanes[i].buffer=malloc(2*WRITE_BLOCK);
        if(!lanes[i].buffer)goto init_failed;
    }
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
    for(int i=0;i<GS_XFER_LANES;i++){free(lanes[i].buffer);lanes[i].buffer=NULL;}
    gs_unlock(&init_gate);return -4;
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
    memset(&owner_metrics[j-jobs],0,sizeof(owner_metrics[0]));
    owner_metrics[j-jobs].sampled_at=owner_metrics[j-jobs].network_progress_at=gs_clock();
#endif
    j->status.state=GS_QUEUED;j->limit=1;j->preparing=1;
    j->target_limit=requested<1?1:requested>GS_XFER_LANES?GS_XFER_LANES:requested;
    snprintf(j->url,sizeof(j->url),"%s",url);snprintf(j->bearer,sizeof(j->bearer),"%s",bearer?bearer:"");
    snprintf(j->dest,sizeof(j->dest),"%s",destination);snprintf(j->origin,sizeof(j->origin),"%s",origin);
    snprintf(j->title,sizeof(j->title),"%s",title?title:"");snprintf(j->content,sizeof(j->content),"%s",content?content:"");snprintf(j->expected,sizeof(j->expected),"%s",sha?sha:"");
    gs_unlock(&gate);
    // The caller has no handle yet. Bound local admission and keep preparation
    // pinned until shutdown/cancellation can no longer race its job storage.
    uint64_t admission_started=gs_clock();
    for(;;) {
        gs_lock(&gate);uint64_t now=gs_clock();
        int canceled=__atomic_load_n(&shutting_down,__ATOMIC_ACQUIRE) || j->stop;
        if(canceled || now-admission_started>=15000) {
            fail(j,canceled?-3:503,canceled?"Transfer preparation canceled before identity probe":"Transfer admission busy before identity probe; retry later");
            gs_transfer_log(j,canceled?"probe-admission-canceled":"probe-admission-timeout");
            j->preparing=0;int handle=j->handle;gs_unlock(&gate);return handle;
        }
        if(now>=server_cooldown && source_available(j) && host_active(j)<GS_XFER_LANES && total_active()<GS_XFER_LANES) {
            j->active=1;gs_unlock(&gate);break;
        }
        gs_unlock(&gate);gs_sleep(25);
    }
    GsHttp h;gs_http_reset(&h);unsigned char header[0x1000],identity[32];size_t got=0;
    char preparation_error[220]={0};const char *probe_stage="response";
    int rc=gs_http_open(&h,url,bearer,0,0xfff);
    if(!rc) {
        if(h.status==206 && h.start==0 && h.end==0xfff && h.total>=0x1000 && (h.length<0||h.length==0x1000))j->status.total=h.total;
        else if(h.status==200 && h.length>=0x1000){j->status.total=h.length;j->single=1;}
        else rc=h.status>=400?h.status:-10;
    }
    if(!rc)probe_stage="header read";
    while(!rc && got<sizeof(header)){int n=gs_http_read(&h,header+got,(unsigned)(sizeof(header)-got));if(n<=0 || (size_t)n>sizeof(header)-got)rc=n<0?n:-11;else got+=(size_t)n;}
    if(!rc&&!j->single){probe_stage="range completion";unsigned char extra;int n=gs_http_read(&h,&extra,1);if(n)rc=n<0?n:-10;}
    if(h.status==429 || h.status==503) {
        uint64_t delay=h.retry_after>0?(uint64_t)h.retry_after*1000U:15000U;
        gs_lock(&gate);server_backoff(gs_clock()+delay);gs_unlock(&gate);
    }
    if(rc)snprintf(preparation_error,sizeof(preparation_error),"Package identity probe failed at %s/%s (HTTP %d, errno %d, TLS 0x%08X)",probe_stage,h.stage?h.stage:"unknown",h.status,h.native_errno,(unsigned)h.ssl_error);
    gs_http_abort(&h);gs_http_close(&h);
    if(__atomic_load_n(&j->stop,__ATOMIC_ACQUIRE))rc=-3;
    if(!rc && gs_verify_header(header,got,j->status.total,j->title,j->content)<0){rc=-13;snprintf(preparation_error,sizeof(preparation_error),"Package header failed size, title or content verification");}
    if(!rc){gs_digest(header,sizeof(header),identity);rc=gs_store_open(j,identity);}
    int complete=!rc && j->status.done==j->status.total;
    if(complete){unsigned char *buffer=malloc(GS_XFER_BUFFER);gs_lock(&gate);j->active=0;gs_unlock(&gate);rc=buffer?gs_finalize_file(j,buffer):-4;if(!buffer)snprintf(preparation_error,sizeof(preparation_error),"Not enough memory to verify the staged package");free(buffer);}
    if(complete && rc==-2 && j->expected[0]) {
        char detail[512];snprintf(detail,sizeof(detail),"ms=%llu event=verification-retry handle=%d resumed=1 %s\n",(unsigned long long)gs_clock(),j->handle,j->verification_error);log_line(j->dest,detail);
        j->verification_retries=1;j->status.retries++;j->status.done=0;j->validation_done=0;
        memset(j->chunks,0,j->header.count*sizeof(GsChunk));j->dirty++;complete=0;
        rc=gs_store_checkpoint(j)?-12:0;
    }
    gs_lock(&gate);j->active=0;
    if(j->stop || __atomic_load_n(&shutting_down,__ATOMIC_ACQUIRE))fail(j,-3,"Native transfer preparation canceled");
    else if(rc) {
        const char *reason=j->storage_error[0]?j->storage_error:j->verification_error[0]?j->verification_error:preparation_error[0]?preparation_error:"Staged package verification failed";
        fail(j,rc,reason);
        char detail[512];snprintf(detail,sizeof(detail),"ms=%llu event=preparation-failed handle=%d storage_stage=%s storage_errno=%d code=%d reason=%s\n",(unsigned long long)gs_clock(),j->handle,j->storage_stage[0]?j->storage_stage:"none",j->storage_errno,rc,reason);log_line(j->dest,detail);
    }
    else {
        if(j->single)j->target_limit=1;
        j->limit=j->target_limit<2?j->target_limit:2;
        j->recovery_at=gs_clock()+10000;
        j->status.state=GS_DOWNLOADING;gs_transfer_log(j,j->single?"range-ignored-single":"range-ready-no-etag");
        if(complete)j->status.state=GS_COMPLETE;
    }
    j->preparing=0;int handle=j->handle;gs_unlock(&gate);return handle;
}
int sspi_xfer_probe(int http_context,const char *url,int64_t total,int64_t offset,void *data,unsigned length)
{
    if(!url||!data||!length||length>8U*1024U*1024U||offset<0||total<4096||offset>total-length)return -2;
    gs_lock(&init_gate);int rc=gs_http_init(http_context);gs_unlock(&init_gate);
    if(rc)return rc;
    GsHttp h;gs_http_reset(&h);size_t got=0;
    rc=gs_http_open(&h,url,NULL,offset,offset+length-1);
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
    if(!status)return -1;char line[TRANSFER_LOG_SIZE]={0},destination[1024];
#if SSPI_OWNER_DEBUG
    char owner_line[TRANSFER_LOG_SIZE]={0};
#endif
    gs_lock(&gate);GsJobState *j=lookup(handle);if(j) {
        *status=j->status;
        if(status->state==GS_VALIDATING) {
            uint64_t verified=__atomic_load_n(&j->validation_done,__ATOMIC_ACQUIRE);
            if(j->expected[0] && verified<(uint64_t)status->total)
                snprintf(status->error,sizeof(status->error),"Verifying SHA-256: %u%%",(unsigned)(verified*100/(uint64_t)status->total));
            else snprintf(status->error,sizeof(status->error),"Saving package for installation...");
        }
        // GS_QUEUED is the preparation phase (admission, source probe, staging
        // file). Publish its detail on every poll so a cold or slow provider is
        // never reported as a silent 0% transfer. No byte count is implied.
        else if(status->state==GS_QUEUED) gs_preparation_detail(status->error,sizeof(status->error),j->title);
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
    }gs_unlock(&gate);if(line[0])log_line(destination,line);
#if SSPI_OWNER_DEBUG
    if(owner_line[0])log_line(destination,owner_line);
#endif
    return j?0:-1;
}
#ifdef GS_HOST_TEST
__declspec(dllexport) int gs_test_waiting_probes(void){gs_lock(&gate);int waiting=0;for(int i=0;i<GS_XFER_JOBS;i++)if(jobs[i].preparing&&jobs[i].status.state==GS_QUEUED&&!jobs[i].active)waiting++;gs_unlock(&gate);return waiting;}
__declspec(dllexport) int gs_test_limit(int handle){gs_lock(&gate);GsJobState *j=lookup(handle);int limit=j?j->limit:0;gs_unlock(&gate);return limit;}
__declspec(dllexport) void gs_test_set_limit(int handle,int limit){gs_lock(&gate);GsJobState *j=lookup(handle);if(j)j->limit=limit>j->target_limit?j->target_limit:limit;gs_unlock(&gate);}
__declspec(dllexport) uint64_t gs_test_io(int handle,int metric){gs_lock(&gate);GsJobState *j=lookup(handle);uint64_t v=!j?0:metric==0?j->read_calls:metric==1?j->write_calls:metric==2?j->write_bytes:metric==4?(uint64_t)j->status.network_bytes-j->write_bytes:j->buffer_wait_ms;gs_unlock(&gate);return v;}
#endif
static int control(int handle,int cancel)
{
    gs_lock(&gate);GsJobState *j=lookup(handle);if(!j){gs_unlock(&gate);return -1;}
    j->resume_requested=0;
    if(j->status.state!=GS_COMPLETE&&j->status.state!=GS_FAILED&&j->status.state!=GS_CANCELED){__atomic_store_n(&j->stop,cancel?2:1,__ATOMIC_RELEASE);j->status.state=cancel?GS_CANCELED:GS_PAUSED;}
    for(int i=0;i<GS_XFER_LANES;i++)if(lanes[i].job>=0 && &jobs[lanes[i].job]==j)gs_http_abort(&lanes[i].http);
    if(!j->active&&!j->finishing)checkpoint(j);
    gs_unlock(&gate);return 0;
}
int sspi_xfer_pause(int handle){return control(handle,0);}
int sspi_xfer_cancel(int handle){return control(handle,1);}
int sspi_xfer_resume(int handle)
{
    gs_lock(&gate);GsJobState *j=lookup(handle);int rc=-1;
    if(j&&j->status.state==GS_PAUSED&&j->stop==1){j->resume_requested=1;apply_resume(j);rc=0;}
    else if(j&&(j->status.state==GS_DOWNLOADING||j->status.state==GS_VALIDATING||j->status.state==GS_COMPLETE))rc=0;
    gs_unlock(&gate);return rc;
}
int sspi_xfer_destroy(int handle)
{
    gs_lock(&gate);GsJobState *j=lookup(handle);if(!j){gs_unlock(&gate);return -1;}
    if(j->active||j->finishing||j->preparing||j->checkpointing||j->checkpoint_waiters||j->resume_requested||j->status.state==GS_DOWNLOADING||j->status.state==GS_QUEUED){gs_unlock(&gate);return -2;}
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
    for(int i=0;i<GS_XFER_LANES;i++){if(lanes[i].started)gs_thread_join(lanes[i].thread);lanes[i].started=0;}
    gs_lock(&gate);checkpoint_stopping=1;gs_unlock(&gate);
    if(checkpoint_started){gs_thread_join(checkpoint_thread);checkpoint_started=0;}
    __atomic_store_n(&writer_stopping,1,__ATOMIC_RELEASE);
    if(writer_started){gs_thread_join(writer_thread);writer_started=0;}
    for(int i=0;i<hash_started;i++)gs_thread_join(hash_threads[i]);hash_started=0;
    for(int i=0;i<GS_XFER_LANES;i++){free(lanes[i].buffer);lanes[i].buffer=NULL;}
    for(int i=0;i<GS_XFER_JOBS;i++)if(jobs[i].handle)sspi_xfer_destroy(jobs[i].handle);
    initialized=0;gs_unlock(&init_gate);
}
