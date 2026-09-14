#ifndef GS_SHELL_LOADER_H
#define GS_SHELL_LOADER_H

#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <time.h>
#include "worker_presence.h"

/* Callers provide flock/open/close, sceKernelUsleep and the GoldHEN loader.
 * The path override is only used by the host regression harness. */
#ifndef GS_SHELL_IPC_ROOT
#define GS_SHELL_IPC_ROOT "/data/SSPI/resident"
#endif
/* SceShellUI may only expose the shared storage namespace, so liveness must be
 * checked against both roots the worker can publish under. */
#ifndef GS_SHELL_SHARED_IPC_ROOT
#define GS_SHELL_SHARED_IPC_ROOT "/user/data/SSPI/resident"
#endif
/* A command-6 load that returned a module ID is not re-issued while this long
 * as no worker heartbeat has appeared; the marker is refreshed on every load
 * and cleared as soon as readiness is proven. */
#ifndef GS_SHELL_PENDING_TICKS
#define GS_SHELL_PENDING_TICKS (120LL * 10000000LL)
#endif

static int gs_shell_worker_ready_at(const char *root)
{
    char path[160], capability[512]; int64_t ticks;
    if (snprintf(path, sizeof(path), "%s/heartbeat.txt", root) >= (int)sizeof(path)) return 0;
    if (!gs_read_worker_heartbeat(path, &ticks, capability, sizeof(capability))) return 0;
    int64_t now = 621355968000000000LL + (int64_t)time(NULL) * 10000000LL;
    return ticks <= now + 20000000LL && ticks >= now - 300000000LL &&
        strstr(capability, "transfer=1 ") && strstr(capability, "bgft=1 ") &&
        strstr(capability, "staged=10 ") && !strstr(capability, "migration=1");
}

static int gs_shell_worker_ready(void)
{
    /* SceShellUI started before GoldHEN patched ShellCore, so it may not expose
     * the application sandbox's /data bind. It reaches the same user storage
     * as /user/data; accept a capable heartbeat from either root. */
    return gs_shell_worker_ready_at(GS_SHELL_IPC_ROOT) ||
        gs_shell_worker_ready_at("/user/data/SSPI/resident");
}

static int gs_shell_worker_heartbeat_advancing(void)
{
    return gs_worker_heartbeat_advancing(GS_SHELL_IPC_ROOT "/heartbeat.txt") ||
        gs_worker_heartbeat_advancing(GS_SHELL_SHARED_IPC_ROOT "/heartbeat.txt");
}

/* The canonical root is not always visible from SceShellUI; report readiness
 * detail from whichever root actually carries the worker heartbeat. */
static int gs_shell_read_present_heartbeat(char *capabilities, size_t capacity)
{
    char scratch[512]; int64_t ticks;
    if (!capabilities || !capacity) { capabilities = scratch; capacity = sizeof(scratch); }
    if (gs_read_worker_heartbeat(GS_SHELL_IPC_ROOT "/heartbeat.txt", &ticks, capabilities, capacity)) return 1;
    return gs_read_worker_heartbeat(GS_SHELL_SHARED_IPC_ROOT "/heartbeat.txt", &ticks, capabilities, capacity);
}

static int gs_shell_worker_heartbeat_present(void)
{ return gs_shell_read_present_heartbeat(NULL, 0); }

/* A successful command-6 load that has not produced a heartbeat yet must not
 * be repeated: GoldHEN maps another PRX copy into SceShellUI each time. The
 * marker holds the returned module ID and the load time so the next activation
 * can wait for the worker instead of injecting a duplicate. */
static int gs_shell_load_pending_read(int *module_result, int64_t *loaded_ticks)
{
    char path[160], line[96];
    int have_module = 0, have_ticks = 0;
    if (snprintf(path, sizeof(path), "%s/shell-load-pending.txt", GS_SHELL_IPC_ROOT) >= (int)sizeof(path)) return 0;
    FILE *file = fopen(path, "rb");
    if (!file) return 0;
    if (!fgets(line, sizeof(line), file) || strncmp(line, "format=1", 8)) { fclose(file); return 0; }
    while (fgets(line, sizeof(line), file)) {
        long long module_value = 0, tick_value = 0;
        if (sscanf(line, "module_result=%lld", &module_value) == 1) {
            have_module = 1;
            if (module_result) *module_result = (int)module_value;
        } else if (sscanf(line, "ticks=%lld", &tick_value) == 1) {
            have_ticks = 1;
            if (loaded_ticks) *loaded_ticks = (int64_t)tick_value;
        }
    }
    fclose(file);
    return have_module && have_ticks;
}

static int gs_shell_load_pending_fresh(void)
{
    int64_t loaded_ticks = 0;
    if (!gs_shell_load_pending_read(NULL, &loaded_ticks)) return 0;
    int64_t now = 621355968000000000LL + (int64_t)time(NULL) * 10000000LL;
    return loaded_ticks <= now + 20000000LL && now - loaded_ticks <= GS_SHELL_PENDING_TICKS;
}

static void gs_shell_write_pending(int module_result)
{
    char path[160], temporary[200];
    if (snprintf(path, sizeof(path), "%s/shell-load-pending.txt", GS_SHELL_IPC_ROOT) >= (int)sizeof(path) ||
        snprintf(temporary, sizeof(temporary), "%s.tmp", path) >= (int)sizeof(temporary)) return;
    FILE *file = fopen(temporary, "wb");
    if (!file) return;
    int64_t ticks = 621355968000000000LL + (int64_t)time(NULL) * 10000000LL;
    int rc = fprintf(file, "format=1\nmodule_result=%d\npid=%d\nticks=%lld\n", module_result, getpid(), (long long)ticks);
    if (rc >= 0) rc = fflush(file);
    if (rc == 0) rc = fsync(fileno(file));
    if (fclose(file) != 0) rc = -1;
    if (rc == 0) rename(temporary, path);
    else unlink(temporary);
}

static void gs_shell_clear_pending(void)
{
    char path[160];
    if (snprintf(path, sizeof(path), "%s/shell-load-pending.txt", GS_SHELL_IPC_ROOT) < (int)sizeof(path)) unlink(path);
}

/* Fresh command-6 load plus the bounded wait for the worker's first capable
 * heartbeat. Shared by the ordinary path and the stale-handle recovery path. */
static int gs_shell_load_fresh(const char *path, const char *diagnostic_path)
{
    int result = gs_goldhen_load_shell_traced(path, diagnostic_path);
    if (result >= 0) {
        gs_shell_write_pending(result);
        /* Keep competing loaders out while plugin_load's worker thread
         * binds its listener and publishes the first capable heartbeat. */
        for (int i = 0; i < 150 && !gs_shell_worker_ready(); ++i)
            sceKernelUsleep(100000);
        /* Preserve the real syscall trace, which says awaiting heartbeat.
         * A returned module ID alone must not claim successful startup. */
        if (!gs_shell_worker_ready() && gs_shared_storage_worker_advancing()) {
            result = -9;
            GsGoldHenLoadTrace trace = { .stage = "worker-shared-storage-unavailable", .result = -9, .request_result = UINT64_MAX };
            gs_goldhen_write_load_trace(diagnostic_path, &trace);
        } else if (!gs_shell_worker_ready()) {
            char capabilities[512];
            result = gs_shell_worker_heartbeat_present() ? -9 : -7;
            GsGoldHenLoadTrace trace = { .stage = result == -9 ? "worker-present-capabilities-unavailable" : "worker-no-heartbeat", .load_called = 1, .load_returned = 1, .result = result, .request_result = UINT64_MAX };
            gs_goldhen_write_load_trace(diagnostic_path, &trace);
            if (result == -9 && gs_shell_read_present_heartbeat(capabilities, sizeof(capabilities)))
                gs_log_write("resident", "readiness %s", capabilities);
        }
        if (gs_shell_worker_ready()) gs_shell_clear_pending();
    } else {
        gs_shell_clear_pending();
    }
    return result;
}

static int gs_shell_load_guarded(const char *path, const char *diagnostic_path)
{
#if defined(__FreeBSD__)
    int directory_result = gs_data_prepare_root();
    if (!directory_result) directory_result = gs_data_ensure_directory(GS_SHELL_IPC_ROOT, 0777);
    if (directory_result) {
        gs_log_write("resident", "loader data-directory unavailable code=0x%08x", (unsigned)directory_result);
        GsGoldHenLoadTrace trace = { .stage = "loader-data-directory-unavailable", .result = -11, .request_result = UINT64_MAX };
        gs_goldhen_write_load_trace(diagnostic_path, &trace);
        return -11;
    }
#else
    mkdir("/data/SSPI", 0777); mkdir(GS_SHELL_IPC_ROOT, 0777);
#endif
    int lock = open(GS_SHELL_IPC_ROOT "/shell-load.lock", O_CREAT | O_RDWR, 0600);
    if (lock < 0) {
        GsGoldHenLoadTrace trace = { .stage = "loader-lock-unavailable", .result = -5, .request_result = UINT64_MAX };
        gs_goldhen_write_load_trace(diagnostic_path, &trace);
        return -5;
    }
    if (flock(lock, LOCK_EX | LOCK_NB)) {
        GsGoldHenLoadTrace trace = { .stage = "loader-lock-busy", .result = -6, .request_result = UINT64_MAX };
        gs_goldhen_write_load_trace(diagnostic_path, &trace);
        close(lock);
        return -6;
    }

    int result;
    if (gs_legacy_worker_advancing()) {
        GsGoldHenLoadTrace trace = { .stage = "legacy-worker-active-restart-required", .result = -8, .request_result = UINT64_MAX };
        gs_goldhen_write_load_trace(diagnostic_path, &trace); result = -8;
    } else if (!access("/data/SSPI/.migration-active", F_OK)) {
        GsGoldHenLoadTrace trace = { .stage = "data-migration-pending", .result = -10, .request_result = UINT64_MAX };
        gs_goldhen_write_load_trace(diagnostic_path, &trace); result = -10;
    } else if (gs_shell_worker_ready()) {
        GsGoldHenLoadTrace trace = { .stage = "skipped-ready", .request_result = UINT64_MAX };
        gs_goldhen_write_load_trace(diagnostic_path, &trace);
        gs_shell_clear_pending();
        result = 0;
    } else if (gs_shared_storage_worker_advancing()) {
        GsGoldHenLoadTrace trace = { .stage = "worker-shared-storage-unavailable", .result = -9, .request_result = UINT64_MAX };
        gs_goldhen_write_load_trace(diagnostic_path, &trace); result = -9;
    } else if (gs_shell_worker_heartbeat_advancing()) {
        for (int i = 0; i < 150 && !gs_shell_worker_ready(); i++) sceKernelUsleep(100000);
        result = gs_shell_worker_ready() ? 0 : -9;
        GsGoldHenLoadTrace trace = { .stage = result ? "worker-present-capabilities-unavailable" : "existing-worker-ready", .result = result, .request_result = UINT64_MAX };
        gs_goldhen_write_load_trace(diagnostic_path, &trace);
        if (!result) gs_shell_clear_pending();
    } else if (gs_shell_load_pending_fresh()) {
        /* The previous activation already returned a module ID; wait for that
         * worker's first heartbeat rather than mapping a second copy. */
        int pending_module = 0;
        (void)gs_shell_load_pending_read(&pending_module, NULL);
        for (int i = 0; i < 150 && !gs_shell_worker_ready(); ++i) sceKernelUsleep(100000);
        if (gs_shell_worker_ready()) {
            gs_shell_clear_pending();
            GsGoldHenLoadTrace trace = { .stage = "existing-worker-ready", .request_result = UINT64_MAX, .load_called = 1, .load_returned = 1 };
            gs_goldhen_write_load_trace(diagnostic_path, &trace);
            result = 0;
        } else {
            /* No advancing/ready heartbeat within the grace window: the
             * recorded handle is stale. Unload it so a same-build retry
             * re-executes the module instead of returning the old handle
             * again, then continue with a normal fresh load. An unload
             * failure is diagnostics only and never a fatal state. */
            int64_t unload_result = gs_goldhen_unload_prx_traced("SceShellUI",
                (uint64_t)(int64_t)pending_module, diagnostic_path);
            if (unload_result == 0)
                gs_log_write("resident", "loader stale handle unloaded handle=0x%llX",
                    (unsigned long long)(uint64_t)(int64_t)pending_module);
            else
                gs_log_write("resident", "loader stale handle unload failed handle=0x%llX code=0x%llX",
                    (unsigned long long)(uint64_t)(int64_t)pending_module, (unsigned long long)unload_result);
            gs_shell_clear_pending();
            result = gs_shell_load_fresh(path, diagnostic_path);
        }
    } else {
        result = gs_shell_load_fresh(path, diagnostic_path);
    }
    flock(lock, LOCK_UN);
    close(lock);
    return result;
}
#endif
