# Duck Game Rebuilt for Nintendo Switch (homebrew)

Runs DGR as a Horizon homebrew `.nro` on a console with custom firmware (Atmosphère).
The game runs on [mono-nx](https://github.com/exelix11/mono-nx): the assemblies are AOT-compiled to
native code and linked into the `.nro`, with the Mono interpreter as a fallback for anything the AOT
compiler can't handle. FNA3D (OpenGL via mesa/nouveau), FAudio and SDL2 are linked statically too.

Status: local play works. No mods, no online play.

## Layout

| Path | What |
|---|---|
| `DuckGame.Switch/` | net9.0 project that compiles the unchanged DGR sources with `SWITCH` defined |
| `DGSteam/` | net9.0 build of the Steam wrapper (Steam stays disabled at runtime) |
| `native/` | libnx host: `source/main.c`, vendored mono-nx host code, FNA lib build and P/Invoke symbol tables |
| `build.sh` | builds everything and stages the SD card layout in `Switch/out/sd` |
| `config.ini` | runtime configuration copied next to the `.nro` |

## Building

Requirements: Docker, .NET 9 SDK, and the mono-nx release `rel-3`
(`mono-nx-sdk-linux-x64.zip` and `sd_files.zip`), each unpacked into its own folder.

```bash
git submodule update --init --recursive
MONO_NX_SDK=/path/to/mono-nx-sdk MONO_NX_SD_FILES=/path/to/sd_files Switch/build.sh
```

`AOT=0` builds an interpreter-only `.nro` (~16 MB instead of ~470 MB, several times slower).

## Installing

1. Copy the contents of `Switch/out/sd` to the root of the SD card.
2. Game content is not distributed: copy the `Content` folder of the Steam version to
   `/switch/DuckGame/game/Content`, then copy `deps/Content` from this repository over it.
3. Launch the homebrew menu in full application mode (hold **R** while starting any game) and run
   **Duck Game Rebuilt**. Applet mode (album) does not have enough memory and is refused.

The log goes to `/switch/DuckGame/log.txt` (see `config.ini` for UDP or svc output);
DGR's own crash log is `/switch/DuckGame/game/ducklog.txt`, saves live in `/switch/DuckGame/home`.

## Platform notes

- AOT runs in mixed mode (`MONO_AOT_MODE_INTERP`). mono-nx asserts when the runtime patches plain heap
  memory in that mode, so `source/codeman_wrap.c` wraps the two codeman functions to let heap
  addresses through. The assemblies on SD must be byte-identical to the ones that were compiled;
  `Switch/tools/AssemblyClosure` computes the set the same way the runtime resolves it.
- Mono's AOT `method_addresses` table is moved into `.text` (`objcopy`), otherwise it ends up out of
  `bl` range of the code.
- The mono-nx cross compiler fails on SixLabors.ImageSharp, NAudio and System.Private.Windows.Core;
  they stay interpreted.
- AppContext switches are passed through `monovm_initialize`: EventSource is off (no EventPipe in
  mono-nx) and resource keys replace exception message lookups.

- The runtime runs on a 32 MB stack thread: MojoShader keeps a ~330 KB context on the stack.
- FNA libraries must be built with devkitA64's own compiler flags (`-mtp=soft` for TLS).
- Threads created with libnx's "default core" would all be pinned to core 0; `source/thread_wrap.c`
  keeps the game loop on core 0 and spreads every other thread over cores 1-2.
- Audio bypasses SDL: `source/faudio_switch.c` replaces FAudio's device layer (linker `--wrap`) and mixes
  one audren frame (240 samples, 5 ms) at a time from its own thread on core 2, with 4 frames queued.
  SDL2's Switch driver queued only two buffers, which either underran or added a lot of latency.
- `DuckGame.Switch/Platform/SwitchDisplay.cs` renders at 1280x720 handheld and 1920x1080 docked and
  follows dock changes (SDL2 always reports a 1920x1080 display mode).
- "Show FPS" draws `SwitchStats`: FPS/UPS, update/draw/wait milliseconds, resolution and GC heap size.
- Desktop-only options are pruned from the menus by `DuckGame.Switch/Platform/SwitchMenus.cs`;
  Graphics gets a "Show FPS" toggle.
- SDL2 on Switch allows a single audio device, so music is streamed through
  `DynamicSoundEffectInstance` instead of FNA's `MediaPlayer`.
- Unix domain sockets are rejected in the P/Invoke shim (Horizon has none).

## Release framework

The mono-nx SDK ships a Debug build of the framework: Mono then compiles it without inlining and keeps every `Debug.Assert`. Build `exelix11/dotnet_runtime` (branch `libnx`) in the devkitA64 image and overlay the result on copies of the SDK and of `sd_files`, then point `MONO_NX_SDK` and `MONO_NX_SD_FILES` at those copies:

```sh
docker run --rm -v $PWD:/mono-nx -w /mono-nx/dotnet_runtime -e ROOTFS_DIR=/opt/devkitpro \
    -e DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 devkitpro/devkita64:20260219 \
    ./build.sh --subset mono.corelib+libs.sfx -c Release --cross -a arm64 --os libnx
```

Replace `System.Private.CoreLib.dll` with `artifacts/bin/mono/libnx.arm64.Release/` and every framework dll with `artifacts/bin/runtime/net9.0-libnx-Release-arm64/`, keeping the Debug directory names of the SDK. The build reports NETSDK1188 and leaves out `System`, `System.Net`, `System.Net.Sockets` and `netstandard`; the SDK's copies of those work.

## Diagnostics

`Show FPS` and `Frame Stats` are under Options > Graphics. The arguments `-framestats` and `-cpubench` go to `[game] args` in `config.ini`: the second prints a fixed CPU benchmark (`BENCH` lines) to the log.

More arguments for the same place:

- `-profile` samples the render thread for 30 seconds, a minute after start, and writes `profile.txt` next to the nro. `tools/prof_report.py`-style symbolization applies to its `native+0x...` lines: they are offsets into `DuckGame.elf`.
- `-levscan` prints a `LEVSCAN` line for every custom level with the mods it needs and the objects this build can't create.

Frames longer than 25 ms are appended to `spikes.log` next to the nro whenever the level changes.
