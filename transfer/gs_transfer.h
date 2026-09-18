#ifndef GS_TRANSFER_H
#define GS_TRANSFER_H
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#define GS_XFER_API 3
#define GS_XFER_CHUNK (16U * 1024U * 1024U)
#define GS_XFER_BUFFER (512U * 1024U)
#define GS_XFER_JOBS 2
#define GS_XFER_LANES 5
enum { GS_QUEUED=1, GS_DOWNLOADING, GS_PAUSED, GS_VALIDATING, GS_COMPLETE, GS_FAILED, GS_CANCELED };
typedef struct {
    int state, lanes, retries, error_code;
    int64_t done, total, network_bytes;
    char error[256];
} GsXferStatus;
/* Bounded operator-facing detail for the preparation phase: admission, source
 * probe and staging-file open. Provider preparation can take minutes, so the
 * manager and UI must keep showing that activity instead of an empty 0%. */
static void gs_preparation_detail(char *out, size_t size, const char *title)
{
    if (!out || !size) return;
    snprintf(out, size, "Preparing %s: probing the source and opening the staging file",
        title && *title ? title : "package");
}
int sspi_xfer_api(void);
int sspi_xfer_init(int http_context);
/* Bounded metadata probe: no transfer job, destination, bitmap or writer. */
int sspi_xfer_probe(int http_context, const char *url, int64_t total,
                    int64_t offset, void *data, unsigned length);
void sspi_xfer_set_module(int module);
int sspi_xfer_start(const char *url, const char *bearer, const char *destination,
                    const char *title, const char *content, const char *sha256, int lanes);
/* Start publishes a positive queued handle after input validation and slot
 * reservation. Admission, source probing and resume verification run under
 * that handle; poll.error may contain nonterminal preparation/recovery detail. */
int sspi_xfer_poll(int handle, GsXferStatus *status);
/* Contiguous completed bytes available at offset; never includes queued writes.
 * -1 means the stream identity failed or the handle no longer exists. */
int64_t sspi_xfer_readable(int handle, int64_t offset);
int sspi_xfer_pause(int handle);
int sspi_xfer_resume(int handle);
int sspi_xfer_cancel(int handle);
int sspi_xfer_destroy(int handle);
int64_t sspi_xfer_durable(const char *destination);
/* Verify an explicitly requested source checksum on retained complete input.
 * Reuses a successful transfer receipt; never downloads or modifies the PKG. */
int sspi_xfer_verify_local(const char *path, const char *destination, int64_t total,
    const char *title, const char *content, const char *sha256, char *error, size_t error_size);
void sspi_xfer_shutdown(void);
#endif
