#!/usr/bin/env bash
set -euo pipefail

rid="${1:?usage: package_macos.sh <osx-arm64|osx-x64> <ffmpeg-prefix> <dist-directory> <version>}"
ffmpeg_prefix="${2:?missing FFmpeg prefix}"
dist_dir="${3:?missing distribution directory}"
version="${4:?missing package version}"
case "$rid" in osx-arm64|osx-x64) ;; *) echo "Unsupported RID: $rid" >&2; exit 2 ;; esac
if [[ ! "$version" =~ ^v[0-9A-Za-z.-]+$ ]]; then
  echo "Unsupported version: $version" >&2
  exit 2
fi

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$dist_dir"
dist_dir="$(cd "$dist_dir" && pwd)"
publish_root="$dist_dir/publish-$rid"
app="$dist_dir/MusicDrop.app"
archive="$dist_dir/MusicDrop-${version}-${rid}.zip"

dotnet publish "$repo_root/MusicDrop.Desktop/MusicDrop.Desktop.csproj" \
  -c Release -r "$rid" --self-contained true -o "$publish_root/desktop"
dotnet publish "$repo_root/MusicDrop3.Portable.Cli/MusicDrop3.Portable.Cli.csproj" \
  -c Release -r "$rid" --self-contained true -o "$publish_root/cli"

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources/ffmpeg/bin" \
  "$app/Contents/Resources/ffmpeg/licenses" "$app/Contents/SharedSupport"
cp "$publish_root/desktop/MusicDrop.Desktop" "$app/Contents/MacOS/MusicDrop"
cp "$publish_root/cli/musicdrop" "$app/Contents/Resources/musicdrop-cli"
cp "$ffmpeg_prefix/bin/ffmpeg" "$ffmpeg_prefix/bin/ffprobe" "$app/Contents/Resources/ffmpeg/bin/"
cp -R "$ffmpeg_prefix/licenses/." "$app/Contents/Resources/ffmpeg/licenses/"
cp "$ffmpeg_prefix/FFMPEG-VERSION.txt" "$ffmpeg_prefix/FFMPEG-BUILDCONF.txt" \
  "$ffmpeg_prefix/SHA256SUMS.txt" "$app/Contents/Resources/ffmpeg/"
cp "$repo_root/tools/macos/Info.plist" "$app/Contents/Info.plist"
cp "$repo_root/README.md" "$repo_root/README.zh-CN.md" "$repo_root/LICENSE" \
  "$repo_root/THIRD-PARTY-NOTICES.md" "$repo_root/TRADEMARKS.md" "$repo_root/PRIVACY.md" \
  "$app/Contents/SharedSupport/"

dotnet_root="${DOTNET_ROOT:-}"
if [[ -z "$dotnet_root" || ! -f "$dotnet_root/LICENSE.txt" ]]; then
  dotnet_root="$(cd "$(dirname "$(command -v dotnet)")" && pwd)"
fi
test -f "$dotnet_root/LICENSE.txt"
test -f "$dotnet_root/ThirdPartyNotices.txt"
cp "$dotnet_root/LICENSE.txt" "$app/Contents/SharedSupport/DOTNET-LICENSE.txt"
cp "$dotnet_root/ThirdPartyNotices.txt" \
  "$app/Contents/SharedSupport/DOTNET-THIRD-PARTY-NOTICES.txt"

iconset="$publish_root/MusicDrop.iconset"
mkdir -p "$iconset"
source_icon="$repo_root/MusicDrop3/Assets/musicdrop-logo-1024.png"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$source_icon" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$source_icon" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/MusicDrop.icns"

chmod 0755 "$app/Contents/MacOS/MusicDrop" "$app/Contents/Resources/musicdrop-cli" \
  "$app/Contents/Resources/ffmpeg/bin/ffmpeg" "$app/Contents/Resources/ffmpeg/bin/ffprobe"
codesign --force --deep --sign - "$app"
codesign --verify --deep --strict --verbose=2 "$app"

rm -f "$archive" "$archive.sha256.txt"
ditto -c -k --sequesterRsrc --keepParent "$app" "$archive"
shasum -a 256 "$archive" | awk '{print $1}' > "$archive.sha256.txt"
echo "$archive"
