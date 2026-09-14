#pragma once
#include <stdint.h>
#include <stddef.h>
#ifdef __cplusplus
extern "C" {
#endif
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
