#!/bin/bash
# Cross-compiles FNA3D and FAudio as static libraries for libnx (SDL2 backend).
# Run inside the devkitpro/devkita64 container; outputs go to Switch/native/build/fna.
# Sources are copied out of the submodules so patches/ can be applied without dirtying them.
set -e

ROOT=$(cd "$(dirname "$0")/../.." && pwd)
HERE="$ROOT/Switch/native"
OUT="$HERE/build/fna"
SRC="$HERE/build/fna-src"
LIB="$ROOT/FNA/lib"

source /opt/devkitpro/switchvars.sh 2>/dev/null || true

for name in FNA3D FAudio; do
	rm -rf "$SRC/$name"
	mkdir -p "$SRC"
	cp -r "$LIB/$name" "$SRC/$name"
	for p in "$HERE"/patches/"$(echo "$name" | tr 'A-Z' 'a-z')"-*.patch; do
		[ -e "$p" ] || continue
		echo "patch: $(basename "$p")"
		patch -d "$SRC/$name" -p1 --quiet < "$p"
	done
	aarch64-none-elf-cmake -S "$SRC/$name" -B "$OUT/$name" \
		-DCMAKE_BUILD_TYPE=RelWithDebInfo \
		-DBUILD_SHARED_LIBS=OFF \
		-DBUILD_SDL3=OFF
	cmake --build "$OUT/$name" -j"$(nproc)"
done

find "$OUT" -maxdepth 2 -name '*.a'
