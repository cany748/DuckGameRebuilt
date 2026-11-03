#include "core.h"

int main(int argc, char *argv[])
{
    // DGR picks the SDL backend from this variable; only SDL2 exists for libnx.
    setenv("FNA_PLATFORM_BACKEND", "SDL2", 1);
    setenv("FNA3D_FORCE_DRIVER", "OpenGL", 1);

    if (!application_initialize(CONFIG_INI_PATH))
        return 1;

    mono_jit_set_aot_mode(MONO_AOT_MODE_INTERP_ONLY);

    application_configure_mono();

    MonoDomain *domain = mono_jit_init("DuckGame");
    if (!domain)
    {
        fatal_error("Failed to initialize mono domain");
        return 1;
    }

    char *launch_dll = io_strdup(g_config.default_assembly);
    if (!launch_dll)
    {
        fatal_error("default_assembly is not set in config.ini");
        return 1;
    }

    io_debugf("Loading assembly %s", launch_dll);
    application_chdir_to_assembly(launch_dll);

    MonoAssembly *assembly = mono_domain_assembly_open(domain, launch_dll);
    if (!assembly)
    {
        fatal_error("Failed to load DuckGame assembly");
        return 1;
    }

    char *monoargs[] = {launch_dll, "-nothreading"};
    mono_jit_exec(domain, assembly, 2, monoargs);

    mono_jit_cleanup(domain);
    free(launch_dll);
    application_terminate();
    return 0;
}
