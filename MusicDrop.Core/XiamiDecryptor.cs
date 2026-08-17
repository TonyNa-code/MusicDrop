// SPDX-License-Identifier: MIT
// Format behavior adapted from leafxdd/unlock-music algo/xiami (MIT).

using System.Buffers.Binary;
using System.Text;

namespace MusicDrop3.MultiPlatform;

internal sealed class XiamiDecryptor : IPlatformDecryptor
{
    private static readonly byte[] SecondaryMagic = { 0xFE, 0xFE, 0xFE, 0xFE };
    private static readonly IReadOnlyDictionary<string, string> TypeMapping =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [" WAV"] = ".wav",
            ["FLAC"] = ".flac",
            [" MP3"] = ".mp3",
            [" A4M"] = ".m4a",
        };

    public IReadOnlyCollection<string> Extensions { get; } = new[] { ".xm" };

    public async Task<PlatformProbeResult> ProbeAsync(
        string inputPath, MultiPlatformOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream input = StreamingAudio.OpenRead(inputPath);
            XiamiHeader state = await ParseAsync(input, cancellationToken).ConfigureAwait(false);
            byte[] audioHeader = await ReadAudioHeaderAsync(input, state, cancellationToken).ConfigureAwait(false);
            string? detected = AudioSignatures.Detect(audioHeader);
            if (detected is null)
                return new(SourcePlatform.XiamiMusic, "Xiami XM", state.Extension, false, false,
                    "XM 文件头有效，但解密后的音频头未知。可能是损坏文件或不匹配的掩码。");
            if (!string.Equals(detected, state.Extension, StringComparison.OrdinalIgnoreCase))
                return new(SourcePlatform.XiamiMusic, "Xiami XM", detected, false, false,
                    $"XM 声明格式 {state.Extension} 与真实音频头 {detected} 不一致。");
            return new(SourcePlatform.XiamiMusic, "Xiami XM", detected, true, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(SourcePlatform.XiamiMusic, "Xiami XM", null, false, false, ex.Message);
        }
    }

    public async Task<PlatformDecryptResult> DecryptAsync(
        string inputPath, string outputPath, MultiPlatformOptions options, CancellationToken cancellationToken)
    {
        await using FileStream input = StreamingAudio.OpenRead(inputPath);
        XiamiHeader state = await ParseAsync(input, cancellationToken).ConfigureAwait(false);
        byte[] audioHeader = await ReadAudioHeaderAsync(input, state, cancellationToken).ConfigureAwait(false);
        string extension = AudioSignatures.Detect(audioHeader)
            ?? throw new InvalidDataException("XM 解密后的音频头未知，已拒绝输出。");
        if (!string.Equals(extension, state.Extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"XM 声明格式 {state.Extension} 与真实音频头 {extension} 不一致。");

        input.Position = 16;
        await using FileStream output = StreamingAudio.OpenWriteNew(outputPath);
        long written = await StreamingAudio.CopyAsync(input, output,
            (buffer, offset) => Xor(buffer, offset, state.EncryptStartAt, state.Mask),
            cancellationToken).ConfigureAwait(false);
        return new(SourcePlatform.XiamiMusic, "Xiami XM", extension, written,
            "离线（虾米 XM）");
    }

    private static async Task<XiamiHeader> ParseAsync(FileStream input, CancellationToken cancellationToken)
    {
        if (input.Length < 32) throw new InvalidDataException("XM 文件头或音频载荷被截断。");
        byte[] header = new byte[16];
        await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("ifmt"u8) ||
            !header.AsSpan(8, 4).SequenceEqual(SecondaryMagic))
            throw new InvalidDataException("XM 文件头不匹配。");
        string declared = Encoding.ASCII.GetString(header, 4, 4);
        if (!TypeMapping.TryGetValue(declared, out string? extension))
            throw new InvalidDataException("XM 声明了未知音频类型：" + declared);
        int encryptedStart = header[12] | (header[13] << 8) | (header[14] << 16);
        if (encryptedStart > input.Length - 16)
            throw new InvalidDataException("XM 加密起点超出音频载荷边界。");
        return new(extension, encryptedStart, header[15]);
    }

    private static async Task<byte[]> ReadAudioHeaderAsync(
        FileStream input, XiamiHeader state, CancellationToken cancellationToken)
    {
        input.Position = 16;
        byte[] header = new byte[(int)Math.Min(64, input.Length - 16)];
        await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        Xor(header, 0, state.EncryptStartAt, state.Mask);
        return header;
    }

    private static void Xor(Span<byte> buffer, long offset, int encryptStartAt, byte mask)
    {
        int start = offset >= encryptStartAt ? 0 : (int)Math.Min(buffer.Length, encryptStartAt - offset);
        for (int i = start; i < buffer.Length; i++) buffer[i] ^= mask;
    }

    private sealed record XiamiHeader(string Extension, int EncryptStartAt, byte Mask);
}
