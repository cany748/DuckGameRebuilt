// Native audio output for FAudio on Switch: mixes straight into the system audio renderer (audren)
// from a dedicated high-priority thread, replacing FAudio's SDL2 audio device.
//
// SDL2's Switch audio driver queues only two buffers and waits for each one to start playing,
// so it either underruns (small buffers) or adds a lot of latency (large ones). Here FAudio mixes
// one audren frame (5 ms) at a time and keeps a few frames queued.
//
// The four FAudio platform entry points below are linked with -Wl,--wrap; the rest of FAudio's
// SDL2 platform layer (threads, mutexes, I/O) stays in use.

#include <switch.h>
#include <malloc.h>
#include <stdatomic.h>
#include <string.h>

#include "FAudio_internal.h"

#define SAMPLE_RATE 48000
#define CHANNELS 2
#define FRAME_SAMPLES 240 /* one audren frame at 48 kHz */
#define BUFFER_COUNT 4
#define THREAD_PRIORITY 0x2B /* just above the default 0x2C used by the game threads */
#define THREAD_CORE 2

static const AudioRendererConfig renderer_config = {
    .output_rate = AudioRendererOutputRate_48kHz,
    .num_voices = 1,
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
} SwitchAudioDevice;

static atomic_bool device_open;

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
}

static void audio_thread(void *arg)
{
    SwitchAudioDevice *dev = arg;
    while (atomic_load(&dev->running))
    {
        bool queued = false;
        for (int i = 0; i < BUFFER_COUNT; i++)
        {
            AudioDriverWaveBufState state = dev->buffers[i].state;
            if (state == AudioDriverWaveBufState_Free || state == AudioDriverWaveBufState_Done)
            {
                fill_buffer(dev, i);
                queued = true;
            }
        }
        if (!audrvVoiceIsPlaying(&dev->driver, 0))
            audrvVoiceStart(&dev->driver, 0);
        audrvUpdate(&dev->driver);
        if (!queued)
            audrenWaitFrame();
    }
}

static void close_device(SwitchAudioDevice *dev, bool renderer, bool driver)
{
    if (driver)
        audrvClose(&dev->driver);
    if (renderer)
        audrenExit();
    free(dev->pool);
    free(dev);
    atomic_store(&device_open, false);
}

void __wrap_FAudio_PlatformInit(FAudio *audio, uint32_t flags, uint32_t deviceIndex,
                                FAudioWaveFormatExtensible *mixFormat, uint32_t *updateSize, void **platformDevice)
{
    (void)flags;
    (void)deviceIndex;

    WriteWaveFormatExtensible(mixFormat, CHANNELS, SAMPLE_RATE, &DATAFORMAT_SUBTYPE_IEEE_FLOAT);
    *updateSize = FRAME_SAMPLES;
    *platformDevice = NULL;

    // audren supports a single client per process; a second FAudio instance stays silent.
    if (atomic_exchange(&device_open, true))
        return;

    SwitchAudioDevice *dev = calloc(1, sizeof(SwitchAudioDevice));
    dev->audio = audio;

    if (R_FAILED(audrenInitialize(&renderer_config)))
    {
        close_device(dev, false, false);
        return;
    }
    if (R_FAILED(audrvCreate(&dev->driver, &renderer_config, CHANNELS)))
    {
        close_device(dev, true, false);
        return;
    }

    size_t buffer_bytes = FRAME_SAMPLES * CHANNELS * sizeof(int16_t);
    size_t pool_size = (BUFFER_COUNT * buffer_bytes + 0xFFF) & ~0xFFF;
    dev->pool = memalign(0x1000, pool_size);
    memset(dev->pool, 0, pool_size);
    for (int i = 0; i < BUFFER_COUNT; i++)
    {
        dev->buffers[i].data_raw = dev->pool;
        dev->buffers[i].size = pool_size;
        dev->buffers[i].start_sample_offset = i * FRAME_SAMPLES;
        dev->buffers[i].end_sample_offset = (i + 1) * FRAME_SAMPLES;
    }

    int pool_id = audrvMemPoolAdd(&dev->driver, dev->pool, pool_size);
    audrvMemPoolAttach(&dev->driver, pool_id);

    static const u8 sink_channels[CHANNELS] = {0, 1};
    audrvDeviceSinkAdd(&dev->driver, AUDREN_DEFAULT_DEVICE_NAME, CHANNELS, sink_channels);

    if (R_FAILED(audrvUpdate(&dev->driver)) || R_FAILED(audrenStartAudioRenderer()))
    {
        close_device(dev, true, true);
        return;
    }

    audrvVoiceInit(&dev->driver, 0, CHANNELS, PcmFormat_Int16, SAMPLE_RATE);
    audrvVoiceSetDestinationMix(&dev->driver, 0, AUDREN_FINAL_MIX_ID);
    audrvVoiceSetMixFactor(&dev->driver, 0, 1.0f, 0, 0);
    audrvVoiceSetMixFactor(&dev->driver, 0, 0.0f, 0, 1);
    audrvVoiceSetMixFactor(&dev->driver, 0, 0.0f, 1, 0);
    audrvVoiceSetMixFactor(&dev->driver, 0, 1.0f, 1, 1);
    audrvVoiceStart(&dev->driver, 0);

    atomic_store(&dev->running, true);
    if (R_FAILED(threadCreate(&dev->thread, audio_thread, dev, NULL, 0x10000, THREAD_PRIORITY, THREAD_CORE)) ||
        R_FAILED(threadStart(&dev->thread)))
    {
        close_device(dev, true, true);
        return;
    }
    *platformDevice = dev;
}

void __wrap_FAudio_PlatformQuit(void *platformDevice)
{
    SwitchAudioDevice *dev = platformDevice;
    if (dev == NULL)
        return;
    atomic_store(&dev->running, false);
    threadWaitForExit(&dev->thread);
    threadClose(&dev->thread);
    audrvVoiceStop(&dev->driver, 0);
    audrvUpdate(&dev->driver);
    close_device(dev, true, true);
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
