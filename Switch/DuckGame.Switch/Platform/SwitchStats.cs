using System;
using System.Diagnostics;
using System.Globalization;

namespace DuckGame
{
    // Performance overlay shown by the "Show FPS" option on Switch.
    // update: Update() start -> Draw() start (all updates of the frame)
    // draw:   Draw() start -> Draw() end (CPU time spent building the frame)
    // wait:   Draw() end -> next Update() (Present, vsync and GPU waits)
    public static class SwitchStats
    {
        private const double Smoothing = 0.1;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double _updateStart = -1, _drawStart = -1, _drawEnd = -1;
        private static double _updateMs, _drawMs, _waitMs;
        private static BitmapFont _font;

        private static double Now => Clock.Elapsed.TotalMilliseconds;

        private static void Average(ref double value, double sample) => value += (sample - value) * Smoothing;

        public static void UpdateStart()
        {
            double now = Now;
            if (_drawEnd >= 0 && _updateStart < _drawEnd)
                Average(ref _waitMs, now - _drawEnd);
            if (_updateStart < _drawStart || _updateStart < 0)
                _updateStart = now;
        }

        public static void DrawStart()
        {
            double now = Now;
            if (_updateStart >= 0 && _updateStart > _drawStart)
                Average(ref _updateMs, now - _updateStart);
            _drawStart = now;
        }

        public static void DrawEnd()
        {
            _drawEnd = Now;
            Average(ref _drawMs, _drawEnd - _drawStart);
        }

        public static void Draw()
        {
            _font ??= new BitmapFont("biosFont", 8) { scale = new Vec2(2f, 2f) };
            string[] lines =
            {
                Format("{0} FPS  {1} UPS", FPSCounter.GetFPS(1), FPSCounter.GetFPS(0)),
                Format("UPD {0:0.0}  DRW {1:0.0}  WAIT {2:0.0} ms", _updateMs, _drawMs, _waitMs),
                Format("{0}x{1} {2}  GC {3} MB", Graphics.width, Graphics.height, SwitchDisplay.Docked ? "DOCK" : "HANDHELD", GC.GetTotalMemory(false) / (1024 * 1024)),
            };
            for (int i = 0; i < lines.Length; i++)
                _font.DrawOutline(lines[i], new Vec2(8f, 8f + i * 20f), Color.White, Color.Black, 1.1f);
        }

        private static string Format(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
