#!/bin/sh
set -eu

# Requires the macOS SDK and Command Line Tools. Produces both Mac architectures.
source_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
output_dir=${1:-"$source_dir/bin"}
mkdir -p "$output_dir"
xcrun --sdk macosx clang++ -std=c++17 -O3 -fobjc-arc -fobjc-arc-exceptions -fvisibility=hidden \
    -arch arm64 -arch x86_64 -mmacosx-version-min=11.0 \
    -dynamiclib -framework Foundation -framework Metal \
    -install_name @rpath/libpgpmint_metal.dylib \
    "$source_dir/PGPMintMetal.mm" -o "$output_dir/libpgpmint_metal.dylib"
