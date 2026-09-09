#include "../../SDK/sqlite/sqlite3.h"
#include <errno.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

int gs_resident_mount_system_data(void);

#define GS_APP_DB "/system_data/priv/mms/app.db"
#define GS_DAEMON_TID "SRCHD0001"
#define GS_DONOR_TID "NPXS20119"
#define GS_HOST_TID "SRCH00001"
#define GS_APPDB_LOG "/data/GameSearch/resident/appdb.txt"

static void appdb_log(const char* line)
{
    FILE* fp;
    mkdir("/data", 0777);
    mkdir("/data/GameSearch", 0777);
    mkdir("/data/GameSearch/resident", 0777);
    fp = fopen(GS_APPDB_LOG, "a");
    if (!fp) return;
    fputs(line ? line : "?", fp);
    fputc('\n', fp);
    fclose(fp);
}

static int ident_ok(const char* s)
{
    int i;
    if (!s || !s[0]) return 0;
    for (i = 0; s[i]; i++)
    {
        char c = s[i];
        if (!((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
            (c >= '0' && c <= '9') || c == '_'))
            return 0;
    }
    return 1;
}

static int count_sql(sqlite3* db, const char* sql, const char* title_id, int* out)
{
    sqlite3_stmt* stmt = 0;
    int rc;
    *out = 0;
    rc = sqlite3_prepare_v2(db, sql, -1, &stmt, 0);
    if (rc != SQLITE_OK)
    {
        char line[256];
        snprintf(line, sizeof(line), "prepare %d %s", rc, sqlite3_errmsg(db));
        appdb_log(line);
        return rc;
    }
    if (title_id) sqlite3_bind_text(stmt, 1, title_id, -1, SQLITE_STATIC);
    rc = sqlite3_step(stmt);
    if (rc == SQLITE_ROW) *out = sqlite3_column_int(stmt, 0);
    sqlite3_finalize(stmt);
    return rc == SQLITE_ROW || rc == SQLITE_DONE ? SQLITE_OK : rc;
}

static int count_title(sqlite3* db, const char* title_id, int* out)
{
    int rc = count_sql(db, "SELECT COUNT(*) FROM tbl_appinfo WHERE titleId=?1",
        title_id, out);
    if (rc == SQLITE_OK && *out > 0) return rc;
    return count_sql(db, "SELECT COUNT(*) FROM tbl_appinfo WHERE titleID=?1",
        title_id, out);
}

static void dump_schema(sqlite3* db)
{
    sqlite3_stmt* stmt = 0;
    int total = 0;
    count_sql(db, "SELECT COUNT(*) FROM tbl_appinfo", 0, &total);
    char line[160];
    snprintf(line, sizeof(line), "tbl_appinfo rows=%d", total);
    appdb_log(line);
    if (sqlite3_prepare_v2(db,
        "SELECT name FROM sqlite_master WHERE type='table' LIMIT 12",
        -1, &stmt, 0) != SQLITE_OK)
        return;
    while (sqlite3_step(stmt) == SQLITE_ROW)
    {
        const char* name = (const char*)sqlite3_column_text(stmt, 0);
        snprintf(line, sizeof(line), "table %s", name ? name : "?");
        appdb_log(line);
    }
    sqlite3_finalize(stmt);
}

static int exec_sql(sqlite3* db, const char* sql)
{
    char* err = 0;
    int rc = sqlite3_exec(db, sql, 0, 0, &err);
    if (rc != SQLITE_OK)
    {
        char line[768];
        snprintf(line, sizeof(line), "sql rc=%d %s | %.400s", rc,
            err ? err : "", sql ? sql : "");
        appdb_log(line);
        sqlite3_free(err);
    }
    return rc;
}

static void rewrite_col(char* out, size_t n, const char* col, const char* donor)
{
    if (strcmp(col, "titleId") == 0 || strcmp(col, "titleID") == 0)
        snprintf(out, n, "'%s'", GS_DAEMON_TID);
    else if (strcmp(col, "titleName") == 0)
        snprintf(out, n, "'Game Search Service'");
    else if (strcmp(col, "visible") == 0)
        snprintf(out, n, "0");
    else if (strcmp(col, "canRemove") == 0)
        snprintf(out, n, "0");
    else if (strcmp(col, "category") == 0 || strcmp(col, "uiCategory") == 0)
        snprintf(out, n, "'gdd'");
    else if (strcmp(col, "metaDataPath") == 0)
        snprintf(out, n, "'/system/vsh/app/%s/sce_sys'", GS_DAEMON_TID);
    else if (strcmp(col, "contentId") == 0)
        snprintf(out, n, "REPLACE(%s,'%s','%s')", col, donor, GS_DAEMON_TID);
    else
        snprintf(out, n,
            "REPLACE(REPLACE(%s,'%s','%s'),'/system/vsh/app/%s','/system/vsh/app/%s')",
            col, donor, GS_DAEMON_TID, donor, GS_DAEMON_TID);
}

static int clone_browse_table(sqlite3* db, const char* table, const char* donor)
{
    sqlite3_stmt* info = 0;
    char pragma[160];
    char cols[64][64];
    char sql[8192];
    char expr[256];
    int n = 0;
    int i;
    int rc;
    int off;
    if (!ident_ok(table)) return SQLITE_MISUSE;
    snprintf(pragma, sizeof(pragma), "PRAGMA table_info(%s)", table);
    rc = sqlite3_prepare_v2(db, pragma, -1, &info, 0);
    if (rc != SQLITE_OK) return rc;
    while (n < 64 && sqlite3_step(info) == SQLITE_ROW)
    {
        const char* name = (const char*)sqlite3_column_text(info, 1);
        if (!name || !ident_ok(name)) continue;
        snprintf(cols[n], sizeof(cols[n]), "%s", name);
        n++;
    }
    sqlite3_finalize(info);
    if (n <= 0) return SQLITE_OK;
    off = snprintf(sql, sizeof(sql),
        "INSERT INTO %s SELECT ", table);
    for (i = 0; i < n && off > 0 && (size_t)off < sizeof(sql) - 8; i++)
    {
        rewrite_col(expr, sizeof(expr), cols[i], donor);
        off += snprintf(sql + off, sizeof(sql) - (size_t)off, "%s%s",
            i ? "," : "", expr);
    }
    if (off < 0 || (size_t)off >= sizeof(sql) - 160) return SQLITE_TOOBIG;
    snprintf(sql + off, sizeof(sql) - (size_t)off,
        " FROM %s WHERE titleId='%s' AND NOT EXISTS (SELECT 1 FROM %s x WHERE x.titleId='%s')",
        table, donor, table, GS_DAEMON_TID);
    return exec_sql(db, sql);
}

static int clone_browse(sqlite3* db, const char* donor)
{
    sqlite3_stmt* stmt = 0;
    int rc = sqlite3_prepare_v2(db,
        "SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'tbl_appbrowse_%'",
        -1, &stmt, 0);
    if (rc != SQLITE_OK) return rc;
    while (sqlite3_step(stmt) == SQLITE_ROW)
    {
        const char* name = (const char*)sqlite3_column_text(stmt, 0);
        if (!name) continue;
        rc = clone_browse_table(db, name, donor);
        if (rc != SQLITE_OK)
        {
            char line[160];
            snprintf(line, sizeof(line), "browse %s rc=%d", name, rc);
            appdb_log(line);
        }
    }
    sqlite3_finalize(stmt);
    return SQLITE_OK;
}

static int clone_appinfo(sqlite3* db, const char* donor)
{
    char sql[2048];
    snprintf(sql, sizeof(sql),
        "INSERT OR IGNORE INTO tbl_appinfo(titleId,key,val) "
        "SELECT '%s', key, CASE key "
        "WHEN 'TITLE_ID' THEN '%s' "
        "WHEN 'TITLE' THEN 'Game Search Service' "
        "WHEN 'CATEGORY' THEN 'gdd' "
        "WHEN 'CONTENT_ID' THEN REPLACE(val,'%s','%s') "
        "WHEN '_org_path' THEN '/system/vsh/app/%s' "
        "WHEN '_metadata_path' THEN '/system/vsh/app/%s/sce_sys' "
        "ELSE REPLACE(REPLACE(val,'%s','%s'),'/system/vsh/app/%s','/system/vsh/app/%s') "
        "END FROM tbl_appinfo WHERE titleId='%s'",
        GS_DAEMON_TID, GS_DAEMON_TID, donor, GS_DAEMON_TID, GS_DAEMON_TID,
        GS_DAEMON_TID, donor, GS_DAEMON_TID, donor, GS_DAEMON_TID, donor);
    return exec_sql(db, sql);
}

__attribute__((visibility("default"))) int gs_resident_register_in_appdb(void)
{
    sqlite3* db = 0;
    int rc;
    int donor = 0;
    int host = 0;
    int ours = 0;
    const char* source;
    char line[160];

    unlink(GS_APPDB_LOG);
    appdb_log("register begin");
    rc = gs_resident_mount_system_data();
    snprintf(line, sizeof(line), "system_data remount=%d errno=%d", rc, errno);
    appdb_log(line);

    rc = sqlite3_open_v2(GS_APP_DB, &db, SQLITE_OPEN_READWRITE, 0);
    if (rc != SQLITE_OK)
    {
        snprintf(line, sizeof(line), "open %s rc=%d %s", GS_APP_DB, rc,
            db ? sqlite3_errmsg(db) : sqlite3_errstr(rc));
        appdb_log(line);
        if (db) sqlite3_close(db);
        return rc ? rc : -1;
    }
    sqlite3_busy_timeout(db, 8000);
    exec_sql(db, "PRAGMA foreign_keys=OFF");

    count_title(db, GS_DONOR_TID, &donor);
    count_title(db, GS_HOST_TID, &host);
    count_title(db, GS_DAEMON_TID, &ours);
    snprintf(line, sizeof(line), "counts donor=%d host=%d ours=%d", donor, host, ours);
    appdb_log(line);
    if (donor <= 0 && host <= 0) dump_schema(db);

    source = donor > 0 ? GS_DONOR_TID : (host > 0 ? GS_HOST_TID : 0);
    if (!source)
    {
        appdb_log("no donor title");
        sqlite3_close(db);
        return -2;
    }
    if (ours <= 0)
    {
        rc = clone_appinfo(db, source);
        if (rc != SQLITE_OK)
        {
            sqlite3_close(db);
            return rc;
        }
        clone_browse(db, source);
        exec_sql(db,
            "INSERT OR REPLACE INTO tbl_appinfo(titleId,key,val) VALUES"
            "('SRCHD0001','TITLE_ID','SRCHD0001'),"
            "('SRCHD0001','TITLE','Game Search Service'),"
            "('SRCHD0001','CATEGORY','gdd'),"
            "('SRCHD0001','_org_path','/system/vsh/app/SRCHD0001'),"
            "('SRCHD0001','_metadata_path','/system/vsh/app/SRCHD0001/sce_sys')");
    }
    else appdb_log("already present");

    sqlite3_wal_checkpoint_v2(db, 0, SQLITE_CHECKPOINT_FULL, 0, 0);
    count_title(db, GS_DAEMON_TID, &ours);
    snprintf(line, sizeof(line), "done ours=%d", ours);
    appdb_log(line);
    sqlite3_close(db);
    return ours > 0 ? 0 : -3;
}
