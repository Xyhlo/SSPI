#define _UNIX
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <strings.h>
#include <wchar.h>
#include <unistd.h>
#include <sys/statfs.h>
#include "../native/storage_space.h"
#include <set>
#include <string>
#include "../../SDK/vendor/unrar/dll.hpp"
#include "gs_unrar.h"

struct Extraction {
    const char *const *names;
    const char *const *paths;
    int volume_count;
    FILE *output;
    uint64_t written, expected, processed;
    GsArchiveProgress progress;
};

static int callback(UINT message, LPARAM opaque, LPARAM p1, LPARAM p2)
{
    Extraction *ctx = reinterpret_cast<Extraction *>(opaque);
    if (ctx->progress && ctx->progress(ctx->processed, 0)) return -1;
    if (message == UCM_NEEDPASSWORD || message == UCM_NEEDPASSWORDW || message == UCM_LARGEDICT) return -1;
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
        if (p2 < 0 || static_cast<uint64_t>(p2) > 8ULL * 1024 * 1024 * 1024 * 1024 - ctx->processed) return -1;
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
    Extraction ctx = { names, paths, volume_count, NULL, 0, 0, 0, progress };
    RAROpenArchiveDataEx open = {};
    open.ArcName = const_cast<char *>(first); open.OpenMode = RAR_OM_EXTRACT;
    open.Callback = callback; open.UserData = reinterpret_cast<LPARAM>(&ctx);
    HANDLE archive = RAROpenArchiveEx(&open);
    if (!archive || open.OpenResult) { if (archive) RARCloseArchive(archive); return -static_cast<int>(open.OpenResult ? open.OpenResult : ERAR_BAD_ARCHIVE); }
    int count = 0, entries = 0, rc = ERAR_SUCCESS;
    uint64_t declared = 0;
    std::set<std::string> seen;
    char output[1100] = {};
    try {
        for (;;) {
            RARHeaderDataEx header = {};
            rc = RARReadHeaderEx(archive, &header);
            if (rc == ERAR_END_ARCHIVE) { rc = ERAR_SUCCESS; break; }
            if (rc != ERAR_SUCCESS) break;
            if (++entries > 4096 || (progress && progress(ctx.processed, 0))) { rc = ERAR_BAD_DATA; break; }
            const char *name = header.FileName;
            size_t length = strnlen(name, sizeof(header.FileName));
            if (length == sizeof(header.FileName) || !length || name[0] == '/' || name[0] == '\\' ||
                strchr(name, ':') || strstr(name, "../") || strstr(name, "..\\") || header.RedirType ||
                (header.HostOS == 3 && ((header.FileAttr & 0170000) == 0120000))) { rc = ERAR_BAD_DATA; break; }
            if (header.Flags & RHDF_ENCRYPTED) { rc = ERAR_MISSING_PASSWORD; break; }
            uint64_t size = (uint64_t(header.UnpSizeHigh) << 32) | header.UnpSize;
            if (size > 8ULL * 1024 * 1024 * 1024 * 1024 - declared || header.DictSize > 256 * 1024) { rc = ERAR_LARGE_DICT; break; }
            declared += size;
            bool selected = !(header.Flags & RHDF_DIRECTORY) && length >= 4 && !strcasecmp(name + length - 4, ".pkg");
            if (selected) {
                std::string key(name);
                for (size_t i = 0; i < key.size(); i++) if (key[i] >= 'A' && key[i] <= 'Z') key[i] += 'a' - 'A';
                if (count >= capacity || !seen.insert(key).second) { rc = ERAR_BAD_DATA; break; }
                int64_t available = gs_storage_available_bytes(destination);
                if (available < 0 || static_cast<uint64_t>(available) < size + 64 * 1024 * 1024)
                { rc = ERAR_EWRITE; break; }
                snprintf(packages[count].path, sizeof(packages[count].path), "%s/pkg-%03d.pkg", destination, count + 1);
                packages[count].size = size;
                snprintf(output, sizeof(output), "%s.part", packages[count].path);
                ctx.output = fopen(output, "wb");
                if (!ctx.output) { rc = ERAR_ECREATE; break; }
                ctx.written = 0; ctx.expected = size;
            }
            // TEST decompresses and verifies checksums but never lets UnRAR create paths.
            // Non-PKG entries are consumed to preserve solid dictionaries without writing junk.
            rc = RARProcessFile(archive, RAR_TEST, NULL, NULL);
            if (ctx.output) {
                if (fflush(ctx.output) || fsync(fileno(ctx.output))) rc = ERAR_EWRITE;
                if (fclose(ctx.output)) rc = ERAR_EWRITE;
                ctx.output = NULL;
                if (ctx.written != ctx.expected) rc = ERAR_BAD_DATA;
                if (!rc) {
                    unsigned char magic[4]; FILE *check = fopen(output, "rb");
                    if (!check || fread(magic, 1, 4, check) != 4 || memcmp(magic, "\x7f\x43\x4e\x54", 4)) rc = ERAR_BAD_DATA;
                    if (check) fclose(check);
                }
                if (!rc && rename(output, packages[count].path)) rc = ERAR_EWRITE;
                if (!rc) { count++; output[0] = 0; }
            }
            if (rc) break;
        }
    } catch (...) { rc = ERAR_UNKNOWN; }
    if (ctx.output) fclose(ctx.output);
    RARCloseArchive(archive);
    if (!rc && count == 0) rc = ERAR_BAD_ARCHIVE;
    if (rc) {
        if (output[0]) unlink(output);
        for (int i = 0; i < count; i++) unlink(packages[i].path);
        return -rc;
    }
    return count;
}
