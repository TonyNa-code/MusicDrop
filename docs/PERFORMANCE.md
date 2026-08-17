# Performance and reliability

## Current design

- QMC, NCM, KWM and KGM use 1 MiB buffers rented from `ArrayPool<byte>`.
- Files are streamed from read-only input to a new output; whole-song buffering is not used.
- Buffers are cleared before returning to the shared pool.
- Batch preflight and conversion use bounded workers. The portable default is half the logical CPU count, clamped to 1–4; `--jobs` allows 1–16.
- Output names are reserved before workers start, preventing same-title races.
- `ORIGINAL` avoids FFmpeg and preserves decoded bytes exactly.
- FLAC/WAV/MP3/OGG use separate FFmpeg processes. Parallelism improves throughput for batches but can be reduced for slow disks or thermally constrained laptops.

## Baseline

The cross-platform harness decrypts and SHA-256 verifies a deterministic 32 MiB KWM vector. On the development Windows x64 machine, repeated runs after the 1 MiB pooled-I/O change measured approximately **620–690 MiB/s** (the final release-candidate run measured **685.5 MiB/s**). This is a local synthetic measurement, not a universal product guarantee; storage, CPU, antivirus and power policy materially affect results.

Run the same gate locally:

```bash
dotnet run --project MusicDrop.Core.Harness/MusicDrop.Core.Harness.csproj -c Release
```

## Correctness before speed

Performance changes are rejected if any byte-exact vector, malformed-input check, output signature, source-preservation rule or atomic-output rule fails. WAV additionally compares decoded PCM MD5 between source and destination. This costs an extra decode pass but detects accidental sample changes.

## Future profiling

- benchmark NCM and KGM v3 cipher loops separately on x64 and arm64;
- evaluate SIMD only with byte-exact chunk-boundary tests;
- add disk-aware adaptive concurrency;
- measure FFmpeg encode presets on Apple Silicon before changing defaults;
- record peak working set for 1, 10, 100 and 1,000-file queues.
