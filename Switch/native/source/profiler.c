#include <switch.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Sampling profiler for the render thread: Horizon lets a process pause its own threads and read their
 * registers, which works on console and in emulators. Managed addresses are named by mono_pmip when
 * the report is written, native ones are left for `nm` on the elf (offsets from the start of .text). */
#define PERIOD_NS 1000000
#define MAX_SAMPLES 70000
#define MAX_FRAMES 16
#define ADDRESS_SLOTS (1 << 18)
#define SAMPLER_STACK (64 * 1024)
#define SAMPLER_PRIORITY 0x20
#define SAMPLER_CORE 1
#define REPORT_LINES 120

/* frames: return addresses found by walking the frame pointer chain (Mono's AOT code and devkitA64's
 * code keep x29 as a frame pointer), for the inclusive report. */
typedef struct
{
    u64 pc, lr;
    u64 frames[MAX_FRAMES];
    int frame_count;
} Sample;

typedef struct
{
    char *name;
    int self, caller, inclusive, last_sample;
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
            Sample *sample = &samples[sample_count];
            sample->pc = context.pc.x;
            sample->lr = context.lr;
            sample->frame_count = 0;
            /* The chain lives on the thread's stack: stay inside the mapping that holds sp. */
            MemoryInfo info;
            u32 page;
            if (R_SUCCEEDED(svcQueryMemory(&info, &page, context.sp)))
            {
                u64 low = context.sp, high = info.addr + info.size;
                u64 fp = context.fp;
                while (sample->frame_count < MAX_FRAMES && fp >= low && fp + 16 <= high && (fp & 15) == 0)
                {
                    u64 *record = (u64 *)fp;
                    sample->frames[sample->frame_count++] = record[1];
                    if (record[0] <= fp)
                        break;
                    fp = record[0];
                }
            }
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

static char *name_of(u64 address);

static Entry *find_entry(Entry *entries, int *count, char *name)
{
    for (int i = 0; i < *count; i++)
        if (strcmp(entries[i].name, name) == 0)
        {
            free(name);
            return &entries[i];
        }
    entries[*count] = (Entry){name, 0, 0, 0, -1};
    return &entries[(*count)++];
}

/* Address -> entry, so each distinct address is named (mono_pmip) and searched for only once. */
static u64 *slot_address;
static int *slot_entry;

static Entry *entry_of(Entry *entries, int *count, u64 address)
{
    u32 slot = (u32)((address >> 2) * 2654435761u) & (ADDRESS_SLOTS - 1);
    while (slot_address[slot] != 0 && slot_address[slot] != address)
        slot = (slot + 1) & (ADDRESS_SLOTS - 1);
    if (slot_address[slot] == 0)
    {
        slot_address[slot] = address;
        slot_entry[slot] = (int)(find_entry(entries, count, name_of(address)) - entries);
    }
    return &entries[slot_entry[slot]];
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
static int by_inclusive(const void *a, const void *b) { return ((const Entry *)b)->inclusive - ((const Entry *)a)->inclusive; }

/* Counts a method once per sample however often it is on the stack. */
static void count_inclusive(Entry *entry, int sample)
{
    if (entry->last_sample == sample)
        return;
    entry->last_sample = sample;
    entry->inclusive++;
}

/* Stops sampling and writes the report. */
void switch_profiler_stop(const char *path)
{
    if (!running)
        return;
    running = false;
    threadWaitForExit(&sampler);
    threadClose(&sampler);

    int count = sample_count, unique = 0;
    Entry *entries = calloc(ADDRESS_SLOTS, sizeof(Entry));
    slot_address = calloc(ADDRESS_SLOTS, sizeof(u64));
    slot_entry = calloc(ADDRESS_SLOTS, sizeof(int));
    for (int i = 0; i < count; i++)
    {
        Entry *leaf = entry_of(entries, &unique, samples[i].pc);
        leaf->self++;
        count_inclusive(leaf, i);
        Entry *caller = entry_of(entries, &unique, samples[i].lr);
        caller->caller++;
        count_inclusive(caller, i);
        for (int f = 0; f < samples[i].frame_count; f++)
            count_inclusive(entry_of(entries, &unique, samples[i].frames[f]), i);
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
        fprintf(file, "\ninclusive%% method (on the stack, through the frame pointer chain, %d frames deep)\n", MAX_FRAMES);
        qsort(entries, unique, sizeof(Entry), by_inclusive);
        for (int i = 0; i < unique && i < REPORT_LINES * 2; i++)
            fprintf(file, "%5.1f %s\n", entries[i].inclusive * 100.0 / count, entries[i].name);
        fclose(file);
    }
    for (int i = 0; i < unique; i++)
        free(entries[i].name);
    free(entries);
    free(slot_address);
    free(slot_entry);
    free(samples);
    samples = NULL;
}
