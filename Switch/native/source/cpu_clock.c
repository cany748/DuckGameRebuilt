#include <switch.h>
#include <stdbool.h>

/* The system runs games at 1020 MHz, but 1785 MHz is the CPU's stock maximum: Horizon itself uses it
 * while games load. Horizon resets the rate on mode changes, so the game re-applies it periodically. */
#define BOOST_HZ 1785000000u

static ClkrstSession session;
static bool session_open;
static u32 original_hz;

static bool open_session(void)
{
    if (session_open)
        return true;
    if (hosversionBefore(8, 0, 0) || R_FAILED(clkrstInitialize()))
        return false;
    if (R_FAILED(clkrstOpenSession(&session, PcvModuleId_CpuBus, 3)))
    {
        clkrstExit();
        return false;
    }
    if (R_FAILED(clkrstGetClockRate(&session, &original_hz)))
        original_hz = 0;
    session_open = true;
    return true;
}

/* Returns the current rate in MHz, 0 when the clock can't be controlled. */
int switch_cpu_boost(int enabled)
{
    if (!open_session())
        return 0;
    u32 wanted = enabled ? BOOST_HZ : original_hz;
    u32 current = 0;
    if (R_FAILED(clkrstGetClockRate(&session, &current)))
        return 0;
    if (wanted != 0 && current != wanted && R_SUCCEEDED(clkrstSetClockRate(&session, wanted)))
        current = wanted;
    return (int)(current / 1000000);
}

void switch_cpu_boost_shutdown(void)
{
    if (!session_open)
        return;
    if (original_hz != 0)
        clkrstSetClockRate(&session, original_hz);
    clkrstCloseSession(&session);
    clkrstExit();
    session_open = false;
}
