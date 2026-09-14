/* Only bounded metadata lives here; BGFT owns the remote package payload. */
#define GS_BGFT_METADATA_LIMIT (8U * 1024U * 1024U)
static int gs_native_metadata(GsStage *stage)
{
    unsigned char header[4096], tail[4096], *table = NULL, *prefix = NULL;
    char path[1150], temporary[1160], tail_path[1160]; int rc = -1, fd = -1;
    if (stage->job.expected_kind != 6 && stage->job.expected_kind != 8) return -1;
    if (gs_network_init()) return -1;
    if (sspi_xfer_probe(gs_http, stage->job.url, stage->job.total, 0, header, sizeof(header))) return -1;
    if (gs_pkg_header_error(header, stage->job.total, stage->job.total, stage->job.title, stage->job.content)) return -1;
    if (gs_pkg_be32(header + 0x74) == 0x1e) return -1;
    uint32_t count = gs_pkg_be32(header + 0x10), at = gs_pkg_be32(header + 0x18);
    if (!count || count > 4096 || at < 4096 || (uint64_t)at + count * 32U > GS_BGFT_METADATA_LIMIT) return -1;
    size_t table_size = count * 32U; table = malloc(table_size); if (!table) return -1;
    if (sspi_xfer_probe(gs_http, stage->job.url, stage->job.total, at, table, (unsigned)table_size)) goto done;
    uint64_t needed = (uint64_t)at + table_size; int have_sfo = 0;
    for (uint32_t i = 0; i < count; i++) {
        unsigned char *e = table + i * 32U; uint32_t id = gs_pkg_be32(e);
        if (id != 0x1000 && id != 0x10) continue;
        uint64_t offset = gs_pkg_be32(e + 16), size = gs_pkg_be32(e + 20);
        if (!size || size > 65536 || offset > GS_BGFT_METADATA_LIMIT || size > GS_BGFT_METADATA_LIMIT - offset || offset + size > (uint64_t)stage->job.total) goto done;
        if (offset + size > needed) needed = offset + size;
        if (id == 0x1000) have_sfo = 1;
    }
    if (!have_sfo || needed + table_size + sizeof(header) + sizeof(tail) > GS_BGFT_METADATA_LIMIT ||
        needed > (uint64_t)stage->job.total || stage->canceled || g_stop) goto done;
    prefix = malloc((size_t)needed); if (!prefix) goto done;
    if (sspi_xfer_probe(gs_http, stage->job.url, stage->job.total, 0, prefix, (unsigned)needed) ||
        memcmp(prefix, header, sizeof(header)) || memcmp(prefix + at, table, table_size) ||
        sspi_xfer_probe(gs_http, stage->job.url, stage->job.total, stage->job.total - sizeof(tail), tail, sizeof(tail))) goto done;
    if (stage->canceled || g_stop) goto done;
    snprintf(path, sizeof(path), "%s.bgft-meta", stage->job.destination);
    snprintf(temporary, sizeof(temporary), "%s.tmp", path);
    snprintf(tail_path, sizeof(tail_path), "%s.tail", path);
    fd = open(temporary, O_WRONLY | O_CREAT | O_TRUNC | O_NOFOLLOW, 0600);
    if (fd < 0 || sceKernelWrite(fd, prefix, needed) != (ssize_t)needed || fsync(fd)) goto done;
    close(fd); fd = -1;
    if (rename(temporary, path)) goto done;
    fd = open(tail_path, O_WRONLY | O_CREAT | O_TRUNC | O_NOFOLLOW, 0600);
    if (fd < 0 || sceKernelWrite(fd, tail, sizeof(tail)) != sizeof(tail) || fsync(fd)) goto done;
    close(fd); fd = -1;
    if (gs_pkg_kind(path) != stage->job.expected_kind) goto done;
    gs_log_write("resident", "native-bgft-metadata job=%s metadata_bytes=%llu payload_bytes=%lld payload_staged=0",
        stage->job.id, (unsigned long long)(needed + sizeof(tail)), (long long)stage->job.total);
    stage->done = 0; stage->wire_bytes = 0; rc = 0;
done:
    if (fd >= 0) close(fd);
    free(prefix); free(table); return rc;
}

static int gs_native_candidate_path(const GsStage *stage, const GsJob *candidate)
{
    if (!stage->job.native_bgft) return !strcmp(candidate->destination, stage->job.destination);
    char path[1150]; snprintf(path, sizeof(path), "%s.bgft-meta", stage->job.destination);
    return candidate->native_bgft && !strcmp(candidate->destination, path);
}

static int gs_native_fallback_saved(const GsJob *job)
{
    char path[1150], generation[128]; snprintf(path, sizeof(path), "%s.bgft-fallback", job->destination);
    FILE *file = fopen(path, "rb"); if (!file) return 0;
    int saved = !gs_line(file, generation, sizeof(generation)) && !strcmp(generation, job->generation);
    fclose(file); return saved;
}
