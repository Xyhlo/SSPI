#include <orbis/libkernel.h>
#include <dirent.h>

extern void (*mono_set_dirs)(const char* lib, const char* etc);
extern void* (*mono_jit_init)(const char* domain_name);
extern void* (*mono_init_from_assembly)(const char* domain_name, const char* filename);
extern void* (*mono_get_root_domain)(void);
extern void* (*mono_domain_assembly_open)(void* domain, const char *assembly_path);
extern void* (*mono_class_from_name)(void* image, const char* class_namespace, const char* class_name);
extern void* (*mono_class_get_method_from_name)(void* method_class, const char *method_name, int param_count);
extern void* (*mono_runtime_invoke)(void* method, void* obj, void** params, void** exc);
extern void* (*mono_method_get_unmanaged_thunk)(void* method);
extern void* (*mono_jit_cleanup)(void* domain);
extern void* (*mono_thread_attach)(void* domain);
extern void* (*mono_assembly_get_image)(void* assembly);

extern int   (*mono_jit_exec)(void* domain, void* assembly, int argc, char* argv[]);
extern void* (*mono_assembly_load_from_full)(void* image, int* fname, int* status, int refonly);
extern void* (*mono_add_internal_call)(const char* methodPath, void* func);
extern void (*mono_debugger_agent_parse_options)(char* debug_options);

extern void (*mono_debug_init)(int debug_format);
extern void (*mono_debug_domain_create)(void* domain);
extern void (*mono_jit_parse_options)(int arc, char* argv[]);
extern void (*mono_debug_open_image_from_memory)(void* image, const char* raw_contents, int size);
extern void* (*mono_image_open_from_data_with_name)(char *data, unsigned int data_len, int need_copy, int* status, int refonly, const char *name);

extern int (*JitCreateSharedMemory)(int flags, size_t size, int protection, void** destinationHandle);
extern int (*JitCreateAliasOfSharedMemory)(int handle, int protection, void** destinationHandle);
extern int (*JitMapSharedMemory)(int handle, int protection, void **destination);

extern uint32_t (*sceKernelLoadStartModule_sys)(const char *, size_t, const void *, uint32_t, void *, void *);

extern void* (*sceKernelLoadStartModuleInternalForMono)(const char *moduleFileName, size_t args, const void *argp, uint32_t flags, void* pOpt, int *pRes);

/* mono-dl-fallback.h */
enum {
    MONO_DL_LAZY  = 1,
    MONO_DL_LOCAL = 2,
    MONO_DL_MASK  = 3
};

/*
 * The "err" variable contents must be allocated using g_malloc or g_strdup
 */
typedef void* (*MonoDlFallbackLoad) (const char *name, int flags, char **err, void *user_data);
typedef void* (*MonoDlFallbackSymbol) (void *handle, const char *name, char **err, void *user_data);
typedef void* (*MonoDlFallbackClose) (void *handle, void *user_data);
 
extern void* (*mono_dl_fallback_register)(MonoDlFallbackLoad load_func, MonoDlFallbackSymbol symbol_func, MonoDlFallbackClose close_func, void *user_data);
extern void (*mono_dl_fallback_unregister)(void* handler);

void* MonoDlLoad(const char *name, int flags, char **err, void *user_data);
void* MonoDlSymbol(void *handle, const char *name, char **err, void *user_data);
void* MonoDlClose(void *handle, void *user_data);

typedef long long unsigned int U64;

#define countof(_array) (sizeof(_array) / sizeof(_array[0]))

#define PAGE_SIZE (16 * 1024)

#define PROT_CPU_READ 1
#define PROT_CPU_WRITE 2
#define PROT_CPU_EXEC 4

#define PROT_GPU_EXEC 8
#define PROT_GPU_READ 16
#define PROT_GPU_WRITE 32

#define PROT_NONE 0
#define PROT_READ PROT_CPU_READ
#define PROT_WRITE PROT_CPU_WRITE
#define PROT_EXEC PROT_CPU_EXEC

#define MAP_SHARED 1
#define MAP_PRIVATE 2
#define MAP_TYPE 0x0f
#define MAP_FIXED 0x10
#define MAP_ANONYMOUS 0x1000
#define MAP_32BIT 0x80000

#define MAP_FAILED (void *)-1

#define MS_SYNC 0x0000
#define MS_ASYNC 0x0001
#define MS_INVALIDATE 0x0002