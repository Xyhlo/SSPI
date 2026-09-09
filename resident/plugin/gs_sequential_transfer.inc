/* A single persistent HTTP response feeds four bounded buffers. The caller is
 * the only disk writer; progress and checkpoints describe committed bytes. */
#define GS_STREAM_BLOCK (512 * 1024)
typedef struct {
    unsigned char *data;
    int size[4], ready[4];
    int stop, finished, result, request;
    int64_t remaining;
    uint64_t read_us, read_calls;
    OrbisPthread thread;
} GsStream;

static uint64_t gs_stream_clock(void)
{
    struct timespec ts;
    if (clock_gettime(CLOCK_MONOTONIC, &ts)) return 0;
    return (uint64_t)ts.tv_sec * 1000000 + (uint64_t)ts.tv_nsec / 1000;
}

static int64_t gs_stream_limit(void)
{
    FILE *settings = fopen("/data/GameSearch/settings.ini", "rb");
    char line[12288]; int mb = 0, count = 0;
    if (!settings) return 0;
    while (count++ < 512 && fgets(line, sizeof(line), settings))
        if (!strncmp(line, "download_limit_mb_s=", 20)) mb = atoi(line + 20);
    fclose(settings);
    return mb > 0 ? (int64_t)(mb > 1000 ? 1000 : mb) * 1000000 : 0;
}

static void *gs_stream_reader(void *opaque)
{
    GsStream *stream = opaque; int slot = 0;
    stream->result = -1;
    while (stream->remaining > 0) {
        if (__atomic_load_n(&stream->stop, __ATOMIC_ACQUIRE) || g_stop || gs_canceled || gs_paused) goto done;
        if (__atomic_load_n(&stream->ready[slot], __ATOMIC_ACQUIRE)) { sceKernelUsleep(500); continue; }
        int wanted = stream->remaining < GS_STREAM_BLOCK ? (int)stream->remaining : GS_STREAM_BLOCK;
        int filled = 0;
        // TLS commonly returns one record (16 KiB). Coalesce those records before
        // handing off to the unbuffered disk writer instead of making tiny writes.
        while (filled < wanted) {
            if (__atomic_load_n(&stream->stop, __ATOMIC_ACQUIRE) || g_stop || gs_canceled || gs_paused) goto done;
            uint64_t read_at = gs_stream_clock();
            int n = gs_read(stream->request, stream->data + slot * GS_STREAM_BLOCK + filled,
                (uint32_t)(wanted - filled));
            __atomic_fetch_add(&stream->read_us, gs_stream_clock() - read_at, __ATOMIC_RELAXED);
            __atomic_fetch_add(&stream->read_calls, 1, __ATOMIC_RELAXED);
            if (n <= 0 || n > wanted - filled) goto done;
            filled += n;
        }
        stream->size[slot] = filled; stream->remaining -= filled;
        __atomic_store_n(&stream->ready[slot], 1, __ATOMIC_RELEASE);
        slot = (slot + 1) % 4;
    }
    { unsigned char extra; if (gs_read(stream->request, &extra, 1) != 0) goto done; }
    stream->result = 0;
done:
    __atomic_store_n(&stream->finished, 1, __ATOMIC_RELEASE);
    return NULL;
}

static int gs_stream_response(FILE *file, int64_t *committed, GsTransferHash *hash,
    const char *marker, const char *identity, int64_t *durable)
{
    int template_id = -1, connection = -1, request = -1, status, result = -1, started = 0, slot = 0;
    char range[96], *headers, *copy = NULL; size_t header_size;
    long long first = -1, last = -1, total = -1, length = -1;
    GsStream stream; memset(&stream, 0, sizeof(stream));
    template_id = gs_template(gs_http, "SSPI/" GS_APP_VERSION, 2, 0);
    if (template_id < 0) goto done;
    gs_connect_timeout(template_id, 30000000); gs_recv_timeout(template_id, 30000000);
    gs_send_timeout(template_id, 30000000); gs_auto_redirect(template_id, 1);
    connection = gs_connection(template_id, gs_job.url, 1); if (connection < 0) goto done;
    request = gs_request(connection, 0, gs_job.url, 0); if (request < 0) goto done;
    if (*committed > 0) {
        snprintf(range, sizeof(range), "bytes=%lld-", (long long)*committed);
        if (gs_header(request, "Range", range, 0)) goto done;
    }
    if (gs_header(request, "Accept-Encoding", "identity", 0) || gs_send(request, NULL, 0) ||
        gs_status(request, &status) || gs_headers(request, &headers, &header_size) || header_size > 65536) goto done;
    copy = calloc(1, header_size + 1); if (!copy) goto done; memcpy(copy, headers, header_size);
    for (const char *at = copy; at && *at; ) {
        if (!strncasecmp(at, "Content-Length:", 15) && sscanf(at + 15, " %lld", &length) != 1) goto done;
        if (!strncasecmp(at, "Content-Range:", 14) && sscanf(at + 14, " bytes %lld-%lld/%lld", &first, &last, &total) != 3) goto done;
        at = strstr(at, "\r\n"); if (at) at += 2;
    }
    if (*committed > 0 && status == 200) { result = -3; goto done; }
    if (*committed > 0) {
        if (status != 206 || first != *committed || last != gs_job.total - 1 || total != gs_job.total ||
            (length >= 0 && length != gs_job.total - *committed)) goto done;
    } else if (status != 200 || length != gs_job.total) goto done;
    stream.data = malloc(4 * GS_STREAM_BLOCK); if (!stream.data) goto done;
    stream.request = request; stream.remaining = gs_job.total - *committed;
    if (scePthreadCreate(&stream.thread, NULL, gs_stream_reader, &stream, "sspi-stream")) goto done;
    started = 1;
    uint64_t limit_at = gs_stream_clock(), now = limit_at, config_at = now, started_at = now, metrics_at = now;
    uint64_t write_us = 0, hash_us = 0, checkpoint_us = 0, stage_at;
    int64_t started_bytes = *committed;
    int64_t limit = gs_stream_limit(); double debt = 0; time_t checkpoint_at = time(NULL), status_at = 0;
    while (*committed < gs_job.total) {
        if (g_stop || gs_canceled || gs_paused) goto done;
        if (!__atomic_load_n(&stream.ready[slot], __ATOMIC_ACQUIRE)) {
            if (__atomic_load_n(&stream.finished, __ATOMIC_ACQUIRE)) goto done;
            sceKernelUsleep(500); continue;
        }
        unsigned char *data = stream.data + slot * GS_STREAM_BLOCK; size_t n = (size_t)stream.size[slot];
        stage_at = gs_stream_clock();
        if (fwrite(data, 1, n, file) != n) goto done;
        write_us += gs_stream_clock() - stage_at; stage_at = gs_stream_clock();
        if (*committed == 0) gs_transfer_hash_begin(hash, data, n);
        gs_transfer_hash_feed(hash, *committed, data, n); *committed += (int64_t)n; gs_done = *committed;
        hash_us += gs_stream_clock() - stage_at;
        __atomic_store_n(&stream.ready[slot], 0, __ATOMIC_RELEASE); slot = (slot + 1) % 4;
        if (*committed - *durable >= 64LL * 1024 * 1024 || time(NULL) - checkpoint_at >= 5) {
            stage_at = gs_stream_clock();
            if (gs_checkpoint(file, marker, identity, *committed)) goto done;
            checkpoint_us += gs_stream_clock() - stage_at;
            *durable = *committed; checkpoint_at = time(NULL);
        }
        if (time(NULL) != status_at) { status_at = time(NULL); gs_write_status("feeding", NULL); }
        now = gs_stream_clock();
        if (now - metrics_at >= 5000000) {
            char metrics[768];
            snprintf(metrics, sizeof(metrics), "transport=sceHttp-resident\nworker=" GS_WORKER_VERSION
                "\nelapsed_ms=%llu\nbytes_written=%lld\nread_calls=%llu\nread_ms=%llu\nwrite_ms=%llu\nhash_ms=%llu\ncheckpoint_ms=%llu\nlimit_bytes_s=%lld\nblock_bytes=%d\nbuffer_count=4\n",
                (unsigned long long)((now - started_at) / 1000), (long long)(*committed - started_bytes),
                (unsigned long long)__atomic_load_n(&stream.read_calls, __ATOMIC_RELAXED),
                (unsigned long long)(__atomic_load_n(&stream.read_us, __ATOMIC_RELAXED) / 1000),
                (unsigned long long)(write_us / 1000), (unsigned long long)(hash_us / 1000),
                (unsigned long long)(checkpoint_us / 1000), (long long)limit, GS_STREAM_BLOCK);
            write_telemetry(GS_IPC_ROOT "/transfer-metrics.txt", metrics); metrics_at = now;
        }
        if (now - config_at >= 1000000) { limit = gs_stream_limit(); config_at = now; debt = 0; }
        if (limit > 0) {
            debt -= (now - limit_at) * (double)limit / 1000000.0;
            if (debt < 0) debt = 0; debt += n;
            while (debt > limit * .1) {
                if (g_stop || gs_canceled || gs_paused) goto done;
                limit_at = now; sceKernelUsleep(10000); now = gs_stream_clock();
                debt -= (now - limit_at) * (double)limit / 1000000.0;
            }
        } else debt = 0;
        limit_at = now;
    }
    scePthreadJoin(stream.thread, NULL); started = 0;
    if (stream.result == 0) result = 0;
done:
    if (started) {
        __atomic_store_n(&stream.stop, 1, __ATOMIC_RELEASE);
        if (gs_abort_request && !__atomic_load_n(&stream.finished, __ATOMIC_ACQUIRE)) gs_abort_request(request);
        scePthreadJoin(stream.thread, NULL);
    }
    free(stream.data); free(copy);
    if (request >= 0) gs_delete_request(request);
    if (connection >= 0) gs_delete_connection(connection);
    if (template_id >= 0) gs_delete_template(template_id);
    return result;
}

static int gs_download_stream(const char *partial)
{
    _Static_assert(sizeof(long) == 8, "64-bit file offsets required");
    FILE *file = NULL, *checkpoint; int result = -1, retries = 0;
    GsTransferHash transfer_hash; memset(&transfer_hash, 0, sizeof(transfer_hash)); gs_transfer_verified = 0;
    char marker[1150], saved_identity[65], identity[65];
    unsigned char digest[32]; SHA256_CTX sha; struct stat st;
    int64_t committed = 0, durable = 0; long long saved_total, saved_done;
    sha256_init(&sha); const char *source = gs_job.sha[0] ? gs_job.sha : gs_job.url;
    sha256_update(&sha, (const unsigned char *)source, strlen(source));
    sha256_update(&sha, (const unsigned char *)gs_job.content, strlen(gs_job.content)); sha256_final(&sha, digest);
    for (int i = 0; i < 32; i++) snprintf(identity + i * 2, 3, "%02x", digest[i]);
    // Retain the existing checkpoint format so installed betas can resume safely.
    snprintf(marker, sizeof(marker), "%s.ranges", partial); checkpoint = fopen(marker, "rb");
    if (checkpoint) {
        if (fscanf(checkpoint, "%64s %lld %lld", saved_identity, &saved_total, &saved_done) == 3 &&
            !strcmp(identity, saved_identity) && saved_total == gs_job.total && saved_done >= 0 &&
            saved_done <= gs_job.total && !stat(partial, &st) && S_ISREG(st.st_mode) && st.st_size >= saved_done) committed = saved_done;
        fclose(checkpoint);
    }
    durable = committed;
    if (!lstat(partial, &st) && !S_ISREG(st.st_mode)) goto done;
    file = fopen(partial, access(partial, F_OK) == 0 ? "r+b" : "w+b");
    if (!file || ftruncate(fileno(file), committed)) goto done;
    setvbuf(file, NULL, _IONBF, 0); if (fseek(file, (long)committed, SEEK_SET)) goto done;
    gs_done = committed;
    while (committed < gs_job.total && !g_stop && !gs_canceled) {
        if (gs_paused) { gs_write_status("paused", NULL); sceKernelUsleep(100000); continue; }
        gs_write_status("feeding", NULL); int64_t before = committed;
        int rc = gs_stream_response(file, &committed, &transfer_hash, marker, identity, &durable);
        if (rc && committed == gs_job.total) {
            committed = before; transfer_hash.enabled = 0;
            if (ftruncate(fileno(file), committed) || fseek(file, (long)committed, SEEK_SET)) goto done;
        }
        if (gs_checkpoint(file, marker, identity, committed)) goto done;
        durable = committed;
        if (!rc) break;
        if (g_stop || gs_canceled) goto done;
        if (gs_paused) continue;
        if (rc == -3) {
            // An origin that ignores resume must start a new body, never append it.
            if (ftruncate(fileno(file), 0) || fseek(file, 0, SEEK_SET)) goto done;
            committed = durable = gs_done = 0; memset(&transfer_hash, 0, sizeof(transfer_hash));
            if (gs_checkpoint(file, marker, identity, 0)) goto done;
        }
        if (committed > before) retries = 0;
        if (++retries > 3) goto done;
        sceKernelUsleep(500000);
    }
    result = committed == gs_job.total && !g_stop && !gs_canceled ? 0 : -1;
    if (!result) gs_transfer_hash_finish(&transfer_hash);
done:
    if (file) {
        if (ftruncate(fileno(file), committed) || gs_checkpoint(file, marker, identity, committed)) {
            ftruncate(fileno(file), durable); committed = durable; result = -1;
        }
        if (fclose(file)) result = -1;
    }
    gs_done = committed; return result;
}
