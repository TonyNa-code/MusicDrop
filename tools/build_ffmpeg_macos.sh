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
curl --fail --location --retry 3 --output "$archive" "$source_url"
printf '%s  %s\n' "$source_sha256" "$archive" | shasum -a 256 --check
tar -xzf "$archive" -C "$work_dir"
source_dir="$(find "$work_dir" -mindepth 1 -maxdepth 1 -type d -name 'FFmpeg-*' -print -quit)"
test -n "$source_dir"

lame_prefix="$(brew --prefix lame)"
ogg_prefix="$(brew --prefix libogg)"
vorbis_prefix="$(brew --prefix libvorbis)"
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
  --extra-ldflags="-L$lame_prefix/lib -L$ogg_prefix/lib -L$vorbis_prefix/lib"

make -j"$(sysctl -n hw.ncpu)"
make install

"$output_dir/bin/ffmpeg" -hide_banner -version | tee "$output_dir/FFMPEG-VERSION.txt"
"$output_dir/bin/ffmpeg" -hide_banner -buildconf | tee "$output_dir/FFMPEG-BUILDCONF.txt"
! grep -Eq -- '--enable-(gpl|nonfree)' "$output_dir/FFMPEG-BUILDCONF.txt"
"$output_dir/bin/ffmpeg" -hide_banner -encoders | grep -Eq 'libmp3lame'
"$output_dir/bin/ffmpeg" -hide_banner -encoders | grep -Eq 'libvorbis'
"$output_dir/bin/ffmpeg" -hide_banner -encoders | grep -Eq '[[:space:]]flac[[:space:]]'
"$output_dir/bin/ffmpeg" -hide_banner -encoders | grep -Eq 'pcm_s(16|24|32)le'

if otool -L "$output_dir/bin/ffmpeg" "$output_dir/bin/ffprobe" | grep -Eq '/(opt/homebrew|usr/local/opt)/'; then
  echo "FFmpeg unexpectedly links to Homebrew dylibs; refusing a non-portable package." >&2
  exit 1
fi

mkdir -p "$output_dir/licenses"
cp COPYING.LGPLv2.1 COPYING.LGPLv3 "$output_dir/licenses/"
cp "$lame_prefix/share/doc/lame/COPYING" "$output_dir/licenses/LAME-COPYING.txt"
cp "$ogg_prefix/share/doc/libogg/COPYING" "$output_dir/licenses/LIBOGG-COPYING.txt"
cp "$vorbis_prefix/share/doc/libvorbis/COPYING" "$output_dir/licenses/LIBVORBIS-COPYING.txt"
shasum -a 256 "$output_dir/bin/ffmpeg" "$output_dir/bin/ffprobe" > "$output_dir/SHA256SUMS.txt"
