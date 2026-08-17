# Architecture / 架构

## Design goals

MusicDrop separates deterministic offline decoding from operating-system integration. The portable layer must never start a music client, inspect another process, contact a platform service or silently accept unknown plaintext.

MusicDrop 将确定性的离线解码与操作系统集成分离。跨平台层不会启动音乐客户端、检查其他进程、访问平台服务，也不会静默接受未知明文。

| Component | Target | Responsibility |
| --- | --- | --- |
| `MusicDrop.Core` | `net10.0` | Format parsing, streaming decryption, signature validation, local QMC keyring, KGM v5 database reading and atomic original output |
| `MusicDrop3.Portable.Cli` | `net10.0` | Strict batch preflight, bounded concurrency, output planning and portable FFmpeg transcoding |
| `MusicDrop.Desktop` | `net10.0` + Avalonia | Windows/macOS drag-and-drop shell; invokes the portable CLI through a bounded local JSON manifest |
| `MusicDrop3` | `net10.0-windows` + WinForms | Mature Windows 10/11 shell, DPAPI cache, signed client discovery and Windows process containment |
| `MusicDrop.Core.Harness` | `net10.0` | Cross-platform synthetic vectors, collision dispatch and streaming performance gate |
| `MusicDrop3.Harness` | `net10.0-windows` | Full Windows integration and FFmpeg validation matrix |

## Trust boundaries

1. Input names, headers, embedded lengths, databases and key files are untrusted.
2. Parsers bound allocations and offsets before reading.
3. Candidate local keys are never trusted by identifier match alone; each must decrypt to a recognized audio header.
4. A batch performs all-ready preflight before creating final output.
5. Output is written to a unique partial path, validated, then moved atomically where the filesystem permits.
6. Source files are opened read-only and are never deleted.
7. FFmpeg is a separate process. Release builds pin its source/binary identity and check the enabled license configuration.

## Platform adapters

Windows-only capabilities intentionally remain outside the portable core:

- DPAPI-protected EKey cache;
- Registry discovery and Tencent Authenticode validation;
- `QQMusic.exe` startup;
- Windows Job Objects for descendant-process containment;
- Windows installer and update paths.

macOS uses the same offline core, a native Avalonia window, platform storage pickers and an app-bundled CLI/FFmpeg layout. It does not inherit Windows client-injection claims.

## Version support

- Windows 10/11 x64: current production shell.
- macOS 12+ arm64/x64: 3.1 preview; stable status requires Actions and real-device results.
- Windows 7 SP1 x64: separate Legacy workstream. The current .NET 10 binaries are unsupported on Windows 7.
