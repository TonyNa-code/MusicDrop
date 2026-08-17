// SPDX-License-Identifier: MIT
//
// Footer layouts and cipher selection are adapted from the QMC decoder in
// leafxdd/unlock-music (Copyright (c) 2020-2021 Unlock Music, MIT License).
// The full upstream MIT notice is retained in OfflineQmcCrypto.cs.

using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace MFlacDrop.OfflineQmc;

/// <summary>
/// Offline, streaming QMC/MFLAC probe and decrypt API. It never contacts QQ
/// Music or the network. MusicEx/STag files therefore need a caller-supplied
/// EKey, normally resolved from a local key database.
/// </summary>
public static class OfflineQmcDecryptor
{
    private const int MinimumProbeBytes = 16;
    private const int ProbeBytes = 64;
    private const int DefaultBufferSize = 1024 * 1024;
    private const int MaximumBufferSize = 16 * 1024 * 1024;
    private const int MaximumLegacyEKeyLength = 65_535;
    private const int MaximumQTagLength = 65_535;
    private const int MusicExMinimumSize = 0xC0;

    private static readonly byte[] QTagMagic = Encoding.ASCII.GetBytes("QTag");
    private static readonly byte[] STagMagic = Encoding.ASCII.GetBytes("STag");
    private static readonly byte[] MusicExMagic = Encoding.ASCII.GetBytes("musicex\0");

    /// <summary>Probes a local file without writing output.</summary>
    public static async Task<QmcProbeResult> ProbeAsync(
        string inputPath,
        string? externalEKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        await using var input = new FileStream(
            inputPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            DefaultBufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);
        return await ProbeAsync(input, externalEKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Probes an open, seekable stream. The stream remains open and its original
    /// position is restored even when probing fails.
    /// </summary>
    public static async Task<QmcProbeResult> ProbeAsync(
        Stream input,
        string? externalEKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateInputStream(input);

        long originalPosition = input.Position;
        try
        {
            ParsedFooter footer = await ParseFooterAsync(input, cancellationToken).ConfigureAwait(false);
            KeySelection selection = SelectCipher(footer, externalEKey);
            string? audioExtension = null;
            string? error = selection.Error;

            if (selection.Cipher is not null)
            {
                if (footer.AudioLength < MinimumProbeBytes)
                {
                    error = $"Encrypted audio is too short ({footer.AudioLength} bytes).";
                }
                else
                {
                    int headLength = (int)Math.Min(ProbeBytes, footer.AudioLength);
                    byte[] head = new byte[headLength];
                    input.Position = 0;
                    await ReadExactlyAsync(input, head, cancellationToken).ConfigureAwait(false);
                    selection.Cipher.Transform(head, 0);
                    audioExtension = DetectAudioExtension(head);
                    if (audioExtension is null)
                        error = "The selected key did not decrypt to a recognized audio header.";
                }
            }

            return ToProbeResult(
                footer,
                selection,
                audioExtension,
                error,
                canDecrypt: selection.Cipher is not null && audioExtension is not null && error is null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or FormatException or OverflowException)
        {
            long fileLength = TryGetLength(input);
            return new QmcProbeResult(
                QmcFooterKind.None,
                fileLength,
                0,
                0,
                null,
                null,
                null,
                null,
                null,
                false,
                false,
                false,
                QmcKeySource.None,
                QmcCipherKind.Unknown,
                null,
                ex.Message);
        }
        finally
        {
            input.Position = originalPosition;
        }
    }

    /// <summary>
    /// Decrypts a local file to a new local file. The destination is created
    /// exclusively and is deleted if cancellation or an error occurs.
    /// </summary>
    public static async Task<QmcDecryptResult> DecryptAsync(
        string inputPath,
        string outputPath,
        string? externalEKey = null,
        IProgress<QmcDecryptProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int bufferSize = DefaultBufferSize)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ValidateBufferSize(bufferSize);

        string inputFullPath = Path.GetFullPath(inputPath);
        string outputFullPath = Path.GetFullPath(outputPath);
        if (string.Equals(inputFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Input and output paths must be different.", nameof(outputPath));

        await using var input = new FileStream(
            inputFullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        FileStream? output = null;
        bool completed = false;
        try
        {
            output = new FileStream(
                outputFullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            QmcDecryptResult result = await DecryptCoreAsync(
                input,
                output,
                outputFullPath,
                externalEKey,
                progress,
                cancellationToken,
                bufferSize).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            completed = true;
            return result;
        }
        finally
        {
            if (output is not null)
                await output.DisposeAsync().ConfigureAwait(false);
            if (!completed)
            {
                try { File.Delete(outputFullPath); }
                catch { /* best-effort cleanup; do not mask the original failure */ }
            }
        }
    }

    /// <summary>
    /// Decrypts between caller-owned streams. Both streams remain open. Input
    /// must be seekable so the footer can be parsed without buffering the file.
    /// </summary>
    public static Task<QmcDecryptResult> DecryptAsync(
        Stream input,
        Stream output,
        string? externalEKey = null,
        IProgress<QmcDecryptProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int bufferSize = DefaultBufferSize)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ValidateInputStream(input);
        if (!output.CanWrite)
            throw new ArgumentException("Output stream must be writable.", nameof(output));
        ValidateBufferSize(bufferSize);
        return DecryptCoreAsync(input, output, null, externalEKey, progress, cancellationToken, bufferSize);
    }

    private static async Task<QmcDecryptResult> DecryptCoreAsync(
        Stream input,
        Stream output,
        string? outputPath,
        string? externalEKey,
        IProgress<QmcDecryptProgress>? progress,
        CancellationToken cancellationToken,
        int bufferSize)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ParsedFooter footer = await ParseFooterAsync(input, cancellationToken).ConfigureAwait(false);
        KeySelection selection = SelectCipher(footer, externalEKey);
        QmcProbeResult initialProbe = ToProbeResult(
            footer,
            selection,
            null,
            selection.Error,
            canDecrypt: false);
        if (selection.Cipher is null)
            throw new QmcDecryptException(selection.Error ?? "No usable QMC key is available.", initialProbe);
        if (footer.AudioLength < MinimumProbeBytes)
            throw new QmcDecryptException($"Encrypted audio is too short ({footer.AudioLength} bytes).", initialProbe);

        int headLength = (int)Math.Min(ProbeBytes, footer.AudioLength);
        byte[] head = new byte[headLength];
        input.Position = 0;
        await ReadExactlyAsync(input, head, cancellationToken).ConfigureAwait(false);
        selection.Cipher.Transform(head, 0);
        string? extension = DetectAudioExtension(head);
        if (extension is null)
            throw new QmcDecryptException("The selected key did not decrypt to a recognized audio header.", initialProbe);

        QmcProbeResult probe = ToProbeResult(
            footer,
            selection,
            extension,
            null,
            canDecrypt: true);

        byte[] rented = ArrayPool<byte>.Shared.Rent(bufferSize);
        long offset = 0;
        long remaining = footer.AudioLength;
        try
        {
            input.Position = 0;
            progress?.Report(new QmcDecryptProgress(0, footer.AudioLength));
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int wanted = (int)Math.Min(rented.Length, remaining);
                int read = await input.ReadAsync(rented.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException($"Unexpected end of input at {offset} of {footer.AudioLength} audio bytes.");

                selection.Cipher.Transform(rented.AsSpan(0, read), offset);
                await output.WriteAsync(rented.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                offset += read;
                remaining -= read;
                progress?.Report(new QmcDecryptProgress(offset, footer.AudioLength));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }

        return new QmcDecryptResult(outputPath, offset, extension, probe);
    }

    private static KeySelection SelectCipher(ParsedFooter footer, string? externalEKey)
    {
        string? embedded = footer.EmbeddedEKey;
        string? resolved = !string.IsNullOrWhiteSpace(embedded)
            ? embedded
            : !string.IsNullOrWhiteSpace(externalEKey) ? externalEKey.Trim() : null;
        QmcKeySource source = resolved is null
            ? footer.Kind == QmcFooterKind.None ? QmcKeySource.StaticV1 : QmcKeySource.None
            : embedded is not null ? QmcKeySource.EmbeddedEKey : QmcKeySource.ExternalEKey;

        try
        {
            IQmcCipher? cipher = resolved is not null
                ? QmcCipherFactory.FromMasterKey(QmcEKey.DeriveMasterKey(resolved))
                : footer.Kind == QmcFooterKind.None ? QmcStaticCipher.Instance : null;
            string? error = cipher is null
                ? footer.Kind == QmcFooterKind.AndroidSTag
                    ? "Android STag contains no EKey; supply a matching external EKey."
                    : "MusicEx contains no EKey; supply a matching external EKey."
                : null;
            return new KeySelection(cipher, source, error);
        }
        catch (Exception ex) when (ex is IOException or FormatException or OverflowException)
        {
            return new KeySelection(null, source, $"EKey decode failed: {ex.Message}");
        }
    }

    private static async Task<ParsedFooter> ParseFooterAsync(Stream input, CancellationToken cancellationToken)
    {
        long fileLength = input.Length;
        if (fileLength < 4)
            throw new QmcDecryptException("File is too short to contain QMC audio.");

        byte[] last16 = new byte[(int)Math.Min(16, fileLength)];
        input.Position = fileLength - last16.Length;
        await ReadExactlyAsync(input, last16, cancellationToken).ConfigureAwait(false);

        if (last16.Length >= 8 && last16.AsSpan(last16.Length - 8).SequenceEqual(MusicExMagic))
            return await ParseMusicExAsync(input, fileLength, last16, cancellationToken).ConfigureAwait(false);

        ReadOnlySpan<byte> suffix = last16.AsSpan(last16.Length - 4);
        if (suffix.SequenceEqual(QTagMagic))
            return await ParseAndroidTagAsync(input, fileLength, QmcFooterKind.AndroidQTag, cancellationToken).ConfigureAwait(false);
        if (suffix.SequenceEqual(STagMagic))
            return await ParseAndroidTagAsync(input, fileLength, QmcFooterKind.AndroidSTag, cancellationToken).ConfigureAwait(false);

        uint legacyKeyLength = BinaryPrimitives.ReadUInt32LittleEndian(suffix);
        if (legacyKeyLength is > 0 and <= MaximumLegacyEKeyLength)
            return await ParseLegacyAsync(input, fileLength, legacyKeyLength, cancellationToken).ConfigureAwait(false);

        return new ParsedFooter(
            QmcFooterKind.None,
            fileLength,
            fileLength,
            0,
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private static async Task<ParsedFooter> ParseLegacyAsync(
        Stream input,
        long fileLength,
        uint keyLength,
        CancellationToken cancellationToken)
    {
        long footerLength = checked((long)keyLength + 4);
        if (footerLength > fileLength)
            throw new QmcDecryptException($"PC legacy footer length {footerLength} exceeds file length {fileLength}.");

        byte[] rawKey = new byte[keyLength];
        input.Position = fileLength - footerLength;
        await ReadExactlyAsync(input, rawKey, cancellationToken).ConfigureAwait(false);
        ReadOnlySpan<byte> trimmed = TrimAtFirstNul(rawKey);
        if (!QmcEKey.LooksLikeBase64(trimmed))
            throw new QmcDecryptException("PC legacy footer does not contain a valid base64 EKey.");

        return new ParsedFooter(
            QmcFooterKind.PcV1Legacy,
            fileLength,
            fileLength - footerLength,
            footerLength,
            Encoding.ASCII.GetString(trimmed),
            null,
            null,
            null,
            null,
            null);
    }

    private static async Task<ParsedFooter> ParseAndroidTagAsync(
        Stream input,
        long fileLength,
        QmcFooterKind kind,
        CancellationToken cancellationToken)
    {
        if (fileLength < 8)
            throw new QmcDecryptException("Android QMC footer is truncated.");

        byte[] lengthBytes = new byte[4];
        input.Position = fileLength - 8;
        await ReadExactlyAsync(input, lengthBytes, cancellationToken).ConfigureAwait(false);
        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        if (payloadLength == 0 || payloadLength > MaximumQTagLength)
            throw new QmcDecryptException($"Android QMC footer length {payloadLength} is outside the supported range.");

        long footerLength = checked((long)payloadLength + 8);
        if (footerLength > fileLength)
            throw new QmcDecryptException($"Android QMC footer length {footerLength} exceeds file length {fileLength}.");

        byte[] payload = new byte[payloadLength];
        input.Position = fileLength - footerLength;
        await ReadExactlyAsync(input, payload, cancellationToken).ConfigureAwait(false);
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(payload);
        }
        catch (DecoderFallbackException ex)
        {
            throw new QmcDecryptException("Android QMC footer is not valid UTF-8.", null, ex);
        }

        string[] items = text.Split(',');
        if (items.Length != 3)
            throw new QmcDecryptException("Android QMC footer must contain exactly three CSV fields.");

        if (kind == QmcFooterKind.AndroidQTag)
        {
            if (!long.TryParse(items[1], NumberStyles.None, CultureInfo.InvariantCulture, out long resourceId) || resourceId < 0)
                throw new QmcDecryptException("Android QTag resource ID is invalid.");
            if (!int.TryParse(items[2], NumberStyles.None, CultureInfo.InvariantCulture, out int version) || version != 2)
                throw new QmcDecryptException($"Unsupported Android QTag version '{items[2]}'.");
            byte[] rawEKey = Encoding.ASCII.GetBytes(items[0]);
            if (!QmcEKey.LooksLikeBase64(rawEKey))
                throw new QmcDecryptException("Android QTag does not contain a valid base64 EKey.");
            return new ParsedFooter(
                kind,
                fileLength,
                fileLength - footerLength,
                footerLength,
                items[0],
                null,
                null,
                null,
                resourceId,
                version);
        }

        if (!long.TryParse(items[0], NumberStyles.None, CultureInfo.InvariantCulture, out long sTagResourceId) || sTagResourceId < 0)
            throw new QmcDecryptException("Android STag resource ID is invalid.");
        if (!int.TryParse(items[1], NumberStyles.None, CultureInfo.InvariantCulture, out int sTagVersion) || sTagVersion != 2)
            throw new QmcDecryptException($"Unsupported Android STag version '{items[1]}'.");
        if (string.IsNullOrWhiteSpace(items[2]))
            throw new QmcDecryptException("Android STag media MID is empty.");
        return new ParsedFooter(
            kind,
            fileLength,
            fileLength - footerLength,
            footerLength,
            null,
            null,
            items[2],
            null,
            sTagResourceId,
            sTagVersion);
    }

    private static async Task<ParsedFooter> ParseMusicExAsync(
        Stream input,
        long fileLength,
        byte[] last16,
        CancellationToken cancellationToken)
    {
        uint tagSize = BinaryPrimitives.ReadUInt32LittleEndian(last16.AsSpan(last16.Length - 16, 4));
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(last16.AsSpan(last16.Length - 12, 4));
        if (version != 1)
            throw new QmcDecryptException($"Unsupported MusicEx version {version}; expected 1.");
        if (tagSize < MusicExMinimumSize)
            throw new QmcDecryptException($"MusicEx tag size 0x{tagSize:X} is smaller than 0x{MusicExMinimumSize:X}.");
        if (tagSize > fileLength)
            throw new QmcDecryptException($"MusicEx tag size {tagSize} exceeds file length {fileLength}.");
        // Fields used below reside in the first 0xB0 bytes. Do not allocate an
        // attacker-controlled TagSize buffer; read only the fixed prefix.
        byte[] fields = new byte[0xB0];
        input.Position = fileLength - tagSize;
        await ReadExactlyAsync(input, fields, cancellationToken).ConfigureAwait(false);

        uint songId = BinaryPrimitives.ReadUInt32LittleEndian(fields.AsSpan(0, 4));
        string mediaId = ReadUtf16LeAscii(fields.AsSpan(0x0C, 60), "MusicEx media ID");
        string mediaFileName = ReadUtf16LeAscii(fields.AsSpan(0x48, 100), "MusicEx media filename");
        if (string.IsNullOrWhiteSpace(mediaFileName))
            throw new QmcDecryptException("MusicEx media filename is empty.");

        return new ParsedFooter(
            QmcFooterKind.PcV2MusicEx,
            fileLength,
            fileLength - tagSize,
            tagSize,
            null,
            songId,
            mediaId,
            mediaFileName,
            null,
            checked((int)version));
    }

    private static string ReadUtf16LeAscii(ReadOnlySpan<byte> data, string fieldName)
    {
        var value = new StringBuilder(data.Length / 2);
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            byte low = data[i];
            byte high = data[i + 1];
            if (low == 0 && high == 0)
                break;
            if (high != 0 || low is < 0x20 or > 0x7E)
                throw new QmcDecryptException($"{fieldName} is not the expected UTF-16LE ASCII text.");
            value.Append((char)low);
        }
        return value.ToString();
    }

    private static ReadOnlySpan<byte> TrimAtFirstNul(ReadOnlySpan<byte> value)
    {
        int nul = value.IndexOf((byte)0);
        return nul < 0 ? value : value[..nul];
    }

    private static string? DetectAudioExtension(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith("fLaC"u8)) return ".flac";
        if (header.StartsWith("OggS"u8)) return header.IndexOf("OpusHead"u8) >= 0 ? ".opus" : ".ogg";
        if (header.StartsWith("ID3"u8)) return ".mp3";
        if (header.StartsWith("RIFF"u8) && header.Length >= 12 && header.Slice(8, 4).SequenceEqual("WAVE"u8)) return ".wav";
        if (header.StartsWith("FORM"u8) && header.Length >= 12 &&
            (header.Slice(8, 4).SequenceEqual("AIFF"u8) || header.Slice(8, 4).SequenceEqual("AIFC"u8))) return ".aiff";
        if (header.StartsWith("MAC "u8)) return ".ape";
        if (header.StartsWith("FRM8"u8)) return ".dff";
        if (header.StartsWith("DSD "u8)) return ".dsf";
        ReadOnlySpan<byte> asf = new byte[] { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11, 0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C };
        if (header.StartsWith(asf)) return ".wma";
        if (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xF6) == 0xF0) return ".aac";
        if (header.Length >= 16 && header.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (boxSize >= 16 && boxSize % 4 == 0)
            {
                bool m4a = header.Slice(8, 4).SequenceEqual("M4A "u8);
                int end = (int)Math.Min(boxSize, (uint)header.Length);
                for (int i = 16; !m4a && i + 4 <= end; i += 4)
                    m4a = header.Slice(i, 4).SequenceEqual("M4A "u8);
                return m4a ? ".m4a" : ".mp4";
            }
        }
        // Raw MPEG audio does not require an ID3 tag. Validate sync/version/
        // layer/bitrate/sample-rate fields to avoid accepting random data.
        if (header.Length >= 4 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0)
        {
            int version = (header[1] >> 3) & 3;
            int layer = (header[1] >> 1) & 3;
            int bitrate = (header[2] >> 4) & 0xF;
            int sampleRate = (header[2] >> 2) & 3;
            if (version != 1 && layer != 0 && bitrate is not 0 and not 15 && sampleRate != 3)
                return ".mp3";
        }
        return null;
    }

    private static QmcProbeResult ToProbeResult(
        ParsedFooter footer,
        KeySelection selection,
        string? extension,
        string? error,
        bool canDecrypt) => new(
            footer.Kind,
            footer.FileLength,
            footer.AudioLength,
            footer.FooterLength,
            footer.SongId,
            footer.MediaId,
            footer.MediaFileName,
            footer.ResourceId,
            footer.Version,
            footer.EmbeddedEKey is not null,
            footer.Kind is QmcFooterKind.PcV2MusicEx or QmcFooterKind.AndroidSTag && footer.EmbeddedEKey is null,
            canDecrypt,
            selection.Source,
            selection.Cipher?.Kind ?? QmcCipherKind.Unknown,
            extension,
            error);

    private static void ValidateInputStream(Stream input)
    {
        if (!input.CanRead || !input.CanSeek)
            throw new ArgumentException("Input stream must be readable and seekable.", nameof(input));
    }

    private static void ValidateBufferSize(int bufferSize)
    {
        if (bufferSize is < ProbeBytes or > MaximumBufferSize)
            throw new ArgumentOutOfRangeException(nameof(bufferSize), $"Buffer size must be between {ProbeBytes} and {MaximumBufferSize} bytes.");
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException($"Unexpected end of stream; needed {buffer.Length - offset} more bytes.");
            offset += read;
        }
    }

    private static long TryGetLength(Stream input)
    {
        try { return input.Length; }
        catch { return 0; }
    }

    private sealed record ParsedFooter(
        QmcFooterKind Kind,
        long FileLength,
        long AudioLength,
        long FooterLength,
        string? EmbeddedEKey,
        uint? SongId,
        string? MediaId,
        string? MediaFileName,
        long? ResourceId,
        int? Version);

    private sealed record KeySelection(IQmcCipher? Cipher, QmcKeySource Source, string? Error);
}
