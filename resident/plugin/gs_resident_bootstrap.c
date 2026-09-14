#include <stdint.h>
#include <fcntl.h>
#include <stdio.h>
#include <string.h>
#include <sys/file.h>
#include <time.h>
#include <unistd.h>
#include <orbis/libkernel.h>
#include "../native/goldhen_process.h"
#include "../native/shell_loader.h"

#ifndef GS_APP_VERSION
#define GS_APP_VERSION "5.11"
#endif
#ifndef GS_BUILD_ID
#define GS_BUILD_ID "unversioned"
#endif
#define GS_ROOT "/data/SSPI/resident"
#define GS_SHELL_PATH GS_ROOT "/gs_resident_shell_" GS_APP_VERSION "-api3-" GS_BUILD_ID ".prx"

__attribute__((visibility("default"))) const char *g_pluginName = "GameSearchBootstrap";
__attribute__((visibility("default"))) const char *g_pluginDesc = "Starts the Game Search worker in SceShellUI";
__attribute__((visibility("default"))) const char *g_pluginAuth = "Game Search";
__attribute__((visibility("default"))) uint32_t g_pluginVersion = 0x00000102u;
static OrbisPthread gs_boot_thread;
static volatile int gs_boot_started;
static int gs_boot_created;

static void *load_shell(void *unused)
{
    (void)unused;
    // This process never downloads or binds the package port. Only SceShellUI does.
    int rc = gs_shell_load_guarded(GS_SHELL_PATH, GS_ROOT "/shell-load-bootstrap.txt");
    gs_log_write("startup", "bootstrap version=%s load=%d", GS_APP_VERSION, rc);
    gs_boot_started = 0;
    return NULL;
}

__attribute__((visibility("default"))) int32_t plugin_load(int32_t argc, const char *argv[])
{
    (void)argc; (void)argv;
    if (gs_boot_started) return 0;
    if (gs_boot_created) { scePthreadJoin(gs_boot_thread, NULL); gs_boot_created = 0; }
    gs_boot_started = 1;
    int rc = scePthreadCreate(&gs_boot_thread, NULL, load_shell, NULL, "gs-shell-load");
    if (rc) { gs_boot_started = 0; return rc; }
    gs_boot_created = 1;
    return 0;
}

__attribute__((visibility("default"))) int32_t plugin_unload(int32_t argc, const char *argv[])
{
    (void)argc; (void)argv;
    if (gs_boot_created) { scePthreadJoin(gs_boot_thread, NULL); gs_boot_created = 0; gs_boot_started = 0; }
    // The shell worker owns its own lifetime and pending BGFT tasks.
    return 0;
}
