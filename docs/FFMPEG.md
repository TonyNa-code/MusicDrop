# FFmpeg packaging

## Windows 10/11 x64

MusicDrop pins one immutable BtbN Windows x64 LGPL shared build. The application can use it in either form:

1. `ffmpeg/bin/ffmpeg.exe` beside the app in the full offline package;
2. a per-user managed installation downloaded on first use.

The managed installer verifies the archive SHA-256, safely extracts only ten whitelisted files, verifies each file hash, runs `ffmpeg -version`, rejects GPL-enabled configuration, and confirms FLAC, libmp3lame and libvorbis encoders.

This choice avoids requiring users to install FFmpeg globally and avoids bundling a GPL build. It does not change FFmpeg's own license. Full packages retain FFmpeg's upstream `LICENSE.txt`, source revision link and build-script link.

Maintainers updating FFmpeg must pin a date-tagged Release rather than `latest`, review its configuration, update all expected hashes, test archive defenses and update third-party notices.

## macOS arm64/x64 preview

The macOS release workflow does not copy an opaque third-party binary. It downloads the source archive for the same FFmpeg commit (`9b6c8969e0`), verifies SHA-256 `7e779215eae16ad7e93ddad59bd82822bd3d34e4dc61f9996f9481b2c0605bc3`, and builds separate binaries on GitHub-hosted Apple Silicon and Intel runners.

The script:

- disables GPL, nonfree, network access, documentation, FFplay and automatic external-library detection;
- enables static LAME, mpg123 and Vorbis dependencies with compatible licenses;
- rejects unexpected Homebrew dylib references;
- verifies FLAC, PCM, MP3 and Vorbis encoders;
- records `-version`, `-buildconf` and SHA-256 outputs;
- copies FFmpeg, LAME, mpg123, libogg and libvorbis license texts into the app bundle.

Homebrew bottles do not consistently install their `COPYING` files. The repository therefore carries the complete license texts extracted from the formula-pinned LAME 4.0, libogg 1.3.6 and libvorbis 1.3.7 source archives after verifying SHA-256 `3df512…16eb`, `83e670…4638` and `b33cc4…954b`; packaging never silently omits them.

See `tools/build_ffmpeg_macos.sh` and [macOS packaging](MACOS.md). A source-built LGPL configuration does not replace the need to comply with LGPL source/relinking obligations when distributing binaries.
