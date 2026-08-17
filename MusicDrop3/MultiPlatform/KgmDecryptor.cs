// SPDX-License-Identifier: MIT
// Adapted from leafxdd/unlock-music algo/kgm (Copyright 2020-2021 Unlock
// Music, MIT License). See THIRD-PARTY-NOTICES.txt.

using System.Buffers.Binary;
using System.Security.Cryptography;
using MFlacDrop.OfflineQmc;

namespace MusicDrop3.MultiPlatform;

internal sealed class KgmDecryptor : IPlatformDecryptor
{
    private static readonly byte[] KgmMagic =
    {
        0x7C, 0xD5, 0x32, 0xEB, 0x86, 0x02, 0x7F, 0x4B,
        0xA8, 0xAF, 0xA6, 0x8E, 0x0F, 0xFF, 0x99, 0x14,
    };
    private static readonly byte[] VprMagic =
    {
        0x05, 0x28, 0xBC, 0x96, 0xE9, 0xE4, 0x5A, 0x43,
        0x91, 0xAA, 0xBD, 0xD0, 0x7A, 0xF5, 0x36, 0x31,
    };

    public IReadOnlyCollection<string> Extensions { get; } =
        new[] { ".kgm", ".kgma", ".vpr", ".kgg", ".kgm.flac", ".vpr.flac" };

    public async Task<PlatformProbeResult> ProbeAsync(
        string inputPath, MultiPlatformOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = OpenRead(inputPath);
            KgmHeader header = await ParseHeaderAsync(stream, cancellationToken);
            if (header.CryptoVersion == 5)
            {
                string ekey;
                try
                {
                    ekey = await ResolveV5EKeyAsync(header, options, cancellationToken);
                }
                catch (FileNotFoundException ex)
                {
                    return new(SourcePlatform.KugouMusic, "KGM v5", null, false, true,
                        ex.Message, header.AudioHash);
                }
                catch (KugouKeyNotFoundException ex)
                {
                    return new(SourcePlatform.KugouMusic, "KGM v5", null, false, true,
                        ex.Message, ex.AudioHash);
                }
                IQmcCipher v5Cipher = QmcCipherFactory.FromMasterKey(QmcEKey.DeriveMasterKey(ekey));
                byte[] v5AudioHeader = await ReadDecryptedHeaderAsync(
                    stream, header, v5Cipher.Transform, cancellationToken);
                string? v5Extension = AudioSignatures.Detect(v5AudioHeader);
                return v5Extension is null
                    ? new(SourcePlatform.KugouMusic, "KGM v5", null, false, false,
                        "KGM v5 密钥已找到，但解密后的音频头未知；密钥可能不匹配。", header.AudioHash)
                    : new(SourcePlatform.KugouMusic, "KGM v5", v5Extension, true, false,
                        KeyIdentifier: header.AudioHash);
            }
            if (header.CryptoVersion != 3)
                return new(SourcePlatform.KugouMusic, $"KGM v{header.CryptoVersion}", null, false, false,
                    "不支持的 KGM 加密版本。");

            KgmV3Cipher cipher = new(header);
            byte[] audioHeader = await ReadDecryptedHeaderAsync(stream, header, cipher, cancellationToken);
            string? extension = AudioSignatures.Detect(audioHeader);
            return extension is null
                ? new(SourcePlatform.KugouMusic, "KGM v3", null, false, false,
                    "KGM v3 文件头有效，但解密后的音频头未知。")
                : new(SourcePlatform.KugouMusic, "KGM v3", extension, true, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            CryptographicException)
        {
            return new(SourcePlatform.KugouMusic, "KGM", null, false, false, ex.Message);
        }
    }

    public async Task<PlatformDecryptResult> DecryptAsync(
        string inputPath, string outputPath, MultiPlatformOptions options, CancellationToken cancellationToken)
    {
        await using FileStream input = OpenRead(inputPath);
        KgmHeader header = await ParseHeaderAsync(input, cancellationToken);
        if (header.CryptoVersion == 5)
        {
            string ekey = await ResolveV5EKeyAsync(header, options, cancellationToken);
            IQmcCipher v5Cipher = QmcCipherFactory.FromMasterKey(QmcEKey.DeriveMasterKey(ekey));
            byte[] v5AudioHeader = await ReadDecryptedHeaderAsync(
                input, header, v5Cipher.Transform, cancellationToken);
            string v5Extension = AudioSignatures.Detect(v5AudioHeader)
                ?? throw new InvalidDataException("KGM v5 解密后的音频头未知，已拒绝输出。");
            long written = await DecryptAudioAsync(
                input, outputPath, header, v5Cipher.Transform, cancellationToken);
            return new(SourcePlatform.KugouMusic, "KGM v5", v5Extension, written,
                "离线（酷狗 KGM v5 本地密钥库）");
        }
        if (header.CryptoVersion != 3)
            throw new InvalidDataException("不支持的 KGM 加密版本：" + header.CryptoVersion);

        KgmV3Cipher cipher = new(header);
        byte[] audioHeader = await ReadDecryptedHeaderAsync(input, header, cipher, cancellationToken);
        string extension = AudioSignatures.Detect(audioHeader)
            ?? throw new InvalidDataException("KGM v3 解密后的音频头未知，已拒绝输出。");

        long bytesWritten = await DecryptAudioAsync(
            input, outputPath, header, cipher.Decrypt, cancellationToken);
        return new(SourcePlatform.KugouMusic, "KGM v3", extension, bytesWritten, "离线（酷狗 KGM v3）");
    }

    private static async Task<long> DecryptAudioAsync(
        FileStream input,
        string outputPath,
        KgmHeader header,
        AudioTransform transform,
        CancellationToken cancellationToken)
    {
        input.Position = header.AudioOffset;
        await using FileStream output = StreamingAudio.OpenWriteNew(outputPath);
        return await StreamingAudio.CopyAsync(
            input, output, transform.Invoke, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ResolveV5EKeyAsync(
        KgmHeader header,
        MultiPlatformOptions options,
        CancellationToken cancellationToken)
    {
        string audioHash = header.AudioHash
            ?? throw new InvalidDataException("KGM v5 文件头缺少 AudioHash。");
        string? ekey = await KugouDatabaseReader.FindEKeyAsync(
            options.KugouDatabasePath, audioHash, cancellationToken);
        if (string.IsNullOrWhiteSpace(ekey))
            throw new KugouKeyNotFoundException(audioHash,
                $"酷狗本地密钥库中没有此 KGM v5 的匹配 EKey（AudioHash={audioHash}）。请确认该歌曲已由当前账号合法下载。 ");
        return ekey;
    }

    private static async Task<KgmHeader> ParseHeaderAsync(FileStream stream, CancellationToken ct)
    {
        if (stream.Length < 60) throw new InvalidDataException("KGM 文件头被截断。");
        byte[] fixedHeader = new byte[60];
        await stream.ReadExactlyAsync(fixedHeader, ct);
        if (!fixedHeader.AsSpan(0, 16).SequenceEqual(KgmMagic) &&
            !fixedHeader.AsSpan(0, 16).SequenceEqual(VprMagic))
            throw new InvalidDataException("KGM/VPR 文件头不匹配。");

        uint audioOffset = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader.AsSpan(0x10, 4));
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader.AsSpan(0x14, 4));
        uint slot = BinaryPrimitives.ReadUInt32LittleEndian(fixedHeader.AsSpan(0x18, 4));
        byte[] cryptoKey = fixedHeader.AsSpan(0x2C, 16).ToArray();
        string? audioHash = null;
        if (version == 5)
        {
            stream.Position = 0x44;
            byte[] rawLength = new byte[4];
            await stream.ReadExactlyAsync(rawLength, ct);
            uint hashLength = BinaryPrimitives.ReadUInt32LittleEndian(rawLength);
            if (hashLength > stream.Length - stream.Position || hashLength > 4096)
                throw new InvalidDataException("KGM v5 音频哈希长度超出安全边界。");
            byte[] hash = new byte[checked((int)hashLength)];
            await stream.ReadExactlyAsync(hash, ct);
            audioHash = System.Text.Encoding.UTF8.GetString(hash);
        }
        if (audioOffset < 60 || audioOffset > stream.Length)
            throw new InvalidDataException("KGM 音频偏移超出文件边界。");
        return new(audioOffset, version, slot, cryptoKey, audioHash);
    }

    private static async Task<byte[]> ReadDecryptedHeaderAsync(
        FileStream stream, KgmHeader header, KgmV3Cipher cipher, CancellationToken ct)
        => await ReadDecryptedHeaderAsync(stream, header, cipher.Decrypt, ct);

    private static async Task<byte[]> ReadDecryptedHeaderAsync(
        FileStream stream, KgmHeader header, AudioTransform transform, CancellationToken ct)
    {
        stream.Position = header.AudioOffset;
        int count = (int)Math.Min(64, stream.Length - stream.Position);
        byte[] buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, ct);
        transform(buffer, 0);
        return buffer;
    }

    private static FileStream OpenRead(string path) => StreamingAudio.OpenRead(path);

    private sealed record KgmHeader(
        uint AudioOffset,
        uint CryptoVersion,
        uint CryptoSlot,
        byte[] CryptoKey,
        string? AudioHash);

    private delegate void AudioTransform(Span<byte> buffer, long offset);

    private sealed class KgmV3Cipher
    {
        private readonly byte[] slotBox;
        private readonly byte[] fileBox;

        public KgmV3Cipher(KgmHeader header)
        {
            if (header.CryptoSlot != 1)
                throw new InvalidDataException("KGM v3 未知密钥槽：" + header.CryptoSlot);
            slotBox = KugouMd5(new byte[] { 0x6C, 0x2C, 0x2F, 0x27 });
            fileBox = [.. KugouMd5(header.CryptoKey), 0x6B];
        }

        public void Decrypt(Span<byte> buffer, long offset)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                long position = offset + i;
                byte value = buffer[i];
                value ^= fileBox[(int)(position % fileBox.Length)];
                value ^= unchecked((byte)(value << 4));
                value ^= slotBox[(int)(position % slotBox.Length)];
                uint p = checked((uint)position);
                value ^= (byte)(p ^ (p >> 8) ^ (p >> 16) ^ (p >> 24));
                buffer[i] = value;
            }
        }

        private static byte[] KugouMd5(byte[] input)
        {
            byte[] digest = MD5.HashData(input);
            byte[] result = new byte[16];
            for (int i = 0; i < 16; i += 2)
            {
                result[i] = digest[14 - i];
                result[i + 1] = digest[15 - i];
            }
            return result;
        }
    }

    private sealed class KugouKeyNotFoundException : IOException
    {
        public string AudioHash { get; }

        public KugouKeyNotFoundException(string audioHash, string message) : base(message)
        {
            AudioHash = audioHash;
        }
    }
}
