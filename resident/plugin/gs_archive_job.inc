/* Native archive continuation: all URLs are resolved by the app before publication. */
#include "gs_unrar.h"
#include <dirent.h>
#define GS_MAX_VOLUMES 512
typedef struct { char name[241], url[8192], path[1024], sha[65]; int64_t size; } GsVolume;
static GsVolume *gs_volumes;
static int gs_volume_count;


static int gs_volume_record(FILE *file, GsVolume *volume)
{
    char line[64]; const char *end; const char *p;
    char root[1024];
    if (gs_encoded_line(file, volume->name, sizeof(volume->name)) ||
        gs_encoded_line(file, volume->url, sizeof(volume->url)) ||
        gs_encoded_line(file, volume->path, sizeof(volume->path)) ||
        gs_encoded_line(file, volume->sha, sizeof(volume->sha)) ||
        gs_line(file, line, sizeof(line)) || parse_number(line, &volume->size, &end) || *end ||
        volume->size <= 0 || volume->size > (int64_t)8 * 1024 * 1024 * 1024 * 1024) return -1;
    if (!volume->name[0] || strchr(volume->name, '/') || strchr(volume->name, '\\') ||
        strchr(volume->name, ':') || strstr(volume->name, "..") ||
        gs_staging_root(volume->path, root, sizeof(root)) < 0 || strcmp(root, gs_job.storage_root) ||
        (strncmp(volume->url, "http://", 7) && strncmp(volume->url, "https://", 8))) return -1;
    if (volume->sha[0]) {
        if (strlen(volume->sha) != 64) return -1;
        for (p = volume->sha; *p; p++) if (!strchr("0123456789abcdefABCDEF", *p)) return -1;
    }
    return 0;
}

static int gs_read_volumes(void)
{
    if (gs_job.local_only) {
        gs_volumes = (GsVolume *)calloc(gs_job.borrowed_source ? GS_MAX_VOLUMES : 1, sizeof(GsVolume));
        if (!gs_volumes) return -1;
        const char *name = strrchr(gs_job.destination, '/'); name = name ? name + 1 : gs_job.destination;
        snprintf(gs_volumes[0].name, sizeof(gs_volumes[0].name), "%s", name);
        snprintf(gs_volumes[0].path, sizeof(gs_volumes[0].path), "%s", gs_job.destination);
        snprintf(gs_volumes[0].sha, sizeof(gs_volumes[0].sha), "%s", gs_job.sha);
        gs_volumes[0].size = gs_job.total; gs_volume_count = 1;
        if (gs_job.borrowed_source) {
            char parent[1024], stem[241]; size_t parent_length = (size_t)(name - gs_job.destination);
            if (parent_length >= sizeof(parent)) return -1;
            memcpy(parent, gs_job.destination, parent_length); parent[parent_length] = 0;
            snprintf(stem, sizeof(stem), "%s", name);
            size_t n = strlen(stem); int digits = 0, prefix = -1;
            if (n > 4 && !strcasecmp(stem + n - 4, ".rar")) {
                for (int i = (int)n - 5; i >= 5; i--) if (!strncasecmp(stem + i - 5, ".part", 5)) {
                    char *end; long first = strtol(stem + i, &end, 10);
                    if (end == stem + n - 4 && first == 1 && end > stem + i && end - (stem + i) <= 6) {
                        prefix = i; digits = (int)(end - (stem + i)); break;
                    }
                }
                for (int i = 1; i < GS_MAX_VOLUMES; i++) {
                    GsVolume *v = &gs_volumes[i];
                    if (prefix >= 0) snprintf(v->name, sizeof(v->name), "%.*s%0*d%s", prefix, stem, digits, i + 1, stem + n - 4);
                    else {
                        int letter = 'r' + (i - 1) / 100;
                        if (letter > 'z') break;
                        snprintf(v->name, sizeof(v->name), "%.*s.%c%02d", (int)n - 4, stem, letter, (i - 1) % 100);
                    }
                    if (snprintf(v->path, sizeof(v->path), "%s%s", parent, v->name) >= (int)sizeof(v->path)) return -1;
                    FILE *part = fopen(v->path, "rb");
                    if (!part) { if (errno == ENOENT) break; return -1; }
                    struct stat st; int valid = !gs_file_stat(part, &st) && st.st_size >= 8;
                    fclose(part);
                    if (!valid || !gs_local_source_check(v->path, gs_job.storage_root, gs_job.storage_token)) return -1;
                    v->size = st.st_size; gs_volume_count++;
                }
            }
        }
        return 0;
    }
    FILE *file; char line[12000]; int i; int64_t count; const char *end; char path[128];
    if (gs_ipc_path(path, sizeof(path), "/job.txt")) return -1;
    file = fopen(path, "rb");
    if (!file) return -1;
    for (i = 0; i < 9; i++) if (gs_line(file, line, sizeof(line))) goto error;
    if (gs_line(file, line, sizeof(line)) || parse_number(line, &count, &end) || *end || count < 1 || count > GS_MAX_VOLUMES) goto error;
    gs_volumes = (GsVolume *)calloc((size_t)count, sizeof(GsVolume));
    if (!gs_volumes) goto error;
    for (i = 0; i < count; i++) if (gs_volume_record(file, &gs_volumes[i])) goto error;
    gs_volume_count = (int)count; fclose(file); return 0;
error:
    free(gs_volumes); gs_volumes = NULL; gs_volume_count = 0; fclose(file); return -1;
}

static int gs_archive_progress(int64_t done, int64_t total)
{
    static uint64_t last, storage_check, control_check;
    uint64_t now = sceKernelGetProcessTime();
    if (!control_check || now - control_check >= 100000) {
        control_check = now;
        gs_archive_control();
    }
    while (gs_paused && !g_stop && !gs_canceled) {
        sceKernelUsleep(100000);
        gs_archive_control();
    }
    if (g_stop || gs_canceled) return -1;
    now = sceKernelGetProcessTime();
    if (now - storage_check >= 250000) {
        storage_check = now;
        if (!gs_storage_matches(gs_job.storage_root, gs_job.storage_token)) return -1;
    }
    int changed_total = gs_job.total != total;
    int finished_now = total > 0 && done == total && gs_done != done;
    gs_done = done; gs_job.total = total;
    if (!last || changed_total || finished_now || now - last >= 1000000) {
        char message[80];
        last = now; gs_done = done;
        if (!total && !done) snprintf(message, sizeof(message), "Extracting: reading archive headers");
        else if (total > 0) snprintf(message, sizeof(message), "Extracting: %lld / %lld MiB", (long long)(done / (1024 * 1024)), (long long)(total / (1024 * 1024)));
        else snprintf(message, sizeof(message), "Extracted %lld MiB", (long long)(done / (1024 * 1024)));
        gs_write_status("extracting", message);
    }
    return 0;
}

static void gs_archive_diagnostic(const char *stage, unsigned entry, int result,
    uint32_t dictionary_kib, uint32_t method, uint64_t unpacked_bytes)
{
    gs_log_write("resident", "archive-rar job=%s stage=%s entry=%u result=%d dictionary_kib=%u method=%u unpacked_bytes=%llu",
        gs_job.id, stage, entry, result, dictionary_kib, method, (unsigned long long)unpacked_bytes);
}

typedef struct {
    int user, entitlement; const char *id, *url, *extra, *name, *icon, *sku;
    uint32_t option; const char *scenario, *release, *type, *subtype; uint64_t size;
} GsBgftParam;
_Static_assert(sizeof(GsBgftParam) == 104, "BGFT parameter ABI");

static unsigned int gs_sfo_u32(const unsigned char *p)
{ return p[0] | ((unsigned int)p[1] << 8) | ((unsigned int)p[2] << 16) | ((unsigned int)p[3] << 24); }

static uint32_t gs_pkg_be32(const unsigned char *p)
{ return ((uint32_t)p[0] << 24) | ((uint32_t)p[1] << 16) | ((uint32_t)p[2] << 8) | p[3]; }

static int gs_sfo_string(const unsigned char *data, size_t size, const char *key, char *out, size_t cap)
{
    if (size < 20 || memcmp(data, "\0PSF", 4)) return -1;
    uint32_t keys = gs_sfo_u32(data + 8), values = gs_sfo_u32(data + 12), count = gs_sfo_u32(data + 16);
    if (count > 256) return -1;
    for (uint32_t i = 0; i < count && 20 + i * 16 + 16 <= size; i++) {
        const unsigned char *e = data + 20 + i * 16;
        uint64_t k = (uint64_t)keys + e[0] + ((uint32_t)e[1] << 8);
        uint64_t v = (uint64_t)values + gs_sfo_u32(e + 12), n = gs_sfo_u32(e + 4);
        if (k + strlen(key) + 1 > size || v + n > size || !n || n >= cap ||
            memcmp(data + k, key, strlen(key) + 1)) continue;
        if (e[2] == 4 && e[3] == 4 && n == 4) snprintf(out, cap, "%08X", gs_sfo_u32(data + v));
        else { memcpy(out, data + v, (size_t)n); out[n] = 0; }
        return 0;
    }
    return -1;
}

static int gs_pkg_sfo(const char *path, const char *key, char *out, size_t cap)
{
    unsigned char h[32], e[32], *data = NULL; int result = -1;
    FILE *file = fopen(path, "rb"); if (!file) return -1;
    if (fread(h, 1, 32, file) != 32) goto done;
    uint32_t count = gs_pkg_be32(h + 16), table = gs_pkg_be32(h + 24);
    if (count > 4096) goto done;
    for (uint32_t i = 0; i < count; i++) {
        if (fseek(file, (long)table + i * 32L, SEEK_SET) || fread(e, 1, 32, file) != 32) goto done;
        if (gs_pkg_be32(e) != 0x1000) continue;
        uint32_t size = gs_pkg_be32(e + 20);
        if (size < 20 || size > 65536) goto done;
        data = (unsigned char *)malloc(size); if (!data) goto done;
        if (fseek(file, gs_pkg_be32(e + 16), SEEK_SET) || fread(data, 1, size, file) != size) goto done;
        result = gs_sfo_string(data, size, key, out, cap); break;
    }
done:
    free(data); fclose(file); return result;
}

static int gs_version_number(const char *v)
{
    unsigned int major, minor; char extra;
    return sscanf(v, "%u.%u%c", &major, &minor, &extra) == 2 && major < 10000 && minor < 100
        ? (int)(major * 100 + minor) : -1;
}

static int gs_installed_patch_version(void)
{
    const char *formats[] = { "/user/patch/%s/sce_sys/param.sfo", "/mnt/ext0/user/patch/%s/sce_sys/param.sfo",
        "/system_data/priv/appmeta/%s/param.sfo", "/user/appmeta/%s/param.sfo", "/mnt/ext0/user/appmeta/%s/param.sfo" };
    char path[256], version[32]; int best = -1;
    const char *pkgs[] = { "/user/patch/%s/patch.pkg", "/mnt/ext0/user/patch/%s/patch.pkg" };
    for (int i = 0; i < 2; i++) {
        snprintf(path, sizeof(path), pkgs[i], gs_job.title);
        if (!gs_pkg_sfo(path, "APP_VER", version, sizeof(version))) {
            int found = gs_version_number(version); if (found > best) best = found;
        }
    }
    for (int i = 0; i < 5; i++) {
        snprintf(path, sizeof(path), formats[i], gs_job.title);
        FILE *file = fopen(path, "rb"); if (!file) continue;
        unsigned char *data = (unsigned char *)malloc(65536); if (!data) { fclose(file); return -1; }
        size_t size = fread(data, 1, 65536, file); fclose(file);
        int found = !gs_sfo_string(data, size, "APP_VER", version, sizeof(version)); free(data);
        if (found && gs_version_number(version) > best) best = gs_version_number(version);
    }
    return best;
}

static const char *gs_pkg_type(const char *path)
{
    unsigned char h[0x80]; FILE *file = fopen(path, "rb"); if (!file) return NULL;
    int ok = fread(h, 1, sizeof(h), file) == sizeof(h); fclose(file); if (!ok) return NULL;
    switch (gs_pkg_be32(h + 0x74)) {
        case 0x1a: return "PS4GD"; case 0x1b: return "PS4AC";
        case 0x1c: return "PS4AL"; case 0x1e: return "PS4DP";
        default: return NULL;
    }
}

static int gs_verify_package(const char *path, int kind)
{
    unsigned char h[0x1000];
    char category[32], title[32], version[32], required[32]; struct stat st;
    FILE *file = fopen(path, "rb"); if (!file) return -1;
    int result = -1;
    if (gs_file_stat(file, &st) || fread(h, 1, sizeof(h), file) != sizeof(h)) goto done;
    if (gs_pkg_be32(h + 0x74) == 0x1e) {
        gs_write_status("failed", "Delta update needs an exact predecessor. Select a full update/backport package."); goto done;
    }
    if (kind == 8 && (gs_pkg_sfo(path, "CATEGORY", category, sizeof(category)) || strcmp(category, "gp") ||
        gs_pkg_sfo(path, "TITLE_ID", title, sizeof(title)) || strcmp(title, gs_job.title) ||
        gs_pkg_sfo(path, "APP_VER", version, sizeof(version)) || gs_version_number(version) < 0)) goto done;
    if (!gs_pkg_sfo(path, "SYSTEM_VER", required, sizeof(required))) {
        unsigned char sw[0x28] = {0}; *(size_t *)sw = sizeof(sw);
        if (!sceKernelGetSystemSwVersion((OrbisKernelSwVersion *)sw)) {
            uint32_t have = 0; memcpy(&have, sw + 0x24, 4);
            uint32_t need = (uint32_t)strtoul(required, NULL, 16);
            if (have && need > have) { gs_write_status("failed", "Package requires newer firmware; select its backport"); goto done; }
        }
    }
    if (kind != 7 && !gs_be64(h + 0x418)) goto done;
    int64_t package_size = gs_job.native_bgft && !strcmp(path, gs_job.destination) ? gs_job.total : st.st_size;
    const char *error = gs_pkg_header_error(h, package_size, 0, gs_job.title, NULL);
    if (error) { gs_write_status("failed", error); goto done; }
    result = 0;
done:
    fclose(file);
    if (result && strcmp(gs_state, "failed")) gs_write_status("failed", "PKG type, metadata or firmware requirement is invalid; file retained");
    return result;
}

static int gs_verify_patch(const char *path) { return gs_verify_package(path, 8); }

static int gs_check_metadata(const char *path, int kind)
{
    unsigned char h[0x1000], digest[32]; SHA256_CTX sha;
    char category[32], title[32] = {0}, content[64] = {0}, header_content[37];
    FILE *file = fopen(path, "rb"); if (!file) return -1;
    int read = fread(h, 1, sizeof(h), file) == sizeof(h); fclose(file); if (!read) return -1;
    sha256_init(&sha); sha256_update(&sha, h, 0xfe0); sha256_final(&sha, digest);
    if (memcmp(digest, h + 0xfe0, 32)) return -1;
    const char *expected = kind == 6 ? "gd" : kind == 8 ? "gp" : "ac";
    if (gs_pkg_sfo(path, "CATEGORY", category, sizeof(category)) || strncmp(category, expected, 2)) return -1;
    if (gs_pkg_sfo(path, "TITLE_ID", title, sizeof(title))) { if (kind != 7) return -1; }
    if (title[0] && strcmp(title, gs_job.title)) return -1;
    memcpy(header_content, h + 0x40, 36); header_content[36] = 0;
    if (!gs_pkg_sfo(path, "CONTENT_ID", content, sizeof(content)) && content[0] && strcmp(content, header_content)) return -1;
    return 0;
}

/* ENTRY_KEYS stores the image-key fingerprint at offset 64. Comparing it
 * requires no decryption and rejects a patch built against a different base. */
static int gs_pkg_key_fingerprint(const char *path, unsigned char *fingerprint, char *content)
{
    unsigned char h[0x80], e[32]; struct stat st; int result = -1;
    FILE *file = fopen(path, "rb"); if (!file) return -1;
    if (gs_file_stat(file, &st) || fread(h, 1, sizeof(h), file) != sizeof(h) || memcmp(h, "\x7f\x43\x4e\x54", 4)) goto done;
    uint32_t count = gs_pkg_be32(h + 16), table = gs_pkg_be32(h + 24);
    if (count > 4096 || (uint64_t)table + (uint64_t)count * 32 > (uint64_t)st.st_size) goto done;
    memcpy(content, h + 0x40, 36); content[36] = 0;
    for (uint32_t i = 0; i < count; i++) {
        if (fseek(file, (long)table + i * 32L, SEEK_SET) || fread(e, 1, 32, file) != 32) goto done;
        if (gs_pkg_be32(e) != 0x10) continue;
        uint64_t offset = gs_pkg_be32(e + 16), size = gs_pkg_be32(e + 20);
        if (size < 96 || offset > (uint64_t)st.st_size || size > (uint64_t)st.st_size - offset ||
            (gs_pkg_be32(e + 8) & 0x80000000u) || fseek(file, (long)(offset + 64), SEEK_SET) || fread(fingerprint, 1, 32, file) != 32) goto done;
        for (int j = 0; j < 32; j++) if (fingerprint[j]) { result = 0; break; }
        break;
    }
done:
    fclose(file); return result;
}

static int gs_patch_key_matches_base(const char *patch, const char *base)
{
    unsigned char a[32], b[32]; char aid[37], bid[37];
    if (gs_pkg_key_fingerprint(patch, a, aid) || gs_pkg_key_fingerprint(base, b, bid)) return -2;
    return strcmp(aid, bid) || memcmp(a, b, 32) ? -1 : 0;
}

static int gs_check_installed_base(const char *patch)
{
    const char *roots[] = { "/user/app", "/mnt/ext0/user/app" }; char base[256];
    for (int i = 0; i < 2; i++) {
        snprintf(base, sizeof(base), "%s/%s/app.pkg", roots[i], gs_job.title);
        FILE *file = fopen(base, "rb"); if (!file) continue;
        fclose(file);
        int result = gs_patch_key_matches_base(patch, base);
        if (result) gs_write_status("failed", result == -1
            ? "Update was built for a different base package. Choose a matching update/backport; game unchanged."
            : "Cannot verify installed base package compatibility. Update retained; installation stopped.");
        return result;
    }
    gs_write_status("failed", "Installed base PKG is unavailable for compatibility checks; update retained.");
    return -1;
}

static int gs_patch_matches_installed(void)
{
    const char *roots[] = { "/system_data/priv/appmeta", "/user/appmeta", "/mnt/ext0/user/appmeta" };
    char path[256]; unsigned char *data; FILE *file; long size;
    for (int root = 0; root < 3; root++) {
        snprintf(path, sizeof(path), "%s/%s/param.sfo", roots[root], gs_job.title);
        file = fopen(path, "rb"); if (!file) continue;
        if (fseek(file, 0, SEEK_END) || (size = ftell(file)) < 20 || size > 1024 * 1024 || fseek(file, 0, SEEK_SET)) { fclose(file); continue; }
        data = (unsigned char *)malloc((size_t)size); if (!data) { fclose(file); return -1; }
        if (fread(data, 1, (size_t)size, file) != (size_t)size || memcmp(data, "\0PSF", 4)) { free(data); fclose(file); continue; }
        fclose(file);
        unsigned int keys = gs_sfo_u32(data + 8), values = gs_sfo_u32(data + 12), count = gs_sfo_u32(data + 16);
        if (count > 128) { free(data); continue; }
        for (unsigned int i = 0; i < count && 20 + i * 16 + 16 <= (unsigned long)size; i++) {
            const unsigned char *e = data + 20 + i * 16;
            uint64_t key = (uint64_t)keys + e[0] + ((unsigned int)e[1] << 8);
            uint64_t value = (uint64_t)values + gs_sfo_u32(e + 12), length = gs_sfo_u32(e + 4);
            if (key + 11 > (uint64_t)size || value + length > (uint64_t)size || length < 36 || length > 128) continue;
            if (!memcmp(data + key, "CONTENT_ID", 11)) {
                int result = memcmp(data + value, gs_job.content, 36) ? -1 : 0;
                free(data); return result;
            }
        }
        free(data);
    }
    return 0; /* Missing optional metadata is not a mismatching content ID. */
}

static int gs_source_tail(FILE *input, const char *path, int64_t size, unsigned char *tail)
{
    if (gs_job.native_bgft && !strcmp(path, gs_job.destination)) {
        char cached[1100]; snprintf(cached, sizeof(cached), "%s.tail", path);
        FILE *file = fopen(cached, "rb"); if (!file) return 0;
        int ok = fread(tail, 1, 4096, file) == 4096 && fgetc(file) == EOF;
        fclose(file); return ok;
    }
    return !fseek(input, size - 4096, SEEK_SET) && fread(tail, 1, 4096, input) == 4096;
}

static int gs_installed_patch_matches(const char *source, int wanted, char *matched, size_t capacity)
{
    const char *roots[] = { "/user/patch", "/mnt/ext0/user/patch" };
    unsigned char a[4096], b[4096]; char path[256], version[32]; struct stat sa, sb;
    FILE *input = fopen(source, "rb"); if (!input) return 0;
    if (gs_file_stat(input, &sa) || sa.st_size < 4096) { fclose(input); return 0; }
    if (gs_job.native_bgft && !strcmp(source, gs_job.destination)) sa.st_size = gs_job.total;
    for (int root = 0; root < 2; root++) {
        snprintf(path, sizeof(path), "%s/%s/patch.pkg", roots[root], gs_job.title);
        FILE *installed = fopen(path, "rb"); if (!installed) continue;
        int matches = !gs_file_stat(installed, &sb) && S_ISREG(sb.st_mode) && sa.st_size == sb.st_size;
        if (matches) matches = !fseek(input, 0, SEEK_SET) && fread(a, 1, sizeof(a), input) == sizeof(a) &&
            fread(b, 1, sizeof(b), installed) == sizeof(b) && !memcmp(a, "\x7f" "CNT", 4) && !memcmp(a, b, sizeof(a));
        if (matches) matches = gs_source_tail(input, source, sa.st_size, a) && !fseek(installed, sb.st_size - sizeof(b), SEEK_SET) &&
            fread(b, 1, sizeof(b), installed) == sizeof(b) && !memcmp(a, b, sizeof(a));
        fclose(installed);
        if (matches && !gs_pkg_sfo(path, "APP_VER", version, sizeof(version)) && gs_version_number(version) >= wanted) {
            snprintf(matched, capacity, "%s", path);
            fclose(input); return 1;
        }
    }
    fclose(input); return 0;
}

static int gs_installed_base_matches(const char *input_path, int verify)
{
    const char *roots[] = { "/user/app", "/mnt/ext0/user/app" };
    unsigned char expected[4096], actual[4096]; struct stat source, installed;
    FILE *input = fopen(input_path, "rb"); if (!input) return 0;
    if (gs_file_stat(input, &source) || source.st_size < 4096 ||
        fread(expected, 1, sizeof(expected), input) != sizeof(expected)) { fclose(input); return 0; }
    if (gs_job.native_bgft && !strcmp(input_path, gs_job.destination)) source.st_size = gs_job.total;
    for (int i = 0; i < 2; i++) {
        char path[256]; snprintf(path, sizeof(path), "%s/%s/app.pkg", roots[i], gs_job.title);
        FILE *file = fopen(path, "rb"); if (!file) continue;
        int match = !gs_file_stat(file, &installed) && installed.st_size == source.st_size &&
            fread(actual, 1, sizeof(actual), file) == sizeof(actual) && !memcmp(expected, actual, sizeof(expected));
        if (match) {
            unsigned char tail[4096];
            match = gs_source_tail(input, input_path, source.st_size, tail) &&
                !fseek(file, source.st_size - sizeof(actual), SEEK_SET) &&
                fread(actual, 1, sizeof(actual), file) == sizeof(actual) && !memcmp(tail, actual, sizeof(tail));
        }
        fclose(file);
        if (match && verify) {
            int prior = gs_transfer_verified; gs_transfer_verified = 0;
            match = !gs_verify_package(path, 6); gs_transfer_verified = prior;
        }
        if (match) { fclose(input); return 1; }
    }
    fclose(input); return 0;
}

static void gs_download_notification(const char *title, const char *detail)
{
    OrbisNotificationRequest notice; memset(&notice, 0, sizeof(notice));
    notice.targetId = -1;
    notice.useIconImageUri = 1;
    snprintf(notice.iconUri, sizeof(notice.iconUri), "file:///user/appmeta/SRCH00001/icon0.png");
    snprintf(notice.message, sizeof(notice.message), "SSPI: %s - %s", title, detail);
    int rc = sceKernelSendNotificationRequest(0, &notice, sizeof(notice), 0);
    gs_log_write("resident", "system-notification result=0x%08X", (unsigned)rc);
}

typedef struct {
    char content[49], digest[65], edges[65];
    int64_t size;
} GsAddonIdentity;

static int gs_addon_digest(FILE *file, int full, char hex[65])
{
    struct stat st; unsigned char buffer[64 * 1024], digest[32]; SHA256_CTX hash;
    if (gs_file_stat(file, &st) || !S_ISREG(st.st_mode) || st.st_size < 4096 || fseek(file, 0, SEEK_SET)) return -1;
    sha256_init(&hash);
    if (full) {
        int64_t remaining = st.st_size;
        while (remaining) {
            size_t n = remaining < (int64_t)sizeof(buffer) ? (size_t)remaining : sizeof(buffer);
            if (g_stop || gs_canceled || fread(buffer, 1, n, file) != n) return -1;
            sha256_update(&hash, buffer, n); remaining -= n;
        }
        if (fgetc(file) != EOF || ferror(file)) return -1;
    } else {
        if (fread(buffer, 1, 4096, file) != 4096) return -1;
        sha256_update(&hash, buffer, 4096);
        if (fseek(file, st.st_size - 4096, SEEK_SET) || fread(buffer, 1, 4096, file) != 4096) return -1;
        sha256_update(&hash, buffer, 4096);
    }
    if (g_stop || gs_canceled) return -1;
    sha256_final(&hash, digest);
    for (int i = 0; i < 32; i++) snprintf(hex + i * 2, 3, "%02x", digest[i]);
    return 0;
}

static int gs_read_addon_identity(const char *journal, GsAddonIdentity *identity)
{
    char path[1200], id[192], generation[64], extra; int version; long long size;
    if (!journal[0]) return 0;
    if (snprintf(path, sizeof(path), "%s.addon", journal) >= (int)sizeof(path)) return -1;
    FILE *file = fopen(path, "rb"); if (!file) return errno == ENOENT ? 0 : -1;
    int fields = fscanf(file, "%d %191s %63s %48s %lld %64s %64s %c", &version, id, generation,
        identity->content, &size, identity->digest, identity->edges, &extra);
    int failed = ferror(file); fclose(file);
    if (failed || fields != 7 || version != 1 || strcmp(id, gs_job.id) ||
        strcmp(generation, gs_job.generation[0] ? gs_job.generation : "-") ||
        strlen(identity->content) != 36 || size < 4096 || size > 8LL * 1024 * 1024 * 1024 * 1024 ||
        strlen(identity->digest) != 64 || strspn(identity->digest, "0123456789abcdef") != 64 ||
        strlen(identity->edges) != 64 || strspn(identity->edges, "0123456789abcdef") != 64) return -1;
    identity->size = size; return 1;
}

static int gs_save_addon_identity(const char *journal)
{
    char path[1200], body[512]; GsAddonIdentity identity; struct stat st;
    FILE *file = fopen(gs_job.destination, "rb"); if (!file) return -1;
    int valid = !gs_file_stat(file, &st) && st.st_size == gs_job.total &&
        !gs_addon_digest(file, 1, identity.digest) && !gs_addon_digest(file, 0, identity.edges);
    fclose(file); if (!valid) return -1;
    if (snprintf(path, sizeof(path), "%s.addon", journal) >= (int)sizeof(path)) return -1;
    int n = snprintf(body, sizeof(body), "1 %s %s %s %lld %s %s\n", gs_job.id,
        gs_job.generation[0] ? gs_job.generation : "-", gs_job.content, (long long)st.st_size, identity.digest, identity.edges);
    return n < 0 || n >= (int)sizeof(body) ? -1 : write_atomic(path, body);
}

static int gs_installed_addon_matches(const char *source, int full)
{
    if (strlen(gs_job.content) != 36 || strspn(gs_job.content + 20, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-") != 16) return 0;
    GsAddonIdentity identity; int saved = gs_read_addon_identity(gs_archive_journal, &identity);
    if (saved < 0 || (saved && (strcmp(identity.content, gs_job.content) || identity.size != gs_job.total))) return 0;
    const char *roots[] = { "/user/addcont", "/mnt/ext0/user/addcont" };
    for (int i = 0; i < 2; i++) {
        char path[256]; struct stat a, b;
        snprintf(path, sizeof(path), "%s/%s/%s/ac.pkg", roots[i], gs_job.title, gs_job.content + 20);
        if (saved) {
            char digest[65]; FILE *installed = fopen(path, "rb"); if (!installed) continue;
            int match = !gs_file_stat(installed, &b) && b.st_size == identity.size &&
                !gs_addon_digest(installed, full, digest) && !strcmp(digest, full ? identity.digest : identity.edges);
            fclose(installed); if (match) return 1;
            continue;
        }
        FILE *input = fopen(source, "rb"), *installed = fopen(path, "rb");
        int match = input && installed && !gs_file_stat(input, &a) && !gs_file_stat(installed, &b) &&
            a.st_size >= 4096 && a.st_size == b.st_size;
        unsigned char left[16384], right[16384]; int64_t remaining = match ? a.st_size : 0;
        if (match && !full) {
            match = fread(left, 1, 4096, input) == 4096 && fread(right, 1, 4096, installed) == 4096 && !memcmp(left, right, 4096) &&
                !fseek(input, (long)(a.st_size - 4096), SEEK_SET) && !fseek(installed, (long)(a.st_size - 4096), SEEK_SET) &&
                fread(left, 1, 4096, input) == 4096 && fread(right, 1, 4096, installed) == 4096 && !memcmp(left, right, 4096);
            remaining = 0;
        }
        while (match && remaining && !g_stop && !gs_canceled) {
            size_t n = remaining < (int64_t)sizeof(left) ? (size_t)remaining : sizeof(left);
            match = fread(left, 1, n, input) == n && fread(right, 1, n, installed) == n && !memcmp(left, right, n);
            remaining -= n;
        }
        if (input) fclose(input);
        if (installed) fclose(installed);
        if (match && !remaining) return 1;
    }
    return 0;
}

static const char *gs_addon_error(int code)
{
    switch ((uint32_t)code) {
        case 0x80A3000Bu: return "The PS4 rejected the add-on package (ADDCONT_BROKEN). Metadata passed local checks; try a matching package. File retained.";
        case 0x80A30006u: return "PS4 rejected the package DRM type. Use an add-on compatible with the installed game. File retained.";
        case 0x80A30004u: case 0x80A3000Au: return "PS4 has not committed the required base game. Finish its installation, then retry the retained add-on.";
        case 0x80A3000Cu: return "Close the running game, then retry the retained add-on.";
        case 0x80A30002u: return "Not enough PS4 installation space. Free space, then retry the retained add-on.";
        default: return "PS4 did not accept the add-on installation. File retained; inspect the API result.";
    }
}

static int gs_install_addon(const char *journal, int accepted)
{
    int module = gs_module("/system/common/lib/libSceAppInstUtil.sprx"), exists = 0;
    int (*install)(const char *, void *), (*app_exists)(const char *, int *);
    if (gs_bind(module, "sceAppInstUtilAppInstallPkg", &install) ||
        gs_bind(module, "sceAppInstUtilAppExists", &app_exists)) {
        gs_write_status("failed", "PS4 add-on installer API is unavailable. PKG retained."); return -1;
    }
    if (app_exists(gs_job.title, &exists) || !exists) {
        gs_write_status("failed", "DLC requires its base game to finish installation. PKG retained."); return -1;
    }
    // AppInstUtil may consume the source. Persist its identity before handing it over.
    if (!accepted && gs_save_addon_identity(journal)) {
        gs_write_status("failed", "DLC_UNCONFIRMED: Could not save the add-on verification record; installation was not submitted."); return -1;
    }
    GsAddonIdentity identity;
    if (accepted && gs_read_addon_identity(journal, &identity) != 1 && access(gs_job.destination, F_OK)) {
        gs_write_status("failed", "DLC_UNCONFIRMED: Original add-on file is unavailable and this older install has no verification record. Remove the queue entry, then add the package again to verify installation."); return -1;
    }
    // Reconcile a previous asynchronous acceptance before submitting it again.
    if (!accepted && !gs_installed_addon_matches(gs_job.destination, 1)) {
        char intent[160]; snprintf(intent, sizeof(intent), "%s -1 0 2 6", gs_job.content);
        if (write_atomic(journal, intent)) {
            gs_write_status("failed", "Add-on install journal could not be saved. PKG retained."); return -1;
        }
        int rc = install(gs_job.destination, NULL);
        gs_log_write("resident", "install-api api=sceAppInstUtilAppInstallPkg rc=0x%08X task=-1 content=%s subtype=7 bytes=%lld path=%s",
            (unsigned)rc, gs_job.content, (long long)gs_job.total, gs_job.destination);
        if (rc) {
            snprintf(intent, sizeof(intent), "%s -1 0 0 6", gs_job.content);
            if (write_atomic(journal, intent)) {
                gs_write_status("failed", "Add-on install journal could not be updated. PKG retained."); return -1;
            }
            char error[512]; snprintf(error, sizeof(error), "%s API=sceAppInstUtilAppInstallPkg rc=0x%08X", gs_addon_error(rc), (unsigned)rc);
            gs_write_status("failed", error); return -1;
        }
    }
    for (int poll = 0, stable = 0; poll < 240 && !g_stop && !gs_canceled; poll++) {
        gs_write_status("installing", "PS4 accepted the add-on; checking installed content");
        if (gs_installed_addon_matches(gs_job.destination, 0)) stable++; else stable = 0;
        if (stable >= 3 && gs_installed_addon_matches(gs_job.destination, 1)) {
            char record[160]; snprintf(record, sizeof(record), "%s -1 1 0 6", gs_job.content);
            if (write_atomic(journal, record)) return -1;
            gs_download_notification(gs_job.title, "[DLC] Installation confirmed");
            gs_log_write("resident", "addon-confirmed job=%s source_present=%d", gs_job.id, access(gs_job.destination, F_OK) == 0);
            return 0;
        }
        sceKernelUsleep(500000);
    }
    int source_present = access(gs_job.destination, F_OK) == 0;
    gs_log_write("resident", "addon-unconfirmed job=%s source_present=%d identity=%d", gs_job.id, source_present,
        gs_read_addon_identity(journal, &identity));
    gs_write_status("failed", source_present
        ? "DLC_UNCONFIRMED: Add-on installation is not yet confirmed. PKG retained; retry checks completion first."
        : "DLC_UNCONFIRMED: Add-on installation is not yet confirmed and the original PKG is no longer present. Retry checks installed content; Remove clears the queue entry.");
    return -1;
}

static int gs_resume_missing_addon(void)
{
    char leaf[300], journal[1150], id[49], extra; int task, complete, pending, version, kind = 7;
    snprintf(leaf, sizeof(leaf), "/packages/%s/install-001.txt", gs_job.id);
    if (gs_ipc_path(journal, sizeof(journal), leaf)) return 0;
    FILE *file = fopen(journal, "rb"); if (!file) return 0;
    int fields = fscanf(file, "%48s %d %d %d %d %d %c", id, &task, &complete, &pending, &version, &kind, &extra);
    fclose(file);
    if ((fields != 5 && fields != 6) || kind != 7 || version != 6 || task != -1 ||
        strcmp(id, gs_job.content) || (complete != 0 && complete != 1) ||
        (pending != 0 && pending != 2) || (!complete && pending != 2)) return 0;
    snprintf(gs_archive_journal, sizeof(gs_archive_journal), "%s", journal);
    gs_log_write("resident", "addon-recovery job=%s source_present=0", gs_job.id);
    if (!gs_install_addon(journal, 1)) { gs_done = gs_job.total; gs_write_status("installed", NULL); }
    return 1;
}

typedef struct {
    uint64_t since, transferred, transferred_total;
    int preparing, copy, initialized;
    uint32_t index;
} GsBgftWatch;

static int gs_bgft_stalled(GsBgftWatch *watch, const GsBgftProgress *p, uint64_t now, int suspended, int local_wait)
{
    int new_piece = watch->initialized && p->index > watch->index;
    int advanced = !watch->initialized || new_piece || p->transferred > watch->transferred ||
        p->transferred_total > watch->transferred_total || p->preparing > watch->preparing || p->copy > watch->copy;
    if (advanced || suspended || local_wait || now < watch->since) watch->since = now;
    if (new_piece) { watch->transferred = 0; watch->preparing = 0; watch->copy = 0; }
    if (p->transferred > watch->transferred) watch->transferred = p->transferred;
    if (p->transferred_total > watch->transferred_total) watch->transferred_total = p->transferred_total;
    if (p->preparing > watch->preparing) watch->preparing = p->preparing;
    if (p->copy > watch->copy) watch->copy = p->copy;
    if (p->index > watch->index) watch->index = p->index;
    watch->initialized = 1;
    uint64_t length = p->length_total ? p->length_total : p->length;
    uint64_t transferred = p->length_total ? p->transferred_total : p->transferred;
    uint64_t limit = (length && transferred >= length ? 600ull : 180ull) * 1000000;
    return !suspended && !local_wait && now - watch->since >= limit;
}

static int gs_park_task(void);

static int gs_bgft_receipt_matches(const char *path, const char *content, int kind, int task, int *complete)
{
    struct stat st; char id[49]; int saved = -1, done = 0, pending = 0, version = 0, subtype = 0;
    if (lstat(path, &st) || !S_ISREG(st.st_mode)) return 0;
    int fd = open(path, O_RDONLY | O_NOFOLLOW); if (fd < 0) return 0;
    FILE *file = fdopen(fd, "rb"); if (!file) { close(fd); return 0; }
    int fields = fscanf(file, "%48s %d %d %d %d %d", id, &saved, &done, &pending, &version, &subtype); fclose(file);
    // Base and patch share a content ID. Legacy records cannot prove the subtype
    // of a recycled task number and must never authorize cross-job removal.
    if (fields != 6 || subtype != kind || (version != 4 && version != 5 && version != 6) ||
        saved != task || strcmp(id, content) || (done != 0 && done != 1) || pending < 0 || pending > 3) return 0;
    *complete = done; return 1;
}

static int gs_find_bgft_receipt(const char *root, int extracted, const char *content, int kind, int task,
    char *receipt, size_t capacity, int *complete)
{
    // Search only SSPI's direct job directories, with a fixed work bound and no symlinks.
    struct stat st; if (lstat(root, &st) || !S_ISDIR(st.st_mode)) return 0;
    DIR *jobs = opendir(root); if (!jobs) return 0;
    struct dirent *job; int count = 0, found = 0;
    while (!found && ++count <= 4096 && (job = readdir(jobs)) != NULL) {
        if (job->d_name[0] == '.' || strchr(job->d_name, '/') || strchr(job->d_name, '\\') ||
            (extracted && strncmp(job->d_name, "extract-", 8))) continue;
        char directory[1150];
        if (snprintf(directory, sizeof(directory), "%s/%s", root, job->d_name) >= (int)sizeof(directory) ||
            lstat(directory, &st) || !S_ISDIR(st.st_mode)) continue;
        for (int ordinal = 1; ordinal <= 256; ordinal++) {
            char path[1200];
            if (snprintf(path, sizeof(path), "%s/install-%03d.txt", directory, ordinal) >= (int)sizeof(path)) break;
            if (gs_bgft_receipt_matches(path, content, kind, task, complete)) {
                if (strlen(path) < capacity) { snprintf(receipt, capacity, "%s", path); found = 1; }
                break;
            }
            // Receipts are created in contiguous package order.
            if (lstat(path, &st) && errno == ENOENT) break;
        }
    }
    closedir(jobs); return found;
}

static int gs_recover_duplicate_bgft(int module, const char *content, int kind)
{
    int (*find)(const char *, int, int *), (*progress)(int, GsBgftProgress *), (*stop)(int), (*remove)(int);
    int task = -1, current = -1, complete = 0; char root[1100], receipt[1200], record[128];
    if (gs_bind(module, "sceBgftServiceDownloadFindTaskByContentId", &find) ||
        gs_bind(module, "sceBgftServiceDownloadGetProgress", &progress) ||
        find(content, kind, &task) || task < 0) return 0;
    int owned = !gs_ipc_path(root, sizeof(root), "/packages") &&
        gs_find_bgft_receipt(root, 0, content, kind, task, receipt, sizeof(receipt), &complete);
    if (!owned) owned = !gs_ipc_path(root, sizeof(root), "/archives") &&
        gs_find_bgft_receipt(root, 0, content, kind, task, receipt, sizeof(receipt), &complete);
    if (!owned) owned = gs_find_bgft_receipt(gs_job.storage_root, 1, content, kind, task, receipt, sizeof(receipt), &complete);
    GsBgftProgress state = {0};
    uint64_t total, done;
    if (!owned || progress(task, &state)) return 0;
    total = state.length_total ? state.length_total : state.length;
    done = state.length_total ? state.transferred_total : state.transferred;
    if (!state.error && !(complete && total && done >= total && state.preparing >= 100 && state.copy >= 100)) return 0;
    // Old tasks retain the old job URL. Never resume them against this new route.
    // Only a failed or completed receipt-owned task may be retired; active/foreign tasks stay intact.
    if (find(content, kind, &current) || current != task ||
        gs_bind(module, "sceBgftServiceDownloadStopTask", &stop) ||
        gs_bind(module, "sceBgftServiceIntDownloadUnregisterTask", &remove)) return 0;
    int stop_rc = stop(task), rc = remove(task);
    gs_log_write("resident", "bgft-duplicate-recovery task=%d content=%s subtype=%d stop=0x%08X unregister=0x%08X receipt=%s",
        task, content, kind, (unsigned)stop_rc, (unsigned)rc, receipt);
    if (rc && (uint32_t)rc != 0x80990019u) return 0;
    snprintf(record, sizeof(record), "%s -1 %d 0 6 %d", content, complete, kind);
    return write_atomic(receipt, record) ? 0 : 1;
}

static int gs_install_archive_pkg(const char *directory, int ordinal, int kind)
{
    char journal[1150], record[256], route[256], id[49]; FILE *file; int task = -1, complete = 0, pending = 0, route_version = 6;
    int bgft, app, users, user = -1, exists = 0, rc, stable = 0, poll_misses = 0, installed_stable = 0, source_retries = 0, copy_confirmed = 0;
    int (*init)(void *), (*user_init)(void *), (*get_user)(int *), (*app_init)(void), (*app_exists)(const char *, int *);
    int (*reg)(GsBgftParam *, int *), (*get_progress)(int, GsBgftProgress *);
    GsBgftParam param; struct { void *heap; size_t size; } heap;
    if (!gs_storage_matches(gs_job.storage_root, gs_job.storage_token)) {
        gs_write_status("failed", "Selected staging drive disconnected; package and installer journal retained"); return -1;
    }
    bgft = gs_module("/system/common/lib/libSceBgft.sprx");
    app = gs_module("/system/common/lib/libSceAppInstUtil.sprx");
    users = gs_module("/system/common/lib/libSceUserService.sprx");
    if (gs_bind(bgft, "sceBgftServiceIntInit", &init) || gs_bind(users, "sceUserServiceGetForegroundUser", &get_user) ||
        gs_bind(app, "sceAppInstUtilInitialize", &app_init) || gs_bind(app, "sceAppInstUtilAppExists", &app_exists) ||
        gs_bind(bgft, "sceBgftServiceDownloadGetProgress", &get_progress)) return -1;
    app_init();
    if (get_user(&user)) { if (!gs_bind(users, "sceUserServiceInitialize", &user_init)) user_init(NULL); if (get_user(&user)) return -1; }
    if (app_exists(gs_job.title, &exists)) return -1;
    if (kind == 8 && exists && gs_patch_matches_installed()) { gs_write_status("failed", "Update content ID does not match the installed base game"); return -1; }
    if (kind == 8 && gs_check_installed_base(gs_job.destination)) return -1;
    if (kind == 8) {
        char version[32];
        if (gs_pkg_sfo(gs_job.destination, "APP_VER", version, sizeof(version)) || gs_version_number(version) < 0 ||
            gs_version_number(version) < gs_installed_patch_version()) {
            gs_write_status("failed", "Older or invalid update cannot replace the installed version; select a matching backport"); return -1;
        }
    }
    if (!gs_bgft_heap) gs_bgft_heap = calloc(1, 1024 * 1024);
    if (!gs_bgft_heap) return -1;
    heap.heap = gs_bgft_heap; heap.size = 1024 * 1024;
    rc = init(&heap); if (rc && (uint32_t)rc != 0x80990001u) return -1;
    snprintf(journal, sizeof(journal), "%s/install-%03d.txt", directory, ordinal);
    snprintf(gs_archive_journal, sizeof(gs_archive_journal), "%s", journal);
    file = fopen(journal, "rb");
    if (file) {
        int saved_kind = 0;
        int fields = fscanf(file, "%48s %d %d %d %d %d", id, &task, &complete, &pending, &route_version, &saved_kind); fclose(file);
        if ((fields != 4 && fields != 5 && fields != 6) || (fields == 6 && saved_kind != kind) ||
            (fields >= 5 && route_version != 4 && route_version != 5 && route_version != 6) ||
            strcmp(id, gs_job.content) || (pending && task < 0 && !(kind == 7 && pending == 2))) return -1;
        if (complete && kind == 7 && gs_installed_addon_matches(gs_job.destination, 1)) return 0;
        if (complete && kind == 8) {
            char installed_path[256], version[32];
            if (!gs_pkg_sfo(gs_job.destination, "APP_VER", version, sizeof(version)) &&
                gs_installed_patch_matches(gs_job.destination, gs_version_number(version), installed_path, sizeof(installed_path))) {
                int prior = gs_transfer_verified; gs_transfer_verified = 0;
                int verified = gs_verify_package(installed_path, 8); gs_transfer_verified = prior;
                if (!verified) return 0;
                char error[192]; snprintf(error, sizeof(error), "Installed update metadata check failed (%d). Task and source PKG retained.", verified);
                gs_write_status("failed", error); return -1;
            }
            complete = 0;
            gs_log_write("resident", "bgft-recovery job=%s task=%d reason=unproven-patch-receipt", gs_job.id, task);
        }
        if (complete && kind == 6) {
            if (gs_stage_stream_finished() == 1 && gs_installed_base_matches(gs_job.destination, 1)) return 0;
            // Older builds certified a dashboard placeholder. Reattach the saved
            // task, but never promote its receipt without installed-file proof.
            complete = 0;
            gs_log_write("resident", "bgft-recovery job=%s task=%d reason=unproven-base-receipt", gs_job.id, task);
        }
        // A pre-5.09 task owns the old URL until it completes. New registrations
        // always use a distinct URL for each extracted PKG.
        if (task >= 0 && (fields == 4 || route_version == 4)) { route_version = 4; gs_package_legacy_route = 1; }
    }
    if (kind == 7 && task < 0) return gs_install_addon(journal, pending == 2);
    // Rebuild only a journal-owned task whose package receive failed.
    // This code alone does not prove an expired remote download link.
    if (task >= 0) {
        int (*find)(const char *, int, int *), current = -1;
        if (gs_bind(bgft, "sceBgftServiceDownloadFindTaskByContentId", &find)) return -1;
        int found = find(gs_job.content, kind, &current);
        if ((uint32_t)found == 0x80990019u || (!found && current < 0)) task = -1;
        else if (found || current != task) {
            gs_write_status("failed", "BGFT_UNRESOLVED:Saved PS4 download no longer matches this installation. Open PS4 Notifications > Downloads and check the matching title. PKG retained."); return -1;
        }
    }
    if (task >= 0) {
        GsBgftProgress prior = {0};
        int prior_rc = get_progress(task, &prior);
        gs_log_write("resident", "bgft-api api=sceBgftServiceDownloadGetProgress rc=0x%08X task=%d error=0x%08X content=%s", (unsigned)prior_rc, task, (unsigned)prior.error, gs_job.content);
        if ((uint32_t)prior_rc == 0x80990019u) {
            task = -1;
        } else if (pending == 3 || (!prior_rc && ((uint32_t)prior.error == 0x8099002Cu || (uint32_t)prior.error == 0x80991404u))) {
            gs_archive_task = task;
            if (gs_cancel_task()) {
                gs_write_status("failed", "Failed legacy install could not be removed. PKG retained."); return -1;
            }
            task = -1;
        }
        if (task < 0) {
            snprintf(record, sizeof(record), "%s -1 0 0 6 %d", gs_job.content, kind);
            if (write_atomic(journal, record)) return -1;
        }
    }
    // A title can appear before its BGFT task finishes copying. A saved task
    // still owns this file/route and must be reattached before advancing.
    // Matching metadata without a completed receipt cannot certify a copy.
    // Register/reattach BGFT and require its preparation, bytes and copy evidence.
    if (kind != 6 && !exists) return -1;
    if (task < 0) {
        if (g_stop || gs_canceled) return -1;
        // Persist intent before registration; an interrupted register is never blindly retried.
        gs_package_legacy_route = 0; route_version = 6;
        snprintf(record, sizeof(record), "%s -1 0 1 6 %d", gs_job.content, kind);
        if (write_atomic(journal, record)) return -1;
        gs_package_url(route, sizeof(route), "http://127.0.0.1:8742");
        // BGFT registration consumes reference-package JSON, not raw PKG bytes.
        // The piece URL inside this document remains the stable .pkg route.
        if (strlen(route) + 5 >= sizeof(route)) return -1;
        strcat(route, ".json");
        memset(&param, 0, sizeof(param));
        param.user = user; param.entitlement = 5; param.id = gs_job.content; param.url = route;
        char display_name[256], package_name[232];
        if (gs_pkg_sfo(gs_job.destination, "TITLE", package_name, sizeof(package_name)))
            snprintf(package_name, sizeof(package_name), "%s", gs_job.title);
        snprintf(display_name, sizeof(display_name), "[%s] %s", kind == 8 ? "UPDATE" : kind == 7 ? "DLC" : "BASE", package_name);
        FILE *icon_check = fopen("/user/appmeta/SRCH00001/icon0.png", "rb");
        param.name = display_name; param.icon = icon_check ? "http://127.0.0.1:8742/sspi-icon.png" : ""; param.option = 0x10000;
        if (icon_check) fclose(icon_check);
        param.scenario = "0"; param.type = gs_pkg_type(gs_job.destination); if (!param.type) return -1; param.subtype = ""; param.size = gs_job.total;
        // Match IntInit with the internal registration ABI (PS4RPI's 0x80990004 fix).
        const char *api = kind == 6 ? "sceBgftServiceIntDownloadRegisterTask" : "sceBgftServiceIntDebugDownloadRegisterPkg";
        if (gs_bind(bgft, api, &reg)) return -1;
        rc = reg(&param, &task);
        gs_log_write("resident", "bgft-register api=%s rc=0x%08X job=%s task=%d content=%s subtype=%d bytes=%lld path=%s local_http_endpoint=%s streaming=%d user=%d",
            api, (unsigned)rc, gs_job.id, task, gs_job.content, kind, (long long)gs_job.total, gs_job.destination, route + 7, gs_stream_slot >= 0, user);
        if ((uint32_t)rc == 0x80F00633u && task < 0 && kind == 6 &&
            !gs_bind(bgft, "sceBgftServiceIntDebugDownloadRegisterPkg", &reg)) {
            api = "sceBgftServiceIntDebugDownloadRegisterPkg";
            rc = reg(&param, &task);
            gs_log_write("resident", "bgft-register api=%s rc=0x%08X task=%d content=%s subtype=%d bytes=%lld path=%s local_http_endpoint=%s",
                api, (unsigned)rc, task, gs_job.content, kind, (long long)gs_job.total, gs_job.destination, route + 7);
        }
        if (task < 0 && ((uint32_t)rc == 0x80990015u || (uint32_t)rc == 0x80990086u) &&
            gs_recover_duplicate_bgft(bgft, gs_job.content, kind)) {
            // Exactly one new registration, now using the current validated PKG route.
            rc = reg(&param, &task);
            gs_log_write("resident", "bgft-register-recovered api=%s rc=0x%08X task=%d content=%s subtype=%d",
                api, (unsigned)rc, task, gs_job.content, kind);
        }
        if (task < 0) {
            char error[384]; snprintf(error, sizeof(error), "%s%s. PKG retained. API=%s rc=0x%08X task=%d",
                gs_bgft_error_terminal(rc) ? "BGFT_TERMINAL:" : "", gs_bgft_error(rc), api, (unsigned)rc, task);
            if ((uint32_t)rc == 0x80990015u || (uint32_t)rc == 0x80990086u)
                snprintf(error, sizeof(error), "BGFT_UNRESOLVED:An existing PS4 download conflicts with this package. Open PS4 Notifications > Downloads, finish or cancel the matching title, then retry. PKG retained. API=%s rc=0x%08X task=%d", api, (unsigned)rc, task);
            // Registration returned without creating a task. Clear only our intent.
            snprintf(record, sizeof(record), "%s -1 0 0 6 %d", gs_job.content, kind);
            write_atomic(journal, record);
            gs_write_status("failed", error); return -1;
        }
        gs_archive_task = task;
        snprintf(record, sizeof(record), "%s %d 0 0 %d %d", gs_job.content, task, route_version, kind);
        if (write_atomic(journal, record)) return -1;
        if (rc) {
            snprintf(record, sizeof(record), "%s %d 0 3 %d %d", gs_job.content, task, route_version, kind);
            if (write_atomic(journal, record)) return -1;
            char error[384]; snprintf(error, sizeof(error), "%s%s. API=%s rc=0x%08X task=%d. Task and PKG retained.",
                gs_bgft_error_terminal(rc) ? "BGFT_TERMINAL:" : "", gs_bgft_error(rc), api, (unsigned int)rc, task);
            gs_write_status("failed", error); return -1;
        }
    }
    gs_archive_task = task;
    char wanted_version[32];
    int wanted = kind == 8 && !gs_pkg_sfo(gs_job.destination, "APP_VER", wanted_version, sizeof(wanted_version))
        ? gs_version_number(wanted_version) : -1;
    if (kind == 8 && wanted < 0) return -1;
    if (g_stop || gs_canceled) return -1;
    if (gs_start_owned_bgft(bgft, task)) return -1;
    // BGFT posts its own added/install notices. Do not duplicate them here.
    uint64_t logged_at = 0;
    GsBgftWatch watch = {0}; int stall_retries = 0;
    while (!g_stop && !gs_canceled) {
        if (gs_stream_slot >= 0 && gs_stage_stream_finished() < 0) {
            gs_write_status("failed", "Resident stream interrupted or validation failed; completed data retained"); return -1;
        }
        // The promoted patch plus its exact package identity takes precedence over
        // stale BGFT counters/errors. Reference JSON totals can exclude metadata.
        char installed_path[256];
        if (kind == 8 && copy_confirmed && gs_installed_patch_matches(gs_job.destination, wanted, installed_path, sizeof(installed_path))) {
            if (++installed_stable >= 8) {
                // The transfer hash certifies our source file, not BGFT's installed
                // destination. Check installed metadata before releasing the source.
                int transferred_verified = gs_transfer_verified; gs_transfer_verified = 0;
                int verified = gs_verify_package(installed_path, 8); gs_transfer_verified = transferred_verified;
                if (verified) {
                    char error[192]; snprintf(error, sizeof(error), "Installed update metadata check failed (%d). Task and source PKG retained.", verified);
                    gs_write_status("failed", error); return -1;
                }
                gs_done = gs_job.total;
                snprintf(record, sizeof(record), "%s %d 1 0 %d %d", gs_job.content, task, route_version, kind);
                int saved = write_atomic(journal, record);
                if (!saved) gs_archive_task = -1;
                return saved;
            }
            sceKernelUsleep(500000); continue;
        }
        installed_stable = 0;
        GsBgftProgress p = {0};
        rc = get_progress(task, &p);
        if (rc) {
            stable = 0;
            if (++poll_misses >= 120) {
                char error[384]; snprintf(error, sizeof(error), "sceBgftServiceDownloadGetProgress rc=0x%08X: %s. Task and PKG retained.", (unsigned int)rc, gs_bgft_error(rc));
                gs_write_status("failed", error); return -1;
            }
            sceKernelUsleep(500000); continue;
        }
        poll_misses = 0;
        uint64_t now = sceKernelGetProcessTime();
        if (!logged_at || now - logged_at >= 5000000 || p.error) {
            logged_at = now;
            gs_log_write("resident", "bgft-progress api=sceBgftServiceDownloadGetProgress job=%s task=%d rc=0x%08X error=0x%08X bytes=%llu total=%llu prepare=%d copy=%d bits=0x%08X stream=%d piece_bytes=%llu piece_total=%llu all_bytes=%llu all_total=%llu index=%u count=%u seconds=%u seconds_total=%u idle_ms=%llu",
                gs_job.id, task, (unsigned)rc, (unsigned)p.error, (unsigned long long)(p.transferred_total ? p.transferred_total : p.transferred),
                (unsigned long long)(p.length_total ? p.length_total : p.length), p.preparing, p.copy, p.bits, gs_stage_stream_finished(),
                (unsigned long long)p.transferred, (unsigned long long)p.length, (unsigned long long)p.transferred_total,
                (unsigned long long)p.length_total, p.index, p.count, p.seconds, p.seconds_total,
                (unsigned long long)(watch.initialized && now >= watch.since ? (now - watch.since) / 1000 : 0));
        }
        if (p.error) {
            // Restart our existing task after a temporary loopback-source failure.
            // Unregistering it here leaves a partially registered dashboard title
            // and turns the next attempt into an installed-content conflict.
            if ((uint32_t)p.error == 0x80991404u && source_retries < 3 &&
                gs_stage_stream_finished() >= 0 && gs_storage_matches(gs_job.storage_root, gs_job.storage_token)) {
                int (*stop_task)(int);
                if (!gs_bind(bgft, "sceBgftServiceDownloadStopTask", &stop_task) && !stop_task(task)) {
                    source_retries++; stable = 0; memset(&watch, 0, sizeof(watch));
                    gs_write_status("installing", "PS4 source handoff interrupted; retrying the same task with downloaded data retained");
                    gs_log_write("resident", "bgft-source-retry job=%s task=%d attempt=%d", gs_job.id, task, source_retries);
                    for (int wait = 0; wait < 20 * source_retries && !g_stop && !gs_canceled; wait++) sceKernelUsleep(100000);
                    if (gs_start_owned_bgft(bgft, task)) return -1;
                    sceKernelUsleep(1000000);
                    continue;
                }
            }
            char error[384]; snprintf(error, sizeof(error), "%s%s. API=sceBgftServiceDownloadGetProgress rc=0x%08X error=0x%08X task=%d. PKG retained.",
                gs_bgft_error_terminal(p.error) ? "BGFT_TERMINAL:" : "", gs_bgft_error(p.error), (unsigned)rc, (unsigned)p.error, task);
            gs_write_status("failed", error); return -1;
        }
        if (gs_bgft_stalled(&watch, &p, now, gs_paused, gs_stream_slot >= 0 && !gs_stage_stream_finished())) {
            gs_log_write("resident", "bgft-stall job=%s task=%d attempt=%d bytes=%llu total=%llu prepare=%d copy=%d",
                gs_job.id, task, stall_retries, (unsigned long long)p.transferred_total,
                (unsigned long long)p.length_total, p.preparing, p.copy);
            int (*stop_task)(int);
            if (stall_retries < 1 && !gs_bind(bgft, "sceBgftServiceDownloadStopTask", &stop_task) && !stop_task(task)) {
                stall_retries++; stable = 0; memset(&watch, 0, sizeof(watch));
                gs_write_status("installing", "PS4 download stopped advancing; resuming the same task with progress retained");
                for (int wait = 0; wait < 30 && !g_stop && !gs_canceled; wait++) sceKernelUsleep(100000);
                if (g_stop || gs_canceled) return -1;
                if (!gs_start_owned_bgft(bgft, task)) continue;
            }
            int parked = gs_park_task();
            char error[384]; snprintf(error, sizeof(error), "PS4 task stalled (task=%d prepare=%d copy=%d). %s; inspect PS4 Notifications and retry. Progress and task journal retained.",
                task, p.preparing, p.copy, parked ? "Stop unconfirmed" : "Task stopped safely");
            gs_write_status("failed", error); return -1;
        }
        if (gs_paused) { sceKernelUsleep(100000); continue; }
        gs_done = (int64_t)(p.transferred_total ? p.transferred_total : p.transferred);
        if (gs_job.native_bgft) { if (gs_done > gs_job.total) gs_done = gs_job.total; gs_network_bytes = gs_done; }
        uint64_t reported_length = p.length_total ? p.length_total : p.length;
        uint64_t reported_done = p.length_total ? p.transferred_total : p.transferred;
        int native_downloading = gs_job.native_bgft && !(reported_length && reported_done >= reported_length);
        int streaming = (gs_stream_slot >= 0 && !gs_stage_stream_finished()) || native_downloading;
        gs_write_status(streaming ? "feeding" : "installing", streaming
            ? (gs_job.native_bgft ? "PS4 is downloading the package directly" : p.preparing < 100 ? "Downloading; PS4 is preparing the install task" : "Downloading; streaming the local package to PS4")
            : (p.preparing < 100 ? "PS4 is preparing installation; download retained" : NULL));
        // Copy percentage alone has produced false completion on hardware. Require
        // the whole PKG to have been served or counted by this owned BGFT task.
        uint64_t transferred = p.transferred_total ? p.transferred_total : p.transferred;
        uint64_t payload = p.length_total ? p.length_total : p.length;
        int base_payload_complete = kind == 6 && payload > 0 && transferred >= payload;
        if (gs_stage_stream_finished() == 1 && p.preparing >= 100 && p.copy >= 100 &&
            ((payload > 0 && transferred >= payload) || gs_served || transferred >= (uint64_t)gs_job.total)) copy_confirmed = 1;
        if (gs_stage_stream_finished() == 1 && p.preparing >= 100 && p.copy >= 100 && (base_payload_complete || gs_served || transferred >= (uint64_t)gs_job.total)) stable++;
        else stable = 0;
        if (stable >= 8) {
            // Updates require the exact installed image identity and metadata
            // above. Version/copy counters cannot distinguish same-version backports.
            int confirmed = kind == 6 ? (!app_exists(gs_job.title, &exists) && exists && gs_installed_base_matches(gs_job.destination, 0))
                : kind == 7 ? gs_installed_addon_matches(gs_job.destination, 1) : 0;
            if (!confirmed) {
                if (stable >= 240) {
                    gs_write_status("failed", "PS4 finished copying but installed version is unconfirmed. Task and PKG retained; inspect Notifications."); return -1;
                }
                sceKernelUsleep(500000); continue;
            }
            snprintf(record, sizeof(record), "%s %d 1 0 %d %d", gs_job.content, task, route_version, kind);
            int saved = write_atomic(journal, record);
            if (!saved) gs_archive_task = -1;
            return saved;
        }
        sceKernelUsleep(500000);
    }
    return -1;
}

static int gs_pause_archive_task(int paused)
{
    int (*action)(int);
    if (gs_archive_task < 0) return 0;
    int module = gs_module("/system/common/lib/libSceBgft.sprx");
    if (gs_bind(module, paused ? "sceBgftServiceDownloadPauseTask" : "sceBgftServiceDownloadResumeTask", &action)) return -1;
    return action(gs_archive_task);
}

static int gs_cancel_task(void)
{
    int (*stop)(int), (*remove)(int); int module;
    if (gs_archive_task < 0 && gs_archive_journal[0]) {
        char id[49]; int task, complete, pending, version;
        FILE *journal = fopen(gs_archive_journal, "rb");
        if (journal) {
            int fields = fscanf(journal, "%48s %d %d %d %d", id, &task, &complete, &pending, &version); fclose(journal);
            if (fields == 5 && !strcmp(id, gs_job.content) && pending == 2 && !complete) return -1;
        }
    }
    if (gs_archive_task < 0) return 0;
    module = gs_module("/system/common/lib/libSceBgft.sprx");
    if (gs_bind(module, "sceBgftServiceDownloadStopTask", &stop) ||
        gs_bind(module, "sceBgftServiceIntDownloadUnregisterTask", &remove)) return -1;
    stop(gs_archive_task);
    int rc = remove(gs_archive_task);
    gs_log_write("resident", "bgft-unregister job=%s task=%d result=0x%08X", gs_job.id, gs_archive_task, (unsigned)rc);
    if (rc && (uint32_t)rc != 0x80990019u) return -1;
    if (gs_archive_journal[0]) {
        char record[96]; snprintf(record, sizeof(record), "%s -1 0 0 6", gs_job.content);
        if (write_atomic(gs_archive_journal, record)) return -1;
    }
    gs_archive_task = -1; return 0;
}

static int gs_park_task(void)
{
    if (gs_archive_task < 0) return 0;
    int (*stop)(int), module = gs_module("/system/common/lib/libSceBgft.sprx");
    if (gs_bind(module, "sceBgftServiceDownloadStopTask", &stop)) return -1;
    int rc = stop(gs_archive_task);
    gs_log_write("resident", "bgft-park job=%s task=%d result=0x%08X journal=retained", gs_job.id, gs_archive_task, (unsigned)rc);
    if (rc) return -1;
    // Keep the durable task ID for the next attempt. Re-registering the title
    // would collide with the placeholder created by this task.
    gs_validated = 0;
    gs_archive_task = -1;
    return 0;
}

static int gs_retire_parked_package(const char *job_id, const char *content, int kind)
{
    char path[1200], leaf[300], id[49], record[128], extra; int task, complete, pending, version, saved_kind = kind;
    snprintf(leaf, sizeof(leaf), "/packages/%s/install-001.txt", job_id);
    if (gs_ipc_path(path, sizeof(path), leaf)) return -1;
    FILE *file = fopen(path, "rb");
    if (!file) return errno == ENOENT ? 0 : -1;
    int fields = fscanf(file, "%48s %d %d %d %d %d %c", id, &task, &complete, &pending, &version, &saved_kind, &extra); fclose(file);
    if ((fields != 5 && fields != 6) || saved_kind != kind || strcmp(id, content) || version != 6 || task < -1 ||
        (complete != 0 && complete != 1) || pending < 0 || pending > 3) return -1;
    // Release SSPI tracking after its worker exits, without claiming to cancel a PS4 add-on operation.
    // Preserve acceptance so a retry checks completion instead of submitting twice.
    if (kind == 7 && task == -1 && pending == 2) {
        gs_log_write("resident", "addon-tracking-released job=%s outcome=unconfirmed", job_id); return 0;
    }
    if (pending && task < 0) return -1;
    if (complete || task < 0) return 0;
    int (*stop)(int), (*remove)(int), module = gs_module("/system/common/lib/libSceBgft.sprx");
    if (gs_bind(module, "sceBgftServiceDownloadStopTask", &stop) ||
        gs_bind(module, "sceBgftServiceIntDownloadUnregisterTask", &remove)) return -1;
    stop(task);
    int rc = remove(task);
    gs_log_write("resident", "bgft-retire-parked job=%s task=%d result=0x%08X", job_id, task, (unsigned)rc);
    if (rc && (uint32_t)rc != 0x80990019u) return -1;
    snprintf(record, sizeof(record), "%s -1 0 0 6", id);
    return write_atomic(path, record);
}

static int gs_verify_volume(GsVolume *volume);

static int gs_archive_download_volume(GsVolume *volume)
{
    struct stat st; char partial[1050];
    snprintf(gs_job.url, sizeof(gs_job.url), "%s", volume->url);
    snprintf(gs_job.sha, sizeof(gs_job.sha), "%s", volume->sha);
    gs_job.total = volume->size;
    FILE *existing = fopen(volume->path, "rb");
    int present = existing && !gs_file_stat(existing, &st) && st.st_size == volume->size;
    if (existing) fclose(existing);
    if (present) return 0;
    if (gs_job.local_only) return -1;
    snprintf(partial, sizeof(partial), "%s.resident.part", volume->path);
    char destination[1100], original[1024];
    snprintf(destination, sizeof(destination), "%s.resident", volume->path);
    if (sspi_xfer_durable(destination) == volume->size) {
        snprintf(original, sizeof(original), "%s", volume->path);
        snprintf(volume->path, sizeof(volume->path), "%s", partial);
        int valid = !gs_verify_volume(volume);
        snprintf(volume->path, sizeof(volume->path), "%s", original);
        if (valid) return !gs_storage_matches(gs_job.storage_root, gs_job.storage_token) || rename(partial, original) ? -1 : 0;
    }
    if (gs_network_init()) return -1;
    if (gs_download_stream(partial) || rename(partial, volume->path)) return -1;
    return 0;
}

static int gs_verify_volume(GsVolume *volume)
{
    FILE *file = fopen(volume->path, "rb"); unsigned char signature[8], hash[32], buffer[128 * 1024];
    SHA256_CTX sha; char hex[65]; size_t count, i; struct stat st;
    if (!file) return -1;
    if (gs_file_stat(file, &st) || st.st_size != volume->size || fread(signature, 1, 8, file) != 8 ||
        ((memcmp(signature, "Rar!\x1a\x07", 6) || (signature[6] != 0 && (signature[6] != 1 || signature[7] != 0))) &&
         memcmp(signature, "PK\x03\x04", 4) && memcmp(signature, "PK\x05\x06", 4) &&
         memcmp(signature, "7z\xbc\xaf\x27\x1c", 6) && memcmp(signature, "\x7f" "CNT", 4))) { fclose(file); return -1; }
    if (volume->sha[0]) {
        rewind(file); sha256_init(&sha);
        while ((count = fread(buffer, 1, sizeof(buffer), file)) > 0) {
            if (g_stop || gs_canceled) { fclose(file); return -1; }
            sha256_update(&sha, buffer, count);
        }
        if (ferror(file)) { fclose(file); return -1; }
        sha256_final(&sha, hash);
        for (i = 0; i < 32; i++) snprintf(hex + i * 2, 3, "%02x", hash[i]);
        if (strcasecmp(hex, volume->sha)) { fclose(file); return -1; }
    }
    fclose(file); return 0;
}

static int gs_pkg_kind(const char *path)
{
    unsigned char h[0x80]; FILE *file = fopen(path, "rb"); uint32_t type, flags;
    if (!file) return -1;
    if (fread(h, 1, sizeof(h), file) != sizeof(h)) { fclose(file); return -1; }
    fclose(file);
    type = (uint32_t)h[0x74] << 24 | (uint32_t)h[0x75] << 16 | (uint32_t)h[0x76] << 8 | h[0x77];
    flags = (uint32_t)h[0x78] << 24 | (uint32_t)h[0x79] << 16 | (uint32_t)h[0x7a] << 8 | h[0x7b];
    if (type == 0x1b || type == 0x1c) return 7;
    if (type == 0x1e || (type == 0x1a && (flags & 0x40300000))) return 8;
    return type == 0x1a ? 6 : -1;
}

static int gs_prepare_pkg(GsExtractedPkg *pkg)
{
    unsigned char header[0x80]; FILE *file = fopen(pkg->path, "rb");
    if (!file) return -1;
    if (fread(header, 1, sizeof(header), file) != sizeof(header)) { fclose(file); return -1; }
    fclose(file);
    memcpy(gs_job.content, header + 0x40, 36); gs_job.content[36] = 0;
    if (!gs_job.title[0] || (gs_job.borrowed_source && gs_job.archive == 1)) { memcpy(gs_job.title, header + 0x47, 9); gs_job.title[9] = 0; }
    snprintf(gs_job.destination, sizeof(gs_job.destination), "%s", pkg->path);
    gs_job.total = pkg->size; gs_job.sha[0] = 0;
    return gs_validate(pkg->path);
}

static int gs_run_package(void)
{
    char directory[1024], packages[1150], leaf[256]; struct stat owned;
    snprintf(leaf, sizeof(leaf), "/packages/%s", gs_job.id);
    if (gs_ipc_path(packages, sizeof(packages), "/packages") || gs_ipc_path(directory, sizeof(directory), leaf)) return -1;
    mkdir(packages, 0700); mkdir(directory, 0700);
    int kind = gs_pkg_kind(gs_job.destination);
    int result = -1;
    if (kind < 0 || (gs_job.expected_kind && kind != gs_job.expected_kind) || gs_check_metadata(gs_job.destination, kind)) {
        gs_write_status("failed", "PKG header and embedded type/title disagree; installation stopped"); return -1;
    }
    if (kind == 8 && gs_check_installed_base(gs_job.destination)) return -1;
    if (gs_verify_package(gs_job.destination, kind)) return -1;
    if (!lstat(directory, &owned) && S_ISDIR(owned.st_mode) && kind >= 0 && !g_stop && !gs_canceled)
        result = gs_install_archive_pkg(directory, 1, kind);
    if (gs_canceled) {
        if (gs_cancel_task()) { gs_release = 0; gs_write_status("failed", "BGFT cancellation unconfirmed; ownership retained"); }
        else gs_write_status("canceled", NULL);
    } else if (result) { if (strcmp(gs_state, "failed")) gs_write_status("failed", "Package install is unconfirmed; PKG and BGFT journal retained"); }
    else gs_write_status("installed", NULL);
    return result;
}

static int gs_read_packages(const char *directory, GsExtractedPkg *packages)
{
    char marker[1150], leaf[40], line[120]; int count = 0;
    snprintf(marker, sizeof(marker), "%s/packages.txt", directory);
    FILE *file = fopen(marker, "rb");
    if (!file) return -1;
    while (fgets(line, sizeof(line), file)) {
        long long size; int ordinal; char suffix, canonical[40];
        if (count == 256 || sscanf(line, "%39s %lld", leaf, &size) != 2 || size < 0 ||
            sscanf(leaf, "pkg-%3d.pkg%c", &ordinal, &suffix) != 1 || strlen(leaf) != 11 || ordinal < 1 || ordinal > 256) {
            fclose(file); return 0;
        }
        snprintf(canonical, sizeof(canonical), "pkg-%03d.pkg", ordinal);
        if (strcmp(leaf, canonical)) { fclose(file); return 0; }
        snprintf(packages[count].path, sizeof(packages[count].path), "%s/%s", directory, leaf);
        packages[count++].size = size;
    }
    int failed = ferror(file); fclose(file);
    return failed ? 0 : count;
}

static int gs_save_packages(const char *directory, GsExtractedPkg *packages, int count)
{
    char marker[1150], body[20000]; size_t used = 0;
    for (int i = 0; i < count; i++) {
        const char *leaf = strrchr(packages[i].path, '/');
        int n = snprintf(body + used, sizeof(body) - used, "%s %lld\n", leaf ? leaf + 1 : packages[i].path, (long long)packages[i].size);
        if (n < 0 || (size_t)n >= sizeof(body) - used) return -1;
        used += n;
    }
    snprintf(marker, sizeof(marker), "%s/packages.txt", directory);
    return write_atomic(marker, body);
}

static int gs_archive_sync_directory(const char *directory)
{
    int fd = open(directory, O_RDONLY | O_DIRECTORY | O_NOFOLLOW);
    if (fd < 0) return -1;
    int rc = fsync(fd);
    /* exFAT may not implement directory fsync; file receipts are still flushed. */
    if (rc && (errno == EINVAL || errno == ENOTSUP) && gs_job.storage_token[0] &&
        gs_storage_matches(gs_job.storage_root, gs_job.storage_token)) rc = 0;
    if (close(fd)) rc = -1;
    return rc;
}

static int gs_prepare_archive_extraction(const char *directory)
{
    char expected[1024], receipt[1150], path[1150], body[256], stored[256], leaf[256]; struct stat st;
    int existing_receipt = 0;
    if (gs_job.local_only || gs_job.generation[0]) snprintf(expected, sizeof(expected), "%s/extract-%s", gs_job.storage_root, gs_job.id);
    else {
        snprintf(leaf, sizeof(leaf), "/archives/%s", gs_job.id);
        if (gs_ipc_path(expected, sizeof(expected), leaf)) return -1;
    }
    if (strcmp(expected, directory) || lstat(directory, &st) || !S_ISDIR(st.st_mode)) return -1;
    snprintf(path, sizeof(path), "%s/packages.txt", directory);
    if (!lstat(path, &st) || errno != ENOENT) {
        gs_write_status("failed", "Archive package journal cannot be read; extraction files and install ownership retained."); return -1;
    }
    snprintf(receipt, sizeof(receipt), "%s/extraction-in-progress.txt", directory);
    snprintf(body, sizeof(body), "1\n%s\n256\n", gs_job.id);
    if (!lstat(receipt, &st)) {
        if (!S_ISREG(st.st_mode)) goto invalid_receipt;
        int fd = open(receipt, O_RDONLY | O_NOFOLLOW);
        if (fd < 0) goto invalid_receipt;
        FILE *file = fdopen(fd, "rb");
        if (!file) { close(fd); goto invalid_receipt; }
        size_t n = fread(stored, 1, sizeof(stored), file);
        int valid = !ferror(file) && n == strlen(body) && !memcmp(stored, body, n);
        fclose(file);
        if (!valid) goto invalid_receipt;
        existing_receipt = 1;
    } else if (errno != ENOENT) goto invalid_receipt;
    // Claim only absent deterministic outputs. Existing install journals take
    // precedence even if the package list was lost or manually removed.
    for (int ordinal = 1; ordinal <= 256; ordinal++) {
        snprintf(path, sizeof(path), "%s/install-%03d.txt", directory, ordinal);
        if (!lstat(path, &st) || errno != ENOENT) {
            if (existing_receipt) goto interrupted;
            gs_write_status("failed", "Archive install ownership exists without its package list. Files retained; inspect PS4 Notifications."); return -1;
        }
        for (int partial = 0; partial < 2; partial++) {
            snprintf(path, sizeof(path), "%s/pkg-%03d.pkg%s", directory, ordinal, partial ? ".part" : "");
            if (!lstat(path, &st)) {
                if (existing_receipt) goto interrupted;
                gs_write_status("failed", "Previous extraction files have no ownership receipt. Files retained; start a new archive job."); return -1;
            } else if (errno != ENOENT) goto invalid_receipt;
        }
    }
    if (g_stop || gs_canceled) return -1;
    // A handled decoder error removes its owned outputs. With no package or
    // install journal left, the matching claim can safely be used for a retry.
    if (existing_receipt) return gs_archive_sync_directory(directory);
    // No extraction starts until both this receipt and its directory entry are durable.
    int fd = open(receipt, O_WRONLY | O_CREAT | O_EXCL, 0600);
    if (fd < 0) goto invalid_receipt;
    FILE *file = fdopen(fd, "wb");
    if (!file) { close(fd); unlink(receipt); goto invalid_receipt; }
    int failed = fwrite(body, 1, strlen(body), file) != strlen(body);
    if (fflush(file) || fsync(fileno(file))) failed = 1;
    if (fclose(file)) failed = 1;
    if (failed || gs_archive_sync_directory(directory)) { unlink(receipt); goto invalid_receipt; }
    return 0;
interrupted:
    gs_write_status("failed", "Archive extraction was interrupted. Files retained; remove this job and add the archive again to retry.");
    return -1;
invalid_receipt:
    gs_write_status("failed", "Archive extraction ownership could not be confirmed. Files retained; start a new archive job.");
    return -1;
}

static int gs_finish_archive_extraction(const char *directory)
{
    char receipt[1150];
    // Persist packages.txt before retiring the interruption marker.
    if (gs_archive_sync_directory(directory)) return -1;
    snprintf(receipt, sizeof(receipt), "%s/extraction-in-progress.txt", directory);
    if (unlink(receipt) && errno != ENOENT) return -1;
    return gs_archive_sync_directory(directory);
}

static int gs_archive_install_receipt(int64_t total)
{
    if (!gs_job.generation[0]) return 0;
    char receipt[1150], body[640], encoded_id[260], encoded_content[100], leaf[256];
    snprintf(leaf, sizeof(leaf), "/installed-%s.txt", gs_job.id);
    if (gs_ipc_path(receipt, sizeof(receipt), leaf)) return -1;
    gs_b64encode((const unsigned char *)gs_job.id, strlen(gs_job.id), encoded_id, sizeof(encoded_id));
    gs_b64encode((const unsigned char *)gs_job.content, strlen(gs_job.content), encoded_content, sizeof(encoded_content));
    snprintf(body, sizeof(body), "1\n%s\n%s\n%lld\n%s\n", encoded_id, encoded_content, (long long)total, gs_job.generation);
    return write_atomic(receipt, body);
}

static int gs_run_archive(void)
{
    char directory[1024], alias[1300], first[1300], leaf[256], archives[1150];
    const char *names[GS_MAX_VOLUMES], *paths[GS_MAX_VOLUMES];
    GsExtractedPkg *packages = NULL; int kinds[256], versions[256]; char content_ids[256][49]; int count = 0, result = -1, extraction_started = 0;
    char expected_content[49]; snprintf(expected_content, sizeof(expected_content), "%s", gs_job.content);
    int64_t original_total = gs_job.total;
    if (gs_job.local_only || gs_job.generation[0]) snprintf(directory, sizeof(directory), "%s/extract-%s", gs_job.storage_root, gs_job.id);
    else {
        snprintf(leaf, sizeof(leaf), "/archives/%s", gs_job.id);
        if (gs_ipc_path(directory, sizeof(directory), leaf)) goto done;
    }
    if (!gs_storage_matches(gs_job.storage_root, gs_job.storage_token)) goto done;
    if (!gs_job.local_only) {
        if (gs_ipc_path(archives, sizeof(archives), "/archives")) goto done;
        mkdir(archives, 0700);
    }
    mkdir(directory, 0700);
    struct stat owned;
    if (lstat(directory, &owned) || !S_ISDIR(owned.st_mode)) goto done;
    char terminal_path[1150], terminal_line[256], terminal_generation[80], terminal_content[80];
    snprintf(terminal_path, sizeof(terminal_path), "%s/complete.txt", directory);
    FILE *terminal = fopen(terminal_path, "rb");
    if (terminal) {
        int finished = !gs_line(terminal, terminal_line, sizeof(terminal_line)) && !strcmp(terminal_line, "1") &&
            !gs_line(terminal, terminal_line, sizeof(terminal_line)) && !strcmp(terminal_line, gs_job.id) &&
            !gs_line(terminal, terminal_generation, sizeof(terminal_generation)) && !strcmp(terminal_generation, gs_job.generation) &&
            !gs_line(terminal, terminal_content, sizeof(terminal_content)) && strlen(terminal_content) == 36;
        fclose(terminal);
        if (finished) {
            snprintf(gs_job.content, sizeof(gs_job.content), "%s", terminal_content);
            if (gs_archive_install_receipt(original_total)) goto done;
            gs_write_status("installed", NULL); if (!gs_job.local_only && gs_job.generation[0]) gs_release = 1;
            result = 0; goto done;
        }
    }
    packages = (GsExtractedPkg *)calloc(256, sizeof(*packages)); if (!packages) goto done;
    count = gs_read_packages(directory, packages);
    if (count == 0) { gs_write_status("failed", "Archive package journal is invalid"); goto done; }
    if (count > 0) goto validate_packages;
    if (gs_read_volumes()) { gs_write_status("failed", "Archive job metadata is invalid"); goto done; }
    for (int i = 0; i < gs_volume_count; i++) {
        if (gs_archive_download_volume(&gs_volumes[i]) || gs_verify_volume(&gs_volumes[i])) {
            gs_write_status("failed", "RAR volume download, size or checksum failed; input retained"); goto done;
        }
        /* The volume callback maps original names directly; exFAT needs no hard links. */
        if (i == 0) snprintf(first, sizeof(first), "%s", gs_volumes[i].path);
        names[i] = gs_volumes[i].name; paths[i] = gs_volumes[i].path;
    }
    gs_log_write("resident", "archive-dispatch job=%s stage=signature-before volumes=%d", gs_job.id, gs_volume_count);
    unsigned char signature[8] = {0}; FILE *input = fopen(first, "rb");
    size_t signature_bytes = input ? fread(signature, 1, sizeof(signature), input) : 0;
    int zip = signature_bytes >= 4 && (!memcmp(signature, "PK\3\4", 4) || !memcmp(signature, "PK\5\6", 4) || !memcmp(signature, "PK\7\10", 4));
    int seven = signature_bytes >= 6 && !memcmp(signature, "7z\xbc\xaf\x27\x1c", 6);
    int rar = (signature_bytes >= 7 && !memcmp(signature, "Rar!\x1a\7\0", 7)) ||
        (signature_bytes >= 8 && !memcmp(signature, "Rar!\x1a\7\1\0", 8));
    if (input) fclose(input);
    if (signature_bytes >= 4 && !memcmp(signature, "\x7f" "CNT", 4)) {
        if (gs_volume_count != 1) { gs_write_status("failed", "Direct PKG received in a multi-volume set; select its individual package link. Input retained."); goto done; }
        GsExtractedPkg direct = {0};
        snprintf(direct.path, sizeof(direct.path), "%s", first); direct.size = gs_volumes[0].size;
        if (gs_prepare_pkg(&direct)) { gs_write_status("failed", "Direct PKG title or structure does not match the selected region. Input retained."); goto done; }
        if (expected_content[0] && strcmp(expected_content, gs_job.content)) { gs_write_status("failed", "Direct PKG content ID does not match the selected package. Input retained."); goto done; }
        gs_log_write("resident", "archive-dispatch job=%s format=pkg stage=direct-install", gs_job.id);
        gs_package_ordinal = 1; gs_package_legacy_route = 0;
        gs_interval_count = 0; gs_served = 0; __sync_synchronize(); gs_validated = 1;
        if (gs_run_package()) goto done;
        count = 0;
        goto archive_complete;
    }
    if (!zip && !seven && !rar) {
        gs_write_status("failed", "ARCHIVE_FAILED: File header is not a supported RAR, ZIP or 7z archive. Input retained."); goto done;
    }
    if (gs_prepare_archive_extraction(directory)) goto done;
    extraction_started = 1;
    gs_done = 0; gs_job.total = 0; // The decoder supplies the expanded header total.
    gs_write_status("extracting", NULL);
    const char *format = seven ? "7z" : zip ? "zip" : "rar";
    gs_log_write("resident", "archive-dispatch job=%s stage=extract-before format=%s signature_bytes=%u signature=%02x%02x%02x%02x%02x%02x volumes=%d password_set=%d",
        gs_job.id, format, (unsigned)signature_bytes, signature[0], signature[1], signature[2], signature[3], signature[4], signature[5], gs_volume_count, gs_job.password[0] != 0);
    char seven_error[256] = {0};
    count = seven ? (gs_volume_count == 1 ? gs_7z_extract_local_ex(first, directory, packages, 256, gs_archive_progress, seven_error, sizeof(seven_error)) : -3001)
                : zip ? gs_extract_zip(first, directory, names, paths, gs_volume_count, packages, 256, gs_archive_progress)
                : gs_extract_rar_password_diagnostic(first, directory, names, paths, gs_volume_count, packages, 256, gs_archive_progress, gs_job.password, gs_archive_diagnostic);
    for (int i = 0; rar && (count == -24 || count == -22) && i < gs_job.password_fallback_count; i++) {
        if (gs_archive_progress(0, 0)) break;
        const char *password = gs_job.password_fallbacks[i];
        int duplicate = !strcmp(password, gs_job.password);
        for (int j = 0; j < i; j++) if (!strcmp(password, gs_job.password_fallbacks[j])) duplicate = 1;
        if (duplicate) continue;
        gs_log_write("resident", "archive-password-retry job=%s attempt=%d limit=%d", gs_job.id, i + 2, gs_job.password_fallback_count + 1);
        gs_write_status("extracting", "Extracting: trying another source password");
        count = gs_extract_rar_password_diagnostic(first, directory, names, paths, gs_volume_count, packages, 256, gs_archive_progress, password, gs_archive_diagnostic);
    }
    gs_log_write("resident", "archive-dispatch job=%s stage=extract-after format=%s result=%d", gs_job.id, format, count);
    if (count <= 0) {
        char error[256], detail[300];
        if (gs_canceled || g_stop) { gs_write_status("canceled", "Archive extraction canceled; input retained"); goto done; }
        if (seven) snprintf(error, sizeof(error), "%s", seven_error[0] ? seven_error : "Split 7z archives are not supported; original retained");
        else if (!zip && count == -1001) snprintf(error, sizeof(error), "RAR header reading timed out; input retained. Remove the queue entry or retry after checking the archive.");
        else if (!zip && count == -1002) snprintf(error, sizeof(error), "RAR was read successfully but contains no PKG files; archive retained. Check that the source supplied a PS4 package archive.");
        else if (!zip && count == -1003) snprintf(error, sizeof(error), "Another RAR extraction is still running; input retained. Retry after it finishes.");
        else if (!zip && count == -24) snprintf(error, sizeof(error), "RAR password is incorrect; archive retained. Update its password and retry.");
        else if (!zip && count == -22) snprintf(error, sizeof(error), "RAR password is required; archive retained. Enter its password and retry.");
        else if (!zip && count == -15) snprintf(error, sizeof(error), "RAR input could not be opened; archive retained. Check that its storage and all supplied volumes are accessible.");
        else snprintf(error, sizeof(error), "%s extraction failed (%d); check missing volumes, storage or archive integrity", zip ? "ZIP" : "RAR", -count);
        snprintf(detail, sizeof(detail), "ARCHIVE_FAILED: %s", error);
        gs_write_status("failed", detail); goto done;
    }
validate_packages:
    // Validate every candidate before submitting any, then sort game -> update -> DLC.
    gs_log_write("resident", "archive-inventory job=%s packages=%d stage=validating", gs_job.id, count);
    for (int i = 0; i < count; i++) {
        kinds[i] = gs_pkg_kind(packages[i].path);
        if (kinds[i] < 0 || gs_prepare_pkg(&packages[i])) { gs_write_status("failed", "Extracted PKG title or structure mismatch"); goto done; }
        snprintf(content_ids[i], sizeof(content_ids[i]), "%s", gs_job.content);
        if (gs_check_metadata(packages[i].path, kinds[i])) { gs_write_status("failed", "Extracted PKG header and embedded type/title disagree"); goto done; }
        char version[32] = {0}, firmware[32] = {0};
        gs_pkg_sfo(packages[i].path, "APP_VER", version, sizeof(version));
        gs_pkg_sfo(packages[i].path, "SYSTEM_VER", firmware, sizeof(firmware));
        versions[i] = kinds[i] == 8 ? gs_version_number(version) : 0;
        gs_log_write("resident", "archive-package job=%s candidate=%d type=%s content=%s version=%s firmware=%s bytes=%lld",
            gs_job.id, i + 1, kinds[i] == 6 ? "base" : kinds[i] == 8 ? "update-or-backport" : "dlc",
            content_ids[i], version[0] ? version : "unknown", firmware[0] ? firmware : "unknown", (long long)packages[i].size);
        if (gs_verify_package(packages[i].path, kinds[i])) goto done;
    }
    if (expected_content[0] && (count != 1 || strcmp(expected_content, gs_job.content))) {
        gs_write_status("failed", "Archive content ID promise does not match extracted packages"); goto done;
    }
    // Backport is a source/release label, not a PKG type bit. Without that
    // relationship, same-version patches in one archive are ambiguous variants.
    for (int i = 0; i < count; i++) if (kinds[i] == 8) {
        for (int j = 0; j < i; j++) if (kinds[j] == 8) {
            if (versions[i] == versions[j] && !strcmp(content_ids[i], content_ids[j])) {
                gs_write_status("failed", "Archive contains competing updates/backports for one version. Select the matching variant; no packages installed."); goto done;
            }
        }
    }
    for (int i = 1; i < count; i++) {
        int j = i, kind = kinds[i], priority = kind == 6 ? 0 : kind == 8 ? 1 : 2;
        GsExtractedPkg pkg = packages[i];
        int current = versions[i];
        while (j > 0) {
            int previous_priority = kinds[j - 1] == 6 ? 0 : kinds[j - 1] == 8 ? 1 : 2;
            int previous = versions[j - 1];
            if (previous_priority < priority || (previous_priority == priority && (kind != 8 || previous <= current))) break;
            packages[j] = packages[j - 1]; kinds[j] = kinds[j - 1]; versions[j] = versions[j - 1]; j--;
        }
        packages[j] = pkg; kinds[j] = kind; versions[j] = current;
    }
    if (gs_save_packages(directory, packages, count)) goto done;
    if (extraction_started && gs_finish_archive_extraction(directory)) goto done;
    for (int i = 0; i < count; i++)
        gs_log_write("resident", "archive-install-order job=%s ordinal=%d packages=%d type=%s version=%d path=%s",
            gs_job.id, i + 1, count, kinds[i] == 6 ? "base" : kinds[i] == 8 ? "update-or-backport" : "dlc", versions[i], packages[i].path);
    for (int i = 0; i < count; i++) {
        gs_validated = 0;
        while (g_client_count && !g_stop && !gs_canceled) sceKernelUsleep(10000);
        if (g_stop || gs_canceled || gs_prepare_pkg(&packages[i])) goto done;
        gs_package_ordinal = i + 1;
        gs_package_legacy_route = 0;
        gs_interval_count = 0; gs_served = 0; __sync_synchronize(); gs_validated = 1;
        if (gs_install_archive_pkg(directory, i + 1, kinds[i])) {
            if (strcmp(gs_state, "failed")) gs_write_status("failed", "Archive install paused at an unconfirmed BGFT task; packages and journal retained"); goto done;
        }
        if (i + 1 < count) for (int wait = 0; wait < 30 && !g_stop && !gs_canceled; wait++) sceKernelUsleep(100000);
    }
archive_complete:
    ; /* The terminal receipt is durable before deleting any completed input. */
    char completed_path[1150], completed_body[512];
    snprintf(completed_path, sizeof(completed_path), "%s/complete.txt", directory);
    snprintf(completed_body, sizeof(completed_body), "1\n%s\n%s\n%s\n", gs_job.id, gs_job.generation, gs_job.content);
    if (write_atomic(completed_path, completed_body)) goto done;
    if (gs_archive_install_receipt(original_total)) goto done;
    /* Cleanup follows every child's durable successful installation receipt. */
    if (gs_storage_matches(gs_job.storage_root, gs_job.storage_token)) {
        while (g_client_count && !g_stop && !gs_canceled) sceKernelUsleep(10000);
        if (!g_stop && !gs_canceled) for (int i = 0; i < count; i++) unlink(packages[i].path);
        if (!gs_job.borrowed_source && !g_stop && !gs_canceled && gs_volumes) for (int i = 0; i < gs_volume_count; i++) {
            unlink(gs_volumes[i].path);
            char map[1150]; snprintf(map, sizeof(map), "%s.resident.map", gs_volumes[i].path); unlink(map);
        }
    }
    gs_write_status("installed", NULL); if (!gs_job.local_only && gs_job.generation[0]) gs_release = 1; result = 0;
done:
    if (gs_canceled) {
        if (gs_cancel_task()) { gs_release = 0; gs_write_status("failed", "BGFT cancellation unconfirmed; archive ownership retained"); }
        else gs_write_status("canceled", NULL);
    }
    else if (result && strcmp(gs_state, "failed")) gs_write_status("failed", "Resident archive preparation failed; input retained");
    free(packages); free(gs_volumes); gs_volumes = NULL; gs_volume_count = 0;
    return result;
}
