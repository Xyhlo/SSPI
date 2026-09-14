/* GoldHEN SDK command ABI, adapted from GoldHEN/GoldHEN_Plugins_SDK.
 * Copyright (c) 2022 GoldHEN. MIT; see THIRD_PARTY_NOTICES.md.
 * Only process identification and named PRX loading are used here.
 */
#ifndef GS_GOLDHEN_PROCESS_H
#define GS_GOLDHEN_PROCESS_H
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>
#include "../../native/sspi_log.h"
#include "shared_module_path.h"

typedef struct {
    int32_t pid;
    char name[40], path[64], titleid[16], contentid[64], version[6];
    uint64_t base_address;
} __attribute__((packed)) GsGoldHenProcess;

typedef struct {
    char process_name[32], prx_path[100];
    uint64_t result;
} __attribute__((packed)) GsGoldHenPrxLoad;

_Static_assert(sizeof(GsGoldHenProcess) == 202, "GoldHEN process ABI");
_Static_assert(sizeof(GsGoldHenPrxLoad) == 140, "GoldHEN PRX loader ABI");

/* GoldHEN SDK commands 2/3 save and restore the process filesystem context.
 * This is process-wide, so the resident holds a cross-module lifetime lease. */
typedef struct {
    uint32_t uid, ruid, rgid, groups;
    uint64_t paid, caps[2];
    void *prison, *cdir, *jdir, *rdir;
} GsGoldHenJailbreakBackup;
_Static_assert(sizeof(GsGoldHenJailbreakBackup) == 72, "GoldHEN jailbreak backup ABI");

typedef struct {
    int64_t raw;
    uint64_t carry;
} GsGoldHenCall;

__attribute__((naked)) static GsGoldHenCall gs_goldhen_call(uint64_t command, void *data)
{
    // Match the SDK's orbis_syscall(500, command, data) indirect syscall ABI.
    // SysV returns this two-word struct in RAX/RDX. Capture CF before any
    // arithmetic so diagnostic/version handling cannot erase kernel status.
    __asm__ volatile("mov %rsi, %rdx\nmov %rdi, %rsi\nmov $500, %edi\nmov %rcx, %r10\nmov $0, %eax\nsyscall\nsetc %dl\nmovzbl %dl, %edx\nret");
}

static int64_t gs_goldhen_call_result(GsGoldHenCall call)
{
    return call.carry ? (int64_t)(0 - (uint64_t)call.raw) : call.raw;
}

static int gs_goldhen_process_valid(const GsGoldHenProcess *info, GsGoldHenCall call)
{
    // The official plugin_loader uses a zeroed output and requires command 4
    // to succeed. A version return alone does not prove this API is usable.
    return !call.carry && call.raw == 0 && info->pid == getpid() &&
        info->name[0] && memchr(info->name, 0, sizeof(info->name)) != NULL;
}

static int gs_goldhen_is_shell(void)
{
    GsGoldHenProcess info;
    GsGoldHenCall version = gs_goldhen_call(0, NULL);
    if (version.raw != 0x100) {
        gs_log_write("resident", "GoldHEN version query rejected raw=0x%llX carry=%llu", (unsigned long long)version.raw, (unsigned long long)version.carry); return 0;
    }
    memset(&info, 0, sizeof(info));
    GsGoldHenCall identity = gs_goldhen_call(4, &info);
    if (!gs_goldhen_process_valid(&info, identity)) {
        gs_log_write("resident", "GoldHEN process identity query failed code=%lld carry=%llu expected_pid=%d actual_pid=%d", (long long)gs_goldhen_call_result(identity), (unsigned long long)identity.carry, getpid(), info.pid); return 0;
    }
    if (memcmp(info.name, "SceShellUI", sizeof("SceShellUI"))) gs_log_write("resident", "Worker host rejected process=%.40s", info.name);
    return !memcmp(info.name, "SceShellUI", sizeof("SceShellUI"));
}

typedef struct {
    const char *stage;
    int api_returned, load_called, load_returned, result;
    int64_t api_version, load_return;
    uint64_t request_result;
    GsGoldHenCall api_call;
    int process_called, process_valid;
    int64_t process_return;
    GsGoldHenCall load_call;
} GsGoldHenLoadTrace;

static void gs_goldhen_write_load_trace(const char *destination, const GsGoldHenLoadTrace *trace)
{
    gs_log_write("resident", "loader stage=%s api_returned=%d command0=0x%llX command6_called=%d command6_returned=%d command6_rc=%lld request_result=0x%llX result=%d command0_raw_hex=0x%llX command0_carry=%llu command4_called=%d command4_rc=%lld command4_valid=%d command6_raw_hex=0x%llX command6_carry=%llu",
        trace->stage, trace->api_returned, (unsigned long long)trace->api_version, trace->load_called, trace->load_returned,
        (long long)trace->load_return, (unsigned long long)trace->request_result, trace->result,
        (unsigned long long)trace->api_call.raw, (unsigned long long)trace->api_call.carry,
        trace->process_called, (long long)trace->process_return, trace->process_valid,
        (unsigned long long)trace->load_call.raw, (unsigned long long)trace->load_call.carry);
    if (!destination) return;
    char temporary[160];
    if (snprintf(temporary, sizeof(temporary), "%s.tmp", destination) >= (int)sizeof(temporary)) return;
    FILE *file = fopen(temporary, "wb");
    if (!file) return;
    int rc = fprintf(file, "format=1\nstage=%s\napi_returned=%d\ncommand0=%lld\ncommand0_hex=0x%016llX\ncommand6_called=%d\ncommand6_returned=%d\ncommand6_rc=%lld\nrequest_result=0x%016llX\nresult=%d\ncommand0_raw_hex=0x%016llX\ncommand0_carry=%llu\ncommand4_called=%d\ncommand4_rc=%lld\ncommand4_valid=%d\ncommand6_raw_hex=0x%016llX\ncommand6_carry=%llu\n",
        trace->stage, trace->api_returned, (long long)trace->api_version,
        (unsigned long long)trace->api_version, trace->load_called, trace->load_returned,
        (long long)trace->load_return, (unsigned long long)trace->request_result, trace->result,
        (unsigned long long)trace->api_call.raw, (unsigned long long)trace->api_call.carry,
        trace->process_called, (long long)trace->process_return, trace->process_valid,
        (unsigned long long)trace->load_call.raw, (unsigned long long)trace->load_call.carry);
    if (rc >= 0) rc = fflush(file);
    if (rc == 0) rc = fsync(fileno(file));
    if (fclose(file) != 0) rc = -1;
    if (rc == 0) rename(temporary, destination);
    else unlink(temporary);
}

static int gs_goldhen_load_shell_traced(const char *path, const char *diagnostic_path)
{
    GsGoldHenPrxLoad request;
    GsGoldHenLoadTrace trace = { .stage = "path-check", .result = -1, .request_result = UINT64_MAX };
    gs_goldhen_write_load_trace(diagnostic_path, &trace);
    const char *prefix = "/data/SSPI/resident/gs_resident_shell_";
    size_t length = path ? strlen(path) : 0;
    if (length <= strlen(prefix) + 4 || length >= sizeof(request.prx_path) ||
        strncmp(path, prefix, strlen(prefix)) || strcmp(path + length - 4, ".prx") ||
        strchr(path + strlen(prefix), '/') || strchr(path, '\\') || strstr(path, "..")) {
        trace.stage = "path-rejected"; goto done;
    }
    trace.stage = "api-query"; gs_goldhen_write_load_trace(diagnostic_path, &trace);
    trace.api_call = gs_goldhen_call(0, NULL);
    trace.api_version = gs_goldhen_call_result(trace.api_call); trace.api_returned = 1;
    if (trace.api_call.raw != 0x100) { trace.stage = "api-rejected"; trace.result = -2; goto done; }
    // A carry-set 0x100 was previously reduced to -256 and blocked here.
    // It is only a reason to query the read-only process API, never success.
    GsGoldHenProcess info;
    memset(&info, 0, sizeof(info));
    trace.stage = "process-query"; trace.process_called = 1;
    gs_goldhen_write_load_trace(diagnostic_path, &trace);
    GsGoldHenCall identity = gs_goldhen_call(4, &info);
    trace.process_return = gs_goldhen_call_result(identity);
    trace.process_valid = gs_goldhen_process_valid(&info, identity);
    if (!trace.process_valid) { trace.stage = "process-query-rejected"; trace.result = -2; goto done; }
    trace.stage = "file-check"; gs_goldhen_write_load_trace(diagnostic_path, &trace);
    if (access(path, R_OK)) { trace.stage = "file-unreadable"; trace.result = -3; goto done; }
    memset(&request, 0, sizeof(request));
    memcpy(request.process_name, "SceShellUI", sizeof("SceShellUI"));
    memcpy(request.prx_path, path, length + 1);
    for (int path_attempt = 0; path_attempt < 2; ++path_attempt) {
        request.result = UINT64_MAX;
        trace.stage = "load-command"; trace.load_called = 1;
        trace.load_returned = 0;
        gs_log_write("resident", "loader request process=SceShellUI path=%s attempt=%d", request.prx_path, path_attempt + 1);
        gs_goldhen_write_load_trace(diagnostic_path, &trace);
        trace.load_call = gs_goldhen_call(6, &request);
        trace.load_return = gs_goldhen_call_result(trace.load_call); trace.load_returned = 1;
        trace.request_result = request.result;
        // GoldHEN.c deliberately returns args.res, not sys_sdk_cmd's return.
        // Only a changed, valid output can acknowledge a load; transport status
        // remains diagnostic. A module result is not a ready-worker heartbeat.
        // Preserve zero-extended and sign-extended PS4 errors instead of replacing
        // every error with -4.
        // Our unchanged sentinel still cannot acknowledge a successful load.
        uint64_t high = request.result >> 32;
        int result_valid = high == 0 || (high == UINT32_MAX && ((uint32_t)request.result & 0x80000000u));
        int retry_alias = 0;
        if (request.result == UINT64_MAX || !result_valid) {
            trace.result = trace.load_return < 0 && (int64_t)(int32_t)trace.load_return == trace.load_return
                ? (int32_t)trace.load_return : -4;
            trace.stage = trace.load_call.carry || trace.load_call.raw != 0
                ? "load-command-rejected" : "load-result-rejected";
            /* The handler leaves the sentinel in place on its own early
             * failures; an alias attempt is cheap and the only way to cover
             * that path. */
            retry_alias = 1;
        } else {
            trace.result = (int32_t)(uint32_t)request.result;
            if (trace.load_call.carry || trace.load_call.raw != 0)
                gs_log_write("resident", "GoldHEN load transport differs from output raw=0x%llX carry=%llu module_result=%d; readiness requires heartbeat",
                    (unsigned long long)trace.load_call.raw, (unsigned long long)trace.load_call.carry, trace.result);
            trace.stage = trace.result >= 0 ? "load-returned-awaiting-heartbeat" : "load-result-rejected";
            retry_alias = trace.result == (int32_t)0x80020002u;
        }
        if (retry_alias && path_attempt == 0) {
            char shared_path[sizeof(request.prx_path)];
            if (gs_shared_module_alias(path, shared_path, sizeof(shared_path))) {
                trace.stage = "load-shared-path-retry";
                gs_goldhen_write_load_trace(diagnostic_path, &trace);
                gs_log_write("resident", "loader retrying shared path=%s after result=%d", shared_path, trace.result);
                memcpy(request.prx_path, shared_path, strlen(shared_path) + 1);
                continue;
            }
            gs_log_write("resident", "loader shared alias rejected path=%s", path);
        }
        break;
    }
done:
    gs_goldhen_write_load_trace(diagnostic_path, &trace);
    return trace.result;
}

static int gs_goldhen_load_shell(const char *path)
{ return gs_goldhen_load_shell_traced(path, NULL); }

/* GoldHEN command 7 ABI: char process_name[32], uint64_t prx_handle (offset
 * 32), uint64_t res (offset 40). The reference SDK's packed struct is 48
 * bytes; only the fields below are meaningful to the kernel handler. */
typedef struct {
    char process_name[32];
    uint64_t prx_handle;
    uint64_t res;
} __attribute__((packed)) GsGoldHenPrxUnload;

_Static_assert(sizeof(GsGoldHenPrxUnload) == 48, "GoldHEN PRX unloader ABI");

typedef struct {
    const char *stage;
    int command_called, command_returned, result;
    int64_t command_return;
    uint64_t request_result;
    GsGoldHenCall call;
} GsGoldHenUnloadTrace;

static void gs_goldhen_write_unload_trace(const char *destination, const GsGoldHenUnloadTrace *trace)
{
    gs_log_write("resident", "unloader stage=%s command7_called=%d command7_returned=%d command7_rc=0x%llX request_result=0x%llX result=%d command7_raw_hex=0x%llX command7_carry=%llu",
        trace->stage, trace->command_called, trace->command_returned, (unsigned long long)trace->command_return,
        (unsigned long long)trace->request_result, trace->result,
        (unsigned long long)trace->call.raw, (unsigned long long)trace->call.carry);
    if (!destination) return;
    char temporary[160];
    if (snprintf(temporary, sizeof(temporary), "%s.tmp", destination) >= (int)sizeof(temporary)) return;
    FILE *file = fopen(temporary, "wb");
    if (!file) return;
    int rc = fprintf(file, "format=1\nstage=%s\ncommand7_called=%d\ncommand7_returned=%d\ncommand7_rc=%lld\nrequest_result=0x%016llX\nresult=%d\ncommand7_raw_hex=0x%016llX\ncommand7_carry=%llu\n",
        trace->stage, trace->command_called, trace->command_returned,
        (long long)trace->command_return, (unsigned long long)trace->request_result, trace->result,
        (unsigned long long)trace->call.raw, (unsigned long long)trace->call.carry);
    if (rc >= 0) rc = fflush(file);
    if (rc == 0) rc = fsync(fileno(file));
    if (fclose(file) != 0) rc = -1;
    if (rc == 0) rename(temporary, destination);
    else unlink(temporary);
}

static int gs_goldhen_unload_prx_traced(const char *process_name, uint64_t handle, const char *diagnostic_path)
{
    GsGoldHenPrxUnload request;
    GsGoldHenUnloadTrace trace = { .stage = "unload-check", .result = -1, .request_result = UINT64_MAX };
    gs_goldhen_write_unload_trace(diagnostic_path, &trace);
    size_t length = process_name ? strlen(process_name) : 0;
    if (!length || length >= sizeof(request.process_name)) {
        trace.stage = "unload-process-rejected"; trace.result = -2; goto done;
    }
    memset(&request, 0, sizeof(request));
    memcpy(request.process_name, process_name, length + 1);
    request.prx_handle = handle;
    request.res = UINT64_MAX;
    trace.stage = "unload-command"; trace.command_called = 1; trace.result = -1;
    gs_log_write("resident", "unloader request process=%s handle=0x%llX rc=0x%llX",
        request.process_name, (unsigned long long)handle, (unsigned long long)request.res);
    gs_goldhen_write_unload_trace(diagnostic_path, &trace);
    trace.call = gs_goldhen_call(7, &request);
    trace.command_return = gs_goldhen_call_result(trace.call); trace.command_returned = 1;
    trace.request_result = request.res;
    // Same sentinel/result rules as command 6: a changed, valid output is the
    // only acknowledgement, zero-extended and sign-extended PS4 errors are
    // preserved, and transport status stays diagnostic.
    uint64_t high = request.res >> 32;
    int result_valid = high == 0 || (high == UINT32_MAX && ((uint32_t)request.res & 0x80000000u));
    if (request.res == UINT64_MAX || !result_valid) {
        trace.result = trace.command_return < 0 && (int64_t)(int32_t)trace.command_return == trace.command_return
            ? (int32_t)trace.command_return : -4;
        trace.stage = trace.call.carry || trace.call.raw != 0
            ? "unload-command-rejected" : "unload-result-rejected";
    } else {
        trace.result = (int32_t)(uint32_t)request.res;
        trace.stage = "unload-result";
        if (trace.call.carry || trace.call.raw != 0)
            gs_log_write("resident", "GoldHEN unload transport differs from output raw=0x%llX carry=%llu unload_result=%d",
                (unsigned long long)trace.call.raw, (unsigned long long)trace.call.carry, trace.result);
    }
done:
    gs_goldhen_write_unload_trace(diagnostic_path, &trace);
    return trace.result;
}
#endif
