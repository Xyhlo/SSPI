/* A disposable loader probe. It performs no work beyond returning success. */
typedef __SIZE_TYPE__ GsProbeSize;

__attribute__((section(".data.sce_module_param"), used, aligned(8)))
const unsigned long long _sceProcessParam[] = { 0x18, 0x13C13F4BF, 0x1000051 };

__attribute__((visibility("default"))) const char *g_pluginName = "SSPIResidentProbe";
__attribute__((visibility("default"))) const char *g_pluginDesc = "SSPI resident loader diagnostic";
__attribute__((visibility("default"))) const char *g_pluginAuth = "SSPI";
__attribute__((visibility("default"))) unsigned int g_pluginVersion = 0x00000100u;

__attribute__((visibility("default"))) int module_start(GsProbeSize argc, const void *argv)
{
    (void)argc; (void)argv;
    return 0;
}

__attribute__((visibility("default"))) int module_stop(GsProbeSize argc, const void *argv)
{
    (void)argc; (void)argv;
    return 0;
}

__attribute__((visibility("default"))) int plugin_load(int argc, const char *argv[])
{
    (void)argc; (void)argv;
    return 0;
}

__attribute__((visibility("default"))) int plugin_unload(int argc, const char *argv[])
{
    (void)argc; (void)argv;
    return 0;
}

__attribute__((visibility("default"))) int _init(GsProbeSize argc, const void *argv)
{
    (void)argc; (void)argv;
    return 0;
}

__attribute__((visibility("default"))) int _fini(GsProbeSize argc, const void *argv)
{
    (void)argc; (void)argv;
    return 0;
}
