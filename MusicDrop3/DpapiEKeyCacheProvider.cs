using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MFlacDrop;

/// <summary>
/// Per-Windows-user encrypted EKey cache.  The JSON index contains only SHA-256
/// identifier hashes and DPAPI ciphertext; it never stores paths, titles, MIDs,
/// account IDs or tokens in plaintext.
/// </summary>
internal sealed class DpapiEKeyCacheProvider : IKeyProvider
{
    private const int FormatVersion = 1;
    private static readonly byte[] Entropy = SHA256.HashData(
        "Music Drop 3/QQ EKey cache/v1"u8.ToArray());
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DpapiEKeyCacheProvider(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public static DpapiEKeyCacheProvider CreateDefault() =>
        new(Path.Combine(AppInfo.DataDir, "ekey-cache.dpapi.json"));

    public string Name => "Windows user EKey cache";

    public async ValueTask<IReadOnlyList<KeyLookupResult>> GetKeysAsync(
        KeyLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CacheDocument document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            var results = new List<KeyLookupResult>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (string identifier in request.Identifiers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string hash = HashIdentifier(identifier);
                // Do not use FirstOrDefault: older cache documents, basename
                // collisions, or interrupted migrations can legitimately leave
                // more than one protected candidate for the same identifier.
                foreach (CacheEntry entry in document.Entries.Where(item =>
                    string.Equals(item.IdentifierHash, hash, StringComparison.Ordinal)))
                {
                    try
                    {
                        byte[] plaintext = Dpapi.Unprotect(Convert.FromBase64String(entry.ProtectedEKey), Entropy);
                        string ekey;
                        try
                        {
                            ekey = Encoding.UTF8.GetString(plaintext);
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(plaintext);
                        }
                        if (EKeyText.IsValid(ekey))
                        {
                            ekey = EKeyText.Normalize(ekey);
                            if (seenKeys.Add(ekey))
                                results.Add(new(ekey, Name, identifier));
                        }
                    }
                    catch (FormatException) { }
                    catch (CryptographicException) { }
                }
            }
            return results.AsReadOnly();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask StoreAsync(
        KeyLookupRequest request,
        string ekey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        string normalizedKey = EKeyText.Normalize(ekey);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CacheDocument document = await LoadAsync(cancellationToken).ConfigureAwait(false);
            byte[] plaintext = Encoding.UTF8.GetBytes(normalizedKey);
            byte[] ciphertext;
            try
            {
                ciphertext = Dpapi.Protect(plaintext, Entropy);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
            string protectedKey = Convert.ToBase64String(ciphertext);
            CryptographicOperations.ZeroMemory(ciphertext);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (string hash in request.Identifiers.Select(HashIdentifier).Distinct(StringComparer.Ordinal))
            {
                bool alreadyStored = document.Entries.Any(entry =>
                    string.Equals(entry.IdentifierHash, hash, StringComparison.Ordinal) &&
                    string.Equals(entry.ProtectedEKey, protectedKey, StringComparison.Ordinal));
                if (!alreadyStored)
                    document.Entries.Add(new(hash, protectedKey, now));
            }

            await SaveAtomicAsync(document, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CacheDocument> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return new();
        try
        {
            await using var stream = new FileStream(
                _path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, useAsync: true);
            CacheDocument? document = await JsonSerializer.DeserializeAsync<CacheDocument>(
                stream, CacheJsonContext.Default.CacheDocument, cancellationToken).ConfigureAwait(false);
            return document is { Version: FormatVersion } && document.Entries is not null
                ? document
                : new();
        }
        catch (JsonException) { return new(); }
        catch (IOException) { return new(); }
        catch (UnauthorizedAccessException) { return new(); }
    }

    private async Task SaveAtomicAsync(CacheDocument document, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, document, CacheJsonContext.Default.CacheDocument, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static string HashIdentifier(string identifier)
    {
        string normalized = KeyIdentifier.Normalize(identifier);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    internal sealed class CacheDocument
    {
        public int Version { get; set; } = FormatVersion;
        public List<CacheEntry> Entries { get; set; } = [];
    }

    internal sealed class CacheEntry
    {
        public CacheEntry() { }
        public CacheEntry(string identifierHash, string protectedEKey, DateTimeOffset updatedUtc)
        {
            IdentifierHash = identifierHash;
            ProtectedEKey = protectedEKey;
            UpdatedUtc = updatedUtc;
        }

        public string IdentifierHash { get; set; } = "";
        public string ProtectedEKey { get; set; } = "";
        public DateTimeOffset UpdatedUtc { get; set; }
    }

    private static class Dpapi
    {
        private const int CryptProtectUiForbidden = 0x1;

        public static byte[] Protect(byte[] plaintext, byte[] entropy) =>
            Transform(plaintext, entropy, protect: true);

        public static byte[] Unprotect(byte[] ciphertext, byte[] entropy) =>
            Transform(ciphertext, entropy, protect: false);

        private static byte[] Transform(byte[] input, byte[] entropy, bool protect)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException("DPAPI is available on Windows only.");

            using var inputBlob = new DataBlob(input);
            using var entropyBlob = new DataBlob(entropy);
            DataBlobNative output = default;
            bool success = protect
                ? CryptProtectData(ref inputBlob.Value, null, ref entropyBlob.Value, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref inputBlob.Value, IntPtr.Zero, ref entropyBlob.Value, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output);
            if (!success)
                throw new CryptographicException(Marshal.GetLastPInvokeError());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                if (output.Data != IntPtr.Zero)
                    LocalFree(output.Data);
            }
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptProtectData(
            ref DataBlobNative input, string? description, ref DataBlobNative entropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlobNative output);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptUnprotectData(
            ref DataBlobNative input, IntPtr description, ref DataBlobNative entropy,
            IntPtr reserved, IntPtr prompt, int flags, out DataBlobNative output);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlobNative
        {
            public int Size;
            public IntPtr Data;
        }

        private sealed class DataBlob : IDisposable
        {
            public DataBlobNative Value;

            public DataBlob(byte[] bytes)
            {
                Value.Size = bytes.Length;
                Value.Data = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, Value.Data, bytes.Length);
            }

            public void Dispose()
            {
                if (Value.Data == IntPtr.Zero)
                    return;
                // Clear unmanaged plaintext without requiring /unsafe.  Zeroing
                // in managed chunks also avoids allocating another key-sized copy.
                byte[] zeros = new byte[Math.Min(Value.Size, 256)];
                for (int offset = 0; offset < Value.Size; offset += zeros.Length)
                    Marshal.Copy(zeros, 0, IntPtr.Add(Value.Data, offset),
                        Math.Min(zeros.Length, Value.Size - offset));
                Marshal.FreeHGlobal(Value.Data);
                Value = default;
            }
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(DpapiEKeyCacheProvider.CacheDocument))]
internal partial class CacheJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
