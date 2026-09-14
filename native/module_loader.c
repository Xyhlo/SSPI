#include <orbis/libkernel.h>
#include <errno.h>
#include <stdint.h>
#include <sys/stat.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include "jailbreak_man.h"
#include "module.h"
#include "mono.h"
#include "io.h"

uint8_t JumpInstructions[] = {
    0xFF, 0x25, 0x00, 0x00, 0x00, 0x00, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, 0x90, // jmp QWORD PTR[Address]
};

//stolen from https://github.com/OSM-Made/Mono-Test/blob/af84e1dec5f02612bfc3f4634a4cc8f2474e5a1b/MonoTest/Detour.cpp
int WriteJump(void* Address, void* Destination, char* OriInstructions)
{
    if (!Address || !Destination) return 0;
    //Write the address of our hook to the instruction.
    *(uint64_t*)(JumpInstructions + 6) = (uint64_t)Destination;

    uintptr_t page = (uintptr_t)Address & ~(uintptr_t)(PAGE_SIZE - 1);
    size_t length = (((uintptr_t)Address + sizeof(JumpInstructions) + PAGE_SIZE - 1) & ~(uintptr_t)(PAGE_SIZE - 1)) - page;
    if (sceKernelMprotect((void*)page, length, PROT_READ | PROT_WRITE | PROT_EXEC) != 0)
        return 0;

    if (OriInstructions) {
        memcpy(OriInstructions, Address, sizeof(JumpInstructions));
    }

    memcpy(Address, JumpInstructions, sizeof(JumpInstructions));
    return memcmp(Address, JumpInstructions, sizeof(JumpInstructions)) == 0;
}

void remove_extension(const char* path, char* new_path)
{
    int len = strlen(path);
    strcpy(new_path, path);
    for (int i = len - 1; i > 0; i--) {
        if (path[i] == '.') {
            new_path[i] = 0;
            return;
        }
    }
}

char* extract_file_name(char* path)
{
    int len = strlen(path);

    for (int i = len - 1; i > 0; i--) {
        if (path[i] == '\\' || path[i] == '/') {
            return path + i + 1;
        }
    }

    return path;
}

char* extract_extension(char* path)
{
    int len = strlen(path);

    for (int i = len - 1; i > 0; i--) {
        if (path[i] == '.') {
            return path + i;
        }
    }

    return 0;
}

char* ApplyRemap(char* AssemblyName){
    char* fname = extract_file_name(AssemblyName);
	
	const char* from[] = { 
		"System.Globalization.dll",
		"System.Globalization.exe",
    };
	const char* to[] = {
		"mscorlib.dll",
		"mscorlib.dll",
    };
	
	for (int i = 0; i < countof(from); i++) {
		if (strcmp(from[i], fname) == 0){
			LOGF("Remapping Assembly: \"%s\" to \"%s\"", from[i], to[i]);
			return to[i];
		}
	}	
	return AssemblyName;
}

void* hookLoadSprxAssembly(const char* AssemblyName, int* OpenStatus, int UnkBool, int RefOnly)
{
    // MonoImageOpenStatus: ERRNO=1, IMAGE_INVALID=3. Every failure must set it;
    // leaving the caller's previous OK value describes a null image as success.
    if (OpenStatus) *OpenStatus = 1;
    if (!AssemblyName || !*AssemblyName || strlen(AssemblyName) >= 0x280) {
        if (OpenStatus) *OpenStatus = 3;
        return 0;
    }
    LOGF("Loading Assembly: %s", AssemblyName);
	
	int IsGAC = strstr(AssemblyName, "mono/gac/") != NULL;
	int IsAOT = strstr(AssemblyName, "mono/aot-cache/") != NULL;
	
	AssemblyName = ApplyRemap(AssemblyName);

    char* finalPath = AssemblyName;
    char hintPath[0x300] = "\x0";

    FILE* fp = fopen(AssemblyName, "rb");
    if (fp == 0) {
        LOG("Error opening file");
		
		if (IsGAC || IsAOT) {
			LOG("Skipping GAC Hint");
			return 0;
		}

        char fnameNoExt[0x300] = "\x0";
        char* fname = extract_file_name(AssemblyName);
        char* extension = extract_extension(fname);
        remove_extension(fname, fnameNoExt);
		
		//when the mono finds for the assembly as .exe this allow find as .dll as well
		int ValidExt = extension != NULL && (strcmp(extension, ".exe") == 0 || strcmp(extension, ".dll") == 0);

        const char* hints[] = { 
            "%s/mono/4.5/%s",
            "%s/mono/4.0/%s",
            "%s/mono/3.5/%s",
            "%s/mono/3.0/%s",
            "%s/mono/2.0/%s",
            "%s/mono/1.0/%s",
            "%s/mono/%s",
            "%s/%s"
        };
		
        const char* hintsExt[] = { 
            "%s/mono/4.5/%s.dll",
            "%s/mono/4.0/%s.dll",
            "%s/mono/3.5/%s.dll",
            "%s/mono/3.0/%s.dll",
            "%s/mono/2.0/%s.dll",
            "%s/mono/1.0/%s.dll",
            "%s/mono/%s.dll",
            "%s/%s.dll"
        };

        for (int i = 0; i < countof(hints); i++) {
            snprintf(hintPath, sizeof(hintPath), hints[i], "/app0", fname);
            fp = fopen(hintPath, "rb");
            if (fp != 0)
                break;

            snprintf(hintPath, sizeof(hintPath), hints[i], baseDir, fname);
            fp = fopen(hintPath, "rb");
            if (fp != 0)
                break;
			
			if (ValidExt){
				snprintf(hintPath, sizeof(hintPath), hintsExt[i], "/app0", fnameNoExt);
				fp = fopen(hintPath, "rb");
				if (fp != 0)
					break;

				snprintf(hintPath, sizeof(hintPath), hintsExt[i], baseDir, fnameNoExt);
				fp = fopen(hintPath, "rb");
				if (fp != 0)
					break;
			}
        }

        if (fp == 0) {
            LOG("No valid hints");
            return 0;
        }

        finalPath = hintPath;
        LOGF("Hint path matched: %s", hintPath);
    }
		
    if (fseek(fp, 0, SEEK_END) != 0) { fclose(fp); return 0; }
    long int size = ftell(fp);
    if (size < 0 || fseek(fp, 0, SEEK_SET) != 0) { fclose(fp); return 0; }

    if (size == 0 || (unsigned long)size > 128UL * 1024 * 1024) {
        if (OpenStatus) *OpenStatus = 3;
        fclose(fp); return 0;
    }
    char* data = malloc(size);
    if (!data) { fclose(fp); return 0; }
    size_t readed = fread(data, 1, (size_t)size, fp);

    fclose(fp);

    if (readed != size) {
        LOG("Error reading the file");
        free(data);
        return 0;
    }
	
    int status = 3;
    void* Image = mono_image_open_from_data_with_name(data, size, 0, &status, RefOnly, AssemblyName);
    if (!Image) { free(data); if (!status) status = 3; }

#ifdef DEBUG
    if (Image)
    {
        char* noExtPath[0x300];
        char* pdbPath[0x300];

        remove_extension(finalPath, noExtPath);
        sprintf(pdbPath, "%s.pdb", noExtPath);

        fp = fopen(pdbPath, "r");

        if (fp) {
            fseek(fp, 0, SEEK_END);
            size = ftell(fp);
            fseek(fp, 0, SEEK_SET);

            char* pdb = malloc(size);
            int readed = fread(pdb, 1, size, fp);
            fclose(fp);

            mono_debug_open_image_from_memory(Image, pdb, size);

            LOGF("Debug symbols loaded: %s", pdbPath);
        }
    }
#endif

    if (OpenStatus != 0)* OpenStatus = status;
	
	LOGF("Assembly Image: %x", Image);
	
    return Image;
}

void* hinted_dlopen(char* name) {
	if (!name || !*name || strlen(name) >= 0x280) return NULL;
	char altPath[0x300];
	char tmp[0x300];
	char hintPath[0x300] = "\x0";
	char rndWordRoot[0x300] = "\x0";
    remove_extension(name, altPath);//sample.prx

    char* fname = extract_file_name(altPath);//sample.prx
    char* orifname = extract_file_name(name);//sample.prx.sprx
    LOGF("fname: %s; orifname: %s", fname, orifname);
	
	int hModule = sceKernelLoadStartModule(name, 0, NULL, 0, 0, 0);	
	if (!(hModule & 0x80000000)) {
		LOGF("hModule: %x; %s", hModule, name);
        return (void*)(intptr_t)hModule;
    }
	
	hModule = sceKernelLoadStartModule(altPath, 0, NULL, 0, 0, 0);	
	if (!(hModule & 0x80000000)) {
		LOGF("hModule: %x; %s", hModule, altPath);
        return (void*)(intptr_t)hModule;
    }
	
	hModule = sceKernelLoadStartModule(orifname, 0, NULL, 0, 0, 0);	
	if (!(hModule & 0x80000000)) {
		LOGF("hModule: %x; %s", hModule, orifname);
        return (void*)(intptr_t)hModule;
    }	
	
	char* rootDir = "/app0";	
	
	if (isJailbroken()){
#ifdef RESIDENT_DAEMON
		rootDir = appRoot;
#else
		snprintf(tmp, sizeof(tmp), "%s/app0", appRoot);
		rootDir = tmp;
#endif
	}

	char* hints[] = { 
		"%s/sce_module/%s",
		"%s/sce_module/%s.sprx",
		"%s/%s",
		"%s/%s.sprx",
		"%s/common/lib/%s",
		"%s/common/lib/%s.sprx",
		"%s/system/common/lib/%s",
		"%s/system/common/lib/%s.sprx",
	};	
	
    const char* sandbox = sceKernelGetFsSandboxRandomWord();
    if (sandbox && *sandbox) snprintf(rndWordRoot, sizeof(rndWordRoot), "/%s", sandbox);
	
	char* roots[4];
	roots[0] = rootDir;
	roots[1] = appRoot;
	roots[2] = rndWordRoot;
	roots[3] = "";
	
	char* names[2];
	
	names[0] = fname;
	names[1] = orifname;
	
	for (int x = 0; x < countof(roots); x++){
		char* root = roots[x];
		for (int y = 0; y < countof(names); y++){
			for (int i = 0; i < countof(hints); i++){
				snprintf(hintPath, sizeof(hintPath), hints[i], root, names[y]);
				LOGF("Trying hint: %s", hintPath);
				hModule = sceKernelLoadStartModule(hintPath, 0, NULL, 0, 0, 0);	
				if (!(hModule & 0x80000000)) {
					LOGF("hModule: %x; %s", hModule, hintPath);
					return (void*)(intptr_t)hModule;
				}
			}
		}
	}
		
	LOGF("Module Not Found: %s", name);
	return NULL;
}

void* MonoDlLoad(const char *name, int flags, char **err, void *user_data) {
    LOGF("MonoDlFallbackLoad: %s", name);//sample.prx.sprx
	return hinted_dlopen(name);
}

void* MonoDlSymbol(void *handle, const char *name, char **err, void *user_data){
	LOGF("MonoDlFallbackSymbol: %s", name);
	(void)err; (void)user_data;
	if (!name || !*name || (intptr_t)handle < 0) return NULL;
	void* result = NULL;
	int rst = sceKernelDlsym((int)(intptr_t)handle, name, &result);
	if (rst){
		LOGF("dlsym fail: 0x%X", rst);
		return NULL;
	}
	if (!result) LOGF("dlsym null symbol: %s", name);
	return result;
}

void* MonoDlClose(void *handle, void *user_data) {
	int status = 0;
	(void)user_data;
	return (void*)(intptr_t)sceKernelStopUnloadModule((int)(intptr_t)handle, 0, NULL, 0, NULL, &status);
}

int InstallHooks()
{
    LOG("Installing hooks...");

    U64 MonoAddr = 0;
    U64 MonoSize = 0;
    get_module_base("libmonosgen-2.0.prx", &MonoAddr, &MonoSize);

    if (MonoAddr == 0) {
        LOG("Refusing hook: libmonosgen-2.0.sprx address not found");
        return 0;
    }

    //MUST UPDATE
    //6.72: 0x18CC60
    //Hint: one of the few functions that references the string ".sprx"
    static const U64 kHookOffset = 0x18CC60;
    static const U64 kJumpSize = sizeof(JumpInstructions);
    if (MonoSize <= kHookOffset + kJumpSize) {
        LOG("Refusing hook: offset outside reported module range");
        return 0;
    }

    void* loadSprxAssembly = ((void*)MonoAddr) + kHookOffset;

    // Exact entry from the hash-pinned runtime supplied with this application.
    // Firmware's own Mono and arbitrary other function prologues are not accepted.
    {
        static const uint8_t expected[] = {0x55,0x48,0x89,0xe5,0x41,0x57,0x41,0x56,0x41,0x55,0x41,0x54,0x53,0x48,0x83,0xec,0x28,0x44,0x89,0x4d,0xc4,0x44,0x89,0x45,0xcc,0x89,0x4d,0xbc,0x89,0x55,0xc0,0x48};
        if (MonoSize < kHookOffset + sizeof(expected) || memcmp(loadSprxAssembly, expected, sizeof(expected)) != 0) {
            LOG("Refusing hook: instruction signature mismatch (unknown runtime)");
            return 0;
        }
    }

    if (!WriteJump(loadSprxAssembly, hookLoadSprxAssembly, 0)) {
        LOG("Refusing hook: cannot write runtime hook");
        return 0;
    }
    LOG("Hooks installed.");
	
	//Fix Internal Call in Debug Mode
	//6.72: 0x17A0F0
	//Hint: The only one function that references the string "Microsoft.Win32.NativeMethods"
	//void* mono_icall_table_init = ((void*)MonoAddr) + 0x17A0F0;
	//((void(*)())mono_icall_table_init)();
    return 1;
}
