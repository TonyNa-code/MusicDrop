// SPDX-License-Identifier: MIT
// Format behavior is adapted from leafxdd/unlock-music (Copyright 2020-2021
// Unlock Music, MIT License). See THIRD-PARTY-NOTICES.txt.

namespace MusicDrop3.MultiPlatform;

internal enum SourcePlatform
{
    QqMusic,
    NeteaseCloudMusic,
    KugouMusic,
    KuwoMusic,
    XiamiMusic,
    Ximalaya,
}

internal sealed record PlatformProbeResult(
    SourcePlatform Platform,
    string FormatName,
    string? AudioExtension,
    bool CanDecrypt,
    bool RequiresExternalKey,
    string? Error = null,
    string? KeyIdentifier = null);

internal sealed record PlatformDecryptResult(
    SourcePlatform Platform,
    string FormatName,
    string AudioExtension,
    long BytesWritten,
    string Route);

internal interface IPlatformDecryptor
{
    IReadOnlyCollection<string> Extensions { get; }

    Task<PlatformProbeResult> ProbeAsync(
        string inputPath,
        MultiPlatformOptions options,
        CancellationToken cancellationToken);

    Task<PlatformDecryptResult> DecryptAsync(
        string inputPath,
        string outputPath,
        MultiPlatformOptions options,
        CancellationToken cancellationToken);
}

internal sealed record MultiPlatformOptions(
    string KugouDatabasePath = "");

internal static class AudioSignatures
{
    public static string? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 4 && header[..4].SequenceEqual("fLaC"u8)) return ".flac";
        if (header.Length >= 4 && header[..4].SequenceEqual("OggS"u8))
            return header.IndexOf("OpusHead"u8) >= 0 ? ".opus" : ".ogg";
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WAVE"u8)) return ".wav";
        if (header.Length >= 12 && header[..4].SequenceEqual("FORM"u8) &&
            (header[8..12].SequenceEqual("AIFF"u8) || header[8..12].SequenceEqual("AIFC"u8))) return ".aiff";
        if (header.Length >= 3 && header[..3].SequenceEqual("ID3"u8)) return ".mp3";
        if (header.Length >= 4 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0)
        {
            int version = (header[1] >> 3) & 3;
            int layer = (header[1] >> 1) & 3;
            int bitrate = (header[2] >> 4) & 0xF;
            int sampleRate = (header[2] >> 2) & 3;
            if (version != 1 && layer != 0 && bitrate is not 0 and not 15 && sampleRate != 3)
                return ".mp3";
        }
        if (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xF6) == 0xF0) return ".aac";
        if (header.Length >= 4 && header[..4].SequenceEqual("MAC "u8)) return ".ape";
        if (header.Length >= 4 && header[..4].SequenceEqual("FRM8"u8)) return ".dff";
        if (header.Length >= 4 && header[..4].SequenceEqual("DSD "u8)) return ".dsf";
        ReadOnlySpan<byte> asf =
        [
            0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
            0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C,
        ];
        if (header.StartsWith(asf)) return ".wma";
        if (header.Length >= 16 && header[4..8].SequenceEqual("ftyp"u8))
            return header[8..12].SequenceEqual("M4A "u8) || header.IndexOf("M4A "u8) >= 0 ? ".m4a" : ".mp4";
        return null;
    }

    public static async Task<string?> DetectFileAsync(string path, CancellationToken cancellationToken)
    {
        byte[] header = new byte[64];
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        int read = await stream.ReadAsync(header, cancellationToken);
        return Detect(header.AsSpan(0, read));
    }
}
