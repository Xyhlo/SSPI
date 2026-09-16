#pragma once

// Included only when compiling the decoder's file implementation for PS4.
#include "../../SDK/vendor/unrar/rar.hpp"
#include <orbis/libkernel.h>

extern "C" int gs_rar_io_poll(const char *stage);
extern "C" int gs_rar_input_allowed(const char *path);

static int64_t gs_rar_io_result(int64_t result)
{
    if (result < 0) {
        int code = (unsigned int)result & 0xffff;
        errno = code > 0 && code < 4096 ? code : EIO;
        return -1;
    }
    return result;
}

static int gs_rar_open(const char *path, int flags, ...)
{
    if (flags != O_RDONLY || !gs_rar_input_allowed(path)) { errno = EACCES; return -1; }
    if (gs_rar_io_poll("file-open-before")) { errno = EINTR; return -1; }
    int fd = (int)gs_rar_io_result(sceKernelOpen(path, O_RDONLY, 0));
    if (gs_rar_io_poll("file-open-after") && fd >= 0) { sceKernelClose(fd); errno = EINTR; return -1; }
    return fd;
}

static ssize_t gs_rar_read(int fd, void *data, size_t size)
{
    if (gs_rar_io_poll("file-read")) { errno = EINTR; return -1; }
    return (ssize_t)gs_rar_io_result((int64_t)sceKernelRead(fd, data, size));
}

static off_t gs_rar_seek(int fd, off_t offset, int whence)
{
    if (gs_rar_io_poll("file-seek")) { errno = EINTR; return -1; }
    return (off_t)gs_rar_io_result(sceKernelLseek(fd, offset, whence));
}

static int gs_rar_close(int fd) { return (int)gs_rar_io_result(sceKernelClose(fd)); }
// Input paths were admitted as regular archive files; do not probe the shell TTY.
static int gs_rar_isatty(int) { return 0; }

#define open gs_rar_open
#define read gs_rar_read
#define lseek gs_rar_seek
#define close gs_rar_close
#define isatty gs_rar_isatty
