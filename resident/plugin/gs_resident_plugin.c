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
#include <sys/time.h>
#include <time.h>
#include <unistd.h>

#include <orbis/Net.h>
#include <orbis/libkernel.h>
#include "../native/goldhen_process.h"

#ifndef GS_APP_VERSION
#define GS_APP_VERSION "5.10"
#endif

#define GS_PLUGIN_VERSION 0x00000103u
#define GS_WORKER_VERSION GS_APP_VERSION "-r6"
#define GS_PORT 8742
#define GS_IPC_ROOT "/data/GameSearch/resident"
#define GS_SELFTEST_PATH GS_IPC_ROOT "/plugin-selftest.pkg"
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
static volatile int g_client_count;

static void ensure_directories(void)
{
    mkdir("/data", 0777);
    mkdir("/data/GameSearch", 0777);
    mkdir(GS_IPC_ROOT, 0777);
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
    char body[192];
    int64_t dotnet_ticks_now = DOTNET_EPOCH_TICKS +
        (int64_t)time(NULL) * 10000000LL;
    snprintf(body, sizeof(body), "%s\n%lld\nhost=shell pid=%d listener=1 download=1\n",
        GS_WORKER_VERSION, (long long)dotnet_ticks_now,
        getpid());
    write_telemetry(GS_IPC_ROOT "/heartbeat.txt", body);
}

static void write_boot(const char *state, int code)
{
    char body[192];
    int64_t ticks = DOTNET_EPOCH_TICKS + (int64_t)time(NULL) * 10000000LL;
    snprintf(body, sizeof(body), "%s\n%lld\n%s code=%d pid=%d\n",
        GS_APP_VERSION, (long long)ticks, state ? state : "unknown", code, getpid());
    write_atomic(GS_IPC_ROOT "/plugin-boot.txt", body);
}

static int send_all(int socket_id, const void *data, size_t size)
{
    const unsigned char *at = (const unsigned char *)data;
    while (size > 0)
    {
        ssize_t sent = send(socket_id, at, size, 0);
        if (sent < 0 && errno == EINTR) continue;
        if (sent <= 0) return -1;
        at += sent;
        size -= (size_t)sent;
    }
    return 0;
}

static void send_head(int socket_id, int status, const char *reason, const char *extra)
{
    char headers[1024];
    int length = snprintf(headers, sizeof(headers),
        "HTTP/1.1 %d %s\r\n%sConnection: close\r\n\r\n",
        status, reason, extra ? extra : "");
    if (length > 0 && length < (int)sizeof(headers))
        send_all(socket_id, headers, (size_t)length);
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
    while (used + 1 < capacity)
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
    const char *piece_route, int64_t length)
{
    unsigned char digest[32]; char hex[65], document[1024], headers[192];
    static const char digits[] = "0123456789abcdef";
    FILE *file = fopen(source, "rb");
    if (!file) return -1;
    int valid = length >= 0x1000 && has_pkg_magic(file) && !fseek(file, 0xfe0, SEEK_SET) &&
        fread(digest, 1, sizeof(digest), file) == sizeof(digest);
    fclose(file); if (!valid) return -1;
    for (int i = 0; i < 32; i++) { hex[i * 2] = digits[digest[i] >> 4]; hex[i * 2 + 1] = digits[digest[i] & 15]; }
    hex[64] = 0;
    int size = snprintf(document, sizeof(document),
        "{\"originalFileSize\":%lld,\"packageDigest\":\"%s\",\"numberOfSplitFiles\":1,\"pieces\":[{\"url\":\"http://127.0.0.1:8742%s\",\"fileOffset\":0,\"fileSize\":%lld,\"hashValue\":\"0000000000000000000000000000000000000000\"}]}",
        (long long)length, hex, piece_route, (long long)length);
    if (size < 0 || size >= (int)sizeof(document)) return -1;
    snprintf(headers, sizeof(headers), "Content-Type: application/json\r\nContent-Length: %d\r\nCache-Control: no-store\r\n", size);
    send_head(socket_id, 200, "OK", headers);
    if (strcmp(method, "HEAD")) send_all(socket_id, document, (size_t)size);
    return 0;
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
    struct timeval timeout;
    FILE *file;
    unsigned char *transfer_buffer;
    int partial = 0;
    int64_t start = 0;
    int64_t end;
    int64_t remaining;

    timeout.tv_sec = 5;
    timeout.tv_usec = 0;
    setsockopt(socket_id, SOL_SOCKET, SO_RCVTIMEO, &timeout, sizeof(timeout));
    timeout.tv_sec = 30;
    setsockopt(socket_id, SOL_SOCKET, SO_SNDTIMEO, &timeout, sizeof(timeout));

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
    snprintf(source, sizeof(source), "%s", GS_SELFTEST_PATH);
    gs_package_url(route, sizeof(route), "");
    snprintf(manifest_route, sizeof(manifest_route), "%s.json", route);
    manifest = strcmp(path, manifest_route) == 0;
    if (strcmp(path, "/pkg/SELFTEST.pkg") != 0)
    {
        if (!gs_job.id[0] || (strcmp(path, route) != 0 && !manifest)) {
            send_empty(socket_id, 404, "Not Found", NULL); return;
        }
        if (!gs_validated || gs_paused || gs_canceled) {
            send_empty(socket_id, 503, "Service Unavailable", "Retry-After: 1\r\n"); return;
        }
        snprintf(source, sizeof(source), "%s", gs_job.destination);
        job_route = 1;
    }
    if (stat(source, &info) != 0 || info.st_size < 4)
    {
        send_empty(socket_id, 404, "Not Found", NULL);
        return;
    }
    if (manifest) {
        if (serve_reference_json(socket_id, method, source, route, info.st_size))
            send_empty(socket_id, 503, "Service Unavailable", "Retry-After: 1\r\n");
        return;
    }
    file = fopen(source, "rb");
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
    send_head(socket_id, partial ? 206 : 200,
        partial ? "Partial Content" : "OK", representation);
    if (strcmp(method, "HEAD") == 0)
    {
        fclose(file);
        return;
    }
    if (fseek(file, (long)start, SEEK_SET) != 0)
    {
        fclose(file);
        return;
    }
    transfer_buffer = (unsigned char *)malloc(256 * 1024);
    if (!transfer_buffer) { fclose(file); return; }
    while (remaining > 0 && !g_stop && (!job_route || (!gs_paused && !gs_canceled)))
    {
        size_t wanted = remaining < 256 * 1024 ? (size_t)remaining : 256 * 1024;
        size_t count = fread(transfer_buffer, 1, wanted, file);
        if (count == 0 || send_all(socket_id, transfer_buffer, count) != 0) break;
        remaining -= (int64_t)count;
    }
    free(transfer_buffer);
    if (job_route) gs_record_served(start, end + 1 - remaining);
    fclose(file);
}

static void *client_thread(void *argument)
{
    int socket_id = *(int *)argument;
    free(argument);
    serve_client(socket_id);
    shutdown(socket_id, SHUT_RDWR);
    close(socket_id);
    __sync_sub_and_fetch(&g_client_count, 1);
    return NULL;
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
    address.sin_port = sceNetHtons(GS_PORT);
    address.sin_addr.s_addr = sceNetHtonl(0x7f000001u);
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

static void *gs_main_loop(void *argument)
{
    int listener;
    int net_result;
    (void)argument;

    ensure_directories();
    net_result = sceNetInit();
    listener = start_listener();
    g_listener = listener;
    write_boot(listener >= 0 ? "active" : "passive", net_result);

    while (!g_stop)
    {
        if (listener < 0) { listener = start_listener(); g_listener = listener; }
        if (listener >= 0)
        {
            write_heartbeat();
            gs_worker_poll();
        }
        if (listener >= 0)
        {
            int accepted;
            do
            {
                accepted = accept(listener, NULL, NULL);
                if (accepted >= 0)
                {
                    int *client = g_client_count < 8 ? (int *)malloc(sizeof(*client)) : NULL;
                    if (client)
                    {
                        OrbisPthread thread;
                        *client = accepted;
                        __sync_add_and_fetch(&g_client_count, 1);
                        if (scePthreadCreate(&thread, NULL, client_thread, client,
                            "gs-http") == 0)
                            scePthreadDetach(thread);
                        else
                        {
                            __sync_sub_and_fetch(&g_client_count, 1);
                            free(client);
                            close(accepted);
                        }
                    }
                    else close(accepted);
                }
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
    while (g_client_count > 0) sceKernelUsleep(10000);
    g_listener = -1;
    g_started = 0;
    return NULL;
}

__attribute__((visibility("default"))) int32_t plugin_load(
    int32_t argc, const char *argv[])
{
    (void)argc;
    (void)argv;
    if (g_started) return 0;
    // A game-owned socket cannot survive game suspension. Refuse that host.
    if (!gs_goldhen_is_shell()) return -1;
    g_stop = 0;
    g_started = 1;
    if (scePthreadCreate(&g_main_thread, NULL, gs_main_loop, NULL, "gs-resident") != 0)
    {
        g_started = 0;
        return -1;
    }
    return 0;
}

__attribute__((visibility("default"))) int32_t plugin_unload(
    int32_t argc, const char *argv[])
{
    int listener;
    (void)argc;
    (void)argv;
    if (!g_started) return 0;
    g_stop = 1;
    listener = g_listener;
    if (listener >= 0) shutdown(listener, SHUT_RDWR);
    scePthreadJoin(g_main_thread, NULL);
    return 0;
}
