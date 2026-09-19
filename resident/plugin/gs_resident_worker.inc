/* Included after the HTTP framing helpers. V2 IPC matches ResidentDownloadService.
 * Only the process holding the loopback listener may consume jobs or heartbeat.
 */
#include "crypto/sha256.h"
#include "../../transfer/gs_transfer.h"
#include "gs_storage_policy.h"
#include <orbis/_types/user.h>

typedef struct {
    char id[192], url[8192], destination[1024], title[16], sha[65], content[49];
    char password[257];
    char password_fallbacks[3][257];
    int password_fallback_count;
    int64_t total;
    int ranges;
    int expected_kind;
    int archive; // 0: legacy V2, 1: RAR V3, 2: self-contained PKG V4
    int auto_install, local_only, native_bgft, borrowed_source;
    char after[192];
    char generation[64], storage_token[64], storage_root[1024];
} GsJob;
static GsJob gs_job;
static void gs_worker_background_priority(const char *phase)
{
    OrbisPthread self = scePthreadSelf(); int32_t current = -1;
    int rc = scePthreadGetprio(self, &current);
    if (!rc && current < ORBIS_KERNEL_PRIO_FIFO_LOWEST)
        rc = scePthreadSetprio(self, ORBIS_KERNEL_PRIO_FIFO_LOWEST);
    gs_log_write("resident", "worker-priority phase=%s previous=%d target=%d rc=0x%08X",
        phase, current, ORBIS_KERNEL_PRIO_FIFO_LOWEST, (unsigned)rc);
}
/* A shell using shared IPC must also open internal staging in that namespace.
 * Removable storage retains its mount and volume identity checks. */
static int gs_resolve_job_storage(GsJob *job)
{
    if (strcmp(g_ipc_root, "/user/data/SSPI/resident") ||
        strncmp(job->destination, "/data/SSPI/downloads/", 21)) return 0;
    char path[1024];
    int n = snprintf(path, sizeof(path), "/user%s", job->destination);
    if (n < 0 || n >= (int)sizeof(path)) return -1;
    snprintf(job->destination, sizeof(job->destination), "%s", path);
    snprintf(job->storage_root, sizeof(job->storage_root), "/user/data/SSPI/downloads");
    return 0;
}
static OrbisPthread gs_worker;
static int gs_worker_created;
static volatile int gs_worker_running, gs_validated, gs_served;
static volatile int gs_paused, gs_canceled, gs_release;
static volatile int64_t gs_done;
static volatile int64_t gs_network_bytes;
static int gs_http = -1;
static int gs_http_module = -1;
static int gs_network_pool = -1, gs_ssl_context = -1;
static int gs_network_ready;
static int gs_net_runtime_bound;
static volatile int gs_network_lock;
static volatile int gs_archive_task = -1;
static int gs_cancel_task(void);
static int gs_pause_archive_task(int paused);
static char gs_archive_journal[1150];
static int gs_package_ordinal;
static int gs_package_legacy_route;
static int gs_transfer_verified;
static unsigned char gs_verified_header[32];
static int64_t gs_verified_size;
static int gs_stream_slot = -1;
static int gs_stage_stream_worker(void);
static int64_t gs_stage_stream_readable(int64_t offset);
static int gs_stage_stream_finished(void);
static uint64_t gs_be64(const unsigned char *p);

static int gs_file_stat(FILE *file, struct stat *value)
{
    int descriptor = fileno(file);
    int64_t at = sceKernelLseek(descriptor, 0, SEEK_CUR);
    int64_t end = sceKernelLseek(descriptor, 0, SEEK_END);
    if (at < 0 || end < 0 || sceKernelLseek(descriptor, at, SEEK_SET) < 0) return -1;
    memset(value, 0, sizeof(*value)); value->st_size = end; value->st_mode = S_IFREG; return 0;
}

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
static int (*gs_http_term)(int), (*gs_ssl_term)(int);
static int (*gs_net_init)(void);
static int (*gs_pool_create)(const char *, int32_t, int32_t);
static int (*gs_pool_destroy)(int);
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
static int (*gs_sema_create)(OrbisKernelSema *,const char *,uint32_t,int32_t,int32_t,const OrbisKernelSemaOptParam *);
static int (*gs_sema_delete)(OrbisKernelSema), (*gs_sema_signal)(OrbisKernelSema,int);
static int (*gs_sema_wait)(OrbisKernelSema,int32_t,OrbisKernelUseconds *);

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
    static uint64_t last;
    static char previous_state[64], previous_error[640];
    char body[1400], id[260], encoded_error[900], path[128];
    if (!error) error = "";
    uint64_t now = sceKernelGetProcessTime() / 1000;
    if (now - last < 250 && !strcmp(state, previous_state) && !strcmp(error, previous_error)) return;
    if (strcmp(state, previous_state) || strcmp(error, previous_error))
        gs_log_write("download", "job=%s generation=%s state=%s done=%lld total=%lld task=%d error=%s", gs_job.id,
            gs_job.generation, state, (long long)gs_done, (long long)gs_job.total, (int)gs_archive_task, error);
    last = now;
    snprintf(previous_state, sizeof(previous_state), "%s", state);
    snprintf(previous_error, sizeof(previous_error), "%s", error);
    gs_b64encode((const unsigned char *)gs_job.id, strlen(gs_job.id), id, sizeof(id));
    if (gs_b64encode((const unsigned char *)error, strlen(error), encoded_error, sizeof(encoded_error)) < 0)
        strcpy(encoded_error, "UmVzaWRlbnQgZXJyb3I=");
    snprintf(gs_state, sizeof(gs_state), "%s", state);
    snprintf(body, sizeof(body), "1\n%s\n%s\n%lld\n%lld\n%d\n%s\n%lld\n%s\n%s\n%llu\n",
        id, state, (long long)gs_done, (long long)gs_job.total, gs_served, encoded_error, (long long)gs_network_bytes,
        gs_job.generation, GS_BUILD_ID, (unsigned long long)g_epoch);
    if (gs_ipc_path(path, sizeof(path), "/status.txt")) return;
    write_telemetry(path, body);
}

// Optional trailer keeps old password records valid and survives staged handoff.
static int gs_read_password_fallbacks(FILE *file, GsJob *job)
{
    int next = fgetc(file);
    if (next == EOF) return ferror(file) ? -1 : 0;
    ungetc(next, file);
    char line[64]; const char *end; int64_t count;
    if (gs_line(file, line, sizeof(line)) || strcmp(line, "password-fallbacks-v1") ||
        gs_line(file, line, sizeof(line)) || parse_number(line, &count, &end) || *end || count < 1 || count > 3 || job->expected_kind) return -1;
    for (int i = 0; i < count; i++)
        if (gs_encoded_line(file, job->password_fallbacks[i], sizeof(job->password_fallbacks[i])) || !job->password_fallbacks[i][0]) return -1;
    if (fgetc(file) != EOF || ferror(file)) return -1;
    job->password_fallback_count = (int)count;
    return 0;
}

static int gs_read_job(GsJob *job)
{
    char line[64], path[128]; const char *end; size_t i; int ok = 0;
    FILE *file;
    if (gs_ipc_path(path, sizeof(path), "/job.txt")) return -1;
    file = fopen(path, "rb");
    if (!file) return -1;
    memset(job, 0, sizeof(*job));
    if (gs_line(file, line, sizeof(line)) != 0 || (strcmp(line, "2") && strcmp(line, "3") && strcmp(line, "4") && strcmp(line, "5") && strcmp(line, "8") && strcmp(line, "9") && strcmp(line, "11") && strcmp(line, "12") && strcmp(line, "14") && strcmp(line, "15") && strcmp(line, "17") && strcmp(line, "19"))) goto done;
    job->native_bgft = !strcmp(line, "17");
    job->borrowed_source = !strcmp(line, "19");
    int has_password = !strcmp(line, "14") || !strcmp(line, "15") || job->borrowed_source;
    int bound_archive = !strcmp(line, "12") || !strcmp(line, "14");
    int local_archive = !strcmp(line, "9") || !strcmp(line, "15") || job->borrowed_source, bound_storage = local_archive || !strcmp(line, "11") || job->native_bgft;
    job->local_only = !strcmp(line, "8") || bound_storage;
    int identity_version = !strcmp(line, "5") || job->local_only;
    job->archive = !strcmp(line, "3") || local_archive || bound_archive ? 1 : (identity_version || !strcmp(line, "4")) ? 2 : 0;
    if (gs_encoded_line(file, job->id, sizeof(job->id)) || gs_encoded_line(file, job->url, sizeof(job->url)) ||
        gs_encoded_line(file, job->destination, sizeof(job->destination)) || gs_encoded_line(file, job->title, sizeof(job->title)) ||
        gs_encoded_line(file, job->sha, sizeof(job->sha)) || gs_encoded_line(file, job->content, sizeof(job->content)) ||
        gs_line(file, line, sizeof(line)) || parse_number(line, &job->total, &end) || *end ||
        job->total < (job->archive == 1 ? 8 : 0x1000) || job->total > (int64_t)8 * 1024 * 1024 * 1024 * 1024) goto done;
    int64_t ranges = 1;
    if (gs_line(file, line, sizeof(line)) || parse_number(line, &ranges, &end) || *end || ranges < 1 || ranges > 16) goto done;
    job->ranges = ranges > GS_XFER_LANES ? GS_XFER_LANES : (int)ranges;
    if (identity_version) {
        int64_t kind;
        if (gs_line(file, line, sizeof(line)) || parse_number(line, &kind, &end) || *end ||
            (job->borrowed_source ? kind != 0 && (kind < 6 || kind > 8) : local_archive ? kind != 0 : kind < 6 || kind > 8)) goto done;
        job->expected_kind = (int)kind;
        if (job->borrowed_source) job->archive = kind ? 2 : 1;
    }
    if (bound_storage && (gs_line(file, job->generation, sizeof(job->generation)) || (job->generation[0] && !gs_hex_token(job->generation)) ||
        gs_line(file, job->storage_token, sizeof(job->storage_token)))) goto done;
    if (bound_archive) {
        int64_t count; char skipped[12000];
        if (gs_line(file, line, sizeof(line)) || parse_number(line, &count, &end) || *end || count < 1 || count > 512) goto done;
        for (int j = 0; j < count * 5; j++) if (gs_line(file, skipped, sizeof(skipped))) goto done;
        if (gs_line(file, job->generation, sizeof(job->generation)) || !gs_hex_token(job->generation) ||
            gs_line(file, job->storage_token, sizeof(job->storage_token))) goto done;
    }
    if (has_password && (gs_encoded_line(file, job->password, sizeof(job->password)) || gs_read_password_fallbacks(file, job))) goto done;
    if (!job->id[0] || (!job->borrowed_source && !job->url[0]) || (job->archive != 1 && strlen(job->content) != 36)) goto done;
    for (i = 0; job->id[i]; i++)
        if (!((job->id[i] >= 'a' && job->id[i] <= 'z') || (job->id[i] >= 'A' && job->id[i] <= 'Z') ||
            (job->id[i] >= '0' && job->id[i] <= '9') || job->id[i] == '_' || job->id[i] == '-')) goto done;
    int external = job->borrowed_source ? gs_local_source_root(job->destination, job->storage_root, sizeof(job->storage_root))
        : gs_staging_root(job->destination, job->storage_root, sizeof(job->storage_root));
    if (external < 0 || (external && ((!bound_storage && !bound_archive) || !gs_hex_token(job->storage_token)))) goto done;
    if (job->native_bgft && (external || job->sha[0] || (job->expected_kind != 6 && job->expected_kind != 8))) goto done;
    if (job->borrowed_source) { if (job->url[0] || job->sha[0] || !gs_hex_token(job->generation) || (job->expected_kind && job->total < 0x1000)) goto done; }
    else if (strncmp(job->url, "https://", 8) != 0 && strncmp(job->url, "http://", 7) != 0) goto done;
    if (job->sha[0]) {
        if (strlen(job->sha) != 64) goto done;
        for (i = 0; i < 64; i++) if (!strchr("0123456789abcdefABCDEF", job->sha[i])) goto done;
    }
    if (gs_resolve_job_storage(job)) goto done;
    ok = 1;
done:
    fclose(file); return ok ? 0 : -1;
}

static int gs_read_control_action(const char *path)
{
    char line[300], id[192];
    int action = 0;
    FILE *file = fopen(path, "rb");
    if (!file) return 0;
    if (gs_line(file, line, sizeof(line)) == 0 && (!strcmp(line, "1") || !strcmp(line, "2"))) {
        int version = atoi(line); char generation[80];
        if (gs_encoded_line(file, id, sizeof(id)) || strcmp(id, gs_job.id) || gs_line(file, line, sizeof(line)) ||
            (version == 2 && (gs_line(file, generation, sizeof(generation)) || strcmp(generation, gs_job.generation))) ||
            (version == 1 && gs_job.generation[0])) { fclose(file); return 0; }
        if (strcmp(line, "pause") == 0) action = 1;
        if (strcmp(line, "resume") == 0) action = 2;
        if (strcmp(line, "cancel") == 0) action = 3;
        if (strcmp(line, "release") == 0) action = 4;
        // Keep the desired state. Unlinking after this read can remove a newer
        // atomic replacement (notably resume arriving while pause is applied).
    }
    fclose(file);
    return action;
}

static void gs_control(void)
{
    char path[128];
    if (gs_ipc_path(path, sizeof(path), "/control.txt")) return;
    int action = gs_read_control_action(path);
    if (action == 1 && !gs_paused && !gs_pause_archive_task(1)) gs_paused = 1;
    if (action == 2 && gs_paused && !gs_pause_archive_task(0)) gs_paused = 0;
    if (action >= 3) gs_canceled = 1;
    if (action == 4) gs_release = 1;
}

static void gs_archive_control(void);

#define GS_MODULE_CANDIDATES 8
#define GS_MODULE_LIST_CAPACITY 1024

static int gs_module_name_matches(const char *module, const char *candidate)
{
    const char *separator;
    size_t length;
    if (!module || !candidate || !module[0] || !candidate[0]) return 0;
    if (!strcmp(module, candidate)) return 1;
    separator = strrchr(module, '/');
    if (separator && !strcmp(separator + 1, candidate)) return 1;
    separator = strrchr(candidate, '/');
    if (separator && !strcmp(module, separator + 1)) return 1;
    length = strlen(candidate);
    separator = strrchr(candidate, '.');
    if (separator) length = (size_t)(separator - candidate);
    return !strncmp(module, candidate, length) &&
        (module[length] == 0 || module[length] == '.' || module[length] == '_' || module[length] == '-');
}

static int gs_module_push(const char **values, int count, const char *value)
{
    int i;
    for (i = 0; i < count; i++) if (!strcmp(values[i], value)) return count;
    if (count < GS_MODULE_CANDIDATES) values[count] = value;
    return count < GS_MODULE_CANDIDATES ? count + 1 : count;
}

static int gs_module_scan(const char *const *names, int count, int *codes, size_t *scanned)
{
    OrbisKernelModule modules[GS_MODULE_LIST_CAPACITY];
    OrbisKernelModuleInfo info;
    size_t available = 0, index;
    int found = -1, name;
    if (scanned) *scanned = 0;
    if (gs_native_module_list(modules, GS_MODULE_LIST_CAPACITY, &available)) return -1;
    if (available > GS_MODULE_LIST_CAPACITY) available = GS_MODULE_LIST_CAPACITY;
    if (scanned) *scanned = available;
    for (index = 0; index < available && found < 0; index++) {
        memset(&info, 0, sizeof(info)); info.size = sizeof(info);
        if (gs_native_module_info(modules[index], &info)) continue;
        for (name = 0; name < count; name++) {
            if (!gs_module_name_matches(info.name, names[name])) continue;
            found = (int)modules[index];
            if (codes) codes[name] = found;
            break;
        }
    }
    return found;
}

static int gs_sysmodule_preload(const char *stem);

/* SceShellUI resolves system libraries by name inside its own namespace and
 * rejects the absolute /system/common/lib paths. Prefer a handle already
 * mapped into this process and only fall back to loading candidate paths. */
static int gs_module(const char *path)
{
    char base[128], stem[128], system_path[160], common_path[160], system_name[160], common_name[160];
    const char *names[GS_MODULE_CANDIDATES], *loads[GS_MODULE_CANDIDATES];
    int codes[GS_MODULE_CANDIDATES], name_count = 0, load_count = 0, index, found, loaded = -1;
    const char *separator, *dot;
    size_t scanned = 0;
    if (!path || !path[0]) return -1;
    separator = strrchr(path, '/');
    snprintf(base, sizeof(base), "%s", separator ? separator + 1 : path);
    snprintf(stem, sizeof(stem), "%s", base);
    dot = strchr(stem, '.');
    if (dot) stem[dot - stem] = 0;
    snprintf(system_path, sizeof(system_path), "/system/common/lib/%s", base);
    snprintf(common_path, sizeof(common_path), "/common/lib/%s", base);
    snprintf(system_name, sizeof(system_name), "/system/common/lib/%s", stem);
    snprintf(common_name, sizeof(common_name), "/common/lib/%s", stem);
    name_count = gs_module_push(names, name_count, path);
    name_count = gs_module_push(names, name_count, system_path);
    name_count = gs_module_push(names, name_count, common_path);
    name_count = gs_module_push(names, name_count, base);
    name_count = gs_module_push(names, name_count, system_name);
    name_count = gs_module_push(names, name_count, common_name);
    name_count = gs_module_push(names, name_count, stem);
    for (index = 0; index < name_count; index++) codes[index] = (int)0x80020002u;
    found = gs_module_scan(names, name_count, codes, &scanned);
    for (index = 0; index < name_count; index++)
        gs_log_write("resident", "Resident module name=%s candidate=%s stage=loaded rc=0x%08X",
            stem, names[index], (unsigned)codes[index]);
    gs_log_write("resident", "Resident module scan name=%s modules=%u handle=0x%08X",
        stem, (unsigned)scanned, (unsigned)found);
    if (found >= 0) return found;
    load_count = gs_module_push(loads, load_count, path);
    load_count = gs_module_push(loads, load_count, system_path);
    load_count = gs_module_push(loads, load_count, common_path);
    load_count = gs_module_push(loads, load_count, base);
    for (index = 0; index < load_count; index++) {
        int result = sceKernelLoadStartModule(loads[index], 0, NULL, 0, NULL, NULL);
        gs_log_write("resident", "Resident module name=%s candidate=%s stage=load rc=0x%08X",
            stem, loads[index], (unsigned)result);
        if (result >= 0) return result;
        if (index == 0) loaded = result;
    }
    if (loaded < 0) {
        // Internal sysmodules can stay unresolvable by name even here; ask the
        // sysmodule table to map them the way the app transport does.
        int sysmodule = gs_sysmodule_preload(stem);
        if (sysmodule >= 0) return sysmodule;
        if (sysmodule == -2) {
            for (index = 0; index < load_count; index++) {
                int result = sceKernelLoadStartModule(loads[index], 0, NULL, 0, NULL, NULL);
                gs_log_write("resident", "Resident module name=%s candidate=%s stage=sysmodule-load rc=0x%08X",
                    stem, loads[index], (unsigned)result);
                if (result >= 0) return result;
            }
        }
    }
    return loaded;
}

static int gs_bind(int module, const char *name, void *slot)
{
    void *symbol = NULL;
    int failed = module < 0 || sceKernelDlsym(module, name, &symbol) != 0 || !symbol;
    if (failed) symbol = NULL;
    memcpy(slot, &symbol, sizeof(symbol)); return failed ? -1 : 0;
}

/* Internal sysmodules stay unresolvable for sceKernelLoadStartModule until the
 * sysmodule table owns them. Returns a process handle, -2 when the caller
 * should retry loading now that the table is updated, or -1 when unavailable. */
static int gs_sysmodule_preload(const char *stem)
{
    int (*load_module)(unsigned int) = NULL;
    int (*is_loaded)(unsigned int) = NULL;
    int (*module_handle)(unsigned int, int *) = NULL;
    int id = 0, handle = -1, module, result;
    if (!strcmp(stem, "libSceNet")) id = 0x8000001C;
    else if (!strcmp(stem, "libSceHttp")) id = 0x8000000A;
    else if (!strcmp(stem, "libSceSsl")) id = 0x8000000B;
    else if (!strcmp(stem, "libSceBgft")) id = 0x8000002A;
    if (!id) return -1;
    module = gs_module("libSceSysmodule.sprx");
    if (module < 0 || gs_bind(module, "sceSysmoduleLoadModuleInternal", &load_module) ||
        gs_bind(module, "sceSysmoduleGetModuleHandleInternal", &module_handle)) {
        gs_log_write("resident", "Resident sysmodule interface unavailable name=%s module=%d", stem, module);
        return -1;
    }
    gs_bind(module, "sceSysmoduleIsLoadedInternal", &is_loaded);
    if (!is_loaded || is_loaded((unsigned)id) != 1) {
        result = load_module((unsigned)id);
        gs_log_write("resident", "Resident sysmodule preload name=%s id=0x%08X rc=0x%08X", stem, (unsigned)id, (unsigned)result);
    }
    if (!module_handle((unsigned)id, &handle) && handle >= 0) {
        gs_log_write("resident", "Resident sysmodule handle name=%s id=0x%08X handle=0x%08X", stem, (unsigned)id, (unsigned)handle);
        return handle;
    }
    gs_log_write("resident", "Resident sysmodule handle unavailable name=%s id=0x%08X", stem, (unsigned)id);
    return -2;
}

static int gs_net_runtime_start(void)
{
    if (gs_net_runtime_bound) return 0;
    int module = gs_module("/system/common/lib/libSceNet.sprx");
    if (module < 0) {
        gs_log_write("network", "Resident network module unavailable name=libSceNet code=0x%08X", (unsigned)module); return -1;
    }
#define GS_NET_BIND(name, target) if (gs_bind(module, name, &(target))) { gs_log_write("network", "Resident network export unavailable name=%s module=%d", name, module); return -1; }
    GS_NET_BIND("sceNetInit", gs_net_init);
    GS_NET_BIND("sceNetPoolCreate", gs_pool_create);
    GS_NET_BIND("sceNetPoolDestroy", gs_pool_destroy);
#undef GS_NET_BIND
    int result = gs_net_init();
    gs_log_write("network", "Resident network initialization code=0x%08X", (unsigned)result);
    // The process may already own sceNetInit. As in NativeHttp, do not infer
    // readiness from its return: later private-pool/TLS/HTTP setup must succeed.
    if (!result) gs_net_runtime_bound = 1;
    return 0;
}

static int gs_net_runtime_init(void)
{
    while (__sync_lock_test_and_set(&gs_network_lock, 1)) sceKernelUsleep(1000);
    int result = gs_net_runtime_start();
    __sync_lock_release(&gs_network_lock);
    return result;
}

static int gs_bgft_ready_probe(void)
{
    int (*init)(void *), (*start)(int), (*progress)(int, void *); void *registration = NULL;
    int module = gs_module("/system/common/lib/libSceBgft.sprx");
    if (module < 0) {
        gs_log_write("resident", "Resident BGFT module unavailable name=libSceBgft code=0x%08X", (unsigned)module); return -1;
    }
    if (gs_bind(module, "sceBgftServiceIntInit", &init) ||
        gs_bind(module, "sceBgftServiceDownloadStartTask", &start) ||
        gs_bind(module, "sceBgftServiceDownloadGetProgress", &progress) ||
        gs_bind(module, "sceBgftServiceIntDebugDownloadRegisterPkg", &registration)) {
        gs_log_write("resident", "BGFT readiness export binding failed module=%d", module); return -1;
    }
    if (!gs_bgft_heap) gs_bgft_heap = calloc(1, 1024 * 1024);
    if (!gs_bgft_heap) return -1;
    struct { void *heap; size_t size; } config = { gs_bgft_heap, 1024 * 1024 };
    int rc = init(&config);
    if (rc && (uint32_t)rc != 0x80990001u) { gs_log_write("resident", "BGFT readiness initialization failed code=0x%08X", (unsigned)rc); return -1; }
    gs_bgft_module = module; return 0;
}

static void gs_bind_queue_events(void)
{
    OrbisKernelModule modules[256] = {0}; size_t count = 0;
    if (gs_native_module_list(modules, 256, &count)) return;
    if (count > 256) count = 256;
    for (size_t i = 0; i < count; i++) {
        OrbisKernelModuleInfo info; memset(&info, 0, sizeof(info)); info.size = sizeof(info);
        if (gs_native_module_info(modules[i], &info) || strncmp(info.name, "libkernel", 9) ||
            (info.name[9] && info.name[9] != '_' && info.name[9] != '.')) continue;
        if (!gs_bind(modules[i], "sceKernelCreateSema", &gs_sema_create) &&
            !gs_bind(modules[i], "sceKernelDeleteSema", &gs_sema_delete) &&
            !gs_bind(modules[i], "sceKernelSignalSema", &gs_sema_signal) &&
            !gs_bind(modules[i], "sceKernelWaitSema", &gs_sema_wait)) return;
        gs_sema_create = NULL; gs_sema_delete = NULL; gs_sema_signal = NULL; gs_sema_wait = NULL;
    }
}

typedef struct { void *data; size_t size; } GsCaData;
typedef struct GsCaAllocation {
    GsCaData cert;
    struct GsCaAllocation *next;
} GsCaAllocation;
static GsCaAllocation *gs_ca_allocations;
static GsCaData *gs_ca_list[32];
static int gs_ca_count;

static int gs_ca_selected(const unsigned char *der, size_t size, int policy)
{
    // The exact bundled roots selected by NativeHttp's subject policy. The host
    // regression checks parity so a bundle/policy change cannot silently drift.
    static const char *roots[] = {
        "CBB522D7B7F127AD6A0113865BDF1CD4102E7D0759AF635A7CF4720DC963C53B",
        "45140B3247EB9CC8C5B4F0D7B53091F73292089E6E5A63E2749DD3ACA9198EDA",
        "CB3CCBB76031E5E0138F8DD39A23F9DE47FFC35E43C1144CEA27D46A5AB1CB5F",
        "552F7BDCF1A7AF9E6CE672017F4F12ABF77240C78E761AC203D1D9D20AC89988",
        "52F0E1C4E58EC629291B60317F074671B85D7EA80D5B07273463534B32B40234",
        "E793C9B02FD8AA13E21C31228ACCB08119643B749C898964B1746D46C3D4CBD2",
        "96BCEC06264976F37460779ACF28C5A7CFE8A3C0AAE11A8FFCEE05C0BDDF08C6",
        "8ECDE6884F3D87B1125BA31AC3FCB13D7016DE7F57CC904FE1CB97C6AE98196E",
        "2CABEAFE37D06CA22ABA7391C0033D25982952C453647349763A3AB5AD6CCF69",
        "C741F70F4B2A8D88BF2E71C14122EF53EF10EBA0CFA5E64CFA20F418853073E0",
        "4FA3126D8D3A11D1C4855A4F807CBAD6CF919D3A5A88B03BEA2C6372D93C40C9",
        "69729B8E15A86EFC177A57AFB7171DFC64ADD28C2FCA8CF1507E34453CCB1470",
        "D947432ABDE7B7FA90FC2E6B59101B1280E0E1C7E4E40FA3C6887FFF57A7F4CF",
        "34D8A73EE208D9BCDB0D956520934B4E40E69482596E8B6F73C8426B010A6F48",
        "349DFA4058C5E263123B398AE795573C4E1313C83FE68F93556CD5E8031B3C7D",
        "7BB647A62AEEAC88BF257AA522D01FFEA395E0AB45C73F93F65654EC38F25A06"
    };
    SHA256_CTX context; unsigned char digest[32]; char hex[65];
    sha256_init(&context); sha256_update(&context, der, size); sha256_final(&context, digest);
    for (int i = 0; i < 32; i++) snprintf(hex + 2 * i, 3, "%02X", digest[i]);
    if (policy == 1) return !strcmp(hex, "E57B7E6F150C419102E8D5C055729FF967B9D1A829BF00CEC89CA604EBF4A86F") ||
        !strcmp(hex, "E14FFCAD5B0025731006CAA43A121A22D8E9700F4FB9CF852F02A708AA5D5666");
    if (policy == 2) return !strcmp(hex, "C06E307F7CFC1D32FA72A4C033C87B90019AF216F0775D64978A2ECA6C8A230E");
    if (policy == 4) return !strcmp(hex, "A287FFAB762CC69A26D482037EDF701F653CE899025C62A7E5CB88BB9B419CBB");
    if (policy == 3) return !strcmp(hex, "13949634D99CD6FD6AA80BC034FEFACCEB1969FEEF986586713ECDBB05758D3F") ||
        !strcmp(hex, "238B85A0099C65B970477D5724F1A1D475CE5058CFFE4EFA8733899BDB863C47") ||
        !strcmp(hex, "A2372D06431E9716365EEED47EC020351497D182FCC038E457E58168A03CAC07") ||
        !strcmp(hex, "97658DE8C68DFA98ACE1E5028A63D54A1AAE911B3E21471076C6850CD08CBAB4");
    for (size_t i = 0; i < sizeof(roots) / sizeof(roots[0]); i++) if (!strcmp(hex, roots[i])) return 1;
    return 0;
}

static int gs_network_reset(void)
{
    gs_network_ready = 0;
    if (gs_http >= 0) {
        int rc = gs_http_term(gs_http);
        if (rc) { gs_log_write("network", "Resident HTTP cleanup failed code=0x%08X", (unsigned)rc); return -1; }
        gs_http = -1;
    }
    // Firmware can retain certificate pointers until the HTTP context ends.
    while (gs_ca_allocations) {
        GsCaAllocation *cert = gs_ca_allocations;
        gs_ca_allocations = cert->next; free(cert->cert.data); free(cert);
    }
    memset(gs_ca_list, 0, sizeof(gs_ca_list)); gs_ca_count = 0;
    if (gs_ssl_context >= 0) {
        int rc = gs_ssl_term(gs_ssl_context);
        if (rc) { gs_log_write("network", "Resident TLS cleanup failed code=0x%08X", (unsigned)rc); return -1; }
        gs_ssl_context = -1;
    }
    if (gs_network_pool >= 0) {
        int rc = gs_pool_destroy(gs_network_pool);
        if (rc) { gs_log_write("network", "Resident network pool cleanup failed code=0x%08X", (unsigned)rc); return -1; }
        gs_network_pool = -1;
    }
    return 0;
}

static int gs_load_ca(const char *path, int policy)
{
    FILE *file = fopen(path, "rb");
    char *pem, *at; long length; int loaded = 0;
    const char *name = strrchr(path, '/'); name = name ? name + 1 : path;
    if (!file) { gs_log_write("network", "Resident CA file unavailable name=%s errno=%d", name, errno); return -1; }
    if (fseek(file, 0, SEEK_END) || (length = ftell(file)) <= 0 || length > 1024 * 1024) { fclose(file); return -1; }
    rewind(file); pem = (char *)calloc(1, (size_t)length + 1);
    if (!pem) { fclose(file); return -1; }
    if (fread(pem, 1, (size_t)length, file) != (size_t)length) { free(pem); fclose(file); return -1; }
    fclose(file); at = pem;
    while ((at = strstr(at, "-----BEGIN CERTIFICATE-----")) != NULL) {
        char *begin = at, *end; GsCaAllocation *cert; int decoded;
        at += 27; end = strstr(at, "-----END CERTIFICATE-----");
        if (!end) { free(pem); return -1; }
        *end = 0; cert = (GsCaAllocation *)calloc(1, sizeof(*cert));
        if (!cert) { free(pem); return -1; }
        unsigned char *der = malloc((size_t)(end - at) + 1);
        if (!der) { free(cert); free(pem); return -1; }
        decoded = gs_b64decode(at, der, (size_t)(end - at) + 1);
        *end = '-';
        int selected = decoded > 0 && gs_ca_selected(der, (size_t)decoded, policy);
        free(der);
        if (decoded <= 0) { free(cert); free(pem); return -1; }
        if (!selected) {
            free(cert);
            if (policy) { gs_log_write("network", "Resident pinned CA mismatch name=%s", name); free(pem); return -1; }
            at = end + strlen("-----END CERTIFICATE-----"); continue;
        }
        if (gs_ca_count >= (int)(sizeof(gs_ca_list) / sizeof(gs_ca_list[0]))) { free(cert); free(pem); return -1; }
        // Match the working app transport: complete PEM, CRLF lines, not ASN.1 DER.
        size_t block = (size_t)(end + strlen("-----END CERTIFICATE-----") - begin);
        char *encoded = malloc(block * 2 + 3); size_t used = 0;
        if (!encoded) { free(cert); free(pem); return -1; }
        for (size_t i = 0; i < block; i++) {
            if (begin[i] == '\r' || begin[i] == '\n') {
                if (begin[i] == '\r' && i + 1 < block && begin[i + 1] == '\n') i++;
                encoded[used++] = '\r'; encoded[used++] = '\n';
            } else encoded[used++] = begin[i];
        }
        encoded[used++] = '\r'; encoded[used++] = '\n'; encoded[used] = 0;
        cert->cert.data = encoded; cert->cert.size = used; gs_ca_list[gs_ca_count++] = &cert->cert;
        // Keep data, records and the pointer array alive until HTTP teardown.
        cert->next = gs_ca_allocations; gs_ca_allocations = cert;
        loaded++; at = end + strlen("-----END CERTIFICATE-----");
    }
    free(pem); return loaded > 0 && (policy != 1 || loaded == 2) && (policy != 2 || loaded == 1) && (policy != 3 || loaded == 4) ? 0 : -1;
}

static int gs_network_start(void)
{
    int http_module, ssl_module;
    char ca_path[512];
    if (gs_network_ready) return 0;
    if (gs_network_reset()) return -1;
    // Keep libSceNet out of the PRX's load-time dependencies. Resolve it only
    // after our diagnostic thread is running, as the app transport does.
    if (gs_net_runtime_start()) return -1;
    ssl_module = gs_module("/system/common/lib/libSceSsl.sprx");
    http_module = gs_module("/system/common/lib/libSceHttp.sprx");
#define GS_BIND(name, target) if (gs_bind(http_module, name, &(target))) { gs_log_write("network", "Resident HTTP export unavailable name=%s module=%d", name, http_module); return -1; }
    GS_BIND("sceHttpInit", gs_http_init); GS_BIND("sceHttpTerm", gs_http_term); GS_BIND("sceHttpCreateTemplate", gs_template);
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
    if (http_module >= 0) gs_http_module = http_module;
    if (gs_bind(ssl_module, "sceSslInit", &gs_ssl_init) || gs_bind(ssl_module, "sceSslTerm", &gs_ssl_term)) {
        gs_log_write("network", "Resident TLS initialization exports unavailable module=%d", ssl_module); return -1;
    }
    gs_bind_queue_events();
    // SceShellUI may already own network initialization. A successful private
    // pool, followed by TLS/HTTP/trust setup, proves our usable capability.
    gs_network_pool = gs_pool_create("GameSearchResident", 8 * 1024 * 1024, 0);
    if (gs_network_pool < 0) { gs_log_write("network", "Resident network pool failed code=0x%08X", (unsigned)gs_network_pool); goto failed; }
    gs_net_runtime_bound = 1;
    gs_ssl_context = gs_ssl_init(8 * 1024 * 1024);
    if (gs_ssl_context < 0) { gs_log_write("network", "Resident TLS pool failed code=0x%08X", (unsigned)gs_ssl_context); goto failed; }
    gs_http = gs_http_init(gs_network_pool, gs_ssl_context, 8 * 1024 * 1024);
    if (gs_http < 0) { gs_log_write("network", "Resident HTTP context failed code=0x%08X", (unsigned)gs_http); goto failed; }
    // Certificate verification stays enabled. Add the same shipped roots as the app.
    if (gs_ipc_path(ca_path, sizeof(ca_path), "/certs/letsencrypt-generation-y-intermediates.pem") || gs_load_ca(ca_path, 3) != 0 ||
        gs_ipc_path(ca_path, sizeof(ca_path), "/certs/torbox-google-we1.pem") || gs_load_ca(ca_path, 4) != 0 ||
        gs_ipc_path(ca_path, sizeof(ca_path), "/certs/real-debrid-geotrust-tls-rsa-ca-g1.pem") || gs_load_ca(ca_path, 2) != 0 ||
        gs_ipc_path(ca_path, sizeof(ca_path), "/certs/letsencrypt-generation-y-roots.pem") || gs_load_ca(ca_path, 1) != 0 ||
        gs_ipc_path(ca_path, sizeof(ca_path), "/certs/ca-certificates.crt") || gs_load_ca(ca_path, 0) != 0) goto failed;
    int cert_rc = gs_load_cert(gs_http, gs_ca_count, gs_ca_list, NULL, NULL);
    if (cert_rc) { gs_log_write("network", "Resident CA import failed count=%d code=0x%08X", gs_ca_count, (unsigned)cert_rc); goto failed; }
    gs_network_ready = 1;
    gs_log_write("network", "Resident HTTPS ready with bundled PEM trust");
    return 0;
failed:
    gs_network_reset();
    return -1;
}

static int gs_network_init(void)
{
    // Readiness polling and legacy job startup share these process-owned contexts.
    while (__sync_lock_test_and_set(&gs_network_lock, 1)) sceKernelUsleep(1000);
    int rc = gs_network_start();
    __sync_lock_release(&gs_network_lock);
    return rc;
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

#include "gs_transfer_host.inc"

static uint64_t gs_be64(const unsigned char *p)
{
    uint64_t value = 0; int i;
    for (i = 0; i < 8; i++) value = (value << 8) | p[i];
    return value;
}

#include "gs_pkg_validation.h"

static int gs_validate(const char *path)
{
    unsigned char header[0x1000]; struct stat st;
    FILE *file = fopen(path, "rb");
    if (!file) { gs_write_status("failed", "PKG cannot be opened; download retained"); return -1; }
    const char *error = NULL;
    if (gs_file_stat(file, &st)) error = "PKG file size cannot be read";
    else if (fread(header, 1, sizeof(header), file) != sizeof(header)) error = "PKG header is incomplete or unreadable";
    else error = gs_pkg_header_error(header, st.st_size, gs_job.total, gs_job.title, gs_job.content);
    fclose(file);
    if (error) { gs_write_status("failed", error); return -1; }
    if (gs_job.sha[0]) {
        char detail[256];
        if (sspi_xfer_verify_local(path, gs_job.destination, gs_job.total, gs_job.title, gs_job.content, gs_job.sha, detail, sizeof(detail))) {
            gs_write_status("failed", detail); return -1;
        }
    }
    return 0;
}

typedef struct {
    uint32_t bits; int error; uint64_t length, transferred, length_total, transferred_total;
    uint32_t index, count, seconds, seconds_total; int preparing, copy;
} GsBgftProgress;
_Static_assert(sizeof(GsBgftProgress) == 64, "BGFT progress ABI");

static const char *gs_bgft_error(int code)
{
    switch ((uint32_t)code) {
        case 0: return "success";
        case 0x80990001u: return "service already initialized";
        case 0x80990004u: return "BGFT rejected the registration arguments";
        case 0x80990015u: return "duplicate task";
        case 0x80990019u: return "task not found";
        case 0x8099002Cu: return "PS4 could not receive the package data (HTTP receive error)";
        case 0x80990086u: return "content already queued";
        case 0x80990088u: return "installed content conflict";
        case 0x80F00633u: return "NP environment rejected registration; a retry repeats the same system rejection";
        case 0x80991404u: return "package source unavailable";
        case 0x80020012u: return "cross-device path rejected";
        default: return "system error";
    }
}

/* Deterministic platform rejections for this package, account and console state.
 * A staged retry re-registers the same task with the same metadata and gets the
 * same answer, so the queue must fail once and release the installer slot
 * instead of repeating it. Recoverable conditions are deliberately absent:
 * 0x80991404 (loopback source) and 0x8099002C (receive error) keep their own
 * bounded source/stall retries, and 0x80990015/0x80990086 keep their duplicate
 * recovery. Codes are recorded in the status text as BGFT_TERMINAL: so the
 * staged poll classifies by evidence, not by a heuristic. */
static int gs_bgft_error_terminal(int code)
{
    switch ((uint32_t)code) {
        case 0x80F00633u: return 1; /* NP environment rejected registration */
        case 0x80990004u: return 1; /* registration arguments rejected */
        case 0x80990088u: return 1; /* installed content conflict */
        case 0x80020012u: return 1; /* cross-device path rejected */
        default: return 0;
    }
}

static int gs_start_owned_bgft(int module, int task)
{
    int (*start)(int), (*internal_start)(int), (*progress)(int, GsBgftProgress *);
    int public_rc = -1, internal_rc = -1, progress_error = 0, accepted = 0, have_prior = 0;
    const char *ack = "unconfirmed";
    GsBgftProgress prior = {0}; char record[256], path[128];
    if (task < 0 || g_stop || gs_canceled) return -1;
    if (!gs_bind(module, "sceBgftServiceDownloadStartTask", &start)) public_rc = start(task);
    if (!public_rc) { accepted = 1; ack = "public"; }
    else {
        if (!gs_bind(module, "sceBgftServiceIntDownloadStartTask", &internal_start)) internal_rc = internal_start(task);
        if (!internal_rc) { accepted = 1; ack = "internal"; }
        else if (!gs_bind(module, "sceBgftServiceDownloadGetProgress", &progress)) {
            for (int probe = 0; probe <= 10 && !g_stop && !gs_canceled; probe++) {
                if (probe) sceKernelUsleep(500000);
                if (g_stop || gs_canceled) break;
                GsBgftProgress current = {0};
                if (progress(task, &current)) continue;
                if (current.error) { progress_error = current.error; break; }
                uint64_t done = current.transferred_total ? current.transferred_total : current.transferred;
                uint64_t total = current.length_total ? current.length_total : current.length;
                uint64_t prior_done = prior.transferred_total ? prior.transferred_total : prior.transferred;
                if ((total && done >= total && current.copy >= 100) ||
                    (have_prior && (done > prior_done || current.preparing > prior.preparing || current.copy > prior.copy))) {
                    accepted = 1; ack = "progress"; break;
                }
                prior = current; have_prior = 1;
            }
        }
    }
    snprintf(record, sizeof(record), "task=%d\npublic=0x%08X\ninternal=0x%08X\nprogress=0x%08X\nack=%s\n",
        task, (unsigned)public_rc, (unsigned)internal_rc, (unsigned)progress_error, ack);
    if (!gs_ipc_path(path, sizeof(path), "/bgft-start.txt")) write_atomic(path, record);
    gs_log_write("resident", "bgft-start job=%s task=%d public=0x%08X internal=0x%08X progress=0x%08X ack=%s",
        gs_job.id, task, (unsigned)public_rc, (unsigned)internal_rc, (unsigned)progress_error, ack);
    if (!accepted && !g_stop && !gs_canceled) {
        int code = progress_error ? progress_error : internal_rc;
        snprintf(record, sizeof(record), "BGFT start unconfirmed: 0x%08X (%s). Task and PKG retained; inspect PS4 Notifications.",
            (unsigned)code, gs_bgft_error(code));
        gs_write_status("failed", record);
    }
    return accepted ? 0 : -1;
}

static int gs_start_bgft(void)
{
    char line[64], id[192], path[128]; FILE *file; int task, rc;
    int (*initialize)(void *);
    struct { void *heap; size_t size; } init;
    if (gs_ipc_path(path, sizeof(path), "/bgft.txt")) return 1;
    file = fopen(path, "rb"); if (!file) return 1;
    if (gs_line(file, line, sizeof(line)) || strcmp(line, "1") || gs_encoded_line(file, id, sizeof(id)) ||
        strcmp(id, gs_job.id) || gs_line(file, line, sizeof(line))) { fclose(file); return 1; }
    fclose(file);
    { int64_t parsed; const char *end;
      if (parse_number(line, &parsed, &end) || *end || parsed < 0 || parsed > INT32_MAX) return -1;
      task = (int)parsed; }
    if (gs_current_task == task) return 0;
    if (gs_bgft_module < 0) {
        int module = gs_module("/system/common/lib/libSceBgft.sprx");
        if (module < 0) {
            gs_log_write("resident", "Resident BGFT module unavailable name=libSceBgft code=0x%08X", (unsigned)module); return -1;
        }
        if (gs_bind(module, "sceBgftServiceIntInit", &initialize)) return -1;
        if (!gs_bgft_heap) gs_bgft_heap = calloc(1, 1024 * 1024);
        if (!gs_bgft_heap) return -1;
        init.heap = gs_bgft_heap; init.size = 1024 * 1024;
        rc = initialize(&init); if (rc && (uint32_t)rc != 0x80990001u) return -1;
        gs_bgft_module = module;
    }
    rc = gs_start_owned_bgft(gs_bgft_module, task);
    if (!rc) gs_current_task = task;
    return rc ? -1 : 0;
}

static int gs_resume_missing_addon(void);

static void *gs_download_worker(void *unused)
{
    char partial[1050]; struct stat st; FILE *file = NULL; int retries = 0;
    (void)unused;
    gs_worker_background_priority("install");
    if (gs_stream_slot >= 0) {
        gs_stage_stream_worker(); gs_worker_running = 0; return NULL;
    }
    if (!gs_storage_matches(gs_job.storage_root, gs_job.storage_token)) {
        gs_write_status("failed", "Selected staging drive unavailable; input and install ownership retained");
        gs_worker_running = 0; return NULL;
    }
    if (gs_job.archive == 2 && gs_job.expected_kind == 7 && access(gs_job.destination, F_OK) &&
        errno == ENOENT && gs_resume_missing_addon()) goto done;
    if (gs_job.borrowed_source && !gs_local_source_check(gs_job.destination, gs_job.storage_root, gs_job.storage_token)) {
        gs_write_status("failed", "Selected USB file path is unavailable or linked; original files retained");
        gs_worker_running = 0; return NULL;
    }
    if (gs_job.archive == 1) { gs_run_archive(); gs_worker_running = 0; return NULL; }
    snprintf(partial, sizeof(partial), gs_job.archive == 2 ? "%s.resident.part" : "%s.part", gs_job.destination);
    if (gs_job.native_bgft) { gs_validated = 1; gs_run_package(); goto done; }
    if (access(gs_job.destination, F_OK) == 0) {
        gs_write_status("validating", NULL);
        if (gs_validate(gs_job.destination)) goto invalid;
        gs_done = gs_job.total; goto validated;
    }
    if (gs_job.local_only) { gs_write_status("failed", "Staged installation input is missing; download ownership was not restarted"); goto done; }
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
    int attachment_polls = 0;
    while (!g_stop && !gs_canceled) {
        int rc;
        if (gs_paused) { sceKernelUsleep(100000); continue; }
        rc = gs_start_bgft();
        if (rc == 0) { gs_write_status("complete", NULL); goto done; }
        if (rc < 0) { if (strcmp(gs_state, "failed")) gs_write_status("failed", "Resident BGFT start failed; PKG retained"); goto done; }
        if (++attachment_polls >= 1800) {
            gs_write_status("failed", "BGFT attachment timed out; open SSPI to finish the pending install. PKG retained."); goto done;
        }
        sceKernelUsleep(100000);
    }
    gs_write_status("canceled", NULL); goto done;
invalid:
    if (strcmp(gs_state, "failed")) gs_write_status("failed", "Completed PKG could not be renamed; input retained");
done:
    if (file) fclose(file);
    gs_worker_running = 0;
    return NULL;
}

static int gs_archive_cancel_before_start(const GsJob *job)
{
    if (job->archive != 1) return 0;
    if (!gs_storage_matches(job->storage_root, job->storage_token)) return -1;
    char directory[1100], path[1150], leaf[300]; struct stat st;
    if (job->local_only || job->generation[0]) {
        int n = snprintf(directory, sizeof(directory), "%s/extract-%s", job->storage_root, job->id);
        if (n < 0 || n >= (int)sizeof(directory)) return -1;
    } else {
        snprintf(leaf, sizeof(leaf), "/archives/%s", job->id);
        if (gs_ipc_path(directory, sizeof(directory), leaf)) return -1;
    }
    if (lstat(directory, &st)) return errno == ENOENT ? 0 : -1;
    if (!S_ISDIR(st.st_mode)) return -1;
    /* No worker has restored child task IDs yet. Even a completion receipt
     * must retain ownership until the installer can verify it. */
    for (int ordinal = 1; ordinal <= 256; ordinal++) {
        snprintf(path, sizeof(path), "%s/install-%03d.txt", directory, ordinal);
        if (!lstat(path, &st) || errno != ENOENT) return -1;
    }
    return 0;
}

#include "gs_archive_job.inc"
#include "gs_staged_transfer.inc"

static void gs_worker_poll(void)
{
    GsJob candidate;
    gs_control();
    if (gs_canceled && !gs_worker_running && gs_job.id[0]) {
        if ((!gs_worker_created && !gs_archive_journal[0] &&
                (gs_archive_cancel_before_start(&gs_job) || (gs_job.archive != 1 && gs_job.expected_kind &&
                    gs_retire_parked_package(gs_job.id, gs_job.content, gs_job.expected_kind)))) || gs_cancel_task()) {
            gs_release = 0; gs_write_status("failed", "BGFT cancellation unconfirmed; ownership retained");
        } else gs_write_status("canceled", NULL);
    }
    if (gs_worker_created && !gs_worker_running) { scePthreadJoin(gs_worker, NULL); gs_worker_created = 0; }
    if (gs_release && !gs_worker_running && g_client_count == 0 && gs_archive_task < 0) {
        int installed = !strcmp(gs_state, "installed");
        char job_path[128];
        if (!gs_read_job(&candidate) && !strcmp(candidate.id, gs_job.id) &&
            !gs_ipc_path(job_path, sizeof(job_path), "/job.txt")) unlink(job_path);
        memset(&gs_job, 0, sizeof(gs_job)); gs_stream_slot = -1; gs_validated = gs_served = gs_canceled = gs_paused = gs_release = 0;
        gs_done = 0; gs_current_task = -1; if (!installed) gs_write_status("idle", NULL);
        gs_archive_journal[0] = 0;
    }
    if (!gs_worker_running && !strcmp(gs_state, "complete")) gs_write_status("complete", NULL);
    if (gs_job.id[0] || gs_worker_running || gs_read_job(&candidate)) return;
    if (gs_stage_transfer_active() || g_client_count) return;
    gs_network_bytes = 0;gs_job = candidate; gs_stream_slot = -1; gs_transfer_verified = 0; gs_interval_count = 0; gs_done = 0; gs_validated = gs_served = gs_paused = gs_canceled = gs_release = 0;
    /* Apply retained intent against this generation before extraction can run.
     * A staged archive may already have a durable singleton install record. */
    for (int i = 0; i < GS_STAGE_COUNT; i++) {
        GsStage *stage = &gs_stages[i];
        if (strcmp(stage->job.id, candidate.id) || strcmp(stage->job.generation, candidate.generation)) continue;
        gs_stage_control(stage);
        gs_paused = stage->paused; gs_canceled = stage->canceled; gs_release = stage->release;
        break;
    }
    gs_control();
    if (gs_canceled) return;
    if (gs_paused) {
        gs_write_status("paused", NULL);
        /* Keep the on-disk candidate available for a later resume. */
        memset(&gs_job, 0, sizeof(gs_job)); return;
    }
    if (gs_stage_owned() && !gs_stage_can_install(&candidate)) {
        int active = 0; for (int i = 0; i < GS_STAGE_COUNT; i++) if (gs_stages[i].running) active++;
        if (candidate.local_only || candidate.archive != 1 || !candidate.generation[0] || active >= GS_STAGE_ACTIVE_JOBS) {
            memset(&gs_job, 0, sizeof(gs_job)); return;
        }
    }
    for (int i = 0; i < GS_STAGE_COUNT; i++) {
        GsStage *stage = &gs_stages[i];
        if (stage->transfer_verified && !strcmp(stage->job.id, candidate.id) &&
            !strcmp(stage->job.generation, candidate.generation) && !strcmp(stage->job.destination, candidate.destination)) {
            gs_transfer_verified = candidate.expected_kind != 0; gs_verified_size = candidate.total;
            memcpy(gs_verified_header, stage->header_identity, 32); break;
        }
    }
    gs_current_task = -1; gs_worker_running = 1; gs_write_status("ready", NULL);
    OrbisPthreadAttr attributes;
    int attr_ready = scePthreadAttrInit(&attributes) == 0;
    int thread_error = attr_ready ? scePthreadAttrSetstacksize(&attributes, 4 * 1024 * 1024) : -1;
    if (!thread_error) thread_error = scePthreadCreate(&gs_worker, &attributes, gs_download_worker, NULL, "gs-download");
    if (attr_ready) scePthreadAttrDestroy(&attributes);
    if (thread_error) {
        if (gs_stream_slot >= 0) __atomic_store_n(&gs_stages[gs_stream_slot].streaming, 0, __ATOMIC_RELEASE);
        gs_worker_running = 0; gs_write_status("failed", "Resident download thread creation failed");
    } else gs_worker_created = 1;
}
