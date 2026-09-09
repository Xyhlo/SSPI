#include <orbis/libkernel.h>
#include <string.h>
#include "module.h"

//stolen from https://github.com/bucanero/apollo-ps4/blob/a893147a54ad7b9c610bf06c4a1b5ee8af5067d9/source/orbis_jbc.c

#define countof(_array) (sizeof(_array) / sizeof(_array[0]))
#define SYS_dynlib_get_info 593

asm("orbis_syscall:\n"
    "movq $0, %rax\n"
    "movq %rcx, %r10\n"
    "syscall\n"
    "jb err\n"
    "retq\n"
    "err:\n"
    "pushq %rax\n"
    "callq __error\n"
    "popq %rcx\n"
    "movl %ecx, 0(%rax)\n"
    "movq $0xFFFFFFFFFFFFFFFF, %rax\n"
    "movq $0xFFFFFFFFFFFFFFFF, %rdx\n"
    "retq\n");
int orbis_syscall(int num, ...);

int _sceKernelGetModuleInfo(OrbisKernelModule handle, OrbisKernelModuleInfo* info)
{
    if (!info)
        return ORBIS_KERNEL_ERROR_EFAULT;

    memset(info, 0, sizeof(*info));
    info->size = sizeof(*info);

    return orbis_syscall(SYS_dynlib_get_info, handle, info);
}

int sceKernelGetModuleInfoByName(const char* name, OrbisKernelModuleInfo* info)
{
    OrbisKernelModuleInfo tmpInfo;

    OrbisKernelModule handles[256];
    size_t numModules;
    int ret;

    if (!name || !info)
        return ORBIS_KERNEL_ERROR_EFAULT;

    memset(handles, 0, sizeof(handles));

    ret = sceKernelGetModuleList(handles, countof(handles), &numModules);
    if (ret) {
        return ret;
    }

    for (size_t i = 0; i < numModules; ++i) {
        ret = _sceKernelGetModuleInfo(handles[i], &tmpInfo);
        if (ret)
			continue;

        if (strcmp(tmpInfo.name, name) == 0) {
            memcpy(info, &tmpInfo, sizeof(tmpInfo));
            return 0;
        }
    }

    return ORBIS_KERNEL_ERROR_ENOENT;
}

int get_module_base(const char* name, uint64_t* base, size_t* size)
{
    OrbisKernelModuleInfo moduleInfo;
    int ret;

    ret = sceKernelGetModuleInfoByName(name, &moduleInfo);
    if (ret) {
        return 0;
    }

    if (base)
        *base = (uint64_t)moduleInfo.segmentInfo[0].address;

    if (size)
        *size = moduleInfo.segmentInfo[0].size;

    return 1;
}
