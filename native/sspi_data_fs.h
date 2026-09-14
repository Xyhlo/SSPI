#ifndef SSPI_DATA_FS_H
#define SSPI_DATA_FS_H
#include <stdint.h>
#include <fcntl.h>

#if defined(__FreeBSD__) || defined(GS_DATA_FS_TEST)
#ifndef GS_DATA_FS_TEST
#include <orbis/libkernel.h>
#include <unistd.h>
#endif

/* These directories coordinate the application, SceShellUI and GoldHEN FTP.
 * Never accept EEXIST alone: a regular file or link is not a usable directory.
 * Opening first also ensures permission repair cannot follow a symlink. */
static int gs_data_ensure_directory(const char *path, unsigned mode)
{
    if (!path || !*path) return -22;
    (void)sceKernelMkdir(path, mode);
    int fd = sceKernelOpen(path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW, 0);
    if (fd < 0) return fd;
    /* Permission repair is best-effort. A successful open already proved the
     * path exists and is a real directory (O_DIRECTORY/O_NOFOLLOW). A foreign
     * owner, or the GoldHEN plugin thread running before the application's
     * jailbreak, may legitimately refuse fchmod while the directory remains
     * perfectly usable. Never turn that into a hard startup failure. */
    (void)sceKernelFchmod(fd, mode);
    sceKernelClose(fd);
    return 0;
}

static int gs_data_prepare_root(void)
{
    return gs_data_ensure_directory("/data/SSPI", 0777);
}
#endif
#endif
