#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <wchar.h>
#include <errno.h>
#include <fcntl.h>
#include <archive.h>
#include <archive_entry.h>
#include "gs_unrar.h"
#ifdef GS_7Z_HOST_TEST
#include <io.h>
#define open _open
#define close _close
#define fdopen _fdopen
#define fsync _commit
#define fileno _fileno
#define unlink _unlink
#define O_NOFOLLOW 0
#define SEVEN_BINARY _O_BINARY
#define seven_seek_file _fseeki64
#define seven_tell_file _ftelli64
static int64_t available_space = INT64_MAX;
void gs_7z_test_space(int64_t space) { available_space = space; }
static int64_t gs_storage_available_bytes(const char *path) { (void)path; return available_space; }
#else
#include <unistd.h>
#include "../native/storage_space.h"
#define SEVEN_BINARY 0
#define seven_seek_file fseeko
#define seven_tell_file ftello
#endif

#define SEVEN_MEMORY_LIMIT (128U * 1024U * 1024U)
#define SEVEN_EXPANDED_LIMIT (512ULL * 1024ULL * 1024ULL * 1024ULL)
typedef union { size_t size; long double alignment; } SevenAllocation;
static size_t memory_used, memory_peak;
static int memory_exhausted;
static volatile int extracting;
static char last_error[256];

/* All allocations in the pinned libarchive/liblzma objects use these wrappers.
 * The resident owns one extraction slot, including solid-folder decoding. */
void *gs_7z_malloc(size_t size)
{
    if (size > SEVEN_MEMORY_LIMIT - sizeof(SevenAllocation)) { memory_exhausted = 1; errno = ENOMEM; return NULL; }
    size += sizeof(SevenAllocation);
    if (size > SEVEN_MEMORY_LIMIT - memory_used) { memory_exhausted = 1; errno = ENOMEM; return NULL; }
    SevenAllocation *block = malloc(size);
    if (!block) { memory_exhausted = 1; return NULL; }
    block->size = size; memory_used += size;
    if (memory_used > memory_peak) memory_peak = memory_used;
    return block + 1;
}
void gs_7z_free(void *pointer)
{
    if (!pointer) return;
    SevenAllocation *block = (SevenAllocation *)pointer - 1;
    memory_used -= block->size; free(block);
}
void *gs_7z_calloc(size_t count, size_t size)
{
    if (size && count > SEVEN_MEMORY_LIMIT / size) { memory_exhausted = 1; errno = ENOMEM; return NULL; }
    void *pointer = gs_7z_malloc(count * size);
    if (pointer) memset(pointer, 0, count * size);
    return pointer;
}
void *gs_7z_realloc(void *pointer, size_t size)
{
    if (!pointer) return gs_7z_malloc(size);
    SevenAllocation *block = (SevenAllocation *)pointer - 1;
    size_t old = block->size;
    if (size > SEVEN_MEMORY_LIMIT - sizeof(SevenAllocation)) { memory_exhausted = 1; errno = ENOMEM; return NULL; }
    size += sizeof(SevenAllocation);
    if (size > SEVEN_MEMORY_LIMIT - (memory_used - old)) { memory_exhausted = 1; errno = ENOMEM; return NULL; }
    block = realloc(block, size);
    if (!block) { memory_exhausted = 1; return NULL; }
    block->size = size; memory_used = memory_used - old + size;
    if (memory_used > memory_peak) memory_peak = memory_used;
    return block + 1;
}
char *gs_7z_strdup(const char *text)
{
    size_t bytes = strlen(text) + 1; char *copy = gs_7z_malloc(bytes);
    if (copy) memcpy(copy, text, bytes);
    return copy;
}
wchar_t *gs_7z_wcsdup(const wchar_t *text)
{
    size_t count = wcslen(text) + 1;
    if (count > SEVEN_MEMORY_LIMIT / sizeof(wchar_t)) return NULL;
    wchar_t *copy = gs_7z_malloc(count * sizeof(wchar_t));
    if (copy) memcpy(copy, text, count * sizeof(wchar_t));
    return copy;
}
const char *gs_7z_last_error(void) { return last_error; }
size_t gs_7z_memory_peak(void) { return memory_peak; }

typedef struct {
    FILE *input;
    int64_t size, processed;
    int canceled;
    GsArchiveProgress progress;
    unsigned char buffer[128 * 1024];
} SevenInput;
static int seven_canceled(SevenInput *input)
{
    if (input->progress && input->progress(input->processed, 0)) input->canceled = 1;
    return input->canceled;
}
static la_ssize_t seven_read(struct archive *archive, void *opaque, const void **buffer)
{
    SevenInput *input = opaque; (void)archive;
    if (seven_canceled(input)) return -1;
    *buffer = input->buffer;
    size_t count = fread(input->buffer, 1, sizeof(input->buffer), input->input);
    return ferror(input->input) ? -1 : (la_ssize_t)count;
}
static la_int64_t seven_seek(struct archive *archive, void *opaque, la_int64_t offset, int whence)
{
    SevenInput *input = opaque; (void)archive;
    if (seven_canceled(input) || seven_seek_file(input->input, offset, whence)) return -1;
    return seven_tell_file(input->input);
}
static int seven_name(const char *name)
{
    if (!name || !*name || strlen(name) > 1024 || *name == '/' || *name == '\\') return 0;
    const char *part = name;
    for (const char *p = name;; p++) {
        if (*p && ((unsigned char)*p < 32 || *p == ':' || *p == 127)) return 0;
        if (*p && *p != '/' && *p != '\\') continue;
        size_t length = (size_t)(p - part);
        if ((!length && *p) || (length == 1 && part[0] == '.') ||
            (length == 2 && part[0] == '.' && part[1] == '.')) return 0;
        if (!*p) return 1;
        part = p + 1;
    }
}
static void seven_error(int code, const char *detail)
{
    const char *message = detail;
    if (!message || !*message) {
        switch (code) {
            case 3005: message = "Encrypted 7z archives are not supported; original retained"; break;
            case 3006: message = "7z decoder exceeds the 128 MiB memory limit; original retained"; break;
            case 3007: message = "Not enough free space for the extracted package; original retained"; break;
            case 3008: message = "7z extraction canceled; original retained"; break;
            case 3009: message = "7z output already exists or cannot be created; original retained"; break;
            case 3012: message = "7z archive contains no PKG files"; break;
            default: message = "7z extraction failed; archive or compression method is invalid or unsupported"; break;
        }
    }
    snprintf(last_error, sizeof(last_error), "%s", message);
    for (char *p = last_error; *p; p++) if ((unsigned char)*p < 32) *p = ' ';
}
static int seven_extract(const char *first, const char *destination,
    const char *const *names, const char *const *paths, int volume_count,
    GsExtractedPkg *packages, int capacity, GsArchiveProgress progress, char *error, int error_capacity)
{
    (void)names; (void)paths;
    if (error && error_capacity > 0) error[0] = 0;
    if (!first || !packages || capacity < 1 || volume_count != 1 || __sync_lock_test_and_set(&extracting, 1)) {
        if (error && error_capacity > 0) snprintf(error, error_capacity, "Invalid 7z input or another extraction is active");
        return -3001;
    }
    memory_used = memory_peak = 0; memory_exhausted = 0; last_error[0] = 0;
    int result = -3002, count = 0, entries = 0, owned = 0, reserved = 0;
    char partial[1100] = {0}; FILE *output = NULL;
    SevenInput *input = NULL; unsigned char *buffer = NULL;
    struct archive *archive = NULL; char **seen = NULL; uint64_t expanded = 0;
    input = gs_7z_calloc(1, sizeof(*input)); buffer = gs_7z_malloc(128 * 1024);
    if (!input || !buffer) { result = -3006; goto done; }
    input->progress = progress;
    int fd = open(first, O_RDONLY | O_NOFOLLOW | SEVEN_BINARY);
    if (fd < 0) goto done;
    input->input = fdopen(fd, "rb"); if (!input->input) { close(fd); goto done; }
    if (seven_seek_file(input->input, 0, SEEK_END) || (input->size = seven_tell_file(input->input)) < 32 ||
        seven_seek_file(input->input, 0, SEEK_SET)) { result = -3003; goto done; }
    archive = archive_read_new(); seen = gs_7z_calloc(4096, sizeof(*seen));
    if (!archive || !seen) { result = -3006; goto done; }
    if (archive_read_support_filter_none(archive) != ARCHIVE_OK ||
        archive_read_support_format_7zip(archive) != ARCHIVE_OK ||
        archive_read_set_seek_callback(archive, seven_seek) != ARCHIVE_OK ||
        archive_read_open2(archive, input, NULL, seven_read, NULL, NULL) != ARCHIVE_OK) { result = -3003; goto done; }
    for (;;) {
        struct archive_entry *entry = NULL;
        if (seven_canceled(input)) { result = -3008; goto done; }
        int status = archive_read_next_header(archive, &entry);
        if (status == ARCHIVE_EOF) { result = count ? count : -3012; break; }
        if (status != ARCHIVE_OK) { result = -3010; goto done; }
        if (archive_entry_is_encrypted(entry) || archive_read_has_encrypted_entries(archive) > 0) { result = -3005; goto done; }
        const char *name = archive_entry_pathname_utf8(entry);
        if (!name) name = archive_entry_pathname(entry);
        int type = archive_entry_filetype(entry);
        if (entries >= 4096 || !seven_name(name) || archive_entry_symlink(entry) || archive_entry_hardlink(entry) ||
            (type != AE_IFREG && type != AE_IFDIR) || !archive_entry_size_is_set(entry) || archive_entry_size(entry) < 0) { result = -3004; goto done; }
        uint64_t expected = (uint64_t)archive_entry_size(entry), written = 0;
        if (expected > SEVEN_EXPANDED_LIMIT - expanded) { result = -3006; goto done; }
        expanded += expected;
        char *key = gs_7z_strdup(name); if (!key) { result = -3006; goto done; }
        for (char *p = key; *p; p++) { if (*p == '\\') *p = '/'; else if (*p >= 'A' && *p <= 'Z') *p += 'a' - 'A'; }
        for (int i = 0; i < entries; i++) if (!strcmp(seen[i], key)) { gs_7z_free(key); result = -3004; goto done; }
        seen[entries++] = key;
        size_t length = strlen(key); int package = type == AE_IFREG && length >= 4 && !strcmp(key + length - 4, ".pkg");
        if (package) {
            if (count >= capacity || count >= 256 || expected < 4) { result = -3004; goto done; }
            if (!destination) {
                if (strlen(name) >= sizeof(packages[count].path)) { result = -3004; goto done; }
                snprintf(packages[count].path, sizeof(packages[count].path), "%s", name);
                packages[count++].size = (int64_t)expected;
            } else {
            int64_t space = gs_storage_available_bytes(destination);
            if (space < 0 || (uint64_t)space < expected + 64 * 1024 * 1024) { result = -3007; goto done; }
            int n = snprintf(packages[count].path, sizeof(packages[count].path), "%s/pkg-%03d.pkg", destination, count + 1);
            if (n < 0 || (size_t)n >= sizeof(packages[count].path)) { result = -3004; goto done; }
            FILE *existing = fopen(packages[count].path, "rb");
            if (existing) { fclose(existing); result = -3009; goto done; }
            snprintf(partial, sizeof(partial), "%s.part", packages[count].path);
            fd = open(partial, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | SEVEN_BINARY, 0600);
            if (fd < 0) { result = -3009; goto done; }
            owned = 1; output = fdopen(fd, "wb");
            if (!output) { close(fd); result = -3009; goto done; }
            }
        }
        /* Listing only parses metadata; extraction later validates every byte and CRC. */
        if (!destination) {
            if (archive_read_data_skip(archive) != ARCHIVE_OK) { result = -3010; goto done; }
            continue;
        }
        unsigned char magic[4] = {0};
        for (;;) {
            if (seven_canceled(input)) { result = -3008; goto done; }
            la_ssize_t bytes = archive_read_data(archive, buffer, 128 * 1024);
            if (bytes < 0 || (bytes > 0 && (uint64_t)bytes > expected - written)) { result = -3010; goto done; }
            if (!bytes) break;
            if (written < sizeof(magic)) {
                size_t n = sizeof(magic) - (size_t)written; if (n > (size_t)bytes) n = (size_t)bytes;
                memcpy(magic + written, buffer, n);
            }
            if (output && fwrite(buffer, 1, (size_t)bytes, output) != (size_t)bytes) { result = -3011; goto done; }
            written += (uint64_t)bytes; input->processed += bytes;
        }
        if (written != expected || (package && memcmp(magic, "\x7f" "CNT", 4))) { result = -3010; goto done; }
        if (output) {
            if (fflush(output) || fsync(fileno(output))) { result = -3011; goto done; }
            int closed = fclose(output); output = NULL;
            if (closed) { result = -3011; goto done; }
            fd = open(packages[count].path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | SEVEN_BINARY, 0600);
            if (fd < 0) { result = -3009; goto done; }
            close(fd); reserved = 1;
#ifdef GS_7Z_HOST_TEST
            /* Windows rename does not replace even our exclusive placeholder. */
            unlink(packages[count].path);
#endif
            if (rename(partial, packages[count].path)) { result = -3011; goto done; }
            reserved = 0; owned = 0; partial[0] = 0; packages[count++].size = (int64_t)written;
        }
    }
done:
    if (output) fclose(output);
    if (result < 0) {
        if (memory_exhausted) result = -3006;
        else if (input && input->canceled) result = -3008;
        else if (archive && archive_read_has_encrypted_entries(archive) > 0) result = -3005;
        const char *detail = archive ? archive_error_string(archive) : NULL;
        seven_error(-result, (result == -3010 || result == -3003) ? detail : NULL);
        if (reserved) unlink(packages[count].path);
        if (owned) unlink(partial);
        if (destination) for (int i = 0; i < count; i++) unlink(packages[i].path);
    }
    if (archive) archive_read_free(archive);
    if (seen) { for (int i = 0; i < entries; i++) gs_7z_free(seen[i]); gs_7z_free(seen); }
    if (input && input->input) fclose(input->input);
    gs_7z_free(buffer); gs_7z_free(input);
    if (error && error_capacity > 0) snprintf(error, error_capacity, "%s", last_error);
    __sync_lock_release(&extracting);
    return result;
}
int gs_extract_7z(const char *first, const char *destination,
    const char *const *names, const char *const *paths, int volume_count,
    GsExtractedPkg *packages, int capacity, GsArchiveProgress progress)
{
    return seven_extract(first, destination, names, paths, volume_count, packages, capacity, progress, NULL, 0);
}
int gs_7z_extract_local_ex(const char *first, const char *destination, GsExtractedPkg *packages,
    int capacity, GsArchiveProgress progress, char *error, int error_capacity)
{
    return seven_extract(first, destination, NULL, NULL, 1, packages, capacity, progress, error, error_capacity);
}
int gs_7z_extract_local(const char *first, const char *destination, GsExtractedPkg *packages,
    int capacity, GsArchiveProgress progress)
{
    return gs_7z_extract_local_ex(first, destination, packages, capacity, progress, NULL, 0);
}
