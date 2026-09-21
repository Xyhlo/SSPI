#include <stdint.h>
#include <stddef.h>

__attribute__((section(".data.sce_module_param"), used, aligned(8)))
const uint64_t _sceProcessParam[] = { 0x18, 0x13C13F4BF, 0x1000051 };
void *__dso_handle = &__dso_handle;
void *_sceLibc;
extern void (*__init_array_start[])(void);
extern void (*__init_array_end[])(void);
extern void (*__fini_array_start[])(void);
extern void (*__fini_array_end[])(void);
extern void __cxa_finalize(void *);
static int initialized;

__attribute__((visibility("default"))) int module_start(size_t argc, const void *argv)
{
    (void)argc; (void)argv;
    if (!initialized) {
        for (void (**entry)(void) = __init_array_start; entry != __init_array_end; ++entry) (*entry)();
        initialized = 1;
    }
    return 0;
}

__attribute__((visibility("default"))) int module_stop(size_t argc, const void *argv)
{
    (void)argc; (void)argv;
    if (initialized) {
        __cxa_finalize(__dso_handle);
        for (void (**entry)(void) = __fini_array_end; entry != __fini_array_start;) (*--entry)();
        initialized = 0;
    }
    return 0;
}

// The PS4 loader calls _init. The SDK crtlib's _init is empty and its constructor
// boundary symbols are BSS variables, so it cannot initialize the RAR decoder.
int _init(size_t argc, const void *argv) { return module_start(argc, argv); }
int _fini(size_t argc, const void *argv) { return module_stop(argc, argv); }
