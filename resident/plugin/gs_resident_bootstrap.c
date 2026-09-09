#include <stdint.h>
#include <fcntl.h>
#include <stdio.h>
#include <string.h>
#include <sys/file.h>
#include <time.h>
#include <unistd.h>
#include <orbis/libkernel.h>
#include "../native/goldhen_process.h"

#ifndef GS_APP_VERSION
#define GS_APP_VERSION "5.00"
#endif
#define GS_ROOT "/data/GameSearch/resident"
#define GS_SHELL_PATH GS_ROOT "/gs_resident_shell_" GS_APP_VERSION "-r4.prx"

__attribute__((visibility("default"))) const char *g_pluginName = "GameSearchBootstrap";
__attribute__((visibility("default"))) const char *g_pluginDesc = "Starts the Game Search worker in SceShellUI";
__attribute__((visibility("default"))) const char *g_pluginAuth = "Game Search";
__attribute__((visibility("default"))) uint32_t g_pluginVersion = 0x00000102u;
static OrbisPthread gs_boot_thread;
static int gs_boot_started;

static int shell_is_ready(void)
{
    char version[32], capability[192]; long long ticks;
    FILE *file = fopen(GS_ROOT "/heartbeat.txt", "rb");
    if (!file) return 0;
    int fields = fscanf(file, "%31s %lld", version, &ticks);
    // fscanf leaves the line ending before the capability line.
    fgets(capability, sizeof(capability), file);
    char *line = fgets(capability, sizeof(capability), file);
    fclose(file);
    int64_t now = 621355968000000000LL + (int64_t)time(NULL) * 10000000LL;
    return fields == 2 && line && !strcmp(version, GS_APP_VERSION "-r4") &&
        ticks <= now + 20000000LL && ticks >= now - 300000000LL &&
        strstr(capability, "host=shell ") && strstr(capability, "listener=1 ") && strstr(capability, "download=1");
}

static void *load_shell(void *unused)
{
    (void)unused;
    // This process never downloads or binds the package port. Only SceShellUI does.
    if (shell_is_ready()) return NULL;
    int lock = open(GS_ROOT "/shell-load.lock", O_CREAT | O_RDWR, 0600);
    if (lock < 0) return NULL;
    if (flock(lock, LOCK_EX | LOCK_NB)) { close(lock); return NULL; }
    if (!shell_is_ready()) {
        int rc = gs_goldhen_load_shell(GS_SHELL_PATH);
        FILE *log = fopen(GS_ROOT "/shell-loader.txt", "wb");
        if (log) { fprintf(log, "%s\npid=%d load=%d\n", GS_APP_VERSION, getpid(), rc); fclose(log); }
        // Let the new worker publish its heartbeat before another loader enters.
        for (int i = 0; rc >= 0 && i < 50 && !shell_is_ready(); i++) sceKernelUsleep(100000);
    }
    flock(lock, LOCK_UN); close(lock);
    return NULL;
}

__attribute__((visibility("default"))) int32_t plugin_load(int32_t argc, const char *argv[])
{
    (void)argc; (void)argv;
    if (gs_boot_started) return 0;
    if (scePthreadCreate(&gs_boot_thread, NULL, load_shell, NULL, "gs-shell-load")) return -1;
    gs_boot_started = 1;
    return 0;
}

__attribute__((visibility("default"))) int32_t plugin_unload(int32_t argc, const char *argv[])
{
    (void)argc; (void)argv;
    if (gs_boot_started) { scePthreadJoin(gs_boot_thread, NULL); gs_boot_started = 0; }
    // The shell worker owns its own lifetime and pending BGFT tasks.
    return 0;
}
