#ifndef SSPI_NATIVE_LOG_H
#define SSPI_NATIVE_LOG_H
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <fcntl.h>
#include <sys/stat.h>
#include "sspi_data_fs.h"
#ifdef _WIN32
#include <windows.h>
#include <io.h>
#include <process.h>
#include <direct.h>
#else
#include <unistd.h>
#include <sys/file.h>
#endif
#if defined(__FreeBSD__) && !defined(GS_LOG_HOST_TEST)
#ifdef __cplusplus
extern "C" uint64_t sceKernelGetProcessTime(void);
#else
extern uint64_t sceKernelGetProcessTime(void);
#endif
#endif

#ifndef GS_LOG_ROOT
#define GS_LOG_ROOT "/data/SSPI/logs"
#endif

#if defined(__FreeBSD__) && !defined(GS_LOG_HOST_TEST)
static int gs_log_prepare_files(void)
{
    int result = gs_data_prepare_root();
    if (!result) result = gs_data_ensure_directory(GS_LOG_ROOT, 0777);
    const char *root = GS_LOG_ROOT;
    if (result) {
        int canonical = result;
        (void)sceKernelMkdir("/user/data", 0777);
        (void)sceKernelMkdir("/user/data/SSPI", 0777);
        (void)sceKernelMkdir("/user/data/SSPI/logs", 0777);
        result = gs_data_ensure_directory("/user/data/SSPI/logs", 0777);
        if (result) return canonical;
        root = "/user/data/SSPI/logs";
    }
    const char *names[] = { "startup", "network", "download", "resident", "combined" };
    for (unsigned i = 0; i < sizeof(names) / sizeof(names[0]); i++) {
        char path[1200]; snprintf(path, sizeof(path), "%s/%s.log", root, names[i]);
        int fd = sceKernelOpen(path, O_WRONLY | O_CREAT | O_APPEND | O_NOFOLLOW | O_NONBLOCK, 0666);
        if (fd < 0) return fd;
        result = sceKernelFchmod(fd, 0666);
        sceKernelClose(fd);
        /* A writable, already shared log may belong to another process. */
        if (result == -1 || result == (int)0x80020001u) result = 0;
        if (result) return result;
    }
    return 0;
}
#endif

static int gs_log_equal_prefix(const char *value, const char *prefix)
{
    while (*prefix) {
        unsigned char a = (unsigned char)*value++, b = (unsigned char)*prefix++;
        if (a >= 'A' && a <= 'Z') a += 'a' - 'A';
        if (b >= 'A' && b <= 'Z') b += 'a' - 'A';
        if (a != b || !a) return 0;
    }
    return 1;
}

static void gs_log_redact(const char *input, char *output, size_t capacity)
{
    size_t used = 0;
    const char *keys[] = { "authorization", "bearer", "token", "api_key", "apikey", "password", "access_token", "refresh_token", "client_secret", "secret", "key" };
    for (const char *p = input; *p && used + 1 < capacity;) {
        if (gs_log_equal_prefix(p, "https://") || gs_log_equal_prefix(p, "http://")) {
            const char *replacement = "[url]";
            while (*replacement && used + 1 < capacity) output[used++] = *replacement++;
            while (*p && *p != ' ' && *p != '\t' && *p != '\r' && *p != '\n' && *p != '"' && *p != '\'') p++;
            continue;
        }
        int secret = -1, quote = 0; const char *value = p;
        for (unsigned i = 0; i < sizeof(keys) / sizeof(keys[0]); i++) {
            const char *at = p; int key_quote = *at == '"' || *at == '\'' ? *at++ : 0;
            if (!gs_log_equal_prefix(at, keys[i])) continue;
            at += strlen(keys[i]);
            if (key_quote) { if (*at != key_quote) continue; at++; }
            if (i == 1) { if (*at != ' ') continue; }
            else {
                while (*at == ' ' || *at == '\t') at++;
                if (*at != ':' && *at != '=') continue;
                at++;
            }
            while (*at == ' ' || *at == '\t') at++;
            quote = *at == '"' || *at == '\'' ? *at++ : 0;
            value = at; secret = (int)i; break;
        }
        if (secret >= 0) {
            while (p < value && used + 1 < capacity) output[used++] = *p++;
            p = value;
            const char *replacement = "[redacted]";
            while (*replacement && used + 1 < capacity) output[used++] = *replacement++;
            if (quote) {
                while (*p && *p != quote) { if (*p == '\\' && p[1]) p++; p++; }
            } else if (secret == 0) {
                while (*p && *p != '\r' && *p != '\n') p++;
            } else while (*p && *p != ' ' && *p != '\t' && *p != '\r' && *p != '\n' && *p != '&' && *p != ',' && *p != '}' && *p != '"' && *p != '\'') p++;
            continue;
        }
        unsigned char c = (unsigned char)*p++;
        output[used++] = c < 32 || c == 127 ? ' ' : (char)c;
    }
    output[used] = 0;
}

static int gs_log_append(const char *path, const char *line, size_t length, int64_t limit)
{
#ifdef _WIN32
    HANDLE handle = CreateFileA(path, FILE_APPEND_DATA | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
        NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (handle == INVALID_HANDLE_VALUE) return -1;
    OVERLAPPED lock = {0};
    if (!LockFileEx(handle, LOCKFILE_EXCLUSIVE_LOCK, 0, MAXDWORD, MAXDWORD, &lock)) { CloseHandle(handle); return -1; }
    LARGE_INTEGER size;
    if (GetFileSizeEx(handle, &size) && size.QuadPart > limit) {
        LARGE_INTEGER zero = {0}; SetFilePointerEx(handle, zero, NULL, FILE_BEGIN); SetEndOfFile(handle);
    }
    LARGE_INTEGER end = {0}; SetFilePointerEx(handle, end, NULL, FILE_END);
    DWORD written; WriteFile(handle, line, (DWORD)length, &written, NULL);
    UnlockFileEx(handle, 0, MAXDWORD, MAXDWORD, &lock); CloseHandle(handle);
#else
    int fd = open(path, O_WRONLY | O_CREAT | O_APPEND | O_NOFOLLOW, 0666);
    if (fd < 0) return -1;
    if (flock(fd, LOCK_EX)) { close(fd); return -1; }
    off_t size = lseek(fd, 0, SEEK_END);
    if (size > limit) ftruncate(fd, 0);
    /* One append syscall prevents records from interleaving between processes. */
    (void)write(fd, line, length);
    flock(fd, LOCK_UN); close(fd);
#endif
    return 0;
}

static int gs_log_append_root(const char *root, const char *leaf, const char *line, size_t length, int64_t limit)
{
    char path[1200];
    if (!root || !leaf || snprintf(path, sizeof(path), "%s/%s", root, leaf) >= (int)sizeof(path)) return -1;
    return gs_log_append(path, line, length, limit);
}

static int gs_log_append_resilient(const char *leaf, const char *line, size_t length, int64_t limit)
{
    if (!gs_log_append_root(GS_LOG_ROOT, leaf, line, length, limit)) return 0;
#if defined(__FreeBSD__) && !defined(GS_LOG_HOST_TEST)
    {
        static int shared_ready;
        if (!shared_ready) {
            (void)sceKernelMkdir("/user/data", 0777);
            (void)sceKernelMkdir("/user/data/SSPI", 0777);
            (void)sceKernelMkdir("/user/data/SSPI/logs", 0777);
        }
        if (!gs_log_append_root("/user/data/SSPI/logs", leaf, line, length, limit)) { shared_ready = 1; return 0; }
    }
#endif
    return -1;
}

static void gs_log_write(const char *category, const char *format, ...)
{
    if (!format || !category || (strcmp(category, "network") && strcmp(category, "download") &&
        strcmp(category, "startup") && strcmp(category, "resident"))) return;
    char raw[1400], sanitized[1500], line[1800], category_leaf[64];
    va_list args; va_start(args, format); vsnprintf(raw, sizeof(raw), format, args); va_end(args);
    gs_log_redact(raw, sanitized, sizeof(sanitized));
#ifdef _WIN32
    _mkdir(GS_LOG_ROOT); int pid = _getpid();
    uint64_t monotonic_ms = GetTickCount64();
#else
#if defined(__FreeBSD__) && !defined(GS_LOG_HOST_TEST)
    static int directories_ready;
    if (!directories_ready) {
        int result = gs_log_prepare_files();
        if (result) {
            static int last_error;
            if (result != last_error) {
                char diagnostic[160];
                snprintf(diagnostic, sizeof(diagnostic), "SSPI shared data/log directory unavailable: kernel=0x%08x\n", (unsigned)result);
                sceKernelDebugOutText(0, diagnostic); last_error = result;
            }
            return;
        }
        directories_ready = 1;
    }
    int pid = getpid();
    uint64_t monotonic_ms = sceKernelGetProcessTime() / 1000;
#else
    mkdir("/data/SSPI", 0777); mkdir(GS_LOG_ROOT, 0777); int pid = getpid();
    struct timespec monotonic = {0}; clock_gettime(CLOCK_MONOTONIC, &monotonic);
    uint64_t monotonic_ms = (uint64_t)monotonic.tv_sec * 1000 + (uint64_t)monotonic.tv_nsec / 1000000;
#endif
#endif
    int count = snprintf(line, sizeof(line), "unix=%lld mono_ms=%llu pid=%d category=%s %s\n", (long long)time(NULL), (unsigned long long)monotonic_ms, pid, category, sanitized);
    if (count <= 0 || count >= (int)sizeof(line)) return;
    snprintf(category_leaf, sizeof(category_leaf), "%s.log", category);
    gs_log_append_resilient("combined.log", line, (size_t)count, 16 * 1024 * 1024);
    gs_log_append_resilient(category_leaf, line, (size_t)count, 8 * 1024 * 1024);
}
#endif
