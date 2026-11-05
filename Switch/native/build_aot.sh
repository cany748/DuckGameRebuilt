#!/bin/bash
# AOT-compiles the game's assemblies to static objects for MONO_AOT_MODE_INTERP (mixed mode).
# Run inside the devkitpro/devkita64 container.
# usage: build_aot.sh <mono-nx dotnet_runtime> <assembly list> [exclude regex]
# The list holds absolute paths, one per line (see Switch/build.sh); the files shipped on the SD
# card must be byte-identical to these, since AOT images are bound to the assembly MVIDs.
# Assemblies matching the exclude regex are only used to resolve references, not compiled; they
# stay interpreted if they ever run.
set -e

MONO_NX_ROOT=$1
LIST=$2
EXCLUDE=${3:-^$}
HERE=$(cd "$(dirname "$0")" && pwd)
OUT="$HERE/build/aot"
GEN="$HERE/build/gen"
AOT="$MONO_NX_ROOT/artifacts/bin/mono/linux.x64.Debug/cross/linux-x64/libnx-arm64/mono-aot-cross"
# Extra code generation optimizations (MONO_AOT_OPT, e.g. "-O=all,-unsafe"); empty = Mono's defaults.
MONO_OPT=${MONO_AOT_OPT:-}
# DGR is large and generic-heavy; the defaults (1000s) run out at runtime.
AOT_OPTS="full,interp,static,direct-icalls,nodebug,ntrampolines=40000,ngsharedvt-trampolines=8192,nimt-trampolines=8192,tool-prefix=aarch64-none-elf-"

export PATH="$PATH:$DEVKITPRO/devkitA64/bin"
mkdir -p "$OUT" "$GEN"

# mono-aot-cross resolves references (corlib included) from a single directory.
INPUT="$OUT/input"
rm -rf "$INPUT"
mkdir -p "$INPUT"
while read -r asm; do
	cp "$asm" "$INPUT/"
done < "$LIST"

: > "$GEN/aot_modules.h"
: > "$OUT/failed.txt"
grep -Ev "$EXCLUDE" "$LIST" > "$GEN/aot_assemblies.txt"
while read -r src; do
	file=$(basename "$src")
	name="${file%.*}"
	obj="$OUT/$name.o"
	if [ "$obj" -nt "$src" ]; then
		echo "aot: $name up to date"
	else
		echo "aot: $name"
		if ! MONO_PATH="$INPUT" "$AOT" $MONO_OPT --path="$INPUT" --aot="$AOT_OPTS,outfile=$obj" "$INPUT/$file" > "$OUT/$name.log" 2>&1; then
			# The (debug) cross compiler fails on a few assemblies; they stay interpreted.
			echo "aot: FAILED $name, skipped (see $OUT/$name.log)" | tee -a "$OUT/failed.txt"
			rm -f "$obj"
			continue
		fi
		# Mono puts the method_addresses table (plain `bl` instructions) in .data.rel.ro, which the
		# linker places after all code and out of the +-128MB bl range. Keep it in .text instead.
		aarch64-none-elf-objcopy --rename-section .data.rel.ro=.text.mono_method_addresses,alloc,load,readonly,code,contents "$obj"
	fi
	echo "AOT_MODULE(mono_aot_module_$(echo "$name" | tr '.-' '__')_info);" >> "$GEN/aot_modules.h"
done < "$GEN/aot_assemblies.txt"

# Drop objects of assemblies that are no longer in the list.
for obj in "$OUT"/*.o; do
	name=$(basename "$obj" .o)
	grep -q "/$name\.\(dll\|exe\)$" "$GEN/aot_assemblies.txt" || rm -f "$obj"
done
du -sh "$OUT"
