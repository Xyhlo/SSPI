extern char appRoot[0x100];
extern char baseCon[0x100];
extern char baseDir[0x100];
extern char mainExe[0x100];


void klog(const char* str);
void klogf(const char* str, ...);
int direxists(const char* path);
int file_exists(const char* path);
void findAppMount(char* path);

#ifdef DEBUG
#define LOG klog
#define LOGF klogf
#else
#define LOG(x)
#define LOGF(x,...)
#endif
