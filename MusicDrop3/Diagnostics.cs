using System.Text.Json;
using MFlacDrop.OfflineQmc;
using MusicDrop3.MultiPlatform;

namespace MFlacDrop;

internal static class Diagnostics
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) return 64;
        return args[0].ToLowerInvariant() switch
        {
            "probe-json" => await ProbeJsonAsync(args.Skip(1).ToArray()),
            "platform-probe-json" => await PlatformProbeJsonAsync(args.Skip(1).ToArray()),
            "key-cache-roundtrip" => await CacheRoundtripAsync(args.Skip(1).ToArray()),
            "db-lookup" => await DbLookupAsync(args.Skip(1).ToArray()),
            _ => 64
        };
    }

    private static async Task<int> ProbeJsonAsync(string[] paths)
    {
        foreach (string path in paths)
        {
            QmcProbeResult result = await OfflineQmcDecryptor.ProbeAsync(path);
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                path,
                footer = result.FooterKind.ToString(),
                result.AudioLength,
                result.FooterLength,
                result.MediaId,
                result.MediaFileName,
                result.HasEmbeddedEKey,
                result.RequiresExternalEKey,
                result.CanDecrypt,
                result.Error
            }));
        }
        return 0;
    }

    private static async Task<int> PlatformProbeJsonAsync(string[] paths)
    {
        var dispatcher = MultiPlatformDispatcher.CreateDefault();
        foreach (string path in paths)
        {
            if (AudioConverter.IsSupportedInput(path) &&
                (path.EndsWith(".ncm", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".kwm", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".kw", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".kgm", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".kgma", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".vpr", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".kgg", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".kgm.flac", StringComparison.OrdinalIgnoreCase) ||
                 path.EndsWith(".vpr.flac", StringComparison.OrdinalIgnoreCase)))
            {
                (_, PlatformProbeResult probe) = await dispatcher.ProbeAsync(path, new MultiPlatformOptions(), CancellationToken.None);
                Console.WriteLine(JsonSerializer.Serialize(new { path, platform = probe.Platform.ToString(), probe.FormatName,
                    probe.AudioExtension, probe.CanDecrypt, probe.RequiresExternalKey, probe.Error, probe.KeyIdentifier }));
            }
            else
            {
                QmcProbeResult probe = await OfflineQmcDecryptor.ProbeAsync(path);
                Console.WriteLine(JsonSerializer.Serialize(new { path, platform = SourcePlatform.QqMusic.ToString(), formatName = "QMC",
                    audioExtension = probe.DetectedAudioExtension, probe.CanDecrypt, requiresExternalKey = probe.RequiresExternalEKey,
                    probe.Error, keyIdentifier = probe.MediaId }));
            }
        }
        return 0;
    }

    private static async Task<int> CacheRoundtripAsync(string[] args)
    {
        if (args.Length != 3) return 64;
        var request = new KeyLookupRequest(args[0], MediaFileName: args[0], MediaMid: args[0]);
        var cache = new DpapiEKeyCacheProvider(args[1]);
        await cache.StoreAsync(request, args[2]);
        IReadOnlyList<KeyLookupResult> results = await cache.GetKeysAsync(request);
        Console.WriteLine(results.Count == 0 ? "MISS" : $"PASS {results.Count}");
        return results.Any(result => result.EKey == args[2]) ? 0 : 2;
    }

    private static async Task<int> DbLookupAsync(string[] args)
    {
        if (args.Length != 2) return 64;
        using var provider = new PlayerProcessDbKeyProvider(args[0]);
        var request = new KeyLookupRequest(args[1], MediaFileName: args[1], MediaMid: args[1]);
        IReadOnlyList<KeyLookupResult> results = await provider.GetKeysAsync(request);
        foreach (KeyLookupResult result in results)
            Console.WriteLine($"PASS {result.Provider} {result.MatchedIdentifier}");
        if (results.Count == 0) Console.WriteLine("MISS");
        return results.Count == 0 ? 2 : 0;
    }
}
