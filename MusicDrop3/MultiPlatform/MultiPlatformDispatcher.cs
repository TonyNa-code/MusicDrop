// SPDX-License-Identifier: MIT

namespace MusicDrop3.MultiPlatform;

internal sealed class MultiPlatformDispatcher
{
    private readonly IReadOnlyList<IPlatformDecryptor> decryptors;

    public MultiPlatformDispatcher(IEnumerable<IPlatformDecryptor> decryptors)
    {
        this.decryptors = decryptors.ToArray();
    }

    public static MultiPlatformDispatcher CreateDefault() => new(new IPlatformDecryptor[]
    {
        new NcmDecryptor(), new KwmDecryptor(), new KgmDecryptor(),
        new TmDecryptor(), new XiamiDecryptor(), new XimalayaDecryptor(),
    });

    public IReadOnlyCollection<string> Extensions => decryptors
        .SelectMany(item => item.Extensions)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task<(IPlatformDecryptor Decryptor, PlatformProbeResult Probe)> ProbeAsync(
        string path,
        MultiPlatformOptions options,
        CancellationToken cancellationToken)
    {
        var failures = new List<(IPlatformDecryptor Decryptor, PlatformProbeResult Probe)>();
        foreach (IPlatformDecryptor decryptor in decryptors)
        {
            if (!decryptor.Extensions.Any(extension =>
                    path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))) continue;
            PlatformProbeResult probe = await decryptor.ProbeAsync(path, options, cancellationToken);
            if (probe.CanDecrypt) return (decryptor, probe);
            failures.Add((decryptor, probe));
        }
        if (failures.Count > 0)
        {
            (IPlatformDecryptor Decryptor, PlatformProbeResult Probe) best = failures
                .OrderByDescending(item => item.Probe.RequiresExternalKey)
                .First();
            return best;
        }
        throw new NotSupportedException("尚未支持此加密扩展名：" + Path.GetExtension(path));
    }
}
