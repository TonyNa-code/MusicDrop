using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MusicDrop.Core;
using MusicDrop3.MultiPlatform;

string root = Path.Combine(Path.GetTempPath(), "MusicDropCoreHarness", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
try
{
    CheckExpandedSignatures();
    await CheckTmAsync();
    await CheckXiamiAsync();
    await CheckXimalayaAsync(x3m: false);
    await CheckXimalayaAsync(x3m: true);
    await CheckXmAmbiguousDispatchAsync();
    await CheckPortableFacadeAtomicityAsync();
    await CheckStreamingPerformanceAsync();
    Console.WriteLine($"PASS {passed} cross-platform core checks");
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

void CheckExpandedSignatures()
{
    var cases = new Dictionary<string, byte[]>
    {
        [".aac"] = [0xFF, 0xF1, 0x50, 0x80],
        [".ape"] = "MAC "u8.ToArray(),
        [".dsf"] = "DSD "u8.ToArray(),
        [".dff"] = "FRM8"u8.ToArray(),
        [".aiff"] = [.. "FORM"u8, 0, 0, 0, 20, .. "AIFF"u8],
        [".wma"] = [0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11,
            0xA6, 0xD9, 0x00, 0xAA, 0x00, 0x62, 0xCE, 0x6C],
    };
    foreach ((string expected, byte[] header) in cases)
        Equal(expected, AudioSignatures.Detect(header), "signature " + expected);
    byte[] opus = new byte[32];
    "OggS"u8.CopyTo(opus);
    "OpusHead"u8.CopyTo(opus.AsSpan(12));
    Equal(".opus", AudioSignatures.Detect(opus), "Opus-in-Ogg signature");
    Pass("expanded AAC/APE/WMA/AIFF/DSF/DFF/Opus signatures");
}

async Task CheckTmAsync()
{
    byte[] expected = BuildM4aPayload(2 * 1024 * 1024 + 17);
    byte[] encrypted = expected.ToArray();
    "QQMU"u8.CopyTo(encrypted);
    encrypted.AsSpan(4, 4).Fill(0xA5);
    string input = Path.Combine(root, "ios.tm2");
    string output = Path.Combine(root, "ios.m4a");
    await File.WriteAllBytesAsync(input, encrypted);
    var decryptor = new TmDecryptor();
    PlatformProbeResult probe = await decryptor.ProbeAsync(input, new(), CancellationToken.None);
    True(probe.CanDecrypt && probe.AudioExtension == ".m4a", "TM probe");
    await decryptor.DecryptAsync(input, output, new(), CancellationToken.None);
    byte[] actual = await File.ReadAllBytesAsync(output);
    True(expected.AsSpan().SequenceEqual(actual), "TM byte-exact restore");
    Pass("QQ iOS TM0/TM2/TM3/TM6 header restore");
}

async Task CheckXiamiAsync()
{
    byte[] expected = BuildFlacPayload(2 * 1024 * 1024 + 31);
    const int encryptStart = 127;
    const byte mask = 0xA7;
    byte[] encryptedAudio = expected.ToArray();
    for (int i = encryptStart; i < encryptedAudio.Length; i++) encryptedAudio[i] ^= mask;
    byte[] encrypted = new byte[16 + encryptedAudio.Length];
    "ifmt"u8.CopyTo(encrypted);
    "FLAC"u8.CopyTo(encrypted.AsSpan(4));
    encrypted.AsSpan(8, 4).Fill(0xFE);
    encrypted[12] = (byte)encryptStart;
    encrypted[13] = (byte)(encryptStart >> 8);
    encrypted[14] = (byte)(encryptStart >> 16);
    encrypted[15] = mask;
    encryptedAudio.CopyTo(encrypted, 16);
    string input = Path.Combine(root, "legacy-xiami.xm");
    string output = Path.Combine(root, "legacy-xiami.flac");
    await File.WriteAllBytesAsync(input, encrypted);
    var decryptor = new XiamiDecryptor();
    PlatformProbeResult probe = await decryptor.ProbeAsync(input, new(), CancellationToken.None);
    True(probe.CanDecrypt && probe.AudioExtension == ".flac", "Xiami probe");
    await decryptor.DecryptAsync(input, output, new(), CancellationToken.None);
    byte[] actual = await File.ReadAllBytesAsync(output);
    True(expected.AsSpan().SequenceEqual(actual), "Xiami byte-exact decrypt");
    Pass("Xiami XM partial-XOR vector across pooled blocks");
}

async Task CheckXimalayaAsync(bool x3m)
{
    byte[] expected = BuildFlacPayload(2 * 1024 * 1024 + (x3m ? 43 : 37));
    byte[] encrypted = expected.ToArray();
    XimalayaDecryptor.ScrambleForTests(expected.AsSpan(0, 1024), x3m).CopyTo(encrypted, 0);
    string variant = x3m ? "x3m" : "x2m";
    string input = Path.Combine(root, "spoken." + variant);
    string output = Path.Combine(root, "spoken-" + variant + ".flac");
    await File.WriteAllBytesAsync(input, encrypted);
    var decryptor = new XimalayaDecryptor();
    PlatformProbeResult probe = await decryptor.ProbeAsync(input, new(), CancellationToken.None);
    True(probe.CanDecrypt && probe.FormatName.Equals(variant, StringComparison.OrdinalIgnoreCase),
        variant + " probe");
    await decryptor.DecryptAsync(input, output, new(), CancellationToken.None);
    byte[] actual = await File.ReadAllBytesAsync(output);
    True(expected.AsSpan().SequenceEqual(actual), variant + " byte-exact decrypt");
    Pass("Ximalaya " + variant.ToUpperInvariant() + " 1024-byte header unscramble");
}

async Task CheckXmAmbiguousDispatchAsync()
{
    byte[] expected = BuildFlacPayload(4096);
    byte[] encrypted = expected.ToArray();
    XimalayaDecryptor.ScrambleForTests(expected.AsSpan(0, 1024), x3m: false).CopyTo(encrypted, 0);
    string input = Path.Combine(root, "ambiguous.xm");
    await File.WriteAllBytesAsync(input, encrypted);
    var dispatcher = MultiPlatformDispatcher.CreateDefault();
    (IPlatformDecryptor selected, PlatformProbeResult probe) = await dispatcher.ProbeAsync(
        input, new(), CancellationToken.None);
    True(selected is XimalayaDecryptor && probe.CanDecrypt,
        "dispatcher stopped after the first .xm decoder instead of trying both");
    Pass("ambiguous .xm dispatch tries Xiami and Ximalaya safely");
}

async Task CheckPortableFacadeAtomicityAsync()
{
    byte[] expected = BuildFlacPayload(128 * 1024 + 9);
    const int encryptStart = 9;
    const byte mask = 0x4D;
    byte[] encrypted = new byte[16 + expected.Length];
    "ifmtFLAC"u8.CopyTo(encrypted);
    encrypted.AsSpan(8, 4).Fill(0xFE);
    encrypted[12] = encryptStart;
    encrypted[15] = mask;
    expected.CopyTo(encrypted, 16);
    for (int i = encryptStart; i < expected.Length; i++) encrypted[16 + i] ^= mask;
    string input = Path.Combine(root, "facade.xm");
    string output = Path.Combine(root, "facade.flac");
    await File.WriteAllBytesAsync(input, encrypted);
    var service = new PortableAudioService();
    PortableDecryptResult result = await service.DecryptToFileAsync(input, output);
    True(result.AudioExtension == ".flac" && File.Exists(input) && File.Exists(output), "facade output/source state");
    True(!Directory.EnumerateFiles(root, "*.partial-*", SearchOption.AllDirectories).Any(), "facade left partial output");
    byte[] actual = await File.ReadAllBytesAsync(output);
    True(expected.AsSpan().SequenceEqual(actual), "facade byte exact");
    Pass("portable facade strict probe, atomic finalization and source preservation");
}

async Task CheckStreamingPerformanceAsync()
{
    const int size = 32 * 1024 * 1024 + 113;
    byte[] expected = BuildKwmPayload(size);
    ulong resourceId = 9876543210123456UL;
    byte[] encrypted = BuildKwm(expected, resourceId);
    string input = Path.Combine(root, "throughput.kwm");
    string output = Path.Combine(root, "throughput.flac");
    await File.WriteAllBytesAsync(input, encrypted);
    var stopwatch = Stopwatch.StartNew();
    await new KwmDecryptor().DecryptAsync(input, output, new(), CancellationToken.None);
    stopwatch.Stop();
    byte[] actualHash = await SHA256.HashDataAsync(File.OpenRead(output));
    byte[] expectedHash = SHA256.HashData(expected);
    True(actualHash.AsSpan().SequenceEqual(expectedHash), "streaming benchmark digest mismatch");
    double mibPerSecond = size / 1024d / 1024d / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
    Console.WriteLine($"INFO pooled streaming KWM: {mibPerSecond:F1} MiB/s ({size / 1024 / 1024} MiB)");
    Pass("1 MiB ArrayPool streaming benchmark and digest verification");
}

static byte[] BuildFlacPayload(int length)
{
    byte[] value = RandomNumberGenerator.GetBytes(length);
    "fLaC"u8.CopyTo(value);
    return value;
}

static byte[] BuildM4aPayload(int length)
{
    byte[] value = RandomNumberGenerator.GetBytes(length);
    BinaryPrimitives.WriteUInt32BigEndian(value, 32);
    "ftypM4A "u8.CopyTo(value.AsSpan(4));
    return value;
}

static byte[] BuildKwmPayload(int length)
{
    byte[] value = new byte[length];
    "fLaC"u8.CopyTo(value);
    for (int i = 4; i < value.Length; i++) value[i] = (byte)(i * 31 + 17);
    return value;
}

static byte[] BuildKwm(byte[] plaintext, ulong resourceId)
{
    byte[] output = new byte[0x400 + plaintext.Length];
    "yeelion-kuwo-tme"u8.CopyTo(output);
    BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(0x18, 8), resourceId);
    "320flac"u8.CopyTo(output.AsSpan(0x30));
    byte[] keyText = Encoding.ASCII.GetBytes(resourceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    ReadOnlySpan<byte> predefined = "MoOtOiTvINGwd2E6n0E1i7L5t2IoOoNk"u8;
    for (int i = 0; i < plaintext.Length; i++)
    {
        byte mask = (byte)(predefined[i & 0x1F] ^ keyText[(i & 0x1F) % keyText.Length]);
        output[0x400 + i] = (byte)(plaintext[i] ^ mask);
    }
    return output;
}

void Pass(string message)
{
    passed++;
    Console.WriteLine("PASS " + message);
}

static void True(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
}
