using System.Collections.ObjectModel;
using System.Text.Json;

namespace MFlacDrop;

/// <summary>
/// Offline EKey source. JSON accepts either an object map or a list containing
/// identifier/fileName/mediaFileName/mediaMid plus ekey. Text accepts
/// identifier TAB ekey, identifier=ekey, or identifier|ekey.
/// </summary>
internal sealed class ImportedEKeyProvider : IKeyProvider
{
    private readonly IReadOnlyList<ImportedEKey> _entries;

    private ImportedEKeyProvider(IEnumerable<ImportedEKey> entries)
    {
        _entries = new ReadOnlyCollection<ImportedEKey>(entries.ToArray());
    }

    public string Name => "imported EKey file";
    public int Count => _entries.Count;

    public static ImportedEKeyProvider Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("EKey import file was not found.", path);

        string text = File.ReadAllText(path);
        IEnumerable<ImportedEKey> entries = Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? ParseJson(text)
            : ParseText(text);
        return new(entries);
    }

    public ValueTask<IReadOnlyList<KeyLookupResult>> GetKeysAsync(
        KeyLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<KeyLookupResult>();
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ImportedEKey entry in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (KeyIdentifier.IsMatch(entry.Identifier, request) && seenKeys.Add(entry.EKey))
                results.Add(new(entry.EKey, Name, entry.Identifier));
        }
        return ValueTask.FromResult<IReadOnlyList<KeyLookupResult>>(results.AsReadOnly());
    }

    private static IEnumerable<ImportedEKey> ParseJson(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 16
        });
        var output = new List<ImportedEKey>();
        JsonElement root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    Add(output, property.Name, property.Value.GetString());
                else if (property.Value.ValueKind == JsonValueKind.Object)
                    ParseObject(property.Value, property.Name, output);
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in root.EnumerateArray())
                if (element.ValueKind == JsonValueKind.Object)
                    ParseObject(element, null, output);
        }
        else
        {
            throw new FormatException("EKey JSON must be an object or an array.");
        }

        return output;
    }

    private static void ParseObject(JsonElement element, string? fallbackIdentifier, List<ImportedEKey> output)
    {
        string? ekey = GetString(element, "ekey", "eKey", "key");
        if (ekey is null)
            return;

        string? identifier = GetString(element,
            "identifier", "fileName", "filename", "filePath", "file_path",
            "mediaFileName", "media_filename", "mediaMid", "media_mid", "mid", "fileId", "file_id");
        Add(output, identifier ?? fallbackIdentifier, ekey);
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
        {
            if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }

    private static IEnumerable<ImportedEKey> ParseText(string text)
    {
        var output = new List<ImportedEKey>();
        int lineNumber = 0;
        foreach (string rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            lineNumber++;
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            int separator = line.IndexOf('\t');
            if (separator < 1) separator = line.IndexOf('|');
            if (separator < 1) separator = line.IndexOf('=');
            if (separator < 1)
                throw new FormatException($"EKey import line {lineNumber} has no separator.");

            string identifier = line[..separator].Trim().Trim('"');
            string ekey = line[(separator + 1)..].Trim().Trim('"');
            if (!EKeyText.IsValid(ekey))
                throw new FormatException($"EKey import line {lineNumber} has an invalid EKey.");
            Add(output, identifier, ekey);
        }
        return output;
    }

    private static void Add(List<ImportedEKey> output, string? identifier, string? ekey)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !EKeyText.IsValid(ekey))
            return;
        string key = EKeyText.Normalize(ekey!);
        if (!output.Any(entry =>
            string.Equals(KeyIdentifier.Normalize(entry.Identifier), KeyIdentifier.Normalize(identifier), StringComparison.Ordinal) &&
            string.Equals(entry.EKey, key, StringComparison.Ordinal)))
            output.Add(new(identifier.Trim(), key));
    }

    private sealed record ImportedEKey(string Identifier, string EKey);
}
