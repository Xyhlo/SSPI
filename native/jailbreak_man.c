#include "jailbreak_man.h"
#include "../SDK/ps4-libjbc/jailbreak.h"

struct jbc_cred OriCred;

int jailbroken = 0;

int jailbreak(U64 authID)
{
    if (jailbroken)
        return 0;

    struct jbc_cred cr;
    if(jbc_get_cred(&cr))
        return -1;
    if(jbc_get_cred(&OriCred))
        return -1;
    if(jbc_jailbreak_cred(&cr))
        return -1;
    
    if (authID){
        cr.jdir = 0;
        cr.sceProcType = authID;
        cr.sonyCred = 0x40001c0000000000;
        cr.sceProcCap = 0x900000000000ff00;
    }
    if(jbc_set_cred(&cr))
        return -1;

    jailbroken = 1;
    return 1;
}


int unjailbreak()
{
    if (!jailbroken)
        return 0;
    if(jbc_set_cred(&OriCred))
        return -1;
    jailbroken = 0;
    return 1;
}

int isJailbroken() {
	return jailbroken;
}