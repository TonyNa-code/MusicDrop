// SPDX-License-Identifier: MIT
// Adapted from leafxdd/unlock-music algo/ncm (Copyright 2020-2021 Unlock
// Music, MIT License). See THIRD-PARTY-NOTICES.txt.

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MusicDrop3.MultiPlatform;

internal sealed class NcmDecryptor : IPlatformDecryptor
{
    private static readonly byte[] Magic = "CTENFDAM"u8.ToArray();
    private static readonly byte[] CoreKey =
    {
        0x68, 0x7A, 0x48, 0x52, 0x41, 0x6D, 0x73, 0x6F,
        0x35, 0x6B, 0x49, 0x6E, 0x62, 0x61, 0x78, 0x57,
    };

    public IReadOnlyCollection<string> Extensions { get; } = new[] { ".ncm" };

    public async Task<PlatformProbeResult> ProbeAsync(
        string inputPath,
        MultiPlatformOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = OpenRead(inputPath);
            NcmState state = await ParseAsync(stream, cancellationToken);
            byte[] header = await ReadDecryptedHeaderAsync(stream, state, cancellationToken);
            string? extension = AudioSignatures.Detect(header);
            return extension is null
                ? new(SourcePlatform.NeteaseCloudMusic, "NCM", null, false, false,
                    "NCM 密钥区可解析，但解密后的音频头未知。")
                : new(SourcePlatform.NeteaseCloudMusic, "NCM", extension, true, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or CryptographicException)
        {
            return new(SourcePlatform.NeteaseCloudMusic, "NCM", null, false, false, ex.Message);
        }
    }

    public async Task<PlatformDecryptResult> DecryptAsync(
        string inputPath,
        string outputPath,
        MultiPlatformOptions options,
        CancellationToken cancellationToken)
    {
        await using FileStream input = OpenRead(inputPath);
        NcmState state = await ParseAsync(input, cancellationToken);
        byte[] header = await ReadDecryptedHeaderAsync(input, state, cancellationToken);
        string extension = AudioSignatures.Detect(header)
            ?? throw new InvalidDataException("NCM 解密后的音频头未知，已拒绝输出。");

        input.Position = state.AudioOffset;
        await using FileStream output = StreamingAudio.OpenWriteNew(outputPath);
        long offset = await StreamingAudio.CopyAsync(
            input, output, state.Cipher.Transform, cancellationToken).ConfigureAwait(false);
        return new(SourcePlatform.NeteaseCloudMusic, "NCM", extension, offset, "离线（网易云 NCM）");
    }

    private static async Task<NcmState> ParseAsync(FileStream stream, CancellationToken cancellationToken)
    {
        byte[] magic = await ReadExactlyAsync(stream, Magic.Length, "NCM 文件头", cancellationToken);
        if (!magic.AsSpan().SequenceEqual(Magic))
            throw new InvalidDataException("NCM 文件头不匹配。");
        await SkipAsync(stream, 2, "NCM 保留字段");

        uint keyLength = await ReadUInt32Async(stream, "NCM 密钥长度", cancellationToken);
        byte[] encryptedKey = await ReadBoundedAsync(stream, keyLength, "NCM 密钥数据", cancellationToken);
        foreach (ref byte value in encryptedKey.AsSpan()) value ^= 0x64;
        byte[] keyPlain = DecryptAesEcbPkcs7(encryptedKey, CoreKey);
        if (keyPlain.Length <= 17)
            throw new InvalidDataException("NCM 解密密钥长度异常。");
        byte[] audioKey = keyPlain[17..];

        uint metadataLength = await ReadUInt32Async(stream, "NCM 元数据长度", cancellationToken);
        await SkipBoundedAsync(stream, metadataLength, "NCM 元数据");
        await SkipAsync(stream, 5, "NCM 元数据保留字段");

        uint coverFrameLength = await ReadUInt32Async(stream, "NCM 封面帧长度", cancellationToken);
        long coverFrameStart = stream.Position;
        _ = await ReadUInt32Async(stream, "NCM 封面长度", cancellationToken);
        long audioOffset = checked(coverFrameStart + coverFrameLength + 4L);
        if (audioOffset < stream.Position || audioOffset > stream.Length)
            throw new InvalidDataException("NCM 音频偏移超出文件边界。");
        return new(audioOffset, new NcmCipher(audioKey));
    }

    private static async Task<byte[]> ReadDecryptedHeaderAsync(
        FileStream stream,
        NcmState state,
        CancellationToken cancellationToken)
    {
        stream.Position = state.AudioOffset;
        int count = (int)Math.Min(64, stream.Length - stream.Position);
        byte[] header = await ReadExactlyAsync(stream, count, "NCM 音频头", cancellationToken);
        state.Cipher.Transform(header, 0);
        return header;
    }

    private static byte[] DecryptAesEcbPkcs7(byte[] ciphertext, byte[] key)
    {
        if (ciphertext.Length == 0 || ciphertext.Length % 16 != 0)
            throw new InvalidDataException("NCM AES 密文长度无效。");
        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = key;
        using ICryptoTransform decryptor = aes.CreateDecryptor();
        try { return decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length); }
        catch (CryptographicException ex) { throw new InvalidDataException("NCM 密钥区 AES/填充校验失败。", ex); }
    }

    private static FileStream OpenRead(string path) => StreamingAudio.OpenRead(path);

    private static async Task<uint> ReadUInt32Async(FileStream stream, string label, CancellationToken ct)
    {
        byte[] raw = await ReadExactlyAsync(stream, 4, label, ct);
        return BinaryPrimitives.ReadUInt32LittleEndian(raw);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        FileStream stream, uint length, string label, CancellationToken ct)
    {
        if (length > stream.Length - stream.Position || length > 64 * 1024 * 1024)
            throw new InvalidDataException($"{label}长度超出安全边界：{length}。");
        return await ReadExactlyAsync(stream, checked((int)length), label, ct);
    }

    private static Task SkipBoundedAsync(FileStream stream, uint length, string label)
    {
        if (length > stream.Length - stream.Position || length > 64 * 1024 * 1024)
            throw new InvalidDataException($"{label}长度超出安全边界：{length}。");
        stream.Position += length;
        return Task.CompletedTask;
    }

    private static Task SkipAsync(FileStream stream, int count, string label)
    {
        if (count < 0 || count > stream.Length - stream.Position)
            throw new InvalidDataException($"{label}超出文件边界。");
        stream.Position += count;
        return Task.CompletedTask;
    }

    private static async Task<byte[]> ReadExactlyAsync(
        FileStream stream, int count, string label, CancellationToken ct)
    {
        if (count < 0 || count > stream.Length - stream.Position)
            throw new InvalidDataException($"{label}被截断。");
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, ct);
        return buffer;
    }

    private sealed record NcmState(long AudioOffset, NcmCipher Cipher);

    private sealed class NcmCipher
    {
        private readonly byte[] box;

        public NcmCipher(byte[] key)
        {
            if (key.Length == 0) throw new InvalidDataException("NCM 音频密钥为空。");
            byte[] work = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
            byte j = 0;
            for (int i = 0; i < 256; i++)
            {
                j = unchecked((byte)(work[i] + j + key[i % key.Length]));
                (work[i], work[j]) = (work[j], work[i]);
            }
            box = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                byte index = unchecked((byte)(i + 1));
                byte si = work[index];
                byte sj = work[unchecked((byte)(index + si))];
                box[i] = work[unchecked((byte)(si + sj))];
            }
        }

        public void Transform(Span<byte> buffer, long offset)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] ^= box[(int)((offset + i) & 0xFF)];
        }
    }
}
