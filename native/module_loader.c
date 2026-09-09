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
void WriteJump(void* Address, void* Destination, char* OriInstructions)
{
    //Write the address of our hook to the instruction.
    *(uint64_t*)(JumpInstructions + 6) = (uint64_t)Destination;

    sceKernelMprotect((void*)Address, sizeof(JumpInstructions), PROT_READ | PROT_WRITE | PROT_EXEC);

    if (OriInstructions) {
        memcpy(OriInstructions, Address, sizeof(JumpInstructions));
    }

    memcpy(Address, JumpInstructions, sizeof(JumpInstructions));
}

//The KernelLoadStartModule export is just a check if you didn't set
//flags arguments, if not, it jump for the real function,
//we will use that to be able to keep the hook allways enabled,
//by calling the real LoadStartModule function directly
void* FindInternalFunction(void* Address){
	
	//Since is just reading maybe this isn't required, but in any case, better be sure.
    sceKernelMprotect((void*)Address, 0x20, PROT_READ | PROT_WRITE | PROT_EXEC);
	
	for (int i = 0; i < 0x20; i++){
		uint32_t* pDWORD = (uint32_t*)(Address+i);
		
		//find the function end (end with NOPs)
		if (*pDWORD == 0x90909090){
			
			//The last instruction is a jmp to the real LoadStartModule function
			uint8_t* pJmp = ((uint8_t*)pDWORD) - 5;
			
			if (*pJmp != 0xE9)
				return 0;//Whatever if fails, we still can do the dirty way disabling the hook temporally
			
			//Get the Jmp Offset
			pDWORD = (uint32_t*)(pJmp+1);
			
			//Calculate the jump offset
			return pJmp + 5 + *pDWORD;
		}
	}
	return 0;
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
        if (path[i] == '\\' || path[i] == '//' || path[i] == '/') {
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
    LOGF("Loading Assembly: %s", AssemblyName);
	
	int IsGAC = strstr(AssemblyName, "mono/gac/") != NULL;
	int IsAOT = strstr(AssemblyName, "mono/aot-cache/") != NULL;
	
	AssemblyName = ApplyRemap(AssemblyName);

    char* finalPath = AssemblyName;

    void* fp = fopen(AssemblyName, "r");
    if (fp == 0) {
        LOG("Error opening file");
		
		if (IsGAC || IsAOT) {
			LOG("Skipping GAC Hint");
			return 0;
		}

        char hintPath[0x300] = "\x0";
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
            sprintf(&hintPath, hints[i], "/app0", fname);
            fp = fopen(hintPath, "r");
            if (fp != 0)
                break;

            sprintf(&hintPath, hints[i], baseDir, fname);
            fp = fopen(hintPath, "r");
            if (fp != 0)
                break;
			
			if (ValidExt){
				sprintf(&hintPath, hintsExt[i], "/app0", fnameNoExt);
				fp = fopen(hintPath, "r");
				if (fp != 0)
					break;

				sprintf(&hintPath, hintsExt[i], baseDir, fnameNoExt);
				fp = fopen(hintPath, "r");
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
		
    fseek(fp, 0, SEEK_END);
    long int size = ftell(fp);
    fseek(fp, 0, SEEK_SET);

    char* data = malloc(size);
    int readed = fread(data, 1, size, fp);

    fclose(fp);

    if (readed != size) {
        LOG("Error reading the file");
        return 0;
    }
	
    int status = 0;
    void* Image = mono_image_open_from_data_with_name(data, size, 0, &status, RefOnly, AssemblyName);

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
	
	snprintf(rndWordRoot, sizeof(rndWordRoot), "/%s", sceKernelGetFsSandboxRandomWord());
	
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
	if (!name || !*name) return NULL;
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

void InstallHooks()
{
    LOG("Installing hooks...");

    U64 MonoAddr = 0;
    U64 MonoSize = 0;
    get_module_base("libmonosgen-2.0.prx", &MonoAddr, &MonoSize);

    if (MonoAddr == 0) {
        LOG("Refusing hook: libmonosgen-2.0.sprx address not found");
        return;
    }

    //MUST UPDATE
    //6.72: 0x18CC60
    //Hint: one of the few functions that references the string ".sprx"
    static const U64 kHookOffset = 0x18CC60;
    static const U64 kJumpSize = sizeof(JumpInstructions);
    if (MonoSize <= kHookOffset + kJumpSize) {
        LOG("Refusing hook: offset outside reported module range");
        return;
    }

    void* loadSprxAssembly = ((void*)MonoAddr) + kHookOffset;

    // Signature gate: refuse to patch anything that does not look like code.
    // A valid x86-64 function entry here starts with a prologue, jump, or push;
    // zeros or unexpected bytes mean a different runtime binary.
    {
        uint8_t first = *(volatile uint8_t*)loadSprxAssembly;
        if (first == 0x00 || (first != 0x55 && first != 0x48 && first != 0x53 &&
                              first != 0xE9 && first != 0xFF && first != 0x56)) {
            LOG("Refusing hook: instruction signature mismatch (unknown runtime)");
            return;
        }
    }

    WriteJump(loadSprxAssembly, hookLoadSprxAssembly, 0);
    LOG("Hooks installed.");
	
	//Fix Internal Call in Debug Mode
	//6.72: 0x17A0F0
	//Hint: The only one function that references the string "Microsoft.Win32.NativeMethods"
	//void* mono_icall_table_init = ((void*)MonoAddr) + 0x17A0F0;
	//((void(*)())mono_icall_table_init)();
}
