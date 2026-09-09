/* GoldHEN SDK command ABI, adapted from GoldHEN/GoldHEN_Plugins_SDK.
 * Copyright (c) 2022 GoldHEN. MIT; see docs/PIPELINE_DEPENDENCIES.md.
 * Only process identification and named PRX loading are used here.
 */
#ifndef GS_GOLDHEN_PROCESS_H
#define GS_GOLDHEN_PROCESS_H
#include <stdint.h>
#include <string.h>
#include <unistd.h>

typedef struct {
    int32_t pid;
    char name[40], path[64], titleid[16], contentid[64], version[6];
    uint64_t base_address;
} __attribute__((packed)) GsGoldHenProcess;

typedef struct {
    char process_name[32], prx_path[100];
    uint64_t result;
} __attribute__((packed)) GsGoldHenPrxLoad;

_Static_assert(sizeof(GsGoldHenProcess) == 202, "GoldHEN process ABI");
_Static_assert(sizeof(GsGoldHenPrxLoad) == 140, "GoldHEN PRX loader ABI");

__attribute__((naked)) static int64_t gs_goldhen_command(uint64_t command, void *data)
{
    __asm__ volatile("mov $500, %eax\nsyscall\njnc 1f\nneg %rax\n1: ret");
}

static int gs_goldhen_has_process_api(void)
{
    int64_t version = gs_goldhen_command(0, NULL);
    return version == 0x100;
}

static int gs_goldhen_is_shell(void)
{
    GsGoldHenProcess info;
    if (!gs_goldhen_has_process_api()) return 0;
    memset(&info, 0, sizeof(info)); info.pid = getpid();
    if (gs_goldhen_command(4, &info) < 0 || info.pid != getpid()) return 0;
    return !memcmp(info.name, "SceShellUI", sizeof("SceShellUI"));
}

static int gs_goldhen_load_shell(const char *path)
{
    GsGoldHenPrxLoad request;
    const char *prefix = "/data/GameSearch/resident/gs_resident_shell_";
    size_t length = path ? strlen(path) : 0;
    if (length <= strlen(prefix) + 4 || length >= sizeof(request.prx_path) ||
        strncmp(path, prefix, strlen(prefix)) || strcmp(path + length - 4, ".prx") ||
        strchr(path + strlen(prefix), '/') || strchr(path, '\\') || strstr(path, "..")) return -1;
    if (!gs_goldhen_has_process_api()) return -2;
    if (access(path, R_OK)) return -3;
    memset(&request, 0, sizeof(request));
    memcpy(request.process_name, "SceShellUI", sizeof("SceShellUI"));
    memcpy(request.prx_path, path, length + 1);
    request.result = UINT64_MAX;
    int64_t rc = gs_goldhen_command(6, &request);
    if (rc < 0) return (int)rc;
    // An unchanged result is not a successful load. Heartbeat is the final acknowledgement.
    return request.result <= INT32_MAX ? (int)request.result : -4;
}
#endif
