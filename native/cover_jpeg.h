#ifndef SSPI_COVER_JPEG_H
#define SSPI_COVER_JPEG_H

enum sspi_cover_jpeg_result {
    SSPI_COVER_JPEG_OK = 0,
    SSPI_COVER_JPEG_INVALID_ARGUMENT = -1,
    SSPI_COVER_JPEG_ENCODED_LIMIT = -2,
    SSPI_COVER_JPEG_INVALID_FORMAT = -3,
    SSPI_COVER_JPEG_DIMENSION_LIMIT = -4,
    SSPI_COVER_JPEG_DECODE_FAILED = -5,
    SSPI_COVER_JPEG_DIMENSION_MISMATCH = -6,
    SSPI_COVER_JPEG_OUTPUT_TOO_SMALL = -7
};

/* Inputs remain caller-owned; output is tightly packed RGBA with opaque alpha. */
int sspi_cover_jpeg_info(const unsigned char *data, int length, int *width, int *height);
int sspi_cover_jpeg_decode(const unsigned char *data, int length, unsigned char *rgba,
    int rgba_length, int expected_width, int expected_height);

#endif
