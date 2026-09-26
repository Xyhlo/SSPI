#pragma once
#include <stdint.h>
#include <stddef.h>
#include <string.h>
#include <errno.h>
#ifdef __cplusplus
extern "C" {
#endif
/* Why an archive held no installable PKG, from its entry names. The RAR, ZIP
 * and 7z readers share it so background and in-app extraction report the same
 * cause: an extracted game folder, split PKG pieces or an archive inside the
 * archive. Each reader maps these to its own "no PKG" result codes. */
enum { GS_ARCHIVE_HINT_DUMP = 1, GS_ARCHIVE_HINT_NESTED = 2, GS_ARCHIVE_HINT_SPLIT_PKG = 4 };
static inline int gs_archive_name_ends(const char *name, size_t length, const char *suffix)
{
    size_t n = strlen(suffix);
    return length >= n && !memcmp(name + length - n, suffix, n);
}
/* A split PKG piece ends in ".pkg." plus 1 to 6 digits (game.pkg.001). Other
 * names are sidecars (game.pkg.md5, game.pkg.sha256) or archives (game.pkg.rar). */
static inline int gs_archive_split_piece(const char *base)
{
    const char *dot = strrchr(base, '.');
    size_t digits = dot ? strlen(dot + 1) : 0;
    if (!dot || digits < 1 || digits > 6 || (size_t)(dot - base) < 4 || memcmp(dot - 4, ".pkg", 4)) return 0;
    for (size_t i = 1; i <= digits; i++) if (dot[i] < '0' || dot[i] > '9') return 0;
    return 1;
}
static inline unsigned gs_archive_entry_hint(const char *name, size_t length)
{
    char lower[1025];
    size_t n = length < sizeof(lower) - 1 ? length : sizeof(lower) - 1;
    for (size_t i = 0; i < n; i++) {
        char c = name[i];
        lower[i] = c == '\\' ? '/' : (c >= 'A' && c <= 'Z') ? (char)(c + 32) : c;
    }
    lower[n] = 0;
    const char *base = strrchr(lower, '/');
    base = base ? base + 1 : lower;
    if (!strcmp(base, "eboot.bin") || gs_archive_name_ends(lower, n, "sce_sys/param.sfo")) return GS_ARCHIVE_HINT_DUMP;
    if (gs_archive_split_piece(base)) return GS_ARCHIVE_HINT_SPLIT_PKG;
    if (gs_archive_name_ends(lower, n, ".rar") || gs_archive_name_ends(lower, n, ".zip") ||
        gs_archive_name_ends(lower, n, ".7z") || gs_archive_name_ends(lower, n, ".r00") ||
        gs_archive_name_ends(lower, n, ".z01") || gs_archive_name_ends(lower, n, ".001")) return GS_ARCHIVE_HINT_NESTED;
    return 0;
}
/* Offset added to a reader's "no PKG" code: +4 game folder, +6 split PKG
 * pieces, +5 nested archive, 0 when the names explain nothing. */
static inline int gs_archive_no_pkg_offset(unsigned hints)
{
    return (hints & GS_ARCHIVE_HINT_DUMP) ? 4 : (hints & GS_ARCHIVE_HINT_SPLIT_PKG) ? 6 :
        (hints & GS_ARCHIVE_HINT_NESTED) ? 5 : 0;
}
/* Why writing an extracted package failed, from the errno of the first failed
 * write, flush or close. EFBIG: the file system's file-size limit (FAT32 stops
 * at 4 GiB). Readers report these as write failures, never as a damaged archive:
 * RAR 1009 (EFBIG) or 19; ZIP 2019, 2007 (ENOSPC) or 2011; 7z 3019, 3007 or 3011. */
enum { GS_ARCHIVE_WRITE_FAILED = 1, GS_ARCHIVE_WRITE_TOO_LARGE = 2, GS_ARCHIVE_WRITE_NO_SPACE = 3 };
static inline int gs_archive_write_cause(int error)
{
    return error == EFBIG ? GS_ARCHIVE_WRITE_TOO_LARGE : error == ENOSPC ? GS_ARCHIVE_WRITE_NO_SPACE : GS_ARCHIVE_WRITE_FAILED;
}
typedef int (*GsArchiveProgress)(int64_t done, int64_t total);
typedef void (*GsArchiveDiagnostic)(const char *stage, unsigned entry, int result,
    uint32_t dictionary_kib, uint32_t method, uint64_t unpacked_bytes);
typedef struct { char path[1024]; int64_t size; } GsExtractedPkg;
int gs_extract_rar(const char *first, const char *destination,
    const char *const *names, const char *const *paths, int volume_count,
    GsExtractedPkg *packages, int capacity, GsArchiveProgress progress);
int gs_extract_rar_password(const char *first, const char *destination,
    const char *const *names, const char *const *paths, int volume_count,
    GsExtractedPkg *packages, int capacity, GsArchiveProgress progress, const char *password);
int gs_extract_rar_password_diagnostic(const char *first, const char *destination,
    const char *const *names, const char *const *paths, int volume_count,
    GsExtractedPkg *packages, int capacity, GsArchiveProgress progress, const char *password,
    GsArchiveDiagnostic diagnostic);
int gs_extract_zip(const char *first, const char *destination,
    const char *const *names, const char *const *paths, int volume_count,
    GsExtractedPkg *packages, int capacity, GsArchiveProgress progress);
int gs_extract_7z(const char *first, const char *destination,
    const char *const *names, const char *const *paths, int volume_count,
    GsExtractedPkg *packages, int capacity, GsArchiveProgress progress);
const char *gs_7z_last_error(void);
size_t gs_7z_memory_peak(void);
int gs_7z_extract_local(const char *first, const char *destination, GsExtractedPkg *packages,
    int capacity, GsArchiveProgress progress);
int gs_7z_extract_local_ex(const char *first, const char *destination, GsExtractedPkg *packages,
    int capacity, GsArchiveProgress progress, char *error, int error_capacity);
#ifdef __cplusplus
}
#endif
