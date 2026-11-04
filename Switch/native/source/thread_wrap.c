#include <switch.h>
#include <stdatomic.h>

// libnx's pthreads and SDL create threads with cpuid -2 ("process default core"), and Horizon pins
// such a thread to that single core. Left alone, the game loop, Mono's threads, SDL/FAudio audio and
// the music decoder all share core 0 while cores 1 and 2 sit idle, which stalls audio mixing.
//
// The first thread created is the runtime thread running the game loop (see main.c): it keeps core 0.
// Every later thread gets core 1 or 2 as its ideal core, and all of them may migrate across 0-2.
// Linked with -Wl,--wrap=threadCreate.

#define APP_CORE_MASK 0x7

Result __real_threadCreate(Thread *t, ThreadFunc entry, void *arg, void *stack_mem, size_t stack_sz, int prio, int cpuid);

static atomic_int created_threads;

Result __wrap_threadCreate(Thread *t, ThreadFunc entry, void *arg, void *stack_mem, size_t stack_sz, int prio, int cpuid)
{
    if (cpuid != -2)
        return __real_threadCreate(t, entry, arg, stack_mem, stack_sz, prio, cpuid);

    int index = atomic_fetch_add(&created_threads, 1);
    int core = index == 0 ? 0 : 1 + (index - 1) % 2;

    Result rc = __real_threadCreate(t, entry, arg, stack_mem, stack_sz, prio, core);
    if (R_FAILED(rc))
        return __real_threadCreate(t, entry, arg, stack_mem, stack_sz, prio, cpuid);

    // Best effort: if the process may not use all three cores the thread just stays on its ideal one.
    svcSetThreadCoreMask(t->handle, core, APP_CORE_MASK);
    return rc;
}
