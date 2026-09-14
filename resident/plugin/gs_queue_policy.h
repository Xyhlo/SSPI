#ifndef GS_QUEUE_POLICY_H
#define GS_QUEUE_POLICY_H
static int gs_install_rank(int kind)
{ return kind == 0 || kind == 6 ? 0 : kind == 8 ? 1 : 2; }

static int gs_precedes_same_title(const char *title, int kind, const char *other_title, int other_kind)
{
    return title && *title && other_title && !strcmp(title, other_title) &&
        gs_install_rank(other_kind) < gs_install_rank(kind);
}
#endif
