/* FTP inbox watcher. Included at the end of gs_resident_worker.inc.
 *
 * FTP clients upload PKGs, RAR sets and single-file ZIP or 7z archives into
 * <data>/pkg-rars, often while SSPI is closed. While this worker runs it scans
 * that flat folder every two seconds, announces a new upload with a PS4
 * notification, and once every tracked file has kept the same size and time for
 * the grace period it claims each upload and publishes it as a borrowed
 * local-source staged job. The existing staged validation, archive extraction
 * and BGFT installation paths then do the work.
 *
 * Ownership is shared with the application through one claim file per upload
 * identity in <data>/ftp-inbox-claims, named <fnv1a64(name)>-<size>-<mtime>.claim
 * and created with O_CREAT|O_EXCL. The side that creates the primary claim (PKG,
 * ZIP, 7z or first RAR volume) owns the upload; a claim also marks it as
 * processed. The application defers to this worker while its heartbeat
 * advertises ftpinbox=1.
 *
 * A claim body holds one field per line: owner, file name, size, mtime, job ID,
 * state and, in claims this worker writes, the upload set's file count and total
 * bytes. Readers ignore missing trailing lines, so older claims stay valid. When
 * an inbox archive job fails or is canceled its claim records that state; if the
 * set's file count or bytes later change (a missing or resumed volume), the claim
 * is removed and the set is installed again as a new job. */

/* Every file of an upload must be tracked: the quiet check below only sees
 * tracked files, so a set larger than the table was handed off while its
 * remaining volumes were still arriving. One RAR set may have up to 512
 * volumes (GS_MAX_VOLUMES in gs_archive_job.inc). */
#define GS_INBOX_TRACKED 512
#define GS_INBOX_POLL_US 2000000ULL
/* Quiet period before hand-off. A PKG is complete once its size stops changing;
 * an archive set arrives one volume at a time and FTP clients can pause between
 * volumes, so a set waits longer. app/FtpInbox.cs uses the same two values. */
#define GS_INBOX_QUIET_US 10000000ULL
#define GS_INBOX_ARCHIVE_QUIET_US 60000000ULL
#define GS_INBOX_RETRY_US 30000000ULL
/* Claims whose upload is gone or changed are removed at this interval, as the
 * application does while it runs. */
#define GS_INBOX_PRUNE_US 60000000ULL
/* The application publishes into the lowest staged slot without a job file and
 * replaces whatever file appears there before its rename. This worker takes the
 * highest free slot, never the last free one, and only once the free slots have
 * not changed for this long, so the two sides do not pick the same slot. */
#define GS_INBOX_SLOT_SETTLE_US 10000000ULL
#define GS_INBOX_SETS 64
#define GS_INBOX_RECHECKS 32
#define GS_INBOX_PREFIX "ftp_"

typedef struct {
    char name[248];
    int64_t size, mtime;
    uint64_t changed_at;
    int present;
} GsInboxEntry;

typedef struct {
    char owner[16], name[248], job[64], state[24];
    int64_t size, mtime, count, total; // count and total are -1 in claims without set fields
} GsInboxClaim;

static GsInboxEntry gs_inbox_entries[GS_INBOX_TRACKED];
static int gs_inbox_count;
static uint64_t gs_inbox_next_poll, gs_inbox_retry_at, gs_inbox_prune_at;
static char gs_inbox_reported[GS_STAGE_COUNT][192], gs_inbox_announced[248];
static int gs_inbox_reported_next;
static uint64_t gs_inbox_serial;
/* File count and bytes of every upload set seen by the current scan. */
static struct { uint64_t key; int64_t total; int64_t count; } gs_inbox_sets[GS_INBOX_SETS];
static int gs_inbox_set_used, gs_inbox_sets_complete;
/* Claimed first volumes of failed archive jobs, compared with their set after a scan. */
static struct { GsInboxEntry entry; int64_t count, total; } gs_inbox_rechecks[GS_INBOX_RECHECKS];
static int gs_inbox_recheck_count;
/* Published records the stage loop has not taken in yet. */
static struct { GsInboxEntry entry; char id[64]; int slot; uint64_t at; } gs_inbox_published[GS_STAGE_COUNT];
static uint32_t gs_inbox_free_slots;
static int gs_inbox_free_slots_known;
static uint64_t gs_inbox_slots_settled_at;
static int gs_inbox_claim_errno;
static uint64_t gs_inbox_claim_logged_at;

static const char *gs_inbox_data_root(void)
{ return strcmp(g_ipc_root, GS_IPC_ROOT_SHARED) ? "/data/SSPI" : "/user/data/SSPI"; }

static uint64_t gs_inbox_hash(const char *text, uint64_t hash)
{
    for (const unsigned char *p = (const unsigned char *)text; *p; p++) { hash ^= *p; hash *= 0x100000001b3ULL; }
    return hash;
}

static int gs_inbox_ends_with(const char *name, const char *suffix)
{
    size_t n = strlen(name), m = strlen(suffix);
    return n > m && !strcasecmp(name + n - m, suffix);
}

/* 1: PKG, ZIP, 7z or first RAR volume, 2: continuation volume, 0: not an upload.
 * Mirrors ArchiveVolumeSet: name.partN.rar, name.rar, then name.r00 .. name.z99.
 * The extractors take ZIP and 7z as single files, like USB installation. */
static int gs_inbox_kind(const char *name)
{
    size_t n = strlen(name);
    if (!n || n > 240 || name[0] == '.') return 0;
    if (gs_inbox_ends_with(name, ".pkg") || gs_inbox_ends_with(name, ".zip") ||
        gs_inbox_ends_with(name, ".7z") || gs_inbox_ends_with(name, ".7zip")) return 1;
    if (gs_inbox_ends_with(name, ".rar")) {
        for (int i = (int)n - 5; i >= 5; i--) if (!strncasecmp(name + i - 5, ".part", 5)) {
            char *end; long index = strtol(name + i, &end, 10);
            if (end == name + n - 4 && end > name + i && end - (name + i) <= 6) return index == 1 ? 1 : index > 1 ? 2 : 0;
        }
        return 1;
    }
    if (n > 4 && name[n - 4] == '.' && ((name[n - 3] | 32) >= 'r' && (name[n - 3] | 32) <= 'z') &&
        name[n - 2] >= '0' && name[n - 2] <= '9' && name[n - 1] >= '0' && name[n - 1] <= '9') return 2;
    return 0;
}

static uint64_t gs_inbox_quiet_us(const char *name)
{ return gs_inbox_ends_with(name, ".pkg") ? GS_INBOX_QUIET_US : GS_INBOX_ARCHIVE_QUIET_US; }

/* Identifies the upload set of a file: its name without the volume suffix, case
 * ignored, plus the naming style, parsed as gs_inbox_kind does. A PKG, ZIP or 7z
 * upload is a set of one. */
static uint64_t gs_inbox_set_key(const char *name)
{
    size_t n = strlen(name), stem = n; unsigned char style = 'F';
    if (gs_inbox_ends_with(name, ".rar")) {
        stem = n - 4; style = 'R';
        for (int i = (int)n - 5; i >= 5; i--) if (!strncasecmp(name + i - 5, ".part", 5)) {
            char *end; (void)strtol(name + i, &end, 10);
            if (end == name + n - 4 && end > name + i && end - (name + i) <= 6) { stem = (size_t)(i - 5); style = 'P'; break; }
        }
    } else if (gs_inbox_kind(name) == 2) { stem = n - 4; style = 'R'; }
    uint64_t hash = 0xcbf29ce484222325ULL ^ style;
    for (size_t i = 0; i < stem; i++) {
        unsigned char c = (unsigned char)name[i];
        hash ^= c >= 'A' && c <= 'Z' ? c + 32 : c; hash *= 0x100000001b3ULL;
    }
    return hash;
}

static void gs_inbox_set_add(const char *name, int64_t size)
{
    uint64_t key = gs_inbox_set_key(name);
    for (int i = 0; i < gs_inbox_set_used; i++)
        if (gs_inbox_sets[i].key == key) { gs_inbox_sets[i].count++; gs_inbox_sets[i].total += size; return; }
    if (gs_inbox_set_used == GS_INBOX_SETS) return; // this set stays unknown for this scan
    gs_inbox_sets[gs_inbox_set_used].key = key; gs_inbox_sets[gs_inbox_set_used].count = 1;
    gs_inbox_sets[gs_inbox_set_used++].total = size;
}

/* 0 with the file count and bytes the last scan found for name's set; -1 when
 * that scan cannot tell (folder larger than the scan limit, table full). */
static int gs_inbox_set_find(const char *name, int64_t *count, int64_t *total)
{
    uint64_t key = gs_inbox_set_key(name);
    if (gs_inbox_sets_complete)
        for (int i = 0; i < gs_inbox_set_used; i++)
            if (gs_inbox_sets[i].key == key) { *count = gs_inbox_sets[i].count; *total = gs_inbox_sets[i].total; return 0; }
    return -1;
}

/* Size, modification second and regular-file type without following links. */
static int gs_inbox_stat(const char *path, int64_t *size, int64_t *mtime)
{
    int fd = open(path, O_RDONLY | O_NOFOLLOW | O_NONBLOCK);
    if (fd < 0) return -1;
    int regular = 0;
#if defined(__FreeBSD__)
    /* PS4 kernel stat layout: mode at byte 8, st_mtim.tv_sec at byte 40. */
    unsigned char info[256] __attribute__((aligned(8))) = {0};
    uint16_t mode = 0; int64_t seconds = 0;
    if (!sceKernelFstat(fd, (OrbisKernelStat *)info)) {
        memcpy(&mode, info + 8, sizeof(mode)); memcpy(&seconds, info + 40, sizeof(seconds));
        regular = S_ISREG(mode); *mtime = seconds;
    }
#else
    struct stat info;
    if (!fstat(fd, &info)) { regular = S_ISREG(info.st_mode); *mtime = (int64_t)info.st_mtime; }
#endif
    *size = regular ? sceKernelLseek(fd, 0, SEEK_END) : -1;
    close(fd);
    return regular && *size > 0 ? 0 : -1;
}

static int gs_inbox_claim_path(char *out, size_t capacity, const char *name, int64_t size, int64_t mtime)
{
    int n = snprintf(out, capacity, "%s/ftp-inbox-claims/%016llx-%lld-%lld.claim", gs_inbox_data_root(),
        (unsigned long long)gs_inbox_hash(name, 0xcbf29ce484222325ULL), (long long)size, (long long)mtime);
    return n < 0 || (size_t)n >= capacity ? -1 : 0;
}

static int gs_inbox_claimed(const GsInboxEntry *entry)
{
    char path[1100];
    return gs_inbox_claim_path(path, sizeof(path), entry->name, entry->size, entry->mtime) || !access(path, F_OK);
}

/* A claim that cannot be created (storage full, no access) blocks publication:
 * log it, rate-limited, and retry after GS_INBOX_RETRY_US instead of every scan. */
static void gs_inbox_claim_failed(int error, const char *name, const char *state)
{
    uint64_t now = sceKernelGetProcessTime();
    gs_inbox_retry_at = now + GS_INBOX_RETRY_US;
    if (error == gs_inbox_claim_errno && now - gs_inbox_claim_logged_at < 600000000ULL) return;
    gs_inbox_claim_errno = error; gs_inbox_claim_logged_at = now;
    gs_log_write("resident", "ftp-inbox claim-failed errno=%d state=%s name=%s retry_s=%llu", error, state, name,
        (unsigned long long)(GS_INBOX_RETRY_US / 1000000ULL));
}

static int gs_inbox_claim_body(char *body, size_t capacity, const char *name, int64_t size, int64_t mtime,
    const char *job, const char *state, int64_t count, int64_t total)
{
    int n = count >= 0
        ? snprintf(body, capacity, "resident\n%s\n%lld\n%lld\n%s\n%s\n%lld\n%lld\n", name, (long long)size,
            (long long)mtime, job ? job : "", state, (long long)count, (long long)total)
        : snprintf(body, capacity, "resident\n%s\n%lld\n%lld\n%s\n%s\n", name, (long long)size,
            (long long)mtime, job ? job : "", state);
    return n < 0 || (size_t)n >= capacity ? -1 : n;
}

/* Returns 1 when this worker created the claim, 0 when another owner has it and
 * -1 when it could not be created (logged; retried later). A negative count
 * leaves out the set fields. */
static int gs_inbox_claim(const GsInboxEntry *entry, const char *job, const char *state, int64_t count, int64_t total)
{
    char path[1100], body[700];
    snprintf(body, sizeof(body), "%s/ftp-inbox-claims", gs_inbox_data_root());
    (void)sceKernelMkdir(body, 0777);
    if (gs_inbox_claim_path(path, sizeof(path), entry->name, entry->size, entry->mtime)) return 0;
    int length = gs_inbox_claim_body(body, sizeof(body), entry->name, entry->size, entry->mtime, job, state, count, total);
    if (length < 0) { gs_inbox_claim_failed(EINVAL, entry->name, state); return -1; }
    int fd = open(path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0666);
    if (fd < 0) {
        if (errno == EEXIST) return 0;
        gs_inbox_claim_failed(errno, entry->name, state);
        return -1;
    }
    int written = (int)write(fd, body, (size_t)length), error = written < 0 ? errno : ENOSPC;
    close(fd);
    if (written != length) { unlink(path); gs_inbox_claim_failed(error, entry->name, state); return -1; }
    return 1;
}

static void gs_inbox_unclaim(const GsInboxEntry *entry)
{
    char path[1100];
    if (!gs_inbox_claim_path(path, sizeof(path), entry->name, entry->size, entry->mtime)) unlink(path);
}

static int gs_inbox_read_claim(const char *path, GsInboxClaim *claim)
{
    char line[300]; const char *end; int64_t count, total;
    memset(claim, 0, sizeof(*claim)); claim->count = claim->total = -1;
    FILE *file = fopen(path, "rb");
    if (!file) return -1;
    int valid = !gs_line(file, claim->owner, sizeof(claim->owner)) && !gs_line(file, claim->name, sizeof(claim->name)) &&
        !gs_line(file, line, sizeof(line)) && !parse_number(line, &claim->size, &end) && !*end &&
        !gs_line(file, line, sizeof(line)) && !parse_number(line, &claim->mtime, &end) && !*end &&
        !gs_line(file, claim->job, sizeof(claim->job)) && !gs_line(file, claim->state, sizeof(claim->state));
    if (valid && !gs_line(file, line, sizeof(line)) && !parse_number(line, &count, &end) && !*end && count > 0 &&
        !gs_line(file, line, sizeof(line)) && !parse_number(line, &total, &end) && !*end) {
        claim->count = count; claim->total = total;
    }
    fclose(file);
    return valid ? 0 : -1;
}

/* Builds that predate claim files recorded path|size|.NET ticks lines. Until the
 * application migrates that ledger, the same leaf name, size and modification
 * second means processed; a re-upload has a newer time and is processed again. */
static int gs_inbox_legacy_processed(const GsInboxEntry *entry)
{
    char path[1100], line[1400];
    snprintf(path, sizeof(path), "%s/ftp-inbox-processed.txt", gs_inbox_data_root());
    FILE *file = fopen(path, "rb"); if (!file) return 0;
    int found = 0;
    while (!found && fgets(line, sizeof(line), file)) {
        char *ticks = strrchr(line, '|'); if (!ticks || ticks == line) continue;
        *ticks++ = 0;
        char *size = strrchr(line, '|'); if (!size) continue;
        *size++ = 0;
        const char *leaf = strrchr(line, '/'); leaf = leaf ? leaf + 1 : line;
        long long seconds = strtoll(ticks, NULL, 10) / 10000000LL - 62135596800LL;
        found = !strcmp(leaf, entry->name) && strtoll(size, NULL, 10) == entry->size && seconds == entry->mtime;
    }
    fclose(file); return found;
}

/* Removes claims whose upload is gone or has changed, as FtpInbox.PruneClaims
 * does in the application. A claim is kept when its body does not describe the
 * claim file or the upload cannot be checked. */
static void gs_inbox_prune_claims(const char *inbox)
{
    char claims[600], path[1100], expected[1100], source[1100]; GsInboxClaim claim; int scanned = 0, removed = 0;
    snprintf(claims, sizeof(claims), "%s/ftp-inbox-claims", gs_inbox_data_root());
    if (gs_nofollow_path(claims, 1) != 1) return;
    DIR *folder = opendir(claims);
    if (!folder) return;
    struct dirent *item;
    while ((item = readdir(folder)) && ++scanned <= 4096) {
        int64_t size, mtime;
        if (!gs_inbox_ends_with(item->d_name, ".claim") ||
            snprintf(path, sizeof(path), "%s/%s", claims, item->d_name) >= (int)sizeof(path) ||
            gs_inbox_read_claim(path, &claim) || !claim.name[0] || strchr(claim.name, '/') ||
            gs_inbox_claim_path(expected, sizeof(expected), claim.name, claim.size, claim.mtime) || strcmp(expected, path) ||
            snprintf(source, sizeof(source), "%s/%s", inbox, claim.name) >= (int)sizeof(source)) continue;
        int present = gs_nofollow_path(source, 0);
        if (present < 0 || (present == 1 && (gs_inbox_stat(source, &size, &mtime) ||
            (size == claim.size && mtime == claim.mtime)))) continue;
        if (!unlink(path)) removed++;
    }
    closedir(folder);
    if (removed) gs_log_write("resident", "ftp-inbox claims-pruned count=%d", removed);
}

static int gs_inbox_same_container(const char *source, const char *installed)
{
    unsigned char a[4096], b[4096]; int64_t size_a, size_b, ignored;
    if (gs_inbox_stat(installed, &size_b, &ignored) || gs_inbox_stat(source, &size_a, &ignored) || size_a != size_b) return 0;
    FILE *left = fopen(source, "rb"), *right = fopen(installed, "rb");
    int same = left && right && fread(a, 1, sizeof(a), left) == sizeof(a) &&
        fread(b, 1, sizeof(b), right) == sizeof(b) && !memcmp(a, b, sizeof(a));
    if (left) fclose(left);
    if (right) fclose(right);
    return same;
}

/* An identical container (same size and 4 KiB header with its digests) is
 * already installed. A deleted game has no container, so it installs again. */
static int gs_inbox_installed(const char *path, const char *content, const char *title, int kind)
{
    const char *roots[] = { "/user", "/mnt/ext0/user" }; char installed[256];
    for (int i = 0; i < 2; i++) {
        if (kind == 6) snprintf(installed, sizeof(installed), "%s/app/%s/app.pkg", roots[i], title);
        else if (kind == 8) snprintf(installed, sizeof(installed), "%s/patch/%s/patch.pkg", roots[i], title);
        else snprintf(installed, sizeof(installed), "%s/addcont/%s/%s/ac.pkg", roots[i], title, content + 20);
        if (gs_inbox_same_container(path, installed)) return 1;
    }
    return 0;
}

/* A live BGFT task for this content (for example the PS4 package installer)
 * must not get a second installation. Failed or finished tasks do not block. */
static int gs_inbox_active_task(const char *content, int kind)
{
    int (*find)(const char *, int, int *), (*progress)(int, GsBgftProgress *); int task = -1;
    if (gs_bgft_module < 0 || gs_bind(gs_bgft_module, "sceBgftServiceDownloadFindTaskByContentId", &find) ||
        gs_bind(gs_bgft_module, "sceBgftServiceDownloadGetProgress", &progress) ||
        find(content, kind, &task) || task < 0) return -1;
    GsBgftProgress state; memset(&state, 0, sizeof(state));
    if (progress(task, &state)) return task;
    if (state.error) return -1;
    uint64_t total = state.length_total ? state.length_total : state.length;
    uint64_t done = state.length_total ? state.transferred_total : state.transferred;
    return total && done >= total && state.preparing >= 100 && state.copy >= 100 ? -1 : task;
}

static int gs_inbox_pkg_identity(const char *path, char content[49], char title[16], int *kind)
{
    unsigned char header[0x80]; FILE *file = fopen(path, "rb");
    if (!file) return -1;
    size_t n = fread(header, 1, sizeof(header), file); fclose(file);
    if (n != sizeof(header) || memcmp(header, "\x7f" "CNT", 4)) return -1;
    memcpy(content, header + 0x40, 36); content[36] = 0;
    for (int i = 0; i < 36; i++)
        if (!((content[i] >= 'A' && content[i] <= 'Z') || (content[i] >= 'a' && content[i] <= 'z') ||
            (content[i] >= '0' && content[i] <= '9') || content[i] == '-' || content[i] == '_')) return -1;
    memcpy(title, content + 7, 9); title[9] = 0;
    *kind = gs_pkg_kind(path);
    return *kind == 6 || *kind == 7 || *kind == 8 ? 0 : -1;
}

static void gs_inbox_notify(const char *title, const char *name)
{
    char detail[300]; snprintf(detail, sizeof(detail), "%.200s", name);
    gs_download_notification(title, detail);
}

/* Name for logs and notifications with control characters shown as '?'. */
static void gs_inbox_printable(const char *name, char *out, size_t capacity)
{
    size_t i = 0;
    for (; name[i] && i + 1 < capacity; i++) out[i] = (unsigned char)name[i] < 32 || name[i] == 127 ? '?' : name[i];
    out[i] = 0;
}

static uint32_t gs_inbox_slot_scan(void)
{
    uint32_t free_slots = 0; char path[1100];
    for (int slot = 0; slot < GS_STAGE_COUNT; slot++) {
        if (gs_stages[slot].job.id[0]) continue;
        gs_stage_path(slot, "job", path, sizeof(path));
        if (path[0] && access(path, F_OK) && errno == ENOENT) free_slots |= 1u << slot;
    }
    return free_slots;
}

/* Called every poll so that a change is seen when it happens, not at the next
 * hand-off; this worker's own publications are expected and do not count. */
static void gs_inbox_observe_slots(uint64_t now)
{
    uint32_t free_slots = gs_inbox_slot_scan();
    if (gs_inbox_free_slots_known && free_slots == gs_inbox_free_slots) return;
    gs_inbox_free_slots = free_slots; gs_inbox_free_slots_known = 1;
    gs_inbox_slots_settled_at = now + GS_INBOX_SLOT_SETTLE_US;
}

/* Highest free staged slot, or -1 while fewer than two are free or the free
 * slots changed recently (see GS_INBOX_SLOT_SETTLE_US). */
static int gs_inbox_free_slot(void)
{
    uint64_t now = sceKernelGetProcessTime(); int free_count = 0, highest = -1;
    gs_inbox_observe_slots(now);
    if (now < gs_inbox_slots_settled_at) return -1;
    for (int slot = 0; slot < GS_STAGE_COUNT; slot++)
        if (gs_inbox_free_slots & (1u << slot)) { free_count++; highest = slot; }
    return free_count >= 2 ? highest : -1;
}

/* Publishes a borrowed local-source record (format 18, as ResidentDownloadService
 * writes it) into slot. O_EXCL keeps an existing record from being replaced. */
static int gs_inbox_publish(int slot, const char *id, const char *generation, const char *source, const char *title,
    const char *content, int64_t size, int kind)
{
    char path[1100], body[4096], encoded_id[260], encoded_source[1500], encoded_title[40], encoded_content[100];
    const char *slash = strrchr(source, '/');
    char root[1024];
    if (slot < 0 || slot >= GS_STAGE_COUNT || gs_stages[slot].job.id[0]) return -1;
    if (!slash || gs_local_source_root(source, root, sizeof(root)) != 0) return -1;
    if (strcmp(g_ipc_root, GS_IPC_ROOT_SHARED) == 0 && strncmp(root, "/user/", 6))
        snprintf(root, sizeof(root), "/user/data/SSPI/downloads");
    (void)sceKernelMkdir(root, 0777);
    gs_b64encode((const unsigned char *)id, strlen(id), encoded_id, sizeof(encoded_id));
    if (gs_b64encode((const unsigned char *)source, strlen(source), encoded_source, sizeof(encoded_source)) < 0) return -1;
    gs_b64encode((const unsigned char *)title, strlen(title), encoded_title, sizeof(encoded_title));
    gs_b64encode((const unsigned char *)content, strlen(content), encoded_content, sizeof(encoded_content));
    int length = snprintf(body, sizeof(body), "18\n%s\n\n%s\n%s\n\n%s\n%lld\n1\n%d\n\n%s\n\n1\n\n",
        encoded_id, encoded_source, encoded_title, encoded_content, (long long)size, kind, generation);
    if (length < 0 || (size_t)length >= sizeof(body)) return -1;
    gs_stage_path(slot, "job", path, sizeof(path));
    if (!path[0] || !access(path, F_OK)) return -1;
    char stale[1100];
    gs_stage_path(slot, "status", stale, sizeof(stale)); unlink(stale);
    gs_stage_path(slot, "control", stale, sizeof(stale)); unlink(stale);
    int fd = open(path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, 0666);
    if (fd < 0) return -1;
    int written = (int)write(fd, body, (size_t)length);
    int synced = fsync(fd);
    close(fd);
    if (written != length || synced) { unlink(path); return -1; }
    gs_inbox_free_slots &= ~(1u << slot);
    gs_log_write("resident", "ftp-inbox publish slot=%d job=%s kind=%d bytes=%lld source=%s", slot, id, kind, (long long)size, source);
    return slot;
}

static void gs_inbox_track_publish(const GsInboxEntry *entry, const char *id, int slot)
{
    for (int i = 0; i < GS_STAGE_COUNT; i++) if (!gs_inbox_published[i].id[0]) {
        gs_inbox_published[i].entry = *entry; gs_inbox_published[i].slot = slot;
        gs_inbox_published[i].at = sceKernelGetProcessTime();
        snprintf(gs_inbox_published[i].id, sizeof(gs_inbox_published[i].id), "%s", id);
        return;
    }
}

/* 1: the slot's job file holds this record, 2: another record or none, 0: unknown. */
static int gs_inbox_slot_holds(int slot, const char *id)
{
    char path[1100], version[16], line[300], encoded[260];
    gs_stage_path(slot, "job", path, sizeof(path));
    if (!path[0]) return 0;
    FILE *file = fopen(path, "rb");
    if (!file) return errno == ENOENT ? 2 : 0;
    gs_b64encode((const unsigned char *)id, strlen(id), encoded, sizeof(encoded));
    int held = gs_line(file, version, sizeof(version)) || gs_line(file, line, sizeof(line)) ? 0 :
        !strcmp(version, "18") && !strcmp(line, encoded) ? 1 : 2;
    fclose(file);
    return held;
}

/* A publication by the application can still replace a record this worker just
 * wrote. Until the stage loop has taken a record in, check that its slot file
 * holds it; if another record replaced it, release the upload for a new attempt. */
static void gs_inbox_verify_published(uint64_t now)
{
    for (int i = 0; i < GS_STAGE_COUNT; i++) {
        GsInboxEntry *entry = &gs_inbox_published[i].entry;
        const char *id = gs_inbox_published[i].id;
        int slot = gs_inbox_published[i].slot;
        if (!id[0]) continue;
        int held = gs_inbox_slot_holds(slot, id);
        if (!strcmp(gs_stages[slot].job.id, id)) {
            if (held == 2) gs_log_write("resident", "ftp-inbox slot-conflict slot=%d job=%s: job file replaced after intake", slot, id);
            gs_inbox_published[i].id[0] = 0; continue;
        }
        if (held == 1 || (!held && now - gs_inbox_published[i].at < 300000000ULL)) continue;
        if (held == 2) {
            char path[1100]; GsInboxClaim claim;
            if (!gs_inbox_claim_path(path, sizeof(path), entry->name, entry->size, entry->mtime) &&
                !gs_inbox_read_claim(path, &claim) && !strcmp(claim.job, id)) unlink(path);
            gs_log_write("resident", "ftp-inbox publish-replaced slot=%d job=%s source=%s; upload released for retry", slot, id, entry->name);
        }
        gs_inbox_published[i].id[0] = 0;
    }
}

/* A failed or canceled archive job records that outcome in its claim, keeping
 * the set count and bytes stored when it was published. */
static void gs_inbox_record_outcome(const GsStage *stage)
{
    const char *leaf = strrchr(stage->job.destination, '/');
    GsInboxEntry entry; GsInboxClaim claim; char path[1100], body[700];
    memset(&entry, 0, sizeof(entry));
    if (!leaf || snprintf(entry.name, sizeof(entry.name), "%s", leaf + 1) >= (int)sizeof(entry.name) ||
        gs_inbox_stat(stage->job.destination, &entry.size, &entry.mtime) ||
        gs_inbox_claim_path(path, sizeof(path), entry.name, entry.size, entry.mtime) ||
        gs_inbox_read_claim(path, &claim) || strcmp(claim.owner, "resident") || strcmp(claim.job, stage->job.id) ||
        !strcmp(claim.state, stage->state) ||
        gs_inbox_claim_body(body, sizeof(body), entry.name, entry.size, entry.mtime, stage->job.id, stage->state,
            claim.count, claim.total) < 0) return;
    if (write_atomic(path, body)) gs_log_write("resident", "ftp-inbox claim-update-failed job=%s", stage->job.id);
}

/* 1 while the durable singleton install record (job.txt) names this job or
 * cannot be read. */
static int gs_inbox_install_record_names(const char *id)
{
    char path[128], line[300], encoded[260];
    if (gs_ipc_path(path, sizeof(path), "/job.txt")) return 1;
    FILE *file = fopen(path, "rb");
    if (!file) return errno == ENOENT ? 0 : 1;
    gs_b64encode((const unsigned char *)id, strlen(id), encoded, sizeof(encoded));
    int named = gs_line(file, line, sizeof(line)) || gs_line(file, line, sizeof(line)) || !strcmp(line, encoded);
    fclose(file);
    return named;
}

/* 1 while BGFT may read an extracted package for this content. A storage
 * registration (route 7) reads the file directly, so any remaining task keeps
 * it; a loopback registration reads it only through this worker, which never
 * serves a released job again, so a failed or finished task does not. A lookup
 * that fails counts as a reader. */
static int gs_inbox_task_reads(const char *content, int kind, int storage)
{
    int (*find)(const char *, int, int *), (*progress)(int, GsBgftProgress *); int task = -1;
    if (gs_bgft_module < 0 || gs_bind(gs_bgft_module, "sceBgftServiceDownloadFindTaskByContentId", &find) ||
        gs_bind(gs_bgft_module, "sceBgftServiceDownloadGetProgress", &progress)) return 1;
    int rc = find(content, kind, &task);
    if ((uint32_t)rc == 0x80990019u || (!rc && task < 0)) return 0;
    if (rc || storage) return 1;
    GsBgftProgress state; memset(&state, 0, sizeof(state));
    if (progress(task, &state)) return 1;
    if (state.error) return 0;
    uint64_t total = state.length_total ? state.length_total : state.length;
    uint64_t done = state.length_total ? state.transferred_total : state.transferred;
    return !(total && done >= total && state.preparing >= 100 && state.copy >= 100);
}

/* 1 when an install journal (written by gs_install_archive_pkg) leaves its
 * package in use: a registered or pending task that may still read it, an
 * add-on handed to AppInstUtil, a theme license, or a record not understood. */
static int gs_inbox_journal_in_use(const char *journal)
{
    char content[49], extra; int task = -1, complete = 0, pending = 0, route = 0, kind = 0;
    FILE *file = fopen(journal, "rb");
    if (!file) return 1;
    int fields = fscanf(file, "%48s %d %d %d %d %d %c", content, &task, &complete, &pending, &route, &kind, &extra);
    fclose(file);
    if ((fields != 5 && fields != 6) || task < -1 || (complete != 0 && complete != 1) || pending < 0 || pending > 3 ||
        (fields == 6 && kind == 9)) return 1;
    if (complete || (task < 0 && !pending)) return 0; // installed, or no task was registered
    if (pending == 2 || fields != 6 || kind < 6 || kind > 8) return 1;
    return gs_inbox_task_reads(content, kind, route == 7);
}

/* A failed or canceled inbox archive keeps its extracted packages (up to the
 * archive's full size) in <staging>/extract-<job>. A retry is a new job with its
 * own folder and nothing reads a released job's folder again, so remove its
 * pkg-NNN.pkg and .part files unless the install record or a journal there shows
 * that installation may still read them. Uploaded files in pkg-rars are never
 * touched. */
static void gs_inbox_remove_extracted(const GsStage *stage)
{
    char directory[1100], path[1200]; int removed = 0;
    if (stage->job.expected_kind || !stage->job.storage_root[0] ||
        snprintf(directory, sizeof(directory), "%s/extract-%s", stage->job.storage_root, stage->job.id) >= (int)sizeof(directory) ||
        gs_nofollow_path(directory, 1) != 1) return;
    if (gs_inbox_install_record_names(stage->job.id)) {
        gs_log_write("resident", "ftp-inbox extracted-kept job=%s reason=install-record", stage->job.id); return;
    }
    for (int ordinal = 1; ordinal <= 256; ordinal++) {
        snprintf(path, sizeof(path), "%s/install-%03d.txt", directory, ordinal);
        int journal = gs_nofollow_path(path, 0);
        if (journal && (journal < 0 || gs_inbox_journal_in_use(path))) {
            gs_log_write("resident", "ftp-inbox extracted-kept job=%s reason=install-journal ordinal=%d", stage->job.id, ordinal);
            return;
        }
    }
    for (int ordinal = 1; ordinal <= 256; ordinal++)
        for (int partial = 0; partial < 2; partial++) {
            snprintf(path, sizeof(path), "%s/pkg-%03d.pkg%s", directory, ordinal, partial ? ".part" : "");
            if (gs_nofollow_path(path, 0) == 1 && !unlink(path)) removed++;
        }
    if (removed) gs_log_write("resident", "ftp-inbox extracted-removed job=%s files=%d", stage->job.id, removed);
}

/* Releases finished inbox jobs so their slot is reused, and reports the outcome. */
static void gs_inbox_finish_jobs(void)
{
    for (int i = 0; i < GS_STAGE_COUNT; i++) {
        GsStage *stage = &gs_stages[i];
        if (strncmp(stage->job.id, GS_INBOX_PREFIX, strlen(GS_INBOX_PREFIX)) || stage->release || stage->created ||
            !strcmp(gs_job.id, stage->job.id)) continue;
        int installed = !strcmp(stage->state, "installed");
        if (!installed && strcmp(stage->state, "failed") && strcmp(stage->state, "canceled")) continue;
        int reported = 0;
        for (int j = 0; j < GS_STAGE_COUNT; j++) if (!strcmp(gs_inbox_reported[j], stage->job.id)) reported = 1;
        if (!reported) {
            const char *name = strrchr(stage->job.destination, '/');
            name = name ? name + 1 : stage->job.destination;
            if (installed) gs_inbox_notify("Installed", name);
            else {
                char detail[300]; snprintf(detail, sizeof(detail), "%.160s could not be installed; the file is kept", name);
                gs_download_notification("FTP upload", detail);
                gs_inbox_record_outcome(stage);
                gs_inbox_remove_extracted(stage);
            }
            gs_log_write("resident", "ftp-inbox finished job=%s state=%s error=%s", stage->job.id, stage->state, stage->error);
            snprintf(gs_inbox_reported[gs_inbox_reported_next], sizeof(gs_inbox_reported[0]), "%s", stage->job.id);
            gs_inbox_reported_next = (gs_inbox_reported_next + 1) % GS_STAGE_COUNT;
        }
        char path[1100], body[400], encoded[260];
        gs_stage_path(i, "control", path, sizeof(path));
        gs_b64encode((const unsigned char *)stage->job.id, strlen(stage->job.id), encoded, sizeof(encoded));
        snprintf(body, sizeof(body), "2\n%s\nrelease\n%s\n", encoded, stage->job.generation);
        if (write_atomic(path, body)) gs_log_write("resident", "ftp-inbox release-write-failed job=%s", stage->job.id);
    }
}

/* Queues a claimed first volume or single archive for the set check after the
 * scan when its own job failed or was canceled. */
static void gs_inbox_note_outcome(const GsInboxEntry *entry)
{
    char path[1100]; GsInboxClaim claim;
    if (gs_inbox_kind(entry->name) != 1 || gs_inbox_ends_with(entry->name, ".pkg") ||
        gs_inbox_recheck_count == GS_INBOX_RECHECKS ||
        gs_inbox_claim_path(path, sizeof(path), entry->name, entry->size, entry->mtime) ||
        gs_inbox_read_claim(path, &claim) || strcmp(claim.owner, "resident") || claim.count < 0 ||
        (strcmp(claim.state, "failed") && strcmp(claim.state, "canceled"))) return;
    gs_inbox_rechecks[gs_inbox_recheck_count].entry = *entry;
    gs_inbox_rechecks[gs_inbox_recheck_count].count = claim.count;
    gs_inbox_rechecks[gs_inbox_recheck_count++].total = claim.total;
}

/* Releases the claim of a failed set whose file count or bytes changed since it
 * was published, so the next scans install the set again as a new job. */
static void gs_inbox_retry_changed_sets(void)
{
    for (int i = 0; i < gs_inbox_recheck_count; i++) {
        const GsInboxEntry *entry = &gs_inbox_rechecks[i].entry; char path[1100]; int64_t count, total;
        if (gs_inbox_set_find(entry->name, &count, &total) ||
            (count == gs_inbox_rechecks[i].count && total == gs_inbox_rechecks[i].total) ||
            gs_inbox_claim_path(path, sizeof(path), entry->name, entry->size, entry->mtime) || unlink(path)) continue;
        gs_log_write("resident", "ftp-inbox retry reason=set-changed files=%lld->%lld bytes=%lld->%lld name=%s",
            (long long)gs_inbox_rechecks[i].count, (long long)count, (long long)gs_inbox_rechecks[i].total,
            (long long)total, entry->name);
    }
    gs_inbox_recheck_count = 0;
}

/* Claims and publishes one ready upload whose primary file is entry. */
static void gs_inbox_take(const GsInboxEntry *entry, const char *directory)
{
    char source[1100], root[1024], content[49] = "", title[16] = "", id[64], generation[40];
    int kind = 0; int64_t size, mtime, set_count = -1, set_total = -1;
    if (snprintf(source, sizeof(source), "%s/%s", directory, entry->name) >= (int)sizeof(source)) return;
    // A file that changed or vanished since the scan is picked up by a later scan.
    if (gs_inbox_stat(source, &size, &mtime) || size != entry->size || mtime != entry->mtime) return;
    if (gs_inbox_set_find(entry->name, &set_count, &set_total)) set_count = set_total = -1;
    /* Local-source records admit only names without control characters, ':' or
     * '\'. Claim such an upload once instead of republishing it every retry. */
    if (gs_local_source_root(source, root, sizeof(root))) {
        if (gs_inbox_claim(entry, "", "unsupported-name", set_count, set_total) == 1) {
            char name[248], detail[300]; gs_inbox_printable(entry->name, name, sizeof(name));
            snprintf(detail, sizeof(detail), "%.160s was skipped; rename it without ':', '\\' or control characters", name);
            gs_download_notification("FTP upload", detail);
            gs_log_write("resident", "ftp-inbox skip unsupported-name name=%s", name);
        }
        return;
    }
    if (gs_inbox_ends_with(entry->name, ".pkg")) {
        /* The staged intake rejects a PKG below 4 KiB, which would hold a slot forever. */
        if (size < 0x1000 || gs_inbox_pkg_identity(source, content, title, &kind)) {
            if (gs_inbox_claim(entry, "", "invalid", set_count, set_total) == 1) gs_inbox_notify("FTP upload is not a PS4 package", entry->name);
            return;
        }
        if (gs_inbox_installed(source, content, title, kind)) {
            if (gs_inbox_claim(entry, "", "installed", set_count, set_total) == 1) {
                char detail[300]; snprintf(detail, sizeof(detail), "%.200s is already installed", entry->name);
                gs_download_notification("FTP upload", detail);
                gs_log_write("resident", "ftp-inbox skip installed content=%s source=%s", content, source);
            }
            return;
        }
        int task = gs_inbox_active_task(content, kind);
        if (task >= 0) {
            if (gs_inbox_claim(entry, "", "busy", set_count, set_total) == 1) {
                char detail[300]; snprintf(detail, sizeof(detail), "%.160s is already being installed by the PS4; SSPI skipped it", entry->name);
                gs_download_notification("FTP upload", detail);
                gs_log_write("resident", "ftp-inbox skip active-task task=%d content=%s source=%s", task, content, source);
            }
            return;
        }
    } else if (size < 8) {
        /* The staged intake rejects an archive below 8 bytes (no signature fits). */
        if (gs_inbox_claim(entry, "", "invalid", set_count, set_total) == 1) gs_inbox_notify("FTP upload is not a RAR, ZIP or 7z archive", entry->name);
        return;
    }
    int slot = gs_inbox_free_slot();
    if (slot < 0) {
        uint64_t at = sceKernelGetProcessTime();
        if (gs_inbox_retry_at <= at) gs_log_write("resident", "ftp-inbox publish-deferred reason=no-free-slot source=%s", source);
        gs_inbox_retry_at = at + GS_INBOX_RETRY_US; // one log line per hand-off pass
        return;
    }
    uint64_t seed = gs_inbox_hash(entry->name, 0xcbf29ce484222325ULL) ^ (uint64_t)sceKernelGetProcessTime() ^
        ((uint64_t)entry->size << 1) ^ ((uint64_t)getpid() << 32) ^ ++gs_inbox_serial;
    uint64_t second = gs_inbox_hash(content, seed * 0x9e3779b97f4a7c15ULL + (uint64_t)time(NULL));
    snprintf(id, sizeof(id), GS_INBOX_PREFIX "%016llx%08llx", (unsigned long long)second, (unsigned long long)(seed & 0xffffffffULL));
    snprintf(generation, sizeof(generation), "%016llx%016llx", (unsigned long long)seed, (unsigned long long)second);
    // 0: the application or an earlier pass owns it; -1: logged, retried after the backoff.
    if (gs_inbox_claim(entry, id, "queued", set_count, set_total) != 1) return;
    if (gs_inbox_publish(slot, id, generation, source, title, content, entry->size, kind) < 0) {
        gs_inbox_unclaim(entry); // Nothing was published; either side may retry.
        gs_inbox_retry_at = sceKernelGetProcessTime() + GS_INBOX_RETRY_US;
        gs_log_write("resident", "ftp-inbox publish-deferred source=%s", source);
        return;
    }
    gs_inbox_track_publish(entry, id, slot);
    gs_inbox_notify(kind ? "Installing FTP upload" : "Extracting FTP upload", entry->name);
}

static void gs_inbox_poll(void)
{
    uint64_t now = sceKernelGetProcessTime();
    if (now < gs_inbox_next_poll || !g_transfer_ready || !g_bgft_ready) return;
    gs_inbox_next_poll = now + GS_INBOX_POLL_US;
    gs_inbox_verify_published(now);
    gs_inbox_finish_jobs();
    gs_inbox_observe_slots(now);
    char directory[128]; snprintf(directory, sizeof(directory), "%s/pkg-rars", gs_inbox_data_root());
    if (gs_nofollow_path(directory, 1) != 1) { gs_inbox_count = 0; return; }
    if (now >= gs_inbox_prune_at) { gs_inbox_prune_at = now + GS_INBOX_PRUNE_US; gs_inbox_prune_claims(directory); }
    DIR *folder = opendir(directory);
    if (!folder) return;
    for (int i = 0; i < gs_inbox_count; i++) gs_inbox_entries[i].present = 0;
    gs_inbox_set_used = 0; gs_inbox_recheck_count = 0;
    int was_idle = gs_inbox_count == 0, arrived = 0, scanned = 0, filling = 0;
    char first[248] = "";
    struct dirent *item;
    while ((item = readdir(folder)) && ++scanned <= 1024) {
        if (!gs_inbox_kind(item->d_name)) continue;
        char path[1100]; GsInboxEntry probe; memset(&probe, 0, sizeof(probe));
        if (snprintf(probe.name, sizeof(probe.name), "%s", item->d_name) >= (int)sizeof(probe.name) ||
            snprintf(path, sizeof(path), "%s/%s", directory, item->d_name) >= (int)sizeof(path)) continue;
        if (gs_inbox_stat(path, &probe.size, &probe.mtime)) {
            /* A client creates each upload (often the next RAR volume) empty and can
             * pause before sending data. A recent empty file is activity, not quiet. */
            int64_t age = (int64_t)time(NULL) - probe.mtime, window = (int64_t)(gs_inbox_quiet_us(probe.name) / 1000000ULL);
            if (!probe.size && probe.mtime > 0 && age > -window && age < window) filling = 1;
            continue;
        }
        gs_inbox_set_add(probe.name, probe.size);
        int at = -1;
        for (int i = 0; i < gs_inbox_count; i++) if (!strcmp(gs_inbox_entries[i].name, probe.name)) { at = i; break; }
        int processed = gs_inbox_claimed(&probe);
        if (!processed && gs_inbox_legacy_processed(&probe)) { gs_inbox_claim(&probe, "", "legacy", -1, -1); processed = 1; }
        if (processed) {
            gs_inbox_note_outcome(&probe);
            if (at >= 0) gs_inbox_entries[at] = gs_inbox_entries[--gs_inbox_count];
            continue;
        }
        if (at < 0) {
            if (gs_inbox_count >= GS_INBOX_TRACKED) continue;
            at = gs_inbox_count++;
            probe.changed_at = now; gs_inbox_entries[at] = probe;
            if (!first[0]) snprintf(first, sizeof(first), "%s", probe.name);
            arrived = 1;
        } else if (gs_inbox_entries[at].size != probe.size || gs_inbox_entries[at].mtime != probe.mtime) {
            gs_inbox_entries[at].size = probe.size; gs_inbox_entries[at].mtime = probe.mtime;
            gs_inbox_entries[at].changed_at = now;
        }
        gs_inbox_entries[at].present = 1;
    }
    closedir(folder);
    gs_inbox_sets_complete = scanned <= 1024; // set sizes are only compared after a complete scan
    gs_inbox_retry_changed_sets();
    for (int i = 0; i < gs_inbox_count;) {
        if (!gs_inbox_entries[i].present) gs_inbox_entries[i] = gs_inbox_entries[--gs_inbox_count];
        else i++;
    }
    if (arrived && was_idle && strcmp(gs_inbox_announced, first)) {
        snprintf(gs_inbox_announced, sizeof(gs_inbox_announced), "%s", first); // A deferred retry is not a new upload.
        gs_inbox_notify("Receiving FTP upload", first);
        gs_log_write("resident", "ftp-inbox upload-started name=%s", first);
    }
    if (!gs_inbox_count || filling || now < gs_inbox_retry_at) return;
    /* RAR sets arrive one volume at a time: wait until the whole inbox is quiet,
     * for the longer archive period while any tracked file belongs to a set. */
    uint64_t quiet = GS_INBOX_QUIET_US;
    for (int i = 0; i < gs_inbox_count; i++)
        if (gs_inbox_quiet_us(gs_inbox_entries[i].name) > quiet) quiet = gs_inbox_quiet_us(gs_inbox_entries[i].name);
    for (int i = 0; i < gs_inbox_count; i++) if (now - gs_inbox_entries[i].changed_at < quiet) return;
    /* While the free staged slots settle, ready uploads stay tracked and are
     * handed off at a later poll without a new quiet period. */
    if (now < gs_inbox_slots_settled_at) return;
    for (int i = 0; i < gs_inbox_count; i++) {
        GsInboxEntry *entry = &gs_inbox_entries[i];
        if (gs_inbox_kind(entry->name) == 1) gs_inbox_take(entry, directory);
    }
    /* Continuation volumes belong to their set's claim; mark them processed. */
    for (int i = 0; i < gs_inbox_count; i++)
        if (gs_inbox_kind(gs_inbox_entries[i].name) == 2) gs_inbox_claim(&gs_inbox_entries[i], "", "volume", -1, -1);
    gs_inbox_count = 0;
    if (sceKernelGetProcessTime() >= gs_inbox_retry_at) gs_inbox_announced[0] = 0; // Nothing deferred.
}
