#!/bin/bash
# Builds the Switch homebrew package into Switch/out/sd (copy its contents to the SD card root).
#
# Requirements on the host: docker, .NET 9 SDK (dotnet on PATH or DOTNET), the mono-nx SDK
# (https://github.com/exelix11/mono-nx/releases, mono-nx-sdk-linux-x64.zip) unpacked at
# MONO_NX_SDK and its sd_files.zip unpacked at MONO_NX_SD_FILES.
# AOT=0 builds an interpreter-only nro (smaller, much slower).
# Game content is not included: copy the Steam "Content" folder plus deps/Content on top of it to
# /switch/DuckGame/game/Content (see Switch/README.md).
set -e

ROOT=$(cd "$(dirname "$0")/.." && pwd)
: "${MONO_NX_SDK:?set MONO_NX_SDK to the unpacked mono-nx-sdk-linux-x64.zip}"
: "${MONO_NX_SD_FILES:?set MONO_NX_SD_FILES to the unpacked sd_files.zip}"
DOTNET=${DOTNET:-dotnet}
IMAGE=${DEVKITA64_IMAGE:-devkitpro/devkita64:20260219}
OUT="$ROOT/Switch/out/sd/switch/DuckGame"

echo "== managed game"
"$DOTNET" publish "$ROOT/Switch/DuckGame.Switch/DuckGame.Switch.csproj" -c Release -o "$ROOT/Switch/out/managed" --nologo -v q

SDK_LIB="$MONO_NX_SDK/dotnet_runtime/artifacts/bin/mono/libnx.arm64.Debug"
SDK_FRAMEWORK="$MONO_NX_SDK/dotnet_runtime/artifacts/bin/runtime/net9.0-libnx-Debug-arm64"
if [ "${AOT:-1}" = 1 ]; then
	echo "== assembly closure"
	mkdir -p "$ROOT/Switch/native/build/gen"
	# Same search order as config.ini's assembly_dir, so the AOT images match the files loaded on console.
	"$DOTNET" run -c Release --project "$ROOT/Switch/tools/AssemblyClosure" -- \
		"$SDK_LIB;$SDK_FRAMEWORK;$ROOT/Switch/out/managed" "$ROOT/Switch/out/managed/DuckGame.dll" \
		| sed -e "s#^$MONO_NX_SDK#/sdk#" -e "s#^$ROOT#/dgr#" > "$ROOT/Switch/native/build/gen/assemblies.txt"
fi

echo "== native host (AOT=${AOT:-1})"
docker run --rm -e AOT="${AOT:-1}" -e MONO_AOT_OPT="$MONO_AOT_OPT" -v "$ROOT":/dgr -v "$MONO_NX_SDK":/sdk:ro -w /dgr/Switch/native "$IMAGE" bash -c '
	set -e
	./build_fna_libs.sh
	mkdir -p build/gen
	NM=$DEVKITPRO/devkitA64/bin/aarch64-none-elf-nm
	python3 gen_dlshim.py build/gen/dlshim_fna.c $NM \
		FNA3D=/dgr/FNA/src/Graphics/FNA3D.cs:build/fna/FNA3D/libFNA3D.a \
		FAudio=/dgr/FNA/lib/FAudio/csharp/FAudio.cs:build/fna/FAudio/libFAudio.a \
		SDL2=/dgr/FNA/lib/SDL2-CS/src/SDL2.cs:$DEVKITPRO/portlibs/switch/lib/libSDL2.a 2>&1 | grep -v "^  missing SDL2"
	if [ "$AOT" = 1 ]; then
		# Mod loading (Roslyn, Cecil, MonoMod, Harmony), Discord and speech are disabled on Switch.
		./build_aot.sh /sdk/dotnet_runtime build/gen/assemblies.txt \
			"/(Microsoft\.CodeAnalysis[^/]*|Mono\.Cecil[^/]*|MonoMod[^/]*|0Harmony|DiscordRPC|System\.Speech)\.(dll|exe)$"
	else
		rm -rf build/aot build/gen/aot_modules.h
	fi
	touch source/main.c
	make -j$(nproc) MONO_NX_ROOT=/sdk/dotnet_runtime ICU_NX_INSTALL_DIR=/sdk/icu/libnx
'

echo "== sd layout"
rm -rf "$OUT/game" "$OUT/mono"
mkdir -p "$OUT/game" "$OUT/mono"
cp "$ROOT/Switch/native/build/DuckGame.nro" "$OUT/"
cp "$ROOT/Switch/config.ini" "$OUT/"
cp -r "$MONO_NX_SD_FILES/mono/lib_net9.0" "$MONO_NX_SD_FILES/mono/framework_net9.0" "$MONO_NX_SD_FILES/mono/etc" "$OUT/mono/"
# Native libraries are linked into the nro; FNA.dll.config would remap their names to .so files.
# Assemblies that also ship with the runtime are resolved from the framework first; leaving
# the NuGet copies out keeps the files on SD identical to what the AOT images were built against.
for f in "$ROOT/Switch/out/managed"/*.dll "$ROOT/Switch/out/managed"/*.exe; do
	[ -f "$MONO_NX_SD_FILES/mono/framework_net9.0/$(basename "$f")" ] || cp "$f" "$OUT/game/"
done
cp "$ROOT/deps/lang.txt" "$ROOT/deps/MonoFont.ttf" "$OUT/game/"
cp -r "$ROOT/deps/spriteatlas" "$OUT/game/"
echo "done: $ROOT/Switch/out/sd"
