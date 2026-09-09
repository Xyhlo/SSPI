#include "module_loader.h"
#include "jailbreak_man.h"
#include "module.h"
#include "mono.h"
#include "io.h"

void* (*mono_domain_create)();
void (*mono_set_dirs)(const char* lib, const char* etc);
void* (*mono_jit_init)(const char* domain_name);
void* (*mono_init_from_assembly)(const char* domain_name, const char* filename);
void* (*mono_get_root_domain)(void);
void* (*mono_domain_assembly_open)(void* domain, const char *assembly_path);
void* (*mono_class_from_name)(void* image, const char* class_namespace, const char* class_name);
void* (*mono_class_get_method_from_name)(void* method_class, const char *method_name, int param_count);
void* (*mono_runtime_invoke)(void* method, void* obj, void** params, void** exc);
void* (*mono_method_get_unmanaged_thunk)(void* method);
void* (*mono_jit_cleanup)(void* domain);
void* (*mono_thread_attach)(void* domain);
void* (*mono_assembly_get_image)(void* assembly);

int   (*mono_jit_exec)(void* domain, void* assembly, int argc, char* argv[]);
void* (*mono_assembly_load_from_full)(void* image, int* fname, int* status, int refonly);
void* (*mono_add_internal_call)(const char* methodPath, void* func);
void (*mono_debugger_agent_parse_options)(char* debug_options);

void (*mono_debug_init)(int debug_format);
void (*mono_debug_domain_create)(void* domain);
void (*mono_jit_parse_options)(int arc, char* argv[]);
void (*mono_debug_open_image_from_memory)(void* image, const char* raw_contents, int size);
void* (*mono_image_open_from_data_with_name)(char *data, unsigned int data_len, int need_copy, int* status, int refonly, const char *name);

int (*JitCreateSharedMemory)(int flags, size_t size, int protection, void** destinationHandle);
int (*JitCreateAliasOfSharedMemory)(int handle, int protection, void** destinationHandle);
int (*JitMapSharedMemory)(int handle, int protection, void **destination);

uint32_t (*sceKernelLoadStartModule_sys)(const char *, size_t, const void *, uint32_t, void *, void *);

void* (*sceKernelLoadStartModuleInternalForMono)(const char *moduleFileName, size_t args, const void *argp, uint32_t flags, void* pOpt, int *pRes);


void (*mono_trace_set_log_handler)(void* callback, void* user_data);

void* (*mono_dl_fallback_register)(MonoDlFallbackLoad load_func, MonoDlFallbackSymbol symbol_func, MonoDlFallbackClose close_func, void *user_data);
void (*mono_dl_fallback_unregister)(void* handler);