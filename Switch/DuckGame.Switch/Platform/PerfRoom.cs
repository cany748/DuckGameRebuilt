using System;
using System.Collections.Generic;

namespace DuckGame
{
    // The "effects" scene of -perftest: a stock level turned into a stress room. A fixed schedule keeps
    // dropping guns for the bots, live grenades, fire, fluids, crates, barrels and particle bursts at the
    // level's spawn points, and dead ducks come back so the round never ends.
    public static class PerfRoom
    {
        public const string SceneName = "effects";
        private const string StockLevel = "city01";
        private const int CycleFrames = 600;
        private const int RespawnFrames = 45;
        private const int MaxGuns = 28;
        private const int MaxCrates = 16;

        private static readonly Func<float, float, Thing>[] Guns =
        {
            (x, y) => new AK47(x, y), (x, y) => new QuadLaser(x, y), (x, y) => new Chaingun(x, y), (x, y) => new Bazooka(x, y),
            (x, y) => new Shotgun(x, y), (x, y) => new PewPewLaser(x, y), (x, y) => new GrenadeLauncher(x, y), (x, y) => new HugeLaser(x, y),
            (x, y) => new Magnum(x, y), (x, y) => new Phaser(x, y), (x, y) => new FlareGun(x, y), (x, y) => new LaserRifle(x, y),
            (x, y) => new Blunderbuss(x, y), (x, y) => new RomanCandle(x, y), (x, y) => new Sniper(x, y), (x, y) => new OldPistol(x, y)
        };

        private static readonly FluidData[] Fluids = { Fluid.Water, Fluid.Gas, Fluid.Lava };

        private static readonly List<Vec2> Anchors = new List<Vec2>();
        private static readonly int[] DeadFrames = new int[8];
        private static Level _level;
        private static int _frame, _gun, _anchor;

        public static Level Create()
        {
            LevelData data = DuckFile.LoadLevel(Content.path + "/levels/deathmatch/" + StockLevel + ".lev");
            return new GameLevel(data.metaData.guid, editorTestMode: true);
        }

        public static void Tick(Level level)
        {
            if (!GameMode.started)
                return;
            if (level != _level)
            {
                _level = level;
                _frame = _gun = _anchor = 0;
                Array.Clear(DeadFrames, 0, DeadFrames.Length);
                Anchors.Clear();
                foreach (Thing spawn in level.things[typeof(SpawnPoint)])
                    Anchors.Add(spawn.position);
                Anchors.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            }
            if (Anchors.Count == 0)
                return;
            Respawn(level);
            int frame = _frame++;
            if (frame % CycleFrames == 0)
                Console.WriteLine("PERF effects things=" + level.things.Count);

            if (frame % 20 == 0 && level.things.CountType<Gun>() < MaxGuns)
            {
                Vec2 at = Next();
                Level.Add(Guns[_gun++ % Guns.Length](at.x + Rando.Float(-12f, 12f), at.y - 20f));
            }
            if (frame % 45 == 10)
            {
                Vec2 at = Next();
                Level.Add(new Grenade(at.x + Rando.Float(-40f, 40f), at.y - 48f) { _pin = false, _timer = 0.7f });
            }
            if (frame % 30 == 5)
            {
                Vec2 at = Next();
                for (int i = 0; i < 6; i++)
                    Level.Add(SmallFire.New(at.x, at.y - 16f, Rando.Float(-3f, 3f), Rando.Float(-4f, -1f)));
            }
            if (frame % 6 == 0)
            {
                Vec2 at = Anchors[frame / 120 % Anchors.Count];
                Level.Add(new Fluid(at.x + 24f, at.y - 56f, new Vec2(Rando.Float(-1f, 1f), 1f), Fluids[frame / 200 % Fluids.Length]));
            }
            if (frame % 60 == 30)
            {
                Vec2 at = Next();
                if (level.things.CountType<Crate>() < MaxCrates)
                    Level.Add(new Crate(at.x + Rando.Float(-24f, 24f), at.y - 64f));
                else
                    Level.Add(new YellowBarrel(at.x + Rando.Float(-24f, 24f), at.y - 64f));
            }
            if (frame % 15 == 7)
            {
                Vec2 at = Next();
                float x = at.x, y = at.y - 24f;
                for (int i = 0; i < 8; i++)
                    Level.Add(ConfettiParticle.New(x, y, new Vec2(Rando.Float(-2f, 2f), Rando.Float(-3f, 0f))));
                for (int i = 0; i < 6; i++)
                    Level.Add(Feather.New(x, y, Profiles.defaultProfiles[i % 8].persona));
                for (int i = 0; i < 4; i++)
                {
                    Level.Add(SmallSmoke.New(x + Rando.Float(-8f, 8f), y + Rando.Float(-8f, 8f)));
                    Level.Add(Spark.New(x, y, new Vec2(Rando.Float(-1f, 1f), Rando.Float(-1f, 1f))));
                    Level.Add(new GlassParticle(x, y, new Vec2(Rando.Float(-1f, 1f), Rando.Float(-1f, 1f))));
                    Level.Add(WoodDebris.New(x, y));
                }
            }
        }

        private static Vec2 Next() => Anchors[_anchor++ % Anchors.Count];

        private static void Respawn(Level level)
        {
            for (int i = 0; i < DeadFrames.Length; i++)
            {
                Duck duck = Profiles.defaultProfiles[i].duck;
                if (duck == null || !duck.dead)
                {
                    DeadFrames[i] = 0;
                    continue;
                }
                if (++DeadFrames[i] < RespawnFrames)
                    continue;
                DeadFrames[i] = 0;
                if (duck.ragdoll != null)
                    Level.Remove(duck.ragdoll);
                if (duck._trapped != null)
                    Level.Remove(duck._trapped);
                Level.Remove(duck);
                Vec2 at = Next();
                Duck fresh = new Duck(at.x, at.y - 7f, duck.profile);
                Level.Add(fresh);
                if (level is GameLevel game)
                    game.followCam.Add(fresh);
            }
        }
    }
}
