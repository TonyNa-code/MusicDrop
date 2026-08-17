#!/usr/bin/env bash
set -euo pipefail

output_dir="${1:?usage: build_ffmpeg_macos.sh <output-directory>}"
source_commit="9b6c8969e0"
source_sha256="7e779215eae16ad7e93ddad59bd82822bd3d34e4dc61f9996f9481b2c0605bc3"
source_url="https://github.com/FFmpeg/FFmpeg/archive/${source_commit}.tar.gz"
work_dir="$(mktemp -d "${TMPDIR:-/tmp}/musicdrop-ffmpeg.XXXXXX")"
trap 'rm -rf "$work_dir"' EXIT

mkdir -p "$output_dir"
output_dir="$(cd "$output_dir" && pwd)"
archive="$work_dir/ffmpeg.tar.gz"

brew install pkg-config nasm lame libogg libvorbis
curl_args=(
  --fail
  --location
  --retry 10
  --retry-all-errors
  --retry-delay 5
  --retry-max-time 900
  --connect-timeout 30
  --max-time 900
  --user-agent "MusicDrop-release-builder"
  --output "$archive"
)
if [[ -n "${GITHUB_TOKEN:-}" ]]; then
  curl_args+=(--header "Authorization: Bearer ${GITHUB_TOKEN}")
fi
curl "${curl_args[@]}" "$source_url"
printf '%s  %s\n' "$source_sha256" "$archive" | shasum -a 256 --check
tar -xzf "$archive" -C "$work_dir"
source_dir="$(find "$work_dir" -mindepth 1 -maxdepth 1 -type d -name 'FFmpeg-*' -print -quit)"
test -n "$source_dir"

lame_prefix="$(brew --prefix lame)"
mpg123_prefix="$(brew --prefix mpg123)"
ogg_prefix="$(brew --prefix libogg)"
vorbis_prefix="$(brew --prefix libvorbis)"

# Homebrew installs both static archives and dylibs. FFmpeg's pkg-config checks
# emit -l flags, which otherwise let Apple's linker select Homebrew dylibs.
# Put only the required archives in the first search path and explicitly use
# Apple's per-directory search order. This preserves FFmpeg's native
# pkg-config probes while producing standalone binaries.
static_lib_dir="$work_dir/static-libs"
mkdir -p "$static_lib_dir"
static_archives=(
  "$lame_prefix/lib/libmp3lame.a"
  "$mpg123_prefix/lib/libmpg123.a"
  "$ogg_prefix/lib/libogg.a"
  "$vorbis_prefix/lib/libvorbis.a"
  "$vorbis_prefix/lib/libvorbisenc.a"
)
for static_archive in "${static_archives[@]}"; do
  test -f "$static_archive"
  ln -s "$static_archive" "$static_lib_dir/$(basename "$static_archive")"
done

cd "$source_dir"
PKG_CONFIG_PATH="$lame_prefix/lib/pkgconfig:$ogg_prefix/lib/pkgconfig:$vorbis_prefix/lib/pkgconfig" \
./configure \
  --prefix="$output_dir" \
  --disable-debug \
  --disable-doc \
  --disable-ffplay \
  --disable-network \
  --disable-shared \
  --enable-static \
  --disable-gpl \
  --disable-nonfree \
  --enable-ffmpeg \
  --enable-ffprobe \
  --enable-libmp3lame \
  --enable-libvorbis \
  --pkg-config-flags="--static" \
  --extra-cflags="-I$lame_prefix/include -I$ogg_prefix/include -I$vorbis_prefix/include" \
  --extra-ldflags="-Wl,-search_paths_first -L$static_lib_dir"

make -j"$(sysctl -n hw.ncpu)"
make install

"$output_dir/bin/ffmpeg" -hide_banner -version | tee "$output_dir/FFMPEG-VERSION.txt"
"$output_dir/bin/ffmpeg" -hide_banner -buildconf | tee "$output_dir/FFMPEG-BUILDCONF.txt"
! grep -Eq -- '--enable-(gpl|nonfree)' "$output_dir/FFMPEG-BUILDCONF.txt"
"$output_dir/bin/ffmpeg" -hide_banner -encoders | grep -Eq 'libmp3lame'
"$output_dir/bin/ffmpeg" -hide_banner -encoders | grep -Eq 'libvorbis'
"$output_dir/bin/ffmpeg" -hide_banner -encoders | grep -Eq '[[:space:]]flac[[:space:]]'
"$output_dir/bin/ffmpeg" -hide_banner -encoders | grep -Eq 'pcm_s(16|24|32)le'

dependencies_file="$output_dir/FFMPEG-DEPENDENCIES.txt"
otool -L "$output_dir/bin/ffmpeg" "$output_dir/bin/ffprobe" | tee "$dependencies_file"
if grep -Eq '/(opt/homebrew|usr/local/opt)/' "$dependencies_file"; then
  echo "FFmpeg unexpectedly links to Homebrew dylibs; refusing a non-portable package." >&2
  exit 1
fi

musicdrop_revision="${GITHUB_SHA:-main}"
cat > "$output_dir/FFMPEG-SOURCE.txt" <<SOURCE_PROVENANCE
FFmpeg commit: ${source_commit}
Source archive: ${source_url}
Source archive SHA-256: ${source_sha256}
Build script: https://github.com/TonyNa-code/MusicDrop/blob/${musicdrop_revision}/tools/build_ffmpeg_macos.sh
Dependency formulas: $(brew list --versions lame mpg123 libogg libvorbis | tr '\n' ';')
Configuration and runtime dependencies are recorded beside this file.
SOURCE_PROVENANCE

mkdir -p "$output_dir/licenses"
cp COPYING.LGPLv2.1 COPYING.LGPLv3 "$output_dir/licenses/"
cp "$lame_prefix/share/doc/lame/COPYING" "$output_dir/licenses/LAME-COPYING.txt"
mpg123_license="$(find "$mpg123_prefix" -type f \( -name COPYING -o -name LICENSE \) -print -quit)"
test -n "$mpg123_license"
cp "$mpg123_license" "$output_dir/licenses/MPG123-COPYING.txt"
cp "$ogg_prefix/share/doc/libogg/COPYING" "$output_dir/licenses/LIBOGG-COPYING.txt"
cp "$vorbis_prefix/share/doc/libvorbis/COPYING" "$output_dir/licenses/LIBVORBIS-COPYING.txt"
shasum -a 256 "$output_dir/bin/ffmpeg" "$output_dir/bin/ffprobe" > "$output_dir/SHA256SUMS.txt"
