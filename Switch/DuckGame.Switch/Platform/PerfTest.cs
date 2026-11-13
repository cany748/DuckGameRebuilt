using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace DuckGame
{
    // -perftest: plays a fixed list of scenes with eight bot ducks and a fixed random seed, and prints one
    // PERF line per scene. Rounds restart on the same level (the editor's test mode), so a scene keeps
    // running until its frame budget is spent. -perfscan measures every custom level briefly instead.
    public static class PerfTest
    {
        private const int Seed = 12345;
        private const int TitleFrames = 120;
        private const int WarmupFrames = 300;
        private const int LoadTimeoutFrames = 3600;
        // Updates right after a round restart are level loading, not gameplay.
        private const int RestartSkipFrames = 30;
        private const double FrameBudgetMs = 1000.0 / 60;

        public static readonly List<KeyValuePair<string, Func<Level>>> Scenes = new List<KeyValuePair<string, Func<Level>>>();

        private static bool? _enabled;
        private static bool _scan, _running, _finished, _verify, _verifyAll, _profile, _native = true;
        private static uint _soundHash;
        private static int[] _pixels;
        private static int _pictureFrame;
        private static int _sounds;
        private static int _titleFrames, _scene = -1, _sceneFrames, _measured, _measureFrames, _skip, _rounds;
        private static Level _lastLevel;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static double _updateStart = -1, _drawStart = -1, _lastUpdateMs;
        private static readonly List<double> UpdateMs = new List<double>(), DrawMs = new List<double>();
        private static int _over, _gen0, _gen1;
        private static long _allocated;
        private static readonly float[] Gpu = new float[12];
        private static double _glDraws, _glUploads, _glTargets;
        private static int _glFrames;
        private static readonly PerfBot[] Bots = new PerfBot[8];
        private static readonly Random DrawRandom = new Random(Seed);
        private static Random _updateRandom;

        [DllImport("__Internal")]
        private static extern void switch_gpu_stats(float[] output, int count);

        [DllImport("__Internal")]
        private static extern int switch_profiler_start();

        [DllImport("__Internal")]
        private static extern void switch_profiler_stop([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        public static bool Enabled => _enabled ??= Program.commandLine.Contains("-perftest") || Program.commandLine.Contains("-perfscan");

        public static bool Measuring => _running && _sceneFrames > WarmupFrames && _skip == 0;

        public static string SceneName => _scene >= 0 && _scene < Scenes.Count ? Scenes[_scene].Key : "";

        public static int MeasuredFrame => _measured;

        public static void SoundStarted(string name, float volume, float pitch)
        {
            if (!_verify || !Measuring)
                return;
            _soundHash = Mix(Mix(Mix(_soundHash, (uint)name.GetStableHash()), Bits(volume)), Bits(pitch));
            ++_sounds;
        }

        private static uint Bits(float value) => (uint)BitConverter.SingleToInt32Bits(value);

        private static uint Mix(uint hash, uint value)
        {
            for (int i = 0; i < 4; i++)
            {
                hash = (hash ^ (value & 0xff)) * 16777619u;
                value >>= 8;
            }
            return hash;
        }

        private static int GetStableHash(this string text)
        {
            uint hash = 2166136261u;
            foreach (char c in text)
                hash = (hash ^ c) * 16777619u;
            return (int)hash;
        }

        // Order-independent: the per-thing hashes are summed, so the container's iteration order
        // doesn't matter.
        private static void Verify(Level level)
        {
            uint world = 0;
            int things = 0;
            foreach (Thing thing in level.things)
            {
                uint hash = (uint)thing.GetType().Name.GetStableHash();
                hash = Mix(Mix(Mix(hash, Bits(thing.x)), Bits(thing.y)), Bits(thing.angle));
                hash = Mix(Mix(hash, Bits(thing.hSpeed)), Bits(thing.vSpeed));
                if (thing is Duck duck)
                    hash = Mix(Mix(hash, duck.dead ? 1u : 0u), duck.holdObject == null ? 0u : (uint)duck.holdObject.GetType().Name.GetStableHash());
                world += hash;
                ++things;
                if (_verifyAll)
                    Console.WriteLine("THING " + _measured + " " + thing.GetType().Name + " " + thing.x.ToString("R", CultureInfo.InvariantCulture) + " " + thing.y.ToString("R", CultureInfo.InvariantCulture) + " " + hash.ToString("x8"));
            }
            if (_verifyAll)
            {
                lock (PerfTracingRandom.Callers)
                {
                    foreach (KeyValuePair<string, int> caller in PerfTracingRandom.Callers)
                        Console.WriteLine("RAND " + _measured + " " + caller.Key + " " + caller.Value);
                    PerfTracingRandom.Callers.Clear();
                }
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "VERIFY {0} {1} world={2:x8} sound={3:x8} things={4} sounds={5}",
                SceneName, _measured, world, _soundHash, things, _sounds));
        }

        // The finished backbuffer of two fixed frames per scene, so a change that only affects drawing shows
        // up as well.
        private static void VerifyPicture()
        {
            _pictureFrame = _measured;
            Microsoft.Xna.Framework.Graphics.GraphicsDevice device = Graphics.device;
            int width = device.PresentationParameters.BackBufferWidth, height = device.PresentationParameters.BackBufferHeight;
            if (_pixels == null || _pixels.Length != width * height)
                _pixels = new int[width * height];
            device.GetBackBufferData(_pixels);
            uint hash = 2166136261u;
            foreach (int pixel in _pixels)
                hash = (hash ^ (uint)pixel) * 16777619u;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "VERIFY {0} {1} picture={2:x8}", SceneName, _measured, hash));
        }

        private static double Now => Clock.Elapsed.TotalMilliseconds;

        public static void UpdateStart()
        {
            if (!Enabled || _finished)
                return;
            double now = Now;
            CloseUpdate(now);
            _updateStart = now;
            if (!_running)
            {
                if (!(Level.current is TitleScreen) || ++_titleFrames < TitleFrames)
                    return;
                Begin();
            }
            // Sprite animations advance when drawn, so the simulation depends on how updates and draws
            // interleave. Dropping the time still owed after this update gives every drawn frame exactly
            // one update: a slow frame slows the game down instead of being caught up.
            Program.SetAccumulatedElapsedTime(Program.main, TimeSpan.Zero);
            Step();
        }

        public static void DrawStart()
        {
            if (!Enabled || _finished)
                return;
            double now = Now;
            CloseUpdate(now);
            _drawStart = now;
            // Drawing takes random numbers too (flicker, shake). It gets its own generator, so the
            // simulation doesn't depend on how many frames were drawn between two updates.
            _updateRandom = Rando.generator;
            Rando.generator = DrawRandom;
        }

        public static void DrawEnd()
        {
            if (!Enabled || _finished || _drawStart < 0)
                return;
            if (_updateRandom != null)
                Rando.generator = _updateRandom;
            _updateRandom = null;
            double drawMs = Now - _drawStart;
            _drawStart = -1;
            if (!Measuring)
                return;
            DrawMs.Add(drawMs);
            if (_verify && (_measured == 600 || _measured == 1500) && _pictureFrame != _measured)
                VerifyPicture();
            if (_lastUpdateMs + drawMs > FrameBudgetMs)
                ++_over;
            ReadGl();
        }

        private static void CloseUpdate(double now)
        {
            if (_updateStart < 0)
                return;
            _lastUpdateMs = now - _updateStart;
            _updateStart = -1;
            if (Measuring)
                UpdateMs.Add(_lastUpdateMs);
        }

        private static void ReadGl()
        {
            if (!_native)
                return;
            try
            {
                switch_gpu_stats(Gpu, Gpu.Length);
                _glDraws += Gpu[0];
                _glUploads += Gpu[2];
                _glTargets += Gpu[5];
                ++_glFrames;
            }
            catch (EntryPointNotFoundException)
            {
                _native = false;
            }
            catch (DllNotFoundException)
            {
                _native = false;
            }
        }

        private static void Begin()
        {
            _running = true;
            _scan = Program.commandLine.Contains("-perfscan");
            _verify = Program.commandLine.Contains("-verify");
            if (_verify)
                DevConsole.showFPS = false;
            _verifyAll = Program.commandLine.Contains("-verifyall");
            _measureFrames = _scan ? 300 : 1800;
            for (int i = 0; i < Bots.Length; i++)
            {
                Bots[i] = new PerfBot();
                Profiles.defaultProfiles[i].team = Teams.all[i];
            }
            if (_scan)
            {
                string[] files = Directory.GetFiles(DuckFile.levelDirectory, "*.lev", SearchOption.AllDirectories);
                Array.Sort(files, StringComparer.Ordinal);
                foreach (string file in files)
                {
                    string relative = file.Substring(DuckFile.levelDirectory.Length);
                    Scenes.Add(new KeyValuePair<string, Func<Level>>(Path.GetFileNameWithoutExtension(file).Replace(' ', '_'), () => Custom(relative)));
                }
            }
            else
            {
                Scenes.Insert(0, new KeyValuePair<string, Func<Level>>("control", () => Stock("city01")));
                Scenes.Insert(1, new KeyValuePair<string, Func<Level>>(PerfRoom.SceneName, PerfRoom.Create));
            }
            // -perfscene a,b keeps only the named scenes.
            string only = Argument("-perfscene");
            if (only != null)
            {
                string[] names = only.Split(',');
                Scenes.RemoveAll(scene => Array.IndexOf(names, scene.Key) < 0);
            }
            _profile = Program.commandLine.Contains("-perfprofile");
            NextScene();
        }

        private static string Argument(string name)
        {
            string[] parts = Program.commandLine.Split(' ');
            int index = Array.IndexOf(parts, name);
            return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : null;
        }

        private static Level Stock(string name)
        {
            LevelData data = DuckFile.LoadLevel(Content.path + "/levels/deathmatch/" + name + ".lev");
            return new GameLevel(data.metaData.guid, editorTestMode: true);
        }

        public static Level Custom(string relativePath)
        {
            if (DuckFile.LoadLevel(DuckFile.levelDirectory + relativePath) == null)
                throw new FileNotFoundException(relativePath);
            return new GameLevel(relativePath, editorTestMode: true);
        }

        private static void NextScene()
        {
            while (++_scene < Scenes.Count)
            {
                Rando.generator = Program.commandLine.Contains("-verifyall") ? new PerfTracingRandom(Seed) : new Random(Seed);
                // A sprite animates on its first drawn frame or on its second depending on this flag's
                // value when it was created; it flips every frame, so its phase is pinned here.
                Graphics.frameFlipFlop = false;
                _sceneFrames = _measured = _skip = _over = _rounds = _sounds = 0;
                _soundHash = 2166136261u;
                _pictureFrame = -1;
                _glDraws = _glUploads = _glTargets = _glFrames = 0;
                UpdateMs.Clear();
                DrawMs.Clear();
                foreach (PerfBot bot in Bots)
                    bot.Reset();
                try
                {
                    Level.current = Scenes[_scene].Value();
                    _lastLevel = null;
                    return;
                }
                catch (Exception e)
                {
                    Console.WriteLine("PERF " + SceneName + " failed " + e.GetType().Name);
                }
            }
            Console.WriteLine("PERF done");
            _finished = true;
            if (!Directory.Exists("/switch/DuckGame"))
                Environment.Exit(0);
            Level.current = new TitleScreen();
        }

        private static void Step()
        {
            Level level = Level.current;
            if (level is TitleScreen || Level.core.nextLevel != null)
            {
                if (++_skip > LoadTimeoutFrames)
                {
                    Console.WriteLine("PERF " + SceneName + " failed timeout");
                    NextScene();
                }
                return;
            }
            if (level != _lastLevel)
            {
                // A new round of the same scene: its things are new, the bots start over.
                _lastLevel = level;
                _skip = RestartSkipFrames;
                ++_rounds;
                foreach (PerfBot bot in Bots)
                    bot.Reset();
            }
            else if (level is GameLevel && !GameMode.started)
            {
                // The "get ready" countdown is idle time; only the fight is measured.
                _skip = Math.Max(_skip, 1);
            }
            else if (_skip > 0)
            {
                --_skip;
            }
            ++_sceneFrames;
            if (_sceneFrames == WarmupFrames + 1)
            {
                _gen0 = GC.CollectionCount(0);
                _gen1 = GC.CollectionCount(1);
                _allocated = GC.GetTotalAllocatedBytes(false);
                if (Program.commandLine.Contains("-perfalloc"))
                    PerfAllocations.Start();
                // -perfprofile: samples the main thread for the measured part of every scene.
                if (_profile)
                    _profile = _native && Directory.Exists("/switch/DuckGame") && switch_profiler_start() != 0;
            }
            if (SceneName == PerfRoom.SceneName)
                PerfRoom.Tick(level);
            DriveBots(level);
            if (_verify && Measuring && (_measured % 60 == 0 || _verifyAll))
                Verify(level);
            if (Measuring && ++_measured >= _measureFrames)
            {
                Report();
                NextScene();
            }
        }

        private static void DriveBots(Level level)
        {
            foreach (Thing thing in level.things[typeof(Duck)])
            {
                Duck duck = (Duck)thing;
                if (duck.profile == null)
                    continue;
                int index = Profiles.defaultProfiles.IndexOf(duck.profile);
                if (index < 0 || index >= Bots.Length)
                    continue;
                if (duck.VirtualInput != Bots[index])
                    duck.VirtualInput = Bots[index];
                Bots[index].Think(duck, level);
            }
        }

        private static void Report()
        {
            PerfAllocations.Report(SceneName);
            if (_profile)
                switch_profiler_stop("/switch/DuckGame/profile-" + SceneName + ".txt");
            UpdateMs.Sort();
            DrawMs.Sort();
            double update = Mean(UpdateMs), draw = Mean(DrawMs);
            int frames = Math.Max(_glFrames, 1);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "PERF {0} frames={1} rounds={19} upd={2:0.00}/{3:0.00}/{4:0.00}/{5:0.00}/{6:0.00} drw={7:0.00}/{8:0.00}/{9:0.00}/{10:0.00}/{11:0.00} sum={12:0.00}/{13:0.00} over16={14} gl={15} gc0={16} gc1={17} alloc={18:0.0}",
                SceneName, UpdateMs.Count,
                update, Percentile(UpdateMs, 0.5), Percentile(UpdateMs, 0.95), Percentile(UpdateMs, 0.99), Percentile(UpdateMs, 1),
                draw, Percentile(DrawMs, 0.5), Percentile(DrawMs, 0.95), Percentile(DrawMs, 0.99), Percentile(DrawMs, 1),
                update + draw, Percentile(UpdateMs, 0.95) + Percentile(DrawMs, 0.95), _over,
                _native && _glFrames > 0 ? string.Format(CultureInfo.InvariantCulture, "{0:0.0}/{1:0.0}/{2:0.0}", _glDraws / frames, _glUploads / frames, _glTargets / frames) : "n/a",
                GC.CollectionCount(0) - _gen0, GC.CollectionCount(1) - _gen1,
                (GC.GetTotalAllocatedBytes(false) - _allocated) / 1048576.0, _rounds));
        }

        private static double Mean(List<double> sorted)
        {
            if (sorted.Count == 0)
                return 0;
            double sum = 0;
            foreach (double value in sorted)
                sum += value;
            return sum / sorted.Count;
        }

        private static double Percentile(List<double> sorted, double fraction) =>
            sorted.Count == 0 ? 0 : sorted[Math.Min(sorted.Count - 1, (int)(sorted.Count * fraction))];
    }

    // Input of one bot duck: find a loaded gun, pick it up, walk at the nearest duck and shoot. It reads
    // only the level and Rando, so a run with the same seed plays the same way.
    public class PerfBot : InputProfile
    {
        private static readonly string[] Buttons = { Triggers.Left, Triggers.Right, Triggers.Jump, Triggers.Shoot, Triggers.Grab, Triggers.Down, Triggers.Quack };
        private const int Left = 0, Right = 1, Jump = 2, Shoot = 3, Grab = 4, Crouch = 5, Quack = 6;
        private const int RetargetFrames = 30;
        private const int StuckFrames = 20;
        private const int WanderFrames = 60;

        private readonly bool[] _want = new bool[Buttons.Length];
        private readonly InputState[] _state = new InputState[Buttons.Length];
        private Thing _target;
        private int _retarget, _stuck, _wander, _wanderDir, _jumpHold, _shootHold, _grabWait;

        public PerfBot() : base("")
        {
        }

        public void Reset()
        {
            _target = null;
            _retarget = _stuck = _wander = _jumpHold = _shootHold = _grabWait = 0;
            Array.Clear(_want, 0, _want.Length);
            Array.Clear(_state, 0, _state.Length);
        }

        private int Index(string trigger)
        {
            for (int i = 0; i < Buttons.Length; i++)
            {
                if (Buttons[i] == trigger)
                    return i;
            }
            return -1;
        }

        public override bool Pressed(string trigger, bool any = false)
        {
            int i = Index(trigger);
            return i >= 0 && _state[i] == InputState.Pressed;
        }

        public override bool Released(string trigger)
        {
            int i = Index(trigger);
            return i >= 0 && _state[i] == InputState.Released;
        }

        public override bool Down(string trigger)
        {
            int i = Index(trigger);
            return i >= 0 && (_state[i] == InputState.Pressed || _state[i] == InputState.Down);
        }

        public void Think(Duck duck, Level level)
        {
            Array.Clear(_want, 0, _want.Length);
            if (!duck.dead)
                Decide(duck, level);
            for (int i = 0; i < Buttons.Length; i++)
            {
                bool held = _state[i] == InputState.Pressed || _state[i] == InputState.Down;
                _state[i] = _want[i] ? (held ? InputState.Down : InputState.Pressed) : (held ? InputState.Released : InputState.None);
            }
        }

        private void Decide(Duck duck, Level level)
        {
            Gun gun = duck.holdObject as Gun;
            bool armed = gun != null && gun.ammo > 0;
            if (duck.holdObject != null && !armed)
            {
                // Throw away whatever can't shoot.
                _want[Grab] = ++_grabWait % 20 == 0;
                _target = null;
            }
            if (_target == null || _target.removeFromLevel || --_retarget <= 0 || (_target is Duck) != armed)
            {
                _retarget = RetargetFrames;
                _target = armed ? NearestDuck(duck, level) : (Thing)NearestGun(duck, level) ?? NearestDuck(duck, level);
            }
            if (_wander > 0)
            {
                --_wander;
                _want[_wanderDir > 0 ? Right : Left] = true;
                _want[Jump] = _wander % 30 < 12;
            }
            else if (_target != null)
            {
                float dx = _target.x - duck.x, dy = _target.y - duck.y;
                bool wantsMove = Math.Abs(dx) > (armed ? 48f : 6f);
                if (wantsMove)
                    _want[dx > 0 ? Right : Left] = true;
                else if (armed && Math.Sign(dx) != duck.offDir)
                    _want[dx > 0 ? Right : Left] = true;
                if (duck.grounded && dy < -12f && _jumpHold == 0)
                    _jumpHold = 14;
                if (duck.grounded && dy > 24f && Math.Abs(dx) < 24f)
                    _want[Crouch] = true;
                if (wantsMove && Math.Abs(duck.hSpeed) < 0.2f)
                {
                    if (++_stuck == StuckFrames / 2 && _jumpHold == 0)
                        _jumpHold = 14;
                    if (_stuck > StuckFrames)
                    {
                        _stuck = 0;
                        _wander = WanderFrames;
                        _wanderDir = Rando.Int(1) == 0 ? -1 : 1;
                    }
                }
                else
                {
                    _stuck = 0;
                }
                if (!armed && duck.holdObject == null && _target is Gun && Math.Abs(dx) < 14f && Math.Abs(dy) < 18f)
                    _want[Grab] = ++_grabWait % 6 == 0;
                if (armed && _target is Duck && Math.Abs(dy) < 28f && Math.Abs(dx) < 220f && Math.Sign(dx) == duck.offDir && _shootHold == 0 && Rando.Int(5) == 0)
                    _shootHold = 4 + Rando.Int(20);
            }
            else if (Rando.Int(90) == 0)
            {
                _wander = WanderFrames;
                _wanderDir = Rando.Int(1) == 0 ? -1 : 1;
            }
            if (_jumpHold > 0)
            {
                --_jumpHold;
                _want[Jump] = true;
            }
            if (_shootHold > 0)
            {
                --_shootHold;
                _want[Shoot] = armed;
            }
            if (Rando.Int(240) == 0)
                _want[Quack] = true;
        }

        private static Duck NearestDuck(Duck self, Level level)
        {
            Duck best = null;
            float bestDistance = float.MaxValue;
            foreach (Thing thing in level.things[typeof(Duck)])
            {
                Duck other = (Duck)thing;
                if (other == self || other.dead)
                    continue;
                float distance = (other.position - self.position).lengthSq;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = other;
                }
            }
            return best;
        }

        private static Gun NearestGun(Duck self, Level level)
        {
            Gun best = null;
            float bestDistance = float.MaxValue;
            foreach (Thing thing in level.things[typeof(Gun)])
            {
                Gun gun = (Gun)thing;
                if (gun.owner != null || gun.ammo <= 0)
                    continue;
                float distance = (gun.position - self.position).lengthSq;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = gun;
                }
            }
            return best;
        }
    }
}

namespace DuckGame
{
    // -perfalloc (desktop runtime only): which types the measured part of a scene allocates, from the
    // runtime's allocation tick events (one per ~100 KB).
    public sealed class PerfAllocations : System.Diagnostics.Tracing.EventListener
    {
        private static PerfAllocations _instance;
        private readonly System.Collections.Generic.Dictionary<string, long> _bytes = new System.Collections.Generic.Dictionary<string, long>();
        private bool _collecting;

        public static void Start()
        {
            _instance ??= new PerfAllocations();
            lock (_instance._bytes)
            {
                _instance._bytes.Clear();
                _instance._collecting = true;
            }
        }

        public static void Report(string scene)
        {
            if (_instance == null)
                return;
            lock (_instance._bytes)
            {
                _instance._collecting = false;
                var rows = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, long>>(_instance._bytes);
                rows.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < rows.Count && i < 30; i++)
                    System.Console.WriteLine("ALLOC " + scene + " " + (rows[i].Value / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + rows[i].Key);
            }
        }

        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, System.Diagnostics.Tracing.EventLevel.Verbose, (System.Diagnostics.Tracing.EventKeywords)1);
        }

        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs data)
        {
            if (data.EventName == null || !data.EventName.StartsWith("GCAllocationTick") || data.Payload == null || data.PayloadNames == null)
                return;
            int type = data.PayloadNames.IndexOf("TypeName"), amount = data.PayloadNames.IndexOf("AllocationAmount64");
            if (type < 0 || amount < 0)
                return;
            lock (_bytes)
            {
                if (!_collecting)
                    return;
                string name = (string)data.Payload[type];
                _bytes.TryGetValue(name, out long total);
                _bytes[name] = total + System.Convert.ToInt64(data.Payload[amount]);
            }
        }
    }
}

namespace DuckGame
{
    // -verifyall: counts who takes random numbers each frame, to find what makes two runs diverge.
    public sealed class PerfTracingRandom : Random
    {
        public static readonly System.Collections.Generic.SortedDictionary<string, int> Callers = new System.Collections.Generic.SortedDictionary<string, int>(StringComparer.Ordinal);
        private static readonly int MainThread = Environment.CurrentManagedThreadId;

        public PerfTracingRandom(int seed) : base(seed)
        {
        }

        private static void Note()
        {
            var trace = new System.Diagnostics.StackTrace(2, false);
            string name = "?";
            for (int i = 0; i < trace.FrameCount; i++)
            {
                var method = trace.GetFrame(i).GetMethod();
                string type = method?.DeclaringType?.Name;
                if (type == null || type == nameof(Rando) || type == nameof(PerfTracingRandom) || type == nameof(Random))
                    continue;
                name = type + "." + method.Name;
                break;
            }
            if (Environment.CurrentManagedThreadId != MainThread)
                name = "OFFTHREAD:" + name;
            lock (Callers)
            {
                Callers.TryGetValue(name, out int count);
                Callers[name] = count + 1;
            }
        }

        public override int Next()
        {
            Note();
            return base.Next();
        }

        public override int Next(int maxValue)
        {
            Note();
            return base.Next(maxValue);
        }

        public override int Next(int minValue, int maxValue)
        {
            Note();
            return base.Next(minValue, maxValue);
        }

        public override double NextDouble()
        {
            Note();
            return base.NextDouble();
        }
    }
}
