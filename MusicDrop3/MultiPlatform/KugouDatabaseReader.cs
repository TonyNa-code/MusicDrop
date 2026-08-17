// SPDX-License-Identifier: MIT
// The page cipher is adapted from leafxdd/unlock-music algo/kgm/pc_kugou_db
// (Copyright 2020-2021 Unlock Music, MIT License). SQLite access uses
// Microsoft.Data.Sqlite (MIT License). See THIRD-PARTY-NOTICES.txt.

using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MusicDrop3.MultiPlatform;

internal static class KugouDatabaseReader
{
    private const int PageSize = 0x400;
    private const long MaximumDatabaseSize = 512L * 1024 * 1024;
    private static readonly byte[] SqliteHeader = "SQLite format 3\0"u8.ToArray();
    private static readonly byte[] DefaultMasterKey =
    {
        0x1D, 0x61, 0x31, 0x45, 0xB2, 0x47, 0xBF, 0x7F,
        0x3D, 0x18, 0x96, 0x72, 0x14, 0x4F, 0xE4, 0xBF,
        0x00, 0x00, 0x00, 0x00, 0x73, 0x41, 0x6C, 0x54,
    };
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static readonly Dictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultDatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Kugou8", "KGMusicV3.db");

    public static string ResolveDatabasePath(string configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configuredPath.Trim()));
        return DefaultDatabasePath;
    }

    public static async Task<string?> FindEKeyAsync(
        string configuredPath,
        string audioHash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(audioHash))
            throw new InvalidDataException("KGM v5 缺少 AudioHash，无法查询本地密钥库。");
        if (audioHash.Length > 4096 || audioHash.Any(char.IsControl))
            throw new InvalidDataException("KGM v5 AudioHash 格式无效。");

        string databasePath = ResolveDatabasePath(configuredPath);
        var info = new FileInfo(databasePath);
        if (!info.Exists)
            throw new FileNotFoundException(
                string.IsNullOrWhiteSpace(configuredPath)
                    ? $"未找到酷狗本地密钥库：{databasePath}。请先在酷狗客户端中合法下载对应歌曲，或手动选择 KGMusicV3.db。"
                    : $"指定的酷狗本地密钥库不存在：{databasePath}",
                databasePath);
        if (info.Length < SqliteHeader.Length || info.Length > MaximumDatabaseSize)
            throw new InvalidDataException($"酷狗本地密钥库大小超出安全范围：{info.Length} 字节。");

        string cacheKey = Path.GetFullPath(databasePath);
        await CacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            info.Refresh();
            if (!Cache.TryGetValue(cacheKey, out CacheEntry? entry) ||
                entry.Length != info.Length || entry.LastWriteUtc != info.LastWriteTimeUtc)
            {
                IReadOnlyDictionary<string, string> keys = await LoadKeyMapAsync(
                    cacheKey, cancellationToken).ConfigureAwait(false);
                entry = new(info.Length, info.LastWriteTimeUtc, keys);
                Cache[cacheKey] = entry;
            }
            return entry.Keys.GetValueOrDefault(audioHash);
        }
        finally
        {
            CacheGate.Release();
        }
    }

    private static async Task<IReadOnlyDictionary<string, string>> LoadKeyMapAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        byte[] database = await File.ReadAllBytesAsync(databasePath, cancellationToken).ConfigureAwait(false);
        DecryptDatabase(database);

        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(), "MusicDrop3", "kugou-db", Guid.NewGuid().ToString("N"));
        string temporaryDatabase = Path.Combine(temporaryDirectory, "keys.db");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            await File.WriteAllBytesAsync(temporaryDatabase, database, cancellationToken).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(database);

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = temporaryDatabase,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            };
            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT EncryptionKeyId, EncryptionKey
                FROM ShareFileItems
                WHERE EncryptionKey IS NOT NULL AND EncryptionKey != ''
                """;
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(
                cancellationToken).ConfigureAwait(false);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1)) continue;
                string id = reader.GetString(0);
                string key = reader.GetString(1);
                if (id.Length is > 0 and <= 4096 && key.Length is > 0 and <= 65_535)
                    result[id] = key;
            }
            return result;
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException(
                "酷狗本地密钥库无法读取，可能是客户端数据库版本已变化或文件损坏：" + ex.Message, ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(database);
            await DeleteTemporaryDirectoryAsync(temporaryDirectory).ConfigureAwait(false);
        }
    }

    internal static void DecryptDatabase(Span<byte> database)
    {
        if (database.Length < SqliteHeader.Length)
            throw new InvalidDataException("酷狗本地密钥库被截断。");
        if (database[..SqliteHeader.Length].SequenceEqual(SqliteHeader))
            return;
        if (database.Length == 0 || database.Length % PageSize != 0)
            throw new InvalidDataException($"加密酷狗本地密钥库不是 {PageSize} 字节页的整数倍。");

        DecryptFirstPage(database[..PageSize]);
        uint pageCount = checked((uint)(database.Length / PageSize));
        for (uint page = 2; page <= pageCount; page++)
        {
            int offset = checked((int)((page - 1) * PageSize));
            DecryptPage(database.Slice(offset, PageSize), page);
        }
    }

    private static void DecryptFirstPage(Span<byte> page)
    {
        ValidateFirstPageHeader(page);
        Span<byte> expectedHeader = stackalloc byte[8];
        page.Slice(0x10, 8).CopyTo(expectedHeader);
        page.Slice(0x08, 8).CopyTo(page.Slice(0x10, 8));
        DecryptPage(page[0x10..], 1);
        if (!page.Slice(0x10, 8).SequenceEqual(expectedHeader))
            throw new InvalidDataException("酷狗本地密钥库第 1 页解密校验失败。");
        SqliteHeader.CopyTo(page);
    }

    private static void ValidateFirstPageHeader(ReadOnlySpan<byte> page)
    {
        uint o10 = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(0x10, 4));
        uint o14 = BinaryPrimitives.ReadUInt32LittleEndian(page.Slice(0x14, 4));
        uint v6 = unchecked(((o10 & 0xFF) << 8) | ((o10 & 0xFF00) << 16));
        bool valid = o14 == 0x20204000 &&
            unchecked(v6 - 0x200) <= 0xFE00 &&
            (unchecked(v6 - 1) & v6) == 0;
        if (!valid)
            throw new InvalidDataException("酷狗本地密钥库第 1 页文件头无效。");
    }

    private static void DecryptPage(Span<byte> buffer, uint page)
    {
        byte[] key = DerivePageKey(page);
        byte[] iv = DerivePageIv(page);
        byte[] decrypted;
        using (Aes aes = Aes.Create())
        {
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.None;
            aes.Key = key;
            aes.IV = iv;
            decrypted = aes.CreateDecryptor().TransformFinalBlock(buffer.ToArray(), 0, buffer.Length);
        }
        decrypted.CopyTo(buffer);
        CryptographicOperations.ZeroMemory(decrypted);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(iv);
    }

    internal static byte[] DerivePageKey(uint page)
    {
        byte[] material = (byte[])DefaultMasterKey.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(material.AsSpan(0x10, 4), page);
        byte[] digest = MD5.HashData(material);
        CryptographicOperations.ZeroMemory(material);
        return digest;
    }

    internal static byte[] DerivePageIv(uint page)
    {
        byte[] ivSeed = new byte[16];
        page = unchecked(page + 1);
        for (int i = 0; i < ivSeed.Length; i += 4)
        {
            page = DeriveIvSeed(page);
            BinaryPrimitives.WriteUInt32LittleEndian(ivSeed.AsSpan(i, 4), page);
        }
        byte[] digest = MD5.HashData(ivSeed);
        CryptographicOperations.ZeroMemory(ivSeed);
        return digest;
    }

    private static uint DeriveIvSeed(uint seed)
    {
        uint left = unchecked(seed * 0x9EF4);
        uint right = unchecked(seed / 0xCE26 * 0x7FFFFF07);
        uint value = unchecked(left - right);
        return (value & 0x80000000) == 0 ? value : unchecked(value + 0x7FFFFF07);
    }

    private static async Task DeleteTemporaryDirectoryAsync(string directory)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                await Task.Delay(50 * (attempt + 1)).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < 3)
            {
                await Task.Delay(50 * (attempt + 1)).ConfigureAwait(false);
            }
        }
    }

    private sealed record CacheEntry(
        long Length,
        DateTime LastWriteUtc,
        IReadOnlyDictionary<string, string> Keys);
}
