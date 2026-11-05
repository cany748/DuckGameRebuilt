// Native audio output for FAudio on Switch: mixes straight into the system audio renderer (audren)
// from a dedicated thread, replacing FAudio's SDL2 audio device.
//
// SDL2's Switch audio driver queues only two buffers and waits for each one to start playing,
// so it either underruns (small buffers) or adds a lot of latency (large ones). Here FAudio mixes
// one audren frame (5 ms) at a time and keeps a few frames queued. If any audren step fails, the
// original SDL2 device is used instead.
//
// The four FAudio platform entry points are linked with -Wl,--wrap; the rest of FAudio's SDL2
// platform layer (threads, mutexes, I/O) stays in use.

#include <switch.h>
#include <malloc.h>
#include <stdatomic.h>
#include <string.h>

#include "FAudio_internal.h"
#include "io_util.h"

#define SAMPLE_RATE 48000
#define CHANNELS 2
#define FRAME_SAMPLES 240 /* one audren frame at 48 kHz */
#define BUFFER_COUNT 4
#define THREAD_PRIORITY 0x2B /* just above the default 0x2C used by the game threads */
#define THREAD_CORE 2

enum
{
    AUDIO_NOT_STARTED = 0,
    AUDIO_AUDREN = 1,
    AUDIO_SDL_FALLBACK = 2,
    AUDIO_FAILED = 3,
};

static const AudioRendererConfig renderer_config = {
    .output_rate = AudioRendererOutputRate_48kHz,
    .num_voices = 2,
    .num_effects = 0,
    .num_sinks = 1,
    .num_mix_objs = 1,
    .num_mix_buffers = CHANNELS,
};

typedef struct
{
    FAudio *audio;
    AudioDriver driver;
    AudioDriverWaveBuf buffers[BUFFER_COUNT];
    int16_t *pool;
    float mix[FRAME_SAMPLES * CHANNELS];
    Thread thread;
    atomic_bool running;
    bool renderer, driver_created;
} SwitchAudioDevice;

static atomic_int audio_state;
static Result audio_error;
static const char *audio_error_step = "";
static atomic_uint frames_mixed, starved_updates;
static void *sdl_device;

void __real_FAudio_PlatformInit(FAudio *, uint32_t, uint32_t, FAudioWaveFormatExtensible *, uint32_t *, void **);
void __real_FAudio_PlatformQuit(void *);

static void fill_buffer(SwitchAudioDevice *dev, int index)
{
    FAudio_zero(dev->mix, sizeof(dev->mix));
    if (dev->audio->active)
        FAudio_INTERNAL_UpdateEngine(dev->audio, dev->mix);

    int16_t *out = dev->pool + index * FRAME_SAMPLES * CHANNELS;
    for (int i = 0; i < FRAME_SAMPLES * CHANNELS; i++)
    {
        float s = dev->mix[i] * 32767.0f;
        out[i] = s > 32767.0f ? 32767 : s < -32768.0f ? -32768 : (int16_t)s;
    }
    armDCacheFlush(out, FRAME_SAMPLES * CHANNELS * sizeof(int16_t));
    audrvVoiceAddWaveBuf(&dev->driver, 0, &dev->buffers[index]);
    atomic_fetch_add(&frames_mixed, 1);
}

static void audio_thread(void *arg)
{
    SwitchAudioDevice *dev = arg;
    while (atomic_load(&dev->running))
    {
        int queued = 0;
        for (int i = 0; i < BUFFER_COUNT; i++)
        {
            AudioDriverWaveBufState state = dev->buffers[i].state;
            if (state == AudioDriverWaveBufState_Free || state == AudioDriverWaveBufState_Done)
            {
                fill_buffer(dev, i);
                queued++;
            }
        }
        if (queued == BUFFER_COUNT)
            atomic_fetch_add(&starved_updates, 1);
        if (!audrvVoiceIsPlaying(&dev->driver, 0))
            audrvVoiceStart(&dev->driver, 0);
        audrvUpdate(&dev->driver);
        // Always wait: bounds the loop to the renderer's 5 ms frame even if something is off.
        audrenWaitFrame();
    }
}

static void destroy_device(SwitchAudioDevice *dev)
{
    if (dev->driver_created)
        audrvClose(&dev->driver);
    if (dev->renderer)
        audrenExit();
    free(dev->pool);
    free(dev);
}

static bool step(Result rc, const char *name)
{
    if (R_SUCCEEDED(rc))
        return true;
    audio_error = rc;
    audio_error_step = name;
    io_debugf("audio: %s failed: 0x%X", name, rc);
    return false;
}

static SwitchAudioDevice *open_audren(FAudio *audio)
{
    SwitchAudioDevice *dev = calloc(1, sizeof(SwitchAudioDevice));
    dev->audio = audio;

    if (!step(audrenInitialize(&renderer_config), "audrenInitialize"))
        goto fail;
    dev->renderer = true;
    if (!step(audrvCreate(&dev->driver, &renderer_config, CHANNELS), "audrvCreate"))
        goto fail;
    dev->driver_created = true;

    size_t buffer_bytes = FRAME_SAMPLES * CHANNELS * sizeof(int16_t);
    size_t pool_size = (BUFFER_COUNT * buffer_bytes + 0xFFF) & ~0xFFF;
    dev->pool = memalign(0x1000, pool_size);
    memset(dev->pool, 0, pool_size);
    armDCacheFlush(dev->pool, pool_size);
    for (int i = 0; i < BUFFER_COUNT; i++)
    {
        dev->buffers[i].data_raw = dev->pool;
        dev->buffers[i].size = pool_size;
        dev->buffers[i].start_sample_offset = i * FRAME_SAMPLES;
        dev->buffers[i].end_sample_offset = (i + 1) * FRAME_SAMPLES;
    }

    int pool_id = audrvMemPoolAdd(&dev->driver, dev->pool, pool_size);
    if (pool_id < 0 || !audrvMemPoolAttach(&dev->driver, pool_id))
    {
        step(MAKERESULT(Module_Libnx, LibnxError_BadInput), "audrvMemPoolAdd");
        goto fail;
    }

    static const u8 sink_channels[CHANNELS] = {0, 1};
    if (audrvDeviceSinkAdd(&dev->driver, AUDREN_DEFAULT_DEVICE_NAME, CHANNELS, sink_channels) < 0)
    {
        step(MAKERESULT(Module_Libnx, LibnxError_BadInput), "audrvDeviceSinkAdd");
        goto fail;
    }
    if (!step(audrvUpdate(&dev->driver), "audrvUpdate") || !step(audrenStartAudioRenderer(), "audrenStartAudioRenderer"))
        goto fail;

    if (!audrvVoiceInit(&dev->driver, 0, CHANNELS, PcmFormat_Int16, SAMPLE_RATE))
    {
        step(MAKERESULT(Module_Libnx, LibnxError_BadInput), "audrvVoiceInit");
        goto fail;
    }
    audrvVoiceSetDestinationMix(&dev->driver, 0, AUDREN_FINAL_MIX_ID);
    audrvVoiceSetMixFactor(&dev->driver, 0, 1.0f, 0, 0);
    audrvVoiceSetMixFactor(&dev->driver, 0, 0.0f, 0, 1);
    audrvVoiceSetMixFactor(&dev->driver, 0, 0.0f, 1, 0);
    audrvVoiceSetMixFactor(&dev->driver, 0, 1.0f, 1, 1);
    audrvVoiceStart(&dev->driver, 0);
    if (!step(audrvUpdate(&dev->driver), "audrvUpdate(voice)"))
        goto fail;

    atomic_store(&dev->running, true);
    if (!step(threadCreate(&dev->thread, audio_thread, dev, NULL, 0x10000, THREAD_PRIORITY, THREAD_CORE), "threadCreate"))
        goto fail;
    if (!step(threadStart(&dev->thread), "threadStart"))
    {
        threadClose(&dev->thread);
        goto fail;
    }
    io_debugf("audio: audren output started");
    return dev;

fail:
    destroy_device(dev);
    return NULL;
}

void __wrap_FAudio_PlatformInit(FAudio *audio, uint32_t flags, uint32_t deviceIndex,
                                FAudioWaveFormatExtensible *mixFormat, uint32_t *updateSize, void **platformDevice)
{
    // audren allows one client per process, and SDL's Switch driver uses audren as well, so the
    // device is claimed atomically: FNA can create two FAudio engines at the same time.
    int expected = AUDIO_NOT_STARTED;
    if (!atomic_compare_exchange_strong(&audio_state, &expected, AUDIO_AUDREN))
    {
        io_debugf("audio: engine %p gets no output, device already taken (state %d)", (void *)audio, expected);
        WriteWaveFormatExtensible(mixFormat, CHANNELS, SAMPLE_RATE, &DATAFORMAT_SUBTYPE_IEEE_FLOAT);
        *updateSize = FRAME_SAMPLES;
        *platformDevice = NULL;
        return;
    }

    io_debugf("audio: init for engine %p", (void *)audio);
    SwitchAudioDevice *dev = open_audren(audio);
    if (dev != NULL)
    {
        WriteWaveFormatExtensible(mixFormat, CHANNELS, SAMPLE_RATE, &DATAFORMAT_SUBTYPE_IEEE_FLOAT);
        *updateSize = FRAME_SAMPLES;
        *platformDevice = dev;
        return;
    }

    __real_FAudio_PlatformInit(audio, flags, deviceIndex, mixFormat, updateSize, platformDevice);
    sdl_device = *platformDevice;
    atomic_store(&audio_state, sdl_device != NULL ? AUDIO_SDL_FALLBACK : AUDIO_FAILED);
}

void __wrap_FAudio_PlatformQuit(void *platformDevice)
{
    io_debugf("audio: quit device %p", platformDevice);
    if (platformDevice != NULL && platformDevice == sdl_device)
    {
        __real_FAudio_PlatformQuit(platformDevice);
        sdl_device = NULL;
        atomic_store(&audio_state, AUDIO_NOT_STARTED);
        return;
    }
    SwitchAudioDevice *dev = platformDevice;
    if (dev == NULL)
        return;
    atomic_store(&dev->running, false);
    threadWaitForExit(&dev->thread);
    threadClose(&dev->thread);
    audrvVoiceStop(&dev->driver, 0);
    audrvUpdate(&dev->driver);
    destroy_device(dev);
    atomic_store(&audio_state, AUDIO_NOT_STARTED);
}

uint32_t __wrap_FAudio_PlatformGetDeviceCount(void)
{
    return 1;
}

uint32_t __wrap_FAudio_PlatformGetDeviceDetails(uint32_t index, FAudioDeviceDetails *details)
{
    if (index != 0)
        return FAUDIO_E_INVALID_CALL;
    FAudio_zero(details, sizeof(FAudioDeviceDetails));
    details->DeviceID[0] = L'0';
    static const char name[] = "Switch audio renderer";
    for (size_t i = 0; i < sizeof(name); i++)
        details->DisplayName[i] = name[i];
    details->Role = FAudioGlobalDefaultDevice;
    WriteWaveFormatExtensible(&details->OutputFormat, CHANNELS, SAMPLE_RATE, &DATAFORMAT_SUBTYPE_PCM);
    return 0;
}

// Called from C# (DllImport "__Internal") for the overlay: state, frames mixed, starved updates, error code.
void switch_audio_stats(int *out)
{
    out[0] = atomic_load(&audio_state);
    out[1] = (int)atomic_load(&frames_mixed);
    out[2] = (int)atomic_load(&starved_updates);
    out[3] = (int)audio_error;
}

const char *switch_audio_error_step(void)
{
    return audio_error_step;
}
