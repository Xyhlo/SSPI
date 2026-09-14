#ifndef GS_PKG_VALIDATION_H
#define GS_PKG_VALIDATION_H
/* These are structural checks. Payload integrity belongs to the transfer's
 * optional publisher checksum and the PS4 installer, not repeated disk scans. */
static const char *gs_pkg_header_error(const unsigned char *h, int64_t size,
    int64_t expected_size, const char *title, const char *content)
{
    if (size < 0x1000 || (expected_size > 0 && size != expected_size)) return "PKG file size does not match the completed download";
    if (memcmp(h, "\x7f" "CNT", 4)) return "File is not a PS4 PKG";
    if (gs_be64(h + 0x430) != (uint64_t)size) return "PKG declared size does not match the file";
    if (title && *title && (strlen(title) != 9 || memcmp(h + 0x47, title, 9))) return "PKG title ID does not match the selected title";
    if (content && *content && (strlen(content) > 48 || memcmp(h + 0x40, content, strlen(content)))) return "PKG content ID does not match the selected package";
    unsigned char digest[32]; SHA256_CTX sha;
    sha256_init(&sha); sha256_update(&sha, h, 0xfe0); sha256_final(&sha, digest);
    if (memcmp(digest, h + 0xfe0, 32)) return "PKG header checksum does not match";
    uint64_t body = gs_be64(h + 0x20), body_size = gs_be64(h + 0x28);
    uint64_t pfs = gs_be64(h + 0x410), pfs_size = gs_be64(h + 0x418);
    /* Regions may overlap or leave alignment padding. Each must fit the file. */
    if (body_size && (body < 0x1000 || body > (uint64_t)size || body_size > (uint64_t)size - body)) return "PKG body region extends outside the file";
    if (pfs_size && (pfs < 0x1000 || pfs > (uint64_t)size || pfs_size > (uint64_t)size - pfs)) return "PKG PFS region extends outside the file";
    return NULL;
}
#endif
