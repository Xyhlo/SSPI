/* Included after the HTTP framing helpers. V2 IPC matches ResidentDownloadService.
 * Only the process holding the loopback listener may consume jobs or heartbeat.
 */
#include "crypto/sha256.h"

typedef struct {
    char id[192], url[8192], destination[1024], title[16], sha[65], content[49];
    int64_t total;
    int ranges;
    int expected_kind;
    int archive; // 0: legacy V2, 1: RAR V3, 2: self-contained PKG V4
} GsJob;
static GsJob gs_job;
static OrbisPthread gs_worker;
static int gs_worker_created;
static volatile int gs_worker_running, gs_validated, gs_served;
static volatile int gs_paused, gs_canceled, gs_release;
static volatile int64_t gs_done;
static int gs_http = -1;
static int gs_network_ready;
static volatile int gs_archive_task = -1;
static int gs_cancel_task(void);
static int gs_pause_archive_task(int paused);
static char gs_archive_journal[1150];
static int gs_package_ordinal;
static int gs_package_legacy_route;
static int gs_transfer_verified;
static unsigned char gs_verified_header[32];
static int64_t gs_verified_size;
static uint64_t gs_be64(const unsigned char *p);

static void gs_package_url(char *route, size_t size, const char *prefix)
{
    if (gs_job.archive == 1 && !gs_package_legacy_route) snprintf(route, size, "%s/pkg/%s-%03d.pkg", prefix, gs_job.id, gs_package_ordinal);
    else snprintf(route, size, "%s/pkg/%s.pkg", prefix, gs_job.id);
}
typedef struct { int64_t start, end; } GsInterval;
static GsInterval gs_intervals[1024];
static int gs_interval_count;
static volatile int gs_interval_lock;

static void gs_record_served(int64_t start, int64_t end)
{
    if (end <= start) return;
    while (__sync_lock_test_and_set(&gs_interval_lock, 1)) sceKernelUsleep(100);
    int at = 0, tail;
    while (at < gs_interval_count && gs_intervals[at].end < start) at++;
    tail = at;
    while (tail < gs_interval_count && gs_intervals[tail].start <= end) {
        if (gs_intervals[tail].start < start) start = gs_intervals[tail].start;
        if (gs_intervals[tail].end > end) end = gs_intervals[tail].end;
        tail++;
    }
    if (tail > at) {
        memmove(&gs_intervals[at], &gs_intervals[tail], (size_t)(gs_interval_count - tail) * sizeof(GsInterval));
        gs_interval_count -= tail - at;
    }
    if (gs_interval_count < 1024) {
        memmove(&gs_intervals[at + 1], &gs_intervals[at], (size_t)(gs_interval_count - at) * sizeof(GsInterval));
        gs_intervals[at].start = start; gs_intervals[at].end = end; gs_interval_count++;
        if (gs_interval_count == 1 && start == 0 && end == gs_job.total) gs_served = 1;
    }
    __sync_lock_release(&gs_interval_lock);
}
static int gs_bgft_module = -1;
static void *gs_bgft_heap;
static int gs_current_task = -1;
static char gs_state[24] = "idle";
static int gs_run_archive(void);
static int gs_run_package(void);

static int (*gs_http_init)(int,int,size_t);
static int (*gs_ssl_init)(size_t);
static int (*gs_template)(int,const char *,int,int);
static int (*gs_connection)(int,const char *,int);
static int (*gs_request)(int,int,const char *,uint64_t);
static int (*gs_header)(int,const char *,const char *,int);
static int (*gs_send)(int,const void *,size_t);
static int (*gs_status)(int,int *);
static int (*gs_headers)(int,char **,size_t *);
static int (*gs_read)(int,void *,uint32_t);
static int (*gs_abort_request)(int);
static int (*gs_delete_request)(int), (*gs_delete_connection)(int), (*gs_delete_template)(int);
static int (*gs_recv_timeout)(int,uint32_t), (*gs_send_timeout)(int,uint32_t), (*gs_connect_timeout)(int,uint32_t);
static int (*gs_auto_redirect)(int,int);
static int (*gs_load_cert)(int,int,void *,void *,void *);

static int gs_b64decode(const char *input, unsigned char *output, size_t cap)
{
    const char *alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    unsigned value = 0; int bits = 0; size_t used = 0;
    for (; *input && *input != '='; input++) {
        const char *at;
        if (*input == '\r' || *input == '\n' || *input == ' ' || *input == '\t') continue;
        at = strchr(alphabet, *input);
        if (!at) return -1;
        value = (value << 6) | (unsigned)(at - alphabet); bits += 6;
        if (bits >= 8) {
            bits -= 8;
            if (used + 1 >= cap) return -1;
            output[used++] = (unsigned char)(value >> bits);
        }
    }
    output[used] = 0;
    return (int)used;
}

static int gs_b64encode(const unsigned char *input, size_t length, char *output, size_t cap)
{
    const char *alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    size_t i, used = 0;
    if ((length + 2) / 3 * 4 + 1 > cap) return -1;
    for (i = 0; i < length; i += 3) {
        unsigned n = (unsigned)input[i] << 16;
        if (i + 1 < length) n |= (unsigned)input[i + 1] << 8;
        if (i + 2 < length) n |= input[i + 2];
        output[used++] = alphabet[(n >> 18) & 63]; output[used++] = alphabet[(n >> 12) & 63];
        output[used++] = i + 1 < length ? alphabet[(n >> 6) & 63] : '=';
        output[used++] = i + 2 < length ? alphabet[n & 63] : '=';
    }
    output[used] = 0; return (int)used;
}

static int gs_line(FILE *file, char *line, size_t size)
{
    size_t length;
    if (!fgets(line, (int)size, file)) return -1;
    length = strlen(line);
    if (length == size - 1 && line[length - 1] != '\n') return -1;
    while (length && (line[length - 1] == '\n' || line[length - 1] == '\r')) line[--length] = 0;
    return 0;
}

static int gs_encoded_line(FILE *file, char *output, size_t size)
{
    char line[12000]; int length;
    if (gs_line(file, line, sizeof(line)) != 0) return -1;
    length = gs_b64decode(line, (unsigned char *)output, size);
    return length >= 0 && (size_t)length == strlen(output) ? 0 : -1;
}

static void gs_write_status(const char *state, const char *error)
{
    static time_t last;
    static char previous_state[64], previous_error[640];
    char body[1400], id[260], encoded_error[900];
    if (!error) error = "";
    time_t now = time(NULL);
    if (now == last && !strcmp(state, previous_state) && !strcmp(error, previous_error)) return;
    last = now;
    snprintf(previous_state, sizeof(previous_state), "%s", state);
    snprintf(previous_error, sizeof(previous_error), "%s", error);
    gs_b64encode((const unsigned char *)gs_job.id, strlen(gs_job.id), id, sizeof(id));
    if (gs_b64encode((const unsigned char *)error, strlen(error), encoded_error, sizeof(encoded_error)) < 0)
        strcpy(encoded_error, "UmVzaWRlbnQgZXJyb3I=");
    snprintf(gs_state, sizeof(gs_state), "%s", state);
    snprintf(body, sizeof(body), "1\n%s\n%s\n%lld\n%lld\n%d\n%s",
        id, state, (long long)gs_done, (long long)gs_job.total, gs_served, encoded_error);
    write_telemetry(GS_IPC_ROOT "/status.txt", body);
}

static int gs_read_job(GsJob *job)
{
    char line[64]; const char *end; size_t i; int ok = 0;
    FILE *file = fopen(GS_IPC_ROOT "/job.txt", "rb");
    if (!file) return -1;
    memset(job, 0, sizeof(*job));
    if (gs_line(file, line, sizeof(line)) != 0 || (strcmp(line, "2") && strcmp(line, "3") && strcmp(line, "4") && strcmp(line, "5"))) goto done;
    int identity_version = !strcmp(line, "5");
    job->archive = !strcmp(line, "3") ? 1 : (identity_version || !strcmp(line, "4")) ? 2 : 0;
    if (gs_encoded_line(file, job->id, sizeof(job->id)) || gs_encoded_line(file, job->url, sizeof(job->url)) ||
        gs_encoded_line(file, job->destination, sizeof(job->destination)) || gs_encoded_line(file, job->title, sizeof(job->title)) ||
        gs_encoded_line(file, job->sha, sizeof(job->sha)) || gs_encoded_line(file, job->content, sizeof(job->content)) ||
        gs_line(file, line, sizeof(line)) || parse_number(line, &job->total, &end) || *end ||
        job->total < (job->archive == 1 ? 8 : 0x1000) || job->total > (int64_t)8 * 1024 * 1024 * 1024 * 1024) goto done;
    int64_t ranges = 1;
    if (gs_line(file, line, sizeof(line)) || parse_number(line, &ranges, &end) || *end || ranges < 1 || ranges > 7) goto done;
    job->ranges = (int)ranges;
    if (identity_version) {
        int64_t kind;
        if (gs_line(file, line, sizeof(line)) || parse_number(line, &kind, &end) || *end || kind < 6 || kind > 8) goto done;
        job->expected_kind = (int)kind;
    }
    if (!job->id[0] || !job->url[0] || (job->archive != 1 && strlen(job->content) != 36)) goto done;
    for (i = 0; job->id[i]; i++)
        if (!((job->id[i] >= 'a' && job->id[i] <= 'z') || (job->id[i] >= 'A' && job->id[i] <= 'Z') ||
            (job->id[i] >= '0' && job->id[i] <= '9') || job->id[i] == '_' || job->id[i] == '-')) goto done;
    if (strncmp(job->destination, "/data/GameSearch/downloads/", 26) != 0 ||
        strchr(job->destination + 26, '/') || strchr(job->destination + 26, '\\') ||
        strstr(job->destination, "..")) goto done;
    if (strncmp(job->url, "https://", 8) != 0 && strncmp(job->url, "http://", 7) != 0) goto done;
    if (job->sha[0]) {
        if (strlen(job->sha) != 64) goto done;
        for (i = 0; i < 64; i++) if (!strchr("0123456789abcdefABCDEF", job->sha[i])) goto done;
    }
    ok = 1;
done:
    fclose(file); return ok ? 0 : -1;
}

static void gs_control(void)
{
    char line[300], id[192];
    FILE *file = fopen(GS_IPC_ROOT "/control.txt", "rb");
    if (!file) return;
    if (gs_line(file, line, sizeof(line)) == 0 && strcmp(line, "1") == 0 &&
        gs_encoded_line(file, id, sizeof(id)) == 0 && strcmp(id, gs_job.id) == 0 &&
        gs_line(file, line, sizeof(line)) == 0) {
        if (strcmp(line, "pause") == 0 && !gs_pause_archive_task(1)) gs_paused = 1;
        if (strcmp(line, "resume") == 0 && !gs_pause_archive_task(0)) gs_paused = 0;
        if (strcmp(line, "cancel") == 0) gs_canceled = 1;
        if (strcmp(line, "release") == 0) { gs_canceled = 1; gs_release = 1; }
        unlink(GS_IPC_ROOT "/control.txt");
    }
    fclose(file);
}

static int gs_module(const char *path)
{
    int loaded = sceKernelLoadStartModule(path, 0, NULL, 0, NULL, NULL);
    OrbisKernelModule modules[256] = {0}; size_t count = 0, i;
    const char *base = strrchr(path, '/'); char name[256];
    if (loaded >= 0) return loaded;
    snprintf(name, sizeof(name), "%s", base ? base + 1 : path);
    char *dot = strchr(name, '.'); if (dot) *dot = 0;
    if (sceKernelGetModuleList(modules, sizeof(modules) / sizeof(modules[0]), &count) != 0) return loaded;
    if (count > 256) count = 256;
    for (i = 0; i < count; i++) {
        OrbisKernelModuleInfo info; memset(&info, 0, sizeof(info)); info.size = sizeof(info);
        if (!sceKernelGetModuleInfo(modules[i], &info) && !strncmp(info.name, name, strlen(name)) &&
            (info.name[strlen(name)] == 0 || info.name[strlen(name)] == '.')) return (int)modules[i];
    }
    return loaded;
}

static int gs_bind(int module, const char *name, void *slot)
{
    void *symbol = NULL;
    if (module < 0 || sceKernelDlsym(module, name, &symbol) != 0 || !symbol) return -1;
    memcpy(slot, &symbol, sizeof(symbol)); return 0;
}

static int gs_load_ca(const char *path)
{
    /* Retained for the HTTP-context lifetime, as required by sceHttpsLoadCert. */
    typedef struct { void *data; size_t size; } Cert;
    FILE *file = fopen(path, "rb");
    char *pem, *at; long length;
    if (!file) return -1;
    if (fseek(file, 0, SEEK_END) || (length = ftell(file)) <= 0 || length > 1024 * 1024) { fclose(file); return -1; }
    rewind(file); pem = (char *)calloc(1, (size_t)length + 1);
    if (!pem) { fclose(file); return -1; }
    if (fread(pem, 1, (size_t)length, file) != (size_t)length) { free(pem); fclose(file); return -1; }
    fclose(file); at = pem;
    while ((at = strstr(at, "-----BEGIN CERTIFICATE-----")) != NULL) {
        char *end; Cert *cert; Cert **list; int decoded;
        at += 27; end = strstr(at, "-----END CERTIFICATE-----");
        if (!end) break;
        *end = 0; cert = (Cert *)calloc(1, sizeof(*cert)); list = (Cert **)calloc(1, sizeof(*list));
        if (!cert || !list) { free(cert); free(list); free(pem); return -1; }
        cert->data = malloc((size_t)(end - at) + 1);
        if (!cert->data) { free(cert); free(list); free(pem); return -1; }
        decoded = gs_b64decode(at, cert->data, (size_t)(end - at) + 1);
        cert->size = decoded > 0 ? (size_t)decoded : 0; *list = cert;
        if (decoded <= 0 || gs_load_cert(gs_http, 1, list, NULL, NULL) != 0) {
            free(cert->data); free(cert); free(list); free(pem); return -1;
        }
        at = end + 25;
    }
    free(pem); return 0;
}

static int gs_network_init(void)
{
    int http_module, ssl_module, pool, ssl;
    if (gs_http >= 0) return gs_network_ready ? 0 : -1;
    ssl_module = gs_module("/system/common/lib/libSceSsl.sprx");
    http_module = gs_module("/system/common/lib/libSceHttp.sprx");
#define GS_BIND(name, target) if (gs_bind(http_module, name, &(target))) return -1
    GS_BIND("sceHttpInit", gs_http_init); GS_BIND("sceHttpCreateTemplate", gs_template);
    GS_BIND("sceHttpCreateConnectionWithURL", gs_connection); GS_BIND("sceHttpCreateRequestWithURL", gs_request);
    GS_BIND("sceHttpAddRequestHeader", gs_header); GS_BIND("sceHttpSendRequest", gs_send);
    GS_BIND("sceHttpGetStatusCode", gs_status); GS_BIND("sceHttpGetAllResponseHeaders", gs_headers);
    GS_BIND("sceHttpAbortRequest", gs_abort_request);
    GS_BIND("sceHttpReadData", gs_read); GS_BIND("sceHttpDeleteRequest", gs_delete_request);
    GS_BIND("sceHttpDeleteConnection", gs_delete_connection); GS_BIND("sceHttpDeleteTemplate", gs_delete_template);
    GS_BIND("sceHttpSetRecvTimeOut", gs_recv_timeout); GS_BIND("sceHttpSetSendTimeOut", gs_send_timeout);
    GS_BIND("sceHttpSetConnectTimeOut", gs_connect_timeout); GS_BIND("sceHttpSetAutoRedirect", gs_auto_redirect);
    GS_BIND("sceHttpsLoadCert", gs_load_cert);
#undef GS_BIND
    if (gs_bind(ssl_module, "sceSslInit", &gs_ssl_init)) return -1;
    pool = sceNetPoolCreate("GameSearchResident", 8 * 1024 * 1024, 0);
    if (pool < 0) return -1;
    ssl = gs_ssl_init(8 * 1024 * 1024);
    if (ssl < 0) return -1;
    gs_http = gs_http_init(pool, ssl, 8 * 1024 * 1024);
    if (gs_http < 0) return -1;
    // Certificate verification stays enabled. Add the same shipped roots as the app.
    if (gs_load_ca(GS_IPC_ROOT "/certs/real-debrid-geotrust-tls-rsa-ca-g1.pem") != 0 ||
        gs_load_ca(GS_IPC_ROOT "/certs/ca-certificates.crt") != 0) return -1;
    gs_network_ready = 1;
    return 0;
}

typedef struct {
    int enabled, has_full_hash;
    unsigned char header[0x1000];
    uint64_t offset[2], size[2], fed[2];
    SHA256_CTX region[2], full;
} GsTransferHash;

static void gs_transfer_hash_begin(GsTransferHash *hash, const unsigned char *data, size_t length)
{
    memset(hash, 0, sizeof(*hash));
    if (length < sizeof(hash->header) || memcmp(data, "\x7f\x43\x4e\x54", 4)) return;
    memcpy(hash->header, data, sizeof(hash->header));
    hash->offset[0] = gs_be64(data + 0x20); hash->size[0] = gs_be64(data + 0x28);
    hash->offset[1] = gs_be64(data + 0x410); hash->size[1] = gs_be64(data + 0x418);
    for (int i = 0; i < 2; i++) {
        if (hash->offset[i] > (uint64_t)gs_job.total || hash->size[i] > (uint64_t)gs_job.total - hash->offset[i]) return;
        sha256_init(&hash->region[i]);
    }
    hash->has_full_hash = gs_job.sha[0] != 0; sha256_init(&hash->full); hash->enabled = 1;
}

static void gs_transfer_hash_feed(GsTransferHash *hash, int64_t start, const unsigned char *data, size_t length)
{
    if (!hash->enabled) return;
    if (hash->has_full_hash) sha256_update(&hash->full, data, length);
    for (int i = 0; i < 2; i++) {
        uint64_t a = (uint64_t)start > hash->offset[i] ? (uint64_t)start : hash->offset[i];
        uint64_t end = hash->offset[i] + hash->size[i];
        uint64_t b = (uint64_t)start + length < end ? (uint64_t)start + length : end;
        if (b > a) { sha256_update(&hash->region[i], data + (size_t)(a - start), (size_t)(b - a)); hash->fed[i] += b - a; }
    }
}

static void gs_transfer_hash_finish(GsTransferHash *hash)
{
    if (!hash->enabled) return;
    unsigned char digest[32]; SHA256_CTX head;
    sha256_init(&head); sha256_update(&head, hash->header, 0xfe0); sha256_final(&head, digest);
    if (memcmp(digest, hash->header + 0xfe0, 32)) return;
    for (int i = 0; i < 2; i++) {
        if (hash->fed[i] != hash->size[i]) return;
        if (!hash->size[i]) continue;
        sha256_final(&hash->region[i], digest);
        if (memcmp(digest, hash->header + (i ? 0x440 : 0x160), 32)) return;
    }
    if (hash->has_full_hash) {
        char hex[65]; sha256_final(&hash->full, digest);
        for (int i = 0; i < 32; i++) snprintf(hex + i * 2, 3, "%02x", digest[i]);
        if (strcasecmp(hex, gs_job.sha)) return;
    }
    memcpy(gs_verified_header, hash->header + 0xfe0, 32); gs_verified_size = gs_job.total; gs_transfer_verified = 1;
}

static int gs_checkpoint(FILE *file, const char *marker, const char *identity, int64_t prefix)
{
    char record[180];
    if (fflush(file) || fsync(fileno(file))) return -1;
    snprintf(record, sizeof(record), "%s %lld %lld", identity, (long long)gs_job.total, (long long)prefix);
    return write_atomic(marker, record);
}

#include "gs_sequential_transfer.inc"

static uint64_t gs_be64(const unsigned char *p)
{
    uint64_t value = 0; int i;
    for (i = 0; i < 8; i++) value = (value << 8) | p[i];
    return value;
}

static int gs_validate(const char *path)
{
    unsigned char header[0x438], hash[32], buffer[128 * 1024];
    char hex[65]; struct stat st; FILE *file; SHA256_CTX context;
    uint64_t body, body_size, pfs, pfs_size; size_t count, i;
    if (stat(path, &st) || st.st_size != gs_job.total) return -1;
    file = fopen(path, "rb"); if (!file) return -1;
    if (fread(header, 1, sizeof(header), file) != sizeof(header) ||
        memcmp(header, "\x7f\x43\x4e\x54", 4) || memcmp(header + 0x40, gs_job.content, 36) ||
        gs_be64(header + 0x430) != (uint64_t)st.st_size) { fclose(file); return -1; }
    body = gs_be64(header + 0x20); body_size = gs_be64(header + 0x28);
    pfs = gs_be64(header + 0x410); pfs_size = gs_be64(header + 0x418);
    if (body < sizeof(header) || body > (uint64_t)st.st_size || !body_size || body_size > (uint64_t)st.st_size - body ||
        (pfs_size && (pfs < body + body_size || pfs > (uint64_t)st.st_size || pfs_size != (uint64_t)st.st_size - pfs)) ||
        (!pfs_size && body + body_size != (uint64_t)st.st_size)) { fclose(file); return -1; }
    if (gs_job.title[0] && (strlen(gs_job.title) != 9 || memcmp(header + 0x47, gs_job.title, 9))) { fclose(file); return -1; }
    uint64_t payload = pfs_size ? pfs : body;
    uint64_t payload_size = pfs_size ? pfs_size : body_size;
    size_t sample = payload_size < sizeof(buffer) ? (size_t)payload_size : sizeof(buffer);
    for (int edge = 0; edge < 2; edge++) {
        uint64_t offset = edge ? (uint64_t)st.st_size - sample : payload;
        int nonzero = 0;
        if (fseek(file, (long)offset, SEEK_SET) || fread(buffer, 1, sample, file) != sample) { fclose(file); return -1; }
        for (i = 0; i < sample; i++) if (buffer[i]) { nonzero = 1; break; }
        if (!nonzero) { fclose(file); return -1; }
    }
    if (gs_job.sha[0] && !(gs_transfer_verified && gs_verified_size == gs_job.total)) {
        rewind(file); sha256_init(&context);
        while ((count = fread(buffer, 1, sizeof(buffer), file)) != 0) {
            if (g_stop || gs_canceled) { fclose(file); return -1; }
            sha256_update(&context, buffer, count);
        }
        if (ferror(file)) { fclose(file); return -1; }
        sha256_final(&context, hash);
        for (i = 0; i < sizeof(hash); i++) snprintf(hex + i * 2, 3, "%02x", hash[i]);
        if (strcasecmp(hex, gs_job.sha)) { fclose(file); return -1; }
    }
    fclose(file); return 0;
}

static int gs_start_bgft(void)
{
    char line[64], id[192]; FILE *file; int task, rc;
    int (*initialize)(void *), (*start)(int), (*internal_start)(int);
    struct { void *heap; size_t size; } init;
    file = fopen(GS_IPC_ROOT "/bgft.txt", "rb"); if (!file) return 1;
    if (gs_line(file, line, sizeof(line)) || strcmp(line, "1") || gs_encoded_line(file, id, sizeof(id)) ||
        strcmp(id, gs_job.id) || gs_line(file, line, sizeof(line))) { fclose(file); return 1; }
    fclose(file);
    { int64_t parsed; const char *end;
      if (parse_number(line, &parsed, &end) || *end || parsed > INT32_MAX) return -1;
      task = (int)parsed; }
    if (gs_current_task == task) return 0;
    if (gs_bgft_module < 0) {
        gs_bgft_module = gs_module("/system/common/lib/libSceBgft.sprx");
        if (gs_bind(gs_bgft_module, "sceBgftServiceIntInit", &initialize)) return -1;
        gs_bgft_heap = malloc(1024 * 1024); if (!gs_bgft_heap) return -1;
        init.heap = gs_bgft_heap; init.size = 1024 * 1024;
        rc = initialize(&init); if (rc && (uint32_t)rc != 0x80990001u) return -1;
    }
    if (gs_bind(gs_bgft_module, "sceBgftServiceDownloadStartTask", &start)) return -1;
    rc = start(task);
    if (rc && !gs_bind(gs_bgft_module, "sceBgftServiceIntDownloadStartTask", &internal_start)) rc = internal_start(task);
    if (!rc) gs_current_task = task;
    return rc ? -1 : 0;
}

static void *gs_download_worker(void *unused)
{
    char partial[1050]; struct stat st; FILE *file = NULL; int retries = 0;
    (void)unused;
    if (gs_job.archive == 1) { gs_run_archive(); gs_worker_running = 0; return NULL; }
    snprintf(partial, sizeof(partial), gs_job.archive == 2 ? "%s.resident.part" : "%s.part", gs_job.destination);
    if (access(gs_job.destination, F_OK) == 0) {
        gs_write_status("validating", NULL);
        if (gs_validate(gs_job.destination)) goto invalid;
        gs_done = gs_job.total; goto validated;
    }
    if (gs_network_init()) { gs_write_status("failed", "Resident HTTPS initialization failed"); goto done; }
    if (gs_download_stream(partial)) {
        gs_write_status(gs_canceled ? "canceled" : "failed",
            "Range transfer interrupted; durable chunks retained. Retry to refresh the link.");
        goto done;
    }
    gs_write_status("validating", NULL);
    if (gs_validate(partial) || rename(partial, gs_job.destination)) goto invalid;
validated:
    gs_validated = 1;
    if (gs_job.archive == 2) { gs_run_package(); goto done; }
    gs_write_status("awaiting-bgft", NULL);
    while (!g_stop && !gs_canceled) {
        int rc;
        if (gs_paused) { sceKernelUsleep(100000); continue; }
        rc = gs_start_bgft();
        if (rc == 0) { gs_write_status("complete", NULL); goto done; }
        if (rc < 0) { gs_write_status("failed", "Resident BGFT start failed"); goto done; }
        sceKernelUsleep(100000);
    }
    gs_write_status("canceled", NULL); goto done;
invalid:
    gs_write_status("failed", "Resident PKG validation or storage failed; input retained");
done:
    if (file) fclose(file);
    gs_worker_running = 0;
    return NULL;
}

#include "gs_archive_job.inc"

static void gs_worker_poll(void)
{
    GsJob candidate;
    gs_control();
    if (gs_canceled && !gs_worker_running && gs_job.id[0]) {
        if (!gs_cancel_task()) gs_write_status("canceled", NULL);
        else { gs_release = 0; gs_write_status("failed", "BGFT cancellation unconfirmed; ownership retained"); }
    }
    if (gs_worker_created && !gs_worker_running) { scePthreadJoin(gs_worker, NULL); gs_worker_created = 0; }
    if (gs_release && !gs_worker_running && g_client_count == 0 && gs_archive_task < 0) {
        if (!gs_read_job(&candidate) && !strcmp(candidate.id, gs_job.id)) unlink(GS_IPC_ROOT "/job.txt");
        memset(&gs_job, 0, sizeof(gs_job)); gs_validated = gs_served = gs_canceled = gs_paused = gs_release = 0;
        gs_done = 0; gs_current_task = -1; gs_write_status("idle", NULL);
        gs_archive_journal[0] = 0;
    }
    if (!gs_worker_running && !strcmp(gs_state, "complete")) gs_write_status("complete", NULL);
    if (gs_job.id[0] || gs_worker_running || gs_read_job(&candidate)) return;
    gs_job = candidate; gs_transfer_verified = 0; gs_interval_count = 0; gs_done = 0; gs_validated = gs_served = gs_paused = gs_canceled = 0;
    gs_current_task = -1; gs_worker_running = 1; gs_write_status("ready", NULL);
    if (scePthreadCreate(&gs_worker, NULL, gs_download_worker, NULL, "gs-download")) {
        gs_worker_running = 0; gs_write_status("failed", "Resident download thread creation failed");
    } else gs_worker_created = 1;
}
