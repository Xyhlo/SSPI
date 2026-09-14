#define _UNIX
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <strings.h>
#include <wchar.h>
#include <unistd.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <sys/statfs.h>
#include "../native/storage_space.h"
#include <set>
#include <string>
#include "../../SDK/vendor/unrar/dll.hpp"
#include "gs_unrar.h"

static const uint64_t maximum_expanded_bytes = 512ULL * 1024 * 1024 * 1024;

static void rar_checkpoint(GsArchiveDiagnostic diagnostic, const char *stage,
    unsigned entry, int result, const RARHeaderDataEx *header = NULL)
{
    if (!diagnostic) return;
    // Trace the first 32 entries, plus terminal/error boundaries. Data callbacks
    // never emit diagnostics, even for very large or solid archives.
    if (entry > 32 && result == 0) {
        if (entry == 33 && !strcmp(stage, "header-before"))
            diagnostic("entry-trace-limit", entry, 0, 0, 0, 0);
        return;
    }
    diagnostic(stage, entry, result, header ? header->DictSize : 0, header ? header->Method : 0,
        header ? (uint64_t(header->UnpSizeHigh) << 32) | header->UnpSize : 0);
}

static bool valid_entry_name(const char *name, size_t length)
{
    if (!length || length > 1024 || name[0] == '/' || name[0] == '\\') return false;
    size_t start = 0;
    for (size_t i = 0; i <= length; ++i) {
        if (i < length && name[i] == ':') return false;
        if (i != length && name[i] != '/' && name[i] != '\\') continue;
        size_t part = i - start;
        if ((!part && i != length) || (part == 1 && name[start] == '.') ||
            (part == 2 && name[start] == '.' && name[start + 1] == '.')) return false;
        start = i + 1;
    }
    return true;
}

struct Extraction {
    const char *const *names;
    const char *const *paths;
    int volume_count;
    FILE *output;
    uint64_t written, expected, processed;
    GsArchiveProgress progress;
    const char *password;
    wchar_t wide_password[257];
    unsigned password_requests;
};

static bool decode_password(const char *password, wchar_t *output, size_t capacity)
{
    if (!password || strnlen(password, 257) > 256 || !capacity) return false;
    size_t used = 0;
    const unsigned char *p = reinterpret_cast<const unsigned char *>(password);
    while (*p) {
        uint32_t value = *p++, minimum = 0; unsigned extra = 0;
        if (value >= 0xc2 && value <= 0xdf) { value &= 31; extra = 1; minimum = 0x80; }
        else if (value >= 0xe0 && value <= 0xef) { value &= 15; extra = 2; minimum = 0x800; }
        else if (value >= 0xf0 && value <= 0xf4) { value &= 7; extra = 3; minimum = 0x10000; }
        else if (value >= 0x80) return false;
        for (unsigned i = 0; i < extra; i++) {
            if ((*p & 0xc0) != 0x80) return false;
            value = (value << 6) | (*p++ & 63);
        }
        if (value < minimum || value > 0x10ffff || (value >= 0xd800 && value <= 0xdfff)) return false;
        if (sizeof(wchar_t) == 2 && value > 0xffff) {
            if (used + 2 >= capacity) return false;
            value -= 0x10000;
            output[used++] = static_cast<wchar_t>(0xd800 + (value >> 10));
            output[used++] = static_cast<wchar_t>(0xdc00 + (value & 1023));
        } else {
            if (used + 1 >= capacity) return false;
            output[used++] = static_cast<wchar_t>(value);
        }
    }
    output[used] = 0;
    return true;
}

static int callback(UINT message, LPARAM opaque, LPARAM p1, LPARAM p2)
{
    Extraction *ctx = reinterpret_cast<Extraction *>(opaque);
    if (ctx->progress && ctx->progress(ctx->processed, 0)) return -1;
    if (message == UCM_NEEDPASSWORD || message == UCM_NEEDPASSWORDW) {
        if (!p1 || p2 <= 0 || !ctx->password || !*ctx->password || ++ctx->password_requests > 4) return -1;
        size_t length = message == UCM_NEEDPASSWORDW ? wcslen(ctx->wide_password) : strlen(ctx->password);
        if (length >= static_cast<size_t>(p2)) return -1;
        if (message == UCM_NEEDPASSWORDW)
            memcpy(reinterpret_cast<void *>(p1), ctx->wide_password, (length + 1) * sizeof(wchar_t));
        else memcpy(reinterpret_cast<void *>(p1), ctx->password, length + 1);
        return 1;
    }
    if (message == UCM_LARGEDICT) return -1;
    if (message == UCM_CHANGEVOLUME || message == UCM_CHANGEVOLUMEW) {
        if (p2 == RAR_VOL_NOTIFY) return 1;
        // UnRAR asks for a specific next volume. Only map names supplied with this job.
        char requested[1024];
        if (message == UCM_CHANGEVOLUMEW) {
            if (wcstombs(requested, reinterpret_cast<wchar_t *>(p1), sizeof(requested)) >= sizeof(requested)) return -1;
        } else snprintf(requested, sizeof(requested), "%s", reinterpret_cast<char *>(p1));
        const char *name = strrchr(requested, '/'); name = name ? name + 1 : requested;
        for (int i = 0; i < ctx->volume_count; i++) {
            if (strcasecmp(name, ctx->names[i])) continue;
            if (strlen(ctx->paths[i]) >= 1024) return -1;
            if (message == UCM_CHANGEVOLUMEW) {
                if (mbstowcs(reinterpret_cast<wchar_t *>(p1), ctx->paths[i], 1024) == static_cast<size_t>(-1)) return -1;
            } else strcpy(reinterpret_cast<char *>(p1), ctx->paths[i]);
            return 1;
        }
        return -1;
    }
    if (message == UCM_PROCESSDATA) {
        if (p2 < 0 || static_cast<uint64_t>(p2) > maximum_expanded_bytes - ctx->processed) return -1;
        ctx->processed += p2;
        if (ctx->output) {
            if (static_cast<uint64_t>(p2) > ctx->expected - ctx->written ||
                fwrite(reinterpret_cast<void *>(p1), 1, p2, ctx->output) != static_cast<size_t>(p2)) return -1;
            ctx->written += p2;
        }
    }
    return 1;
}

extern "C" int gs_extract_rar(const char *first, const char *destination,
    const char *const *names, const char *const *paths, int volume_count,
    GsExtractedPkg *packages, int capacity, GsArchiveProgress progress)
{
    return gs_extract_rar_password(first, destination, names, paths, volume_count, packages, capacity, progress, "");
}

extern "C" int gs_extract_rar_password(const char *first, const char *destination,
    const char *const *names, const char *const *paths, int volume_count,
    GsExtractedPkg *packages, int capacity, GsArchiveProgress progress, const char *password)
{
    return gs_extract_rar_password_diagnostic(first, destination, names, paths, volume_count,
        packages, capacity, progress, password, NULL);
}

extern "C" int gs_extract_rar_password_diagnostic(const char *first, const char *destination,
    const char *const *names, const char *const *paths, int volume_count,
    GsExtractedPkg *packages, int capacity, GsArchiveProgress progress, const char *password,
    GsArchiveDiagnostic diagnostic)
{
    if (!first || !destination || !names || !paths || !packages || volume_count < 1 ||
        volume_count > 512 || capacity < 1) return -ERAR_BAD_DATA;
    if (capacity > 256) capacity = 256;
    Extraction ctx = { names, paths, volume_count, NULL, 0, 0, 0, progress, password ? password : "", {}, 0 };
    if (!decode_password(ctx.password, ctx.wide_password, 257)) return -ERAR_BAD_PASSWORD;
    RAROpenArchiveDataEx open = {};
    open.ArcName = const_cast<char *>(first); open.OpenMode = RAR_OM_EXTRACT;
    open.Callback = callback; open.UserData = reinterpret_cast<LPARAM>(&ctx);
    rar_checkpoint(diagnostic, "open-before", 0, 0);
    HANDLE archive = RAROpenArchiveEx(&open);
    rar_checkpoint(diagnostic, "open-after", 0,
        static_cast<int>(open.OpenResult ? open.OpenResult : archive ? ERAR_SUCCESS : ERAR_BAD_ARCHIVE));
    if (!archive || open.OpenResult) {
        if (archive) {
            rar_checkpoint(diagnostic, "close-before", 0, 0);
            int close_result = RARCloseArchive(archive);
            rar_checkpoint(diagnostic, "close-after", 0, close_result);
        }
        return -static_cast<int>(open.OpenResult ? open.OpenResult : ERAR_BAD_ARCHIVE);
    }
    int count = 0, entries = 0, rc = ERAR_SUCCESS;
    uint64_t declared = 0;
    std::set<std::string> seen;
    char output[1100] = {};
    bool output_owned = false;
    try {
        for (;;) {
            RARHeaderDataEx header = {};
            ctx.password_requests = 0;
            rar_checkpoint(diagnostic, "header-before", entries + 1, 0);
            rc = RARReadHeaderEx(archive, &header);
            rar_checkpoint(diagnostic, "header-after", entries + 1, rc, rc == ERAR_SUCCESS ? &header : NULL);
            if (rc == ERAR_END_ARCHIVE) { rc = ERAR_SUCCESS; break; }
            if (rc != ERAR_SUCCESS) break;
            if (++entries > 4096 || (progress && progress(ctx.processed, 0))) { rc = ERAR_BAD_DATA; break; }
            const char *name = header.FileName;
            size_t length = strnlen(name, sizeof(header.FileName));
            if (length == sizeof(header.FileName) || !valid_entry_name(name, length) || header.RedirType ||
                (header.HostOS == 3 && ((header.FileAttr & 0170000) == 0120000))) { rc = ERAR_BAD_DATA; break; }
            if ((header.Flags & RHDF_ENCRYPTED) && !*ctx.password) { rc = ERAR_MISSING_PASSWORD; break; }
            uint64_t size = (uint64_t(header.UnpSizeHigh) << 32) | header.UnpSize;
            if (size > maximum_expanded_bytes - declared || header.DictSize > 256 * 1024) { rc = ERAR_LARGE_DICT; break; }
            declared += size;
            std::string key(name);
            for (size_t i = 0; i < key.size(); i++) {
                if (key[i] == '\\') key[i] = '/';
                else if (key[i] >= 'A' && key[i] <= 'Z') key[i] += 'a' - 'A';
            }
            if (!(header.Flags & RHDF_DIRECTORY) && !seen.insert(key).second) { rc = ERAR_BAD_DATA; break; }
            bool selected = !(header.Flags & RHDF_DIRECTORY) && length >= 4 && !strcasecmp(name + length - 4, ".pkg");
            if (selected) {
                if (count >= capacity) { rc = ERAR_BAD_DATA; break; }
                int64_t available = gs_storage_available_bytes(destination);
                if (available < 0 || static_cast<uint64_t>(available) < size + 64 * 1024 * 1024)
                { rc = ERAR_EWRITE; break; }
                int path_length = snprintf(packages[count].path, sizeof(packages[count].path), "%s/pkg-%03d.pkg", destination, count + 1);
                if (path_length < 0 || static_cast<size_t>(path_length) >= sizeof(packages[count].path)) { rc = ERAR_ECREATE; break; }
                packages[count].size = size;
                snprintf(output, sizeof(output), "%s.part", packages[count].path);
                struct stat existing;
                if (!lstat(packages[count].path, &existing)) { rc = ERAR_ECREATE; break; }
                int descriptor = ::open(output, O_WRONLY | O_CREAT | O_EXCL, 0600);
                if (descriptor < 0) { rc = ERAR_ECREATE; break; }
                output_owned = true;
                ctx.output = fdopen(descriptor, "wb");
                if (!ctx.output) close(descriptor);
                if (!ctx.output) { rc = ERAR_ECREATE; break; }
                ctx.written = 0; ctx.expected = size;
            }
            // TEST decompresses and verifies checksums but never lets UnRAR create paths.
            // Non-PKG entries are consumed to preserve solid dictionaries without writing junk.
            ctx.password_requests = 0;
            rar_checkpoint(diagnostic, "process-before", entries, 0, &header);
            rc = RARProcessFile(archive, RAR_TEST, NULL, NULL);
            rar_checkpoint(diagnostic, "process-after", entries, rc, &header);
            if (ctx.output) {
                if (fflush(ctx.output) || fsync(fileno(ctx.output))) rc = ERAR_EWRITE;
                if (fclose(ctx.output)) rc = ERAR_EWRITE;
                ctx.output = NULL;
                if (!rc && ctx.written != ctx.expected) rc = ERAR_BAD_DATA;
                if (!rc) {
                    unsigned char magic[4]; FILE *check = fopen(output, "rb");
                    if (!check || fread(magic, 1, 4, check) != 4 || memcmp(magic, "\x7f\x43\x4e\x54", 4)) rc = ERAR_BAD_DATA;
                    if (check) fclose(check);
                }
                struct stat existing;
                if (!rc && (!lstat(packages[count].path, &existing) || rename(output, packages[count].path))) rc = ERAR_EWRITE;
                if (!rc) { count++; output[0] = 0; output_owned = false; }
            }
            if (rc) break;
        }
    } catch (...) { rc = ERAR_UNKNOWN; rar_checkpoint(diagnostic, "exception", entries, rc); }
    if (ctx.output) fclose(ctx.output);
    rar_checkpoint(diagnostic, "close-before", 0, 0);
    int close_result = RARCloseArchive(archive);
    rar_checkpoint(diagnostic, "close-after", 0, close_result);
    if (!rc && count == 0) rc = ERAR_BAD_ARCHIVE;
    if (rc) {
        if (output_owned) unlink(output);
        for (int i = 0; i < count; i++) unlink(packages[i].path);
        return -rc;
    }
    return count;
}
