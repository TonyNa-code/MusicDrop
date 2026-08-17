# macOS preview

## Packages

MusicDrop produces separate app archives:

- `osx-arm64` for Apple Silicon;
- `osx-x64` for Intel Macs.

The application bundle contains:

```text
MusicDrop.app/
  Contents/MacOS/MusicDrop
  Contents/Resources/musicdrop-cli
  Contents/Resources/ffmpeg/bin/{ffmpeg,ffprobe}
  Contents/Resources/ffmpeg/licenses/
  Contents/SharedSupport/{README,LICENSE,notices}
```

The desktop passes a size-bounded local JSON input manifest to the CLI. File names do not have to fit into a single command line. The manifest is removed after the child process exits.

## FFmpeg provenance

`tools/build_ffmpeg_macos.sh` downloads FFmpeg commit `9b6c8969e0` and requires source archive SHA-256:

```text
7e779215eae16ad7e93ddad59bd82822bd3d34e4dc61f9996f9481b2c0605bc3
```

It builds local-only `ffmpeg` and `ffprobe` with GPL/nonfree disabled, statically enables LAME and Vorbis, rejects Homebrew dylib references, checks required encoders and records binary hashes/build configuration. Licenses are copied into the bundle.

## CI and local package command

The release workflow uses the current GitHub-hosted labels:

- `macos-15` — arm64;
- `macos-15-intel` — x64.

On a matching Mac:

```bash
./tools/build_ffmpeg_macos.sh "$TMPDIR/musicdrop-ffmpeg"
./tools/package_macos.sh osx-arm64 "$TMPDIR/musicdrop-ffmpeg" "$PWD/dist" v3.1.0-preview.1
```

## Signing and notarization boundary

Open-source CI preview artifacts receive ad-hoc signing so the bundle structure can be verified. Ad-hoc signing is not Apple notarization. A retail-quality build still needs:

1. an Apple Developer Program account;
2. Developer ID Application signing for the app and nested executables;
3. `notarytool` submission and stapling;
4. Gatekeeper verification on a clean Mac;
5. real Intel and Apple Silicon conversion tests.

Until those checks pass, label macOS downloads **Preview**, not production-ready or 100%-compatible.

## Platform limitations

- Offline QMC/NCM/KWM/KGM/TM/XM/X2M/X3M paths are portable.
- Windows DPAPI caches cannot be read on macOS. Use a user-owned QMC keyring file instead.
- Windows Registry, Authenticode, `QQMusic.exe` startup and the optional Windows compatibility process do not exist on macOS.
- KGM v5 works with a selected database copy; automatic macOS Kugou database discovery remains unclaimed until a real client layout is verified.
