#ifndef GS_7Z_ALLOCATOR_H
#define GS_7Z_ALLOCATOR_H
#include <stdlib.h>
#include <string.h>
#include <wchar.h>
void *gs_7z_malloc(size_t size);
void *gs_7z_calloc(size_t count, size_t size);
void *gs_7z_realloc(void *pointer, size_t size);
void gs_7z_free(void *pointer);
char *gs_7z_strdup(const char *text);
wchar_t *gs_7z_wcsdup(const wchar_t *text);
#define malloc gs_7z_malloc
#define calloc gs_7z_calloc
#define realloc gs_7z_realloc
#define free gs_7z_free
#define strdup gs_7z_strdup
#define _strdup gs_7z_strdup
#define wcsdup gs_7z_wcsdup
#define _wcsdup gs_7z_wcsdup
#endif
