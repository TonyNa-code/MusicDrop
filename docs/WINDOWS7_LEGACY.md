# Windows 7 SP1 Legacy plan

The current MusicDrop 3.1 applications target .NET 10 and **do not run on Windows 7**. Renaming the executable, suppressing a version check or using an unsupported runtime hack would not create reliable compatibility.

The planned Legacy edition is a separate x64 deliverable with reduced scope:

- .NET Framework 4.8 WinForms shell or a separately reviewed native shell;
- backported offline core with compatibility wrappers for modern stream, hash and filesystem APIs;
- a Windows 7-compatible, bundled LGPL FFmpeg build;
- no dependence on modern TLS for first-run downloads;
- Windows 7 SP1 + SHA-2 servicing prerequisites documented explicitly;
- dedicated clean-VM tests for drag/drop, Unicode paths, cancellation, long batches and every supported decoder.

Windows 7 is end-of-life. The package must be labelled **Legacy / best-effort security support**, kept separate from Windows 10/11, and never presented as receiving the same platform security guarantees.

Release criteria:

1. build without unsupported .NET runtime substitution;
2. pass byte-exact offline vectors in a real Windows 7 SP1 x64 VM;
3. pass bundled FFmpeg FLAC/WAV/MP3/OGG output verification;
4. prove source preservation and partial-file cleanup under cancellation;
5. document features intentionally absent from Legacy.

No Windows 7 binary is published from this repository until all five criteria pass.
