#ifndef GS_MODULE_INVENTORY_H
#define GS_MODULE_INVENTORY_H
#include <stddef.h>

_Static_assert(sizeof(OrbisKernelModule) == 4, "dynlib module handle ABI");
_Static_assert(sizeof(OrbisKernelModuleInfo) == 352, "dynlib module information ABI");
_Static_assert(offsetof(OrbisKernelModuleInfo, segmentInfo) == 264, "dynlib segments ABI");
_Static_assert(offsetof(OrbisKernelModuleInfo, segmentCount) == 328, "dynlib segment count ABI");

/* Native dynlib inventory, as used by ps4-payload-sdk module.c (592/593).
 * ShellUI's public sceKernelGetModuleList can report only eboot.bin, omitting
 * even the calling PRX. That list cannot establish process-wide ownership. */
#ifndef GS_DYNLIB_CALL
__attribute__((naked)) static GsGoldHenCall gs_dynlib_call(uint64_t number,
    uintptr_t first, uintptr_t second, uintptr_t third)
{
    __asm__ volatile("mov %rcx, %r10\nmov $0, %eax\nsyscall\nsetc %dl\nmovzbl %dl, %edx\nret");
}
#define GS_DYNLIB_CALL gs_dynlib_call
#endif

static int gs_native_module_list(OrbisKernelModule *modules, size_t capacity, size_t *count)
{
    if (!modules || !count || !capacity || capacity > INT32_MAX) return -1;
    *count = 0;
    GsGoldHenCall call = GS_DYNLIB_CALL(592, (uintptr_t)modules, capacity, (uintptr_t)count);
    if (call.carry || call.raw) return call.raw ? (int)gs_goldhen_call_result(call) : -1;
    if (*count > capacity) return -1;
    return 0;
}

static int gs_native_module_info(OrbisKernelModule module, OrbisKernelModuleInfo *info)
{
    if (!info) return -1;
    memset(info, 0, sizeof(*info)); info->size = sizeof(*info);
    GsGoldHenCall call = GS_DYNLIB_CALL(593, (uintptr_t)(uint32_t)module, (uintptr_t)info, 0);
    return call.carry && !call.raw ? -1 : (int)gs_goldhen_call_result(call);
}
#endif
