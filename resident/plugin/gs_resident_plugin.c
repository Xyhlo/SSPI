#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <netinet/in.h>
#include <sys/socket.h>
#include <sys/stat.h>
#include <sys/file.h>
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

#include <orbis/Net.h>
#include <orbis/libkernel.h>
#include "../native/goldhen_process.h"
#include "../native/worker_presence.h"
#include "../native/filesystem_context.h"

#ifndef GS_APP_VERSION
#define GS_APP_VERSION "5.11"
#endif

#define GS_PLUGIN_VERSION 0x00000103u
#define GS_WORKER_VERSION GS_APP_VERSION "-api3"
#ifndef GS_BUILD_ID
#define GS_BUILD_ID "unversioned"
#endif
#define GS_PORT 8742
/* libkernel uses the PS4/FreeBSD socket ABI. The bundled musl MSG_NOSIGNAL
 * value is Linux-specific, so suppress SIGPIPE with the native socket option. */
#define GS_SO_NOSIGPIPE 0x0800
#define GS_HTTP_CLIENTS 8
/* BGFT reads the served package from the same HDD it writes it back to, so every
 * read/write head switch costs throughput. A console log measured ~28 MB/s for
 * a single sequential loopback reader with 256 KiB reads while the install
 * write was active. One bounded block per connection (8 MiB total for
 * GS_HTTP_CLIENTS) cuts the switch rate; this is a bounded-change hypothesis,
 * not a measured speedup. */
#define GS_LOOPBACK_BLOCK (1024 * 1024)
/* A peer that stops reading must not pin one of the connection slots forever.
 * The budget is deliberately unchanged from the previous 90 s: BGFT may pause a
 * response body while it prepares or verifies on the same disk, and only a
 * hardware capture could prove a shorter bound safe. Any successful partial send
 * resets the deadline, and g_stop still cancels within one 10 ms wakeup. */
#define GS_LOOPBACK_STALL_US 90000000ULL
#define GS_IPC_ROOT "/data/SSPI/resident"
#define GS_IPC_ROOT_SHARED "/user/data/SSPI/resident"
#define GS_SHARED_BOOT_PATH "/user/data/SSPI/resident/plugin-boot.txt"
#define GS_SHARED_HEARTBEAT_PATH "/user/data/SSPI/resident/heartbeat.txt"
#define DOTNET_EPOCH_TICKS 621355968000000000LL

/*
 * DateTime.UtcNow.Ticks = 621355968000000000 + UnixSeconds * 10000000.
 * The B0 hardware gate compares this value to DateTime.UtcNow within 2 s.
 */

__attribute__((visibility("default"))) const char *g_pluginName = "GameSearchResident";
__attribute__((visibility("default"))) const char *g_pluginDesc =
    "Game Search shell background download worker";
__attribute__((visibility("default"))) const char *g_pluginAuth = "Game Search";
__attribute__((visibility("default"))) uint32_t g_pluginVersion = GS_PLUGIN_VERSION;

static volatile int g_stop;
static volatile int g_started;
static volatile int g_listener = -1;
static OrbisPthread g_main_thread;
static int g_main_thread_created;
static volatile int g_client_count;
typedef struct {
    OrbisPthread thread;
    int socket_id, created, finished;
} GsHttpClient;
static GsHttpClient g_clients[GS_HTTP_CLIENTS];
static uint64_t g_epoch;
static int g_transfer_ready, g_bgft_ready;
static const char *g_ipc_root = GS_IPC_ROOT;
static int g_shared_storage_result;
static int g_capability_attempts;
static int g_capability_network_rc;
static int g_capability_transfer_rc;
static int g_capability_bgft_rc;
static int g_ready_boot_written;
static GsFilesystemContext g_filesystem_context = { .fd = -1 };

static int gs_ipc_path(char *out, size_t cap, const char *leaf)
{
    int length;
    if (!out || !cap || !leaf || leaf[0] != '/') return -1;
    length = snprintf(out, cap, "%s%s", g_ipc_root, leaf);
    if (length < 0 || (size_t)length >= cap) {
        if (cap) out[0] = 0;
        return -1;
    }
    return 0;
}

static int gs_storage_probe(const char *root, int canonical)
{
    int result;
    int directory = sceKernelOpen(root, O_RDONLY | O_DIRECTORY | O_NOFOLLOW, 0);
    if (directory < 0) return directory;
    sceKernelClose(directory);
    result = gs_data_prepare_root();
    if (result && canonical) return result;
    result = gs_data_ensure_directory(root, 0777);
    if (result) return result;
    char path[192], actual[96], expected[96], leaf[64];
    int length = snprintf(leaf, sizeof(leaf), "/.storage-check-%d-%llu", getpid(), (unsigned long long)g_epoch);
    if (length < 0 || length >= (int)sizeof(leaf) || gs_ipc_path(path, sizeof(path), leaf)) return -5;
    length = snprintf(expected, sizeof(expected), "SSPI storage %s %llu\n", GS_BUILD_ID, (unsigned long long)g_epoch);
    if (length < 0 || length >= (int)sizeof(expected)) return -5;
    int file = sceKernelOpen(path, O_RDWR | O_CREAT | O_EXCL | O_NOFOLLOW, 0600);
    if (file < 0) return file;
    int64_t count = sceKernelWrite(file, expected, (size_t)length);
    if (count != length) result = count < 0 ? (int)count : -5;
    if (!result && sceKernelLseek(file, 0, SEEK_SET) != 0) result = -5;
    if (!result) {
        count = sceKernelRead(file, actual, (size_t)length);
        if (count != length || memcmp(actual, expected, (size_t)length)) result = count < 0 ? (int)count : -5;
    }
    int close_result = sceKernelClose(file);
    int unlink_result = sceKernelUnlink(path);
    if (!result) result = close_result ? close_result : unlink_result;
    return result;
}

static int ensure_directories(void)
{
    // The application stages the canonical directory. SceShellUI started
    // before GoldHEN patched ShellCore and sees the same storage at /user/data.
    int result = gs_storage_probe(GS_IPC_ROOT, 1);
    if (result) {
        int canonical = result;
        (void)sceKernelMkdir("/user/data", 0777);
        (void)sceKernelMkdir("/user/data/SSPI", 0777);
        (void)sceKernelMkdir(GS_IPC_ROOT_SHARED, 0777);
        g_ipc_root = GS_IPC_ROOT_SHARED;
        result = gs_storage_probe(GS_IPC_ROOT_SHARED, 0);
        g_shared_storage_result = result;
        if (result) {
            g_ipc_root = GS_IPC_ROOT;
            return canonical;
        }
        gs_log_write("resident", "storage-root=shared");
    }
    return gs_log_prepare_files();
}

static int write_atomic_mode(const char *path, const char *body, int durable)
{
    char temporary[1300];
    FILE *file;
    int result;

    if (!path || !body || snprintf(temporary, sizeof(temporary), "%s.tmp", path) >=
        (int)sizeof(temporary))
        return -1;
    file = fopen(temporary, "wb");
    if (!file) return -1;
    if (fwrite(body, 1, strlen(body), file) != strlen(body))
    {
        fclose(file);
        unlink(temporary);
        return -1;
    }
    result = fflush(file);
    if (result == 0 && durable) result = fsync(fileno(file));
    if (fclose(file) != 0) result = -1;
    if (result != 0)
    {
        unlink(temporary);
        return -1;
    }
    if (rename(temporary, path) != 0)
    {
        unlink(temporary);
        return -1;
    }
    return 0;
}

static int write_atomic(const char *path, const char *body)
{ return write_atomic_mode(path, body, 1); }

/* Telemetry is replaceable; it must not flush the HDD's download writes. */
static int write_telemetry(const char *path, const char *body)
{ return write_atomic_mode(path, body, 0); }

static void write_heartbeat(void)
{
    static time_t last;
    time_t now = time(NULL);
    if (now == last) return;
    last = now;
    char body[512], path[128], capability[192];
    int64_t dotnet_ticks_now = DOTNET_EPOCH_TICKS +
        (int64_t)time(NULL) * 10000000LL;
    // The listener proves the worker thread is alive even while its optional
    // network/BGFT capabilities are still retrying. Keep the last return codes
    // visible so a console log can explain which capability is not ready.
    capability[0] = 0;
    if (!g_transfer_ready || !g_bgft_ready)
        snprintf(capability, sizeof(capability),
            "capattempts=%d capnet=0x%08X captransfer=0x%08X capbgft=0x%08X ",
            g_capability_attempts, (unsigned)g_capability_network_rc,
            (unsigned)g_capability_transfer_rc, (unsigned)g_capability_bgft_rc);
    snprintf(body, sizeof(body), "%s\n%lld\nhost=shell pid=%d listener=%d download=%d transfer=%d bgft=%d %sv=2 api=3 engine=sceHttp-chunks build=%s epoch=%llu staged=10 usb=1 archives=1 zip=1 sevenzip=1 fscontext=1 hostcontext=preserved usbcontext=0 revision=1 migration=%d\n",
        GS_WORKER_VERSION, (long long)dotnet_ticks_now,
        getpid(), g_listener >= 0 ? 1 : 0, g_transfer_ready, g_transfer_ready, g_bgft_ready,
        capability, GS_BUILD_ID, (unsigned long long)g_epoch,
        access("/data/SSPI/.migration-active", F_OK) == 0);
    if (gs_ipc_path(path, sizeof(path), "/heartbeat.txt")) return;
    write_telemetry(path, body);
}

static void write_boot(const char *state, int code)
{
    gs_log_write("resident", "boot state=%s code=%d build=%s epoch=%llu", state ? state : "unknown", code, GS_BUILD_ID, (unsigned long long)g_epoch);
    char body[192], path[128];
    int64_t ticks = DOTNET_EPOCH_TICKS + (int64_t)time(NULL) * 10000000LL;
    snprintf(body, sizeof(body), "%s\n%lld\n%s code=%d pid=%d\n",
        GS_WORKER_VERSION, (long long)ticks, state ? state : "unknown", code, getpid());
    if (gs_ipc_path(path, sizeof(path), "/plugin-boot.txt")) return;
    write_atomic(path, body);
}

static int wait_for_storage(void)
{
    while (!g_stop) {
        int result = ensure_directories();
        if (!result) return 0;
        char body[320], message[160];
        int64_t ticks = DOTNET_EPOCH_TICKS + (int64_t)time(NULL) * 10000000LL;
        snprintf(body, sizeof(body), "%s\n%lld\nstorage-unavailable code=%d shared=0x%08x pid=%d build=%s epoch=%llu canonical=/data/SSPI/resident shared_root=/user/data/SSPI/resident\n",
            GS_WORKER_VERSION, (long long)ticks, result, (unsigned)g_shared_storage_result, getpid(), GS_BUILD_ID, (unsigned long long)g_epoch);
        // This diagnostic runs before either root is usable and always uses the
        // shared staging path so the application can read it under its /data view.
        write_atomic(GS_SHARED_BOOT_PATH, body);
        snprintf(message, sizeof(message), "SSPI resident storage-unavailable: 0x%08x\n", (unsigned)result);
        sceKernelDebugOutText(0, message);
        for (int i = 0; i < 100 && !g_stop; i++) {
            if (i % 10 == 0) {
                ticks = DOTNET_EPOCH_TICKS + (int64_t)time(NULL) * 10000000LL;
                snprintf(body, sizeof(body), "%s\n%lld\nhost=shell pid=%d listener=0 download=0 transfer=0 bgft=0 v=2 api=3 staged=10 storage=0 build=%s epoch=%llu \n",
                    GS_WORKER_VERSION, (long long)ticks, getpid(), GS_BUILD_ID, (unsigned long long)g_epoch);
                write_telemetry(GS_SHARED_HEARTBEAT_PATH, body);
            }
            sceKernelUsleep(100000);
        }
    }
    return -1;
}

static int send_all(int socket_id, const void *data, size_t size)
{
    const unsigned char *at = (const unsigned char *)data;
    uint64_t stalled_at = sceKernelGetProcessTime();
    while (size > 0)
    {
        if (g_stop) return -1;
        ssize_t sent = send(socket_id, at, size, 0);
        if (sent < 0 && errno == EINTR) continue;
        if (sent < 0 && (errno == EAGAIN || errno == EWOULDBLOCK) && !g_stop &&
            sceKernelGetProcessTime() - stalled_at < GS_LOOPBACK_STALL_US) {
            sceKernelUsleep(10000); continue;
        }
        if (sent <= 0) {
            gs_log_write("resident", "bgft-http-send fd=%d errno=%d remaining=%llu", socket_id, errno, (unsigned long long)size);
            return -1;
        }
        at += sent;
        size -= (size_t)sent;
        stalled_at = sceKernelGetProcessTime();
    }
    return 0;
}

static int send_head(int socket_id, int status, const char *reason, const char *extra)
{
    char headers[1024];
    int length = snprintf(headers, sizeof(headers),
        "HTTP/1.1 %d %s\r\n%sConnection: close\r\n\r\n",
        status, reason, extra ? extra : "");
    if (length > 0 && length < (int)sizeof(headers))
        return send_all(socket_id, headers, (size_t)length);
    return -1;
}

static void send_empty(int socket_id, int status, const char *reason, const char *extra)
{
    char headers[512];
    snprintf(headers, sizeof(headers), "%sContent-Length: 0\r\n", extra ? extra : "");
    send_head(socket_id, status, reason, headers);
}

static int read_headers(int socket_id, char *buffer, size_t capacity)
{
    size_t used = 0;
    while (!g_stop && used + 1 < capacity)
    {
        ssize_t count = recv(socket_id, buffer + used, capacity - used - 1, 0);
        if (count < 0 && errno == EINTR) continue;
        if (count <= 0) return -1;
        used += (size_t)count;
        buffer[used] = 0;
        if (strstr(buffer, "\r\n\r\n")) return (int)used;
    }
    return -1;
}

static const char *header_value(char *request, const char *name)
{
    size_t name_length = strlen(name);
    char *line = strstr(request, "\r\n");
    while (line)
    {
        char *next;
        line += 2;
        if (*line == '\r' || *line == 0) return NULL;
        next = strstr(line, "\r\n");
        if (!next) return NULL;
        if ((size_t)(next - line) > name_length &&
            strncasecmp(line, name, name_length) == 0 && line[name_length] == ':')
        {
            char *value = line + name_length + 1;
            while (value < next && (*value == ' ' || *value == '\t')) value++;
            *next = 0;
            return value;
        }
        line = next;
    }
    return NULL;
}

static int parse_number(const char *text, int64_t *value, const char **end)
{
    char *parsed_end;
    long long parsed;
    if (!text || !*text) return -1;
    errno = 0;
    parsed = strtoll(text, &parsed_end, 10);
    if (errno != 0 || parsed_end == text || parsed < 0) return -1;
    *value = (int64_t)parsed;
    if (end) *end = parsed_end;
    return 0;
}

static int parse_range(const char *header, int64_t length, int64_t *start, int64_t *end)
{
    const char *dash;
    const char *tail;
    int64_t first;
    int64_t last;

    if (!header || length <= 0 || strncasecmp(header, "bytes=", 6) != 0 ||
        strchr(header + 6, ','))
        return -1;
    header += 6;
    dash = strchr(header, '-');
    if (!dash) return -1;
    if (dash == header)
    {
        if (parse_number(dash + 1, &last, &tail) != 0 || *tail != 0 || last <= 0)
            return -1;
        if (last > length) last = length;
        *start = length - last;
        *end = length - 1;
        return 0;
    }
    if (parse_number(header, &first, &tail) != 0 || tail != dash || first >= length)
        return -1;
    if (dash[1] == 0)
        last = length - 1;
    else if (parse_number(dash + 1, &last, &tail) != 0 || *tail != 0 || last < first)
        return -1;
    if (last >= length) last = length - 1;
    *start = first;
    *end = last;
    return 0;
}

static char *request_path(char *target)
{
    char *scheme = strstr(target, "://");
    char *query;
    if (scheme)
    {
        target = strchr(scheme + 3, '/');
        if (!target) return "/";
    }
    query = strchr(target, '?');
    if (query) *query = 0;
    return target;
}

static int has_pkg_magic(FILE *file)
{
    unsigned char magic[4];
    if (fseek(file, 0, SEEK_SET) != 0 || fread(magic, 1, sizeof(magic), file) != sizeof(magic))
        return 0;
    return magic[0] == 0x7f && magic[1] == 0x43 && magic[2] == 0x4e && magic[3] == 0x54;
}

static int serve_reference_json(int socket_id, const char *method, const char *source,
    const char *piece_route, int64_t length, const char *remote_url)
{
    unsigned char digest[32]; char hex[65], headers[192], local_url[512];
    static const char digits[] = "0123456789abcdef";
    FILE *file = fopen(source, "rb");
    if (!file) return -1;
    int valid = length >= 0x1000 && has_pkg_magic(file) && !fseek(file, 0xfe0, SEEK_SET) &&
        fread(digest, 1, sizeof(digest), file) == sizeof(digest);
    fclose(file); if (!valid) return -1;
    for (int i = 0; i < 32; i++) { hex[i * 2] = digits[digest[i] >> 4]; hex[i * 2 + 1] = digits[digest[i] & 15]; }
    hex[64] = 0;
    snprintf(local_url, sizeof(local_url), "http://127.0.0.1:8742%s", piece_route);
    const char *url = remote_url ? remote_url : local_url;
    size_t url_length = strlen(url), used = 0;
    if (url_length >= 8192) return -1;
    char *escaped = (char *)malloc(url_length * 2 + 1);
    char *document = (char *)malloc(url_length * 2 + 512);
    if (!escaped || !document) { free(escaped); free(document); return -1; }
    for (size_t i = 0; i < url_length; i++) {
        unsigned char c = (unsigned char)url[i];
        if (c < 32 || c == 127) { free(escaped); free(document); return -1; }
        if (c == '\\' || c == '"') escaped[used++] = '\\';
        escaped[used++] = (char)c;
    }
    escaped[used] = 0;
    int size = snprintf(document, url_length * 2 + 512,
        "{\"originalFileSize\":%lld,\"packageDigest\":\"%s\",\"numberOfSplitFiles\":1,\"pieces\":[{\"url\":\"%s\",\"fileOffset\":0,\"fileSize\":%lld,\"hashValue\":\"0000000000000000000000000000000000000000\"}]}",
        (long long)length, hex, escaped, (long long)length);
    free(escaped);
    if (size < 0 || size >= (int)(url_length * 2 + 512)) { free(document); return -1; }
    snprintf(headers, sizeof(headers), "Content-Type: application/json\r\nContent-Length: %d\r\nCache-Control: no-store\r\n", size);
    int rc = send_head(socket_id, 200, "OK", headers);
    if (!rc && strcmp(method, "HEAD")) rc = send_all(socket_id, document, (size_t)size);
    free(document); return rc;
}

#include "gs_resident_worker.inc"

static void serve_client(int socket_id)
{
    char request[16384];
    char method[8];
    char target[1024];
    char version[16];
    char representation[768];
    char source[1024];
    char route[256];
    char manifest_route[272];
    int manifest = 0;
    int job_route = 0;
    const char *range_header;
    char *path;
    struct stat info;
    FILE *file;
    unsigned char *transfer_buffer;
    int partial = 0;
    int64_t start = 0;
    int64_t end;
    int64_t remaining;

    if (read_headers(socket_id, request, sizeof(request)) < 0 ||
        sscanf(request, "%7s %1023s %15s", method, target, version) != 3)
    {
        send_empty(socket_id, 400, "Bad Request", NULL);
        return;
    }
    if (strcmp(method, "GET") != 0 && strcmp(method, "HEAD") != 0)
    {
        send_empty(socket_id, 405, "Method Not Allowed", "Allow: GET, HEAD\r\n");
        return;
    }
    path = request_path(target);
    if (!strcmp(path, "/sspi-icon.png")) {
        FILE *icon = fopen("/user/appmeta/SRCH00001/icon0.png", "rb");
        struct stat icon_info;
        if (!icon || gs_file_stat(icon, &icon_info) || icon_info.st_size <= 0 || icon_info.st_size > 512 * 1024) {
            if (icon) fclose(icon);
            send_empty(socket_id, 404, "Not Found", NULL); return;
        }
        char icon_headers[128];
        snprintf(icon_headers, sizeof(icon_headers), "Content-Type: image/png\r\nContent-Length: %lld\r\n", (long long)icon_info.st_size);
        if (!send_head(socket_id, 200, "OK", icon_headers) && !strcmp(method, "GET")) {
            unsigned char icon_buffer[16384];
            int64_t offset = 0;
            while (offset < icon_info.st_size) {
                size_t wanted = (size_t)(icon_info.st_size - offset);
                if (wanted > sizeof(icon_buffer)) wanted = sizeof(icon_buffer);
                int got = sceKernelPread(fileno(icon), icon_buffer, wanted, offset);
                if (got <= 0 || send_all(socket_id, icon_buffer, (size_t)got)) break;
                offset += got;
            }
        }
        fclose(icon); return;
    }
    if (gs_ipc_path(source, sizeof(source), "/plugin-selftest.pkg")) source[0] = 0;
    gs_package_url(route, sizeof(route), "");
    snprintf(manifest_route, sizeof(manifest_route), "%s.json", route);
    manifest = strcmp(path, manifest_route) == 0;
    if (strcmp(path, "/pkg/SELFTEST.pkg") != 0)
    {
        if (!gs_job.id[0] || (strcmp(path, route) != 0 && !manifest)) {
            send_empty(socket_id, 404, "Not Found", NULL); return;
        }
        if (!gs_validated || gs_canceled || (gs_paused && gs_stream_slot < 0)) {
            send_empty(socket_id, 503, "Service Unavailable", "Retry-After: 1\r\n"); return;
        }
        if (!gs_storage_matches(gs_job.storage_root, gs_job.storage_token)) {
            send_empty(socket_id, 503, "Service Unavailable", "Retry-After: 3\r\n"); return;
        }
        snprintf(source, sizeof(source), "%s", gs_job.destination);
        job_route = 1;
    }
    file = fopen(source, "rb");
    if (!file || gs_file_stat(file, &info) || info.st_size < 4)
    {
        if (file) fclose(file);
        send_empty(socket_id, 404, "Not Found", NULL);
        return;
    }
    // Sparse staging length is not the representation length. Missing ranges
    // are held below until the resident has written and committed their chunks.
    if (job_route && gs_stream_slot >= 0) {
        info.st_size = gs_job.total;
        // A stdio read-ahead must not cache unwritten sparse bytes beyond a
        // committed range. The explicit bounded buffer already batches I/O.
        setvbuf(file, NULL, _IONBF, 0);
    }
    if (manifest) {
        fclose(file);
        if (job_route && gs_job.native_bgft) info.st_size = gs_job.total;
        int rc = serve_reference_json(socket_id, method, source, route, info.st_size,
            job_route && gs_job.native_bgft ? gs_job.url : NULL);
        gs_log_write("resident", "bgft-http-manifest job=%s method=%s rc=%d bytes=%lld", gs_job.id, method, rc, (long long)info.st_size);
        return;
    }
    if (job_route && gs_job.native_bgft) {
        fclose(file); send_empty(socket_id, 404, "Not Found", NULL); return;
    }
    if (!file)
    {
        send_empty(socket_id, 503, "Service Unavailable", "Retry-After: 1\r\n");
        return;
    }
    if (!has_pkg_magic(file))
    {
        fclose(file);
        send_empty(socket_id, 503, "Service Unavailable", "Retry-After: 1\r\n");
        return;
    }

    end = (int64_t)info.st_size - 1;
    range_header = header_value(request, "Range");
    if (range_header)
    {
        partial = 1;
        if (parse_range(range_header, (int64_t)info.st_size, &start, &end) != 0)
        {
            fclose(file);
            snprintf(representation, sizeof(representation),
                "Content-Range: bytes */%lld\r\nAccept-Ranges: bytes\r\n",
                (long long)info.st_size);
            send_empty(socket_id, 416, "Range Not Satisfiable", representation);
            return;
        }
    }

    remaining = end - start + 1;
    if (job_route) gs_log_write("resident", "bgft-http job=%s method=%s start=%lld end=%lld bytes=%lld stream=%d",
        gs_job.id, method, (long long)start, (long long)end, (long long)info.st_size, gs_stream_slot >= 0);
    snprintf(representation, sizeof(representation),
        "Content-Type: application/octet-stream\r\n"
        "Content-Length: %lld\r\n"
        "Accept-Ranges: bytes\r\n%s"
        "Cache-Control: no-transform\r\n",
        (long long)remaining,
        partial ? "Content-Range: placeholder\r\n" : "");
    if (partial)
    {
        snprintf(representation, sizeof(representation),
            "Content-Type: application/octet-stream\r\n"
            "Content-Length: %lld\r\n"
            "Accept-Ranges: bytes\r\n"
            "Content-Range: bytes %lld-%lld/%lld\r\n"
            "Cache-Control: no-transform\r\n",
            (long long)remaining, (long long)start, (long long)end,
            (long long)info.st_size);
    }
    if (send_head(socket_id, partial ? 206 : 200,
        partial ? "Partial Content" : "OK", representation)) { fclose(file); return; }
    if (strcmp(method, "HEAD") == 0)
    {
        fclose(file);
        return;
    }
    transfer_buffer = (unsigned char *)malloc(GS_LOOPBACK_BLOCK);
    if (!transfer_buffer) { fclose(file); return; }
    uint64_t body_started = sceKernelGetProcessTime(), body_logged_at = body_started;
    uint64_t read_us = 0, send_us = 0, wait_us = 0, gate_us = 0;
    while (remaining > 0 && !g_stop && (!job_route || (gs_validated && !gs_canceled && (!gs_paused || gs_stream_slot >= 0))))
    {
        uint64_t now = sceKernelGetProcessTime();
        if (job_route && now - body_logged_at >= 5000000) {
            body_logged_at = now;
            gs_log_write("resident", "bgft-http-body job=%s fd=%d start=%lld sent=%lld remaining=%lld block=%u elapsed_ms=%llu read_ms=%llu send_ms=%llu wait_ms=%llu gate_ms=%llu",
                gs_job.id, socket_id, (long long)start, (long long)(end - start + 1 - remaining), (long long)remaining,
                (unsigned)GS_LOOPBACK_BLOCK,
                (unsigned long long)((now - body_started) / 1000), (unsigned long long)(read_us / 1000),
                (unsigned long long)(send_us / 1000), (unsigned long long)(wait_us / 1000), (unsigned long long)(gate_us / 1000));
        }
        size_t wanted = remaining < GS_LOOPBACK_BLOCK ? (size_t)remaining : GS_LOOPBACK_BLOCK;
        if (job_route && gs_stream_slot >= 0) {
            uint64_t before = sceKernelGetProcessTime();
            int64_t available = gs_stage_stream_readable(end + 1 - remaining);
            gate_us += sceKernelGetProcessTime() - before;
            if (available < 0) break;
            if (!available || gs_paused) {
                before = sceKernelGetProcessTime(); sceKernelUsleep(20000);
                wait_us += sceKernelGetProcessTime() - before; continue;
            }
            if ((int64_t)wanted > available) wanted = (size_t)available;
        }
        uint64_t before = sceKernelGetProcessTime();
        ssize_t count = sceKernelPread(fileno(file), transfer_buffer, wanted, (off_t)(end + 1 - remaining));
        int read_error = errno;
        read_us += sceKernelGetProcessTime() - before;
        if (count < 0 && read_error == EINTR) continue;
        if (count <= 0 || (size_t)count > wanted) {
            gs_log_write("resident", "bgft-http-read job=%s offset=%lld wanted=%llu result=%lld errno=%d",
                gs_job.id, (long long)(end + 1 - remaining), (unsigned long long)wanted, (long long)count, read_error);
            break;
        }
        before = sceKernelGetProcessTime();
        int send_result = send_all(socket_id, transfer_buffer, (size_t)count);
        send_us += sceKernelGetProcessTime() - before;
        if (send_result != 0) break;
        remaining -= (int64_t)count;
    }
    free(transfer_buffer);
    if (job_route) gs_log_write("resident", "bgft-http-end job=%s fd=%d start=%lld sent=%lld remaining=%lld block=%u canceled=%d paused=%d elapsed_ms=%llu read_ms=%llu send_ms=%llu wait_ms=%llu gate_ms=%llu",
        gs_job.id, socket_id, (long long)start, (long long)(end - start + 1 - remaining), (long long)remaining, (unsigned)GS_LOOPBACK_BLOCK, gs_canceled, gs_paused,
        (unsigned long long)((sceKernelGetProcessTime() - body_started) / 1000), (unsigned long long)(read_us / 1000),
        (unsigned long long)(send_us / 1000), (unsigned long long)(wait_us / 1000), (unsigned long long)(gate_us / 1000));
    if (job_route) gs_record_served(start, end + 1 - remaining);
    fclose(file);
}

static void *client_thread(void *argument)
{
    GsHttpClient *client = (GsHttpClient *)argument;
    int socket_id = client->socket_id;
    int no_sigpipe = 1;
    if (setsockopt(socket_id, SOL_SOCKET, GS_SO_NOSIGPIPE, &no_sigpipe, sizeof(no_sigpipe))) {
        gs_log_write("resident", "bgft-http-nosigpipe fd=%d errno=%d", socket_id, errno);
        goto done;
    }
    // BSD accept can inherit the listener's O_NONBLOCK. A temporary empty
    // receive/send queue must not truncate a BGFT manifest or package body.
    int flags = fcntl(socket_id, F_GETFL, 0);
    if (flags < 0 || fcntl(socket_id, F_SETFL, flags & ~O_NONBLOCK) != 0) {
        gs_log_write("resident", "bgft-http-config fd=%d errno=%d", socket_id, errno);
        goto done;
    }
    struct timeval timeout = { 5, 0 };
    if (setsockopt(socket_id, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout))) {
        gs_log_write("resident", "bgft-http-recv-timeout fd=%d errno=%d", socket_id, errno);
        goto done;
    }
    timeout.tv_sec = 30;
    if (setsockopt(socket_id, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout))) {
        gs_log_write("resident", "bgft-http-send-timeout fd=%d errno=%d", socket_id, errno);
        goto done;
    }
    serve_client(socket_id);
    // Advertised Connection: close is deliberate. Flush the response through
    // FIN, then drain the peer briefly so unread input does not cause a reset.
    shutdown(socket_id, SHUT_WR);
    struct timeval drain = { 1, 0 };
    setsockopt(socket_id, SOL_SOCKET, SO_RCVTIMEO, &drain, sizeof(drain));
    unsigned char tail[512]; size_t drained = 0;
    uint64_t drain_started = sceKernelGetProcessTime();
    while (!g_stop && drained < 16384 && sceKernelGetProcessTime() - drain_started < 2000000) {
        ssize_t n = recv(socket_id, tail, sizeof(tail), 0);
        if (n <= 0) break;
        drained += (size_t)n;
    }
done:
    close(socket_id);
    __sync_sub_and_fetch(&g_client_count, 1);
    __atomic_store_n(&client->finished, 1, __ATOMIC_RELEASE);
    return NULL;
}

static void gs_reap_clients(int all)
{
    for (int i = 0; i < GS_HTTP_CLIENTS; i++) {
        GsHttpClient *client = &g_clients[i];
        if (!client->created || (!all && !__atomic_load_n(&client->finished, __ATOMIC_ACQUIRE))) continue;
        /* The finished flag is published before the thread's final return.
         * Joining also proves that no thread can return through unloaded code. */
        scePthreadJoin(client->thread, NULL);
        client->created = 0;
    }
}

static void gs_start_client(int socket_id)
{
    gs_reap_clients(0);
    for (int i = 0; i < GS_HTTP_CLIENTS; i++) {
        GsHttpClient *client = &g_clients[i];
        if (client->created) continue;
        client->socket_id = socket_id;
        __atomic_store_n(&client->finished, 0, __ATOMIC_RELAXED);
        __sync_add_and_fetch(&g_client_count, 1);
        if (!scePthreadCreate(&client->thread, NULL, client_thread, client, "gs-http")) {
            client->created = 1;
            return;
        }
        __sync_sub_and_fetch(&g_client_count, 1);
        break;
    }
    close(socket_id);
}

static int start_listener(void)
{
    int listener;
    int flags;
    struct sockaddr_in address;

    listener = socket(AF_INET, SOCK_STREAM, 0);
    if (listener < 0) return -1;
    int reuse = 1;
    setsockopt(listener, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));
    memset(&address, 0, sizeof(address));
    address.sin_len = sizeof(address);
    address.sin_family = AF_INET;
    // PS4 is little-endian; these fixed network-order values need no SPRX.
    address.sin_port = __builtin_bswap16((uint16_t)GS_PORT);
    address.sin_addr.s_addr = __builtin_bswap32(0x7f000001u);
    if (bind(listener, (struct sockaddr *)&address, sizeof(address)) != 0 ||
        listen(listener, 8) != 0)
    {
        close(listener);
        return -1;
    }
    flags = fcntl(listener, F_GETFL, 0);
    if (flags < 0 || fcntl(listener, F_SETFL, flags | O_NONBLOCK) != 0)
    {
        close(listener);
        return -1;
    }
    return listener;
}

/* Every worker announces its build once the storage root is final. A later
 * build's record lets an older, already-mapped worker retire and free port
 * 8742. The window is deliberately short: peers poll every loop second. */
static void write_retire_record(void)
{
    char body[160], path[128];
    int64_t ticks = DOTNET_EPOCH_TICKS + (int64_t)time(NULL) * 10000000LL;
    int length = snprintf(body, sizeof(body), "format=1\nbuild=%s\npid=%d\nticks=%lld\n",
        GS_BUILD_ID, getpid(), (long long)ticks);
    if (length < 0 || length >= (int)sizeof(body)) return;
    if (gs_ipc_path(path, sizeof(path), "/worker-retire.txt")) return;
    write_atomic(path, body);
}

static int gs_worker_retire_requested(void)
{
    char path[128], body[256], build[64] = {0};
    int64_t ticks = 0;
    int have_format = 0, have_ticks = 0;
    if (gs_ipc_path(path, sizeof(path), "/worker-retire.txt")) return 0;
    FILE *file = fopen(path, "rb");
    if (!file) return 0;
    size_t count = fread(body, 1, sizeof(body) - 1, file);
    fclose(file);
    body[count] = 0;
    char *line = body;
    while (line && *line) {
        char *next = strchr(line, '\n');
        if (next) *next++ = 0;
        if (!strcmp(line, "format=1")) have_format = 1;
        else if (!strncmp(line, "build=", 6)) snprintf(build, sizeof(build), "%s", line + 6);
        else if (!strncmp(line, "ticks=", 6)) { ticks = strtoll(line + 6, NULL, 10); have_ticks = 1; }
        line = next;
    }
    if (!have_format || !build[0] || !have_ticks) return 0;
    int64_t now = DOTNET_EPOCH_TICKS + (int64_t)time(NULL) * 10000000LL;
    if (ticks <= 0 || ticks > now + 20000000LL || now - ticks > 300000000LL) return 0;
    if (!strcmp(build, GS_BUILD_ID)) return 0;
    gs_log_write("resident", "retire-request build=%s self=%s", build, GS_BUILD_ID);
    return 1;
}

static void *gs_main_loop(void *argument)
{
    int listener;
    (void)argument;

    g_epoch = sceKernelGetProcessTime();
    // Never invoke GoldHEN recursively from the module-start callback that
    // the named-process loader is still waiting to finish.
    if (!gs_goldhen_is_shell()) {
        write_boot("shell-host-rejected", -1);
        g_started = 0; return NULL;
    }
    if (wait_for_storage()) { g_started = 0; return NULL; }
    write_boot("plugin-entry", 0);
    write_boot("shell-host-accepted-starting-thread", 0);
    if (gs_legacy_worker_advancing()) {
        write_boot("legacy-worker-active-restart-required", -8);
        g_started = 0; return NULL;
    }
    int context_result = gs_filesystem_context_lock(&g_filesystem_context);
    if (context_result) {
        write_boot("filesystem-context-busy-restart-required", context_result);
        g_started = 0; return NULL;
    }
    GsFilesystemProof proof;
    int context_verified = gs_filesystem_context_unique_module((const void *)gs_main_loop, &proof);
    gs_storage_context_unverified = 1;
    if (!context_verified) {
        gs_log_write("resident", "filesystem-context resident ownership unverified modules=%llu list=0x%08X info=0x%08X self=%d module=%s; original shell context preserved",
            (unsigned long long)proof.count, (unsigned)proof.list_result, (unsigned)proof.info_result, proof.self, proof.module);
        int release_result = gs_filesystem_context_unlock(&g_filesystem_context);
        write_boot(release_result ? "filesystem-context-release-failed-restart-required" :
            "filesystem-context-unverified-restart-required", -1);
        g_started = 0; return NULL;
    }
    /* A pre-lease worker still using the port cannot overlap this owner. */
    listener = start_listener();
    if (listener < 0) {
        write_boot("filesystem-context-listener-busy-restart-required", -1);
        gs_filesystem_context_unlock(&g_filesystem_context);
        g_started = 0; return NULL;
    }
    gs_log_write("resident", "filesystem-context original shell context preserved lease=held modules=%llu self=%d module=%s",
        (unsigned long long)proof.count, proof.self, proof.module);
    // libSceNet/sceNetInit remains a best-effort prerequisite, but it must not
    // gate liveness: sockets are imported from libkernel, so bind the listener
    // first and let the loop below keep resolving optional capabilities.
    (void)gs_net_runtime_init();
    write_retire_record();
    g_listener = listener;
    write_boot(listener >= 0 ? "listening" : "listener-retry", listener >= 0 ? 0 : -1);

    while (!g_stop)
    {
        gs_reap_clients(0);
        /* A newer build's fresh retire record supersedes this worker. Exit the
         * loop so the normal shutdown path closes the listener and joins the
         * worker thread, freeing port 8742 for the replacement. */
        if (gs_worker_retire_requested()) { g_stop = 1; break; }
        static uint64_t listener_retry;
        if (listener < 0 && sceKernelGetProcessTime() >= listener_retry) {
            listener_retry = sceKernelGetProcessTime() + 10000000;
            (void)gs_net_runtime_init();
            listener = start_listener(); g_listener = listener;
            if (listener >= 0) write_boot("listener-recovered", 0);
        }
        if (listener >= 0)
        {
            static uint64_t readiness_retry;
            uint64_t now = sceKernelGetProcessTime();
            // Publish liveness before a capability attempt can block on module
            // resolution; the loader treats an advancing heartbeat as presence.
            write_heartbeat();
            if ((!g_transfer_ready || !g_bgft_ready) && now >= readiness_retry) {
                readiness_retry = now + 10000000;
                if (!g_transfer_ready) {
                    int network_rc = gs_network_init();
                    // SceShellUI hides loaded system libraries from the module
                    // list; hand the engine the handle we already resolved.
                    if (!network_rc && gs_http_module >= 0) sspi_xfer_set_module(gs_http_module);
                    int transfer_rc = network_rc ? network_rc : sspi_xfer_init(gs_http);
                    if (!transfer_rc) g_transfer_ready = 1;
                    g_capability_network_rc = network_rc;
                    g_capability_transfer_rc = transfer_rc;
                    g_capability_attempts++;
                    gs_log_write("resident", "capability network=%d transfer=%d ready=%d", network_rc, transfer_rc, g_transfer_ready);
                }
                if (!g_bgft_ready) {
                    int rc = gs_bgft_ready_probe();
                    if (!rc) g_bgft_ready = 1;
                    g_capability_bgft_rc = rc;
                    g_capability_attempts++;
                    gs_log_write("resident", "capability bgft=%d ready=%d", rc, g_bgft_ready);
                }
                if (g_transfer_ready && g_bgft_ready && !g_ready_boot_written) {
                    g_ready_boot_written = 1;
                    write_boot("active", 0);
                }
            }
            if (access("/data/SSPI/.migration-active", F_OK) != 0) {
                gs_stage_poll();
                gs_worker_poll();
            } else {
                static uint64_t migration_log_at;
                uint64_t now = sceKernelGetProcessTime();
                if (!migration_log_at || now - migration_log_at >= 10000000) {
                    migration_log_at = now;
                    gs_log_write("resident", "intake state=blocked reason=data-migration marker=/data/SSPI/.migration-active");
                }
            }
        }
        if (listener >= 0)
        {
            int accepted;
            do
            {
                accepted = accept(listener, NULL, NULL);
                if (accepted >= 0)
                    gs_start_client(accepted);
            }
            while (accepted >= 0 && !g_stop);
        }
        sceKernelUsleep(1000000);
    }

    if (listener >= 0)
    {
        shutdown(listener, SHUT_RDWR);
        close(listener);
    }
    if (gs_worker_created) { scePthreadJoin(gs_worker, NULL); gs_worker_created = 0; }
    gs_stage_shutdown();
    gs_reap_clients(1);
    sspi_xfer_shutdown();
    int release_result;
    if (gs_park_task()) {
        g_filesystem_context.poisoned = 1;
        release_result = g_filesystem_context.code = -1;
        write_boot("filesystem-context-bgft-stop-unconfirmed-restart-required", -1);
    } else release_result = gs_filesystem_context_unlock(&g_filesystem_context);
    if (release_result) write_boot("filesystem-context-release-failed-restart-required", release_result);
    else gs_log_write("resident", "filesystem-context original shell context preserved lease=released");
    g_listener = -1;
    g_started = 0;
    return NULL;
}

__attribute__((visibility("default"))) int32_t plugin_load(
    int32_t argc, const char *argv[])
{
    (void)argc;
    (void)argv;
    if (g_filesystem_context.poisoned) return -1;
    if (!__sync_bool_compare_and_swap(&g_started, 0, 1)) return 0;
    if (g_main_thread_created) {
        scePthreadJoin(g_main_thread, NULL);
        g_main_thread_created = 0;
    }
    g_stop = 0;
    int thread_result = scePthreadCreate(&g_main_thread, NULL, gs_main_loop, NULL, "gs-resident");
    if (thread_result != 0)
    {
        g_started = 0;
        return thread_result;
    }
    g_main_thread_created = 1;
    return 0;
}

__attribute__((visibility("default"))) int32_t plugin_unload(
    int32_t argc, const char *argv[])
{
    int listener;
    (void)argc;
    (void)argv;
    if (!g_main_thread_created) return g_filesystem_context.poisoned || g_filesystem_context.fd >= 0 ? -1 : 0;
    g_stop = 1;
    listener = g_listener;
    if (listener >= 0) shutdown(listener, SHUT_RDWR);
    scePthreadJoin(g_main_thread, NULL);
    g_main_thread_created = 0;
    return g_filesystem_context.poisoned || g_filesystem_context.fd >= 0 ? -1 : 0;
}
