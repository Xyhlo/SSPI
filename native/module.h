#define PAGE_SIZE (16 * 1024)

#define PROT_CPU_READ 1
#define PROT_CPU_WRITE 2
#define PROT_CPU_EXEC 4

#define PROT_GPU_EXEC 8
#define PROT_GPU_READ 16
#define PROT_GPU_WRITE 32

#define PROT_NONE 0
#define PROT_READ PROT_CPU_READ
#define PROT_WRITE PROT_CPU_WRITE
#define PROT_EXEC PROT_CPU_EXEC

#define MAP_SHARED 1
#define MAP_PRIVATE 2
#define MAP_TYPE 0x0f
#define MAP_FIXED 0x10
#define MAP_ANONYMOUS 0x1000
#define MAP_32BIT 0x80000

#define MAP_FAILED (void *)-1

#define MS_SYNC 0x0000
#define MS_ASYNC 0x0001
#define MS_INVALIDATE 0x0002

extern int get_module_base(const char* name, uint64_t* base, uint64_t* size);