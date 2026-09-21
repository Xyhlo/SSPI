#include <stddef.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <time.h>
#include <orbis/UserService.h>
#include <orbis/SystemService.h>
#include <orbis/Sysmodule.h>
#include <orbis/libkernel.h>
#include <stdio.h>
#include "main.h"
#include "sspi_log.h"
#ifndef RESIDENT_DAEMON
#include "cover_jpeg.h"
#endif

static int (*user_initialize)(void*);
static int (*user_terminate)(void);
static int (*user_initial)(int*);
static int (*user_foreground)(int*);
static int (*system_hide_splash)(void);
static int (*system_load_exec)(const char*, const char* const*);

static int boot_failure(const char* stage, int code)
{
    boot_stage(stage, code);
#ifndef RESIDENT_DAEMON
    OrbisNotificationRequest notice;
    memset(&notice, 0, sizeof(notice));
    notice.targetId = -1;
    snprintf(notice.message, sizeof(notice.message),
        "SSPI could not start (%s, 0x%08x). Startup details were saved locally.", stage, (unsigned int)code);
    sceKernelSendNotificationRequest(0, &notice, sizeof(notice), 0);
#endif
    return -1;
}

static int load_system_module(const char* name)
{
    char path[256];
    const char* sandbox = sceKernelGetFsSandboxRandomWord();
    int handle = -1;
    if (sandbox && *sandbox) {
        snprintf(path, sizeof(path), "/%s/common/lib/%s.sprx", sandbox, name);
        handle = sceKernelLoadStartModule(path, 0, NULL, 0, NULL, NULL);
        if (handle >= 0) return handle;
    }
    snprintf(path, sizeof(path), "/system/common/lib/%s.sprx", name);
    handle = sceKernelLoadStartModule(path, 0, NULL, 0, NULL, NULL);
    if (handle >= 0) return handle;
    snprintf(path, sizeof(path), "%s.sprx", name);
    handle = sceKernelLoadStartModule(path, 0, NULL, 0, NULL, NULL);
    if (handle >= 0) return handle;

    // A dependency can already be present even when the loader rejects another
    // load. Recover only an exact module name, never a similarly named library.
    OrbisKernelModule modules[256] = {0}; size_t count = 0;
    if (sceKernelGetModuleList(modules, sizeof(modules) / sizeof(modules[0]), &count) != 0)
        return handle;
    if (count > sizeof(modules) / sizeof(modules[0])) count = sizeof(modules) / sizeof(modules[0]);
    char prxName[256];
    snprintf(prxName, sizeof(prxName), "%s.prx", name);
    for (size_t i = 0; i < count; i++) {
        OrbisKernelModuleInfo info; memset(&info, 0, sizeof(info)); info.size = sizeof(info);
        if (sceKernelGetModuleInfo(modules[i], &info) != 0 ||
            !memchr(info.name, 0, sizeof(info.name))) continue;
        if (!strcmp(info.name, name) || !strcmp(info.name, path) || !strcmp(info.name, prxName)) {
            klogf("Using already loaded system module %s (0x%x)", name, (unsigned)modules[i]);
            return (int)modules[i];
        }
    }
    return handle;
}

static int load_mono_runtime(const char* path)
{
    // Preload the pinned runtime's DT_NEEDED imports so a dependency failure is
    // logged separately from a rejection of the Mono module itself.
    static const char* dependencies[] = {
        "libkernel", "libSceSsl", "libSceNet", "libSceSysmodule",
        "libSceRegMgr", "libSceLibcInternal"
    };
    struct stat runtime;
    if (!path || stat(path, &runtime) != 0 || !S_ISREG(runtime.st_mode) || runtime.st_size <= 0) {
        boot_stage("mono-runtime-file-unavailable", -1);
        return -1;
    }
    gs_log_write("startup", "mono runtime bytes=%llu packaged_sdk=0x06720001",
        (unsigned long long)runtime.st_size);
    for (size_t i = 0; i < sizeof(dependencies) / sizeof(dependencies[0]); i++) {
        int module = load_system_module(dependencies[i]);
        gs_log_write("startup", "mono dependency=%s code=0x%08x", dependencies[i], (unsigned)module);
        if (module < 0) {
            boot_stage("mono-dependency-unavailable", module);
        }
    }
    int startResult = 0;
    boot_stage("mono-module-load", 0);
    int module = sceKernelLoadStartModule(path, 0, NULL, 0, NULL, &startResult);
    gs_log_write("startup", "mono load_result=0x%08x start_result=0x%08x",
        (unsigned)module, (unsigned)startResult);
    return module;
}

static int require_export(int module, const char* name, void** pointer)
{
    *pointer = NULL;
    int rc = module < 0 ? module : sceKernelDlsym(module, name, pointer);
    if (rc != 0 || !*pointer) {
        boot_failure(name, rc ? rc : -1);
        return 0;
    }
    return 1;
}

static void log_package_build(void)
{
    char path[sizeof(baseDir) + 32];
    char build[16] = {0};
    snprintf(path, sizeof(path), "%s/build-id.txt", baseDir);
    FILE* file = fopen(path, "rb");
    if (!file) return;
    size_t length = fread(build, 1, sizeof(build), file);
    fclose(file);
    if (length != 12 && !(length == 13 && build[12] == '\n')) return;
    for (size_t i = 0; i < 12; i++)
        if (!strchr("0123456789abcdef", build[i]) || !build[i]) return;
    build[12] = 0;
    gs_log_write("startup", "package_build=%s", build);
}

#define REQUIRE_EXPORT(module, name) \
    do { if (!require_export(module, #name, (void**)&name)) return -1; } while (0)

#ifdef RESIDENT_DAEMON
#define RESIDENT_APP_ROOT "/system/vsh/app/SRCHD0001"
#define RESIDENT_VERSION "4.42"
#define DOTNET_UNIX_EPOCH_TICKS 621355968000000000ULL

static const char* resident_dirs[] = {
    "/data/SSPI/resident",
    "/user/data/SSPI/resident",
    0
};

static void ensure_resident_dir(const char* dir)
{
    if (!dir)
        return;
    if (strncmp(dir, "/user/data/", 11) == 0)
    {
        mkdir("/user", 0777);
        mkdir("/user/data", 0777);
        mkdir("/user/data/SSPI", 0777);
    }
    else
    {
        mkdir("/data", 0777);
        mkdir("/data/SSPI", 0777);
    }
    mkdir(dir, 0777);
}

static void write_resident_named(const char* name, const char* body, int append)
{
    int i;
    for (i = 0; resident_dirs[i]; i++)
    {
        char path[256];
        FILE* fp;
        ensure_resident_dir(resident_dirs[i]);
        snprintf(path, sizeof(path), "%s/%s", resident_dirs[i], name);
        fp = fopen(path, append ? "a" : "w");
        if (!fp)
            continue;
        fputs(body, fp);
        fclose(fp);
    }
}

static void write_resident_boot(const char* stage, int code)
{
    char line[512];
    snprintf(line, sizeof(line),
        "%s code=%d appRoot=%s baseDir=%s mainExe=%s exists=%d\n",
        stage ? stage : "?", code, appRoot, baseDir, mainExe, file_exists(mainExe));
    gs_log_write("startup", "daemon %s", line);
}

static void write_resident_heartbeat(void)
{
    char line[128];
    unsigned long long ticks = DOTNET_UNIX_EPOCH_TICKS +
        ((unsigned long long)time(0) * 10000000ULL);
    snprintf(line, sizeof(line), "%s\n%llu\n", RESIDENT_VERSION, ticks);
    write_resident_named("heartbeat.txt", line, 0);
}

static void* resident_heartbeat_thread(void* arg)
{
    (void)arg;
    for (;;)
    {
        write_resident_heartbeat();
        sceKernelUsleep(1000000);
    }
    return 0;
}

static void start_resident_heartbeat(void)
{
    OrbisPthread thread;
    write_resident_heartbeat();
    scePthreadCreate(&thread, 0, resident_heartbeat_thread, 0, "gs-hb");
}
#endif

const char* JailedBase = "/app0";

char* getBaseDirectory()
{
    if (isJailbroken())
        return baseDir;
    return JailedBase;
}

void* getMonoMethod(void* Image, char* Namespace, char* Class, char* Method) {	
    void* programClass = mono_class_from_name(Image, Namespace, Class);

    if (!programClass) {
        klogf("Failed to find the class %s.%s", Namespace, Class);
        return NULL;
    }	

    void* methodTarget = mono_class_get_method_from_name(programClass, Method, 0);

    if (!methodTarget) {
		methodTarget = mono_class_get_method_from_name(programClass, Method, 1);
		
		if (!methodTarget) {
			klogf("Failed to find the class %s.%s.%s() Method", Namespace, Class, Method);
			return NULL;
		}
    }
	
	return methodTarget;
}
void addInternalCall(char* symbol, void* function)
{
	if (!function)
	{
		klogf("Unresolved Symbol: %s", symbol);
		return;
	}
    mono_add_internal_call(symbol, function);	
}

void addInternalCalls(){
#ifndef RESIDENT_DAEMON
    addInternalCall("Orbis.NativeJpegDecoder::GetInfo", sspi_cover_jpeg_info);
    addInternalCall("Orbis.NativeJpegDecoder::DecodePixels", sspi_cover_jpeg_decode);
#endif
	klog("Adding Kernel internal calls...");
    addInternalCall("Orbis.Internals.Kernel::Log(void*)", klog);
    addInternalCall("Orbis.Internals.Kernel::malloc(int)", malloc);
    addInternalCall("Orbis.Internals.Kernel::free(void*)", free);
    addInternalCall("Orbis.Internals.Kernel::Jailbreak(long)", jailbreak);
    addInternalCall("Orbis.Internals.Kernel::JailbreakCred(long)", jailbreak);
    addInternalCall("Orbis.Internals.Kernel::Unjailbreak", unjailbreak);
    addInternalCall("Orbis.Internals.Kernel::IsJailbroken", isJailbroken);
    addInternalCall("Orbis.Internals.Kernel::LoadStartModule", hinted_dlopen);
    addInternalCall("Orbis.Internals.Kernel::GetModuleBase", get_module_base);
	addInternalCall("Orbis.Internals.Kernel::GetMethodPointer", mono_method_get_unmanaged_thunk);
    klog("Adding IO internal calls...");
    addInternalCall("Orbis.Internals.IO::GetBaseDirectory", getBaseDirectory);
    klog("Adding User Service internal calls...");
    addInternalCall("Orbis.Internals.UserService::Initialize", user_initialize);
    addInternalCall("Orbis.Internals.UserService::Terminate", user_terminate);
    addInternalCall("Orbis.Internals.UserService::GetInitialUser", user_initial);
    addInternalCall("Orbis.Internals.UserService::GetForegroundUser", user_foreground);
    addInternalCall("Orbis.Internals.UserService::HideSplashScreen", system_hide_splash);
    addInternalCall("Orbis.Internals.UserService::NativeLoadExec", system_load_exec);
    klog("Internal calls added.");
}

void MonoLogCallback(const char *log_domain, const char *log_level, const char *message, int fatal, void *user_data){
	klogf("[%s] %s", log_level, message);
}


void* startMono()
{

#ifndef DEBUG
    setenv("MONO_DISABLE_SHARED_AREA", "1", 1);
#endif
    boot_stage("mono-start", 0);

#ifdef DEBUG
    klog("Initializing Debugger at port 2222...");

    mono_debugger_agent_parse_options("address=0.0.0.0:2222,transport=dt_socket,server=y");
    mono_debug_init(1);

    const char* options[] = { "--soft-breakpoints" };

    mono_jit_parse_options(sizeof(options) / sizeof(char*), (char**)options);
#endif
	
	mono_trace_set_log_handler(MonoLogCallback, NULL);
	
    klog("Starting Mono...");

    void* domain = mono_get_root_domain();

    if (!domain) {
        mono_set_dirs(baseDir, baseCon);
        domain = mono_jit_init("main");
    }

    klog("Enabling mono_dl_fallback...");
	mono_dl_fallback_register(MonoDlLoad, MonoDlSymbol, MonoDlClose, NULL);

    if (!domain) {
        boot_failure("mono-domain", -1);
        return 0;
    }

    // The embedded entry uses runtime_invoke rather than exec_main, so Mono
    // does not discover the executable's assembly-binding configuration.
    char mainConfig[sizeof(mainExe) + sizeof(".config")];
    int configLength = snprintf(mainConfig, sizeof(mainConfig), "%s.config", mainExe);
    if (configLength <= 0 || configLength >= sizeof(mainConfig) || !mono_domain_set_config) {
        boot_failure("mono-application-config", -1);
        return 0;
    }
#ifndef RESIDENT_DAEMON
    if (!file_exists(mainConfig)) {
        boot_failure("mono-application-config", -1);
        return 0;
    }
#endif
    mono_domain_set_config(domain, baseDir, mainConfig);
    boot_stage("mono-application-config-ready", 0);
	
	klog("Mono domain Initialized");

#ifdef DEBUG
    mono_debug_domain_create(domain);
#endif

    return domain;
}

void runMain()
{
    void* rootDomain = mono_get_root_domain();
    if (!rootDomain) {
        klog("get_root_domain failed");
        return;
    }
	
	addInternalCalls();

    void* mainImage = hookLoadSprxAssembly(mainExe, 0, 0, 0);
    if (!mainImage) { boot_failure("managed-image", -1); return; }
    void* mainAssembly = mono_assembly_load_from_full(mainImage, mainExe, 0, 0);
    
    if (!mainAssembly) {
        boot_failure("managed-assembly", -1);
        return;
    }

    klogf("Main Assembly: %x", mainAssembly);
	
	void* methodMain = getMonoMethod(mainImage, "Orbis", "Program", "Main");

	if (!methodMain) {
        boot_failure("managed-entry-point", -1);
		return;
    }

    sceSysmoduleLoadModule(ORBIS_SYSMODULE_FREETYPE_OL);

    klog("Starting program...");
	
    char* argv[] = { 0 };
    void* exception = NULL;
    boot_stage("managed-invoke", 0);
    mono_runtime_invoke(methodMain, 0, (void**)argv, &exception);
    if (exception) boot_failure("managed-unhandled", -1);
    else boot_stage("managed-return", 0);
}

void run()
{
    void* rootDomain = startMono();
    if (rootDomain == 0)
        return;
	
    runMain();
    if (system_load_exec) system_load_exec("exit", NULL);
}

int main()
{
	klog("Main Begin");
    boot_stage("native-entry", 0);
#ifdef RESIDENT_DAEMON
    write_resident_boot("main-begin", 0);
    write_resident_heartbeat();
#endif
	
    int libKernel = load_system_module("libkernel");
    if (libKernel < 0) return boot_failure("module-kernel", libKernel);

    OrbisKernelSwVersion systemVersion;
    memset(&systemVersion, 0, sizeof(systemVersion));
    systemVersion.Size = sizeof(systemVersion);
    int versionResult = sceKernelGetSystemSwVersion(&systemVersion);
    gs_log_write("startup", "firmware=0x%08x query_result=0x%08x",
        (unsigned)systemVersion.Version, (unsigned)versionResult);
	
    int jailbreakResult = jailbreak(0);
    boot_stage("jailbreak-result", jailbreakResult);
    if (jailbreakResult < 0) return boot_failure("jailbreak-capability", jailbreakResult);

#ifdef RESIDENT_DAEMON
    write_resident_boot("jailbreak", isJailbroken());
    start_resident_heartbeat();
    {
        static const char* resident_app_roots[] = {
            RESIDENT_APP_ROOT,
            "/data/SSPI/daemon",
            "/user/data/SSPI/daemon",
            0
        };
        int picked = 0;
        int i;
        for (i = 0; resident_app_roots[i]; i++)
        {
            char exe[256];
            snprintf(exe, sizeof(exe), "%s/main.exe", resident_app_roots[i]);
            if (file_exists(exe))
            {
                sprintf(appRoot, "%s", resident_app_roots[i]);
                sprintf(baseDir, "%s", resident_app_roots[i]);
                sprintf(mainExe, "%s/main.exe", baseDir);
                sprintf(baseCon, "%s/mono", baseDir);
                write_resident_boot(resident_app_roots[i], 1);
                picked = 1;
                break;
            }
        }
        if (!picked)
        {
            findAppMount(&appRoot);
            sprintf(&baseDir, "%s/app0", appRoot);
            sprintf(&mainExe, "%s/main.exe", baseDir);
            sprintf(&baseCon, "%s/mono", baseDir);
            write_resident_boot("path-mount", 0);
        }
    }
#else
    findAppMount(&appRoot);

    sprintf(&baseDir, "%s/app0", appRoot);
    sprintf(&mainExe, "%s/main.exe", baseDir);
    sprintf(&baseCon, "%s/mono", baseDir);
#endif
	
    if (!file_exists(mainExe)) return boot_failure("application-mount", -1);
    boot_stage("application-mount-ready", 0);
    log_package_build();
    char pkgLib[0x100] = "\x0";
    sprintf(&pkgLib, "%s/sce_module/libmonosgen-2.0.prx", baseDir);
	
	int libSceIpmi = load_system_module("libSceIpmi");
	int libSceNet = load_system_module("libSceNet");
	int libSceSystemService = load_system_module("libSceSystemService");
    int libSceUserService = load_system_module("libSceUserService");
	int mono_framework = load_mono_runtime(pkgLib);

#ifdef RESIDENT_DAEMON
    if (!(libSceSystemService & 0x80000000))
    {
        int (*registerDaemon)(void) = 0;
        int registerResult = -1;
        sceKernelDlsym(libSceSystemService, "sceSystemServiceRegisterDaemon", (void**)&registerDaemon);
        if (registerDaemon)
            registerResult = registerDaemon();
        write_resident_boot("register-daemon", registerResult);
    }
#endif

    if (libSceIpmi < 0) boot_stage("module-ipmi-unavailable", libSceIpmi);
    if (libSceNet < 0) boot_stage("module-net-unavailable", libSceNet);
    if (libSceSystemService < 0) boot_stage("module-system-service-unavailable", libSceSystemService);
    if (libSceUserService < 0) boot_stage("module-user-service-unavailable", libSceUserService);
    if (mono_framework < 0) return boot_failure("module-mono", mono_framework);
    boot_stage("mono-module-ready", 0);

    sceKernelDlsym(mono_framework, "mono_set_dirs", (void**)&mono_set_dirs);
    sceKernelDlsym(mono_framework, "mono_jit_init", (void**)&mono_jit_init);
    sceKernelDlsym(mono_framework, "mono_init_from_assembly", (void**)&mono_init_from_assembly);
    sceKernelDlsym(mono_framework, "mono_get_root_domain", (void**)&mono_get_root_domain);
    sceKernelDlsym(mono_framework, "mono_domain_assembly_open", (void**)&mono_domain_assembly_open);
    sceKernelDlsym(mono_framework, "mono_class_from_name", (void**)&mono_class_from_name);
    sceKernelDlsym(mono_framework, "mono_class_get_method_from_name", (void**)&mono_class_get_method_from_name);
    sceKernelDlsym(mono_framework, "mono_runtime_invoke", (void**)&mono_runtime_invoke);
    sceKernelDlsym(mono_framework, "mono_method_get_unmanaged_thunk", (void**)&mono_method_get_unmanaged_thunk);
    sceKernelDlsym(mono_framework, "mono_jit_cleanup", (void**)&mono_jit_cleanup);
    sceKernelDlsym(mono_framework, "mono_image_open_from_data_with_name", (void**)&mono_image_open_from_data_with_name);
    sceKernelDlsym(mono_framework, "mono_thread_attach", (void**)&mono_thread_attach);
    sceKernelDlsym(mono_framework, "mono_assembly_get_image", (void**)&mono_assembly_get_image);
    sceKernelDlsym(mono_framework, "mono_assembly_load_from_full", (void**)&mono_assembly_load_from_full);
    sceKernelDlsym(mono_framework, "mono_add_internal_call", (void**)&mono_add_internal_call);
    sceKernelDlsym(mono_framework, "mono_debugger_agent_parse_options", (void**)&mono_debugger_agent_parse_options);
    sceKernelDlsym(mono_framework, "mono_debug_init", (void**)&mono_debug_init);
    sceKernelDlsym(mono_framework, "mono_debug_domain_create", (void**)&mono_debug_domain_create);
    sceKernelDlsym(mono_framework, "mono_jit_parse_options", (void**)&mono_jit_parse_options);
    sceKernelDlsym(mono_framework, "mono_debug_open_image_from_memory", (void**)&mono_debug_open_image_from_memory);
    sceKernelDlsym(mono_framework, "mono_dl_fallback_register", (void**)&mono_dl_fallback_register);
    sceKernelDlsym(mono_framework, "mono_dl_fallback_unregister", (void**)&mono_dl_fallback_unregister);
    sceKernelDlsym(mono_framework, "mono_trace_set_log_handler", (void**)&mono_trace_set_log_handler);
    sceKernelDlsym(mono_framework, "mono_domain_create", (void**)&mono_domain_create);

    sceKernelDlsym(libKernel, "sceKernelJitCreateSharedMemory", (void**)&JitCreateSharedMemory);
    sceKernelDlsym(libKernel, "sceKernelJitCreateAliasOfSharedMemory", (void**)&JitCreateAliasOfSharedMemory);
    sceKernelDlsym(libKernel, "sceKernelJitMapSharedMemory", (void**)&JitMapSharedMemory);

    sceKernelDlsym(libKernel, "sceKernelLoadStartModule", (void**)&sceKernelLoadStartModule_sys);
	
    sceKernelDlsym(libKernel, "sceKernelLoadStartModuleInternalForMono", (void**)&sceKernelLoadStartModuleInternalForMono);
	
    // Store dlsym results in pointer variables, never in the addresses of C
    // function imports (that would overwrite executable code).
    if (libSceUserService >= 0) {
        sceKernelDlsym(libSceUserService, "sceUserServiceInitialize", (void**)&user_initialize);
        sceKernelDlsym(libSceUserService, "sceUserServiceTerminate", (void**)&user_terminate);
        sceKernelDlsym(libSceUserService, "sceUserServiceGetInitialUser", (void**)&user_initial);
        sceKernelDlsym(libSceUserService, "sceUserServiceGetForegroundUser", (void**)&user_foreground);
    }
    if (libSceSystemService >= 0) {
        sceKernelDlsym(libSceSystemService, "sceSystemServiceHideSplashScreen", (void**)&system_hide_splash);
        sceKernelDlsym(libSceSystemService, "sceSystemServiceLoadExec", (void**)&system_load_exec);
    }
	
    REQUIRE_EXPORT(mono_framework, mono_set_dirs);
    REQUIRE_EXPORT(mono_framework, mono_jit_init);
    REQUIRE_EXPORT(mono_framework, mono_get_root_domain);
    REQUIRE_EXPORT(mono_framework, mono_domain_set_config);
    REQUIRE_EXPORT(mono_framework, mono_class_from_name);
    REQUIRE_EXPORT(mono_framework, mono_class_get_method_from_name);
    REQUIRE_EXPORT(mono_framework, mono_runtime_invoke);
    REQUIRE_EXPORT(mono_framework, mono_image_open_from_data_with_name);
    REQUIRE_EXPORT(mono_framework, mono_assembly_load_from_full);
    REQUIRE_EXPORT(mono_framework, mono_add_internal_call);
    REQUIRE_EXPORT(mono_framework, mono_dl_fallback_register);
    REQUIRE_EXPORT(mono_framework, mono_trace_set_log_handler);
    boot_stage("exports-ready", 0);
    if (!InstallHooks()) return boot_failure("mono-hook-mismatch", -1);
    boot_stage("mono-hook-ready", 0);

    run();

    return 0;
}
