using System;
using System.Runtime.InteropServices;

namespace DuckGame
{
    // Renders at the panel's real resolution: 1280x720 handheld, 1920x1080 docked.
    // SDL2 for Switch always reports a 1920x1080 display mode, and a fullscreen FNA window keeps that
    // size, so handheld mode used to draw 2.25x the pixels and have the system downscale them.
    // A windowed SDL window maps straight to nwindowSetDimensions, so the backbuffer follows it.
    public static class SwitchDisplay
    {
        private const int AppletOperationModeHandheld = 0;

        [DllImport("libnx")]
        private static extern int appletGetOperationMode();

        [DllImport("__Internal")]
        private static extern int switch_cpu_boost(int enabled);

        // Horizon resets the clock on mode changes, so it's checked again now and then.
        private const int ClockCheckFrames = 120;

        public static bool cpuBoost = true;
        public static int cpuMhz;
        private static int _clockCheckIn;
        private static int _mode = -1;
        private static bool _available = true;

        public static bool Docked => _mode != AppletOperationModeHandheld;

        // Called from Resolution.Update() every frame; queues a resolution change when the dock state changes.
        public static void Update()
        {
            if (!_available)
                return;
            int mode;
            try
            {
                mode = appletGetOperationMode();
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                // Not running on Switch (e.g. the desktop benchmark build): keep the desktop resolution.
                _available = false;
                return;
            }
            if (--_clockCheckIn <= 0)
            {
                _clockCheckIn = ClockCheckFrames;
                cpuMhz = switch_cpu_boost(cpuBoost ? 1 : 0);
            }
            if (mode == _mode)
                return;
            _mode = mode;

            int width = Docked ? 1920 : 1280;
            int height = Docked ? 1080 : 720;
            Resolution current = Options.LocalData.currentResolution;
            if (current != null && current.x == width && current.y == height && current.mode == ScreenMode.Windowed)
                return;
            Resolution.Set(new Resolution { dimensions = new Vec2(width, height), mode = ScreenMode.Windowed });
        }
    }
}
