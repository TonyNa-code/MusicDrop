# Third-party notices

## Unlock Music algorithms

QMC、NCM、KWM、KGM、QQ iOS TM、Xiami XM 和 Ximalaya X2M/X3M 的格式行为参考 `leafxdd/unlock-music` 中相应算法实现，并保留其 MIT 许可声明。Ximalaya 的两个 1024 项置换表以嵌入资源形式保留在跨平台核心中。

Copyright (c) 2020-2021 Unlock Music

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, subject to inclusion of the copyright and permission notice. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND.

## Microsoft .NET and Microsoft.Data.Sqlite

Self-contained builds include Microsoft .NET Runtime and Microsoft.Data.Sqlite under the MIT License. Release packages include the upstream .NET license and third-party notices.

## SQLitePCLRaw and SQLite

- SQLitePCLRaw 3.0.5: Apache License 2.0, Copyright 2014-2024 SourceGear, LLC.
- SQLite native library: public domain, <https://www.sqlite.org/copyright.html>.

## Avalonia

The cross-platform desktop uses Avalonia 11.3.20, Avalonia.Desktop, Avalonia.Themes.Fluent and Avalonia.Fonts.Inter under the MIT License. Copyright belongs to the Avalonia project contributors and respective font authors.

## FFmpeg and BtbN FFmpeg Builds

MusicDrop uses FFmpeg as a separate executable process and does not link its application binary to FFmpeg libraries.

The pinned zero-configuration build is:

- BtbN tag: `autobuild-2026-08-11-13-11`
- Asset: `ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-shared-8.1.zip`
- Archive SHA-256: `026f3ba22f0acf4fe58bf4da28a7eb64ffb107b270119684b91e4cace3b577aa`
- FFmpeg revision marker: `n8.1.2-34-g9b6c8969e0-20260811`
- Build configuration includes `--enable-version3 --enable-shared` and does not include `--enable-gpl`.

Upstream: <https://ffmpeg.org/> · build scripts: <https://github.com/BtbN/FFmpeg-Builds> · corresponding source revision: <https://github.com/FFmpeg/FFmpeg/commit/9b6c8969e0>

Binary packages that bundle FFmpeg include its `LICENSE.txt`. Users may replace the separate FFmpeg folder with a compatible build, subject to that build's license.

macOS packages build the same FFmpeg commit from the pinned source archive instead of using BtbN Windows binaries. The macOS configuration disables GPL and nonfree, statically enables LAME (LGPL), libogg and libvorbis (BSD-style licenses), records the build configuration and includes the corresponding license files. See `docs/FFMPEG.md` and `tools/build_ffmpeg_macos.sh`.

## Optional qqmusic_decrypt compatibility component

`luyikk/qqmusic_decrypt` v0.1.1 is not bundled because its repository does not declare a clear redistribution license. Installation is optional and user-approved; MusicDrop accepts only the fixed ZIP and EXE hashes recorded in `AppInfo.cs`. Source-visible upstream: <https://github.com/luyikk/qqmusic_decrypt>.

## Brand artwork

The MusicDrop droplet/note/waveform artwork was created specifically for this project. See [TRADEMARKS.md](TRADEMARKS.md) for nominative and branding use.
