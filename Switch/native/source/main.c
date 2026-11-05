#include "core.h"
#include <mono/jit/mono-private-unstable.h>
#include <pthread.h>
#include <sys/stat.h>

#define SAVE_ROOT "/switch/DuckGame/home"
#define GAME_DIR "/switch/DuckGame/game"

// The homebrew main thread has a small stack, while MojoShader keeps a ~330KB context on the
// stack and the interpreter nests deeply, so the runtime gets a thread of its own.
#define RUNTIME_STACK_SIZE (32 * 1024 * 1024)

static int exit_code = 0;

#ifdef DUCKGAME_AOT
#define AOT_MODULE(sym) do { extern void *sym; mono_aot_register_module(sym); } while (0)
#endif

// Precompiled assemblies run as native code; the rest (assemblies the AOT compiler can't handle,
// code it couldn't pregenerate) falls back to the interpreter. See codeman_wrap.c.
static void register_aot_modules(void)
{
#ifdef DUCKGAME_AOT
#include "aot_modules.h"
    mono_jit_set_aot_mode(MONO_AOT_MODE_INTERP);
#else
    mono_jit_set_aot_mode(MONO_AOT_MODE_INTERP_ONLY);
#endif
}

static void *run_game(void *arg)
{
    (void)arg;

    register_aot_modules();

    extern void frame_stats_install_gc_profiler(void);
    frame_stats_install_gc_profiler();

    // runtimeconfig.json isn't read by an embedding host, so feature switches go here.
    // EventSource needs EventPipe, which mono-nx doesn't build; resource keys avoid loading
    // resource sets just to format exception messages.
    const char *keys[] = {
        "System.Diagnostics.Tracing.EventSource.IsSupported",
        "System.Resources.UseSystemResourceKeys",
        "System.Globalization.Invariant",
        "APP_CONTEXT_BASE_DIRECTORY",
    };
    const char *values[] = {"false", "true", "false", GAME_DIR "/"};
    monovm_initialize(sizeof(keys) / sizeof(keys[0]), keys, values);

    application_configure_mono();

    MonoDomain *domain = mono_jit_init("DuckGame");
    if (!domain)
    {
        fatal_error("Failed to initialize mono domain");
        exit_code = 1;
        return NULL;
    }

    char *launch_dll = io_strdup(g_config.default_assembly);
    if (!launch_dll)
    {
        fatal_error("default_assembly is not set in config.ini");
        exit_code = 1;
        return NULL;
    }

    io_debugf("Loading assembly %s", launch_dll);
    application_chdir_to_assembly(launch_dll);

    MonoAssembly *assembly = mono_domain_assembly_open(domain, launch_dll);
    if (!assembly)
    {
        fatal_error("Failed to load DuckGame assembly");
        exit_code = 1;
        return NULL;
    }

    // -linux selects DGR's non-Windows code paths (no Win32 module hooks, single-threaded loading);
    // the rest switch off desktop services that don't exist on the console.
    char *monoargs[64] = {launch_dll, "-linux", "-nothreading", "-nosteam", "-nomods", "-noRPC", "-norebuiltupdates", "-nocloud"};
    int argc = 8;
    // Extra arguments from config.ini, e.g. args = -command "lev arcade" for repeatable benchmarks.
    // Double quotes group words into one argument and are stripped.
    char *extra = g_config.game_args ? io_strdup(g_config.game_args) : NULL;
    for (char *p = extra; p && *p && argc < 63;)
    {
        while (*p == ' ')
            p++;
        if (!*p)
            break;
        char *start = p;
        if (*p == '"')
        {
            p = strchr(p + 1, '"');
            p = p ? p + 1 : start + strlen(start);
        }
        else
        {
            while (*p && *p != ' ')
                p++;
        }
        if (*p)
            *p++ = '\0';
        if (*start == '"' && start[strlen(start) - 1] == '"' && strlen(start) > 1)
        {
            start[strlen(start) - 1] = '\0';
            start++;
        }
        monoargs[argc++] = start;
    }
    exit_code = mono_jit_exec(domain, assembly, argc, monoargs);
    free(extra);

    mono_jit_cleanup(domain);
    free(launch_dll);
    return NULL;
}

int main(int argc, char *argv[])
{
    // DGR picks the SDL backend from this variable; only SDL2 exists for libnx.
    setenv("FNA_PLATFORM_BACKEND", "SDL2", 1);
    setenv("FNA3D_FORCE_DRIVER", "OpenGL", 1);
    // SDL2 for libnx defaults to a GLES context; mesa also offers desktop GL, which is the path FNA3D is tested on.
    setenv("FNA3D_OPENGL_FORCE_COMPATIBILITY_PROFILE", "1", 1);
    setenv("FNA3D_MOJOSHADER_PROFILE", "glsl120", 1);
    // DGR starts SDL text input at startup, which on Switch pops up the software keyboard.
    setenv("FNADROID", "1", 1);
    // DGR allocates a lot every frame: a 32 MB nursery (default 4 MB) makes minor collections rarer.
    // SGen otherwise lets the major heap grow toward the whole (half-of-RAM) mono heap before a full
    // collection; past the soft limit major collections run every few nursery-sizes of growth.
    setenv("MONO_GC_PARAMS", "nursery-size=32m,soft-heap-limit=512m", 1);

    // Horizon has no passwd database; .NET resolves special folders from these variables.
    mkdir(SAVE_ROOT, 0777);
    setenv("HOME", SAVE_ROOT, 1);
    setenv("XDG_DATA_HOME", SAVE_ROOT "/.local/share", 1);
    setenv("XDG_CONFIG_HOME", SAVE_ROOT "/.config", 1);

    if (!application_initialize(CONFIG_INI_PATH))
        return 1;

    pthread_attr_t attr;
    pthread_attr_init(&attr);
    pthread_attr_setstacksize(&attr, RUNTIME_STACK_SIZE);

    pthread_t thread;
    if (pthread_create(&thread, &attr, run_game, NULL) != 0)
    {
        fatal_error("Failed to create the runtime thread");
        return 1;
    }
    pthread_attr_destroy(&attr);
    pthread_join(thread, NULL);

    application_terminate();
    return exit_code;
}
