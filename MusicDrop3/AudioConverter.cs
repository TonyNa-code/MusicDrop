using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MFlacDrop.OfflineQmc;
using MusicDrop3.MultiPlatform;

namespace MFlacDrop;

internal sealed record AudioInfo(int SampleRate, int Channels, int BitsPerSample, string SampleFormat, double DurationSeconds);

internal sealed record ConversionOptions(
    string Format,
    string Mp3Quality,
    string OutputDirectory,
    string FfmpegPath,
    string DecryptorPath,
    bool Overwrite,
    string PlayerProcessDbPath = "",
    string ImportedEKeyPath = "",
    bool UseQqFallback = true,
    string KugouDatabasePath = "",
    bool AutoStartRequiredClients = true,
    string QqMusicExecutablePath = "",
    int ClientReadyTimeoutSeconds = 30,
    bool StrictBatchPreflight = true);

internal sealed record ConversionItem(string InputPath, string OutputPath, bool Success, string Message, string Route = "");

internal sealed record FilePreflightResult(
    string PlatformFormat,
    string AudioCodec,
    string Status,
    bool CanConvert,
    string? Detail = null,
    bool RequiresQqCompatibility = false);

internal static class AudioConverter
{
    private static readonly string[] QqExtensions =
    {
        ".mflac0", ".mflac1", ".mflaca", ".mflach", ".mflacl", ".mflacm", ".mflac",
        ".mgg0", ".mgg1", ".mgga", ".mggh", ".mggl", ".mggm", ".mgg",
        ".qmcflac", ".qmcogg", ".qmc0", ".qmc2", ".qmc3", ".qmc4", ".qmc6", ".qmc8",
        ".tkm", ".bkcmp3", ".bkcm4a", ".bkcflac", ".bkcwav", ".bkcape", ".bkcogg", ".bkcwma",
        ".666c6163", ".6d7033", ".6f6767", ".6d3461", ".776176", ".mmp4",
    };

    private static readonly string[] OtherPlatformExtensions =
    {
        ".kgm.flac", ".vpr.flac", ".ncm", ".kwm", ".kw", ".kgm", ".kgma", ".vpr", ".kgg",
        ".tm0", ".tm2", ".tm3", ".tm6", ".xm", ".x2m", ".x3m",
    };

    private static readonly string[] StandardAudioExtensions =
    {
        ".flac", ".wav", ".mp3", ".ogg", ".m4a", ".mp4", ".m4b", ".aac",
        ".opus", ".ape", ".wma", ".aiff", ".aif", ".dsf", ".dff",
    };

    internal static IReadOnlyCollection<string> SupportedInputExtensions { get; } =
        QqExtensions.Concat(OtherPlatformExtensions).Concat(StandardAudioExtensions)
            .OrderByDescending(value => value.Length)
            .ToArray();

    public static void CleanupStaleTempDirectories()
    {
        string parent = Path.Combine(Path.GetTempPath(), "MusicDrop3");
        if (!Directory.Exists(parent)) return;
        foreach (string directory in Directory.EnumerateDirectories(parent))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) continue;
            try
            {
                if (Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                    Directory.Delete(directory, recursive: true);
            }
            catch { }
        }
    }

    public static async Task<FilePreflightResult> PreflightAsync(
        string input,
        string playerProcessDbPath,
        string importedEKeyPath,
        string kugouDatabasePath,
        CancellationToken ct,
        bool useQqFallback = true,
        bool autoStartRequiredClients = true,
        string qqMusicExecutablePath = "",
        string decryptorPath = "")
    {
        try
        {
            if (!File.Exists(input))
                return new("未知", "—", "文件不存在", false);
            if (!IsSupportedInput(input))
                return new("未知", "—", "损坏或不支持", false, "扩展名尚未支持。");

            if (IsStandardAudioInput(input))
            {
                string? extension = await AudioSignatures.DetectFileAsync(input, ct);
                return extension is null
                    ? new("标准音频", "—", "损坏或不支持", false, "文件扩展名受支持，但真实音频头无法识别。")
                    : new("标准音频", FormatCodec(extension), "可直接转码", true);
            }

            if (IsQqInput(input))
            {
                var cache = DpapiEKeyCacheProvider.CreateDefault();
                var providers = new List<IKeyProvider> { cache };
                var disposableProviders = new List<IDisposable>();
                try
                {
                    var options = new ConversionOptions(
                        "原始格式", "", "", "", "", false,
                        playerProcessDbPath, importedEKeyPath, false, kugouDatabasePath);
                    LoadQqKeyProviders(options, providers, disposableProviders, _ => { });
                    QqPreparation preparation = await PrepareQqAsync(
                        input, providers, cache, _ => { }, ct, persistResolvedKey: false);
                    string platform = DescribeQqFormat(preparation.Probe);
                    string codec = FormatCodec(preparation.Probe.DetectedAudioExtension);
                    if (preparation.Probe.CanDecrypt)
                        return new(platform, codec, "可离线转换", true);
                    if (preparation.Probe.RequiresExternalEKey)
                        return DescribeQqCompatibilityPreflight(platform, codec, useQqFallback,
                            autoStartRequiredClients, qqMusicExecutablePath, decryptorPath);
                    return new(platform, codec, "损坏或不支持", false, preparation.Probe.Error);
                }
                finally
                {
                    foreach (IDisposable provider in disposableProviders) provider.Dispose();
                }
            }

            var dispatcher = MultiPlatformDispatcher.CreateDefault();
            (_, PlatformProbeResult probe) = await dispatcher.ProbeAsync(
                input, new MultiPlatformOptions(kugouDatabasePath), ct);
            string detected = FormatCodec(probe.AudioExtension);
            if (probe.CanDecrypt)
                return new(probe.FormatName, detected, "可离线转换", true);
            if (probe.FormatName.Equals("KGM v5", StringComparison.OrdinalIgnoreCase))
                return new(probe.FormatName, detected,
                    probe.RequiresExternalKey ? "需要本地酷狗密钥" : "KGM v5 校验失败",
                    false, probe.Error);
            if (probe.AudioExtension is not null ||
                probe.Error?.Contains("音频头未知", StringComparison.Ordinal) == true)
                return new(probe.FormatName, detected, "格式有效但真实音频头未知", false, probe.Error);
            return new(probe.FormatName, detected, "损坏或不支持", false, probe.Error);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return new("未知", "—", "损坏或不支持", false, ex.Message);
        }
    }

    public static string ValidateStorageAndPaths(IReadOnlyList<string> inputs, ConversionOptions options)
    {
        ValidateFormat(options.Format);
        if (inputs.Count == 0) throw new ArgumentException("没有待转换文件。", nameof(inputs));

        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        if (File.Exists(outputDirectory))
            throw new IOException("输出路径指向文件而不是目录：" + outputDirectory);
        Directory.CreateDirectory(outputDirectory);

        long totalInputBytes = 0;
        foreach (string input in inputs)
        {
            string fullInput = Path.GetFullPath(input);
            if (!File.Exists(fullInput)) throw new FileNotFoundException("输入文件不存在。", fullInput);
            totalInputBytes = checked(totalInputBytes + new FileInfo(fullInput).Length);
        }

        const long safetyMargin = 128L * 1024 * 1024;
        string format = NormalizeOutputFormat(options.Format);
        long outputEstimate = format switch
        {
            "ORIGINAL" => totalInputBytes,
            // Exact WAV capacity is checked after probing the decrypted audio.
            // At this stage reject only volumes that are clearly insufficient.
            "WAV" => totalInputBytes,
            "FLAC" => SaturatingMultiply(totalInputBytes, 4),
            "MP3" or "OGG" => SaturatingMultiply(totalInputBytes, 2),
            _ => totalInputBytes,
        };
        long temporaryEstimate = format == "ORIGINAL" ? 0 : totalInputBytes;

        var requiredByVolume = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        AddVolumeRequirement(requiredByVolume, outputDirectory, outputEstimate);
        if (temporaryEstimate > 0)
            AddVolumeRequirement(requiredByVolume, Path.GetTempPath(), temporaryEstimate);

        var summaries = new List<string>();
        foreach ((string root, long bytes) in requiredByVolume)
        {
            long required = bytes > long.MaxValue - safetyMargin ? long.MaxValue : bytes + safetyMargin;
            var drive = new DriveInfo(root);
            if (!drive.IsReady) throw new IOException("磁盘卷当前不可用：" + root);
            long available = drive.AvailableFreeSpace;
            if (available < required)
                throw new IOException($"磁盘空间不足（{root}）：保守估算需要 {FormatBytes(required)}，可用 {FormatBytes(available)}。请更换输出目录或释放空间。");
            summaries.Add($"{root} 需要约 {FormatBytes(required)} / 可用 {FormatBytes(available)}");
        }
        return "路径与磁盘预检通过：" + string.Join("；", summaries);
    }

    private static async Task ValidateBatchReadinessAsync(
        IReadOnlyList<string> inputs,
        ConversionOptions options,
        IReadOnlyList<IKeyProvider> providers,
        DpapiEKeyCacheProvider cache,
        Action<string> log,
        CancellationToken ct)
    {
        var issues = new List<string>();
        int offlineReady = 0;
        int qqCompatibilityRequired = 0;
        var dispatcher = MultiPlatformDispatcher.CreateDefault();
        var platformOptions = new MultiPlatformOptions(options.KugouDatabasePath);
        string? qqCompatibilityProbeInput = null;

        for (int i = 0; i < inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string input = inputs[i];
            try
            {
                if (!File.Exists(input)) throw new FileNotFoundException("输入文件不存在。", input);
                if (!IsSupportedInput(input)) throw new NotSupportedException("扩展名不在已支持范围内。");

                if (IsStandardAudioInput(input))
                {
                    string? extension = await AudioSignatures.DetectFileAsync(input, ct);
                    if (extension is null)
                        issues.Add($"{Path.GetFileName(input)}：标准音频头无法识别");
                    else
                        offlineReady++;
                }
                else if (IsQqInput(input))
                {
                    QqPreparation preparation = await PrepareQqAsync(
                        input, providers, cache, _ => { }, ct, persistResolvedKey: false);
                    if (preparation.Probe.CanDecrypt) offlineReady++;
                    else if (preparation.Probe.RequiresExternalEKey)
                    {
                        qqCompatibilityRequired++;
                        qqCompatibilityProbeInput ??= input;
                    }
                    else issues.Add($"{Path.GetFileName(input)}：{preparation.Probe.Error ?? "QQ 格式损坏或不支持"}");
                }
                else
                {
                    (_, PlatformProbeResult probe) = await dispatcher.ProbeAsync(input, platformOptions, ct);
                    if (probe.CanDecrypt) offlineReady++;
                    else issues.Add($"{Path.GetFileName(input)}：{probe.Error ?? $"{probe.FormatName} 暂不可转换"}");
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                NotSupportedException or ArgumentException or System.Security.Cryptography.CryptographicException)
            {
                issues.Add($"{Path.GetFileName(input)}：{ex.Message}");
            }
        }

        if (issues.Count > 0)
            throw new InvalidDataException("严格批量预检未通过，尚未生成任何输出：" + Environment.NewLine +
                string.Join(Environment.NewLine, issues.Select(value => "• " + value)));

        if (qqCompatibilityRequired > 0)
        {
            if (!options.UseQqFallback)
                throw new InvalidDataException($"严格批量预检未通过：有 {qqCompatibilityRequired} 个 QQ 文件缺少 EKey，且兼容模式已关闭。尚未生成任何输出。");
            if (!ToolManager.IsTrustedDecryptor(options.DecryptorPath))
                throw new InvalidDataException($"严格批量预检未通过：有 {qqCompatibilityRequired} 个 QQ 文件需要兼容模式，但固定版本兼容组件未安装或校验失败。尚未生成任何输出。");

            await MusicClientManager.EnsureQqMusicReadyAsync(
                options.QqMusicExecutablePath,
                options.AutoStartRequiredClients,
                TimeSpan.FromSeconds(options.ClientReadyTimeoutSeconds),
                log,
                ct);
            await ProbeQqCompatibilityAsync(qqCompatibilityProbeInput!, options, log, ct);
        }

        log($"严格批量预检通过：离线就绪 {offlineReady}，QQ 兼容就绪 {qqCompatibilityRequired}，不支持 0");
    }

    public static async Task<List<ConversionItem>> ConvertAsync(
        IReadOnlyList<string> inputs,
        ConversionOptions options,
        Action<int, string> progress,
        Action<string> log,
        CancellationToken ct)
    {
        ValidateFormat(options.Format);
        log(ValidateStorageAndPaths(inputs, options));
        string root = Path.Combine(Path.GetTempPath(), "MusicDrop3", Guid.NewGuid().ToString("N"));
        string plainDir = Path.Combine(root, "plain");
        string bridgeInputDir = Path.Combine(root, "bridge", "input");
        string bridgeRunDir = Path.Combine(root, "bridge", "run");
        Directory.CreateDirectory(plainDir);
        var results = new ConversionItem?[inputs.Count];
        var reservedOutputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<PendingBridge>();
        var dispatcher = MultiPlatformDispatcher.CreateDefault();
        var platformOptions = new MultiPlatformOptions(options.KugouDatabasePath);
        bool cleanupSafe = true;

        var cache = DpapiEKeyCacheProvider.CreateDefault();
        var providers = new List<IKeyProvider> { cache };
        var disposableProviders = new List<IDisposable>();
        try
        {
            LoadQqKeyProviders(options, providers, disposableProviders, log);

            if (options.StrictBatchPreflight)
            {
                progress(1, "正在执行严格批量预检…");
                await ValidateBatchReadinessAsync(inputs, options, providers, cache, log, ct);
            }

            for (int i = 0; i < inputs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                string input = inputs[i];
                progress(2 + (int)(52.0 * i / Math.Max(1, inputs.Count)),
                    $"识别与解密 {i + 1}/{inputs.Count}：{Path.GetFileName(input)}");
                try
                {
                    if (!File.Exists(input)) throw new FileNotFoundException("输入文件不存在。", input);
                    if (!IsSupportedInput(input)) throw new NotSupportedException("尚未支持此输入扩展名：" + Path.GetExtension(input));

                    if (IsStandardAudioInput(input))
                    {
                        string extension = NormalizeAudioExtension(
                            await AudioSignatures.DetectFileAsync(input, ct));
                        results[i] = await FinishStandardAudioAsync(
                            input, extension, options, reservedOutputs, log, ct);
                    }
                    else if (IsQqInput(input))
                    {
                        QqPreparation preparation = await PrepareQqAsync(input, providers, cache, log, ct);
                        if (!preparation.Probe.CanDecrypt)
                        {
                            if (preparation.Probe.RequiresExternalEKey)
                            {
                                pending.Add(new(i, input));
                                log("QQ 文件缺少 EKey，等待可选兼容模式：" + Path.GetFileName(input));
                            }
                            else
                            {
                                results[i] = new(input, "", false,
                                    preparation.Probe.Error ?? "无法识别或解密此 QQ 音乐文件", "QQ 离线失败");
                            }
                            continue;
                        }

                        string extension = NormalizeAudioExtension(preparation.Probe.DetectedAudioExtension);
                        if (NormalizeOutputFormat(options.Format) == "ORIGINAL")
                        {
                            results[i] = await FinishDirectOriginalAsync(
                                input, extension, preparation.Route, options, reservedOutputs, log,
                                async (partial, token) =>
                                {
                                    QmcDecryptResult decrypted = await OfflineQmcDecryptor.DecryptAsync(
                                        input, partial, preparation.EKey, null, token);
                                    return decrypted.DetectedAudioExtension;
                                }, ct);
                        }
                        else
                        {
                            string plain = Path.Combine(plainDir, $"{i + 1:D4}{extension}");
                            QmcDecryptResult decrypted = await OfflineQmcDecryptor.DecryptAsync(
                                input, plain, preparation.EKey, null, ct);
                            if (!string.Equals(extension, decrypted.DetectedAudioExtension, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("QQ 探测结果与完整解密结果的音频格式不一致。");
                            results[i] = await FinishPlaintextAsync(
                                i, input, plain, extension, preparation.Route, options,
                                reservedOutputs, log, ct);
                        }
                    }
                    else
                    {
                        (IPlatformDecryptor decryptor, PlatformProbeResult probe) =
                            await dispatcher.ProbeAsync(input, platformOptions, ct);
                        if (!probe.CanDecrypt)
                        {
                            string route = probe.RequiresExternalKey ? $"{probe.FormatName} 缺少密钥" : $"{probe.FormatName} 失败";
                            results[i] = new(input, "", false, probe.Error ?? "无法解密此文件。", route);
                            continue;
                        }

                        string extension = NormalizeAudioExtension(probe.AudioExtension);
                        if (NormalizeOutputFormat(options.Format) == "ORIGINAL")
                        {
                            string route = $"离线（{probe.FormatName}）";
                            results[i] = await FinishDirectOriginalAsync(
                                input, extension, route, options, reservedOutputs, log,
                                async (partial, token) =>
                                {
                                    PlatformDecryptResult decrypted = await decryptor.DecryptAsync(
                                        input, partial, platformOptions, token);
                                    return decrypted.AudioExtension;
                                }, ct);
                        }
                        else
                        {
                            string plain = Path.Combine(plainDir, $"{i + 1:D4}{extension}");
                            PlatformDecryptResult decrypted = await decryptor.DecryptAsync(input, plain, platformOptions, ct);
                            if (!string.Equals(extension, decrypted.AudioExtension, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException($"{probe.FormatName} 探测结果与完整解密结果的音频格式不一致。");
                            results[i] = await FinishPlaintextAsync(
                                i, input, plain, extension, decrypted.Route, options,
                                reservedOutputs, log, ct);
                        }
                    }
                }
                catch (ProcessCleanupException) { throw; }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    results[i] = new(input, "", false, ex.Message, "离线失败");
                    log("失败：" + Path.GetFileName(input) + " — " + ex.Message);
                }
            }

            if (pending.Count > 0)
            {
                if (options.UseQqFallback && ToolManager.IsTrustedDecryptor(options.DecryptorPath))
                {
                    try
                    {
                        await MusicClientManager.EnsureQqMusicReadyAsync(
                            options.QqMusicExecutablePath,
                            options.AutoStartRequiredClients,
                            TimeSpan.FromSeconds(options.ClientReadyTimeoutSeconds),
                            log,
                            ct);
                        await RunQqBridgeAsync(pending, results, bridgeInputDir, bridgeRunDir,
                            options, reservedOutputs, progress, log, ct);
                    }
                    catch (MusicClientException ex)
                    {
                        foreach (PendingBridge item in pending)
                            results[item.Index] = new(item.InputPath, "", false, ex.Message, "QQ 客户端未就绪");
                    }
                }
                else
                {
                    string reason = !options.UseQqFallback ? "QQ 兼容模式已关闭" :
                        "缺少已校验的 QQ 兼容组件";
                    foreach (PendingBridge item in pending)
                        results[item.Index] = new(item.InputPath, "", false, reason, "QQ 缺少密钥");
                }
            }

            var final = results.Select((item, index) => item ??
                new ConversionItem(inputs[index], "", false, "未产生结果", "内部错误")).ToList();
            progress(100, $"完成：成功 {final.Count(x => x.Success)}，失败 {final.Count(x => !x.Success)}");
            return final;
        }
        catch (ProcessCleanupException ex) when (!ex.CleanupSafe)
        {
            cleanupSafe = false;
            throw;
        }
        finally
        {
            foreach (IDisposable provider in disposableProviders) provider.Dispose();
            if (cleanupSafe)
                await DeleteDirectoryWithRetryAsync(root, log);
            else
                log("严重警告：无法确认外部进程已停止，临时明文目录不会自动删除：" + root);
        }
    }

    private static void LoadQqKeyProviders(
        ConversionOptions options,
        List<IKeyProvider> providers,
        List<IDisposable> disposableProviders,
        Action<string> log)
    {
        if (!string.IsNullOrWhiteSpace(options.ImportedEKeyPath) && File.Exists(options.ImportedEKeyPath))
        {
            try
            {
                var imported = ImportedEKeyProvider.Load(options.ImportedEKeyPath);
                providers.Add(imported);
                log($"已载入 QQ EKey 文件：{imported.Count} 条");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException)
            {
                log("警告：QQ EKey 文件载入失败，将继续尝试其他离线路径：" + ex.Message);
            }
        }
        if (!string.IsNullOrWhiteSpace(options.PlayerProcessDbPath) && File.Exists(options.PlayerProcessDbPath))
        {
            try
            {
                var database = new PlayerProcessDbKeyProvider(options.PlayerProcessDbPath);
                providers.Add(database);
                disposableProviders.Add(database);
                log("已只读载入 QQ player_process_db");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                log("警告：QQ player_process_db 载入失败，将继续尝试其他离线路径：" + ex.Message);
            }
        }
    }

    private static async Task<QqPreparation> PrepareQqAsync(
        string input,
        IReadOnlyList<IKeyProvider> providers,
        DpapiEKeyCacheProvider cache,
        Action<string> log,
        CancellationToken ct,
        bool persistResolvedKey = true)
    {
        QmcProbeResult selected = await OfflineQmcDecryptor.ProbeAsync(input, null, ct);
        string? ekey = null;
        string route = selected.HasEmbeddedEKey || selected.KeySource == QmcKeySource.StaticV1
            ? "离线（QQ 文件内密钥/旧版静态密钥）"
            : "QQ 离线";

        if (!selected.CanDecrypt && selected.RequiresExternalEKey)
        {
            var request = new KeyLookupRequest(input, selected.MediaFileName, selected.MediaId);
            foreach (IKeyProvider provider in providers)
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<KeyLookupResult> candidates;
                try
                {
                    candidates = await provider.GetKeysAsync(request, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                    InvalidDataException or System.Security.Cryptography.CryptographicException)
                {
                    log($"警告：QQ 密钥来源 {provider.Name} 查询失败：{ex.Message}");
                    continue;
                }

                foreach (KeyLookupResult candidate in candidates)
                {
                    QmcProbeResult verified = await OfflineQmcDecryptor.ProbeAsync(input, candidate.EKey, ct);
                    if (!verified.CanDecrypt)
                    {
                        log($"忽略未通过音频头验证的 QQ 密钥候选：{provider.Name} / {Path.GetFileName(input)}");
                        continue;
                    }
                    ekey = candidate.EKey;
                    selected = verified;
                    route = provider == cache ? "离线（QQ 安全缓存）" :
                        provider is PlayerProcessDbKeyProvider ? "离线（QQ 密钥库）" : "离线（导入 QQ EKey）";
                    if (persistResolvedKey && provider != cache)
                    {
                        try { await cache.StoreAsync(request, ekey, ct); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
                        { log("警告：QQ EKey 安全缓存写入失败：" + ex.Message); }
                    }
                    break;
                }
                if (selected.CanDecrypt) break;
            }
        }
        return new(selected, ekey, route);
    }

    private static async Task<ConversionItem> FinishPlaintextAsync(
        int index,
        string input,
        string plain,
        string sourceExtension,
        string route,
        ConversionOptions options,
        HashSet<string> reservedOutputs,
        Action<string> log,
        CancellationToken ct)
    {
        bool plainCleanupSafe = true;
        try
        {
            string targetExtension = GetTargetExtension(options.Format, sourceExtension);
            string output = AllocateOutputPath(input, targetExtension, options, reservedOutputs);
            string completed = await ConvertOneAsync(plain, sourceExtension, output, options, log, ct);
            log($"{route}：{Path.GetFileName(input)} → {Path.GetExtension(completed)}");
            return new(input, completed, true, "完成", route);
        }
        catch (ProcessCleanupException ex) when (!ex.CleanupSafe)
        {
            plainCleanupSafe = false;
            throw;
        }
        finally
        {
            if (plainCleanupSafe)
                try { if (File.Exists(plain)) File.Delete(plain); } catch { }
        }
    }

    private static async Task<ConversionItem> FinishStandardAudioAsync(
        string input,
        string sourceExtension,
        ConversionOptions options,
        HashSet<string> reservedOutputs,
        Action<string> log,
        CancellationToken ct)
    {
        const string route = "标准音频直接转码";
        if (NormalizeOutputFormat(options.Format) == "ORIGINAL")
        {
            return await FinishDirectOriginalAsync(
                input, sourceExtension, "标准音频安全复制", options, reservedOutputs, log,
                async (partial, token) =>
                {
                    await CopyFileAsync(input, partial, token);
                    return sourceExtension;
                }, ct);
        }

        string targetExtension = GetTargetExtension(options.Format, sourceExtension);
        string output = AllocateOutputPath(input, targetExtension, options, reservedOutputs);
        string completed = await ConvertOneAsync(input, sourceExtension, output, options, log, ct);
        log($"{route}：{Path.GetFileName(input)} → {Path.GetExtension(completed)}");
        return new(input, completed, true, "完成", route);
    }

    private static async Task<ConversionItem> FinishDirectOriginalAsync(
        string input,
        string sourceExtension,
        string route,
        ConversionOptions options,
        HashSet<string> reservedOutputs,
        Action<string> log,
        Func<string, CancellationToken, Task<string>> decryptToPartial,
        CancellationToken ct)
    {
        string output = AllocateOutputPath(input, sourceExtension, options, reservedOutputs);
        string partial = output + ".partial";
        bool cleanupSafe = true;
        try
        {
            string decryptedExtension = NormalizeAudioExtension(await decryptToPartial(partial, ct));
            if (!string.Equals(sourceExtension, decryptedExtension, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("预检结果与完整解密结果的真实音频格式不一致。");
            await VerifyDecodeAsync(options.FfmpegPath, partial, ct);
            File.Move(partial, output, options.Overwrite);
            log($"{route}：{Path.GetFileName(input)} → {Path.GetExtension(output)}（原始编码直出）");
            log("已输出：" + output);
            return new(input, output, true, "完成", route);
        }
        catch (ProcessCleanupException ex) when (!ex.CleanupSafe)
        {
            cleanupSafe = false;
            throw;
        }
        finally
        {
            if (cleanupSafe)
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
        }
    }

    private static async Task RunQqBridgeAsync(
        IReadOnlyList<PendingBridge> pending,
        ConversionItem?[] results,
        string encryptedDir,
        string decryptRunDir,
        ConversionOptions options,
        HashSet<string> reservedOutputs,
        Action<int, string> progress,
        Action<string> log,
        CancellationToken ct)
    {
        Directory.CreateDirectory(encryptedDir);
        Directory.CreateDirectory(decryptRunDir);
        progress(56, $"正在准备 {pending.Count} 个 QQ 兼容模式文件…");
        foreach (PendingBridge item in pending)
        {
            ct.ThrowIfCancellationRequested();
            string suffix = GetMatchedInputSuffix(item.InputPath) ?? Path.GetExtension(item.InputPath).ToLowerInvariant();
            string stagedName = $"{item.Index + 1:D4}{suffix}";
            await CopyFileAsync(item.InputPath, Path.Combine(encryptedDir, stagedName), ct);
        }

        progress(60, "正在调用本机 QQ 音乐兼容模式…");
        string stdin = "n\r\n" + encryptedDir + "\r\n";
        ProcessResult bridge = await ProcessRunner.RunAsync(
            options.DecryptorPath, Array.Empty<string>(), decryptRunDir, stdin,
            line => { if (!string.IsNullOrWhiteSpace(line)) log("QQ 兼容组件：" + line); }, ct);
        string decryptedDir = Path.Combine(decryptRunDir, "output");

        for (int p = 0; p < pending.Count; p++)
        {
            PendingBridge item = pending[p];
            ct.ThrowIfCancellationRequested();
            string prefix = $"{item.Index + 1:D4}";
            string? plaintext = null;
            string? extension = null;
            if (Directory.Exists(decryptedDir))
            {
                foreach (string candidate in Directory.GetFiles(decryptedDir, prefix + ".*", SearchOption.TopDirectoryOnly))
                {
                    extension = await AudioSignatures.DetectFileAsync(candidate, ct);
                    if (extension is not null) { plaintext = candidate; break; }
                }
            }
            if (plaintext is null || extension is null)
            {
                string detail = bridge.ExitCode == 0 ? "QQ 兼容组件未生成可识别的音频" :
                    DescribeQqBridgeFailure(bridge.ExitCode, bridge.CombinedOutput);
                results[item.Index] = new(item.InputPath, "", false, detail, "QQ 兼容失败");
                continue;
            }
            try
            {
                progress(64 + (int)(33.0 * p / Math.Max(1, pending.Count)),
                    $"正在转换 {p + 1}/{pending.Count}：{Path.GetFileName(item.InputPath)}");
                results[item.Index] = await FinishPlaintextAsync(
                    item.Index, item.InputPath, plaintext, extension, "QQ 兼容模式",
                    options, reservedOutputs, log, ct);
            }
            catch (ProcessCleanupException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                results[item.Index] = new(item.InputPath, "", false, ex.Message, "QQ 兼容失败");
            }
        }
    }

    private static async Task ProbeQqCompatibilityAsync(
        string input,
        ConversionOptions options,
        Action<string> log,
        CancellationToken ct)
    {
        string root = Path.Combine(Path.GetTempPath(), "MusicDrop3", "compat-probe-" + Guid.NewGuid().ToString("N"));
        string encryptedDir = Path.Combine(root, "input");
        string runDir = Path.Combine(root, "run");
        Directory.CreateDirectory(encryptedDir);
        Directory.CreateDirectory(runDir);
        try
        {
            string suffix = GetMatchedInputSuffix(input) ?? Path.GetExtension(input).ToLowerInvariant();
            string staged = Path.Combine(encryptedDir, "probe" + suffix);
            await CopyFileAsync(input, staged, ct);
            log("QQ 兼容链路：正在用一个临时文件验证客户端真实解密能力");
            ProcessResult bridge = await ProcessRunner.RunAsync(
                options.DecryptorPath,
                Array.Empty<string>(),
                runDir,
                "n\r\n" + encryptedDir + "\r\n",
                line => { if (!string.IsNullOrWhiteSpace(line)) log("QQ 兼容探针：" + line); },
                ct);
            string outputDir = Path.Combine(runDir, "output");
            string? candidate = Directory.Exists(outputDir)
                ? Directory.GetFiles(outputDir, "probe.*", SearchOption.TopDirectoryOnly).FirstOrDefault()
                : null;
            if (bridge.ExitCode != 0)
                throw new InvalidDataException(DescribeQqBridgeFailure(bridge.ExitCode, bridge.CombinedOutput));
            if (candidate is null)
                throw new InvalidDataException("QQ 兼容组件报告完成，但没有生成探针输出。客户端版本可能不兼容。");

            string? extension = await AudioSignatures.DetectFileAsync(candidate, ct);
            if (extension is null)
                throw new InvalidDataException(
                    "QQ 音乐进程已运行，但兼容探针仍是加密数据。请确认客户端已登录且当前账号有权播放该文件；若仍失败，则当前 QQ 音乐版本暂不兼容。尚未生成任何正式输出。");
            await VerifyDecodeAsync(options.FfmpegPath, candidate, ct);
            log($"QQ 兼容链路实文件探针通过：{extension.TrimStart('.').ToUpperInvariant()} 可完整解码");
        }
        finally
        {
            await DeleteDirectoryWithRetryAsync(root, log);
        }
    }

    private static async Task<string> ConvertOneAsync(
        string source,
        string sourceExtension,
        string output,
        ConversionOptions options,
        Action<string> log,
        CancellationToken ct)
    {
        string partial = output + ".partial";
        bool cleanupSafe = true;
        try
        {
            string format = NormalizeOutputFormat(options.Format);
            bool directCopy = format == "ORIGINAL" ||
                (format == "FLAC" && sourceExtension.Equals(".flac", StringComparison.OrdinalIgnoreCase)) ||
                (format == "MP3" && sourceExtension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)) ||
                (format == "OGG" && sourceExtension.Equals(".ogg", StringComparison.OrdinalIgnoreCase)) ||
                (format == "WAV" && sourceExtension.Equals(".wav", StringComparison.OrdinalIgnoreCase));

            if (directCopy)
            {
                await CopyFileAsync(source, partial, ct);
                await VerifyDecodeAsync(options.FfmpegPath, partial, ct);
                log(format == "ORIGINAL" ? "保留解密后的原始音频编码" : $"源文件已是 {format}，避免重复编码");
            }
            else
            {
                AudioInfo info = await ProbeAsync(options.FfmpegPath, source, ct);
                EnsureTranscodeCapacity(output, format, info, new FileInfo(source).Length);
                bool lossySource = IsKnownLossy(sourceExtension);
                if (lossySource && format is "FLAC" or "WAV")
                    log($"提示：源音频为 {sourceExtension.TrimStart('.').ToUpperInvariant()} 有损编码；转换为 {format} 不会恢复已损失的音质。");

                var args = new List<string>
                {
                    "-hide_banner", "-loglevel", "error", "-y", "-i", source,
                    "-map", "0:a:0", "-map_metadata", "0",
                };
                switch (format)
                {
                    case "FLAC":
                        args.AddRange(new[] { "-c:a", "flac", "-compression_level", "8", "-f", "flac", partial });
                        break;
                    case "WAV":
                        int wavBits = info.BitsPerSample > 0 ? info.BitsPerSample : 16;
                        string codec = wavBits switch
                        {
                            <= 16 => "pcm_s16le",
                            <= 24 => "pcm_s24le",
                            <= 32 => "pcm_s32le",
                            _ => throw new InvalidDataException($"不支持写入 WAV 的位深：{wavBits}"),
                        };
                        args.AddRange(new[] { "-c:a", codec, "-write_bext", "0", "-f", "wav", partial });
                        log($"WAV 输出：{info.SampleRate} Hz / {wavBits}-bit / {info.Channels} 声道");
                        break;
                    case "MP3":
                        bool cbr = options.Mp3Quality.StartsWith("320", StringComparison.OrdinalIgnoreCase);
                        args.AddRange(cbr
                            ? new[] { "-c:a", "libmp3lame", "-b:a", "320k" }
                            : new[] { "-c:a", "libmp3lame", "-q:a", "0" });
                        args.AddRange(new[] { "-id3v2_version", "3", "-f", "mp3", partial });
                        log("MP3 编码质量：" + (cbr ? "CBR 320 kbps" : "V0 VBR（约 245 kbps）"));
                        break;
                    case "OGG":
                        args.AddRange(new[] { "-c:a", "libvorbis", "-q:a", "6", "-f", "ogg", partial });
                        log("OGG Vorbis 编码质量：q6（有损）");
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(options.Format));
                }

                ProcessResult result = await ProcessRunner.RunAsync(
                    options.FfmpegPath, args, options.OutputDirectory, null, null, ct);
                if (result.ExitCode != 0)
                    throw new InvalidOperationException("FFmpeg 转换失败：" + result.CombinedOutput.Trim());
                await VerifyDecodeAsync(options.FfmpegPath, partial, ct);

                if (format == "WAV")
                {
                    int comparisonBits = info.BitsPerSample > 0 ? info.BitsPerSample : 16;
                    string sourceMd5 = await PcmMd5Async(options.FfmpegPath, source, comparisonBits, ct);
                    string wavMd5 = await PcmMd5Async(options.FfmpegPath, partial, comparisonBits, ct);
                    if (!string.Equals(sourceMd5, wavMd5, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("WAV 逐样本校验失败，已拒绝输出。");
                    log("WAV 逐样本 PCM MD5 校验通过");
                }
            }

            File.Move(partial, output, options.Overwrite);
            log("已输出：" + output);
            return output;
        }
        catch (ProcessCleanupException ex) when (!ex.CleanupSafe)
        {
            cleanupSafe = false;
            throw;
        }
        finally
        {
            if (cleanupSafe)
                try { if (File.Exists(partial)) File.Delete(partial); } catch { }
        }
    }

    public static async Task<AudioInfo> ProbeAsync(string ffmpeg, string path, CancellationToken ct)
    {
        string ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg) ?? "", "ffprobe.exe");
        if (File.Exists(ffprobe))
        {
            ProcessResult result = await ProcessRunner.RunAsync(ffprobe,
                new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=sample_rate,channels,bits_per_raw_sample,bits_per_sample,sample_fmt:format=duration", "-of", "json", path },
                Path.GetDirectoryName(path)!, null, null, ct);
            if (result.ExitCode == 0)
            {
                return ParseFfprobeJson(result.StandardOutput);
            }
        }

        ProcessResult fallback = await ProcessRunner.RunAsync(ffmpeg,
            new[] { "-hide_banner", "-i", path, "-map", "0:a:0", "-t", "0.001", "-f", "null", "-" },
            Path.GetDirectoryName(path)!, null, null, ct);
        string text = fallback.CombinedOutput;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"Audio:.*?,\s*(\d+) Hz,\s*([^,]+),\s*([^,\r\n]+)");
        if (!match.Success) throw new InvalidDataException("无法可靠识别源音频参数。请把 ffprobe.exe 放在 ffmpeg.exe 同一目录。");
        int rate = int.TryParse(match.Groups[1].Value, out int parsedRate) ? parsedRate : throw new InvalidDataException("无法识别采样率。");
        string channelText = match.Groups[2].Value;
        string sampleText = match.Groups[3].Value;
        int bitDepth = sampleText.Contains("32 bit", StringComparison.OrdinalIgnoreCase) ? 32 :
            sampleText.Contains("24 bit", StringComparison.OrdinalIgnoreCase) ? 24 :
            sampleText.Contains("s16", StringComparison.OrdinalIgnoreCase) ? 16 : 0;
        return new(rate, channelText.Contains("stereo", StringComparison.OrdinalIgnoreCase) ? 2 : 1, bitDepth, sampleText, 0);
    }

    private static int ParseInt(JsonElement element, string property, int fallback)
    {
        if (!element.TryGetProperty(property, out JsonElement value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out int number) => number,
            JsonValueKind.String when int.TryParse(value.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int text) => text,
            _ => fallback,
        };
    }

    internal static AudioInfo ParseFfprobeJson(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement streams = doc.RootElement.GetProperty("streams");
        if (streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() == 0)
            throw new InvalidDataException("FFprobe 没有返回音频流。");
        JsonElement stream = streams[0];
        int sampleRate = ParseInt(stream, "sample_rate", 44100);
        int channels = ParseInt(stream, "channels", 2);
        int bits = ParseInt(stream, "bits_per_raw_sample", ParseInt(stream, "bits_per_sample", 0));
        string sampleFormat = stream.TryGetProperty("sample_fmt", out JsonElement formatValue) &&
            formatValue.ValueKind == JsonValueKind.String ? formatValue.GetString() ?? "" : "";
        double duration = 0;
        if (doc.RootElement.TryGetProperty("format", out JsonElement format) &&
            format.TryGetProperty("duration", out JsonElement durationValue))
        {
            if (durationValue.ValueKind == JsonValueKind.Number)
                durationValue.TryGetDouble(out duration);
            else if (durationValue.ValueKind == JsonValueKind.String)
                double.TryParse(durationValue.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out duration);
        }
        return new(sampleRate, channels, bits, sampleFormat, duration);
    }

    private static async Task VerifyDecodeAsync(string ffmpeg, string path, CancellationToken ct)
    {
        ProcessResult result = await ProcessRunner.RunAsync(ffmpeg,
            new[] { "-v", "error", "-xerror", "-i", path, "-map", "0:a:0", "-f", "null", "-" },
            Path.GetDirectoryName(path)!, null, null, ct);
        if (result.ExitCode != 0)
            throw new InvalidDataException("输出文件完整解码校验失败：" + result.CombinedOutput.Trim());
    }

    private static async Task<string> PcmMd5Async(string ffmpeg, string path, int bits, CancellationToken ct)
    {
        string codec = bits switch
        {
            <= 16 => "pcm_s16le", <= 24 => "pcm_s24le", <= 32 => "pcm_s32le",
            _ => throw new InvalidDataException("不支持的 PCM 位深。"),
        };
        ProcessResult result = await ProcessRunner.RunAsync(ffmpeg,
            new[] { "-v", "error", "-i", path, "-map", "0:a:0", "-c:a", codec, "-f", "md5", "-" },
            Path.GetDirectoryName(path)!, null, null, ct);
        if (result.ExitCode != 0)
            throw new InvalidDataException("PCM 校验计算失败：" + result.CombinedOutput.Trim());
        return result.StandardOutput.Trim();
    }

    private static string AllocateOutputPath(
        string input,
        string extension,
        ConversionOptions options,
        HashSet<string> reserved)
    {
        string stem = GetInputStem(input);
        string candidate = Path.Combine(options.OutputDirectory, stem + extension);
        int suffix = 1;
        while (PathsEqual(input, candidate) || reserved.Contains(candidate) ||
               File.Exists(candidate + ".partial") || (!options.Overwrite && File.Exists(candidate)))
            candidate = Path.Combine(options.OutputDirectory, $"{stem} ({suffix++}){extension}");
        if (PathsEqual(input, candidate))
            throw new IOException("输出路径不能与加密源文件相同。");
        reserved.Add(candidate);
        return candidate;
    }

    private static bool PathsEqual(string first, string second) => string.Equals(
        Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        StringComparison.OrdinalIgnoreCase);

    internal static bool IsSupportedInput(string path) => GetMatchedInputSuffix(path) is not null;

    private static bool IsQqInput(string path) => QqExtensions.Any(extension =>
        path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static bool IsStandardAudioInput(string path)
    {
        string? matched = GetMatchedInputSuffix(path);
        return matched is not null && StandardAudioExtensions.Contains(matched, StringComparer.OrdinalIgnoreCase);
    }

    private static string? GetMatchedInputSuffix(string path) => SupportedInputExtensions.FirstOrDefault(extension =>
        path.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static string GetInputStem(string input)
    {
        string name = Path.GetFileName(input);
        string? suffix = GetMatchedInputSuffix(name);
        return suffix is null ? Path.GetFileNameWithoutExtension(name) : name[..^suffix.Length];
    }

    private static string NormalizeAudioExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension) || extension[0] != '.')
            throw new InvalidDataException("解密后未能确定真实音频格式。");
        return extension.ToLowerInvariant();
    }

    private static string NormalizeOutputFormat(string format) => format.Trim().ToUpperInvariant() switch
    {
        "原始格式" or "ORIGINAL" => "ORIGINAL",
        "FLAC" => "FLAC",
        "WAV" => "WAV",
        "MP3" => "MP3",
        "OGG" => "OGG",
        _ => throw new ArgumentOutOfRangeException(nameof(format), "不支持的输出格式：" + format),
    };

    private static void ValidateFormat(string format) => _ = NormalizeOutputFormat(format);

    private static string GetTargetExtension(string format, string sourceExtension) => NormalizeOutputFormat(format) switch
    {
        "ORIGINAL" => sourceExtension,
        "FLAC" => ".flac",
        "WAV" => ".wav",
        "MP3" => ".mp3",
        "OGG" => ".ogg",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static bool IsKnownLossy(string extension) => extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ogg", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".wma", StringComparison.OrdinalIgnoreCase);

    private static string DescribeQqFormat(QmcProbeResult probe) => probe.FooterKind switch
    {
        QmcFooterKind.None => "QQ QMC v1",
        QmcFooterKind.PcV1Legacy => "QQ QMC PC v1",
        QmcFooterKind.AndroidQTag => "QQ QMC Android QTag",
        QmcFooterKind.AndroidSTag => "QQ QMC Android STag",
        QmcFooterKind.PcV2MusicEx => "QQ QMC MusicEx v1",
        _ => "QQ QMC",
    };

    private static string FormatCodec(string? extension) => string.IsNullOrWhiteSpace(extension)
        ? "—"
        : extension.TrimStart('.').ToUpperInvariant();

    private static FilePreflightResult DescribeQqCompatibilityPreflight(
        string platform,
        string codec,
        bool useQqFallback,
        bool autoStartRequiredClients,
        string qqMusicExecutablePath,
        string decryptorPath)
    {
        if (!useQqFallback)
            return new(platform, codec, "需要 QQ EKey", false,
                "兼容模式已关闭；可导入 EKey 或本地密钥库。", true);
        if (!ToolManager.IsTrustedDecryptor(decryptorPath))
            return new(platform, codec, "兼容组件未安装", false,
                "需要安装并通过 SHA-256 校验的固定版本 QQ 兼容组件。", true);

        MusicClientDiscovery discovery = MusicClientManager.DiscoverQqMusic(qqMusicExecutablePath);
        if (discovery.IsRunning && discovery.IsTrusted)
            return new(platform, codec, "可使用 QQ 兼容转换", true,
                "QQ 音乐已运行；转换时仍会验证兼容组件输出。", true);
        if (!discovery.IsTrusted && discovery.ExecutablePath is not null)
            return new(platform, codec, "QQ 音乐身份校验失败", false, discovery.Detail, true);
        if (discovery.ExecutablePath is null)
            return new(platform, codec, "未找到 QQ 音乐", false, discovery.Detail, true);
        if (autoStartRequiredClients)
            return new(platform, codec, "可自动启动 QQ 兼容转换", true,
                "开始转换时仅启动通过腾讯数字签名校验的 QQMusic.exe。", true);
        return new(platform, codec, "需要启动 QQ 音乐", false,
            "已找到官方客户端，但自动启动已关闭。", true);
    }

    private static string DescribeQqBridgeFailure(int exitCode, string output)
    {
        string normalized = output.Trim();
        if (normalized.Contains("failed to attach", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("attach", StringComparison.OrdinalIgnoreCase) &&
            normalized.Contains("fail", StringComparison.OrdinalIgnoreCase))
            return "QQ 兼容组件无法附加到 QQ 音乐进程。请确保两者由同一 Windows 用户和相同权限级别运行，然后重试。";
        if (normalized.Contains("access", StringComparison.OrdinalIgnoreCase) &&
            normalized.Contains("denied", StringComparison.OrdinalIgnoreCase))
            return "QQ 兼容组件被 Windows 权限隔离阻止。程序不会自动提权；请用与 QQ 音乐相同的权限级别重新运行。";
        if (normalized.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return "QQ 兼容组件未找到所需的客户端模块；QQ 音乐版本可能已变化。";
        return $"QQ 兼容组件失败（退出码 {exitCode}）。详细诊断已写入界面日志。";
    }

    private static long SaturatingMultiply(long value, long factor) =>
        value > long.MaxValue / factor ? long.MaxValue - 256L * 1024 * 1024 : value * factor;

    private static void AddVolumeRequirement(Dictionary<string, long> requirements, string path, long bytes)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath)
            ?? throw new IOException("无法确定路径所在磁盘卷：" + fullPath);
        requirements.TryGetValue(root, out long current);
        requirements[root] = current > long.MaxValue - bytes ? long.MaxValue : current + bytes;
    }

    private static string FormatBytes(long value)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = value;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1) { size /= 1024; unit++; }
        return $"{size:0.##} {units[unit]}";
    }

    private static void EnsureTranscodeCapacity(
        string outputPath,
        string format,
        AudioInfo info,
        long sourceBytes)
    {
        const long safetyMargin = 128L * 1024 * 1024;
        long estimate = format switch
        {
            "WAV" when info.DurationSeconds > 0 => EstimatePcmBytes(info),
            "FLAC" => SaturatingMultiply(sourceBytes, 4),
            "MP3" or "OGG" => SaturatingMultiply(sourceBytes, 2),
            _ => sourceBytes,
        };
        long required = estimate > long.MaxValue - safetyMargin ? long.MaxValue : estimate + safetyMargin;
        string root = Path.GetPathRoot(Path.GetFullPath(outputPath))
            ?? throw new IOException("无法确定输出路径所在磁盘卷。");
        var drive = new DriveInfo(root);
        if (!drive.IsReady || drive.AvailableFreeSpace < required)
            throw new IOException($"输出磁盘空间不足（{root}）：预计至少需要 {FormatBytes(required)}，可用 {FormatBytes(drive.IsReady ? drive.AvailableFreeSpace : 0)}。");
    }

    private static long EstimatePcmBytes(AudioInfo info)
    {
        int bits = info.BitsPerSample > 0 ? Math.Clamp(info.BitsPerSample, 16, 32) : 16;
        double value = info.DurationSeconds * info.SampleRate * Math.Max(1, info.Channels) * (bits / 8.0) + 64 * 1024;
        return value >= long.MaxValue ? long.MaxValue : Math.Max(0, (long)Math.Ceiling(value));
    }

    private static async Task CopyFileAsync(string source, string destination, CancellationToken ct)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, 1024 * 1024, ct);
        await output.FlushAsync(ct);
    }

    private static async Task DeleteDirectoryWithRetryAsync(string root, Action<string> log)
    {
        if (!Directory.Exists(root)) return;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try { Directory.Delete(root, recursive: true); return; }
            catch when (attempt < 5) { await Task.Delay(200 * attempt); }
            catch (Exception ex) { log("警告：临时目录清理失败，请手动删除：" + root + "（" + ex.Message + "）"); }
        }
    }

    internal static bool IsProcessRunning(string processName)
    {
        if (processName.Equals("QQMusic", StringComparison.OrdinalIgnoreCase))
            return MusicClientManager.IsQqMusicRunning();
        Process[] processes = Process.GetProcessesByName(processName);
        try { return processes.Any(process => !process.HasExited); }
        catch { return processes.Length > 0; }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }

    private sealed record PendingBridge(int Index, string InputPath);
    private sealed record QqPreparation(QmcProbeResult Probe, string? EKey, string Route);
}
