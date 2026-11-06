using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace DuckGame
{
    // Performance overlay shown by the "Show FPS" option on Switch. It's one cheap line unless "Frame
    // Stats" is on: the full breakdown costs ~1ms a frame by itself (timers, one sprite per character).
    // UPD:  Update() start -> Draw() start (all updates of the frame)
    // DRW:  Draw() start -> Draw() end (CPU time spent building the frame)
    // WAIT: Draw() end -> next Update() (Present, vsync and GPU waits)
    // GL:   FNA3D calls made by the frame (native side, see Switch/native/source/gpu_stats.c)
    public static class SwitchStats
    {
        private const double Smoothing = 0.1;
        private const double MissedVsyncMs = 20;
        private const int LineCount = 8;
        // One line is rebuilt per refresh, so formatting never lands on a single frame.
        private const double LineRefreshMs = 30;

        public static bool detailed;

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double _updateStart = -1, _drawStart = -1, _drawEnd = -1;
        private static double _updateMs, _drawMs, _waitMs;
        private static readonly float[] Gpu = new float[12];
        private static readonly int[] Audio = new int[4];
        private static BitmapFont _font;
        private static bool _native = true;
        private static readonly Dictionary<string, double> Sections = new Dictionary<string, double>();
        private static readonly Dictionary<string, double> SectionAverages = new Dictionary<string, double>();
        private static readonly Dictionary<string, string> GapNames = new Dictionary<string, string>();
        private static readonly List<string> SectionNames = new List<string>();
        private static string _topSections = "", _topSections2 = "";
        private static readonly string[] Lines = new string[LineCount];
        private static string _compactLine = "";
        private static int _nextLine, _missed, _missedShown;
        private static double _missedWindowStart;
        private static double _sectionsMs, _otherMs, _frameStart = -1, _worstFrame, _worstShown;
        // Update and draw of the frame before the slowest interval, to tell what made it miss.
        private static double _lastUpdateMs, _lastDrawMs, _worstUpdate, _worstDraw;
        // Time between sections is attributed to the next section or Mark() as ">name", so every
        // millisecond of Draw() lands somewhere.
        private static double _boundary, _pendingGap;
        private static double _linesBuilt = double.MinValue, _overlayMs;

        private static bool Collecting => detailed && DevConsole.showFPS;

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
            if (_frameStart >= 0)
            {
                double interval = now - _frameStart;
                if (interval > MissedVsyncMs)
                    ++_missed;
                if (interval > _worstFrame)
                {
                    _worstFrame = interval;
                    _worstUpdate = _lastUpdateMs;
                    _worstDraw = _lastDrawMs;
                }
            }
            _frameStart = now;
            // Layers drawn outside Draw() (e.g. the pause screen capture during Update) don't count.
            Sections.Clear();
            _boundary = now;
            if (_updateStart >= 0 && _updateStart > _drawStart)
            {
                _lastUpdateMs = now - _updateStart;
                Average(ref _updateMs, _lastUpdateMs);
            }
            _drawStart = now;
        }

        public static void DrawEnd()
        {
            _drawEnd = Now;
            _lastDrawMs = _drawEnd - _drawStart;
            Average(ref _drawMs, _lastDrawMs);
            if (!Collecting)
                return;
            Mark("end");
            double sections = 0;
            foreach (KeyValuePair<string, double> section in Sections)
            {
                sections += section.Value;
                SectionAverages.TryGetValue(section.Key, out double average);
                SectionAverages[section.Key] = average + (section.Value - average) * Smoothing;
            }
            // Sections that weren't drawn this frame decay toward zero.
            SectionNames.Clear();
            SectionNames.AddRange(SectionAverages.Keys);
            foreach (string name in SectionNames)
                if (!Sections.ContainsKey(name))
                    SectionAverages[name] *= 1 - Smoothing;
            Average(ref _sectionsMs, sections);
            _otherMs = _drawMs - _sectionsMs - _overlayMs;
            Sections.Clear();
        }

        // Per-frame time of a named part of the draw (layers, level); the top ones are shown twice a second.
        public static double SectionStart()
        {
            if (!Collecting)
                return 0;
            double now = Now;
            _pendingGap = now - _boundary;
            _boundary = now;
            return now;
        }

        // Closes the current gap: the time since the last section or mark is reported as ">name".
        public static void Mark(string name)
        {
            if (!Collecting)
                return;
            double now = Now;
            AddSection(GapName(name), now - _boundary);
            _boundary = now;
        }

        private static string GapName(string name)
        {
            if (!GapNames.TryGetValue(name, out string gap))
                GapNames[name] = gap = ">" + name;
            return gap;
        }

        private static void AddSection(string name, double elapsed)
        {
            if (elapsed <= 0)
                return;
            Sections.TryGetValue(name, out double total);
            Sections[name] = total + elapsed;
        }

        public static void SectionEnd(string name, double start)
        {
            if (!Collecting)
                return;
            name ??= "?";
            AddSection(GapName(name), _pendingGap);
            _pendingGap = 0;
            double elapsed = Now - start;
            // The overlay is drawn inside the console layer; keep it out of the layer numbers.
            if (name == "CONSOLE")
                elapsed -= _overlayMs;
            AddSection(name, elapsed);
            _boundary = Now;
        }

        public static void Draw()
        {
            double start = Now;
            _font ??= new BitmapFont("biosFont", 8) { scale = new Vec2(1.5f, 1.5f) };
            SwitchBench.RunOnce(start, _font);
            if (start - _missedWindowStart >= 1000)
            {
                _missedWindowStart = start;
                _missedShown = _missed;
                _missed = 0;
                _compactLine = Format("{0} FPS  {1} UPS  miss {2}", FPSCounter.GetFPS(1), FPSCounter.GetFPS(0), _missedShown);
            }
            if (!detailed)
            {
                _font.Draw(_compactLine, new Vec2(8f, 8f), Color.White, 1.1f);
                return;
            }
            if (start - _linesBuilt > LineRefreshMs)
            {
                _linesBuilt = start;
                Lines[_nextLine] = BuildLine(_nextLine);
                _nextLine = (_nextLine + 1) % LineCount;
            }
            int width = 0;
            foreach (string line in Lines)
                width = Math.Max(width, line?.Length ?? 0);
            Graphics.DrawRect(new Vec2(4f, 4f), new Vec2(12f + width * 12f, 8f + LineCount * 15f), Color.Black * 0.6f, 1f);
            for (int i = 0; i < LineCount; i++)
                if (Lines[i] != null)
                    _font.Draw(Lines[i], new Vec2(8f, 8f + i * 15f), Color.White, 1.1f);
            Average(ref _overlayMs, Now - start);
        }

        private static void ReadNativeStats()
        {
            if (!_native)
                return;
            try
            {
                switch_gpu_stats(Gpu, Gpu.Length);
                switch_audio_stats(Audio);
            }
            catch (Exception e) when (e is DllNotFoundException || e is EntryPointNotFoundException)
            {
                _native = false; // desktop benchmark build: no native counters
            }
        }

        private static string BuildLine(int index)
        {
            switch (index)
            {
                case 0:
                    return _compactLine;
                case 1:
                    return Format("UPD {0:0.0}  DRW {1:0.0}  WAIT {2:0.0}  (overlay {3:0.0}) ms", _updateMs, _drawMs - _overlayMs, _waitMs, _overlayMs);
                case 2:
                    string[] top = SectionAverages.OrderByDescending(x => x.Value).Take(6).Select(x => Format("{0} {1:0.0}", x.Key, x.Value)).ToArray();
                    _topSections = string.Join("  ", top.Take(3));
                    _topSections2 = string.Join("  ", top.Skip(3));
                    return "DRW: " + _topSections;
                case 3:
                    return "     " + _topSections2;
                case 4:
                    // Worst values since the previous cycle: stutters don't show in averages.
                    ReadNativeStats();
                    _worstShown = _worstFrame;
                    _worstFrame = 0;
                    return Format("worst {0:0.0}ms upd {1:0.0} drw {2:0.0}  GC {3} pauses {4:0.0}ms max {5:0.0}ms",
                        _worstShown, _worstUpdate, _worstDraw, Gpu[9], Gpu[8], Gpu[10]);
                case 5:
                    return Format("GL clear {7:0.0} swap {6:0.0}  {0} draws {1:0.0}ms  up {2} {3:0.0}ms  fx {4} rt {5}",
                        Gpu[0], Gpu[1], Gpu[2], Gpu[3], Gpu[4], Gpu[5], Gpu[6], Gpu[7]);
                case 6:
                    // Not GC.GetTotalMemory: sgen walks the whole major heap for it (30+ ms here).
                    return Format("{0}x{1} {2}  cpu {6}MHz  heap {3}MB  gen0 {4} gen1 {5}",
                        Graphics.width, Graphics.height, SwitchDisplay.Docked ? "DOCK" : "HANDHELD",
                        Gpu[11], GC.CollectionCount(0), GC.CollectionCount(1), SwitchDisplay.cpuMhz);
                default:
                    return Format("AUDIO {0} mixed {1} starved {2} {3}", AudioState(Audio[0]), Audio[1], Audio[2],
                        Audio[3] == 0 || !_native ? "" : Format("err {0} 0x{1:X}", Marshal.PtrToStringAnsi(switch_audio_error_step()), Audio[3]));
            }
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
