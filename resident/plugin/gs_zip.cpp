#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <errno.h>
#include <fcntl.h>
#include <set>
#include <string>
#ifdef GS_ZIP_HOST_TEST
#include <io.h>
#define close _close
#define open _open
#define fdopen _fdopen
#define fsync _commit
#define fileno _fileno
#define unlink _unlink
#define O_NOFOLLOW 0
#define ZIP_BINARY O_BINARY
static int64_t zip_test_space = INT64_MAX;
extern "C" __declspec(dllexport) void gs_zip_test_space(int64_t value) { zip_test_space = value; }
static int64_t gs_storage_available_bytes(const char *) { return zip_test_space; }
#else
#include <unistd.h>
#include "../native/storage_space.h"
#define ZIP_BINARY 0
#endif
#include "../../SDK/vendor/miniz/headers/miniz.h"
#include "gs_unrar.h"

static const uint64_t zip_max_expanded = 512ULL * 1024 * 1024 * 1024;
static const size_t zip_memory_budget = 32 * 1024 * 1024;
struct ZipContext {
    FILE *input, *output;
    uint64_t size, written, expected, completed, total;
    size_t allocated;
    GsArchiveProgress progress;
};
union ZipAllocation { size_t size; unsigned char alignment[16]; };

static void *zip_allocate(void *opaque, size_t items, size_t size)
{
    ZipContext *ctx = static_cast<ZipContext *>(opaque);
    if (size && items > zip_memory_budget / size) return NULL;
    size_t bytes = items * size;
    if (bytes > zip_memory_budget - ctx->allocated) return NULL;
    ZipAllocation *block = static_cast<ZipAllocation *>(malloc(sizeof(*block) + bytes));
    if (!block) return NULL;
    block->size = bytes; ctx->allocated += bytes;
    return block + 1;
}
static void zip_free(void *opaque, void *address)
{
    if (!address) return;
    ZipContext *ctx = static_cast<ZipContext *>(opaque);
    ZipAllocation *block = static_cast<ZipAllocation *>(address) - 1;
    ctx->allocated -= block->size; free(block);
}
static void *zip_reallocate(void *opaque, void *address, size_t items, size_t size)
{
    if (!address) return zip_allocate(opaque, items, size);
    if (size && items > zip_memory_budget / size) return NULL;
    size_t bytes = items * size;
    ZipAllocation *block = static_cast<ZipAllocation *>(address) - 1;
    ZipContext *ctx = static_cast<ZipContext *>(opaque);
    if (bytes > zip_memory_budget - (ctx->allocated - block->size)) return NULL;
    size_t old_size = block->size;
    block = static_cast<ZipAllocation *>(realloc(block, sizeof(*block) + bytes));
    if (!block) return NULL;
    block->size = bytes; ctx->allocated = ctx->allocated - old_size + bytes;
    return block + 1;
}
static int zip_seek(FILE *file, uint64_t offset, int whence)
{
    if (offset > INT64_MAX) return -1;
#ifdef GS_ZIP_HOST_TEST
    return _fseeki64(file, (int64_t)offset, whence);
#else
    return fseeko(file, (off_t)offset, whence);
#endif
}
static int64_t zip_tell(FILE *file)
{
#ifdef GS_ZIP_HOST_TEST
    return _ftelli64(file);
#else
    return (int64_t)ftello(file);
#endif
}
static size_t zip_read(void *opaque, mz_uint64 offset, void *buffer, size_t bytes)
{
    ZipContext *ctx = static_cast<ZipContext *>(opaque);
    if (offset > ctx->size || bytes > ctx->size - offset || zip_seek(ctx->input, offset, SEEK_SET)) return 0;
    return fread(buffer, 1, bytes, ctx->input);
}
static size_t zip_write(void *opaque, mz_uint64 offset, const void *buffer, size_t bytes)
{
    ZipContext *ctx = static_cast<ZipContext *>(opaque);
    if (offset != ctx->written || bytes > ctx->expected - ctx->written ||
        (ctx->progress && ctx->progress(ctx->completed + ctx->written, ctx->total))) return 0;
    size_t count = fwrite(buffer, 1, bytes, ctx->output);
    ctx->written += count; return count;
}
static bool zip_name(const char *name, size_t length)
{
    if (!length || length > 1024 || name[0] == '/' || name[0] == '\\') return false;
    size_t start = 0;
    for (size_t i = 0; i <= length; ++i) {
        if (i < length && ((unsigned char)name[i] < 32 || name[i] == ':' || name[i] == 127)) return false;
        if (i < length && name[i] != '/' && name[i] != '\\') continue;
        size_t n = i - start;
        if ((!n && i < length) || (n == 1 && name[start] == '.') ||
            (n == 2 && name[start] == '.' && name[start + 1] == '.')) return false;
        start = i + 1;
    }
    return true;
}
static bool zip_is_package(const std::string &name)
{ return name.size() >= 4 && name.compare(name.size() - 4, 4, ".pkg") == 0; }

extern "C" int gs_extract_zip(const char *first, const char *destination,
    const char *const *names, const char *const *paths, int volume_count,
    GsExtractedPkg *packages, int capacity, GsArchiveProgress progress)
{
    (void)names; (void)paths;
    if (!first || !destination || !packages || capacity < 1 || volume_count != 1) return -2001;
    if (capacity > 256) capacity = 256;
    ZipContext ctx = {}; ctx.progress = progress;
    int fd = open(first, O_RDONLY | O_NOFOLLOW | ZIP_BINARY);
    if (fd < 0) return -2002;
    ctx.input = fdopen(fd, "rb");
    if (!ctx.input) { close(fd); return -2002; }
    setvbuf(ctx.input, NULL, _IOFBF, 1024 * 1024);
    if (zip_seek(ctx.input, 0, SEEK_END) || zip_tell(ctx.input) < 22) { fclose(ctx.input); return -2003; }
    ctx.size = (uint64_t)zip_tell(ctx.input);
    mz_zip_archive archive = {};
    archive.m_pAlloc = zip_allocate; archive.m_pFree = zip_free; archive.m_pRealloc = zip_reallocate;
    archive.m_pAlloc_opaque = &ctx; archive.m_pRead = zip_read; archive.m_pIO_opaque = &ctx;
    if (!mz_zip_reader_init(&archive, ctx.size, MZ_ZIP_FLAG_DO_NOT_SORT_CENTRAL_DIRECTORY)) {
        fclose(ctx.input); return -2003;
    }
    int count = 0, result = -2004;
    char partial[1100] = {}; bool partial_owned = false;
    try {
        mz_uint entries = mz_zip_reader_get_num_files(&archive);
        if (!entries || entries > 4096) throw 2004;
        std::set<std::string> seen;
        // Validate the entire directory before creating any output; miniz never receives an output path.
        for (mz_uint i = 0; i < entries; ++i) {
            mz_zip_archive_file_stat stat;
            char name[1025];
            mz_uint needed = mz_zip_reader_get_filename(&archive, i, NULL, 0);
            if (!needed || needed > sizeof(name) || !mz_zip_reader_file_stat(&archive, i, &stat) ||
                mz_zip_reader_get_filename(&archive, i, name, sizeof(name)) != needed ||
                strlen(name) + 1 != needed || !zip_name(name, needed - 1)) throw 2004;
            unsigned mode = stat.m_external_attr >> 16;
            if (stat.m_is_encrypted || !stat.m_is_supported ||
                ((mode & 0170000) && (mode & 0170000) != 0100000 && (mode & 0170000) != 0040000)) throw 2005;
            if (stat.m_uncomp_size > zip_max_expanded - ctx.total) throw 2006;
            ctx.total += stat.m_uncomp_size;
            std::string key(name);
            for (size_t k = 0; k < key.size(); ++k) {
                if (key[k] == '\\') key[k] = '/';
                else if (key[k] >= 'A' && key[k] <= 'Z') key[k] += 'a' - 'A';
            }
            if (!seen.insert(key).second) throw 2004;
        }
        for (mz_uint i = 0; i < entries; ++i) {
            mz_zip_archive_file_stat stat; char name[1025];
            if (!mz_zip_reader_file_stat(&archive, i, &stat) ||
                !mz_zip_reader_get_filename(&archive, i, name, sizeof(name))) throw 2004;
            std::string key(name);
            for (size_t k = 0; k < key.size(); ++k) if (key[k] >= 'A' && key[k] <= 'Z') key[k] += 'a' - 'A';
            if (stat.m_is_directory || !zip_is_package(key)) { ctx.completed += stat.m_uncomp_size; continue; }
            if (count >= capacity || stat.m_uncomp_size < 4) throw 2004;
            int64_t available = gs_storage_available_bytes(destination);
            if (available < 0 || (uint64_t)available < stat.m_uncomp_size + 64 * 1024 * 1024) throw 2007;
            if (progress && progress(ctx.completed, ctx.total)) throw 2008;
            int n = snprintf(packages[count].path, sizeof(packages[count].path), "%s/pkg-%03d.pkg", destination, count + 1);
            if (n < 0 || (size_t)n >= sizeof(packages[count].path)) throw 2004;
            FILE *existing = fopen(packages[count].path, "rb");
            if (existing) { fclose(existing); throw 2009; }
            snprintf(partial, sizeof(partial), "%s.part", packages[count].path);
            fd = open(partial, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW | ZIP_BINARY, 0600);
            if (fd < 0) throw 2009;
            partial_owned = true; ctx.output = fdopen(fd, "wb");
            if (!ctx.output) { close(fd); throw 2009; }
            setvbuf(ctx.output, NULL, _IOFBF, 1024 * 1024);
            ctx.written = 0; ctx.expected = stat.m_uncomp_size;
            if (!mz_zip_reader_extract_to_callback(&archive, i, zip_write, &ctx, 0) || ctx.written != ctx.expected) throw 2010;
            if (fflush(ctx.output) || fsync(fileno(ctx.output))) throw 2011;
            int closed = fclose(ctx.output); ctx.output = NULL;
            if (closed) throw 2011;
            unsigned char magic[4]; FILE *check = fopen(partial, "rb");
            bool valid = check && fread(magic, 1, 4, check) == 4 && !memcmp(magic, "\x7f" "CNT", 4);
            if (check) fclose(check);
            if (!valid) throw 2010;
            if (rename(partial, packages[count].path)) throw 2011;
            partial_owned = false; partial[0] = 0;
            packages[count].size = (int64_t)ctx.written; ++count;
            ctx.completed += ctx.written;
        }
        result = count ? count : -2012;
    } catch (int error) { result = -error; }
      catch (...) { result = -2013; }
    if (ctx.output) fclose(ctx.output);
    mz_zip_reader_end(&archive); fclose(ctx.input);
    if (result < 0) {
        if (partial_owned) unlink(partial);
        for (int i = 0; i < count; ++i) unlink(packages[i].path);
    }
    return result;
}
