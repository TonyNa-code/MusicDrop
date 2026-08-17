// SPDX-License-Identifier: MIT
// Adapted from leafxdd/unlock-music algo/kwm (Copyright 2020-2021 Unlock
// Music, MIT License). See THIRD-PARTY-NOTICES.txt.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MusicDrop3.MultiPlatform;

internal sealed class KwmDecryptor : IPlatformDecryptor
{
    private static readonly byte[] Magic1 = Encoding.ASCII.GetBytes("yeelion-kuwo-tme");
    private static readonly byte[] Magic2 = Encoding.ASCII.GetBytes("yeelion-kuwo\0\0\0\0");
    private static readonly byte[] PredefinedKey = Encoding.ASCII.GetBytes("MoOtOiTvINGwd2E6n0E1i7L5t2IoOoNk");

    public IReadOnlyCollection<string> Extensions { get; } = new[] { ".kwm", ".kw" };

    public async Task<PlatformProbeResult> ProbeAsync(
        string inputPath, MultiPlatformOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = OpenRead(inputPath);
            KwmState state = await ParseAsync(stream, cancellationToken);
            byte[] header = await ReadDecryptedHeaderAsync(stream, state, cancellationToken);
            string? detected = AudioSignatures.Detect(header);
            return detected is null
                ? new(SourcePlatform.KuwoMusic, "KWM", state.DeclaredExtension, false, false,
                    "KWM 文件头有效，但解密后的音频头未知。")
                : new(SourcePlatform.KuwoMusic, "KWM", detected, true, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(SourcePlatform.KuwoMusic, "KWM", null, false, false, ex.Message);
        }
    }

    public async Task<PlatformDecryptResult> DecryptAsync(
        string inputPath, string outputPath, MultiPlatformOptions options, CancellationToken cancellationToken)
    {
        await using FileStream input = OpenRead(inputPath);
        KwmState state = await ParseAsync(input, cancellationToken);
        byte[] header = await ReadDecryptedHeaderAsync(input, state, cancellationToken);
        string extension = AudioSignatures.Detect(header)
            ?? throw new InvalidDataException("KWM 解密后的音频头未知，已拒绝输出。");

        input.Position = 0x400;
        await using FileStream output = StreamingAudio.OpenWriteNew(outputPath);
        long offset = await StreamingAudio.CopyAsync(input, output,
            (buffer, position) => Transform(buffer, position, state.Mask),
            cancellationToken).ConfigureAwait(false);
        return new(SourcePlatform.KuwoMusic, "KWM", extension, offset, "离线（酷我 KWM）");
    }

    private static async Task<KwmState> ParseAsync(FileStream stream, CancellationToken ct)
    {
        if (stream.Length < 0x400) throw new InvalidDataException("KWM 文件短于固定 1024-byte 头部。");
        byte[] header = new byte[0x400];
        await stream.ReadExactlyAsync(header, ct);
        if (!header.AsSpan(0, 16).SequenceEqual(Magic1) && !header.AsSpan(0, 16).SequenceEqual(Magic2))
            throw new InvalidDataException("KWM 文件头不匹配。");

        ulong keyNumber = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x18, 8));
        string digits = keyNumber.ToString(CultureInfo.InvariantCulture);
        byte[] padded = new byte[32];
        for (int i = 0; i < padded.Length; i++) padded[i] = (byte)digits[i % digits.Length];
        byte[] mask = new byte[32];
        for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(PredefinedKey[i] ^ padded[i]);

        string declared = Encoding.ASCII.GetString(header, 0x30, 8).TrimEnd('\0');
        int split = 0;
        while (split < declared.Length && char.IsDigit(declared[split])) split++;
        string? declaredExtension = split < declared.Length ? "." + declared[split..].ToLowerInvariant() : null;
        return new(mask, declaredExtension);
    }

    private static async Task<byte[]> ReadDecryptedHeaderAsync(FileStream stream, KwmState state, CancellationToken ct)
    {
        stream.Position = 0x400;
        int count = (int)Math.Min(64, stream.Length - stream.Position);
        byte[] header = new byte[count];
        await stream.ReadExactlyAsync(header, ct);
        Transform(header, 0, state.Mask);
        return header;
    }

    private static void Transform(Span<byte> buffer, long offset, byte[] mask)
    {
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] ^= mask[(int)((offset + i) & 0x1F)];
    }

    private static FileStream OpenRead(string path) => StreamingAudio.OpenRead(path);

    private sealed record KwmState(byte[] Mask, string? DeclaredExtension);
}
