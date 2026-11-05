// Per-frame counters for the FNA3D calls made from C#, shown by the "Show FPS" overlay.
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

void __real_FNA3D_SwapBuffers(FNA3D_Device *, void *, void *, void *);
void __wrap_FNA3D_SwapBuffers(FNA3D_Device *d, void *src, void *dst, void *window)
{
    uint64_t t = now();
    __real_FNA3D_SwapBuffers(d, src, dst, window);
    current[STAT_SWAP_MS] = ms_since(t);
    memcpy(last_frame, current, sizeof(current));
    memset(current, 0, sizeof(current));
}

// Called from C# (DllImport "__Internal"): copies the counters of the last presented frame.
void switch_gpu_stats(float *out, int count)
{
    for (int i = 0; i < count && i < STAT_COUNT; i++)
        out[i] = (float)last_frame[i];
}
