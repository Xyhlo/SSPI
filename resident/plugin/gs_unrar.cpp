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
#ifdef GS_RAR_PS4
#include <orbis/libkernel.h>
#endif

static const uint64_t maximum_expanded_bytes = 512ULL * 1024 * 1024 * 1024;
static int extraction_busy;
struct ExtractionLease {
    bool held;
    ExtractionLease() : held(__sync_lock_test_and_set(&extraction_busy, 1) == 0) {}
    ~ExtractionLease() { if (held) __sync_lock_release(&extraction_busy); }
};

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

template <typename Character>
static bool valid_entry_name(const Character *name, size_t length)
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
    GsArchiveDiagnostic diagnostic;
    uint64_t opening_since;
    bool opening, open_timed_out;
    uint64_t total;
    int volume_index;
};

static Extraction *active_extraction;

extern "C" int gs_rar_input_allowed(const char *path)
{
    if (!active_extraction || !path) return 0;
    for (int i = 0; i < active_extraction->volume_count; i++)
        if (!strcmp(path, active_extraction->paths[i])) return 1;
    return 0;
}

extern "C" int gs_rar_io_poll(const char *stage)
{
    Extraction *ctx = active_extraction;
    if (!ctx) return -1;
#ifdef GS_RAR_PS4
    if (ctx->opening && sceKernelGetProcessTime() - ctx->opening_since > 30000000) {
        ctx->open_timed_out = true;
        return -1;
    }
#endif
    if (!strcmp(stage, "file-open-before") || !strcmp(stage, "file-open-after") ||
        (ctx->opening && !strncmp(stage, "unicode-", 8)))
        rar_checkpoint(ctx->diagnostic, stage, 0, 0);
    return ctx->progress && ctx->progress(ctx->processed, ctx->total) ? -1 : 0;
}

struct ActiveExtraction {
    ActiveExtraction(Extraction *ctx) { active_extraction = ctx; }
    ~ActiveExtraction() { active_extraction = NULL; }
};

static bool decode_utf8(const char *password, wchar_t *output, size_t capacity)
{
    if (!password || !capacity) return false;
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

static bool encode_utf8(const wchar_t *input, char *output, size_t capacity)
{
    if (!input || !capacity) return false;
    size_t used = 0;
    for (size_t i = 0; i < 1024; i++) {
        uint32_t value = input[i];
        if (!value) { output[used] = 0; return true; }
        if (sizeof(wchar_t) == 2 && value >= 0xd800 && value <= 0xdbff) {
            if (++i >= 1024 || input[i] < 0xdc00 || input[i] > 0xdfff) return false;
            value = 0x10000 + ((value - 0xd800) << 10) + (input[i] - 0xdc00);
        }
        if (value > 0x10ffff || (value >= 0xd800 && value <= 0xdfff)) return false;
        unsigned bytes = value < 0x80 ? 1 : value < 0x800 ? 2 : value < 0x10000 ? 3 : 4;
        if (used + bytes >= capacity) return false;
        if (bytes == 1) output[used++] = static_cast<char>(value);
        else {
            output[used++] = static_cast<char>((bytes == 2 ? 0xc0 : bytes == 3 ? 0xe0 : 0xf0) | (value >> (6 * (bytes - 1))));
            for (unsigned j = bytes - 1; j > 0; j--)
                output[used++] = static_cast<char>(0x80 | ((value >> (6 * (j - 1))) & 63));
        }
    }
    return false;
}

static int callback(UINT message, LPARAM opaque, LPARAM p1, LPARAM p2)
{
    Extraction *ctx = reinterpret_cast<Extraction *>(opaque);
    if (ctx->progress && ctx->progress(ctx->processed, ctx->total)) return -1;
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
        // Downloads use hashed disk names. Resolve the source name first, then
        // the explicitly ordered input set; never open an unlisted disk path.
        char requested[1024];
        if (message == UCM_CHANGEVOLUMEW) {
            if (!encode_utf8(reinterpret_cast<wchar_t *>(p1), requested, sizeof(requested))) return -1;
        } else snprintf(requested, sizeof(requested), "%s", reinterpret_cast<char *>(p1));
        const char *name = strrchr(requested, '/'); name = name ? name + 1 : requested;
        int selected = -1;
        for (int i = ctx->volume_index + 1; i < ctx->volume_count; i++)
            if (!strcasecmp(name, ctx->names[i])) { selected = i; break; }
        if (selected < 0 && ctx->volume_index + 1 < ctx->volume_count) selected = ctx->volume_index + 1;
        if (selected >= 0) {
            int i = selected;
            if (strlen(ctx->paths[i]) >= 1024) return -1;
            if (message == UCM_CHANGEVOLUMEW) {
                if (!decode_utf8(ctx->paths[i], reinterpret_cast<wchar_t *>(p1), 1024)) return -1;
            } else strcpy(reinterpret_cast<char *>(p1), ctx->paths[i]);
            ctx->volume_index = i;
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

// LIST skips compressed payloads (including solid files) with 64-bit seeks.
// Count each split file once; never divide expanded output by compressed size.
template <typename Character>
static bool pkg_name(const Character *name, size_t capacity)
{
    size_t length = 0;
    while (length < capacity && name[length]) length++;
    return length >= 4 && length < capacity && name[length - 4] == '.' &&
        (name[length - 3] == 'p' || name[length - 3] == 'P') &&
        (name[length - 2] == 'k' || name[length - 2] == 'K') &&
        (name[length - 1] == 'g' || name[length - 1] == 'G');
}

static int measure_archive(RAROpenArchiveDataEx open, Extraction &ctx)
{
    open.OpenMode = RAR_OM_LIST;
    HANDLE archive = RAROpenArchiveEx(&open);
    int rc = open.OpenResult ? static_cast<int>(open.OpenResult) : archive ? 0 : ERAR_BAD_ARCHIVE;
    uint64_t total = 0;
    unsigned entries = 0, packages = 0;
    while (archive && !rc) {
        RARHeaderDataEx header = {};
        ctx.password_requests = 0;
        if (gs_rar_io_poll("scan")) { rc = ctx.open_timed_out ? 1001 : ERAR_UNKNOWN; break; }
        rc = RARReadHeaderEx(archive, &header);
        if (rc == ERAR_END_ARCHIVE) { rc = 0; break; }
        if (rc) break;
        uint64_t size = (uint64_t(header.UnpSizeHigh) << 32) | header.UnpSize;
        if (++entries > 4096 || size > maximum_expanded_bytes - total || header.DictSize > 256 * 1024)
        { rc = ERAR_LARGE_DICT; break; }
        if (!(header.Flags & RHDF_DIRECTORY)) {
            total += size;
            if (header.FileNameW[0] ? pkg_name(header.FileNameW, sizeof(header.FileNameW) / sizeof(wchar_t))
                : pkg_name(header.FileName, sizeof(header.FileName))) packages++;
        }
        rc = RARProcessFile(archive, RAR_SKIP, NULL, NULL);
    }
    if (archive) RARCloseArchive(archive);
    if (!rc) ctx.total = total;
    rar_checkpoint(ctx.diagnostic, "scan-complete", 0, rc);
    if (ctx.diagnostic) ctx.diagnostic("scan-pkg-count", packages, rc, 0, 0, total);
    return rc;
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
    ExtractionLease lease;
    if (!lease.held) return -1003;
    if (capacity > 256) capacity = 256;
    Extraction ctx = { names, paths, volume_count, NULL, 0, 0, 0, progress, password ? password : "", {}, 0, diagnostic, 0, true, false };
    ActiveExtraction active(&ctx);
#ifdef GS_RAR_PS4
    ctx.opening_since = sceKernelGetProcessTime();
#endif
    if (strnlen(ctx.password, 257) > 256 || !decode_utf8(ctx.password, ctx.wide_password, 257)) return -ERAR_BAD_PASSWORD;
    wchar_t archive_path[1300];
    if (strnlen(first, 1300) >= 1300 || !decode_utf8(first, archive_path, 1300)) return -ERAR_EOPEN;
    RAROpenArchiveDataEx open = {};
    open.ArcName = const_cast<char *>(first); open.OpenMode = RAR_OM_EXTRACT;
    open.ArcNameW = archive_path;
    open.Callback = callback; open.UserData = reinterpret_cast<LPARAM>(&ctx);
    int measured = measure_archive(open, ctx);
    if (measured) return -measured;
    ctx.volume_index = 0;
    ctx.password_requests = 0;
    if (progress && progress(0, ctx.total)) return -ERAR_UNKNOWN;
#ifdef GS_RAR_PS4
    ctx.opening_since = sceKernelGetProcessTime();
#endif
    rar_checkpoint(diagnostic, "open-before", 0, 0);
    if (gs_rar_io_poll("open-check")) return ctx.open_timed_out ? -1001 : -ERAR_UNKNOWN;
    HANDLE archive = RAROpenArchiveEx(&open);
    ctx.opening = false;
    rar_checkpoint(diagnostic, "open-after", 0,
        static_cast<int>(open.OpenResult ? open.OpenResult : archive ? ERAR_SUCCESS : ERAR_BAD_ARCHIVE));
    if (!archive || open.OpenResult) {
        if (archive) {
            rar_checkpoint(diagnostic, "close-before", 0, 0);
            int close_result = RARCloseArchive(archive);
            rar_checkpoint(diagnostic, "close-after", 0, close_result);
        }
        return ctx.open_timed_out ? -1001 : -static_cast<int>(open.OpenResult ? open.OpenResult : ERAR_BAD_ARCHIVE);
    }
    int count = 0, entries = 0, rc = ERAR_SUCCESS;
    uint64_t declared = 0;
    std::set<std::wstring> seen;
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
            if (++entries > 4096 || (progress && progress(ctx.processed, ctx.total))) {
                rc = ERAR_BAD_DATA; rar_checkpoint(diagnostic, "entry-stopped", entries, rc, &header); break;
            }
            // The DLL's narrow name uses the host locale. Shell hosts may not
            // have a working multibyte locale, even when the wide name is valid.
            std::wstring name;
            if (header.FileNameW[0]) {
                size_t length = 0, capacity = sizeof(header.FileNameW) / sizeof(header.FileNameW[0]);
                while (length < capacity && header.FileNameW[length]) length++;
                if (length == capacity) { rc = ERAR_BAD_DATA; }
                else name.assign(header.FileNameW, length);
            } else {
                size_t length = strnlen(header.FileName, sizeof(header.FileName));
                if (length == sizeof(header.FileName)) { rc = ERAR_BAD_DATA; }
                else for (size_t i = 0; i < length; i++) name += static_cast<unsigned char>(header.FileName[i]);
            }
            if (rc || !valid_entry_name(name.c_str(), name.size())) {
                rc = ERAR_BAD_DATA; rar_checkpoint(diagnostic, "entry-name-rejected", entries, rc, &header); break;
            }
            if (header.RedirType || (header.HostOS == 3 && ((header.FileAttr & 0170000) == 0120000))) {
                rc = ERAR_BAD_DATA; rar_checkpoint(diagnostic, "entry-link-rejected", entries, rc, &header); break;
            }
            if ((header.Flags & RHDF_ENCRYPTED) && !*ctx.password) { rc = ERAR_MISSING_PASSWORD; break; }
            uint64_t size = (uint64_t(header.UnpSizeHigh) << 32) | header.UnpSize;
            if (size > maximum_expanded_bytes - declared || header.DictSize > 256 * 1024) { rc = ERAR_LARGE_DICT; break; }
            declared += size;
            std::wstring key(name);
            for (size_t i = 0; i < key.size(); i++) {
                if (key[i] == '\\') key[i] = '/';
                else if (key[i] >= 'A' && key[i] <= 'Z') key[i] += 'a' - 'A';
            }
            if (!(header.Flags & RHDF_DIRECTORY) && !seen.insert(key).second) {
                rc = ERAR_BAD_DATA; rar_checkpoint(diagnostic, "entry-duplicate-rejected", entries, rc, &header); break;
            }
            bool selected = !(header.Flags & RHDF_DIRECTORY) && key.size() >= 4 && key.compare(key.size() - 4, 4, L".pkg") == 0;
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
                // A bounded buffer avoids tiny writes on large sequential PKGs.
                setvbuf(ctx.output, NULL, _IOFBF, 1024 * 1024);
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
    if (!rc && count == 0) rc = 1002;
    if (rc) {
        if (output_owned) unlink(output);
        for (int i = 0; i < count; i++) unlink(packages[i].path);
        return -rc;
    }
    if (ctx.processed != ctx.total) {
        for (int i = 0; i < count; i++) unlink(packages[i].path);
        return -ERAR_BAD_DATA;
    }
    if (progress && progress(ctx.processed, ctx.total)) {
        for (int i = 0; i < count; i++) unlink(packages[i].path);
        return -ERAR_UNKNOWN;
    }
    return count;
}
