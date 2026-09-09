#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <errno.h>
#include <fcntl.h>
#include <unistd.h>
#include <sys/types.h>
#include <sys/stat.h>
#include <sys/statfs.h>
#include <sys/mount.h>
#include <sys/uio.h>
#include <orbis/SystemService.h>
#include <orbis/UserService.h>
#include <orbis/libkernel.h>
#include <sys/file.h>
#include "goldhen_process.h"
#include "storage_space.h"

__attribute__((visibility("default"))) int gs_resident_load_shell_worker(const char *path)
{
    int lock = open("/data/GameSearch/resident/shell-load.lock", O_CREAT | O_RDWR, 0600);
    if (lock < 0) return -5;
    if (flock(lock, LOCK_EX | LOCK_NB)) { close(lock); return -6; }
    int result = gs_goldhen_load_shell(path);
    flock(lock, LOCK_UN); close(lock);
    return result;
}

#define GS_MNT_UPDATE 0x00010000

__attribute__((visibility("default"))) int64_t gs_resident_available_bytes(const char *path)
{
    return gs_storage_available_bytes(path);
}

int32_t sceLncUtilInitialize(void);
int32_t sceLncUtilGetAppId(const char* title_id);

static int launch_title(const char* title_id, uint32_t user_id, int flags)
{
    LncAppParam param;
    memset(&param, 0, sizeof(param));
    param.size = sizeof(param);
    param.user_id = user_id != 0 ? user_id : (uint32_t)-1;
    param.app_opt = 0;
    param.crash_report = 0;
    param.LaunchAppCheck_flag = (enum LaunchApp_Flag)flags;
    return sceLncUtilLaunchApp(title_id, 0, &param);
}

__attribute__((naked)) static int gs_nmount(struct iovec* iov, unsigned int count, int flags)
{
    __asm__ volatile("mov $378, %rax\nmov %rcx, %r10\nsyscall\nret");
}

__attribute__((visibility("default"))) int gs_resident_mount_system(void)
{
    char empty[] = "";
    struct iovec iov[] = {
        { "fstype", sizeof("fstype") }, { "exfatfs", sizeof("exfatfs") },
        { "fspath", sizeof("fspath") }, { "/system", sizeof("/system") },
        { "from", sizeof("from") }, { "/dev/da0x4.crypt", sizeof("/dev/da0x4.crypt") },
        { "large", sizeof("large") }, { "yes", sizeof("yes") },
        { "timezone", sizeof("timezone") }, { "static", sizeof("static") },
        { "async", sizeof("async") }, { empty, sizeof(empty) },
        { "ignoreacl", sizeof("ignoreacl") }, { empty, sizeof(empty) },
        { "dirmask", sizeof("dirmask") }, { "511", sizeof("511") },
        { "mask", sizeof("mask") }, { "511", sizeof("511") }
    };
    return gs_nmount(iov, sizeof(iov) / sizeof(iov[0]), GS_MNT_UPDATE);
}

__attribute__((visibility("default"))) int gs_resident_mount_system_data(void)
{
    char empty[] = "";
    struct iovec iov[] = {
        { "fstype", sizeof("fstype") }, { "ufs", sizeof("ufs") },
        { "fspath", sizeof("fspath") }, { "/system_data", sizeof("/system_data") },
        { "from", sizeof("from") }, { "/dev/da0x9.crypt", sizeof("/dev/da0x9.crypt") },
        { "large", sizeof("large") }, { "yes", sizeof("yes") },
        { "async", sizeof("async") }, { empty, sizeof(empty) }
    };
    return gs_nmount(iov, sizeof(iov) / sizeof(iov[0]), GS_MNT_UPDATE);
}

__attribute__((visibility("default"))) int gs_resident_initialize(void)
{
    return sceLncUtilInitialize();
}

__attribute__((visibility("default"))) int gs_resident_launch(uint32_t user_id)
{
    return launch_title("SRCHD0001", user_id, LaunchApp_None);
}

__attribute__((visibility("default"))) int gs_resident_launch_skip(uint32_t user_id)
{
    return launch_title("SRCHD0001", user_id, LaunchApp_SkipLaunch);
}

__attribute__((visibility("default"))) int gs_resident_system_launch(uint32_t user_id)
{
    int (*launch)(const char*, const char**, LncAppParam*) = 0;
    LncAppParam param;
    int lib;
    memset(&param, 0, sizeof(param));
    param.size = sizeof(param);
    param.user_id = user_id != 0 ? user_id : (uint32_t)-1;
    param.app_opt = 0;
    param.crash_report = 0;
    param.LaunchAppCheck_flag = LaunchApp_SkipLaunch;
    lib = sceKernelLoadStartModule("/system/common/lib/libSceSystemService.sprx", 0, 0, 0, 0, 0);
    if (lib & 0x80000000)
        lib = sceKernelLoadStartModule("libSceSystemService.sprx", 0, 0, 0, 0, 0);
    sceKernelDlsym(lib, "sceSystemServiceLaunchApp", (void**)&launch);
    if (!launch) return -2;
    return launch("SRCHD0001", 0, &param);
}

__attribute__((visibility("default"))) int gs_resident_launch_title(const char* title_id, uint32_t user_id)
{
    if (!title_id || !title_id[0]) return -22;
    return launch_title(title_id, user_id, LaunchApp_None);
}

__attribute__((visibility("default"))) int gs_resident_get_app_id(void)
{
    return sceLncUtilGetAppId("SRCHD0001");
}

__attribute__((visibility("default"))) int gs_resident_get_app_id_of(const char* title_id)
{
    if (!title_id || !title_id[0]) return -1;
    return sceLncUtilGetAppId(title_id);
}

__attribute__((visibility("default"))) int gs_resident_stop(void)
{
    int app_id = sceLncUtilGetAppId("SRCHD0001");
    if (((uint32_t)app_id & 0xFF000000U) != 0x60000000U) return 0;
    return sceSystemServiceKillApp((uint32_t)app_id, -1, 0, 0);
}

__attribute__((visibility("default"))) int gs_resident_stop_title(const char* title_id)
{
    int app_id;
    if (!title_id || !title_id[0]) return -22;
    app_id = sceLncUtilGetAppId(title_id);
    if (((uint32_t)app_id & 0xFF000000U) != 0x60000000U) return 0;
    return sceSystemServiceKillApp((uint32_t)app_id, -1, 0, 0);
}

__attribute__((visibility("default"))) int gs_resident_get_user_id(void)
{
    OrbisUserServiceLoginUserIdList list;
    int32_t user = 0;
    int i;
    memset(&list, 0, sizeof(list));
    if (sceUserServiceGetForegroundUser(&user) == 0 &&
        user != 0 && user != -1 && user != (int32_t)0xFF)
        return user;
    if (sceUserServiceGetLoginUserIdList(&list) == 0)
    {
        for (i = 0; i < ORBIS_USER_SERVICE_MAX_LOGIN_USERS; i++)
        {
            user = list.userId[i];
            if (user != 0 && user != -1 && user != (int32_t)0xFF)
                return user;
        }
    }
    user = 0;
    if (sceUserServiceGetInitialUser(&user) == 0 &&
        user != 0 && user != -1 && user != (int32_t)0xFF)
        return user;
    return 0;
}

__attribute__((visibility("default"))) int gs_resident_spawn_local(const char* eboot_path)
{
    int (*add_local)(int, const char*, int, const char**) = 0;
    int lib;
    int app_id;
    struct stat st;
    const char* args[2];
    if (!eboot_path || !eboot_path[0]) return -22;
    if (stat(eboot_path, &st) != 0) return -10000 - errno;
    if (!S_ISREG(st.st_mode) || st.st_size <= 0) return -21;
    args[0] = "--GameSearchResident";
    args[1] = 0;
    lib = sceKernelLoadStartModule("/system/common/lib/libSceSystemService.sprx", 0, 0, 0, 0, 0);
    if (lib & 0x80000000)
        lib = sceKernelLoadStartModule("libSceSystemService.sprx", 0, 0, 0, 0, 0);
    sceKernelDlsym(lib, "sceSystemServiceAddLocalProcess", (void**)&add_local);
    if (!add_local) return -2;
    app_id = sceSystemServiceGetAppIdOfBigApp();
    if (((uint32_t)app_id & 0xFF000000U) != 0x60000000U)
        app_id = sceSystemServiceGetAppIdOfMiniApp();
    if (((uint32_t)app_id & 0xFF000000U) != 0x60000000U)
        return -1;
    return add_local(app_id, eboot_path, 0, args);
}

__attribute__((visibility("default"))) int gs_resident_mkdir(const char* path)
{
    if (!path || !path[0]) return -22;
    if (mkdir(path, 0777) == 0 || errno == EEXIST) return 0;
    return -errno;
}

__attribute__((visibility("default"))) int gs_resident_chmod(const char* path, int mode)
{
    if (!path || !path[0]) return -22;
    if (chmod(path, (mode_t)mode) == 0) return 0;
    return -errno;
}

__attribute__((visibility("default"))) int gs_resident_unlink(const char* path)
{
    if (!path || !path[0]) return -22;
    if (unlink(path) == 0 || errno == ENOENT) return 0;
    return -errno;
}

__attribute__((visibility("default"))) int gs_resident_copy_file(const char* src, const char* dst)
{
    char buf[64 * 1024];
    int in;
    int out;
    ssize_t n;
    if (!src || !dst) return -22;
    in = open(src, O_RDONLY);
    if (in < 0) return -10000 - errno;
    unlink(dst);
    out = open(dst, O_CREAT | O_TRUNC | O_WRONLY, 0777);
    if (out < 0)
    {
        int err = errno;
        close(in);
        return -20000 - err;
    }
    while ((n = read(in, buf, sizeof(buf))) > 0)
    {
        ssize_t off = 0;
        while (off < n)
        {
            ssize_t w = write(out, buf + off, (size_t)(n - off));
            if (w <= 0)
            {
                int err = errno;
                close(in);
                close(out);
                return -30000 - err;
            }
            off += w;
        }
    }
    if (n < 0)
    {
        int err = errno;
        close(in);
        close(out);
        return -40000 - err;
    }
    close(in);
    close(out);
    chmod(dst, 0777);
    return 0;
}

/* Same portable SHA-256 implementation used by the resident downloader. Keeps
 * large update hashing outside Mono's interpreter; no extra file read. */
#include <stdlib.h>
#include "../plugin/crypto/sha256.c"
__attribute__((visibility("default"))) void *gs_sha256_create(void)
{ SHA256_CTX *p = (SHA256_CTX *)malloc(sizeof(*p)); if (p) sha256_init(p); return p; }
__attribute__((visibility("default"))) void gs_sha256_reset(void *p) { sha256_init((SHA256_CTX *)p); }
__attribute__((visibility("default"))) void gs_sha256_update(void *p, const void *data, size_t count) { sha256_update((SHA256_CTX *)p, data, count); }
__attribute__((visibility("default"))) void gs_sha256_finish(void *p, unsigned char *result) { sha256_final((SHA256_CTX *)p, result); }
__attribute__((visibility("default"))) void gs_sha256_free(void *p) { free(p); }
