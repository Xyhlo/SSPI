/* Durable staged jobs share the same bounded transport pool as the app.
 * The resident owns intake, dependency ordering and BGFT admission. */
#include "../../https/http_range.h"
#include "gs_queue_policy.h"
#define GS_STAGE_COUNT 16
#define GS_STAGE_ACTIVE_JOBS 1
#define GS_STAGE_LANES 4
#define GS_STAGE_GLOBAL_LANES GS_XFER_LANES
#define GS_STAGE_CHUNK (16LL * 1024 * 1024)
#define GS_STAGE_BLOCK (512 * 1024)
#define GS_STAGE_CHECKPOINT_BATCH 16

typedef struct GsStage GsStage;
#if GS_HAVE_CURL_BULK
typedef struct { void *curl; unsigned char *buffer; } GsStageHttp;
typedef void *GsStageRequest;
#define GS_STAGE_HTTP_INIT { NULL, NULL }
#define GS_STAGE_TRANSPORT "libcurl-resident-ranges"
#else
typedef struct { int template_id, connection; unsigned char *buffer; } GsStageHttp;
typedef int GsStageRequest;
#define GS_STAGE_HTTP_INIT { -1, -1, NULL }
#define GS_STAGE_TRANSPORT "sceHttp-shared-chunks-api3"
#endif
typedef struct { GsStage *stage; int ordinal; OrbisPthread thread; int created; } GsStageLane;
struct GsStage {
    GsJob job;
    int slot, created, running, pending, canceled, paused, release, failed, active, range_mode;
    int fd, journal_fd, chunks, completed, next;
    volatile int lock, checkpoint_lock;
    int64_t done;
    unsigned char *map, *chunk_hashes, header_identity[32];
    char part[1100], journal[1150], identity[65], legacy_identity[65], validator[512], error[256], state[24];
    int validator_kind;
    int install_owned;
    int install_retries;
    char logged_state[24], logged_error[256];
    int transfer_verified;
    int transfer_handle, streaming, stream_attempted, stream_complete, notified;
    char bgft_detail[180];
    uint64_t install_retry_at;
    int control_epoch;
    GsStageRequest requests[GS_STAGE_LANES + 1];
    OrbisPthread thread;
    GsStageLane lanes[GS_STAGE_LANES];
    uint64_t started_at, sample_at, metrics_at; int64_t sample_done;
    uint64_t read_us, write_us, hash_us, sync_us, request_us, checkpoint_at;
    int64_t wire_bytes;
    int pending_chunks, body_readers, last_http_status, last_transport_error, connections_opened, checkpoints;
};
static GsStage gs_stages[GS_STAGE_COUNT];
static volatile int gs_stage_budget_lock;
static int gs_stage_leases;
static uint64_t gs_stage_install_after;

static void gs_stage_lock(volatile int *lock)
{ while (__sync_lock_test_and_set(lock, 1)) sceKernelUsleep(100); }
static void gs_stage_unlock(volatile int *lock) { __sync_lock_release(lock); }
static void gs_stage_path(int slot, const char *kind, char *path, size_t size)
{
    char leaf[96];
    int length = snprintf(leaf, sizeof(leaf), "/transfer-%d.%s", slot, kind);
    if (length < 0 || length >= (int)sizeof(leaf) || gs_ipc_path(path, size, leaf)) path[0] = 0;
}

static void gs_archive_control(void)
{
    char path[128];
    int action = 0;
    // Read this job's durable intent from the extraction thread as well as the
    // listener. Cancellation must not wait behind listener/install polling.
    for (int i = 0; i < GS_STAGE_COUNT; i++) {
        if (strcmp(gs_stages[i].job.id, gs_job.id) ||
            strcmp(gs_stages[i].job.generation, gs_job.generation)) continue;
        gs_stage_path(i, "control", path, sizeof(path));
        action = gs_read_control_action(path);
        break;
    }
    if (!action && !gs_ipc_path(path, sizeof(path), "/control.txt")) action = gs_read_control_action(path);
    if (action >= 3) {
        if (!gs_canceled) gs_log_write("resident", "archive-control job=%s action=%s accepted=1", gs_job.id, action == 4 ? "release" : "cancel");
        gs_canceled = 1;
        if (action == 4) gs_release = 1;
    } else if (action == 1) gs_paused = 1;
    else if (action == 2) gs_paused = 0;
}

static void gs_stage_status(GsStage *stage, const char *state, const char *error)
{
    char path[1100], body[1800], id[260], encoded[900];
    gs_stage_path(stage->slot, "status", path, sizeof(path));
    gs_b64encode((const unsigned char *)stage->job.id, strlen(stage->job.id), id, sizeof(id));
    gs_stage_lock(&stage->lock);
    if (!stage->job.native_bgft && state && (!strcmp(state, "feeding") || !strcmp(state, "paused")) &&
        strcmp(stage->state, "queued") && strcmp(stage->state, "ready") && strcmp(stage->state, "feeding") && strcmp(stage->state, "paused") && stage->state[0]) {
        if (!strcmp(stage->state, "storage-wait") ||
            (!strcmp(stage->state, "validating") && !stage->install_owned)) stage->state[0] = 0;
        else { gs_stage_unlock(&stage->lock); return; }
    }
    if (!error) error = "";
    if (state && (strcmp(state, stage->logged_state) || strcmp(error, stage->logged_error))) {
        gs_log_write("download", "staged slot=%d job=%s generation=%s state=%s done=%lld total=%lld error=%s",
            stage->slot, stage->job.id, stage->job.generation, state, (long long)stage->done, (long long)stage->job.total, error);
        snprintf(stage->logged_state, sizeof(stage->logged_state), "%s", state);
        snprintf(stage->logged_error, sizeof(stage->logged_error), "%s", error);
    }
    if (gs_b64encode((const unsigned char *)error, strlen(error), encoded, sizeof(encoded)) < 0) encoded[0] = 0;
    if (state) snprintf(stage->state, sizeof(stage->state), "%s", state);
    int64_t published = stage->done;
    if (published >= stage->job.total && (!strcmp(stage->state, "feeding") || !strcmp(stage->state, "ready") ||
        !strcmp(stage->state, "paused") || !strcmp(stage->state, "validating"))) published = stage->job.total - 1;
    int extracting = !strcmp(stage->state, "extracting") && !strcmp(stage->job.id, gs_job.id) &&
        !strcmp(stage->job.generation, gs_job.generation);
    snprintf(body, sizeof(body), "1\n%s\n%s\n%lld\n%lld\n0\n%s\n%d\n%d\n%lld\n%s\n%s\n%llu\n", id, stage->state,
        (long long)(extracting ? gs_done : published), (long long)(extracting ? gs_job.total : stage->job.total), encoded, stage->active, stage->job.auto_install, (long long)stage->wire_bytes,
        stage->job.generation, GS_BUILD_ID, (unsigned long long)g_epoch);
    write_telemetry(path, body);
    uint64_t now = sceKernelGetProcessTime() / 1000;
    if (!strcmp(stage->state, "feeding") && now - stage->metrics_at < 2000) { gs_stage_unlock(&stage->lock); return; }
    stage->metrics_at = now;
    gs_stage_path(stage->slot, "metrics", path, sizeof(path));
    snprintf(body, sizeof(body), "version=1\nworker=" GS_WORKER_VERSION "\ntransport=" GS_STAGE_TRANSPORT "\nstate=%s\nrange_mode=%d\nrequested_lanes=%d\nleases=%d\nbody_readers=%d\nwire_bytes=%lld\naccepted_bytes=%lld\npending_chunks=%d\nread_ms=%llu\nwrite_ms=%llu\nhash_ms=%llu\nfsync_ms=%llu\nrequest_ms=%llu\nconnections_opened=%d\ncheckpoints=%d\nlast_http_status=%d\nlast_transport_error=0x%08X\nlimit_bytes_s=%lld\nsample_unix=%lld\n",
        stage->state, stage->range_mode, stage->job.ranges, stage->active, stage->body_readers,
        (long long)stage->wire_bytes, (long long)stage->done, stage->pending_chunks,
        (unsigned long long)(stage->read_us / 1000), (unsigned long long)(stage->write_us / 1000),
        (unsigned long long)(stage->hash_us / 1000), (unsigned long long)(stage->sync_us / 1000),
        (unsigned long long)(stage->request_us / 1000), stage->connections_opened, stage->checkpoints,
        stage->last_http_status, (unsigned)stage->last_transport_error, (long long)0, (long long)time(NULL));
    write_telemetry(path, body);
    gs_stage_unlock(&stage->lock);
}

static int gs_stage_read_job(int slot, GsJob *job)
{
    char path[1100], line[80]; const char *end; int rc = -1; int64_t lanes, kind;
    gs_stage_path(slot, "job", path, sizeof(path)); FILE *file = fopen(path, "rb");
    if (!file) return -1;
    memset(job, 0, sizeof(*job));
    if (gs_line(file, line, sizeof(line)) || (strcmp(line, "6") && strcmp(line, "7") && strcmp(line, "10") && strcmp(line, "13") && strcmp(line, "16") && strcmp(line, "18"))) goto done;
    job->native_bgft = !strcmp(line, "16");
    job->borrowed_source = !strcmp(line, "18");
    int has_password = !strcmp(line, "13") || job->borrowed_source;
    int bound_storage = !strcmp(line, "10") || has_password || job->native_bgft;
    job->auto_install = !strcmp(line, "7");
    if (
        gs_encoded_line(file, job->id, sizeof(job->id)) || gs_encoded_line(file, job->url, sizeof(job->url)) ||
        gs_encoded_line(file, job->destination, sizeof(job->destination)) || gs_encoded_line(file, job->title, sizeof(job->title)) ||
        gs_encoded_line(file, job->sha, sizeof(job->sha)) || gs_encoded_line(file, job->content, sizeof(job->content)) ||
        gs_line(file, line, sizeof(line)) || parse_number(line, &job->total, &end) || *end ||
        gs_line(file, line, sizeof(line)) || parse_number(line, &lanes, &end) || *end ||
        gs_line(file, line, sizeof(line)) || parse_number(line, &kind, &end) || *end) goto done;
    if ((job->auto_install || bound_storage) && gs_encoded_line(file, job->after, sizeof(job->after))) goto done;
    if (bound_storage) {
        if (gs_line(file, job->generation, sizeof(job->generation)) || !gs_hex_token(job->generation) ||
            gs_line(file, job->storage_token, sizeof(job->storage_token)) ||
            gs_line(file, line, sizeof(line)) || (strcmp(line, "0") && strcmp(line, "1"))) goto done;
        job->auto_install = !strcmp(line, "1");
    }
    if (has_password && ((!job->borrowed_source && kind != 0) || gs_encoded_line(file, job->password, sizeof(job->password)))) goto done;
    if (has_password && (gs_read_password_fallbacks(file, job) || (kind && job->password_fallback_count))) goto done;
    if (job->total < (job->borrowed_source && !kind ? 8 : 0x1000) || job->total > 8LL * 1024 * 1024 * 1024 * 1024 ||
        lanes < 1 || lanes > 64 || (kind != 0 && (kind < 6 || kind > 8 || strlen(job->content) != 36)) ||
        !job->id[0] || (job->title[0] && strlen(job->title) != 9)) goto done;
    for (const char *p = job->id; *p; p++)
        if (!((*p >= 'a' && *p <= 'z') || (*p >= 'A' && *p <= 'Z') || (*p >= '0' && *p <= '9') || *p == '_' || *p == '-')) goto done;
    if (!strcmp(job->after, job->id)) goto done;
    for (const char *p = job->after; *p; p++)
        if (!((*p >= 'a' && *p <= 'z') || (*p >= 'A' && *p <= 'Z') || (*p >= '0' && *p <= '9') || *p == '_' || *p == '-')) goto done;
    int external = job->borrowed_source ? gs_local_source_root(job->destination, job->storage_root, sizeof(job->storage_root))
        : gs_staging_root(job->destination, job->storage_root, sizeof(job->storage_root));
    if (external < 0 || (external && (!bound_storage || !gs_hex_token(job->storage_token)))) goto done;
    if (job->native_bgft && (external || job->sha[0] || (kind != 6 && kind != 8) || !job->auto_install)) goto done;
    if (job->borrowed_source) {
        if (job->url[0] || job->sha[0] || !job->auto_install || (kind && job->password[0])) goto done;
    } else {
    if (strncmp(job->url, "https://", 8) && strncmp(job->url, "http://", 7)) goto done;
    const char *authority = strstr(job->url, "://") + 3, *slash = strchr(authority, '/');
    for (const char *p = job->url; *p; p++) if ((unsigned char)*p < 32 || *p == 127) goto done;
    for (const char *p = authority; *p && (!slash || p < slash); p++) if (*p == '@') goto done;
    }
    if (job->sha[0]) {
        if (strlen(job->sha) != 64) goto done;
        for (int i = 0; i < 64; i++) if (!strchr("0123456789abcdefABCDEF", job->sha[i])) goto done;
    }
    job->ranges = lanes > GS_XFER_LANES ? GS_XFER_LANES : (int)lanes;
    job->expected_kind = (int)kind;
    if (gs_resolve_job_storage(job)) goto done;
    rc = 0;
done:
    fclose(file); return rc;
}

static int gs_stage_stopped(GsStage *stage) { return g_stop || stage->canceled || stage->failed; }

static int gs_stage_validate(GsStage *stage, const char *path)
{
    unsigned char header[0x1000] = {0};
    int fd = open(path, O_RDONLY | O_NOFOLLOW);
    if (fd < 0) { snprintf(stage->error, sizeof(stage->error), "Completed file cannot be opened (errno %d)", errno); return -1; }
    const char *error = NULL;
    int64_t actual_size = sceKernelLseek(fd, 0, SEEK_END);
    if (actual_size != stage->job.total) error = "Completed file size does not match the download";
    else if (sceKernelPread(fd, header, actual_size < (int64_t)sizeof(header) ? (size_t)actual_size : sizeof(header), 0) !=
        (actual_size < (int64_t)sizeof(header) ? actual_size : (int64_t)sizeof(header))) error = "Completed file header is incomplete or unreadable";
    else if (!stage->job.expected_kind) {
        if (memcmp(header, "Rar!\x1a\x07\x00", 7) && memcmp(header, "Rar!\x1a\x07\x01\x00", 8) &&
            memcmp(header, "PK\x03\x04", 4) && memcmp(header, "PK\x05\x06", 4) &&
            memcmp(header, "7z\xbc\xaf\x27\x1c", 6)) error = "Completed file is not a supported RAR, ZIP or 7z archive";
    } else {
        error = gs_pkg_header_error(header, actual_size, stage->job.total, stage->job.title, stage->job.content);
        int actual_kind = error ? 0 : gs_pkg_kind(path);
        if (!error && actual_kind != stage->job.expected_kind) {
            // Sources sometimes label an update as the base game; name both kinds.
            const char *wanted = stage->job.expected_kind == 6 ? "a base game" : stage->job.expected_kind == 8 ? "an update" : "DLC";
            const char *found = actual_kind == 6 ? "a base game" : actual_kind == 8 ? "an update" : actual_kind == 7 ? "DLC" : "not a recognized PS4 package type";
            close(fd);
            snprintf(stage->error, sizeof(stage->error), "PKG type mismatch: the source lists this as %s, but the file is %s. Choose another mirror; complete file retained", wanted, found);
            return -1;
        }
    }
    close(fd);
    if (error) { snprintf(stage->error, sizeof(stage->error), "%s; complete file retained", error); return -1; }
    if (stage->job.sha[0] && sspi_xfer_verify_local(path, stage->job.destination, stage->job.total,
        stage->job.expected_kind ? stage->job.title : "", stage->job.content, stage->job.sha, stage->error, sizeof(stage->error))) return -1;
    /* A successful structural check is not a body/PFS cryptographic proof. */
    return 0;
}

#include "gs_native_bgft.inc"

static void *gs_stage_worker(void *opaque)
{
    GsStage *stage = opaque; int rc = -1, handle = -1, storage_lost = 0, parked = 0;
    gs_worker_background_priority("stage");
    stage->fd = stage->journal_fd = -1;
    snprintf(stage->part, sizeof(stage->part), "%s.part", stage->job.destination);
 retry_storage:
    storage_lost = 0;
    while (!gs_stage_stopped(stage)) {
        GsStorageError storage_failure;
        int storage_check = gs_storage_check_error(stage->job.storage_root, stage->job.storage_token, &storage_failure);
        if (storage_check == 1) break;
        char storage_error[256];
        gs_storage_wait_detail(storage_error, sizeof(storage_error), storage_check, &storage_failure);
        gs_stage_status(stage, "storage-wait", storage_error); sceKernelUsleep(500000);
    }
    if (gs_stage_stopped(stage)) goto done;
    if (stage->job.borrowed_source) {
        if (!gs_local_source_check(stage->job.destination, stage->job.storage_root, stage->job.storage_token))
            snprintf(stage->error, sizeof(stage->error), "Selected USB file path is unavailable or linked; original retained");
        else if (!gs_stage_validate(stage, stage->job.destination)) { stage->done = stage->job.total; rc = 0; }
        goto done;
    }
    if (stage->job.native_bgft) {
        gs_stage_status(stage, "ready", "Checking package metadata for PS4 download");
        if (!gs_native_metadata(stage)) { rc = 0; goto done; }
        if (stage->canceled || g_stop) goto done;
        char fallback[1150]; snprintf(fallback, sizeof(fallback), "%s.bgft-fallback", stage->job.destination);
        if (write_atomic(fallback, stage->job.generation)) {
            gs_stage_status(stage, "failed", "Could not save the download fallback; retry safely"); goto done;
        }
        stage->job.native_bgft = 0;
        gs_log_write("resident", "native-bgft-fallback job=%s reason=metadata-or-range-unsupported", stage->job.id);
        gs_stage_status(stage, "ready", "Using SSPI download for this package");
    }
    if (!access(stage->job.destination, F_OK)) {
        stage->done = stage->job.total;
        if (!gs_stage_validate(stage, stage->job.destination)) rc = 0;
        goto done;
    }
    /* A complete checkpoint is local ownership, even if its CDN URL is gone. */
    if (sspi_xfer_durable(stage->job.destination) == stage->job.total) {
        stage->done = stage->job.total;
        if (gs_stage_validate(stage, stage->part)) goto done;
        if (gs_storage_matches(stage->job.storage_root, stage->job.storage_token) &&
            !rename(stage->part, stage->job.destination)) {
            char map[1150]; snprintf(map, sizeof(map), "%s.map", stage->job.destination); unlink(map);
            stage->done = stage->job.total; rc = 0;
        } else snprintf(stage->error, sizeof(stage->error), "Completed file could not be renamed; input retained (errno %d)", errno);
        goto done;
    }
    if (sspi_xfer_init(gs_http)) { snprintf(stage->error, sizeof(stage->error), "Firmware transfer pool initialization failed"); goto done; }
    handle = sspi_xfer_start(stage->job.url, NULL, stage->job.destination,
        stage->job.expected_kind ? stage->job.title : "", stage->job.content, stage->job.sha, stage->job.ranges);
    if (handle < 0) { snprintf(stage->error, sizeof(stage->error), "Native transfer could not start: %d", handle); goto done; }
    __atomic_store_n(&stage->transfer_handle, handle, __ATOMIC_RELEASE);
    for (;;) {
        GsXferStatus status;
        if (sspi_xfer_poll(handle, &status)) break;
        stage->done = status.done; stage->active = status.lanes; stage->wire_bytes = status.network_bytes;
        if (!stage->notified && status.network_bytes > 0) {
            stage->notified = 1;
            gs_download_notification(stage->job.title[0] ? stage->job.title : "Package",
                stage->job.expected_kind ? "Downloading in background; PS4 installation follows the download."
                    : "Archive downloading in background; extraction and installation follow verification.");
        }
        if (g_stop || stage->canceled) { sspi_xfer_pause(handle); break; }
        if (!gs_storage_matches(stage->job.storage_root, stage->job.storage_token)) {
            sspi_xfer_pause(handle);
            storage_lost = 1;
            snprintf(stage->error, sizeof(stage->error), "Selected staging drive disconnected; reconnect it and resume");
            break;
        }
        /* Release the transfer lease only after the engine has drained its lanes.
         * Keeping this worker alive while paused starved every other queued file. */
        if (stage->paused && !__atomic_load_n(&stage->streaming, __ATOMIC_ACQUIRE)) {
            sspi_xfer_pause(handle); parked = 1; break;
        }
        if (stage->paused) sspi_xfer_pause(handle);
        else if (status.state == GS_PAUSED) sspi_xfer_resume(handle);
        if (status.total > 0 && status.total != stage->job.total) { snprintf(stage->error, sizeof(stage->error), "Provider returned a different total size"); break; }
        if (status.state == GS_FAILED) { snprintf(stage->error, sizeof(stage->error), "%s", status.error); break; }
        if (status.state == GS_COMPLETE) {
            gs_stage_status(stage, "validating", "Download complete; checking package metadata");
            if (gs_stage_validate(stage, stage->part)) break;
            __atomic_store_n(&stage->stream_complete, 1, __ATOMIC_RELEASE);
            while (__atomic_load_n(&stage->streaming, __ATOMIC_ACQUIRE) && !gs_stage_stopped(stage)) sceKernelUsleep(100000);
            if (gs_stage_stopped(stage)) break;
            if (sspi_xfer_destroy(handle)) break; handle = -1;
            __atomic_store_n(&stage->transfer_handle, -1, __ATOMIC_RELEASE);
            if (!gs_storage_matches(stage->job.storage_root, stage->job.storage_token) ||
                access(stage->job.destination, F_OK)==0 || rename(stage->part, stage->job.destination)) break;
            char map[1100]; snprintf(map, sizeof(map), "%s.map", stage->job.destination); unlink(map);
            stage->done = stage->job.total; rc = 0; break;
        }
        char bgft_detail[180];
        gs_stage_lock(&stage->lock);
        snprintf(bgft_detail, sizeof(bgft_detail), "%s", stage->bgft_detail);
        gs_stage_unlock(&stage->lock);
        /* GS_QUEUED is the engine's preparation phase: admission, source probe
         * and staging-file open. Forward its detail every poll so an uncached
         * provider preparation is visible instead of a silent 0% transfer. */
        const char *detail = NULL;
        if (!stage->paused && status.state == GS_QUEUED) detail = status.error[0] ? status.error : bgft_detail;
        else if (!stage->paused && status.state != GS_VALIDATING && bgft_detail[0]) detail = bgft_detail;
        gs_stage_status(stage, stage->paused ? "paused" : status.state == GS_VALIDATING ? "validating" : "feeding", detail);
        sceKernelUsleep(250000);
    }
 done:
    if (rc && __atomic_load_n(&stage->streaming, __ATOMIC_ACQUIRE)) {
        __atomic_store_n(&stage->stream_complete, -1, __ATOMIC_RELEASE);
        while (__atomic_load_n(&stage->streaming, __ATOMIC_ACQUIRE) && !g_stop) sceKernelUsleep(100000);
    }
    if (handle >= 0) { sspi_xfer_pause(handle); while (sspi_xfer_destroy(handle)==-2) sceKernelUsleep(50000); }
    __atomic_store_n(&stage->transfer_handle, -1, __ATOMIC_RELEASE);
    handle = -1;
    if (storage_lost && !gs_stage_stopped(stage)) { stage->error[0] = 0; goto retry_storage; }
    stage->active = 0;
    if (stage->canceled || g_stop) gs_stage_status(stage, "canceled", NULL);
    else if (parked) { stage->pending = 1; gs_stage_status(stage, stage->paused ? "paused" : "queued", "Verified chunks retained; transfer slot released"); }
    else if (!rc) gs_stage_status(stage, "staged", NULL);
    else gs_stage_status(stage, "failed", stage->error[0] ? stage->error : "Native transfer failed; committed chunks retained");
    __atomic_store_n(&stage->running, 0, __ATOMIC_RELEASE); return NULL;
}

static void gs_stage_control(GsStage *stage)
{
    char path[1100], line[300], id[192]; gs_stage_path(stage->slot, "control", path, sizeof(path));
    FILE *file = fopen(path, "rb"); if (!file) return;
    if (!gs_line(file, line, sizeof(line)) && (!strcmp(line, "1") || !strcmp(line, "2"))) {
        int version = atoi(line); char generation[80];
        if (gs_encoded_line(file, id, sizeof(id)) || strcmp(id, stage->job.id) || gs_line(file, line, sizeof(line)) ||
            (version == 2 && (gs_line(file, generation, sizeof(generation)) || strcmp(generation, stage->job.generation))) ||
            (version == 1 && stage->job.generation[0])) { fclose(file); return; }
        int was_paused = stage->paused, was_canceled = stage->canceled;
        if (!strcmp(line, "pause")) stage->paused = 1;
        if (!strcmp(line, "resume")) stage->paused = 0;
        if (!strcmp(line, "cancel")) stage->canceled = 1;
        if (!strcmp(line, "release")) { stage->canceled = 1; stage->release = 1; }
        if ((!was_paused && stage->paused) || (!was_canceled && stage->canceled)) {
            __atomic_add_fetch(&stage->control_epoch, 1, __ATOMIC_RELEASE);
            gs_stage_lock(&stage->lock);
            for (int i = 0; i <= GS_STAGE_LANES; i++) {
#if GS_HAVE_CURL_BULK
                if (stage->requests[i]) sspi_curl_abort(stage->requests[i]);
#else
                if (stage->requests[i] && gs_abort_request) gs_abort_request(stage->requests[i] - 1);
#endif
            }
            gs_stage_unlock(&stage->lock);
        }
        // Commands are generation-bound desired state; a read must never unlink
        // a newer atomic replacement, and a restart must retain a manual pause.
    }
    fclose(file);
}

static int gs_stage_owned(void)
{
    for (int i = 0; i < GS_STAGE_COUNT; i++) {
        char path[1100]; gs_stage_path(i, "job", path, sizeof(path));
        if (gs_stages[i].job.id[0] || !access(path, F_OK)) return 1;
    }
    return 0;
}

#include "gs_stage_dependency.h"

static int gs_stage_stream_eligible(GsStage *stage)
{
    // AppInstUtil consumes a complete local add-on. Never submit a sparse DLC
    // during metadata-only streaming, even if its header is already available.
    if (stage->job.expected_kind == 7) return 0;
    if (!stage->running || stage->stream_attempted || !stage->job.auto_install ||
        stage->job.expected_kind < 6 || stage->job.expected_kind > 8 || stage->paused || stage->canceled ||
        strcmp(stage->state, "feeding") || !gs_stage_dependency_ready(stage)) return 0;
    int handle = __atomic_load_n(&stage->transfer_handle, __ATOMIC_ACQUIRE);
    int64_t prefix = sspi_xfer_readable(handle, 0);
    if (prefix < 0x1000) return 0;
    unsigned char header[0x1000], digest[32];
    int fd = open(stage->part, O_RDONLY | O_NOFOLLOW); if (fd < 0) return 0;
    int good = sceKernelPread(fd, header, sizeof(header), 0) == sizeof(header);
    if (!good || memcmp(header, "\x7f\x43\x4e\x54", 4) ||
        memcmp(header + 0x40, stage->job.content, 36) ||
        gs_be64(header + 0x430) != (uint64_t)stage->job.total) { close(fd); return 0; }
    // BGFT needs the entry table and SFO to register, not the entire metadata
    // body. Some packages place large assets there. Gate every served byte below.
    uint32_t count = gs_pkg_be32(header + 0x10), table = gs_pkg_be32(header + 0x18);
    if (!count || count > 4096 || table < 0x1000 || (uint64_t)table + (uint64_t)count * 32 > (uint64_t)prefix)
    { close(fd); return 0; }
    int have_sfo = 0;
    for (uint32_t i = 0; i < count; i++) {
        unsigned char entry[32];
        if (sceKernelPread(fd, entry, sizeof(entry), (off_t)table + i * 32) != sizeof(entry)) { close(fd); return 0; }
        if (gs_pkg_be32(entry) != 0x1000) continue;
        uint32_t offset = gs_pkg_be32(entry + 16), size = gs_pkg_be32(entry + 20);
        have_sfo = size >= 20 && size <= 65536 && (uint64_t)offset + size <= (uint64_t)prefix;
        break;
    }
    close(fd);
    if (!have_sfo) return 0;
    SHA256_CTX hash; sha256_init(&hash); sha256_update(&hash, header, 0xfe0); sha256_final(&hash, digest);
    return !memcmp(digest, header + 0xfe0, 32);
}

static int64_t gs_stage_stream_readable(int64_t offset)
{
    if (gs_stream_slot < 0 || gs_stream_slot >= GS_STAGE_COUNT) return -1;
    GsStage *stage = &gs_stages[gs_stream_slot];
    int complete = __atomic_load_n(&stage->stream_complete, __ATOMIC_ACQUIRE);
    if (complete < 0 || stage->canceled || g_stop) return -1;
    if (stage->paused || !gs_storage_matches(stage->job.storage_root, stage->job.storage_token)) return 0;
    int64_t available = sspi_xfer_readable(__atomic_load_n(&stage->transfer_handle, __ATOMIC_ACQUIRE), offset);
    if (available < 0) return -1;
    // BGFT must not finish its transfer before full-file validation passes.
    int64_t end = stage->job.total - (complete == 1 ? 0 : 1);
    if (offset >= end) return 0;
    return available > end - offset ? end - offset : available;
}

static int gs_stage_stream_finished(void)
{
    if (gs_stream_slot < 0) return 1;
    GsStage *stage = &gs_stages[gs_stream_slot];
    if (stage->canceled || g_stop) return -1;
    int complete = __atomic_load_n(&stage->stream_complete, __ATOMIC_ACQUIRE);
    if (complete < 0) return -1;
    if (!complete && sspi_xfer_readable(__atomic_load_n(&stage->transfer_handle, __ATOMIC_ACQUIRE), 0) < 0) return -1;
    return complete;
}

static int gs_stage_stream_worker(void)
{
    GsStage *stage = &gs_stages[gs_stream_slot];
    char directory[1100], packages[1100], leaf[256]; int result = -1;
    snprintf(leaf, sizeof(leaf), "/packages/%s", gs_job.id);
    if (gs_ipc_path(packages, sizeof(packages), "/packages") || gs_ipc_path(directory, sizeof(directory), leaf)) goto done;
    mkdir(packages, 0700); mkdir(directory, 0700);
    if (!gs_storage_matches(gs_job.storage_root, gs_job.storage_token) ||
        gs_check_metadata(gs_job.destination, gs_job.expected_kind)) goto done;
    gs_validated = 1; // Header/metadata only; the HTTP route separately gates every body byte.
    gs_log_write("resident", "bgft-stream job=%s generation=%s state=registering total=%lld", gs_job.id,
        gs_job.generation, (long long)gs_job.total);
    result = gs_install_archive_pkg(directory, 1, gs_job.expected_kind);
    if (!result && gs_stage_stream_finished() != 1) result = -1;
done:
    if (result) {
        int stopped = gs_canceled || stage->canceled ? gs_cancel_task() : gs_park_task();
        if (stopped) gs_write_status("failed", "BGFT streaming stop unconfirmed; task and data retained");
        else if (gs_canceled) gs_write_status("canceled", NULL);
        else if (strcmp(gs_state, "failed")) gs_write_status("failed", "BGFT stream interrupted; resident download retained for local installation");
    } else gs_write_status("installed", NULL);
    __atomic_store_n(&stage->streaming, 0, __ATOMIC_RELEASE);
    return result;
}

static int gs_stage_transfer_active(void)
{
    for (int i = 0; i < GS_STAGE_COUNT; i++)
        if (__atomic_load_n(&gs_stages[i].running, __ATOMIC_ACQUIRE) || gs_stages[i].created) return 1;
    return 0;
}

static int gs_stage_can_install(const GsJob *candidate)
{
    if (!candidate->local_only || gs_stage_transfer_active()) return 0;
    for (int i = 0; i < GS_STAGE_COUNT; i++) {
        GsStage *stage = &gs_stages[i];
        if (stage->job.auto_install && !strcmp(candidate->id, stage->job.id) &&
            gs_native_candidate_path(stage, candidate) &&
            !strcmp(candidate->content, stage->job.content) && candidate->total == stage->job.total &&
            !strcmp(candidate->generation, stage->job.generation) &&
            !stage->canceled && !stage->paused &&
            !stage->running && !stage->created && !stage->pending &&
            (!strcmp(stage->state, "staged") || !strcmp(stage->state, "waiting") || !strcmp(stage->state, "installing")) &&
            gs_stage_dependency_ready(stage)) return 1;
    }
    return 0;
}

static int gs_stage_install_record(GsStage *stage)
{
    char body[16500], id[260], url[11000], destination[1500], title[40], sha[100], content[100], password[350];
    gs_b64encode((const unsigned char *)stage->job.id, strlen(stage->job.id), id, sizeof(id));
    gs_b64encode((const unsigned char *)stage->job.url, strlen(stage->job.url), url, sizeof(url));
    char metadata[1150]; snprintf(metadata, sizeof(metadata), "%s.bgft-meta", stage->job.destination);
    const char *input = stage->job.native_bgft ? metadata : stage->job.destination;
    gs_b64encode((const unsigned char *)input, strlen(input), destination, sizeof(destination));
    gs_b64encode((const unsigned char *)stage->job.title, strlen(stage->job.title), title, sizeof(title));
    gs_b64encode((const unsigned char *)stage->job.sha, strlen(stage->job.sha), sha, sizeof(sha));
    gs_b64encode((const unsigned char *)stage->job.content, strlen(stage->job.content), content, sizeof(content));
    snprintf(body, sizeof(body), "%d\n%s\n%s\n%s\n%s\n%s\n%s\n%lld\n1\n%d\n%s\n%s\n", stage->job.borrowed_source ? 19 : stage->job.native_bgft ? 17 : stage->job.expected_kind ? 11 : (stage->job.password[0] || stage->job.password_fallback_count) ? 15 : 9, id, url, destination,
        title, sha, content, (long long)stage->job.total, stage->job.expected_kind,
        stage->job.generation, stage->job.storage_token);
    if (stage->job.borrowed_source || (!stage->job.expected_kind && (stage->job.password[0] || stage->job.password_fallback_count))) {
        gs_b64encode((const unsigned char *)stage->job.password, strlen(stage->job.password), password, sizeof(password));
        size_t used = strlen(body); snprintf(body + used, sizeof(body) - used, "%s\n", password);
    }
    if (stage->job.password_fallback_count) {
        size_t used = strlen(body);
        snprintf(body + used, sizeof(body) - used, "password-fallbacks-v1\n%d\n", stage->job.password_fallback_count);
        for (int i = 0; i < stage->job.password_fallback_count; i++) {
            gs_b64encode((const unsigned char *)stage->job.password_fallbacks[i], strlen(stage->job.password_fallbacks[i]), password, sizeof(password));
            used = strlen(body); snprintf(body + used, sizeof(body) - used, "%s\n", password);
        }
    }
    char path[128];
    if (gs_ipc_path(path, sizeof(path), "/job.txt")) return -1;
    return write_atomic(path, body);
}

static void gs_stage_idle_status(GsStage *stage, const char *state, const char *error)
{
    if (strcmp(stage->state, state) || strcmp(stage->logged_error, error ? error : ""))
        gs_stage_status(stage, state, error);
}

static void gs_stage_install_detail(const GsStage *stage, char *detail, size_t capacity)
{
    char path[128], line[1000], id[192], generation[80], decoded[640];
    detail[0] = 0;
    if (gs_ipc_path(path, sizeof(path), "/status.txt")) return;
    FILE *file = fopen(path, "rb"); if (!file) return;
    int valid = !gs_line(file, line, sizeof(line)) && !strcmp(line, "1") &&
        !gs_encoded_line(file, id, sizeof(id)) && !strcmp(id, stage->job.id);
    // State, byte counts and served flag precede the detail. Read the atomic
    // worker snapshot so staged telemetry keeps the actual PS4 install phase.
    for (int i = 0; valid && i < 4; i++) valid = !gs_line(file, line, sizeof(line));
    valid = valid && !gs_encoded_line(file, decoded, sizeof(decoded)) &&
        !gs_line(file, line, sizeof(line)) && !gs_line(file, generation, sizeof(generation)) &&
        !strcmp(generation, stage->job.generation);
    fclose(file);
    if (valid) snprintf(detail, capacity, "%s", decoded);
}

static void gs_stage_install_poll(void)
{
    if (gs_job.id[0] && gs_job.local_only) {
        for (int i = 0; i < GS_STAGE_COUNT; i++) {
            GsStage *stage = &gs_stages[i]; if (strcmp(stage->job.id, gs_job.id)) continue;
            stage->install_owned = 1;
            if (!strcmp(gs_state, "installed")) {
                if (__atomic_load_n(&stage->running, __ATOMIC_ACQUIRE)) continue;
                char path[1100], body[512], id[260], content[100], leaf[256];
                snprintf(leaf, sizeof(leaf), "/installed-%s.txt", stage->job.id);
                if (gs_ipc_path(path, sizeof(path), leaf)) { gs_stage_status(stage, "failed", "Installed package receipt could not be saved; dependency queue remains held"); return; }
                gs_b64encode((const unsigned char *)stage->job.id, strlen(stage->job.id), id, sizeof(id));
                const char *installed_content = stage->job.content[0] ? stage->job.content : gs_job.content;
                gs_b64encode((const unsigned char *)installed_content, strlen(installed_content), content, sizeof(content));
                snprintf(body, sizeof(body), "1\n%s\n%s\n%lld\n%s\n", id, content, (long long)stage->job.total, stage->job.generation);
                if (write_atomic(path, body)) { gs_stage_status(stage, "failed", "Installed package receipt could not be saved; dependency queue remains held"); return; }
                stage->done = stage->job.total; gs_stage_status(stage, "installed", NULL);
                gs_stage_install_after = sceKernelGetProcessTime() + 3000000;
                if (!stage->job.borrowed_source && !g_client_count && gs_storage_matches(stage->job.storage_root, stage->job.storage_token)) {
                    /* All extracted children have committed install receipts at this point. */
                    unlink(stage->job.destination);
                    char map[1150]; snprintf(map, sizeof(map), "%s.map", stage->job.destination); unlink(map);
                    snprintf(map, sizeof(map), "%s.sha256-ok", stage->job.destination); unlink(map);
                    snprintf(map, sizeof(map), "%s.sha256-ok.tmp", stage->job.destination); unlink(map);
                    snprintf(map, sizeof(map), "%s.bgft-meta", stage->job.destination); unlink(map);
                    snprintf(map, sizeof(map), "%s.bgft-meta.tail", stage->job.destination); unlink(map);
                    snprintf(map, sizeof(map), "%s.bgft-meta.tmp", stage->job.destination); unlink(map);
                    snprintf(map, sizeof(map), "%s.bgft-fallback", stage->job.destination); unlink(map);
                }
                gs_release = 1;
            } else if (!gs_worker_running && (!strcmp(gs_state, "failed") || !strcmp(gs_state, "canceled"))) {
                char line[1000], message[640] = "Background installation failed; PKG and install journal retained", path[128];
                FILE *status;
                if (gs_ipc_path(path, sizeof(path), "/status.txt")) status = NULL;
                else status = fopen(path, "rb");
                if (status) { int j; for (j = 0; j < 6 && !gs_line(status, line, sizeof(line)); j++) { }
                    if (j == 6) gs_encoded_line(status, message, sizeof(message)); fclose(status); }
                int addon_rejected = strstr(message, "ADDCONT_BROKEN") || strstr(message, "DRM type") ||
                    strstr(message, "not yet confirmed") || strstr(message, "Close the running game") ||
                    strstr(message, "Not enough PS4 installation space") || strstr(message, "0x80990004") ||
                    strstr(message, "PS4 task stalled") || strstr(message, "BGFT_UNRESOLVED:") ||
                    /* Deterministic platform rejections never change on retry. The
                     * installer already parked the task and kept the package, so a
                     * staged retry would only re-register and fail again. */
                    strstr(message, "BGFT_TERMINAL:") ||
                    strstr(message, "0x80990015") || strstr(message, "0x80990086") ||
                    strstr(message, "Archive extraction was interrupted.") || strstr(message, "DLC_UNCONFIRMED:") ||
                    strstr(message, "ARCHIVE_FAILED:") || strstr(message, "THEME_UNCONFIRMED:");
                if (!strcmp(gs_state, "failed") && !addon_rejected && stage->install_retries < 3 && !stage->canceled) {
                    stage->install_retries++;
                    stage->install_retry_at = sceKernelGetProcessTime() + (uint64_t)(3 << stage->install_retries) * 1000000;
                    if (gs_archive_task < 0 || !(stage->job.expected_kind ? gs_park_task() : gs_cancel_task())) {
                        char retry[900]; snprintf(retry, sizeof(retry), "Install retry %d/3 in %d seconds. %s",
                            stage->install_retries, 3 << stage->install_retries, message);
                        gs_stage_status(stage, stage->running ? "feeding" : "waiting", retry);
                        gs_release = 1;
                    } else gs_stage_status(stage, "failed", "Installer ownership could not be released; file retained");
                } else {
                    int stopped = gs_archive_task < 0 ? 0 : !strcmp(gs_state, "canceled") ? gs_cancel_task() : gs_park_task();
                    gs_stage_status(stage, gs_state, stopped ? "Installer stop unconfirmed; task and progress retained" : message);
                    gs_release = !stopped;
                }
                gs_stage_install_after = sceKernelGetProcessTime() + 3000000;
            } else if (gs_worker_running && gs_job.native_bgft) {
                char detail[640]; gs_stage_install_detail(stage, detail, sizeof(detail));
                stage->done = gs_done; stage->wire_bytes = gs_network_bytes;
                int downloading = !strcmp(gs_state, "feeding");
                gs_stage_status(stage, gs_paused ? "paused" : downloading ? "feeding" : "installing",
                    detail[0] ? detail : downloading ? "PS4 is downloading the package directly" : "PS4 is completing installation");
            } else if (gs_worker_running && gs_stream_slot < 0) {
                const char *phase = !strcmp(gs_state, "extracting") ? "extracting" :
                    !strcmp(gs_state, "validating") ? "validating" : !strcmp(gs_state, "submitted") ? "submitted" : "installing";
                if (!strcmp(phase, "extracting")) {
                    char message[96];
                    if (gs_canceled) snprintf(message, sizeof(message), "Canceling extraction; waiting for file cleanup");
                    else if (!gs_done) snprintf(message, sizeof(message), "Opening archive and reading package headers");
                    else snprintf(message, sizeof(message), "Extracted %lld MiB", (long long)(gs_done / (1024 * 1024)));
                    gs_stage_status(stage, phase, message);
                } else {
                    char detail[640]; gs_stage_install_detail(stage, detail, sizeof(detail));
                    gs_stage_idle_status(stage, phase, detail);
                }
            }
            else if (gs_worker_running && gs_stream_slot == stage->slot) {
                gs_stage_lock(&stage->lock);
                snprintf(stage->bgft_detail, sizeof(stage->bgft_detail), "%s", gs_done > 0
                    ? "Downloading; streaming the local package to PS4" : "Downloading; PS4 is preparing the install task");
                gs_stage_unlock(&stage->lock);
            }
        }
        return;
    }
    if (gs_job.id[0] || gs_worker_running || gs_worker_created || g_client_count ||
        gs_stage_transfer_active() || sceKernelGetProcessTime() < gs_stage_install_after) return;
    GsJob existing;
    if (!gs_read_job(&existing)) return; // A durable install already owns the singleton slot.
    int chosen = -1, chosen_rank = 4;
    for (int i = 0; i < GS_STAGE_COUNT; i++) {
        GsStage *stage = &gs_stages[i];
        if (!stage->job.auto_install || stage->canceled || stage->paused || stage->pending || stage->running || stage->created ||
            (strcmp(stage->state, "staged") && strcmp(stage->state, "waiting"))) continue;
        if (sceKernelGetProcessTime() < stage->install_retry_at) continue;
        if (!gs_storage_matches(stage->job.storage_root, stage->job.storage_token)) {
            gs_stage_idle_status(stage, "waiting", "Reconnect the selected staging drive to install the retained file"); continue;
        }
        if (!gs_stage_dependency_ready(stage)) { gs_stage_idle_status(stage, "waiting", "Waiting for the required package to finish installing"); continue; }
        int rank = stage->job.borrowed_source && !stage->job.expected_kind ? 3 : gs_install_rank(stage->job.expected_kind);
        if (rank < chosen_rank) { chosen = i; chosen_rank = rank; }
    }
    if (chosen >= 0) {
        GsStage *stage = &gs_stages[chosen];
        if (gs_stage_install_record(stage)) gs_stage_status(stage, "failed", "Background installation handoff could not be saved");
        else { stage->install_owned = 1;
            if (!stage->running) gs_stage_status(stage, "waiting", "Waiting for the PS4 installer"); }
    }
}

static void gs_stage_download_poll(void)
{
    if (gs_job.id[0] || gs_worker_running || gs_worker_created || g_client_count ||
        gs_stage_transfer_active() || sceKernelGetProcessTime() < gs_stage_install_after) return;
    GsJob existing;
    if (!gs_read_job(&existing)) return;
    /* A completed package keeps the I/O lease through install retries. A
     * retained update waiting for its base must still let that base download. */
    for (int i = 0; i < GS_STAGE_COUNT; i++) {
        GsStage *stage = &gs_stages[i];
        if (stage->job.auto_install && !stage->pending && !stage->canceled && !stage->paused &&
            (!strcmp(stage->state, "staged") || !strcmp(stage->state, "waiting") || !strcmp(stage->state, "installing")) &&
            gs_stage_dependency_ready(stage)) return;
    }
    int chosen = -1, chosen_rank = 4;
    for (int i = 0; i < GS_STAGE_COUNT; i++) {
        GsStage *stage = &gs_stages[i];
        if (!stage->pending || stage->canceled || stage->paused ||
            (stage->job.auto_install && !gs_stage_dependency_ready(stage))) continue;
        uint64_t now = sceKernelGetProcessTime();
        if (!strcmp(stage->state, "storage-wait") && stage->sample_at && now - stage->sample_at < 1000000) continue;
        stage->sample_at = now;
        GsStorageError storage_failure;
        int check = gs_storage_check_error(stage->job.storage_root, stage->job.storage_token, &storage_failure);
        if (check != 1) {
            char detail[256];
            gs_storage_wait_detail(detail, sizeof(detail), check, &storage_failure);
            gs_stage_idle_status(stage, "storage-wait", detail);
            continue;
        }
        int rank = stage->job.borrowed_source && !stage->job.expected_kind ? 3 : gs_install_rank(stage->job.expected_kind);
        if (rank < chosen_rank) { chosen = i; chosen_rank = rank; }
    }
    if (chosen < 0) return;
    GsStage *stage = &gs_stages[chosen];
    stage->pending = 0; stage->running = 1;
    gs_stage_status(stage, "ready", stage->job.borrowed_source ? "Checking selected USB file for installation" : "Starting background download; installation follows verification");
    if ((!stage->job.borrowed_source && gs_network_init()) || scePthreadCreate(&stage->thread, NULL, gs_stage_worker, stage, "sspi-stage")) {
        stage->running = 0; gs_stage_status(stage, "failed", "Resident HTTPS or transfer worker initialization failed");
    } else stage->created = 1;
}

static void gs_stage_poll(void)
{
    static uint64_t intake_log_at[GS_STAGE_COUNT];
    for (int i = 0; i < GS_STAGE_COUNT; i++) {
        GsStage *stage = &gs_stages[i]; stage->slot = i;
        if (stage->job.id[0]) {
            gs_stage_control(stage);
            if (stage->pending && !stage->canceled) {
                if (stage->paused && strcmp(stage->state, "paused")) gs_stage_status(stage, "paused", NULL);
                else if (!stage->paused && !strcmp(stage->state, "paused"))
                    gs_stage_status(stage, "queued", "Queued; waiting for the current package to finish");
            }
            if (stage->install_owned && !strcmp(gs_job.id, stage->job.id)) {
                if (stage->canceled) { gs_canceled = 1; if (stage->release) gs_release = 1; }
                else if (stage->paused != gs_paused && !gs_pause_archive_task(stage->paused)) gs_paused = stage->paused;
            }
            if (!__atomic_load_n(&stage->running, __ATOMIC_ACQUIRE) && stage->created) {
                scePthreadJoin(stage->thread, NULL); stage->created = 0;
            }
            if (stage->canceled && !stage->created && strcmp(gs_job.id, stage->job.id)) {
                if (gs_archive_cancel_before_start(&stage->job) ||
                    (stage->job.expected_kind && gs_retire_parked_package(stage->job.id, stage->job.content, stage->job.expected_kind))) {
                    gs_stage_status(stage, "failed", "Stopping the retained PS4 task is unconfirmed; job and files kept");
                    continue;
                }
                gs_stage_status(stage, "canceled", NULL);
            }
            if (stage->release && !stage->created && strcmp(gs_job.id, stage->job.id)) {
                char path[1100]; gs_stage_path(i, "job", path, sizeof(path));
                if (!unlink(path) || errno == ENOENT) {
                    gs_stage_status(stage, "released", NULL); memset(stage, 0, sizeof(*stage)); stage->slot = i;
                }
            }
            /* The worker publishes live bytes and detail every 250 ms. A second
             * publisher here would clear its BGFT detail and duplicate I/O. */
            continue;
        }
        char intake_path[1100]; gs_stage_path(i, "job", intake_path, sizeof(intake_path));
        if (access(intake_path, F_OK)) continue;
        uint64_t now = sceKernelGetProcessTime();
        int log_intake = !intake_log_at[i] || now - intake_log_at[i] >= 10000000;
        if (log_intake) intake_log_at[i] = now;
        GsJob candidate; if (gs_stage_read_job(i, &candidate)) {
            if (log_intake) gs_log_write("resident", "intake slot=%d state=rejected reason=invalid-record path=%s", i, intake_path);
            continue;
        }
        gs_log_write("resident", "intake slot=%d state=accepted job=%s generation=%s lanes=%d destination=%s", i,
            candidate.id, candidate.generation, candidate.ranges, candidate.destination);
        stage->job = candidate; stage->pending = 1; stage->transfer_handle = -1;
        if (stage->job.native_bgft && gs_native_fallback_saved(&stage->job)) stage->job.native_bgft = 0;
        GsJob active_native;
        if (candidate.native_bgft && !gs_read_job(&active_native) && active_native.native_bgft &&
            !strcmp(candidate.id, active_native.id) && !strcmp(candidate.generation, active_native.generation)) {
            stage->pending = 0; snprintf(stage->state, sizeof(stage->state), "waiting");
            gs_stage_control(stage); continue;
        }
        snprintf(stage->part, sizeof(stage->part), "%s.parallel.part", stage->job.destination);
        snprintf(stage->journal, sizeof(stage->journal), "%s.ranges", stage->part);
        char status_path[1100], saved[300], saved_id[192]; gs_stage_path(i, "status", status_path, sizeof(status_path));
        FILE *status_file = fopen(status_path, "rb");
        if (status_file) {
            if (!gs_line(status_file, saved, sizeof(saved)) && !strcmp(saved, "1") &&
                !gs_encoded_line(status_file, saved_id, sizeof(saved_id)) && !strcmp(saved_id, stage->job.id) &&
                !gs_line(status_file, saved, sizeof(saved))) {
                char previous[24]; snprintf(previous, sizeof(previous), "%s", saved);
                int matches_generation = !stage->job.generation[0];
                if (stage->job.generation[0]) {
                    int field = 3; while (field < 10 && !gs_line(status_file, saved, sizeof(saved))) field++;
                    matches_generation = field == 10 && !gs_line(status_file, saved, sizeof(saved)) && !strcmp(saved, stage->job.generation);
                }
                snprintf(saved, sizeof(saved), "%s", previous);
                if (!matches_generation) { fclose(status_file); status_file = NULL; goto fresh_stage; }
                // Extraction already has a durable local install record. Resume
                // that owner after restart instead of starting another transfer.
                if (!strcmp(saved, "extracting")) snprintf(saved, sizeof(saved), "waiting");
                stage->paused = !strcmp(saved, "paused");
                if (!strcmp(saved, "staged") || !strcmp(saved, "failed") || !strcmp(saved, "canceled") ||
                    !strcmp(saved, "installed") || (stage->job.auto_install && (!strcmp(saved, "waiting") || !strcmp(saved, "installing")))) {
                    snprintf(stage->state, sizeof(stage->state), "%s", saved); stage->pending = 0;
                    if (!strcmp(saved, "installed") || (!stage->job.native_bgft && (!strcmp(saved, "staged") || !strcmp(saved, "waiting") || !strcmp(saved, "installing")))) stage->done = stage->job.total;
                    fclose(status_file); gs_stage_control(stage); continue;
                }
            }
            fclose(status_file);
        }
fresh_stage:
        gs_stage_control(stage);
        gs_stage_status(stage, stage->paused ? "paused" : "queued", stage->paused ? NULL : "Queued; waiting for the current package to finish");
    }
    gs_stage_install_poll();
    gs_stage_download_poll();
}

static void gs_stage_shutdown(void)
{
    for (int i = 0; i < GS_STAGE_COUNT; i++) if (gs_stages[i].created) {
        scePthreadJoin(gs_stages[i].thread, NULL); gs_stages[i].created = 0;
    }
}
