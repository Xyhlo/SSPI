#include "../SDK/ps4-libjbc/jailbreak.h"

typedef long long unsigned int U64;

extern struct jbc_cred OriCred;
extern int jailbroken;
extern int jailbreak(U64 authID);
extern int unjailbreak();
extern int isJailbroken();