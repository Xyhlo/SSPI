#ifndef RESIDENT_DAEMON
#include "cover_jpeg.h"
#include <stddef.h>
#include <string.h>

#define STB_IMAGE_STATIC
#define STB_IMAGE_IMPLEMENTATION
#define STBI_ONLY_JPEG
#define STBI_NO_STDIO
#define STBI_NO_LINEAR
#define STBI_NO_SIMD
#define STBI_NO_THREAD_LOCALS
#define STBI_NO_FAILURE_STRINGS
#define STBI_MAX_DIMENSIONS 4096
#include <stb/stb_image.h>

#define COVER_MAX_ENCODED (8 * 1024 * 1024)
#define COVER_MAX_PIXELS (4 * 1024 * 1024)

static int cover_dimensions(int width, int height)
{
    return width >= 16 && height >= 16 && width <= 4096 && height <= 4096 &&
        (size_t)width * (size_t)height <= COVER_MAX_PIXELS;
}

/* stb accepts some truncated JPEGs. Require a complete marker stream through
 * EOI, including every segment length and progressive scan, before decoding. */
static int cover_complete_jpeg(const unsigned char *data, int length)
{
    size_t at = 2, limit = (size_t)length;
    int entropy = 0, scans = 0;
    if (length < 4 || data[0] != 0xff || data[1] != 0xd8) return 0;
    while (at < limit) {
        unsigned int marker, size;
        if (entropy) {
            while (at < limit && data[at] != 0xff) ++at;
        } else if (data[at] != 0xff) return 0;
        if (at >= limit) return 0;
        while (at < limit && data[at] == 0xff) ++at;
        if (at >= limit) return 0;
        marker = data[at++];
        if (entropy && (marker == 0 || (marker >= 0xd0 && marker <= 0xd7))) continue;
        if (marker == 0xd9) return scans > 0;
        if (marker == 0xd8 || marker == 0 || (marker >= 0xd0 && marker <= 0xd7)) return 0;
        if (marker == 1) continue; /* TEM is the other lengthless marker. */
        entropy = 0;
        if (limit - at < 2) return 0;
        size = ((unsigned int)data[at] << 8) | data[at + 1];
        if (size < 2 || size > limit - at) return 0;
        at += size;
        if (marker == 0xda) { entropy = 1; ++scans; }
    }
    return 0;
}

int sspi_cover_jpeg_info(const unsigned char *data, int length, int *width, int *height)
{
    int w = 0, h = 0, components = 0;
    if (width) *width = 0;
    if (height) *height = 0;
    if (!data || !width || !height || length <= 0) return SSPI_COVER_JPEG_INVALID_ARGUMENT;
    if (length > COVER_MAX_ENCODED) return SSPI_COVER_JPEG_ENCODED_LIMIT;
    if (!cover_complete_jpeg(data, length) || !stbi_info_from_memory(data, length, &w, &h, &components))
        return SSPI_COVER_JPEG_INVALID_FORMAT;
    /* Enforced here even with SDK headers predating STBI_MAX_DIMENSIONS. */
    if (!cover_dimensions(w, h)) return SSPI_COVER_JPEG_DIMENSION_LIMIT;
    if (components != 1 && components != 3 && components != 4) return SSPI_COVER_JPEG_INVALID_FORMAT;
    *width = w;
    *height = h;
    return SSPI_COVER_JPEG_OK;
}

int sspi_cover_jpeg_decode(const unsigned char *data, int length, unsigned char *rgba,
    int rgba_length, int expected_width, int expected_height)
{
    int w = 0, h = 0, components = 0;
    size_t required;
    unsigned char *decoded;
    int result;
    if (!rgba || rgba_length < 0) return SSPI_COVER_JPEG_INVALID_ARGUMENT;
    if (!cover_dimensions(expected_width, expected_height)) return SSPI_COVER_JPEG_DIMENSION_LIMIT;
    required = (size_t)expected_width * (size_t)expected_height * 4;
    if ((size_t)rgba_length < required) return SSPI_COVER_JPEG_OUTPUT_TOO_SMALL;
    result = sspi_cover_jpeg_info(data, length, &w, &h);
    if (result != SSPI_COVER_JPEG_OK) return result;
    if (w != expected_width || h != expected_height) return SSPI_COVER_JPEG_DIMENSION_MISMATCH;
    decoded = stbi_load_from_memory(data, length, &w, &h, &components, 4);
    if (!decoded) return SSPI_COVER_JPEG_DECODE_FAILED;
    if (w != expected_width || h != expected_height) {
        stbi_image_free(decoded);
        return SSPI_COVER_JPEG_DIMENSION_MISMATCH;
    }
    memcpy(rgba, decoded, required);
    stbi_image_free(decoded);
    return SSPI_COVER_JPEG_OK;
}
#endif
