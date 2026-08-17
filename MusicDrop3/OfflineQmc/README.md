# Offline QMC backend

These three C# source files implement a dependency-free, offline QMC/MFLAC
probe and decrypt backend for MFLAC Drop. They are automatically included by
the SDK-style `MFlacDrop.csproj`; no package reference is needed.

Supported paths:

- footerless QMC v1 static cipher;
- PC v1 legacy footer with an embedded EKey;
- Android QTag with an embedded EKey;
- PC MusicEx v1 and Android STag with an externally supplied EKey;
- EKey v1 and `QQMusic EncV2,Key:` v2 derivation;
- QMC Map and modified-RC4 stream ciphers.

Minimal use:

```csharp
using MFlacDrop.OfflineQmc;

QmcProbeResult probe = await OfflineQmcDecryptor.ProbeAsync(inputPath, ekey, ct);
if (!probe.CanDecrypt)
    throw new InvalidOperationException(probe.Error);

QmcDecryptResult result = await OfflineQmcDecryptor.DecryptAsync(
    inputPath,
    temporaryOutputPath,
    ekey,
    progress,
    ct);
```

For MusicEx, resolve the EKey by `probe.MediaFileName` first, then optionally
by `probe.MediaId`; call `ProbeAsync` or `DecryptAsync` again with that EKey.
The API intentionally performs no network or QQ Music process access.

`DecryptAsync(path, path, ...)` is rejected. The path overload creates the
destination exclusively and deletes it after a failure/cancellation. The
stream overload leaves both streams open and never reads beyond `AudioLength`,
so the encrypted footer is not copied into the result.

## Attribution

The format and algorithms are adapted from `leafxdd/unlock-music`, specifically
`algo/qmc`, licensed under MIT. The complete upstream MIT notice is retained at
the top of `OfflineQmcCrypto.cs`.
