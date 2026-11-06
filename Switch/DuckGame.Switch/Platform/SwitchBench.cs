using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DuckGame
{
    // CPU benchmark started with -cpubench: fixed workloads timed on the render thread, independent of
    // vsync and of what's on screen, so builds can be compared in the throttled emulator or on console.
    public static class SwitchBench
    {
        private const double StartAfterMs = 40000;
        private const int Rounds = 3;

        private const double ProfileAfterMs = 60000;
        private const double ProfileMs = 30000;
        private const string ProfilePath = "/switch/DuckGame/profile.txt";

        [DllImport("__Internal")]
        private static extern int switch_profiler_start();

        [DllImport("__Internal")]
        private static extern void switch_profiler_stop([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        private static bool _done, _profiling, _profiled, _scanned;
        private static double _profileStart;
        private static long _sink;

        public static void RunOnce(double clockMs, BitmapFont font)
        {
            Profile(clockMs);
            ScanLevels(clockMs);
            if (_done || clockMs < StartAfterMs || !Program.commandLine.Contains("-cpubench"))
                return;
            _done = true;
            for (int round = 0; round < Rounds; round++)
            {
                Time("sprites 5000", () => Sprites(font));
                Time("list 1M", ListLoop);
                Time("dictionary 100k", DictionaryLoop);
                Time("hashset 1M", HashSetLoop);
                Time("span 1M", SpanLoop);
                Time("math 500k", MathLoop);
                Time("strings 20k", Strings);
                Time("collision 2000", Collision);
            }
            Console.WriteLine("BENCH done " + _sink);
        }

        // -profile: samples the render thread for 30 seconds and writes profile.txt next to the nro.
        private static void Profile(double clockMs)
        {
            if (_profiled || clockMs < ProfileAfterMs || !Program.commandLine.Contains("-profile"))
                return;
            if (!_profiling)
            {
                _profiling = switch_profiler_start() != 0;
                _profileStart = clockMs;
                _profiled = !_profiling;
            }
            else if (clockMs - _profileStart > ProfileMs)
            {
                switch_profiler_stop(ProfilePath);
                _profiled = true;
                Console.WriteLine("PROFILE written");
            }
        }

        // -levscan: reports what every custom level needs: mods, and objects this build doesn't know.
        private static void ScanLevels(double clockMs)
        {
            if (_scanned || clockMs < StartAfterMs || !Program.commandLine.Contains("-levscan"))
                return;
            _scanned = true;
            foreach (string path in System.IO.Directory.GetFiles(DuckFile.levelDirectory, "*.lev", System.IO.SearchOption.AllDirectories))
            {
                try
                {
                    LevelData data = DuckFile.LoadLevel(path);
                    int unknown = 0, total = 0;
                    HashSet<string> missing = new HashSet<string>();
                    foreach (BinaryClassChunk chunk in data.objects.objects)
                    {
                        ++total;
                        if (Thing.LoadThing(chunk, false) == null)
                        {
                            ++unknown;
                            missing.Add(chunk.GetProperty<string>("type") ?? "?");
                        }
                    }
                    Console.WriteLine("LEVSCAN " + System.IO.Path.GetFileName(path) + " local=" + data.modData.hasLocalMods
                        + " ws=" + string.Join(",", data.modData.workshopIDs) + " art=" + data.metaData.hasCustomArt
                        + " objects=" + total + " unknown=" + unknown + " " + string.Join(",", missing));
                }
                catch (Exception e)
                {
                    Console.WriteLine("LEVSCAN " + System.IO.Path.GetFileName(path) + " FAILED " + e.GetType().Name + " " + e.Message);
                }
            }
            Console.WriteLine("LEVSCAN done");
        }

        private static void Time(string name, Action action)
        {
            Stopwatch watch = Stopwatch.StartNew();
            action();
            Console.WriteLine("BENCH " + name + " " + watch.Elapsed.TotalMilliseconds.ToString("0.0") + " ms");
        }

        private static void Sprites(BitmapFont font)
        {
            string text = new string('x', 100);
            for (int i = 0; i < 50; i++)
                font.Draw(text, new Vec2(-2000f, 8f), Color.White, 0.5f);
        }

        private static void ListLoop()
        {
            List<int> list = new List<int>();
            for (int i = 0; i < 1000; i++)
                list.Add(i);
            for (int pass = 0; pass < 1000; pass++)
                for (int i = 0; i < list.Count; i++)
                    _sink += list[i];
        }

        private static void DictionaryLoop()
        {
            Dictionary<string, int> map = new Dictionary<string, int>();
            string[] keys = new string[64];
            for (int i = 0; i < keys.Length; i++)
                map[keys[i] = "sprite_name_" + i] = i;
            for (int i = 0; i < 100000; i++)
                _sink += map[keys[i & 63]];
        }

        private static void HashSetLoop()
        {
            HashSet<object> set = new HashSet<object>();
            for (int i = 0; i < 1000; i++)
                set.Add(new object());
            for (int pass = 0; pass < 1000; pass++)
                foreach (object item in set)
                    _sink += item == null ? 1 : 0;
        }

        private static void SpanLoop()
        {
            byte[] data = new byte[1000];
            for (int pass = 0; pass < 1000; pass++)
            {
                Span<byte> span = data;
                for (int i = 0; i < span.Length; i++)
                    span[i] += 1;
            }
            _sink += data[0];
        }

        private static void MathLoop()
        {
            float total = 0f;
            for (int i = 0; i < 500000; i++)
                total += (float)Math.Sin(i * 0.01f) + Math.Abs(total) * 0.001f + Math.Min(i, 3f);
            _sink += (long)total;
        }

        private static void Strings()
        {
            for (int i = 0; i < 20000; i++)
                _sink += ("name" + (i & 7)).GetHashCode();
        }

        private static void Collision()
        {
            for (int i = 0; i < 2000; i++)
                _sink += Level.CheckRect<Block>(new Vec2(i % 300, 40f), new Vec2(i % 300 + 32f, 72f)) == null ? 0 : 1;
        }
    }
}
