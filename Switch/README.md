# Duck Game Rebuilt for Nintendo Switch (homebrew)

Runs DGR as a Horizon homebrew `.nro` on a console with custom firmware (Atmosphère).
The game assemblies are executed by the [mono-nx](https://github.com/exelix11/mono-nx) interpreter;
FNA3D (OpenGL via mesa/nouveau), FAudio and SDL2 are linked statically into the `.nro`.

Status: first local-play build. No mods, no online play. Performance is not tuned yet.

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

## Installing

1. Copy the contents of `Switch/out/sd` to the root of the SD card.
2. Game content is not distributed: copy the `Content` folder of the Steam version to
   `/switch/DuckGame/game/Content`, then copy `deps/Content` from this repository over it.
3. Launch the homebrew menu in full application mode (hold **R** while starting any game) and run
   **Duck Game Rebuilt**. Applet mode (album) does not have enough memory and is refused.

The log goes to `/switch/DuckGame/log.txt` (see `config.ini` for UDP or svc output);
DGR's own crash log is `/switch/DuckGame/game/ducklog.txt`, saves live in `/switch/DuckGame/home`.

## Platform notes

- The runtime runs on a 32 MB stack thread: MojoShader keeps a ~330 KB context on the stack.
- FNA libraries must be built with devkitA64's own compiler flags (`-mtp=soft` for TLS).
- SDL2 on Switch allows a single audio device, so music is streamed through
  `DynamicSoundEffectInstance` instead of FNA's `MediaPlayer`.
- Unix domain sockets are rejected in the P/Invoke shim (Horizon has none).
