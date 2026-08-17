using System.Collections.ObjectModel;
using System.Text;

namespace MFlacDrop;

/// <summary>
/// Describes the identifiers which may locate an EKey.  Providers must treat all
/// fields as untrusted input; a request never contains an account token.
/// </summary>
internal sealed record KeyLookupRequest(
    string InputPath,
    string? MediaFileName = null,
    string? MediaMid = null)
{
    /// <summary>
    /// Exact identifiers, ordered from the strongest MusicEx metadata to the
    /// user-facing input basename.  Normalized equality is always allowed.
    /// </summary>
    public IReadOnlyList<string> Identifiers => KeyIdentifier.Create(this);

    /// <summary>
    /// Opaque MusicEx IDs that may safely occur inside an AIM/p2p wrapper.
    /// These are the only values for which substring lookup is permitted.
    /// </summary>
    public IReadOnlyList<string> OpaqueMediaIds => KeyIdentifier.CreateOpaqueMediaIds(this);

    public static KeyLookupRequest FromMusicFile(string inputPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        MusicExFooterInfo? footer = MusicExFooterReader.TryRead(inputPath);
        return new(inputPath, footer?.MediaFileName, footer?.MediaMid);
    }
}

internal sealed record KeyLookupResult(string EKey, string Provider, string MatchedIdentifier);

internal interface IKeyProvider
{
    string Name { get; }

    /// <summary>
    /// Returns every distinct, syntactically valid candidate known to this
    /// provider.  Callers must still verify each candidate against the audio
    /// header; a lookup match alone never proves that an EKey is correct.
    /// </summary>
    ValueTask<IReadOnlyList<KeyLookupResult>> GetKeysAsync(
        KeyLookupRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compatibility helper for callers that can only consume one candidate.
    /// New conversion code should use <see cref="GetKeysAsync"/> so that a
    /// stale first match cannot hide a later valid EKey.
    /// </summary>
    async ValueTask<KeyLookupResult?> TryGetKeyAsync(
        KeyLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<KeyLookupResult> results = await GetKeysAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return results.Count == 0 ? null : results[0];
    }
}

/// <summary>Combines providers in order and removes duplicate EKeys.</summary>
internal sealed class CompositeKeyProvider : IKeyProvider, IDisposable
{
    private readonly IReadOnlyList<IKeyProvider> _providers;

    public CompositeKeyProvider(params IKeyProvider[] providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = Array.AsReadOnly(providers.ToArray());
    }

    public string Name => "combined";

    public async ValueTask<IReadOnlyList<KeyLookupResult>> GetKeysAsync(
        KeyLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var results = new List<KeyLookupResult>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (IKeyProvider provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<KeyLookupResult> candidates = await provider.GetKeysAsync(request, cancellationToken)
                .ConfigureAwait(false);
            foreach (KeyLookupResult candidate in candidates)
            {
                if (!EKeyText.IsValid(candidate.EKey))
                    continue;
                string ekey = EKeyText.Normalize(candidate.EKey);
                if (seenKeys.Add(ekey))
                    results.Add(candidate with { EKey = ekey });
            }
        }
        return results.AsReadOnly();
    }

    public void Dispose()
    {
        foreach (IDisposable provider in _providers.OfType<IDisposable>())
            provider.Dispose();
    }
}

/// <summary>
/// Checks the DPAPI cache first.  A key found by another provider is saved for
/// future offline use. Cache write failures do not turn a successful lookup
/// into a conversion failure.
/// </summary>
internal sealed class CachingKeyProvider : IKeyProvider
{
    private readonly DpapiEKeyCacheProvider _cache;
    private readonly IKeyProvider _fallback;

    public CachingKeyProvider(DpapiEKeyCacheProvider cache, IKeyProvider fallback)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
    }

    public string Name => "cache + " + _fallback.Name;

    public async ValueTask<IReadOnlyList<KeyLookupResult>> GetKeysAsync(
        KeyLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<KeyLookupResult> cached = await _cache.GetKeysAsync(request, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<KeyLookupResult> discovered = await _fallback.GetKeysAsync(request, cancellationToken)
            .ConfigureAwait(false);
        foreach (KeyLookupResult candidate in discovered)
        {
            try
            {
                await _cache.StoreAsync(request, candidate.EKey, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Security.Cryptography.CryptographicException) { }
        }

        var results = new List<KeyLookupResult>(cached.Count + discovered.Count);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyLookupResult candidate in cached.Concat(discovered))
        {
            if (EKeyText.IsValid(candidate.EKey) && seenKeys.Add(candidate.EKey))
                results.Add(candidate);
        }
        return results.AsReadOnly();
    }
}

internal sealed record MusicExFooterInfo(string? MediaMid, string? MediaFileName);

internal static class MusicExFooterReader
{
    private static readonly byte[] Magic = "musicex\0"u8.ToArray();
    private const int MusicExSize = 0xC0;

    /// <summary>Reads only the 192-byte MusicEx v1 footer; audio is never loaded.</summary>
    public static MusicExFooterInfo? TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < MusicExSize)
                return null;

            var footer = new byte[MusicExSize];
            stream.Position = stream.Length - footer.Length;
            stream.ReadExactly(footer);

            if (!footer.AsSpan(MusicExSize - 8).SequenceEqual(Magic))
                return null;
            if (BitConverter.ToUInt32(footer, MusicExSize - 12) != 1)
                return null;
            if (BitConverter.ToUInt32(footer, MusicExSize - 16) != MusicExSize)
                return null;

            // Layout used by QQ Music PC: 3*u32, mid[60 UTF-16LE],
            // media_filename[100 UTF-16LE], u32, length, version, magic.
            string? mid = ReadAsciiUtf16Le(footer.AsSpan(12, 60));
            string? mediaFileName = ReadAsciiUtf16Le(footer.AsSpan(72, 100));
            return new(mid, mediaFileName);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private static string? ReadAsciiUtf16Le(ReadOnlySpan<byte> bytes)
    {
        var value = new StringBuilder(bytes.Length / 2);
        for (int index = 0; index + 1 < bytes.Length; index += 2)
        {
            byte character = bytes[index];
            byte high = bytes[index + 1];
            if (character == 0 && high == 0)
                break;
            if (high != 0 || character is < 0x20 or > 0x7E)
                return null;
            value.Append((char)character);
        }
        return value.Length == 0 ? null : value.ToString();
    }
}

internal static class KeyIdentifier
{
    private static readonly string[] QmcExtensions =
        [".mflac0", ".mflac", ".qmcflac", ".mgg0", ".mgg", ".qmcogg", ".qmc0", ".qmc3"];

    public static IReadOnlyList<string> Create(KeyLookupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var values = new List<string>();
        // Footer metadata is authoritative.  Keep the display/input basename
        // last so a same-title collision cannot outrank a strong identifier.
        Add(values, request.MediaFileName);
        Add(values, request.MediaMid);
        Add(values, request.InputPath);

        return new ReadOnlyCollection<string>(values);
    }

    public static IReadOnlyList<string> CreateOpaqueMediaIds(KeyLookupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var values = new List<string>();
        string? mediaMid = request.MediaMid?.Trim();
        if (IsOpaqueMediaId(mediaMid))
            values.Add(mediaMid!);
        return new ReadOnlyCollection<string>(values);
    }

    public static string Normalize(string identifier)
    {
        string value = identifier.Trim().Replace('\\', '/');
        int slash = value.LastIndexOf('/');
        if (slash >= 0)
            value = value[(slash + 1)..];

        bool removed;
        do
        {
            removed = false;
            foreach (string extension in QmcExtensions)
            {
                if (!value.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                    continue;
                value = value[..^extension.Length];
                removed = true;
                break;
            }
        }
        while (removed);

        return value.Trim().ToUpperInvariant();
    }

    public static bool IsMatch(string storedIdentifier, KeyLookupRequest request)
    {
        string stored = Normalize(storedIdentifier);
        if (stored.Length == 0)
            return false;

        foreach (string candidate in request.Identifiers)
        {
            string expected = Normalize(candidate);
            if (expected.Length > 0 && string.Equals(stored, expected, StringComparison.Ordinal))
                return true;
        }

        // p2p file_id may wrap a MID, e.g. AIM0000<mid>.mflac.  This is
        // deliberately one-way: a trusted MID may occur inside the stored
        // wrapper, but a short/stored fragment may never match the request.
        foreach (string mediaId in request.OpaqueMediaIds)
        {
            string expected = Normalize(mediaId);
            if (expected.Length > 0 && stored.Contains(expected, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static bool IsOpaqueMediaId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        ReadOnlySpan<char> text = value.Trim();
        // Current QQ Music MediaMid values are 14 ASCII alphanumerics.  Keep a
        // narrow upper bound for compatible opaque IDs without admitting song
        // titles, paths, punctuation, or tiny fragments.
        if (text.Length is < 14 or > 32)
            return false;
        foreach (char character in text)
        {
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'))
                return false;
        }
        return true;
    }

    private static void Add(List<string> values, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        string full = value.Trim();
        if (!values.Contains(full, StringComparer.OrdinalIgnoreCase))
            values.Add(full);
        string fileName = Path.GetFileName(full.Replace('/', Path.DirectorySeparatorChar));
        if (fileName.Length > 0 && !values.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            values.Add(fileName);
        string normalized = Normalize(full);
        if (normalized.Length > 0 && !values.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            values.Add(normalized);
    }
}

internal static class EKeyText
{
    // EKeys observed in QQ Music are ASCII base64, with an optional decoded
    // "QQMusic EncV2,Key:" envelope represented as another base64 string.
    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        string text = value.Trim();
        if (text.Length is < 16 or > 8192 || (text.Length & 3) != 0)
            return false;
        foreach (char character in text)
        {
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
                  or '+' or '/' or '='))
                return false;
        }
        try
        {
            return Convert.FromBase64String(text).Length >= 8;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string normalized = value.Trim();
        if (!IsValid(normalized))
            throw new FormatException("EKey is not valid base64 text.");
        return normalized;
    }
}
