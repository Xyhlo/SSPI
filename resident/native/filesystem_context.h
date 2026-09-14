#ifndef GS_FILESYSTEM_CONTEXT_H
#define GS_FILESYSTEM_CONTEXT_H
#include "module_inventory.h"

/* This lease coordinates resident generations without changing SceShellUI's
 * credentials or filesystem roots. GoldHEN SDK commands 2/3 affect Sony's
 * threads too, so they must never be used from a resident shell worker. */
typedef struct {
    int fd, poisoned, code;
} GsFilesystemContext;

static int gs_filesystem_context_unlock(GsFilesystemContext *context)
{
    if (context->fd < 0) return 0;
    if (flock(context->fd, LOCK_UN)) { context->poisoned = 1; return context->code = -1; }
    int fd = context->fd;
    if (sceKernelClose(fd)) { context->poisoned = 1; return context->code = -1; }
    context->fd = -1;
    return 0;
}

static int gs_filesystem_context_lock(GsFilesystemContext *context)
{
    if (context->fd >= 0 || context->poisoned) return -1;
    int fd = sceKernelOpen("/user/data/SSPI/resident/filesystem-context.lock", O_RDWR | O_CREAT | O_NOFOLLOW, 0600);
    if (fd < 0) return context->code = fd;
    if (flock(fd, LOCK_EX | LOCK_NB)) {
        sceKernelClose(fd); return context->code = -1;
    }
    context->fd = fd;
    return 0;
}

typedef struct {
    int list_result, info_result, self;
    size_t count;
    char module[256];
} GsFilesystemProof;

static int gs_filesystem_context_unique_module(const void *self, GsFilesystemProof *proof)
{
    OrbisKernelModule modules[256]; size_t count = 0; int found_self = 0;
    memset(proof, 0, sizeof(*proof));
    if (!self) return 0;
    proof->list_result = gs_native_module_list(modules, 256, &count); proof->count = count;
    if (proof->list_result || !count || count >= 256) return 0;
    for (size_t i = 0; i < count; i++) {
        OrbisKernelModuleInfo info; memset(&info, 0, sizeof(info)); info.size = sizeof(info);
        proof->info_result = gs_native_module_info(modules[i], &info);
        if (proof->info_result || !memchr(info.name, 0, sizeof(info.name)) ||
            !info.segmentCount || info.segmentCount > 4) return 0;
        snprintf(proof->module, sizeof(proof->module), "%s", info.name);
        int owns_self = 0;
        for (uint32_t n = 0; n < info.segmentCount; n++) {
            uintptr_t begin = (uintptr_t)info.segmentInfo[n].address;
            uintptr_t address = (uintptr_t)self;
            if (address >= begin && address - begin < info.segmentInfo[n].size) owns_self = 1;
        }
        if (owns_self) { proof->self++; if (found_self++) return 0; continue; }
        /* Older workers do not hold this lease, and a stale heartbeat/free
         * listener does not prove their install or transfer threads stopped.
         * Require their module to be absent, including retired mapped copies. */
        if (strstr(info.name, "gs_resident_shell") || strstr(info.name, "gs_resident_plugin") ||
            strstr(info.name, "GameSearchResident")) return 0;
        void *symbol = NULL;
        if (!sceKernelDlsym(modules[i], "g_pluginName", &symbol) && symbol) {
            const char *name = NULL; memcpy(&name, symbol, sizeof(name));
            if (name && !strcmp(name, "GameSearchResident")) return 0;
        }
    }
    return found_self == 1;
}
#endif
