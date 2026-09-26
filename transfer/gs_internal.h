#ifndef GS_INTERNAL_H
#define GS_INTERNAL_H
#include "gs_transfer.h"
#define WORD GS_SHA_WORD
#define BYTE GS_SHA_BYTE
#include "../resident/plugin/crypto/sha256.h"
#undef WORD
#undef BYTE
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifndef _WIN32
#include <strings.h>
#endif
#include <errno.h>
#include <fcntl.h>
#include <sys/stat.h>
#ifndef _WIN32
#include <unistd.h>
#else
#include <windows.h>
#include <io.h>
#include <process.h>
#define strcasecmp _stricmp
#define strncasecmp _strnicmp
#define fsync _commit
static int gs_host_truncate(int fd,int64_t size) {
    HANDLE h=(HANDLE)_get_osfhandle(fd);LARGE_INTEGER at;at.QuadPart=size;
    return SetFilePointerEx(h,at,NULL,FILE_BEGIN)&&SetEndOfFile(h)?0:-1;
}
#define ftruncate(fd, size) gs_host_truncate(fd, size)
#define lstat _stat64
#define stat _stat64
#define fstat _fstat64
#define getpid _getpid
#define S_ISREG(mode) (((mode)&_S_IFMT)==_S_IFREG)
#define F_OK 0
#define R_OK 4
#endif
#include <sys/stat.h>
#include <time.h>
#ifdef GS_HOST_TEST
#ifdef _WIN32
typedef HANDLE GsThread;
#else
#include <pthread.h>
typedef pthread_t GsThread;
#endif
#else
#include <orbis/libkernel.h>
typedef OrbisPthread GsThread;
#endif
typedef struct { int template_id, connection, request, status; int64_t start,end,total,length,date_epoch,last_modified_epoch; char origin[512], effective[8192], location[8192], source[8192], etag[512], etag_value[512], date[64], last_modified[64]; int etag_present, etag_single, date_present, date_valid, last_modified_present, last_modified_single, last_modified_valid, retry_after, retry_after_invalid_time, reused, aborted, ssl_error, native_errno, retire, recv_block, recv_block_rc; unsigned ssl_verify, open_wait_ms, interruptions; const char *stage; volatile int request_gate; uint64_t open_started; } GsHttp;
typedef struct {
    uint32_t magic, version, chunk_size, count;
    uint64_t total;
    unsigned char identity[32], expected[32];
    unsigned char source_identity[32];
    char etag[512];
    uint32_t single, crc;
} GsMapHeader;
typedef struct { unsigned char done, reserved[7], hash[32]; } GsChunk;
typedef enum {
    GS_FAILURE_NONE=0, GS_FAILURE_CANCELED, GS_FAILURE_ADMISSION,
    GS_FAILURE_TRANSPORT, GS_FAILURE_COOLDOWN, GS_FAILURE_LINK,
    GS_FAILURE_TLS, GS_FAILURE_PROTOCOL, GS_FAILURE_STORAGE
} GsFailureClass;
typedef struct {
    int handle, fd, active, limit, single, stop, finishing, preparing, lock_fd, resume_requested;
    int checkpointing, checkpoint_dirty, checkpoint_waiters, checkpoint_failed, target_limit, requested_limit, clean_chunks, verification_retries;
    int preparation_started, preparation_joining, pkg_integrity;
    // The package's own digests can audit every payload byte (see gs_store_open).
    int pkg_capable;
    GsThread preparation_thread;
    GsHttp preparation_http;
    unsigned stream_failed_lanes;
    char verification_error[220];
    char storage_error[220], storage_stage[32];
    int storage_errno;
    char url[8192], effective[8192], bearer[2048], dest[1024], part[1100], map[1100], lock_path[1100], origin[512], etag[512], if_range[512], last_modified[64];
    char validator_kind[24], fallback_reason[48];
    char title[16], content[49], expected[65];
    GsMapHeader header;
    GsChunk *chunks, *checkpoint_chunks;
    unsigned char *claims, *attempts;
    uint64_t *retry_at, *accepted;
    uint64_t checkpoint_at, next_retry, recovery_at, checkpoint_ms;
    uint64_t failure_window, backoff_until;
    unsigned failed_lanes;
    // Adaptive lane governor: the most lanes that held without a reset burst or
    // a wasted raise (0 until either happens), when probing above it resumes,
    // and the real-time speed window a raise is judged by.
    int ceiling, probe_from;
    unsigned ceiling_hold;
    uint64_t ceiling_until, probe_rate, rate_at, rate_bytes;
    uint64_t useful_bytes, useful_progress_at, tls_failure_started, recovery_deadline;
    unsigned tls_failures, tls_failed_lanes;
    GsFailureClass recovery_class;
    char recovery_detail[160];
    uint64_t request_ms, read_ms, write_ms, hash_ms, log_at;
    uint64_t read_calls, write_calls, write_bytes, buffer_wait_ms, read_interruptions;
    uint64_t write_jumps, write_jump_bytes, write_switches, write_max_ms;
    uint64_t validation_done;
    int dirty;
    GsXferStatus status;
} GsJobState;
void gs_lock(volatile int *lock);
void gs_unlock(volatile int *lock);
void gs_sleep(unsigned milliseconds);
uint64_t gs_clock(void);
int gs_thread_start(GsThread *thread, void *(*run)(void *), void *argument);
void gs_thread_join(GsThread thread);
int gs_http_init(int context);
void gs_http_set_module(int module);
void gs_http_reset(GsHttp *http);
void gs_http_close(GsHttp *http);
void gs_http_abort(GsHttp *http);
int gs_http_open(GsHttp *http, const char *url, const char *bearer, int64_t start, int64_t end, const char *if_range);
int gs_http_read(GsHttp *http, void *data, unsigned length);
void gs_http_clear_abort(GsHttp *http);
int gs_http_strong_etag(const char *etag);
int gs_http_parse_response_validators(GsHttp *http, const char *headers, size_t length);
int gs_http_range_validator(const GsHttp *http, char *output, size_t capacity);
int gs_http_origin(const char *url, char *output, size_t size);
int gs_range_parse(const char *value, int64_t *start, int64_t *end, int64_t *total);
int gs_store_open(GsJobState *job, const unsigned char identity[32], const unsigned char source_identity[32], const char *etag);
int gs_store_checkpoint(GsJobState *job);
int gs_store_snapshot(GsJobState *job, GsMapHeader header, const GsChunk *chunks);
int gs_store_write(int fd, const void *data, size_t length, uint64_t offset);
int gs_store_read(int fd, void *data, size_t length, uint64_t offset);
int gs_store_size(int fd, int64_t *size);
void gs_store_close(GsJobState *job);
int gs_verify_header(const unsigned char *header, size_t length, int64_t total, const char *title, const char *content);
int gs_pkg_integrity_header(const unsigned char *header, size_t length, int64_t total);
int gs_verify_file(GsJobState *job, unsigned char *buffer);
int gs_finalize_file(GsJobState *job, unsigned char *buffer);
void gs_digest(const void *data, size_t length, unsigned char output[32]);
int gs_unhex(const char *hex, unsigned char output[32]);
uint32_t gs_crc(const void *data, size_t length, uint32_t crc);
void gs_transfer_log(const GsJobState *job, const char *event);
#endif
