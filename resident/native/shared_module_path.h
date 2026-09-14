#ifndef GS_SHARED_MODULE_PATH_H
#define GS_SHARED_MODULE_PATH_H

#include <stddef.h>
#include <fcntl.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

/* Resolve only an existing, byte-identical /user alias of a staged SSPI module. */
static int gs_shared_module_path(const char *path, char *output, size_t capacity)
{
    const char prefix[] = "/data/SSPI/resident/gs_resident_shell_";
    char alias[100];
    unsigned char original_bytes[4096], alias_bytes[4096];
    struct stat original_stat, alias_stat;
    size_t length = 0;
    int original = -1, shared = -1, match = 0;

    if (!output || !capacity) return 0;
    if (!path) goto done;
    while (length < sizeof(alias) && path[length]) length++;
    if (length <= sizeof(prefix) - 1 + 4 || length + 6 > sizeof(alias) ||
        length + 6 > capacity || strncmp(path, prefix, sizeof(prefix) - 1) ||
        strcmp(path + length - 4, ".prx") ||
        strchr(path + sizeof(prefix) - 1, '/') || strchr(path, '\\') ||
        strstr(path, "..")) goto done;

    memcpy(alias, "/user", 5);
    memcpy(alias + 5, path, length + 1);
    /* Nonblocking open lets fstat reject a FIFO without waiting for a writer. */
    original = open(path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK);
    if (original < 0) goto done;
    shared = open(alias, O_RDONLY | O_NOFOLLOW | O_NONBLOCK);
    if (shared < 0) goto done;
    if (fstat(original, &original_stat) || fstat(shared, &alias_stat) ||
        !S_ISREG(original_stat.st_mode) || !S_ISREG(alias_stat.st_mode) ||
        original_stat.st_size < 1 || original_stat.st_size > 16 * 1024 * 1024 ||
        alias_stat.st_size != original_stat.st_size) goto done;

    size_t remaining = (size_t)original_stat.st_size;
    while (remaining) {
        size_t amount = remaining < sizeof(original_bytes) ? remaining : sizeof(original_bytes);
        if (read(original, original_bytes, amount) != (ssize_t)amount ||
            read(shared, alias_bytes, amount) != (ssize_t)amount ||
            memcmp(original_bytes, alias_bytes, amount)) goto done;
        remaining -= amount;
    }
    /* Reject files that grew after fstat rather than comparing only a prefix. */
    if (read(original, original_bytes, 1) != 0 || read(shared, alias_bytes, 1) != 0) goto done;
    match = 1;

done:
    if (shared >= 0 && close(shared)) match = 0;
    if (original >= 0 && close(original)) match = 0;
    if (match) memcpy(output, alias, length + 6);
    else output[0] = 0;
    return match;
}

/* Format-only /user alias for a canonical shell module path.
 * The caller's own namespace may not expose /user at all (the application
 * sandbox maps the user partition at /data), while the load target,
 * SceShellUI, reaches that same storage at /user/data. The alias must
 * therefore never be gated on a local open() succeeding; the load target
 * resolves the path in its own namespace. Security checks below only reject
 * malformed paths. */
static int gs_shared_module_alias(const char *path, char *output, size_t capacity)
{
    const char prefix[] = "/data/SSPI/resident/gs_resident_shell_";
    size_t length = 0;
    if (!output || capacity < 2 || !path) return 0;
    output[0] = 0;
    while (path[length]) length++;
    if (length <= sizeof(prefix) - 1 + 4 || length + 5 >= capacity ||
        length + 5 >= 100 || strncmp(path, prefix, sizeof(prefix) - 1) ||
        strcmp(path + length - 4, ".prx") ||
        strchr(path + sizeof(prefix) - 1, '/') || strchr(path, '\\') ||
        strstr(path, "..")) return 0;
    memcpy(output, "/user", 5);
    memcpy(output + 5, path, length + 1);
    return 1;
}

#endif
