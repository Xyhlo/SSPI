#include <orbis/libkernel.h>
#include <dirent.h>
#include <stdarg.h>
#include <stdio.h>

char appRoot[0x100] = "\x0";
char baseCon[0x100] = "\x0";
char baseDir[0x100] = "\x0";
char mainExe[0x100] = "\x0";

void* hLog;

void klog(const char* str)
{
    char buff[0x600];
    sprintf(&buff, "[OpenOrbisMono] %s\n", str);
    sceKernelDebugOutText(0, buff);
}

void klogf(const char* str, ...)
{
    char buff[0x600];
    
    va_list arg;
    va_start(arg, str);
    vsprintf(buff, str, arg);
    va_end(arg);
    
    klog(buff);
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

    dp = opendir("/mnt/sandbox/");
    if (dp != 0) {
        while (ep = readdir(dp)) {
            char sbPath[0x100];
            sprintf(&sbPath, "/mnt/sandbox/%s/%s", ep->d_name, MountID);

            if (!direxists(sbPath))
                continue;

            sprintf(path, "/mnt/sandbox/%s", ep->d_name);
            klogf("mount dir found: %s", path);
            break;
        }
        closedir(dp);
    }
}
