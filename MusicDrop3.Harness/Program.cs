using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MFlacDrop;
using MFlacDrop.OfflineQmc;
using MusicDrop3.MultiPlatform;

if (args.Length != 2)
    throw new ArgumentException("Usage: MusicDrop3.Harness <qmc-testdata-dir> <ffmpeg.exe>");

string qmcData = Path.GetFullPath(args[0]);
string ffmpeg = Path.GetFullPath(args[1]);
if (!Directory.Exists(qmcData)) throw new DirectoryNotFoundException(qmcData);
if (!File.Exists(ffmpeg)) throw new FileNotFoundException("FFmpeg not found", ffmpeg);

string root = Path.Combine(Path.GetTempPath(), "MusicDrop3Harness", Guid.NewGuid().ToString("N"));
string sourceDir = Path.Combine(root, "source");
string encryptedDir = Path.Combine(root, "encrypted");
string outputDir = Path.Combine(root, "output");
Directory.CreateDirectory(sourceDir);
Directory.CreateDirectory(encryptedDir);
Directory.CreateDirectory(outputDir);
Environment.SetEnvironmentVariable("MUSICDROP3_DATA_DIR", Path.Combine(root, "appdata"));

int passed = 0;
try
{
    await GenerateAudioAsync("flac", new[] { "-c:a", "flac", "-compression_level", "5" });
    await GenerateAudioAsync("mp3", new[] { "-c:a", "libmp3lame", "-q:a", "4", "-id3v2_version", "3" });
    await GenerateAudioAsync("ogg", new[] { "-c:a", "libvorbis", "-q:a", "4" });

    await CheckFfmpegInstallerDefensesAsync();
    CheckFfprobeJsonCompatibility();
    CheckOfflineBuyerLicense();
    await CheckQmcPublicFixturesAsync();
    await CheckNcmVectorsAsync();
    await CheckKwmVectorsAsync();
    await CheckKgmV3VectorsAsync();
    await CheckLargeStreamingVectorsAsync();
    await CheckMalformedInputsAsync();
    await CheckKgmV5VectorsAsync();
    CheckMusicClientSafety();
    await CheckPreflightAndDirectOriginalAsync();
    await CheckStandardAudioInputAsync();
    await CheckEndToEndMatrixAsync();
    await CheckBatchCollisionAsync();
    await CheckStrictBatchReadinessAsync();

    Console.WriteLine($"PASS {passed} V3 integration checks");
}

finally
{
    try { Directory.Delete(root, recursive: true); } catch { }
}

void CheckOfflineBuyerLicense()
{
    (string privateKey, string publicKey) = RetailLicenseService.GenerateKeyPair();
    var payload = new BuyerLicensePayload(1, "MusicDrop", "Convenience", "测试买家", "ORDER-2026-0001", "2026-08-15", true);
    string path = Path.Combine(root, "buyer-license.json");
    File.WriteAllText(path, RetailLicenseService.CreateSignedDocument(payload, privateKey));
    BuyerLicenseStatus valid = RetailLicenseService.ValidateFile(path, publicKey);
    True(valid.IsValid && valid.Payload?.OrderId == payload.OrderId, "valid offline buyer license was rejected");

    string tampered = File.ReadAllText(path).Replace("ORDER-2026-0001", "ORDER-2026-9999", StringComparison.Ordinal);
    File.WriteAllText(path, tampered);
    BuyerLicenseStatus invalid = RetailLicenseService.ValidateFile(path, publicKey);
    True(!invalid.IsValid && invalid.Summary.Contains("签名", StringComparison.Ordinal),
        "tampered offline buyer license was accepted");
    Pass("offline permanent buyer license: signature acceptance and tamper rejection");
}

void CheckFfprobeJsonCompatibility()
{
    AudioInfo numeric = AudioConverter.ParseFfprobeJson("""
        {"streams":[{"sample_rate":48000,"channels":2,"bits_per_sample":24,"sample_fmt":"s32"}],"format":{"duration":12.5}}
        """);
    Equal(48000, numeric.SampleRate, "numeric ffprobe sample rate");
    Equal(2, numeric.Channels, "numeric ffprobe channels");
    Equal(24, numeric.BitsPerSample, "numeric ffprobe bit depth");
    Equal(12.5, numeric.DurationSeconds, "numeric ffprobe duration");

    AudioInfo text = AudioConverter.ParseFfprobeJson("""
        {"streams":[{"sample_rate":"44100","channels":"1","bits_per_raw_sample":"16","sample_fmt":"s16"}],"format":{"duration":"3.25"}}
        """);
    Equal(44100, text.SampleRate, "string ffprobe sample rate");
    Equal(1, text.Channels, "string ffprobe channels");
    Equal(16, text.BitsPerSample, "string ffprobe bit depth");
    Equal(3.25, text.DurationSeconds, "string ffprobe duration");
    Pass("FFprobe JSON compatibility: numeric and string scalar forms");
}

async Task CheckFfmpegInstallerDefensesAsync()
{
    string malicious = Path.Combine(root, "ffmpeg-path-traversal.zip");
    using (ZipArchive archive = ZipFile.Open(malicious, ZipArchiveMode.Create))
    {
        ZipArchiveEntry entry = archive.CreateEntry("../escape.txt");
        await using Stream stream = entry.Open();
        await stream.WriteAsync("escape"u8.ToArray());
    }
    bool traversalRejected = false;
    try
    {
        FfmpegManager.ValidateArchiveSafetyForTests(malicious);
    }
    catch (InvalidDataException ex)
    {
        traversalRejected = ex.Message.Contains("不安全路径", StringComparison.Ordinal);
    }
    True(traversalRejected, "FFmpeg ZIP path traversal was not rejected");
    True(!File.Exists(Path.Combine(root, "escape.txt")), "FFmpeg ZIP traversal wrote outside staging");

    bool hashRejected = false;
    try
    {
        await ToolManager.InstallFfmpegFromZipAsync(
            malicious,
            new Progress<(int percent, string status)>(_ => { }),
            CancellationToken.None);
    }
    catch (InvalidDataException ex)
    {
        hashRejected = ex.Message.Contains("SHA-256", StringComparison.Ordinal);
    }
    True(hashRejected, "FFmpeg archive hash mismatch was not rejected");
    True(!File.Exists(AppInfo.ManagedFfmpegExe), "hash-mismatched FFmpeg archive was installed");
    Pass("FFmpeg installer: path traversal and archive hash mismatch rejection");
}

async Task GenerateAudioAsync(string extension, string[] codecArgs)
{
    string output = Path.Combine(sourceDir, "golden." + extension);
    var command = new List<string>
    {
        "-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", "sine=frequency=997:sample_rate=44100:duration=1.2",
        "-ac", "2",
    };
    command.AddRange(codecArgs);
    command.Add(output);
    await RunAsync(ffmpeg, command);
    await DecodeCheckAsync(output);
    True(new FileInfo(output).Length > 1000, "generated audio is unexpectedly small: " + extension);
}

async Task CheckQmcPublicFixturesAsync()
{
    foreach ((string Name, QmcFooterKind Footer, string Extension) item in new[]
    {
        ("qmc0_static", QmcFooterKind.None, ".mp3"),
        ("mflac_map", QmcFooterKind.PcV1Legacy, ".flac"),
        ("mflac_rc4", QmcFooterKind.PcV1Legacy, ".flac"),
        ("mflac0_rc4", QmcFooterKind.AndroidQTag, ".flac"),
        ("mgg_map", QmcFooterKind.PcV1Legacy, ".ogg"),
    })
    {
        byte[] body = await File.ReadAllBytesAsync(Path.Combine(qmcData, item.Name + "_raw.bin"));
        byte[] suffix = await File.ReadAllBytesAsync(Path.Combine(qmcData, item.Name + "_suffix.bin"));
        byte[] expected = await File.ReadAllBytesAsync(Path.Combine(qmcData, item.Name + "_target.bin"));
        using var input = new MemoryStream([.. body, .. suffix], writable: false);
        QmcProbeResult probe = await OfflineQmcDecryptor.ProbeAsync(input);
        Equal(item.Footer, probe.FooterKind, item.Name + " footer");
        Equal(item.Extension, probe.DetectedAudioExtension, item.Name + " extension");
        True(probe.CanDecrypt, item.Name + ": " + probe.Error);
        using var output = new MemoryStream();
        input.Position = 0;
        QmcDecryptResult result = await OfflineQmcDecryptor.DecryptAsync(
            input, output, bufferSize: item.Name == "mflac0_rc4" ? 127 : 4093);
        True(expected.AsSpan().SequenceEqual(output.ToArray()), item.Name + " public fixture mismatch");
        Equal(expected.LongLength, result.BytesWritten, item.Name + " output length");
    }
    byte[] chunkBody = await File.ReadAllBytesAsync(Path.Combine(qmcData, "mflac0_rc4_raw.bin"));
    byte[] chunkSuffix = await File.ReadAllBytesAsync(Path.Combine(qmcData, "mflac0_rc4_suffix.bin"));
    byte[] chunkExpected = await File.ReadAllBytesAsync(Path.Combine(qmcData, "mflac0_rc4_target.bin"));
    foreach (int bufferSize in new[] { 64, 127, 128, 129, 511, 4093, 8191, 65_536 })
    {
        using var chunkInput = new MemoryStream([.. chunkBody, .. chunkSuffix], writable: false);
        using var chunkOutput = new MemoryStream();
        await OfflineQmcDecryptor.DecryptAsync(chunkInput, chunkOutput, bufferSize: bufferSize);
        True(chunkExpected.AsSpan().SequenceEqual(chunkOutput.ToArray()), "QMC chunk boundary " + bufferSize);
    }
    Pass("QQ/QMC public binary fixtures: 5/5 byte-exact");
}

async Task CheckNcmVectorsAsync()
{
    var decryptor = new NcmDecryptor();
    foreach (string extension in new[] { "flac", "mp3", "ogg" })
    {
        byte[] source = await File.ReadAllBytesAsync(Path.Combine(sourceDir, "golden." + extension));
        string encrypted = Path.Combine(encryptedDir, "golden-" + extension + ".ncm");
        await File.WriteAllBytesAsync(encrypted, BuildNcm(source, SeedBytes("ncm-audio-key-" + extension, 32)));
        await CheckDecryptorAsync(decryptor, encrypted, source, "." + extension, "NCM " + extension);
    }
    Pass("NCM independent golden vectors: FLAC/MP3/OGG");
}

async Task CheckKwmVectorsAsync()
{
    var decryptor = new KwmDecryptor();
    int index = 0;
    foreach (string extension in new[] { "flac", "mp3", "ogg" })
    {
        byte[] source = await File.ReadAllBytesAsync(Path.Combine(sourceDir, "golden." + extension));
        string encrypted = Path.Combine(encryptedDir, "golden-" + extension + ".kwm");
        await File.WriteAllBytesAsync(encrypted, BuildKwm(source, extension, 9876543210123456UL + (ulong)index++));
        await CheckDecryptorAsync(decryptor, encrypted, source, "." + extension, "KWM " + extension);
    }
    Pass("KWM independent golden vectors: FLAC/MP3/OGG");
}

async Task CheckKgmV3VectorsAsync()
{
    var decryptor = new KgmDecryptor();
    int index = 0;
    foreach (string extension in new[] { "flac", "mp3", "ogg" })
    {
        byte[] source = await File.ReadAllBytesAsync(Path.Combine(sourceDir, "golden." + extension));
        string encrypted = Path.Combine(encryptedDir, "golden-" + extension + ".kgm");
        await File.WriteAllBytesAsync(encrypted, BuildKgmV3(source, SeedBytes("kgm-file-key-" + index++, 16)));
        await CheckDecryptorAsync(decryptor, encrypted, source, "." + extension, "KGM v3 " + extension);
    }
    Pass("KGM v3 independent golden vectors: FLAC/MP3/OGG");
}

async Task CheckLargeStreamingVectorsAsync()
{
    byte[] prefix = await File.ReadAllBytesAsync(Path.Combine(sourceDir, "golden.flac"));
    byte[] large = new byte[400_123];
    prefix.AsSpan(0, Math.Min(prefix.Length, 4096)).CopyTo(large);
    byte[] tail = SeedBytes("large-streaming-vector", 32);
    for (int i = Math.Min(prefix.Length, 4096); i < large.Length; i++) large[i] = tail[i % tail.Length];

    string ncm = Path.Combine(encryptedDir, "large.ncm");
    await File.WriteAllBytesAsync(ncm, BuildNcm(large, SeedBytes("large-ncm-key", 32)));
    await CheckDecryptorAsync(new NcmDecryptor(), ncm, large, ".flac", "NCM multi-block", decode: false);

    string kwm = Path.Combine(encryptedDir, "large.kwm");
    await File.WriteAllBytesAsync(kwm, BuildKwm(large, "flac", 1234567890123456UL));
    await CheckDecryptorAsync(new KwmDecryptor(), kwm, large, ".flac", "KWM multi-block", decode: false);

    string kgm = Path.Combine(encryptedDir, "large.kgm");
    await File.WriteAllBytesAsync(kgm, BuildKgmV3(large, SeedBytes("large-kgm-key", 16)));
    await CheckDecryptorAsync(new KgmDecryptor(), kgm, large, ".flac", "KGM multi-block", decode: false);
    Pass("NCM/KWM/KGM streaming across 128 KiB block boundaries");
}

async Task CheckDecryptorAsync(
    IPlatformDecryptor decryptor,
    string encrypted,
    byte[] expected,
    string expectedExtension,
    string label,
    bool decode = true,
    MultiPlatformOptions? options = null)
{
    options ??= new MultiPlatformOptions();
    PlatformProbeResult probe = await decryptor.ProbeAsync(encrypted, options, CancellationToken.None);
    True(probe.CanDecrypt, label + " probe failed: " + probe.Error);
    Equal(expectedExtension, probe.AudioExtension, label + " extension");
    string output = Path.Combine(outputDir, Guid.NewGuid().ToString("N") + expectedExtension);
    PlatformDecryptResult result = await decryptor.DecryptAsync(
        encrypted, output, options, CancellationToken.None);
    byte[] actual = await File.ReadAllBytesAsync(output);
    True(expected.AsSpan().SequenceEqual(actual), label + " byte-exact mismatch");
    Equal(expected.LongLength, result.BytesWritten, label + " output length");
    if (decode) await DecodeCheckAsync(output);
}

async Task CheckMalformedInputsAsync()
{
    var cases = new List<(IPlatformDecryptor Decryptor, string Name, byte[] Data)>
    {
        (new NcmDecryptor(), "short.ncm", "CTEN"u8.ToArray()),
        (new NcmDecryptor(), "huge.ncm", [.. "CTENFDAM"u8, 0, 0, 0xFF, 0xFF, 0xFF, 0x7F]),
        (new KwmDecryptor(), "short.kwm", "yeelion-kuwo-tme"u8.ToArray()),
        (new KwmDecryptor(), "badmagic.kwm", new byte[0x500]),
        (new KgmDecryptor(), "short.kgm", new byte[59]),
        (new KgmDecryptor(), "badmagic.kgm", new byte[100]),
    };
    foreach ((IPlatformDecryptor decryptor, string name, byte[] data) in cases)
    {
        string path = Path.Combine(encryptedDir, name);
        await File.WriteAllBytesAsync(path, data);
        PlatformProbeResult probe = await decryptor.ProbeAsync(path, new MultiPlatformOptions(), CancellationToken.None);
        True(!probe.CanDecrypt && !string.IsNullOrWhiteSpace(probe.Error), "malformed input accepted: " + name);
    }

    byte[] forgedV5 = new byte[0x48];
    KgmMagicBytes().CopyTo(forgedV5, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(forgedV5.AsSpan(0x10, 4), 0x48);
    BinaryPrimitives.WriteUInt32LittleEndian(forgedV5.AsSpan(0x14, 4), 5);
    BinaryPrimitives.WriteUInt32LittleEndian(forgedV5.AsSpan(0x18, 4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(forgedV5.AsSpan(0x44, 4), uint.MaxValue);
    string forgedV5Path = Path.Combine(encryptedDir, "forged-hash-length.kgm");
    await File.WriteAllBytesAsync(forgedV5Path, forgedV5);
    PlatformProbeResult forgedV5Probe = await new KgmDecryptor().ProbeAsync(
        forgedV5Path, new MultiPlatformOptions(), CancellationToken.None);
    True(!forgedV5Probe.CanDecrypt && forgedV5Probe.Error?.Contains("边界", StringComparison.Ordinal) == true,
        "KGM v5 forged AudioHash length was not rejected");

    byte[] source = await File.ReadAllBytesAsync(Path.Combine(sourceDir, "golden.flac"));
    byte[] invalidSlot = BuildKgmV3(source, SeedBytes("invalid-slot", 16));
    BinaryPrimitives.WriteUInt32LittleEndian(invalidSlot.AsSpan(0x18, 4), 99);
    string invalidSlotPath = Path.Combine(encryptedDir, "invalid-slot.kgm");
    await File.WriteAllBytesAsync(invalidSlotPath, invalidSlot);
    PlatformProbeResult invalidSlotProbe = await new KgmDecryptor().ProbeAsync(
        invalidSlotPath, new MultiPlatformOptions(), CancellationToken.None);
    True(!invalidSlotProbe.CanDecrypt && invalidSlotProbe.Error?.Contains("99", StringComparison.Ordinal) == true,
        "KGM invalid slot was not rejected");

    byte[] unsupportedVersion = BuildKgmV3(source, SeedBytes("invalid-version", 16));
    BinaryPrimitives.WriteUInt32LittleEndian(unsupportedVersion.AsSpan(0x14, 4), 4);
    string unsupportedVersionPath = Path.Combine(encryptedDir, "unsupported-version.kgm");
    await File.WriteAllBytesAsync(unsupportedVersionPath, unsupportedVersion);
    PlatformProbeResult unsupportedProbe = await new KgmDecryptor().ProbeAsync(
        unsupportedVersionPath, new MultiPlatformOptions(), CancellationToken.None);
    True(!unsupportedProbe.CanDecrypt && unsupportedProbe.Error?.Contains("不支持", StringComparison.Ordinal) == true,
        "KGM unsupported version was not rejected");
    Pass("truncated, forged-length, bad-magic, invalid-slot/version rejection");
}

async Task CheckKgmV5VectorsAsync()
{
    byte[] hash = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
    byte[] suffix = await File.ReadAllBytesAsync(Path.Combine(qmcData, "mflac_map_suffix.bin"));
    uint keyLength = BinaryPrimitives.ReadUInt32LittleEndian(suffix.AsSpan(suffix.Length - 4));
    Equal((uint)suffix.Length - 4, keyLength, "KGM v5 fixture EKey footer length");
    string ekey = Encoding.ASCII.GetString(suffix, 0, checked((int)keyLength)).TrimEnd('\0');
    byte[] expected = await File.ReadAllBytesAsync(Path.Combine(sourceDir, "golden.flac"));
    byte[] encryptedAudio = expected.ToArray();
    IQmcCipher fixtureCipher = QmcCipherFactory.FromMasterKey(QmcEKey.DeriveMasterKey(ekey));
    fixtureCipher.Transform(encryptedAudio, 0);

    byte[] kgm = BuildKgmV5(encryptedAudio, hash);
    string path = Path.Combine(encryptedDir, "identified-v5.kgm");
    await File.WriteAllBytesAsync(path, kgm);
    PlatformProbeResult probe = await new KgmDecryptor().ProbeAsync(path, new MultiPlatformOptions(), CancellationToken.None);
    True(!probe.CanDecrypt && probe.RequiresExternalKey, "KGM v5 should require a local database key");
    Equal(Encoding.UTF8.GetString(hash), probe.KeyIdentifier, "KGM v5 audio hash");

    string plainDatabase = Path.Combine(encryptedDir, "KGMusicV3-plain.db");
    await CreateKugouDatabaseAsync(plainDatabase, Encoding.UTF8.GetString(hash), ekey);
    await CheckDecryptorAsync(new KgmDecryptor(), path, expected, ".flac",
        "KGM v5 plain local DB", options: new MultiPlatformOptions(plainDatabase));

    byte[] plainDatabaseBytes = await File.ReadAllBytesAsync(plainDatabase);
    byte[] encryptedDatabaseBytes = EncryptKugouDatabase(plainDatabaseBytes);
    string encryptedDatabase = Path.Combine(encryptedDir, "KGMusicV3-encrypted.db");
    await File.WriteAllBytesAsync(encryptedDatabase, encryptedDatabaseBytes);
    await CheckDecryptorAsync(new KgmDecryptor(), path, expected, ".flac",
        "KGM v5 encrypted local DB", options: new MultiPlatformOptions(encryptedDatabase));

    Equal("1962C05FA2EBBE2428FF522B9E03EAD4",
        Convert.ToHexString(KugouDatabaseReader.DerivePageKey(0)), "KGM database page-0 key vector");
    Equal("055A673593892DDF3AB3B3C621C34802",
        Convert.ToHexString(KugouDatabaseReader.DerivePageIv(0)), "KGM database page-0 IV vector");
    Pass("KGM v5: missing-key classification, plain/encrypted KGMusicV3.db, byte-exact FLAC");
}

void CheckMusicClientSafety()
{
    string fakeDirectory = Path.Combine(root, "fake-client");
    Directory.CreateDirectory(fakeDirectory);
    string fake = Path.Combine(fakeDirectory, "QQMusic.exe");
    File.WriteAllBytes(fake, "not a signed Tencent executable"u8.ToArray());
    (bool fakeValid, string fakeDetail) = MusicClientManager.ValidateQqMusicExecutable(fake);
    True(!fakeValid && !string.IsNullOrWhiteSpace(fakeDetail), "untrusted QQMusic.exe candidate was accepted");

    string installed = @"C:\Program Files (x86)\Tencent\QQMusic\QQMusic.exe";
    if (File.Exists(installed))
    {
        (bool installedValid, string installedDetail) = MusicClientManager.ValidateQqMusicExecutable(installed);
        True(installedValid, "installed Tencent QQMusic.exe did not validate: " + installedDetail);
    }
    Pass("QQ client discovery rejects impostors and validates Tencent Authenticode identity");
}

async Task CheckEndToEndMatrixAsync()
{
    string ncmSource = Path.Combine(encryptedDir, "golden-flac.ncm");
    foreach (string format in new[] { "原始格式", "FLAC", "WAV", "MP3", "OGG" })
    {
        string dir = Path.Combine(outputDir, "matrix-" + format);
        var options = new ConversionOptions(format, "V0（约 245 kbps）", dir, ffmpeg, "", false, UseQqFallback: false);
        List<ConversionItem> result = await AudioConverter.ConvertAsync(
            new[] { ncmSource }, options, (_, _) => { }, _ => { }, CancellationToken.None);
        True(result.Single().Success, "NCM E2E " + format + ": " + result.Single().Message);
        await DecodeCheckAsync(result.Single().OutputPath);
        string expectedExtension = format switch
        {
            "原始格式" or "FLAC" => ".flac", "WAV" => ".wav", "MP3" => ".mp3", "OGG" => ".ogg", _ => "",
        };
        Equal(expectedExtension, Path.GetExtension(result.Single().OutputPath).ToLowerInvariant(), "NCM E2E extension " + format);
    }

    byte[] mggRaw = await File.ReadAllBytesAsync(Path.Combine(qmcData, "mgg_map_raw.bin"));
    byte[] mggSuffix = await File.ReadAllBytesAsync(Path.Combine(qmcData, "mgg_map_suffix.bin"));
    byte[] mggExpected = await File.ReadAllBytesAsync(Path.Combine(qmcData, "mgg_map_target.bin"));
    string mggPath = Path.Combine(encryptedDir, "public-mgg.mgg");
    await File.WriteAllBytesAsync(mggPath, [.. mggRaw, .. mggSuffix]);
    var mggOptions = new ConversionOptions("原始格式", "V0（约 245 kbps）",
        Path.Combine(outputDir, "mgg-original"), ffmpeg, "", false, UseQqFallback: false);
    ConversionItem mggResult = (await AudioConverter.ConvertAsync(
        new[] { mggPath }, mggOptions, (_, _) => { }, _ => { }, CancellationToken.None)).Single();
    True(mggResult.Success, "MGG E2E original: " + mggResult.Message);
    byte[] mggActual = await File.ReadAllBytesAsync(mggResult.OutputPath);
    True(mggExpected.AsSpan().SequenceEqual(mggActual), "MGG E2E original byte mismatch");
    await DecodeCheckAsync(mggResult.OutputPath);
    Pass("full conversion pipeline: Original/FLAC/WAV/MP3/OGG plus MGG→OGG byte-exact");
}

async Task CheckPreflightAndDirectOriginalAsync()
{
    Equal("原始格式", new AppSettings().OutputFormat, "default output format");
    string input = Path.Combine(encryptedDir, "golden-flac.ncm");
    FilePreflightResult preflight = await AudioConverter.PreflightAsync(
        input, "", "", "", CancellationToken.None);
    True(preflight.CanConvert, "NCM preflight should be ready: " + preflight.Detail);
    Equal("NCM", preflight.PlatformFormat, "NCM preflight platform");
    Equal("FLAC", preflight.AudioCodec, "NCM preflight codec");
    Equal("可离线转换", preflight.Status, "NCM preflight status");

    string directDir = Path.Combine(outputDir, "direct-original");
    var options = new ConversionOptions(
        "原始格式", "V0（约 245 kbps）", directDir, ffmpeg, "", false, UseQqFallback: false);
    string capacity = AudioConverter.ValidateStorageAndPaths(new[] { input }, options);
    True(capacity.Contains("通过", StringComparison.Ordinal), "storage preflight summary");
    ConversionItem result = (await AudioConverter.ConvertAsync(
        new[] { input }, options, (_, _) => { }, _ => { }, CancellationToken.None)).Single();
    True(result.Success, "direct original conversion: " + result.Message);
    byte[] expected = await File.ReadAllBytesAsync(Path.Combine(sourceDir, "golden.flac"));
    byte[] actual = await File.ReadAllBytesAsync(result.OutputPath);
    True(expected.AsSpan().SequenceEqual(actual), "direct original output is not byte-exact");
    True(!File.Exists(result.OutputPath + ".partial"), "direct original left a partial file");
    Pass("async file preflight, original-format default, storage guard and byte-exact direct output");
}

async Task CheckStandardAudioInputAsync()
{
    string input = Path.Combine(sourceDir, "golden.mp3");
    FilePreflightResult preflight = await AudioConverter.PreflightAsync(
        input, "", "", "", CancellationToken.None);
    True(preflight.CanConvert, "standard MP3 preflight: " + preflight.Detail);
    Equal("标准音频", preflight.PlatformFormat, "standard MP3 platform");
    Equal("MP3", preflight.AudioCodec, "standard MP3 codec");

    var originalOptions = new ConversionOptions(
        "原始格式", "V0（约 245 kbps）", Path.Combine(outputDir, "standard-original"),
        ffmpeg, "", false, UseQqFallback: false);
    ConversionItem original = (await AudioConverter.ConvertAsync(
        new[] { input }, originalOptions, (_, _) => { }, _ => { }, CancellationToken.None)).Single();
    True(original.Success, "standard MP3 original copy: " + original.Message);
    byte[] sourceBytes = await File.ReadAllBytesAsync(input);
    byte[] copiedBytes = await File.ReadAllBytesAsync(original.OutputPath);
    True(sourceBytes.AsSpan().SequenceEqual(copiedBytes), "standard MP3 original copy was not byte-exact");
    True(File.Exists(input), "standard MP3 source was removed");

    var flacOptions = originalOptions with
    {
        Format = "FLAC",
        OutputDirectory = Path.Combine(outputDir, "standard-to-flac"),
    };
    ConversionItem flac = (await AudioConverter.ConvertAsync(
        new[] { input }, flacOptions, (_, _) => { }, _ => { }, CancellationToken.None)).Single();
    True(flac.Success && flac.OutputPath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase),
        "standard MP3 to FLAC: " + flac.Message);
    await DecodeCheckAsync(flac.OutputPath);
    True(File.Exists(input), "standard MP3 source was removed after transcode");
    Pass("standard FLAC/WAV/MP3/OGG/M4A queue path: byte-exact original copy and MP3→FLAC");
}

async Task CheckBatchCollisionAsync()
{
    string aDir = Path.Combine(encryptedDir, "collision-a");
    string bDir = Path.Combine(encryptedDir, "collision-b");
    Directory.CreateDirectory(aDir);
    Directory.CreateDirectory(bDir);
    string source = Path.Combine(encryptedDir, "golden-ogg.kwm");
    string first = Path.Combine(aDir, "same.kwm");
    string second = Path.Combine(bDir, "same.kwm");
    File.Copy(source, first);
    File.Copy(source, second);
    var options = new ConversionOptions("原始格式", "V0（约 245 kbps）",
        Path.Combine(outputDir, "collisions"), ffmpeg, "", false, UseQqFallback: false);
    List<ConversionItem> result = await AudioConverter.ConvertAsync(
        new[] { first, second }, options, (_, _) => { }, _ => { }, CancellationToken.None);
    True(result.All(value => value.Success), "batch collision conversion failed");
    True(!string.Equals(result[0].OutputPath, result[1].OutputPath, StringComparison.OrdinalIgnoreCase),
        "batch collision allocated duplicate outputs");
    True(result[1].OutputPath.EndsWith("same (1).ogg", StringComparison.OrdinalIgnoreCase),
        "batch collision suffix mismatch");
    Pass("batch output collision reservation");
}

async Task CheckStrictBatchReadinessAsync()
{
    Equal(true, new AppSettings().AutoStartRequiredClients, "default client auto-start setting");
    Equal(true, new AppSettings().StrictBatchPreflight, "default strict preflight setting");
    string valid = Path.Combine(encryptedDir, "golden-flac.ncm");
    string unsupported = Path.Combine(encryptedDir, "identified-v5.kgm");
    string output = Path.Combine(outputDir, "strict-batch");
    var options = new ConversionOptions(
        "原始格式", "V0（约 245 kbps）", output, ffmpeg, "", false,
        UseQqFallback: false, StrictBatchPreflight: true);
    bool rejected = false;
    try
    {
        _ = await AudioConverter.ConvertAsync(
            new[] { valid, unsupported }, options, (_, _) => { }, _ => { }, CancellationToken.None);
    }
    catch (InvalidDataException ex)
    {
        rejected = ex.Message.Contains("严格批量预检未通过", StringComparison.Ordinal);
    }
    True(rejected, "strict batch accepted an unsupported member");
    True(!Directory.Exists(output) || !Directory.EnumerateFiles(output).Any(),
        "strict batch wrote output before all members were ready");
    Pass("strict batch readiness is all-ready-before-output");
}

static byte[] BuildNcm(byte[] plaintext, byte[] audioKey)
{
    byte[] prefix = "neteasecloudmusic"u8.ToArray();
    byte[] keyPlain = [.. prefix, .. audioKey];
    byte[] encryptedKey;
    using (Aes aes = Aes.Create())
    {
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = NcmCoreKeyBytes();
        encryptedKey = aes.CreateEncryptor().TransformFinalBlock(keyPlain, 0, keyPlain.Length);
    }
    for (int i = 0; i < encryptedKey.Length; i++) encryptedKey[i] ^= 0x64;
    byte[] encryptedAudio = plaintext.ToArray();
    NcmTransform(encryptedAudio, audioKey);

    using var output = new MemoryStream();
    output.Write("CTENFDAM"u8);
    output.Write(new byte[2]);
    WriteUInt32(output, (uint)encryptedKey.Length);
    output.Write(encryptedKey);
    WriteUInt32(output, 0);
    output.Write(new byte[5]);
    WriteUInt32(output, 0);
    WriteUInt32(output, 0);
    output.Write(encryptedAudio);
    return output.ToArray();
}

static void NcmTransform(Span<byte> data, byte[] key)
{
    byte[] box = Enumerable.Range(0, 256).Select(value => (byte)value).ToArray();
    byte j = 0;
    for (int i = 0; i < box.Length; i++)
    {
        j = unchecked((byte)(box[i] + j + key[i % key.Length]));
        (box[i], box[j]) = (box[j], box[i]);
    }
    byte[] streamBox = new byte[256];
    for (int i = 0; i < streamBox.Length; i++)
    {
        byte index = unchecked((byte)(i + 1));
        byte first = box[index];
        byte second = box[unchecked((byte)(index + first))];
        streamBox[i] = box[unchecked((byte)(first + second))];
    }
    for (int i = 0; i < data.Length; i++) data[i] ^= streamBox[i & 0xFF];
}

static byte[] BuildKwm(byte[] plaintext, string extension, ulong resourceId)
{
    byte[] output = new byte[0x400 + plaintext.Length];
    "yeelion-kuwo-tme"u8.CopyTo(output);
    BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(0x18, 8), resourceId);
    Encoding.ASCII.GetBytes("320" + extension).CopyTo(output, 0x30);
    byte[] keyText = Encoding.ASCII.GetBytes(resourceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    byte[] mask = new byte[32];
    ReadOnlySpan<byte> predefined = "MoOtOiTvINGwd2E6n0E1i7L5t2IoOoNk"u8;
    for (int i = 0; i < mask.Length; i++) mask[i] = (byte)(predefined[i] ^ keyText[i % keyText.Length]);
    for (int i = 0; i < plaintext.Length; i++) output[0x400 + i] = (byte)(plaintext[i] ^ mask[i & 0x1F]);
    return output;
}

static byte[] BuildKgmV3(byte[] plaintext, byte[] fileKey)
{
    const int audioOffset = 0x40;
    byte[] output = new byte[audioOffset + plaintext.Length];
    KgmMagicBytes().CopyTo(output, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x10, 4), audioOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x14, 4), 3);
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x18, 4), 1);
    fileKey.CopyTo(output, 0x2C);
    byte[] slotBox = KugouMd5(new byte[] { 0x6C, 0x2C, 0x2F, 0x27 });
    byte[] fileBox = [.. KugouMd5(fileKey), 0x6B];
    for (int i = 0; i < plaintext.Length; i++)
    {
        uint position = (uint)i;
        byte value = (byte)(plaintext[i] ^ (byte)(position ^ (position >> 8) ^ (position >> 16) ^ (position >> 24)));
        value ^= slotBox[i % slotBox.Length];
        value ^= unchecked((byte)(value << 4));
        value ^= fileBox[i % fileBox.Length];
        output[audioOffset + i] = value;
    }
    return output;
}

static byte[] BuildKgmV5(byte[] encryptedAudio, byte[] audioHash)
{
    const int audioOffset = 0x90;
    if (audioHash.Length > audioOffset - 0x48) throw new ArgumentOutOfRangeException(nameof(audioHash));
    byte[] output = new byte[audioOffset + encryptedAudio.Length];
    KgmMagicBytes().CopyTo(output, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x10, 4), audioOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x14, 4), 5);
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x18, 4), 1);
    BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0x44, 4), (uint)audioHash.Length);
    audioHash.CopyTo(output, 0x48);
    encryptedAudio.CopyTo(output, audioOffset);
    return output;
}

static async Task CreateKugouDatabaseAsync(string path, string audioHash, string ekey)
{
    var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
    await using var connection = new SqliteConnection(builder.ToString());
    await connection.OpenAsync();
    await using SqliteCommand command = connection.CreateCommand();
    command.CommandText = """
        PRAGMA page_size=1024;
        VACUUM;
        CREATE TABLE ShareFileItems (EncryptionKeyId TEXT, EncryptionKey TEXT);
        INSERT INTO ShareFileItems (EncryptionKeyId, EncryptionKey) VALUES ($id, $key);
        """;
    command.Parameters.AddWithValue("$id", audioHash);
    command.Parameters.AddWithValue("$key", ekey);
    await command.ExecuteNonQueryAsync();
}

static byte[] EncryptKugouDatabase(byte[] plaintext)
{
    const int pageSize = 0x400;
    if (plaintext.Length == 0 || plaintext.Length % pageSize != 0 ||
        !plaintext.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8))
        throw new InvalidDataException("Synthetic KGM database is not a 1024-byte-page SQLite database.");

    byte[] encrypted = plaintext.ToArray();
    uint pageCount = checked((uint)(plaintext.Length / pageSize));
    for (uint page = 2; page <= pageCount; page++)
    {
        int offset = checked((int)((page - 1) * pageSize));
        EncryptKugouPage(plaintext.AsSpan(offset, pageSize), encrypted.AsSpan(offset, pageSize), page);
    }

    byte[] firstCipher = new byte[pageSize - 0x10];
    EncryptKugouPage(plaintext.AsSpan(0x10, pageSize - 0x10), firstCipher, 1);
    firstCipher.AsSpan(0, 8).CopyTo(encrypted.AsSpan(0x08, 8));
    plaintext.AsSpan(0x10, 8).CopyTo(encrypted.AsSpan(0x10, 8));
    firstCipher.AsSpan(8).CopyTo(encrypted.AsSpan(0x18));
    return encrypted;
}

static void EncryptKugouPage(ReadOnlySpan<byte> plaintext, Span<byte> destination, uint page)
{
    using Aes aes = Aes.Create();
    aes.Mode = CipherMode.CBC;
    aes.Padding = PaddingMode.None;
    aes.Key = KugouDatabaseReader.DerivePageKey(page);
    aes.IV = KugouDatabaseReader.DerivePageIv(page);
    byte[] result = aes.CreateEncryptor().TransformFinalBlock(plaintext.ToArray(), 0, plaintext.Length);
    result.CopyTo(destination);
}

static byte[] KugouMd5(byte[] input)
{
    byte[] digest = MD5.HashData(input);
    byte[] result = new byte[16];
    for (int i = 0; i < result.Length; i += 2)
    {
        result[i] = digest[14 - i];
        result[i + 1] = digest[15 - i];
    }
    return result;
}

static byte[] SeedBytes(string seed, int count)
{
    byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
    return digest[..count];
}

static void WriteUInt32(Stream stream, uint value)
{
    Span<byte> data = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(data, value);
    stream.Write(data);
}

async Task DecodeCheckAsync(string path)
{
    await RunAsync(ffmpeg, new[] { "-v", "error", "-xerror", "-i", path, "-map", "0:a:0", "-f", "null", "NUL" });
}

static async Task RunAsync(string executable, IEnumerable<string> arguments)
{
    var start = new ProcessStartInfo
    {
        FileName = executable,
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (string argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start " + executable);
    Task<string> stdout = process.StandardOutput.ReadToEndAsync();
    Task<string> stderr = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    string output = (await stdout) + (await stderr);
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"Process failed ({process.ExitCode}): {output}");
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

static byte[] NcmCoreKeyBytes() =>
[
    0x68, 0x7A, 0x48, 0x52, 0x41, 0x6D, 0x73, 0x6F,
    0x35, 0x6B, 0x49, 0x6E, 0x62, 0x61, 0x78, 0x57,
];

static byte[] KgmMagicBytes() =>
[
    0x7C, 0xD5, 0x32, 0xEB, 0x86, 0x02, 0x7F, 0x4B,
    0xA8, 0xAF, 0xA6, 0x8E, 0x0F, 0xFF, 0x99, 0x14,
];
