#include <orbis/libkernel.h>
#include <dirent.h>
#include <stdarg.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include "sspi_log.h"

char appRoot[0x100] = "\x0";
char baseCon[0x100] = "\x0";
char baseDir[0x100] = "\x0";
char mainExe[0x100] = "\x0";

void* hLog;

void klog(const char* str)
{
    char buff[0x600];
    snprintf(buff, sizeof(buff), "[OpenOrbisMono] %s\n", str ? str : "");
    sceKernelDebugOutText(0, buff);
}

void klogf(const char* str, ...)
{
    char buff[0x600];
    
    va_list arg;
    va_start(arg, str);
    vsnprintf(buff, sizeof(buff), str, arg);
    va_end(arg);
    
    klog(buff);
}

void boot_stage(const char* stage, int code)
{
    klogf("boot %s 0x%08x", stage, (unsigned int)code);
    gs_log_write("startup", "bootstrap stage=%s code=0x%08x", stage, (unsigned int)code);
}

int direxists(const char* path)
{
    void* dp = opendir(path);
    if (dp == 0)
        return 0;
    closedir(dp);
    return 1;
}

int file_exists(const char* path)
{
    FILE* fp;
    if (path == 0 || path[0] == 0)
        return 0;
    fp = fopen(path, "rb");
    if (fp == 0)
        return 0;
    fclose(fp);
    return 1;
}

void findAppMount(char* path)
{
    void* dp;
    struct dirent* ep;

    char* MountID = sceKernelGetFsSandboxRandomWord();

    path[0] = 0;
    if (!MountID || !*MountID) return;

    dp = opendir("/mnt/sandbox/");
    if (dp != 0) {
        while ((ep = readdir(dp)) != NULL) {
            char sbPath[0x100];
            if (snprintf(sbPath, sizeof(sbPath), "/mnt/sandbox/%s/%s", ep->d_name, MountID) >= sizeof(sbPath))
                continue;

            if (!direxists(sbPath))
                continue;

            snprintf(path, sizeof(appRoot), "/mnt/sandbox/%s", ep->d_name);
            klogf("mount dir found: %s", path);
            break;
        }
        closedir(dp);
    }
}
