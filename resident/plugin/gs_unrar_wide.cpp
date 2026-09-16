#include <stddef.h>
#include <stdint.h>
#include <string.h>

// The SDK's libc++ uses 32-bit wchar_t, but its libc wide-string routines use
// 16-bit elements. Only UnRAR and its private libc++ copy bind to these helpers.
// Compile with -fno-builtin so loops cannot turn back into libc wide calls.
static_assert(sizeof(wchar_t) == sizeof(uint32_t), "RAR must match the SDK libc++ wchar_t ABI");

extern "C" {

size_t gs_rar_wcslen(const uint32_t *s)
{
    const uint32_t *end = s;
    while (*end) ++end;
    return end - s;
}

uint32_t *gs_rar_wcscpy(uint32_t *dest, const uint32_t *src)
{
    uint32_t *result = dest;
    while ((*dest++ = *src++)) {}
    return result;
}

int gs_rar_wcscmp(const uint32_t *a, const uint32_t *b)
{
    while (*a && *a == *b) { ++a; ++b; }
    return *a == *b ? 0 : *a < *b ? -1 : 1;
}

int gs_rar_wcsncmp(const uint32_t *a, const uint32_t *b, size_t count)
{
    for (size_t i = 0; i < count; ++i) {
        if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
        if (!a[i]) break;
    }
    return 0;
}

uint32_t *gs_rar_wcschr(const uint32_t *s, uint32_t value)
{
    do {
        if (*s == value) return const_cast<uint32_t *>(s);
    } while (*s++);
    return NULL;
}

uint32_t *gs_rar_wcsrchr(const uint32_t *s, uint32_t value)
{
    const uint32_t *result = NULL;
    do {
        if (*s == value) result = s;
    } while (*s++);
    return const_cast<uint32_t *>(result);
}

uint32_t *gs_rar_wcspbrk(const uint32_t *s, const uint32_t *accept)
{
    for (; *s; ++s)
        if (gs_rar_wcschr(accept, *s)) return const_cast<uint32_t *>(s);
    return NULL;
}

uint32_t *gs_rar_wmemchr(const uint32_t *s, uint32_t value, size_t count)
{
    for (size_t i = 0; i < count; ++i)
        if (s[i] == value) return const_cast<uint32_t *>(s + i);
    return NULL;
}

int gs_rar_wmemcmp(const uint32_t *a, const uint32_t *b, size_t count)
{
    for (size_t i = 0; i < count; ++i)
        if (a[i] != b[i]) return a[i] < b[i] ? -1 : 1;
    return 0;
}

uint32_t *gs_rar_wmemcpy(uint32_t *dest, const uint32_t *src, size_t count)
{
    return static_cast<uint32_t *>(memcpy(dest, src, count * sizeof(*dest)));
}

uint32_t *gs_rar_wmemmove(uint32_t *dest, const uint32_t *src, size_t count)
{
    return static_cast<uint32_t *>(memmove(dest, src, count * sizeof(*dest)));
}

uint32_t *gs_rar_wmemset(uint32_t *dest, uint32_t value, size_t count)
{
    for (size_t i = 0; i < count; ++i) dest[i] = value;
    return dest;
}

}
