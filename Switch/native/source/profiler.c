#include <switch.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Sampling profiler for the render thread: Horizon lets a process pause its own threads and read their
 * registers, which works on console and in emulators. Managed addresses are named by mono_pmip when
 * the report is written, native ones are left for `nm` on the elf (offsets from the start of .text). */
#define PERIOD_NS 1000000
#define MAX_SAMPLES 120000
#define SAMPLER_STACK (64 * 1024)
#define SAMPLER_PRIORITY 0x20
#define SAMPLER_CORE 1
#define REPORT_LINES 120

typedef struct
{
    u64 pc, lr;
} Sample;

typedef struct
{
    char *name;
    int self, caller;
} Entry;

extern char *mono_pmip(void *ip);

static Sample *samples;
static volatile int sample_count;
static volatile bool running;
static Handle target;
static Thread sampler;

static void sample_loop(void *arg)
{
    (void)arg;
    while (running && sample_count < MAX_SAMPLES)
    {
        svcSleepThread(PERIOD_NS);
        if (R_FAILED(svcSetThreadActivity(target, ThreadActivity_Paused)))
            continue;
        ThreadContext context;
        Result result = svcGetThreadContext3(&context, target);
        svcSetThreadActivity(target, ThreadActivity_Runnable);
        if (R_SUCCEEDED(result))
        {
            samples[sample_count].pc = context.pc.x;
            samples[sample_count].lr = context.lr;
            sample_count++;
        }
    }
}

/* Starts sampling the calling thread. */
int switch_profiler_start(void)
{
    if (running)
        return 0;
    samples = malloc(sizeof(Sample) * MAX_SAMPLES);
    if (samples == NULL)
        return 0;
    target = threadGetCurHandle();
    sample_count = 0;
    running = true;
    if (R_FAILED(threadCreate(&sampler, sample_loop, NULL, NULL, SAMPLER_STACK, SAMPLER_PRIORITY, SAMPLER_CORE)) ||
        R_FAILED(threadStart(&sampler)))
    {
        running = false;
        return 0;
    }
    return 1;
}

static Entry *find_entry(Entry *entries, int *count, char *name)
{
    for (int i = 0; i < *count; i++)
        if (strcmp(entries[i].name, name) == 0)
        {
            free(name);
            return &entries[i];
        }
    entries[*count] = (Entry){name, 0, 0};
    return &entries[(*count)++];
}

/* The address the nro was loaded at: virtmem-free way to turn pc values into elf offsets. */
static u64 code_base(void)
{
    MemoryInfo info;
    u32 page;
    svcQueryMemory(&info, &page, (u64)&code_base);
    return info.addr;
}

static char *name_of(u64 address)
{
    char *name = mono_pmip((void *)address);
    if (name != NULL)
    {
        char *end = strstr(name, " {");
        if (end == NULL)
            end = strstr(name, " + 0x");
        if (end != NULL)
            *end = 0;
        return name;
    }
    char buffer[32];
    /* Grouped by 256 bytes: enough to tell functions apart after nm. */
    snprintf(buffer, sizeof(buffer), "native+0x%lx", (address - code_base()) & ~0xfful);
    return strdup(buffer);
}

static int by_self(const void *a, const void *b) { return ((const Entry *)b)->self - ((const Entry *)a)->self; }
static int by_caller(const void *a, const void *b) { return ((const Entry *)b)->caller - ((const Entry *)a)->caller; }

/* Stops sampling and writes the report. */
void switch_profiler_stop(const char *path)
{
    if (!running)
        return;
    running = false;
    threadWaitForExit(&sampler);
    threadClose(&sampler);

    int count = sample_count, unique = 0;
    Entry *entries = calloc(count * 2 + 1, sizeof(Entry));
    for (int i = 0; i < count; i++)
    {
        find_entry(entries, &unique, name_of(samples[i].pc))->self++;
        find_entry(entries, &unique, name_of(samples[i].lr))->caller++;
    }
    FILE *file = fopen(path, "w");
    if (file != NULL)
    {
        fprintf(file, "samples %d, text 0x%lx\n\nself%% method\n", count, code_base());
        qsort(entries, unique, sizeof(Entry), by_self);
        for (int i = 0; i < unique && i < REPORT_LINES; i++)
            fprintf(file, "%5.1f %s\n", entries[i].self * 100.0 / count, entries[i].name);
        fprintf(file, "\ncaller%% method (the leaf's return address)\n");
        qsort(entries, unique, sizeof(Entry), by_caller);
        for (int i = 0; i < unique && i < REPORT_LINES; i++)
            fprintf(file, "%5.1f %s\n", entries[i].caller * 100.0 / count, entries[i].name);
        fclose(file);
    }
    for (int i = 0; i < unique; i++)
        free(entries[i].name);
    free(entries);
    free(samples);
    samples = NULL;
}
