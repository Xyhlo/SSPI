/* Native archive continuation: all URLs are resolved by the app before publication. */
#include "gs_unrar.h"
#define GS_MAX_VOLUMES 512
typedef struct { char name[241], url[8192], path[1024], sha[65]; int64_t size; } GsVolume;
static GsVolume *gs_volumes;
static int gs_volume_count;


static int gs_volume_record(FILE *file, GsVolume *volume)
{
    char line[64]; const char *end; const char *p;
    if (gs_encoded_line(file, volume->name, sizeof(volume->name)) ||
        gs_encoded_line(file, volume->url, sizeof(volume->url)) ||
        gs_encoded_line(file, volume->path, sizeof(volume->path)) ||
        gs_encoded_line(file, volume->sha, sizeof(volume->sha)) ||
        gs_line(file, line, sizeof(line)) || parse_number(line, &volume->size, &end) || *end ||
        volume->size <= 0 || volume->size > (int64_t)8 * 1024 * 1024 * 1024 * 1024) return -1;
    if (!volume->name[0] || strchr(volume->name, '/') || strchr(volume->name, '\\') ||
        strchr(volume->name, ':') || strstr(volume->name, "..") ||
        strncmp(volume->path, "/data/GameSearch/downloads/", 26) ||
        strchr(volume->path + 26, '/') || strchr(volume->path, '\\') || strstr(volume->path, "..") ||
        (strncmp(volume->url, "http://", 7) && strncmp(volume->url, "https://", 8))) return -1;
    if (volume->sha[0]) {
        if (strlen(volume->sha) != 64) return -1;
        for (p = volume->sha; *p; p++) if (!strchr("0123456789abcdefABCDEF", *p)) return -1;
    }
    return 0;
}

static int gs_read_volumes(void)
{
    FILE *file = fopen(GS_IPC_ROOT "/job.txt", "rb");
    char line[12000]; int i; int64_t count; const char *end;
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
    static time_t last;
    (void)total;
    while (gs_paused && !g_stop && !gs_canceled) sceKernelUsleep(100000);
    if (g_stop || gs_canceled) return -1;
    if (time(NULL) != last) { last = time(NULL); gs_done = done; gs_write_status("extracting", NULL); }
    return 0;
}

typedef struct {
    int user, entitlement; const char *id, *url, *extra, *name, *icon, *sku;
    uint32_t option; const char *scenario, *release, *type, *subtype; uint64_t size;
} GsBgftParam;
typedef struct {
    uint32_t bits; int error; uint64_t length, transferred, length_total, transferred_total;
    uint32_t index, count, seconds, seconds_total; int preparing, copy;
} GsBgftProgress;
_Static_assert(sizeof(GsBgftParam) == 104, "BGFT parameter ABI");
_Static_assert(sizeof(GsBgftProgress) == 64, "BGFT progress ABI");

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
    unsigned char h[0x1000], *buffer = NULL, digest[32]; SHA256_CTX sha;
    char category[32], title[32], version[32], required[32]; struct stat st;
    FILE *file = fopen(path, "rb"); if (!file) return -1;
    int result = -1;
    if (fstat(fileno(file), &st) || fread(h, 1, sizeof(h), file) != sizeof(h)) goto done;
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
    if (gs_transfer_verified && gs_verified_size == st.st_size && !memcmp(gs_verified_header, h + 0xfe0, 32)) { result = 0; goto done; }
    buffer = (unsigned char *)malloc(1024 * 1024); if (!buffer) goto done;
    uint64_t offsets[3] = { 0, gs_be64(h + 0x20), gs_be64(h + 0x410) };
    uint64_t sizes[3] = { 0xfe0, gs_be64(h + 0x28), gs_be64(h + 0x418) };
    int hashes[3] = { 0xfe0, 0x160, 0x440 };
    time_t last = 0;
    for (int region = 0; region < 3; region++) {
        uint64_t left = sizes[region];
        if (!left && region == 2 && kind == 7) continue; // License-only add-ons have no PFS image.
        if (!left || offsets[region] > (uint64_t)st.st_size || left > (uint64_t)st.st_size - offsets[region] ||
            fseek(file, (long)offsets[region], SEEK_SET)) goto done;
        sha256_init(&sha);
        while (left) {
            if (g_stop || gs_canceled) goto done;
            while (gs_paused && !g_stop && !gs_canceled) sceKernelUsleep(100000);
            size_t n = left < 1024 * 1024 ? (size_t)left : 1024 * 1024;
            if (fread(buffer, 1, n, file) != n) goto done;
            sha256_update(&sha, buffer, n); left -= n;
            if (time(NULL) != last) { last = time(NULL); gs_done = (int64_t)(sizes[region] - left); gs_write_status("validating", NULL); }
        }
        sha256_final(&sha, digest); if (memcmp(digest, h + hashes[region], 32)) goto done;
    }
    result = 0;
done:
    free(buffer); fclose(file);
    if (result && strcmp(gs_state, "failed")) gs_write_status("failed", "Package metadata or checksum failed; installed game was not modified");
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
    if (fstat(fileno(file), &st) || fread(h, 1, sizeof(h), file) != sizeof(h) || memcmp(h, "\x7f\x43\x4e\x54", 4)) goto done;
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
    const char *roots[] = { "/user/app", "/mnt/ext0/user/app" }; char base[256]; struct stat st;
    for (int i = 0; i < 2; i++) {
        snprintf(base, sizeof(base), "%s/%s/app.pkg", roots[i], gs_job.title);
        if (stat(base, &st) || !S_ISREG(st.st_mode)) continue;
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

static int gs_install_archive_pkg(const char *directory, int ordinal, int kind)
{
    char journal[1150], record[256], route[256], id[49]; FILE *file; int task = -1, complete = 0, pending = 0, route_version = 5;
    int bgft, app, users, user = -1, exists = 0, rc, stable = 0, poll_misses = 0;
    int (*init)(void *), (*user_init)(void *), (*get_user)(int *), (*app_init)(void), (*app_exists)(const char *, int *);
    int (*reg)(GsBgftParam *, int *), (*start)(int), (*get_progress)(int, GsBgftProgress *);
    GsBgftParam param; struct { void *heap; size_t size; } heap;
    bgft = gs_module("/system/common/lib/libSceBgft.sprx");
    app = gs_module("/system/common/lib/libSceAppInstUtil.sprx");
    users = gs_module("/system/common/lib/libSceUserService.sprx");
    if (gs_bind(bgft, "sceBgftServiceIntInit", &init) || gs_bind(users, "sceUserServiceGetForegroundUser", &get_user) ||
        gs_bind(app, "sceAppInstUtilInitialize", &app_init) || gs_bind(app, "sceAppInstUtilAppExists", &app_exists) ||
        gs_bind(bgft, "sceBgftServiceDownloadGetProgress", &get_progress) ||
        gs_bind(bgft, "sceBgftServiceDownloadStartTask", &start)) return -1;
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
        int fields = fscanf(file, "%48s %d %d %d %d", id, &task, &complete, &pending, &route_version); fclose(file);
        if ((fields != 4 && fields != 5) || (fields == 5 && route_version != 4 && route_version != 5) ||
            strcmp(id, gs_job.content) || (pending && task < 0)) return -1;
        if (complete) return 0;
        // A pre-5.09 task owns the old URL until it completes. New registrations
        // always use a distinct URL for each extracted PKG.
        if (task >= 0 && (fields == 4 || route_version == 4)) { route_version = 4; gs_package_legacy_route = 1; }
    }
    // A title can appear before its BGFT task finishes copying. A saved task
    // still owns this file/route and must be reattached before advancing.
    if (task < 0 && kind == 6 && exists) return 0;
    if (kind != 6 && !exists) return -1;
    if (task < 0) {
        if (g_stop || gs_canceled) return -1;
        // Persist intent before registration; an interrupted register is never blindly retried.
        gs_package_legacy_route = 0; route_version = 5;
        snprintf(record, sizeof(record), "%s -1 0 1 5", gs_job.content);
        if (write_atomic(journal, record)) return -1;
        gs_package_url(route, sizeof(route), "http://127.0.0.1:8742");
        memset(&param, 0, sizeof(param));
        param.user = user; param.entitlement = 5; param.id = gs_job.content; param.url = route;
        param.name = gs_job.title; param.icon = ""; param.option = 0x10000;
        param.scenario = "0"; param.type = gs_pkg_type(gs_job.destination); if (!param.type) return -1; param.subtype = ""; param.size = gs_job.total;
        if (gs_bind(bgft, kind == 6 ? "sceBgftServiceDownloadRegisterTask" : "sceBgftServiceIntDebugDownloadRegisterPkg", &reg)) return -1;
        rc = reg(&param, &task);
        if (rc && task < 0 && kind == 6 && (uint32_t)rc != 0x80990015u && (uint32_t)rc != 0x80990086u &&
            !gs_bind(bgft, "sceBgftServiceIntDebugDownloadRegisterPkg", &reg)) rc = reg(&param, &task);
        if (task < 0) {
            char error[160]; snprintf(error, sizeof(error), "PS4 registration failed: BGFT 0x%08X. PKG retained.", (unsigned int)rc);
            // Registration returned without creating a task. Clear only our intent.
            snprintf(record, sizeof(record), "%s -1 0 0 5", gs_job.content);
            write_atomic(journal, record);
            gs_write_status("failed", error); return -1;
        }
        gs_archive_task = task;
        snprintf(record, sizeof(record), "%s %d 0 0 %d", gs_job.content, task, route_version);
        if (write_atomic(journal, record)) return -1;
        if (rc) {
            char error[160]; snprintf(error, sizeof(error), "PS4 registration returned BGFT 0x%08X. Task and PKG retained.", (unsigned int)rc);
            gs_write_status("failed", error); return -1;
        }
    }
    gs_archive_task = task;
    char wanted_version[32];
    int wanted = kind == 8 && !gs_pkg_sfo(gs_job.destination, "APP_VER", wanted_version, sizeof(wanted_version))
        ? gs_version_number(wanted_version) : -1;
    if (kind == 8 && wanted < 0) return -1;
    if (g_stop || gs_canceled) return -1;
    rc = start(task);
    if (rc && !gs_bind(bgft, "sceBgftServiceIntDownloadStartTask", &start)) rc = start(task);
    // A journaled task can already be running after a process takeover.
    if (rc) {
        GsBgftProgress p = {0}; int poll = get_progress(task, &p);
        if (poll || p.error) {
            char error[160]; snprintf(error, sizeof(error), "PS4 install could not start: BGFT 0x%08X. Task and PKG retained.", (unsigned int)(p.error ? p.error : rc));
            gs_write_status("failed", error); return -1;
        }
    }
    while (!g_stop && !gs_canceled) {
        GsBgftProgress p = {0};
        rc = get_progress(task, &p);
        if (rc) {
            stable = 0;
            if (++poll_misses >= 120) {
                char error[180]; snprintf(error, sizeof(error), "PS4 install status unavailable: BGFT 0x%08X. Task and PKG retained; inspect PS4 Notifications.", (unsigned int)rc);
                gs_write_status("failed", error); return -1;
            }
            sceKernelUsleep(500000); continue;
        }
        poll_misses = 0;
        if (p.error) {
            char error[160]; snprintf(error, sizeof(error), "PS4 install failed: BGFT 0x%08X. PKG retained; no automatic retry.", (unsigned int)p.error);
            gs_write_status("failed", error); return -1;
        }
        if (gs_paused) { sceKernelUsleep(100000); continue; }
        gs_done = (int64_t)(p.transferred_total ? p.transferred_total : p.transferred);
        gs_write_status("installing", NULL);
        // Copy percentage alone has produced false completion on hardware. Require
        // the whole PKG to have been served or counted by this owned BGFT task.
        uint64_t transferred = p.transferred_total ? p.transferred_total : p.transferred;
        if (p.copy >= 100 && (gs_served || transferred >= (uint64_t)gs_job.total)) stable++;
        else stable = 0;
        if (stable >= 8) {
            int confirmed = kind == 6 ? (!app_exists(gs_job.title, &exists) && exists)
                : kind == 8 ? gs_installed_patch_version() >= wanted : 1;
            if (!confirmed) {
                if (stable >= 240) {
                    gs_write_status("failed", "PS4 finished copying but installed version is unconfirmed. Task and PKG retained; inspect Notifications."); return -1;
                }
                sceKernelUsleep(500000); continue;
            }
            snprintf(record, sizeof(record), "%s %d 1 0 %d", gs_job.content, task, route_version);
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
    if (gs_archive_task < 0) return 0;
    module = gs_module("/system/common/lib/libSceBgft.sprx");
    if (gs_bind(module, "sceBgftServiceDownloadStopTask", &stop) ||
        gs_bind(module, "sceBgftServiceIntDownloadUnregisterTask", &remove)) return -1;
    stop(gs_archive_task);
    int rc = remove(gs_archive_task);
    if (rc && (uint32_t)rc != 0x80990019u) return -1;
    if (gs_archive_journal[0]) {
        char record[96]; snprintf(record, sizeof(record), "%s -1 0 0 5", gs_job.content);
        if (write_atomic(gs_archive_journal, record)) return -1;
    }
    gs_archive_task = -1; return 0;
}

static int gs_archive_download_volume(GsVolume *volume)
{
    struct stat st; char partial[1050];
    snprintf(gs_job.url, sizeof(gs_job.url), "%s", volume->url);
    snprintf(gs_job.sha, sizeof(gs_job.sha), "%s", volume->sha);
    gs_job.total = volume->size;
    if (!stat(volume->path, &st) && S_ISREG(st.st_mode) && st.st_size == volume->size) return 0;
    if (gs_network_init()) return -1;
    snprintf(partial, sizeof(partial), "%s.resident.part", volume->path);
    if (gs_download_stream(partial) || rename(partial, volume->path)) return -1;
    return 0;
}

static int gs_verify_volume(GsVolume *volume)
{
    FILE *file = fopen(volume->path, "rb"); unsigned char signature[8], hash[32], buffer[128 * 1024];
    SHA256_CTX sha; char hex[65]; size_t count, i; struct stat st;
    if (!file) return -1;
    if (fstat(fileno(file), &st) || st.st_size != volume->size || fread(signature, 1, 8, file) != 8 ||
        memcmp(signature, "Rar!\x1a\x07", 6) || (signature[6] != 0 && (signature[6] != 1 || signature[7] != 0))) { fclose(file); return -1; }
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
    snprintf(gs_job.destination, sizeof(gs_job.destination), "%s", pkg->path);
    gs_job.total = pkg->size; gs_job.sha[0] = 0;
    return gs_validate(pkg->path);
}

static int gs_run_package(void)
{
    char directory[1024]; struct stat owned;
    snprintf(directory, sizeof(directory), GS_IPC_ROOT "/packages/%s", gs_job.id);
    mkdir(GS_IPC_ROOT "/packages", 0700); mkdir(directory, 0700);
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

static int gs_run_archive(void)
{
    char directory[1024], alias[1300], first[1300];
    const char *names[GS_MAX_VOLUMES], *paths[GS_MAX_VOLUMES];
    GsExtractedPkg *packages = NULL; int kinds[256], versions[256]; int count = 0, result = -1;
    char expected_content[49]; snprintf(expected_content, sizeof(expected_content), "%s", gs_job.content);
    snprintf(directory, sizeof(directory), GS_IPC_ROOT "/archives/%s", gs_job.id);
    mkdir(GS_IPC_ROOT "/archives", 0700); mkdir(directory, 0700);
    struct stat owned;
    if (lstat(directory, &owned) || !S_ISDIR(owned.st_mode)) goto done;
    packages = (GsExtractedPkg *)calloc(256, sizeof(*packages)); if (!packages) goto done;
    count = gs_read_packages(directory, packages);
    if (count == 0) { gs_write_status("failed", "Archive package journal is invalid"); goto done; }
    if (count > 0) goto validate_packages;
    if (gs_read_volumes()) { gs_write_status("failed", "Archive job metadata is invalid"); goto done; }
    for (int i = 0; i < gs_volume_count; i++) {
        if (gs_archive_download_volume(&gs_volumes[i]) || gs_verify_volume(&gs_volumes[i])) {
            gs_write_status("failed", "RAR volume download, size or checksum failed; input retained"); goto done;
        }
        snprintf(alias, sizeof(alias), "%s/%s", directory, gs_volumes[i].name);
        struct stat input, existing;
        if (stat(gs_volumes[i].path, &input)) goto done;
        if (lstat(alias, &existing) == 0) {
            if (!S_ISREG(existing.st_mode) || existing.st_ino != input.st_ino || existing.st_dev != input.st_dev) goto done;
        } else if (link(gs_volumes[i].path, alias)) goto done;
        if (i == 0) snprintf(first, sizeof(first), "%s", alias);
        names[i] = gs_volumes[i].name; paths[i] = gs_volumes[i].path;
    }
    gs_done = 0; gs_job.total = 0; // Expanded total is not known before sequential decoding.
    gs_write_status("extracting", NULL);
    count = gs_extract_rar(first, directory, names, paths, gs_volume_count, packages, 256, gs_archive_progress);
    if (count <= 0) {
        char error[160]; snprintf(error, sizeof(error), "RAR extraction failed (UnRAR %d); check missing volumes, password, storage or dictionary size", -count);
        gs_write_status("failed", error); goto done;
    }
validate_packages:
    // Validate every candidate before submitting any, then sort game -> update -> DLC.
    for (int i = 0; i < count; i++) {
        kinds[i] = gs_pkg_kind(packages[i].path);
        if (kinds[i] < 0 || gs_prepare_pkg(&packages[i])) { gs_write_status("failed", "Extracted PKG title or structure mismatch"); goto done; }
        if (gs_check_metadata(packages[i].path, kinds[i])) { gs_write_status("failed", "Extracted PKG header and embedded type/title disagree"); goto done; }
        if (gs_verify_package(packages[i].path, kinds[i])) goto done;
        char version[32];
        versions[i] = kinds[i] == 8 && !gs_pkg_sfo(packages[i].path, "APP_VER", version, sizeof(version)) ? gs_version_number(version) : 0;
    }
    if (expected_content[0] && (count != 1 || strcmp(expected_content, gs_job.content))) {
        gs_write_status("failed", "Archive content ID promise does not match extracted packages"); goto done;
    }
    // Backport is a source/release label, not a PKG type bit. Without that
    // relationship, same-version patches in one archive are ambiguous variants.
    for (int i = 0; i < count; i++) if (kinds[i] == 8) {
        for (int j = 0; j < i; j++) if (kinds[j] == 8) {
            if (versions[i] == versions[j]) {
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
    }
    gs_write_status("installed", NULL); result = 0;
done:
    if (gs_canceled) {
        if (gs_cancel_task()) { gs_release = 0; gs_write_status("failed", "BGFT cancellation unconfirmed; archive ownership retained"); }
        else gs_write_status("canceled", NULL);
    }
    else if (result && strcmp(gs_state, "failed")) gs_write_status("failed", "Resident archive preparation failed; input retained");
    free(packages); free(gs_volumes); gs_volumes = NULL; gs_volume_count = 0;
    return result;
}
