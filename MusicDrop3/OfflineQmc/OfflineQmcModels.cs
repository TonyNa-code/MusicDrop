// SPDX-License-Identifier: MIT
//
// The QMC format and cipher behavior represented by these API models was
// implemented with reference to leafxdd/unlock-music (algo/qmc), which is
// distributed under the MIT License. The cryptographic implementation and the
// complete upstream attribution are in OfflineQmcCrypto.cs.

namespace MFlacDrop.OfflineQmc;

/// <summary>Known QMC footer layouts.</summary>
public enum QmcFooterKind
{
    /// <summary>No footer; legacy files use the QMC v1 static cipher.</summary>
    None,

    /// <summary>Classic PC footer containing an EKey and a little-endian length.</summary>
    PcV1Legacy,

    /// <summary>Android footer containing an EKey, resource ID and version.</summary>
    AndroidQTag,

    /// <summary>Android metadata-only footer. An external EKey is required.</summary>
    AndroidSTag,

    /// <summary>PC MusicEx v1 footer. An external EKey is required.</summary>
    PcV2MusicEx
}

/// <summary>The stream cipher selected after resolving an EKey.</summary>
public enum QmcCipherKind
{
    Unknown,
    StaticV1,
    Map,
    ModifiedRc4
}

/// <summary>Where the key used by a probe came from.</summary>
public enum QmcKeySource
{
    None,
    StaticV1,
    EmbeddedEKey,
    ExternalEKey
}

/// <summary>
/// Safe, non-secret information discovered from a QMC file. Embedded EKeys are
/// deliberately not exposed by this record.
/// </summary>
public sealed record QmcProbeResult(
    QmcFooterKind FooterKind,
    long FileLength,
    long AudioLength,
    long FooterLength,
    uint? SongId,
    string? MediaId,
    string? MediaFileName,
    long? ResourceId,
    int? FooterVersion,
    bool HasEmbeddedEKey,
    bool RequiresExternalEKey,
    bool CanDecrypt,
    QmcKeySource KeySource,
    QmcCipherKind CipherKind,
    string? DetectedAudioExtension,
    string? Error)
{
    public bool IsMusicEx => FooterKind == QmcFooterKind.PcV2MusicEx;
}

/// <summary>Progress reported between streaming blocks.</summary>
public sealed record QmcDecryptProgress(long BytesWritten, long TotalBytes)
{
    public double Fraction => TotalBytes <= 0 ? 1 : (double)BytesWritten / TotalBytes;
}

/// <summary>Result returned after a complete, validated decryption.</summary>
public sealed record QmcDecryptResult(
    string? OutputPath,
    long BytesWritten,
    string DetectedAudioExtension,
    QmcProbeResult Probe);

/// <summary>An expected QMC format, key-resolution, or header-validation error.</summary>
public sealed class QmcDecryptException : IOException
{
    public QmcDecryptException(string message, QmcProbeResult? probe = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Probe = probe;
    }

    public QmcProbeResult? Probe { get; }
}
