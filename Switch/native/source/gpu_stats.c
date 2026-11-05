// Per-frame counters for the FNA3D calls made from C# and for GC pauses, shown by the "Show FPS" overlay.
// The functions are linked with -Wl,--wrap, which only redirects the references from the P/Invoke
// symbol table (dlshim_fna.c); FNA3D's internal calls are untouched.

#include <switch.h>
#include <stdint.h>
#include <string.h>

typedef struct FNA3D_Device FNA3D_Device;
typedef struct FNA3D_Buffer FNA3D_Buffer;
typedef struct FNA3D_Texture FNA3D_Texture;
typedef struct FNA3D_Effect FNA3D_Effect;
typedef struct FNA3D_Renderbuffer FNA3D_Renderbuffer;

enum
{
    STAT_DRAWS,
    STAT_DRAW_MS,
    STAT_UPLOADS,
    STAT_UPLOAD_MS,
    STAT_EFFECTS,
    STAT_TARGETS,
    STAT_SWAP_MS,
    STAT_CLEAR_MS,
    STAT_GC_MS,    /* since the previous switch_gpu_stats() call, not per frame */
    STAT_GC_COUNT, /* same */
    STAT_GC_MAX_MS,
    STAT_COUNT
};

static double current[STAT_COUNT];
static double last_frame[STAT_COUNT];

static inline uint64_t now(void) { return armGetSystemTick(); }
static inline double ms_since(uint64_t start) { return armTicksToNs(armGetSystemTick() - start) / 1e6; }

void __real_FNA3D_DrawIndexedPrimitives(FNA3D_Device *, int, int32_t, int32_t, int32_t, int32_t, int32_t, FNA3D_Buffer *, int);
void __wrap_FNA3D_DrawIndexedPrimitives(FNA3D_Device *d, int type, int32_t baseVertex, int32_t minIndex, int32_t numVertices,
                                        int32_t startIndex, int32_t count, FNA3D_Buffer *indices, int size)
{
    uint64_t t = now();
    __real_FNA3D_DrawIndexedPrimitives(d, type, baseVertex, minIndex, numVertices, startIndex, count, indices, size);
    current[STAT_DRAWS]++;
    current[STAT_DRAW_MS] += ms_since(t);
}

void __real_FNA3D_DrawPrimitives(FNA3D_Device *, int, int32_t, int32_t);
void __wrap_FNA3D_DrawPrimitives(FNA3D_Device *d, int type, int32_t start, int32_t count)
{
    uint64_t t = now();
    __real_FNA3D_DrawPrimitives(d, type, start, count);
    current[STAT_DRAWS]++;
    current[STAT_DRAW_MS] += ms_since(t);
}

void __real_FNA3D_SetVertexBufferData(FNA3D_Device *, FNA3D_Buffer *, int32_t, void *, int32_t, int32_t, int32_t, int);
void __wrap_FNA3D_SetVertexBufferData(FNA3D_Device *d, FNA3D_Buffer *b, int32_t offset, void *data, int32_t count,
                                      int32_t size, int32_t stride, int options)
{
    uint64_t t = now();
    __real_FNA3D_SetVertexBufferData(d, b, offset, data, count, size, stride, options);
    current[STAT_UPLOADS]++;
    current[STAT_UPLOAD_MS] += ms_since(t);
}

void __real_FNA3D_SetTextureData2D(FNA3D_Device *, FNA3D_Texture *, int32_t, int32_t, int32_t, int32_t, int32_t, void *, int32_t);
void __wrap_FNA3D_SetTextureData2D(FNA3D_Device *d, FNA3D_Texture *tex, int32_t x, int32_t y, int32_t w, int32_t h,
                                   int32_t level, void *data, int32_t length)
{
    uint64_t t = now();
    __real_FNA3D_SetTextureData2D(d, tex, x, y, w, h, level, data, length);
    current[STAT_UPLOADS]++;
    current[STAT_UPLOAD_MS] += ms_since(t);
}

void __real_FNA3D_ApplyEffect(FNA3D_Device *, FNA3D_Effect *, uint32_t, void *);
void __wrap_FNA3D_ApplyEffect(FNA3D_Device *d, FNA3D_Effect *effect, uint32_t pass, void *changes)
{
    uint64_t t = now();
    __real_FNA3D_ApplyEffect(d, effect, pass, changes);
    current[STAT_EFFECTS]++;
    current[STAT_DRAW_MS] += ms_since(t);
}

void __real_FNA3D_SetRenderTargets(FNA3D_Device *, void *, int32_t, FNA3D_Renderbuffer *, int, uint8_t);
void __wrap_FNA3D_SetRenderTargets(FNA3D_Device *d, void *targets, int32_t count, FNA3D_Renderbuffer *depth, int format,
                                   uint8_t preserve)
{
    uint64_t t = now();
    __real_FNA3D_SetRenderTargets(d, targets, count, depth, format, preserve);
    current[STAT_TARGETS]++;
    current[STAT_DRAW_MS] += ms_since(t);
}

// The first clear of the backbuffer after a swap blocks until the system hands back a free buffer
// (vsync), so this shows time spent waiting rather than working.
void __real_FNA3D_Clear(FNA3D_Device *, int, void *, float, int32_t);
void __wrap_FNA3D_Clear(FNA3D_Device *d, int options, void *color, float depth, int32_t stencil)
{
    uint64_t t = now();
    __real_FNA3D_Clear(d, options, color, depth, stencil);
    current[STAT_CLEAR_MS] += ms_since(t);
}

void __real_FNA3D_SwapBuffers(FNA3D_Device *, void *, void *, void *);
void __wrap_FNA3D_SwapBuffers(FNA3D_Device *d, void *src, void *dst, void *window)
{
    uint64_t t = now();
    __real_FNA3D_SwapBuffers(d, src, dst, window);
    current[STAT_SWAP_MS] = ms_since(t);
    memcpy(last_frame, current, sizeof(current));
    memset(current, 0, sizeof(current));
}

// GC pauses: time from stopping the world to restarting it, summed per frame.
#include <mono/metadata/profiler.h>

static uint64_t gc_pause_start;
static double gc_window[3]; /* total ms, count, longest ms */

static void on_gc_event(MonoProfiler *prof, MonoProfilerGCEvent event, uint32_t generation, mono_bool is_serial)
{
    (void)prof;
    (void)generation;
    (void)is_serial;
    if (event == MONO_GC_EVENT_PRE_STOP_WORLD)
    {
        gc_pause_start = now();
    }
    else if (event == MONO_GC_EVENT_POST_START_WORLD && gc_pause_start != 0)
    {
        double pause = ms_since(gc_pause_start);
        gc_window[0] += pause;
        gc_window[1]++;
        if (pause > gc_window[2])
            gc_window[2] = pause;
        gc_pause_start = 0;
    }
}

void frame_stats_install_gc_profiler(void)
{
    MonoProfilerHandle handle = mono_profiler_create(NULL);
    mono_profiler_set_gc_event_callback(handle, on_gc_event);
}

// Called from C# (DllImport "__Internal"): copies the counters of the last presented frame.
void switch_gpu_stats(float *out, int count)
{
    for (int i = 0; i < count && i < STAT_GC_MS; i++)
        out[i] = (float)last_frame[i];
    for (int i = 0; i < 3 && STAT_GC_MS + i < count; i++)
    {
        out[STAT_GC_MS + i] = (float)gc_window[i];
        gc_window[i] = 0;
    }
}
