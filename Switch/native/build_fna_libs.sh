#!/bin/bash
# Cross-compiles FNA3D and FAudio as static libraries for libnx (SDL2 backend).
# Run inside the devkitpro/devkita64 container; outputs go to Switch/native/build/fna.
set -e

ROOT=$(cd "$(dirname "$0")/../.." && pwd)
OUT="$ROOT/Switch/native/build/fna"
LIB="$ROOT/FNA/lib"

source /opt/devkitpro/switchvars.sh 2>/dev/null || true

for name in FNA3D FAudio; do
	aarch64-none-elf-cmake -S "$LIB/$name" -B "$OUT/$name" \
		-DCMAKE_BUILD_TYPE=RelWithDebInfo \
		-DBUILD_SHARED_LIBS=OFF \
		-DBUILD_SDL3=OFF
	cmake --build "$OUT/$name" -j"$(nproc)"
done

find "$OUT" -maxdepth 2 -name '*.a'
