#include <stdint.h>
#include <stddef.h>

// OpenOrbis PRX module parameter ABI. The stock crtlib only invokes constructors;
// the named-process loader needs module_start to start this plugin explicitly.
__attribute__((section(".data.sce_module_param"), used, aligned(8)))
const uint64_t _sceProcessParam[] = { 0x18, 0x13C13F4BF, 0x1000051 };
void *__dso_handle = &__dso_handle;
void *_sceLibc;
extern void (*__init_array_start[])(void);
extern void (*__init_array_end[])(void);
extern void (*__fini_array_start[])(void);
extern void (*__fini_array_end[])(void);
extern void __cxa_finalize(void *);
extern int32_t plugin_load(int32_t, const char *[]);
extern int32_t plugin_unload(int32_t, const char *[]);
static int gs_initialized;

__attribute__((visibility("default"))) int32_t module_start(size_t argc, const void *argv)
{
    (void)argc; (void)argv;
    if (gs_initialized) return 0;
    for (void (**entry)(void) = __init_array_start; entry != __init_array_end; entry++) (*entry)();
    gs_initialized = 1;
    return plugin_load(0, NULL);
}

__attribute__((visibility("default"))) int32_t module_stop(size_t argc, const void *argv)
{
    (void)argc; (void)argv;
    if (!gs_initialized) return 0;
    int32_t rc = plugin_unload(0, NULL);
    if (rc) return rc;
    __cxa_finalize(__dso_handle);
    for (void (**entry)(void) = __fini_array_end; entry != __fini_array_start;) (*--entry)();
    gs_initialized = 0;
    return 0;
}

// GoldHEN's named-process loader follows the PRX _init/_fini entry points.
int32_t _init(size_t argc, const void *argv) { return module_start(argc, argv); }
int32_t _fini(size_t argc, const void *argv) { return module_stop(argc, argv); }
