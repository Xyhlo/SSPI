#ifndef GS_STAGE_DEPENDENCY_H
#define GS_STAGE_DEPENDENCY_H
/* Install dependency decision for staged packages. Extracted unchanged from
 * gs_staged_transfer.inc so the host regression harness can exercise the real
 * function against stub job records and receipts. It is included at the same
 * point in gs_staged_transfer.inc, so it still sees the production GsStage,
 * gs_stages[], gs_stage_read_job(), gs_ipc_path(), gs_line() and
 * gs_encoded_line() definitions. */
#define GS_DEPENDENCY_CHAIN_DEPTH 16

/* 1 when the explicit `after` chain that starts at `start` reaches `target`.
 * Follows stored job records, so a container row that must follow another
 * container row is still recognised. Depth is bounded: a malformed or cyclic
 * chain terminates instead of looping, and the walk never mutates the queue. */
static int gs_explicit_chain_reaches(const char *start, const char *target, int max_depth)
{
    char current[192];
    if (!start || !*start || !target || !*target) return 0;
    snprintf(current, sizeof(current), "%s", start);
    for (int depth = 0; depth < max_depth; depth++) {
        if (!strcmp(current, target)) return 1;
        int advanced = 0;
        for (int i = 0; i < GS_STAGE_COUNT && !advanced; i++) {
            GsJob candidate;
            if (gs_stage_read_job(i, &candidate) || strcmp(candidate.id, current)) continue;
            snprintf(current, sizeof(current), "%s", candidate.after);
            advanced = 1;
        }
        if (!advanced) break;
    }
    return !strcmp(current, target);
}

static int gs_stage_dependency_ready(const GsStage *stage)
{
    /* Re-evaluate lower-rank packages, including jobs added after this one.
     * A dependency snapshot alone cannot support arbitrary queue order. */
    for (int i = 0; i < GS_STAGE_COUNT; i++) {
        GsJob candidate;
        if (gs_stage_read_job(i, &candidate) || !strcmp(candidate.id, stage->job.id) ||
            !candidate.auto_install || !gs_precedes_same_title(stage->job.title, stage->job.expected_kind,
                candidate.title, candidate.expected_kind)) continue;
        /* An inferred rank edge must not contradict an explicit chain. A staged
         * archive/container record carries no package type, so it is stored as
         * expected_kind 0 and gs_install_rank() reads it as a base. When such a
         * record must follow this package - directly or through other records -
         * it cannot also precede it; keeping that edge deadlocks both sides
         * until one queue row is removed. Only this contradiction is dropped:
         * real bases, explicit chains, unloaded on-disk prerequisites, missing
         * receipts and unrelated rows keep blocking exactly as before. */
        if (candidate.expected_kind == 0 &&
            gs_explicit_chain_reaches(candidate.after, stage->job.id, GS_DEPENDENCY_CHAIN_DEPTH))
            continue;
        if (strcmp(gs_stages[i].job.id, candidate.id) || strcmp(gs_stages[i].state, "installed")) return 0;
    }
    if (!stage->job.after[0]) return 1;
    for (int i = 0; i < GS_STAGE_COUNT; i++) {
        if (!strcmp(gs_stages[i].job.id, stage->job.after) && strcmp(gs_stages[i].state, "installed")) return 0;
        GsJob queued;
        if (!gs_stage_read_job(i, &queued) && !strcmp(queued.id, stage->job.after) &&
            (!gs_stages[i].job.id[0] || strcmp(gs_stages[i].state, "installed"))) return 0;
    }
    char path[1100], line[256], id[192], content[49], leaf[256];
    snprintf(leaf, sizeof(leaf), "/installed-%s.txt", stage->job.after);
    if (gs_ipc_path(path, sizeof(path), leaf)) return 0;
    FILE *file = fopen(path, "rb"); if (!file) return 0;
    int good = !gs_line(file, line, sizeof(line)) && !strcmp(line, "1") &&
        !gs_encoded_line(file, id, sizeof(id)) && !strcmp(id, stage->job.after) &&
        !gs_encoded_line(file, content, sizeof(content)) && strlen(content) == 36 &&
        (!stage->job.title[0] || !memcmp(content + 7, stage->job.title, 9));
    fclose(file); return good;
}
#endif
