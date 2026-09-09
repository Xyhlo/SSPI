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

#ifdef RESIDENT_DAEMON
#define RESIDENT_APP_ROOT "/system/vsh/app/SRCHD0001"
#define RESIDENT_VERSION "4.42"
#define DOTNET_UNIX_EPOCH_TICKS 621355968000000000ULL

static const char* resident_dirs[] = {
    "/data/GameSearch/resident",
    "/user/data/GameSearch/resident",
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
        mkdir("/user/data/GameSearch", 0777);
    }
    else
    {
        mkdir("/data", 0777);
        mkdir("/data/GameSearch", 0777);
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
    write_resident_named("boot.txt", line, 1);
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
	}
    mono_add_internal_call(symbol, function);	
}

void addInternalCalls(){
	klog("Adding Kernel internal calls...");
    addInternalCall("Orbis.Internals.Kernel::Log(void*)", klog);
    addInternalCall("Orbis.Internals.Kernel::malloc(int)", malloc);
    addInternalCall("Orbis.Internals.Kernel::free(void*)", free);
    addInternalCall("Orbis.Internals.Kernel::Jailbreak(long)", jailbreak);
    addInternalCall("Orbis.Internals.Kernel::Unjailbreak", unjailbreak);
    addInternalCall("Orbis.Internals.Kernel::IsJailbroken", isJailbroken);
    addInternalCall("Orbis.Internals.Kernel::LoadStartModule", hinted_dlopen);
    addInternalCall("Orbis.Internals.Kernel::GetModuleBase", get_module_base);
	addInternalCall("Orbis.Internals.Kernel::GetMethodPointer", mono_method_get_unmanaged_thunk);
    klog("Adding IO internal calls...");
    addInternalCall("Orbis.Internals.IO::GetBaseDirectory", getBaseDirectory);
    klog("Adding User Service internal calls...");
    addInternalCall("Orbis.Internals.UserService::Initialize", sceUserServiceInitialize);
    addInternalCall("Orbis.Internals.UserService::Terminate", sceUserServiceTerminate);
    addInternalCall("Orbis.Internals.UserService::GetInitialUser", sceUserServiceGetInitialUser);
    addInternalCall("Orbis.Internals.UserService::GetForegroundUser", sceUserServiceGetForegroundUser);
    addInternalCall("Orbis.Internals.UserService::HideSplashScreen", sceSystemServiceHideSplashScreen);
    addInternalCall("Orbis.Internals.UserService::NativeLoadExec", sceSystemServiceLoadExec);
    klog("Internal calls added.");
}

void MonoLogCallback(const char *log_domain, const char *log_level, const char *message, int fatal, void *user_data){
	klogf("[%s] %s", log_level, message);
}


void* startMono()
{

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

	if (!domain) {
		domain = mono_domain_create();
	}
	
    klog("Enabling mono_dl_fallback...");
	mono_dl_fallback_register(MonoDlLoad, MonoDlSymbol, MonoDlClose, NULL);

    if (!domain) {
        klog("Failed to init the mono domain");
        return 0;
    }
	
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
    void* mainAssembly = mono_assembly_load_from_full(mainImage, mainExe, 0, 0);
    
    if (!mainAssembly) {
        klog("Failed to get the main assembly");
        return;
    }

    klogf("Main Assembly: %x", mainAssembly);
	
	void* methodMain = getMonoMethod(mainImage, "Orbis", "Program", "Main");

	if (!methodMain)
		return;

    sceSysmoduleLoadModule(ORBIS_SYSMODULE_FREETYPE_OL);

    klog("Starting program...");
	
    char* argv[] = { 0 };
    mono_runtime_invoke(methodMain, 0, argv, 0);
}

void run()
{
    void* rootDomain = startMono();
    if (rootDomain == 0)
        return;
	
    runMain();
    sceSystemServiceLoadExec("exit", NULL);
}

int main()
{
	klog("Main Begin");
#ifdef RESIDENT_DAEMON
    write_resident_boot("main-begin", 0);
    write_resident_heartbeat();
#endif
	
	char syslib[0x100] = "\x0";
    int libKernel;
#ifdef RESIDENT_DAEMON
	sprintf(&syslib, "/system/common/lib/libkernel.sprx");
    libKernel = sceKernelLoadStartModule(syslib, 0, NULL, 0, 0, 0);
    if (libKernel & 0x80000000) {
        char* sandboxWord = sceKernelGetFsSandboxRandomWord();
        sprintf(&syslib, "/%s/common/lib/libkernel.sprx", sandboxWord);
        libKernel = sceKernelLoadStartModule(syslib, 0, NULL, 0, 0, 0);
    }
#else
	char* sandboxWord = sceKernelGetFsSandboxRandomWord();
	sprintf(&syslib, "/%s/common/lib/libkernel.sprx", sandboxWord);
    libKernel = sceKernelLoadStartModule(syslib, 0, NULL, 0, 0, 0);
#endif
	
    if (libKernel & 0x80000000) {
        klog("Failed o Load the libKernel");
        return -1;
    }
	
    jailbreak(0);

#ifdef RESIDENT_DAEMON
    write_resident_boot("jailbreak", isJailbroken());
    start_resident_heartbeat();
    {
        static const char* resident_app_roots[] = {
            RESIDENT_APP_ROOT,
            "/data/GameSearch/daemon",
            "/user/data/GameSearch/daemon",
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
	
    char pkgLib[0x100] = "\x0";
    sprintf(&pkgLib, "%s/sce_module/libmonosgen-2.0.prx", baseDir);
	
	int libSceIpmi = sceKernelLoadStartModule("/system/common/lib/libSceIpmi.sprx", 0, NULL, 0, 0, 0);
	int libSceNet = sceKernelLoadStartModule("/system/common/lib/libSceNet.sprx", 0, NULL, 0, 0, 0);
	int libSceSystemService = sceKernelLoadStartModule("/system/common/lib/libSceSystemService.sprx", 0, NULL, 0, 0, 0);
    int libSceUserService = sceKernelLoadStartModule("/system/common/lib/libSceUserService.sprx", 0, NULL, 0, 0, 0);
	int mono_framework = sceKernelLoadStartModule(pkgLib, 0, NULL, 0, 0, 0);

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

	if (libSceIpmi & 0x80000000){
        klog("Failed o Load the libSceNet");
        return -1;		
	}
	if (libSceNet & 0x80000000){
        klog("Failed o Load the libSceNet");
        return -1;		
	}
	if (libSceSystemService & 0x80000000){
        klog("Failed o Load the libSceSystemService");
        return -1;		
	}
	if (libSceUserService & 0x80000000){
        klog("Failed o Load the libSceUserService");
        return -1;		
	}
    if (mono_framework & 0x80000000) {
        klog("Failed o Load the Mono");
        return -1;
    }	

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
    sceKernelDlsym(libKernel, "sceKernelJitMapSharedMemory", (void**)&JitCreateAliasOfSharedMemory);

    sceKernelDlsym(libKernel, "sceKernelLoadStartModule", (void**)&sceKernelLoadStartModule_sys);
	
    sceKernelDlsym(libKernel, "sceKernelLoadStartModuleInternalForMono", (void**)&sceKernelLoadStartModuleInternalForMono);
	
    sceKernelDlsym(libSceUserService, "sceUserServiceInitialize", (void**)&sceUserServiceInitialize);
    sceKernelDlsym(libSceUserService, "sceUserServiceTerminate", (void**)&sceUserServiceTerminate);
    sceKernelDlsym(libSceUserService, "sceUserServiceGetInitialUser", (void**)&sceUserServiceGetInitialUser);
    sceKernelDlsym(libSceUserService, "sceUserServiceGetForegroundUser", (void**)&sceUserServiceGetForegroundUser);
    sceKernelDlsym(libSceUserService, "sceSystemServiceHideSplashScreen", (void**)&sceSystemServiceHideSplashScreen);
    sceKernelDlsym(libSceUserService, "sceSystemServiceLoadExec", (void**)&sceSystemServiceLoadExec);
	
    InstallHooks();

    run();

    return 0;
}
