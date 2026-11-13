# Perf Testbench and 2x Speedup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A reproducible `-perftest` run (bots, effects room, heavy custom maps) on throttled Ryujinx and on x64, then halve update + draw time in the heavy scenes without changing behaviour.

**Architecture:** One static driver, `PerfTest`, lives in `Switch/DuckGame.Switch/Platform/` and is called from the hooks `SwitchStats` already has in `MonoMain`. It owns the scene list, seeds `Rando`, spawns bot ducks, records per-frame timings and, with `-verify`, hashes world state and sound starts. A host script builds, runs and diffs results against a saved baseline.

**Tech Stack:** C# (LangVersion 9, net9.0 mono AOT on Switch, net8.0 CoreCLR for the x64 build in `/root/3rd/game/benchsrc/game`), bash, Ryujinx under Xvfb with `ryu_throttle.sh`.

**Spec:** `Switch/docs/specs/2026-11-13-perf-testbench-design.md`

## Global Constraints

- Base commit for all comparisons: `13f2a2a7`.
- No behaviour change outside `-perftest`: fewer particles, slower physics, lower resolution, skipped frames or trimmed sound are not acceptable optimizations.
- Bots never appear in menus; they exist only under `-perftest`.
- Maps that need mods are excluded.
- `THROTTLE=40` for every Ryujinx measurement.
- Pass criterion per heavy scene and the effects room: mean and p95 of update + draw ≤ 50% of baseline, GL calls per frame not higher; control map not slower.
- Commits: Conventional Commits, one English line; commit and push to `switch` freely.
- Code comments only where the reason is not obvious.

## Review Focus

- A custom map without `PathNode`s or without spawn points: bots must still move and fight, the scene must not crash or stall.
- A scene that throws during load: logged as `PERF <scene> failed`, the run continues with the next scene.
- All ducks dead or a round-end condition reached mid-measurement: ducks respawn, the level does not change until the frame budget is spent.
- The same build run twice with `-verify`: identical hashes; if not, the run reports the first differing frame instead of silently passing.
- Normal start without `-perftest`: no bots, no extra per-frame work, title screen as before.

---

### Task 1: `PerfTest` driver, bots and `PERF` lines

**Files:**
- Create: `Switch/DuckGame.Switch/Platform/PerfTest.cs`
- Modify: `DuckGame/src/MonoTime/MonoMain.cs` (next to the existing `SwitchStats.UpdateStart/DrawStart/DrawEnd` calls, under `#if SWITCH`)
- Modify: `Switch/native/source/gpu_stats.c` only if per-frame GL counters are not already readable through `switch_gpu_stats`

**Interfaces:**
- Produces: `PerfTest.Enabled` (true when the command line has `-perftest`), `PerfTest.UpdateStart()`, `PerfTest.DrawStart()`, `PerfTest.DrawEnd()`, `PerfTest.Scenes` (list of `(string name, Func<Level> create)`), log lines `PERF <scene> frames=<n> upd=<mean>/<p50>/<p95>/<p99>/<max> drw=<...> sum=<mean>/<p95> over16=<n> gl=<draws>/<uploads>/<rt> gc0=<n> gc1=<n> alloc=<MB>` and `PERF done`.

- [ ] **Step 1:** Scene runner: after the title screen is reached, set `Level.current` to each scene in turn; 300 warm-up frames, 1800 measured frames; `Rando.generator = new Random(12345)` at scene start; a failed load logs `PERF <scene> failed <exception type>` and moves on; after the last scene log `PERF done` and exit the process on desktop.
- [ ] **Step 2:** Bots: 8 ducks with distinct default profiles and personas, input from a `DuckAI` subclass `PerfBot` that falls back to seeded wandering, jumping, grabbing and shooting when the level has no path nodes or the base AI has no target. Dead ducks respawn at a spawn point after 60 frames; round-end logic is suppressed while a scene is measured.
- [ ] **Step 3:** Initial scene list: control map (one stock deathmatch level, fixed name) and every `.lev` under the custom level folder when `-perfscan` is also given (300 measured frames each in that mode).
- [ ] **Step 4:** Build the x64 project and run `run_xvfb.sh 120 <prefix> -nosteam -nointro -perftest`. Expected: a `PERF` line for the control map, `PERF done`, screenshots show 8 ducks fighting.
- [ ] **Step 5:** Run again without `-perftest`. Expected: title screen, no `PERF` lines.
- [ ] **Step 6:** Commit `feat(switch): add -perftest scene runner with bot ducks`.

### Task 2: Effects room

**Files:**
- Create: `Switch/DuckGame.Switch/Platform/PerfRoom.cs`

**Interfaces:**
- Consumes: `PerfTest.Scenes`.
- Produces: `PerfRoom : Level` (built in code: closed arena of blocks, platforms, spawn points, path nodes), registered as scene `effects`.

- [ ] **Step 1:** Arena and a frame-indexed schedule that repeats every 600 frames and spawns, at fixed positions: grenade and rocket explosions, fire, smoke, shells and bullets from every automatic gun type, feathers, glass shards from breaking windows, fluid streams (water, gasoline, lava), laser and death-beam shots, confetti, destructible crates and blocks. Types that the code base has but the list misses are added when found while reading `Stuff/` and `Effects/`.
- [ ] **Step 2:** Run on x64 with `-perftest`. Expected: `PERF effects ...` line; screenshots at three points show all effect families on screen at once; object count stays bounded across schedule repeats (logged once per repeat as `PERF effects things=<n>`).
- [ ] **Step 3:** Commit `feat(switch): add effects stress room to -perftest`.

### Task 3: `-verify` hashes

**Files:**
- Modify: `Switch/DuckGame.Switch/Platform/PerfTest.cs`
- Modify: the single sound-start entry point in `DuckGame/src/MonoTime/Audio/SFX.cs` (one `#if SWITCH` call into `PerfTest.SoundStarted(name, volume, pitch)`)

**Interfaces:**
- Produces: log lines `VERIFY <scene> <frame> world=<hex> sound=<hex> things=<n>` every 60 measured frames; on desktop, a screenshot `verify-<scene>-<frame>.png` at frames 600 and 1500.

- [ ] **Step 1:** World hash: FNV-1a over every thing in the level in a stable order (type name, then creation index): type name, position, velocity, angle, `removeFromLevel`, plus for ducks `dead` and held object type. Sound hash: running FNV-1a over (name, volume, pitch) of every sound start.
- [ ] **Step 2:** Run the x64 build twice with `-perftest -verify`; diff the `VERIFY` lines. Expected: identical. If not, find the first differing frame, remove the source of nondeterminism under `PerfTest.Enabled` only (wall-clock use, unordered iteration, background threads), and repeat until two runs match.
- [ ] **Step 3:** Run once in Ryujinx and compare with x64. Record whether cross-runtime hashes match; if they do not, baselines are kept per runtime.
- [ ] **Step 4:** Commit `feat(switch): add -verify world and sound hashes to -perftest`.

### Task 4: Host script, heavy map selection, baseline

**Files:**
- Create: `/root/3rd/game/tools/perftest.sh` (outside the repository, like the other host scripts)
- Create: `Switch/docs/perf/baseline.md` (table of baseline numbers)
- Modify: `Switch/DuckGame.Switch/Platform/PerfTest.cs` (fixed heavy-map list)

**Interfaces:**
- Produces: `perftest.sh ryu|x64 [verify] [label]` → writes `perf/<label>.tsv` and `perf/<label>.verify` under `/root/3rd/game/`, prints a table and, when `perf/baseline.tsv` exists, the ratio per scene and PASS/FAIL against the criterion.

- [ ] **Step 1:** Script: build (`iterate.sh`-style full build for `ryu`, `dotnet build` of the bench project for `x64`), put the arguments into `config.ini` / command line, run until `PERF done` or a timeout, parse `PERF` and `VERIFY` lines.
- [ ] **Step 2:** `-perfscan` run in Ryujinx over all 98 custom maps; pick the 5 with the highest mean update + draw; hard-code their file names as scenes `heavy1..heavy5`.
- [ ] **Step 3:** Full baseline run: `perftest.sh ryu verify baseline` and `perftest.sh x64 verify baseline-x64`. Expected: 7 `PERF` lines each. Save the table to `Switch/docs/perf/baseline.md`.
- [ ] **Step 4:** Commit `feat(switch): fix heavy custom maps as -perftest scenes and record baseline`.

### Task 5: Optimization loop

**Files:** determined by the profile.

- [ ] **Step 1:** Profile the two slowest scenes: `-profile` in Ryujinx (symbolized with `prof_report.py`) and a sampling profile of the x64 build. Write the top 15 self-time entries per scene into `Switch/docs/perf/profile-<n>.md`.
- [ ] **Step 2:** Take the largest entry. Change it. Run `perftest.sh x64 verify` — `VERIFY` lines must equal the baseline. Run `perftest.sh ryu verify opt-<n>`.
- [ ] **Step 3:** Keep the change only if hashes match and no scene got slower; commit `perf(switch): <what>` and append the new numbers to `Switch/docs/perf/baseline.md`. If the hash differs, the change altered behaviour: fix or drop it.
- [ ] **Step 4:** Repeat from Step 1 until every heavy scene and the effects room meet the pass criterion. If the profile flattens before that, report the reached ratio and what remains instead of trading behaviour for speed.

### Task 6: Console package and docs

**Files:**
- Modify: `Switch/README.md` (Diagnostics: `-perftest`, `-verify`, `-perfscan`)
- Modify: `/root/3rd/game/tools/package.sh` only if the package needs a second `config.ini` variant

- [ ] **Step 1:** Document the arguments and the host script in `Switch/README.md`.
- [ ] **Step 2:** Build the release package; add `config-perftest.ini` with `-perftest -cpubench` and a three-line instruction for the console run (rename, start, send `log.txt`).
- [ ] **Step 3:** Smoke run of the packaged build in Ryujinx without `-perftest`: title screen, music counter moving, no `PERF` lines.
- [ ] **Step 4:** Commit `docs(switch): describe -perftest and console measurement` and push.
