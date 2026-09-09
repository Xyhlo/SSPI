#pragma once
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
typedef int (*GsArchiveProgress)(int64_t done, int64_t total);
typedef struct { char path[1024]; int64_t size; } GsExtractedPkg;
int gs_extract_rar(const char *first, const char *destination,
    const char *const *names, const char *const *paths, int volume_count,
    GsExtractedPkg *packages, int capacity, GsArchiveProgress progress);
#ifdef __cplusplus
}
#endif
