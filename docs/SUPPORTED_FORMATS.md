# Supported formats and honest limits

## Output modes

- 原始格式：保留解密后的原始音频编码，不进行不必要的再编码。
- FLAC / WAV：无损编码；把有损源转成这两种格式不会恢复已经丢失的音质。
- MP3 / OGG：有损编码。

## Platform paths

- QQ/QMC: embedded EKey, imported EKey, Android `player_process_db`, or DPAPI cache can support offline decryption. The current public process-injection fallback does not reliably handle QQ Music 21.62 when an EKey is absent; MusicDrop's canary rejects still-encrypted output.
- NCM and KWM: deterministic offline algorithms with streaming and malformed-input checks.
- KGM v3: deterministic offline algorithm.
- KGM v5: reads the current user's Kugou `KGMusicV3.db` or a selected copy, matches `AudioHash` to EKey, and performs offline decryption. Both plain and Kugou page-encrypted SQLite layouts are supported. Missing EKey remains a hard stop.
- QQ iOS TM0/TM2/TM3/TM6: validates standard payloads or restores the displaced M4A `ftyp` header.
- Xiami XM: validates its 16-byte container header, declared type, bounded XOR start and decrypted audio signature.
- Ximalaya X2M/X3M: uses the upstream MIT scramble tables to restore exactly the first 1024 bytes; `.xm` ambiguity is handled by probing both Xiami and Ximalaya decoders.
- Standard input: FLAC, WAV, MP3, OGG/Opus, M4A/MP4, AAC, APE, WMA, AIFF, DSF and DFF are accepted only when their real header is recognized.

Portable QMC keyrings accept JSON maps/arrays or text entries in `identifier=EKey`, `identifier|EKey` or tab-separated form. A bare EKey is treated as a fallback candidate. Identifier matches only select candidates; the audio signature must still verify before a key is accepted. Key values are never printed by the CLI.

## Platform availability

| Platform | Status | Notes |
| --- | --- | --- |
| Windows 10/11 x64 | Supported | Mature WinForms release plus portable preview shell |
| macOS 12+ arm64/x64 | Preview | Offline core and packages are implemented; stable status waits for Actions and real-device tests |
| Windows 7 SP1 x64 | Not yet shipped | Separate Legacy port required; current .NET 10 binaries are incompatible |

No code path obtains membership rights, server-side keys or account authorization. A platform update can introduce an unsupported variant; please provide a legally redistributable synthetic or public fixture when reporting one.
