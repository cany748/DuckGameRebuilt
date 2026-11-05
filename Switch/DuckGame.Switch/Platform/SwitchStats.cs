using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DuckGame
{
    // Performance overlay shown by the "Show FPS" option on Switch.
    // UPD:  Update() start -> Draw() start (all updates of the frame)
    // DRW:  Draw() start -> Draw() end (CPU time spent building the frame)
    // WAIT: Draw() end -> next Update() (Present, vsync and GPU waits)
    // GL:   FNA3D calls made by the frame (native side, see Switch/native/source/gpu_stats.c)
    public static class SwitchStats
    {
        private const double Smoothing = 0.1;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double _updateStart = -1, _drawStart = -1, _drawEnd = -1;
        private static double _updateMs, _drawMs, _waitMs;
        private static readonly float[] Gpu = new float[7];
        private static readonly int[] Audio = new int[4];
        private static BitmapFont _font;
        private static readonly Dictionary<string, double> Sections = new Dictionary<string, double>();
        private static string _topSections = "";
        private static string[] _lines;
        private static double _linesBuilt, _overlayMs;

        [DllImport("__Internal")]
        private static extern void switch_gpu_stats(float[] output, int count);

        [DllImport("__Internal")]
        private static extern void switch_audio_stats(int[] output);

        [DllImport("__Internal")]
        private static extern IntPtr switch_audio_error_step();

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
            if (Sections.Count > 0 && FPSCounter.GetFPS(1) > 0 && (int)_drawEnd / 500 != (int)_drawStart / 500)
                _topSections = string.Join("  ", Sections.OrderByDescending(s => s.Value).Take(4).Select(s => Format("{0} {1:0.0}", s.Key, s.Value)));
            Sections.Clear();
        }

        // Per-frame time of a named part of the draw (layers, level); the top ones are shown twice a second.
        public static double SectionStart() => Now;

        public static void SectionEnd(string name, double start)
        {
            name ??= "?";
            double elapsed = Now - start;
            // The overlay is drawn inside the console layer; keep it out of the layer numbers.
            if (name == "CONSOLE")
                elapsed -= _overlayMs;
            Sections.TryGetValue(name, out double total);
            Sections[name] = total + elapsed;
        }

        public static void Draw()
        {
            double start = Now;
            _font ??= new BitmapFont("biosFont", 8) { scale = new Vec2(1.5f, 1.5f) };
            // Text is rebuilt 4 times a second and drawn without outline: the overlay must not
            // distort what it measures (an outlined line is 9 sprites per character).
            if (_lines == null || start - _linesBuilt > 250)
            {
                _linesBuilt = start;
                _lines = BuildLines();
            }
            float width = 0f;
            foreach (string line in _lines)
                width = Math.Max(width, line.Length * 8f * 1.5f);
            Graphics.DrawRect(new Vec2(4f, 4f), new Vec2(12f + width, 8f + _lines.Length * 15f), Color.Black * 0.6f, 1f);
            for (int i = 0; i < _lines.Length; i++)
                _font.Draw(_lines[i], new Vec2(8f, 8f + i * 15f), Color.White, 1.1f);
            _overlayMs = Now - start;
        }

        private static string[] BuildLines()
        {
            switch_gpu_stats(Gpu, Gpu.Length);
            switch_audio_stats(Audio);
            return new[]
            {
                Format("{0} FPS  {1} UPS", FPSCounter.GetFPS(1), FPSCounter.GetFPS(0)),
                Format("UPD {0:0.0}  DRW {1:0.0}  WAIT {2:0.0}  (overlay {3:0.0}) ms", _updateMs, _drawMs - _overlayMs, _waitMs, _overlayMs),
                "DRW: " + _topSections,
                Format("GL {0} draws {1:0.0}ms  up {2} {3:0.0}ms  fx {4} rt {5}  swap {6:0.0}ms",
                    Gpu[0], Gpu[1], Gpu[2], Gpu[3], Gpu[4], Gpu[5], Gpu[6]),
                Format("{0}x{1} {2}  GC {3}MB  gen0 {4} gen1 {5}",
                    Graphics.width, Graphics.height, SwitchDisplay.Docked ? "DOCK" : "HANDHELD",
                    GC.GetTotalMemory(false) / (1024 * 1024), GC.CollectionCount(0), GC.CollectionCount(1)),
                Format("AUDIO {0} mixed {1} starved {2} {3}", AudioState(Audio[0]), Audio[1], Audio[2],
                    Audio[3] == 0 ? "" : Format("err {0} 0x{1:X}", Marshal.PtrToStringAnsi(switch_audio_error_step()), Audio[3])),
            };
        }

        private static string AudioState(int state) => state switch
        {
            1 => "audren",
            2 => "sdl",
            3 => "failed",
            _ => "off",
        };

        private static string Format(string format, params object[] args) => string.Format(CultureInfo.InvariantCulture, format, args);
    }
}
