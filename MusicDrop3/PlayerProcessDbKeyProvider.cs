using System.Runtime.InteropServices;

namespace MFlacDrop;

/// <summary>
/// Reads QQ Music's Android player_process_db through the SQLite library shipped
/// with Windows. The database is opened read-only and never copied or modified.
/// </summary>
internal sealed class PlayerProcessDbKeyProvider : IKeyProvider, IDisposable
{
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int SqliteOpenReadOnly = 0x00000001;
    private const int SqliteTransient = -1;

    private static readonly TableDefinition[] Tables =
    [
        new("audio_file_ekey_table", "file_path", "ekey"),
        new("EKeyFileInfo", "filePath", "eKey"),
        new("p2p_cache_info_table", "file_id", "ekey")
    ];

    private readonly IntPtr _database;
    private bool _disposed;

    public PlayerProcessDbKeyProvider(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("player_process_db was not found.", fullPath);
        // READONLY is sufficient and avoids URI escaping edge cases with Windows
        // drive letters and non-ASCII file names. No write/create flag is passed.
        int result = Native.sqlite3_open_v2(fullPath, out _database,
            SqliteOpenReadOnly, IntPtr.Zero);
        if (result != SqliteOk)
        {
            string message = GetError(_database, result);
            if (_database != IntPtr.Zero)
                Native.sqlite3_close_v2(_database);
            throw new InvalidDataException("Unable to open player_process_db read-only: " + message);
        }
    }

    public string Name => "player_process_db";

    public ValueTask<IReadOnlyList<KeyLookupResult>> GetKeysAsync(
        KeyLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<KeyLookupResult>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (TableDefinition table in Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TableExists(table.Name))
                continue;

            // The table/column identifiers are compile-time constants above. All
            // untrusted lookup values use sqlite3_bind_text parameters.
            string exactSql = $"SELECT [{table.IdentifierColumn}], [{table.KeyColumn}] " +
                              $"FROM [{table.Name}] " +
                              $"WHERE lower([{table.IdentifierColumn}]) = lower(?1) " +
                              $"OR lower([{table.IdentifierColumn}]) = lower(?2) " +
                              $"OR lower([{table.IdentifierColumn}]) = lower(?3) LIMIT 32";
            foreach (string candidate in request.Identifiers)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string normalized = KeyIdentifier.Normalize(candidate);
                if (normalized.Length == 0)
                    continue;

                string fileName = Path.GetFileName(candidate.Replace('/', Path.DirectorySeparatorChar));
                if (fileName.Length == 0)
                    fileName = candidate.Trim();
                string normalizedFileName = KeyIdentifier.Normalize(fileName);
                foreach ((string StoredIdentifier, string EKey) row in Query(
                    exactSql, candidate, fileName, normalizedFileName))
                {
                    AddResult(results, seenKeys, row, request, table);
                }
            }

            // Only a validated opaque MediaMid is eligible for substring
            // lookup. The stored DB identifier may wrap it (AIM...MID...), but
            // the query never searches using a title, basename, or path.
            string wrappedMidSql = $"SELECT [{table.IdentifierColumn}], [{table.KeyColumn}] " +
                                   $"FROM [{table.Name}] " +
                                   $"WHERE lower([{table.IdentifierColumn}]) " +
                                   "LIKE '%' || lower(?1) || '%' LIMIT 32";
            foreach (string mediaId in request.OpaqueMediaIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach ((string StoredIdentifier, string EKey) row in Query(wrappedMidSql, mediaId))
                    AddResult(results, seenKeys, row, request, table);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<KeyLookupResult>>(results.AsReadOnly());
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Native.sqlite3_close_v2(_database);
    }

    private bool TableExists(string tableName)
    {
        const string Sql = "SELECT name FROM sqlite_master WHERE type='table' AND name = ?1 LIMIT 1";
        IntPtr statement = Prepare(Sql);
        try
        {
            BindText(statement, 1, tableName);
            return Native.sqlite3_step(statement) == SqliteRow;
        }
        finally
        {
            Native.sqlite3_finalize(statement);
        }
    }

    private static void AddResult(
        List<KeyLookupResult> results,
        HashSet<string> seenKeys,
        (string StoredIdentifier, string EKey) row,
        KeyLookupRequest request,
        TableDefinition table)
    {
        if (!EKeyText.IsValid(row.EKey) || !KeyIdentifier.IsMatch(row.StoredIdentifier, request))
            return;
        string ekey = EKeyText.Normalize(row.EKey);
        if (seenKeys.Add(ekey))
            results.Add(new(ekey, "player_process_db/" + table.Name, row.StoredIdentifier));
    }

    private IEnumerable<(string StoredIdentifier, string EKey)> Query(
        string sql, params string[] parameters)
    {
        IntPtr statement = Prepare(sql);
        try
        {
            for (int index = 0; index < parameters.Length; index++)
                BindText(statement, index + 1, parameters[index]);
            while (true)
            {
                int result = Native.sqlite3_step(statement);
                if (result == SqliteDone)
                    yield break;
                if (result != SqliteRow)
                    throw new InvalidDataException("player_process_db query failed: " + GetError(_database, result));
                string? identifier = GetColumnText(statement, 0);
                string? ekey = GetColumnText(statement, 1);
                if (identifier is not null && ekey is not null)
                    yield return (identifier, ekey.Trim());
            }
        }
        finally
        {
            Native.sqlite3_finalize(statement);
        }
    }

    private IntPtr Prepare(string sql)
    {
        int result = Native.sqlite3_prepare_v2(_database, sql, -1, out IntPtr statement, IntPtr.Zero);
        if (result != SqliteOk)
            throw new InvalidDataException("player_process_db query could not be prepared: " + GetError(_database, result));
        return statement;
    }

    private void BindText(IntPtr statement, int index, string value)
    {
        int result = Native.sqlite3_bind_text16(statement, index, value, value.Length * sizeof(char), new IntPtr(SqliteTransient));
        if (result != SqliteOk)
            throw new InvalidDataException("player_process_db parameter binding failed: " + GetError(_database, result));
    }

    private static string? GetColumnText(IntPtr statement, int index)
    {
        IntPtr value = Native.sqlite3_column_text16(statement, index);
        if (value == IntPtr.Zero)
            return null;
        int bytes = Native.sqlite3_column_bytes16(statement, index);
        return Marshal.PtrToStringUni(value, bytes / sizeof(char));
    }

    private static string GetError(IntPtr database, int code)
    {
        if (database == IntPtr.Zero)
            return "SQLite error " + code;
        IntPtr value = Native.sqlite3_errmsg16(database);
        return Marshal.PtrToStringUni(value) ?? "SQLite error " + code;
    }

    private sealed record TableDefinition(string Name, string IdentifierColumn, string KeyColumn);

    private static class Native
    {
        // winsqlite3.dll is an operating-system component on supported Windows
        // versions. No native binary or NuGet package is redistributed.
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        internal static extern int sqlite3_open_v2(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
            out IntPtr database, int flags, IntPtr virtualFileSystem);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_close_v2(IntPtr database);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        internal static extern int sqlite3_prepare_v2(
            IntPtr database,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
            int byteCount,
            out IntPtr statement,
            IntPtr unusedTail);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
        internal static extern int sqlite3_bind_text16(
            IntPtr statement, int index, string value, int byteCount, IntPtr destructor);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_step(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_finalize(IntPtr statement);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_column_text16(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int sqlite3_column_bytes16(IntPtr statement, int column);

        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr sqlite3_errmsg16(IntPtr database);
    }
}
