#ifndef GS_STORAGE_POLICY_H
#define GS_STORAGE_POLICY_H

typedef struct {
    int code;
    const char *operation;
    char path[1100];
} GsStorageError;
static int gs_storage_context_unverified;

static void gs_storage_error(GsStorageError *error, const char *operation, const char *path, int code)
{
    if (!error) return;
    error->code = code; error->operation = operation;
    snprintf(error->path, sizeof(error->path), "%s", path);
}

/* Only flat, application-owned staging files are admitted. Never create a USB
 * mount point: its identity marker is minted by the foreground storage picker. */
static int gs_hex_token(const char *value)
{
    if (!value || strlen(value) != 32) return 0;
    for (int i = 0; i < 32; i++) if (!strchr("0123456789abcdef", value[i])) return 0;
    return 1;
}

static int gs_staging_root(const char *path, char *root, size_t capacity)
{
    const char *leaf = NULL; size_t prefix = 0;
    if (!path || strchr(path, '\\') || strstr(path, "..")) return -1;
    if (!strncmp(path, "/data/SSPI/downloads/", 21)) prefix = 21;
    else if (!strncmp(path, "/user/data/SSPI/downloads/", 26)) prefix = 26;
    else if (!strncmp(path, "/mnt/usb", 8) && path[8] >= '0' && path[8] <= '7' &&
             !strncmp(path + 9, "/SSPI/staging/", 14)) prefix = 23;
    else return -1;
    leaf = path + prefix;
    if (!*leaf || strchr(leaf, '/') || strlen(leaf) > 240 || prefix >= capacity) return -1;
    for (const char *p = leaf; *p; p++) if ((unsigned char)*p < 32 || *p == 127 || *p == ':') return -1;
    memcpy(root, path, prefix - 1); root[prefix - 1] = 0;
    return prefix == 21 || prefix == 26 ? 0 : 1;
}

static int gs_storage_directory_error(const char *path, uint32_t *device, GsStorageError *error)
{
#if defined(__FreeBSD__)
    // Shell's libc and the SDK disagree on parts of struct stat. Open the
    // directory without following links; only read the kernel's dev_t field.
    unsigned char info[256] = {0};
    int fd = sceKernelOpen(path, O_RDONLY | O_DIRECTORY | O_NOFOLLOW, 0);
    if (fd < 0) { gs_storage_error(error, "open", path, fd); return 0; }
    int rc = sceKernelFstat(fd, (OrbisKernelStat *)info);
    sceKernelClose(fd);
    if (rc) { gs_storage_error(error, "fstat", path, rc); return 0; }
    memcpy(device, info, sizeof(*device));
    return 1;
#else
    struct stat st;
    if (lstat(path, &st)) { gs_storage_error(error, "lstat", path, -errno); return 0; }
    if (!S_ISDIR(st.st_mode)) { gs_storage_error(error, "directory", path, -ENOTDIR); return 0; }
    *device = (uint32_t)st.st_dev; return 1;
#endif
}
static int gs_storage_directory(const char *path, uint32_t *device)
{ return gs_storage_directory_error(path, device, NULL); }

static int gs_storage_marker_matches(const unsigned char *value, size_t length, const char *token)
{
    size_t start = 0;
    if (length >= 3 && value[0] == 0xef && value[1] == 0xbb && value[2] == 0xbf) start = 3;
    while (start < length && (value[start] == ' ' || value[start] == '\t' || value[start] == '\r' || value[start] == '\n')) start++;
    while (length > start && (value[length - 1] == ' ' || value[length - 1] == '\t' || value[length - 1] == '\r' || value[length - 1] == '\n')) length--;
    if (length - start != 32) return -12;
    return !memcmp(value + start, token, 32) ? 1 : -13;
}

static int gs_storage_check_error(const char *root, const char *token, GsStorageError *error)
{
    char path[1100]; unsigned char observed[128]; uint32_t device, parent, mounted;
    if (error) memset(error, 0, sizeof(*error));
    if (!root || !*root) return -1;
    if (!strcmp(root, "/data/SSPI/downloads") || !strcmp(root, "/user/data/SSPI/downloads"))
        return gs_storage_directory_error(root, &device, error) ? 1 : -2;
    if (strlen(root) != 22 || strncmp(root, "/mnt/usb", 8) || root[8] < '0' || root[8] > '7' ||
        strcmp(root + 9, "/SSPI/staging") || !gs_hex_token(token)) return -3;
    /* Verify every ancestor without following symlinks. The mount must be a
     * separate filesystem from /mnt, not an accidentally-created directory. */
    snprintf(path, sizeof(path), "%.9s", root);
    if (!gs_storage_directory_error("/mnt", &parent, error)) return -4;
    if (!gs_storage_directory_error(path, &mounted, error)) return -5;
    if (parent == mounted) return -6;
    snprintf(path, sizeof(path), "%.9s/SSPI", root);
    if (!gs_storage_directory_error(path, &device, error)) return -7;
    if (device != mounted) return -8;
    if (!gs_storage_directory_error(root, &device, error)) return -9;
    if (device != mounted) return -10;
    snprintf(path, sizeof(path), "%s/.sspi-volume-id", root);
#if defined(__FreeBSD__)
    // Keep the marker in the same kernel namespace as the directory checks.
    // ShellUI's libc filesystem wrappers can resolve a different sandbox root.
    int fd = sceKernelOpen(path, O_RDONLY | O_NOFOLLOW, 0);
    if (fd < 0) { gs_storage_error(error, "open", path, fd); return -11; }
    int64_t count = sceKernelRead(fd, observed, sizeof(observed));
    if (count < 0) gs_storage_error(error, "read", path, (int)count);
    sceKernelClose(fd);
#else
    int fd = open(path, O_RDONLY | O_NOFOLLOW);
    if (fd < 0) { gs_storage_error(error, "open", path, -errno); return -11; }
    ssize_t count = read(fd, observed, sizeof(observed));
    if (count < 0) gs_storage_error(error, "read", path, -errno);
    close(fd);
#endif
    if (count < 0) return -12;
    if ((size_t)count == sizeof(observed)) return -12;
    // Match the managed reader's UTF-8 BOM/newline handling for existing markers.
    // The token itself must still match exactly; no marker is recreated here.
    return gs_storage_marker_matches(observed, (size_t)count, token);
}
static int gs_storage_check(const char *root, const char *token)
{ return gs_storage_check_error(root, token, NULL); }
static int gs_storage_matches(const char *root, const char *token)
{ return gs_storage_check(root, token) == 1; }

/* Borrowed inputs are read-only files anywhere beneath one explicit USB mount.
 * Their staging marker identifies the drive; it does not confer file ownership.
 * FTP uploads are flat files in the application's pkg-rars inbox on internal
 * storage: they are borrowed the same way, stage in the internal downloads folder
 * of the same namespace and return 0 (internal, no drive token). */
static int gs_local_source_root(const char *path, char *root, size_t capacity)
{
    int user = path && !strncmp(path, "/user/data/SSPI/pkg-rars/", 25);
    if (user || (path && !strncmp(path, "/data/SSPI/pkg-rars/", 20))) {
        const char *leaf = path + (user ? 25 : 20); size_t length = strlen(leaf);
        if (strlen(path) >= 1024 || strchr(path, '\\') || !length || length > 240 || strchr(leaf, '/') ||
            !strcmp(leaf, ".") || !strcmp(leaf, "..")) return -1;
        for (const char *p = leaf; *p; p++) if ((unsigned char)*p < 32 || *p == 127 || *p == ':') return -1;
        return snprintf(root, capacity, "%s", user ? "/user/data/SSPI/downloads" : "/data/SSPI/downloads") >= (int)capacity ? -1 : 0;
    }
    if (!path || strncmp(path, "/mnt/usb", 8) || path[8] < '0' || path[8] > '7' || path[9] != '/' ||
        !path[10] || strlen(path) >= 1024 || strchr(path, '\\')) return -1;
    const char *part = path + 10;
    for (const char *p = part; ; p++) {
        if (*p && ((unsigned char)*p < 32 || *p == 127 || *p == ':')) return -1;
        if (!*p || *p == '/') {
            size_t n = (size_t)(p - part);
            if (!n || n > 240 || (n == 1 && part[0] == '.') || (n == 2 && !memcmp(part, "..", 2))) return -1;
            if (!*p) break;
            part = p + 1;
        }
    }
    return snprintf(root, capacity, "%.9s/SSPI/staging", path) >= (int)capacity ? -1 : 1;
}

static int gs_local_source_check(const char *path, const char *root, const char *token)
{
    char expected[1024], parent_path[1024]; uint32_t mounted, device;
    int kind = gs_local_source_root(path, expected, sizeof(expected));
    if (kind < 0 || strcmp(expected, root) || !gs_storage_matches(root, token)) return 0;
    if (kind == 0) {
        /* Inbox file: the unlinked pkg-rars folder and the file share a device. */
        size_t n = (size_t)(strrchr(path, '/') - path);
        memcpy(parent_path, path, n); parent_path[n] = 0;
        if (!gs_storage_directory(parent_path, &mounted)) return 0;
    } else {
    snprintf(parent_path, sizeof(parent_path), "%.9s", path);
    if (!gs_storage_directory(parent_path, &mounted)) return 0;
    for (const char *p = path + 10; *p; p++) if (*p == '/') {
        size_t n = (size_t)(p - path); memcpy(parent_path, path, n); parent_path[n] = 0;
        if (!gs_storage_directory(parent_path, &device) || device != mounted) return 0;
    }
    }
#if defined(__FreeBSD__)
    unsigned char info[256] = {0};
    int fd = sceKernelOpen(path, O_RDONLY | O_NOFOLLOW, 0);
    if (fd < 0) return 0;
    int rc = sceKernelFstat(fd, (OrbisKernelStat *)info); sceKernelClose(fd);
    memcpy(&device, info, sizeof(device));
    return !rc && device == mounted;
#else
    struct stat st;
    return !lstat(path, &st) && S_ISREG(st.st_mode) && (uint32_t)st.st_dev == mounted;
#endif
}
static const char *gs_storage_detail(int check)
{
    switch (check) {
    case -1: return "Staging location is missing";
    case -2: return "PS4 staging folder is missing or inaccessible";
    case -3: return "Selected staging path or saved drive identity is invalid";
    case -4: return "USB mount directory is inaccessible";
    case -5: return gs_storage_context_unverified ? "The background service could not open the selected USB drive" : "Selected USB drive is disconnected or inaccessible";
    case -6: return "Selected USB path has no mounted drive";
    case -7: return "Selected drive's SSPI folder is missing, linked or inaccessible";
    case -8: return "Selected drive's SSPI folder belongs to a different device";
    case -9: return "Selected drive's staging folder is missing, linked or inaccessible";
    case -10: return "Selected staging folder belongs to a different device";
    case -11: return "Selected drive's identity marker is missing, linked or inaccessible";
    case -12: return "Selected drive's identity marker is unreadable or invalid";
    case -13: return "Connected drive does not match this download's saved drive identity";
    default: return "Staging storage could not be verified";
    }
}
static void gs_storage_wait_detail(char *out, size_t capacity, int check, const GsStorageError *error)
{
    /* In the shell process a failed open of /mnt/usbN can persist while the drive
     * works in SSPI itself; reselecting or reseating it does not help. */
    const char *action = check == -5 && gs_storage_context_unverified
        ? "if it is connected, set Staging location to PS4 or Download mode to In-app"
        : "restore the selected storage to resume";
    if (error && error->code)
        snprintf(out, capacity, "%s; %s. Files retained (storage check %d; %s %s: 0x%08X)",
            gs_storage_detail(check), action, check, error->operation, error->path, (unsigned)error->code);
    else
        snprintf(out, capacity, "%s; %s. Files retained (storage check %d)",
            gs_storage_detail(check), action, check);
}
#endif
