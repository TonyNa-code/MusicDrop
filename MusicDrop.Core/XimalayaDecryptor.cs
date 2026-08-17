// SPDX-License-Identifier: MIT
// Format behavior and scramble tables adapted from leafxdd/unlock-music
// algo/ximalaya (Copyright 2020-2021 Unlock Music, MIT License).

using System.Buffers.Binary;
using System.Reflection;

namespace MusicDrop3.MultiPlatform;

internal sealed class XimalayaDecryptor : IPlatformDecryptor
{
    private const int HeaderSize = 1024;
    private static readonly byte[] X2mKey = "xmly"u8.ToArray();
    private static readonly byte[] X3mKey = "3989d111aad5613940f4fc44b639b292"u8.ToArray();
    private static readonly Lazy<ushort[]> X2mTable = new(() => LoadTable("x2m_scramble_table.bin"));
    private static readonly Lazy<ushort[]> X3mTable = new(() => LoadTable("x3m_scramble_table.bin"));

    public IReadOnlyCollection<string> Extensions { get; } = new[] { ".x2m", ".x3m", ".xm" };

    public async Task<PlatformProbeResult> ProbeAsync(
        string inputPath, MultiPlatformOptions options, CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream input = StreamingAudio.OpenRead(inputPath);
            byte[] encrypted = await ReadEncryptedHeaderAsync(input, cancellationToken).ConfigureAwait(false);
            (string Variant, byte[] Header, string Extension) decoded = DecodeHeader(encrypted);
            return new(SourcePlatform.Ximalaya, decoded.Variant, decoded.Extension, true, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(SourcePlatform.Ximalaya, "Ximalaya X2M/X3M", null, false, false, ex.Message);
        }
    }

    public async Task<PlatformDecryptResult> DecryptAsync(
        string inputPath, string outputPath, MultiPlatformOptions options, CancellationToken cancellationToken)
    {
        await using FileStream input = StreamingAudio.OpenRead(inputPath);
        byte[] encrypted = await ReadEncryptedHeaderAsync(input, cancellationToken).ConfigureAwait(false);
        (string Variant, byte[] Header, string Extension) decoded = DecodeHeader(encrypted);
        await using FileStream output = StreamingAudio.OpenWriteNew(outputPath);
        await output.WriteAsync(decoded.Header, cancellationToken).ConfigureAwait(false);
        long written = HeaderSize + await StreamingAudio.CopyAsync(
            input, output, null, cancellationToken).ConfigureAwait(false);
        return new(SourcePlatform.Ximalaya, decoded.Variant, decoded.Extension, written,
            $"离线（喜马拉雅 {decoded.Variant}）");
    }

    private static async Task<byte[]> ReadEncryptedHeaderAsync(
        FileStream input, CancellationToken cancellationToken)
    {
        if (input.Length < HeaderSize)
            throw new InvalidDataException("X2M/X3M 文件短于 1024-byte 加密头部。");
        byte[] header = new byte[HeaderSize];
        await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        return header;
    }

    private static (string Variant, byte[] Header, string Extension) DecodeHeader(byte[] encrypted)
    {
        byte[] x2m = Unscramble(encrypted, X2mTable.Value, X2mKey);
        string? x2mExtension = AudioSignatures.Detect(x2m);
        if (x2mExtension is not null) return ("X2M", x2m, x2mExtension);

        byte[] x3m = Unscramble(encrypted, X3mTable.Value, X3mKey);
        string? x3mExtension = AudioSignatures.Detect(x3m);
        if (x3mExtension is not null) return ("X3M", x3m, x3mExtension);
        throw new InvalidDataException("X2M/X3M 置换表校验通过，但解密后的音频头未知。");
    }

    private static byte[] Unscramble(byte[] encrypted, ushort[] table, byte[] key)
    {
        var output = new byte[HeaderSize];
        for (int destination = 0; destination < HeaderSize; destination++)
        {
            int source = table[destination];
            if ((uint)source >= HeaderSize)
                throw new InvalidDataException("X2M/X3M 置换表包含越界索引。");
            output[destination] = (byte)(encrypted[source] ^ key[destination % key.Length]);
        }
        return output;
    }

    internal static byte[] ScrambleForTests(ReadOnlySpan<byte> plaintextHeader, bool x3m)
    {
        if (plaintextHeader.Length != HeaderSize)
            throw new ArgumentException("Synthetic header must be exactly 1024 bytes.", nameof(plaintextHeader));
        ushort[] table = x3m ? X3mTable.Value : X2mTable.Value;
        byte[] key = x3m ? X3mKey : X2mKey;
        var encrypted = new byte[HeaderSize];
        for (int destination = 0; destination < HeaderSize; destination++)
            encrypted[table[destination]] = (byte)(plaintextHeader[destination] ^ key[destination % key.Length]);
        return encrypted;
    }

    private static ushort[] LoadTable(string fileName)
    {
        Assembly assembly = typeof(XimalayaDecryptor).Assembly;
        string resource = assembly.GetManifestResourceNames().Single(name =>
            name.EndsWith(fileName, StringComparison.Ordinal));
        using Stream stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidDataException("缺少嵌入式置换表：" + fileName);
        byte[] raw = new byte[HeaderSize * 2];
        stream.ReadExactly(raw);
        if (stream.ReadByte() != -1) throw new InvalidDataException("置换表长度异常：" + fileName);
        var table = new ushort[HeaderSize];
        var seen = new bool[HeaderSize];
        for (int i = 0; i < table.Length; i++)
        {
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(i * 2, 2));
            if (value >= HeaderSize || seen[value])
                throw new InvalidDataException("置换表不是有效的 1024 项排列：" + fileName);
            table[i] = value;
            seen[value] = true;
        }
        return table;
    }
}
