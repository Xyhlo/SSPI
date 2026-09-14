#ifndef GS_WORKER_PRESENCE_H
#define GS_WORKER_PRESENCE_H
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <time.h>

#ifndef GS_LEGACY_HEARTBEAT
#define GS_LEGACY_HEARTBEAT "/data/GameSearch/resident/heartbeat.txt"
#endif

static int gs_read_worker_heartbeat(const char *path, int64_t *ticks, char *capabilities, size_t capacity)
{
    char version[64], line[96]; long long parsed;
    FILE *file = fopen(path, "rb"); if (!file) return 0;
    int good = fgets(version, sizeof(version), file) && fgets(line, sizeof(line), file) &&
        sscanf(line, "%lld", &parsed) == 1 && fgets(capabilities, (int)capacity, file);
    fclose(file);
    if (!good || !strstr(capabilities, "host=shell ") || !strstr(capabilities, "listener=1 ")) return 0;
    *ticks = parsed; return 1;
}

static int gs_worker_heartbeat_advancing(const char *path)
{
    int64_t first, current; char capabilities[512];
    if (!gs_read_worker_heartbeat(path, &first, capabilities, sizeof(capabilities))) return 0;
    /* A fresh file from the previous boot is not evidence of a running worker. */
    for (int i = 0; i < 10; i++) {
        sceKernelUsleep(150000);
        if (gs_read_worker_heartbeat(path, &current, capabilities, sizeof(capabilities)) && current != first) return 1;
    }
    return 0;
}
static int gs_legacy_worker_advancing(void) { return gs_worker_heartbeat_advancing(GS_LEGACY_HEARTBEAT); }

#ifndef GS_SHARED_STORAGE_HEARTBEAT
#define GS_SHARED_STORAGE_HEARTBEAT "/user/data/SSPI/resident/heartbeat.txt"
#endif

static int gs_read_shared_storage_heartbeat(int64_t *ticks)
{
    char version[64], line[96], capabilities[512]; long long parsed;
    FILE *file = fopen(GS_SHARED_STORAGE_HEARTBEAT, "rb");
    if (!file) return 0;
    int good = fgets(version, sizeof(version), file) && fgets(line, sizeof(line), file) &&
        sscanf(line, "%lld", &parsed) == 1 && fgets(capabilities, sizeof(capabilities), file);
    fclose(file);
    if (!good || !strstr(capabilities, "host=shell ") ||
        !strstr(capabilities, "listener=0 ") || !strstr(capabilities, "storage=0 ")) return 0;
    *ticks = parsed;
    return 1;
}

static int gs_shared_storage_worker_advancing(void)
{
    int64_t first, current;
    if (!gs_read_shared_storage_heartbeat(&first)) return 0;
    for (int i = 0; i < 10; ++i) {
        sceKernelUsleep(150000);
        if (gs_read_shared_storage_heartbeat(&current) && current != first) return 1;
    }
    return 0;
}
#endif
