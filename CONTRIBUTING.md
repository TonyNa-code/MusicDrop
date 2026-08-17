# Contributing

Contributions that improve correctness, safety, accessibility, performance, documentation or lawful format interoperability are welcome.

1. Do not commit real music, account data, platform databases, EKeys, buyer licenses or seller keys.
2. Add deterministic synthetic vectors or redistributable public fixtures for format changes.
3. Preserve the rule that source files are never modified or deleted.
4. Treat a conversion as successful only after validating the decoded output.
5. Run `dotnet build MusicDrop.slnx -c Release`, `MusicDrop.Core.Harness`, and the relevant Windows integration checks. Portable changes must also build through `MusicDrop.Portable.slnx` on Windows and macOS CI.
6. Explain license provenance for any copied algorithm, binary or asset.

Changes intended to bypass subscription access, obtain service-side keys or evade account authorization are out of scope.
