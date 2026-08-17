// SPDX-License-Identifier: MIT

using MFlacDrop.OfflineQmc;
using MusicDrop3.MultiPlatform;

namespace MusicDrop.Core;

public sealed record PortableDecryptOptions(
    string QmcEKey = "",
    string KugouDatabasePath = "",
    bool Overwrite = false,
    QmcKeyring? QmcKeyring = null);

public sealed record PortableProbeResult(
    string InputPath,
    string Family,
    string? AudioExtension,
    bool CanDecrypt,
    bool RequiresExternalKey,
    string? Error = null,
    string? KeyIdentifier = null,
    bool IsStandardAudio = false);

public sealed record PortableDecryptResult(
    string InputPath,
    string OutputPath,
    string Family,
    string AudioExtension,
    long BytesWritten,
    string Route);

/// <summary>
/// Platform-neutral, offline-only format probing and decryption facade.
/// It never starts a music client, contacts a service, or modifies the source.
/// </summary>
public sealed class PortableAudioService
{
    private static readonly string[] QmcExtensions =
    {
        ".mflac0", ".mflac1", ".mflaca", ".mflach", ".mflacl", ".mflacm", ".mflac",
        ".mgg0", ".mgg1", ".mgga", ".mggh", ".mggl", ".mggm", ".mgg",
        ".qmcflac", ".qmcogg", ".qmc0", ".qmc2", ".qmc3", ".qmc4", ".qmc6", ".qmc8",
        ".tkm", ".bkcmp3", ".bkcm4a", ".bkcflac", ".bkcwav", ".bkcape", ".bkcogg", ".bkcwma",
        ".666c6163", ".6d7033", ".6f6767", ".6d3461", ".776176", ".mmp4",
    };

    private static readonly string[] StandardExtensions =
    {
        ".flac", ".wav", ".mp3", ".ogg", ".m4a", ".mp4", ".m4b", ".aac",
        ".opus", ".ape", ".wma", ".aiff", ".aif", ".dsf", ".dff",
    };

    private readonly MultiPlatformDispatcher dispatcher = MultiPlatformDispatcher.CreateDefault();

    public IReadOnlyCollection<string> SupportedExtensions { get; } = QmcExtensions
        .Concat(StandardExtensions)
        .Concat(MultiPlatformDispatcher.CreateDefault().Extensions)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(value => value.Length)
        .ToArray();

    public bool IsSupportedPath(string path) => MatchSuffix(path) is not null;

    public async Task<PortableProbeResult> ProbeAsync(
        string inputPath,
        PortableDecryptOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PortableDecryptOptions();
        string input = Path.GetFullPath(inputPath);
        try
        {
            if (!File.Exists(input))
                return new(input, "Unknown", null, false, false, "Input file does not exist.");
            string? suffix = MatchSuffix(input);
            if (suffix is null)
                return new(input, "Unknown", null, false, false, "Unsupported filename extension.");

            if (StandardExtensions.Contains(suffix, StringComparer.OrdinalIgnoreCase))
            {
                string? extension = await AudioSignatures.DetectFileAsync(
                    input, cancellationToken).ConfigureAwait(false);
                return extension is null
                    ? new(input, "Standard audio", null, false, false,
                        "The filename is supported, but the audio signature is not recognized.", IsStandardAudio: true)
                    : new(input, "Standard audio", extension, true, false, IsStandardAudio: true);
            }

            if (QmcExtensions.Contains(suffix, StringComparer.OrdinalIgnoreCase))
            {
                (QmcProbeResult probe, _) = await ResolveQmcAsync(
                    input, options, cancellationToken).ConfigureAwait(false);
                return new(input, DescribeQmc(probe), probe.DetectedAudioExtension,
                    probe.CanDecrypt, probe.RequiresExternalEKey, probe.Error,
                    probe.MediaId ?? probe.MediaFileName);
            }

            (_, PlatformProbeResult platform) = await dispatcher.ProbeAsync(
                input, new MultiPlatformOptions(options.KugouDatabasePath), cancellationToken)
                .ConfigureAwait(false);
            return new(input, platform.FormatName, platform.AudioExtension,
                platform.CanDecrypt, platform.RequiresExternalKey, platform.Error, platform.KeyIdentifier);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Security.Cryptography.CryptographicException or ArgumentException or NotSupportedException)
        {
            return new(input, "Unknown", null, false, false, ex.Message);
        }
    }

    public async Task<PortableDecryptResult> DecryptToFileAsync(
        string inputPath,
        string outputPath,
        PortableDecryptOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PortableDecryptOptions();
        string input = Path.GetFullPath(inputPath);
        string output = Path.GetFullPath(outputPath);
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(input, output, pathComparison))
            throw new IOException("Source and output paths must be different.");
        if (!File.Exists(input)) throw new FileNotFoundException("Input file does not exist.", input);
        Directory.CreateDirectory(Path.GetDirectoryName(output)
            ?? throw new IOException("Output directory is invalid."));
        if (File.Exists(output) && !options.Overwrite)
            throw new IOException("Output file already exists: " + output);

        PortableProbeResult probe = await ProbeAsync(input, options, cancellationToken).ConfigureAwait(false);
        if (!probe.CanDecrypt || string.IsNullOrWhiteSpace(probe.AudioExtension))
            throw new InvalidDataException(probe.Error ?? $"{probe.Family} is not ready for offline conversion.");

        string partial = output + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            PortableDecryptResult result;
            string? suffix = MatchSuffix(input);
            if (probe.IsStandardAudio)
            {
                await using FileStream source = StreamingAudio.OpenRead(input);
                await using FileStream destination = StreamingAudio.OpenWriteNew(partial);
                long copied = await StreamingAudio.CopyAsync(
                    source, destination, null, cancellationToken).ConfigureAwait(false);
                result = new(input, output, "Standard audio", probe.AudioExtension,
                    copied, "Byte-exact copy");
            }
            else if (suffix is not null && QmcExtensions.Contains(suffix, StringComparer.OrdinalIgnoreCase))
            {
                (QmcProbeResult resolvedProbe, string? resolvedKey) = await ResolveQmcAsync(
                    input, options, cancellationToken).ConfigureAwait(false);
                if (!resolvedProbe.CanDecrypt)
                    throw new InvalidDataException(resolvedProbe.Error ?? "No matching QMC EKey was found.");
                QmcDecryptResult decrypted = await OfflineQmcDecryptor.DecryptAsync(
                    input, partial, resolvedKey, null, cancellationToken)
                    .ConfigureAwait(false);
                result = new(input, output, DescribeQmc(decrypted.Probe),
                    decrypted.DetectedAudioExtension, decrypted.BytesWritten, "Offline QQ/QMC");
            }
            else
            {
                (IPlatformDecryptor decryptor, PlatformProbeResult platformProbe) =
                    await dispatcher.ProbeAsync(input,
                        new MultiPlatformOptions(options.KugouDatabasePath), cancellationToken)
                    .ConfigureAwait(false);
                if (!platformProbe.CanDecrypt)
                    throw new InvalidDataException(platformProbe.Error ?? "Encrypted input is not ready.");
                PlatformDecryptResult decrypted = await decryptor.DecryptAsync(
                    input, partial, new MultiPlatformOptions(options.KugouDatabasePath), cancellationToken)
                    .ConfigureAwait(false);
                result = new(input, output, decrypted.FormatName, decrypted.AudioExtension,
                    decrypted.BytesWritten, decrypted.Route);
            }

            string? verified = await AudioSignatures.DetectFileAsync(partial, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(verified, result.AudioExtension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Output signature mismatch: expected {result.AudioExtension}, detected {verified ?? "unknown"}.");
            File.Move(partial, output, options.Overwrite);
            return result;
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
        }
    }

    public string GetInputStem(string path)
    {
        string name = Path.GetFileName(path);
        string? suffix = MatchSuffix(name);
        return suffix is null ? Path.GetFileNameWithoutExtension(name) : name[..^suffix.Length];
    }

    private string? MatchSuffix(string path) => SupportedExtensions.FirstOrDefault(extension =>
        path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<(QmcProbeResult Probe, string? EKey)> ResolveQmcAsync(
        string input,
        PortableDecryptOptions options,
        CancellationToken cancellationToken)
    {
        string? explicitKey = NullIfWhiteSpace(options.QmcEKey);
        QmcProbeResult initial = await OfflineQmcDecryptor.ProbeAsync(
            input, explicitKey, cancellationToken).ConfigureAwait(false);
        if (initial.CanDecrypt || !initial.RequiresExternalEKey)
            return (initial, explicitKey);
        if (options.QmcKeyring is null) return (initial, null);

        foreach (string candidate in options.QmcKeyring.GetCandidates(
            input, initial.MediaFileName, initial.MediaId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            QmcProbeResult tested = await OfflineQmcDecryptor.ProbeAsync(
                input, candidate, cancellationToken).ConfigureAwait(false);
            if (tested.CanDecrypt) return (tested, candidate);
        }
        return (initial with { Error = "No matching entry in the local QMC EKey keyring passed audio-header verification." }, null);
    }

    private static string DescribeQmc(QmcProbeResult probe) => probe.FooterKind switch
    {
        QmcFooterKind.None => "QQ/QMC static",
        QmcFooterKind.PcV1Legacy => "QQ/QMC PC legacy",
        QmcFooterKind.PcV2MusicEx => "QQ/QMC MusicEx",
        QmcFooterKind.AndroidQTag => "QQ/QMC Android QTag",
        QmcFooterKind.AndroidSTag => "QQ/QMC Android STag",
        _ => "QQ/QMC",
    };
}
