#ifndef SSPI_HTTP_RANGE_H
#define SSPI_HTTP_RANGE_H

#include <stddef.h>
#include <stdint.h>

static inline int sspi_http_range_number(const char **cursor, const char *limit, int64_t *out)
{
    const char *p = *cursor;
    int64_t value = 0;
    if (p == limit || *p < '0' || *p > '9') return 0;
    do {
        int digit = *p - '0';
        if (value > (INT64_MAX - digit) / 10) return 0;
        value = value * 10 + digit;
        p++;
    } while (p < limit && *p >= '0' && *p <= '9');
    *cursor = p; *out = value;
    return 1;
}

/* Known-length byte ranges only: unknown totals cannot bind an SSPI resume.
 * Keep semantics aligned with DownloadResumeInfo.TryParseContentRange; both
 * implementations run against the same boundary/malformed-input fixtures. */
static inline int sspi_http_content_range(const char *value, size_t length,
    int64_t *first, int64_t *last, int64_t *total, int *unsatisfied)
{
    *first = *last = *total = -1; *unsatisfied = 0;
    if (!value || length > 256) return 0;
    const char *p = value, *limit = value + length;
    while (p < limit && (*p == ' ' || *p == '\t')) p++;
    while (limit > p && (limit[-1] == ' ' || limit[-1] == '\t')) limit--;
    if (limit - p < 8) return 0;
    const char unit[] = "bytes";
    for (int i = 0; i < 5; i++) {
        char c = p[i];
        if (c >= 'A' && c <= 'Z') c = (char)(c + ('a' - 'A'));
        if (c != unit[i]) return 0;
    }
    if (p[5] != ' ') return 0;
    p += 6;
    int missing = *p == '*';
    int64_t a = -1, b = -1, n;
    if (missing) p++;
    else {
        if (!sspi_http_range_number(&p, limit, &a) || p == limit || *p++ != '-' ||
            !sspi_http_range_number(&p, limit, &b) || b < a) return 0;
    }
    if (p == limit || *p++ != '/' || !sspi_http_range_number(&p, limit, &n) ||
        p != limit || (!missing && n <= b)) return 0;
    *first = a; *last = b; *total = n; *unsatisfied = missing;
    return 1;
}

#endif
