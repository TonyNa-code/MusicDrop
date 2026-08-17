// SPDX-License-Identifier: MIT

using System.Text.Json;
using MFlacDrop.OfflineQmc;

namespace MusicDrop.Core;

/// <summary>
/// Local-only QMC EKey mapping. Keys are never logged or returned by probes.
/// JSON object/array and identifier=EKey text formats are supported; a line
/// containing only an EKey is treated as a verified fallback candidate.
/// </summary>
public sealed class QmcKeyring
{
    private const int MaximumEntries = 10_000;
    private readonly IReadOnlyList<Entry> entries;

    private QmcKeyring(IReadOnlyList<Entry> entries) => this.entries = entries;

    public int Count => entries.Count;

    public static QmcKeyring Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var info = new FileInfo(Path.GetFullPath(path));
        if (!info.Exists) throw new FileNotFoundException("QMC EKey file does not exist.", info.FullName);
        if (info.Length is <= 0 or > 1024 * 1024)
            throw new InvalidDataException("QMC EKey file size is outside the 1 MiB safety limit.");
        string text = File.ReadAllText(info.FullName);
        List<Entry> parsed = info.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? ParseJson(text)
            : ParseText(text);
        if (parsed.Count == 0) throw new InvalidDataException("QMC EKey file contains no valid entries.");
        if (parsed.Count > MaximumEntries) throw new InvalidDataException("QMC EKey file contains too many entries.");
        return new(parsed);
    }

    internal IReadOnlyList<string> GetCandidates(
        string inputPath, string? mediaFileName, string? mediaId)
    {
        string[] identifiers = new[] { mediaFileName, mediaId, inputPath, Path.GetFileName(inputPath) }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizeIdentifier(value!))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string? opaqueMediaId = IsOpaqueMediaId(mediaId) ? NormalizeIdentifier(mediaId!) : null;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Entry entry in entries)
        {
            bool match = entry.Identifier is null || identifiers.Contains(entry.Identifier, StringComparer.Ordinal) ||
                opaqueMediaId is not null && entry.Identifier.Contains(opaqueMediaId, StringComparison.Ordinal);
            if (match && seen.Add(entry.EKey)) result.Add(entry.EKey);
            if (result.Count >= 128) break;
        }
        return result;
    }

    private static List<Entry> ParseJson(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 16,
        });
        var output = new List<Entry>();
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    Add(output, property.Name, property.Value.GetString());
                else if (property.Value.ValueKind == JsonValueKind.Object)
                    AddObject(output, property.Value, property.Name);
            }
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement element in document.RootElement.EnumerateArray())
                if (element.ValueKind == JsonValueKind.Object) AddObject(output, element, null);
        }
        else throw new InvalidDataException("QMC EKey JSON must be an object or array.");
        return output;
    }

    private static void AddObject(List<Entry> output, JsonElement element, string? fallback)
    {
        string? ekey = GetString(element, "ekey", "eKey", "key");
        string? identifier = GetString(element, "identifier", "fileName", "filename", "filePath",
            "mediaFileName", "mediaMid", "mid", "fileId") ?? fallback;
        Add(output, identifier, ekey);
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (string name in names)
            if (element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static List<Entry> ParseText(string text)
    {
        var output = new List<Entry>();
        int lineNumber = 0;
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            int separator = line.IndexOf('\t');
            if (separator < 1) separator = line.IndexOf('|');
            if (separator < 1) separator = line.IndexOf('=');
            string? identifier = separator < 1 ? null : line[..separator].Trim().Trim('"');
            string ekey = (separator < 1 ? line : line[(separator + 1)..]).Trim().Trim('"');
            if (!TryNormalizeEKey(ekey, out string? normalized))
                throw new InvalidDataException($"QMC EKey line {lineNumber} is not valid base64 EKey text.");
            AddNormalized(output, identifier, normalized!);
        }
        return output;
    }

    private static void Add(List<Entry> output, string? identifier, string? ekey)
    {
        if (!TryNormalizeEKey(ekey, out string? normalized)) return;
        AddNormalized(output, identifier, normalized!);
    }

    private static void AddNormalized(List<Entry> output, string? identifier, string ekey)
    {
        string? normalizedIdentifier = string.IsNullOrWhiteSpace(identifier)
            ? null : NormalizeIdentifier(identifier);
        if (!output.Any(item => item.Identifier == normalizedIdentifier && item.EKey == ekey))
            output.Add(new(normalizedIdentifier, ekey));
    }

    private static bool TryNormalizeEKey(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string text = value.Trim();
        if (text.Length is < 16 or > 8192 || (text.Length & 3) != 0) return false;
        try
        {
            _ = QmcEKey.DeriveMasterKey(text);
            normalized = text;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or IOException or OverflowException)
        {
            return false;
        }
    }

    private static string NormalizeIdentifier(string value)
    {
        string text = Path.GetFileName(value.Trim().Replace('/', Path.DirectorySeparatorChar));
        string[] suffixes =
        {
            ".mflac0", ".mflac1", ".mflaca", ".mflach", ".mflacl", ".mflacm", ".mflac",
            ".mgg0", ".mgg1", ".mgga", ".mggh", ".mggl", ".mggm", ".mgg",
            ".qmcflac", ".qmcogg", ".qmc0", ".qmc2", ".qmc3", ".qmc4", ".qmc6", ".qmc8",
        };
        bool removed;
        do
        {
            removed = false;
            foreach (string suffix in suffixes)
            {
                if (!text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                text = text[..^suffix.Length];
                removed = true;
                break;
            }
        } while (removed);
        return text.Trim().ToUpperInvariant();
    }

    private static bool IsOpaqueMediaId(string? value) => !string.IsNullOrWhiteSpace(value) &&
        value.Trim().Length is >= 14 and <= 32 && value.Trim().All(char.IsAsciiLetterOrDigit);

    private sealed record Entry(string? Identifier, string EKey);
}
