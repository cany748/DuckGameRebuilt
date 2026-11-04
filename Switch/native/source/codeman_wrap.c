#include <stdbool.h>
#include <stdint.h>

// mono-nx translates every "enable write" request to the RW alias of a JIT area and asserts when the
// address isn't one. In mixed AOT+interpreter mode the runtime also patches plain heap memory this
// way (e.g. switch jump tables for AOT code), which is already writable and must be used as-is.
// Linked with -Wl,--wrap for both functions.

bool nx_address_in_heap(const void *address);

uint8_t *__real_mono_codeman_enable_write_ex(void *address, const char *trace_line);
uint8_t *__real_mono_codeman_disable_write_ex(void *address, const char *trace_line);

uint8_t *__wrap_mono_codeman_enable_write_ex(void *address, const char *trace_line)
{
    if (nx_address_in_heap(address))
        return (uint8_t *)address;
    return __real_mono_codeman_enable_write_ex(address, trace_line);
}

uint8_t *__wrap_mono_codeman_disable_write_ex(void *address, const char *trace_line)
{
    if (nx_address_in_heap(address))
        return (uint8_t *)address;
    return __real_mono_codeman_disable_write_ex(address, trace_line);
}
