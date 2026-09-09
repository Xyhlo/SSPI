#ifndef SSPI_STORAGE_SPACE_H
#define SSPI_STORAGE_SPACE_H
#include <stdint.h>
#include <errno.h>
#include <fcntl.h>
#include <unistd.h>
#include <sys/statfs.h>

static int64_t gs_storage_available_bytes(const char *path)
{
    if (!path || !*path) { errno = EINVAL; return -1; }
    int fd = open(path, O_RDONLY);
    if (fd < 0) return -1;
    struct statfs info = {0};
    // The bundled OpenOrbis statfs(path) returns fd, discarding fstatfs's result.
    int result = fstatfs(fd, &info);
    int saved_errno = errno;
    close(fd);
    if (result != 0) { errno = saved_errno; return -1; }
    if (info.f_bavail < 0) return 0;
    if (info.f_bsize == 0 || (uint64_t)info.f_bavail > INT64_MAX / info.f_bsize)
    { errno = EOVERFLOW; return -1; }
    return (int64_t)((uint64_t)info.f_bavail * info.f_bsize);
}
#endif
