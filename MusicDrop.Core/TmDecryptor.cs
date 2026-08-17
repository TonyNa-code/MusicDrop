// SPDX-License-Identifier: MIT
// Format behavior adapted from leafxdd/unlock-music algo/tm (MIT).

namespace MusicDrop3.MultiPlatform;

internal sealed class TmDecryptor : IPlatformDecryptor
{
    private static ReadOnlySpan<byte> Magic => "QQMU"u8;
    private static ReadOnlySpan<byte> Replacement =>
        [0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70];

    public IReadOnlyCollection<string> Extensions { get; } =
        new[] { ".tm0", ".tm2", ".tm3", ".tm6" };

    public async Task<PlatformProbeResult> ProbeAsync(
        string inputPath, MultiPlatformOptions options, CancellationToken cancellationToken)
    {
        try
        {
            byte[] header = await ReadHeaderAsync(inputPath, cancellationToken).ConfigureAwait(false);
            bool restored = header.AsSpan().StartsWith(Magic);
            if (restored) Replacement.CopyTo(header);
            string? extension = AudioSignatures.Detect(header);
            return extension is null
                ? new(SourcePlatform.QqMusic, "QQ iOS TM", null, false, false,
                    "TM 文件头无法恢复为受支持的音频格式。")
                : new(SourcePlatform.QqMusic, restored ? "QQ iOS TM (M4A)" : "QQ iOS TM",
                    extension, true, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return new(SourcePlatform.QqMusic, "QQ iOS TM", null, false, false, ex.Message);
        }
    }

    public async Task<PlatformDecryptResult> DecryptAsync(
        string inputPath, string outputPath, MultiPlatformOptions options, CancellationToken cancellationToken)
    {
        byte[] header = await ReadHeaderAsync(inputPath, cancellationToken).ConfigureAwait(false);
        bool restored = header.AsSpan().StartsWith(Magic);
        if (restored) Replacement.CopyTo(header);
        string extension = AudioSignatures.Detect(header)
            ?? throw new InvalidDataException("TM 文件头无法恢复为受支持的音频格式。");

        await using FileStream input = StreamingAudio.OpenRead(inputPath);
        await using FileStream output = StreamingAudio.OpenWriteNew(outputPath);
        long written;
        if (restored)
        {
            await output.WriteAsync(Replacement.ToArray(), cancellationToken).ConfigureAwait(false);
            input.Position = Replacement.Length;
            written = Replacement.Length + await StreamingAudio.CopyAsync(
                input, output, null, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            written = await StreamingAudio.CopyAsync(input, output, null, cancellationToken).ConfigureAwait(false);
        }
        return new(SourcePlatform.QqMusic, "QQ iOS TM", extension, written,
            "离线（QQ 音乐 iOS TM）");
    }

    private static async Task<byte[]> ReadHeaderAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream input = StreamingAudio.OpenRead(path);
        if (input.Length < 16) throw new InvalidDataException("TM 文件头被截断。");
        byte[] header = new byte[(int)Math.Min(64, input.Length)];
        await input.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        return header;
    }
}
