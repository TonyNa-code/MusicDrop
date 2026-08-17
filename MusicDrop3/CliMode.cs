namespace MFlacDrop;

internal static class CliMode
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Contains("--version", StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{AppInfo.AppName} {AppInfo.AppVersion}");
                return 0;
            }
            if (args.Contains("--license-status", StringComparer.OrdinalIgnoreCase))
            {
                BuyerLicenseStatus status = RetailLicenseService.GetCurrentStatus();
                Console.WriteLine((status.IsValid ? "LICENSE OK " : "LICENSE INFO ") + status.Summary);
                return RetailLicenseService.IsRetailBuild && !status.IsValid ? 3 : 0;
            }
            int installLicenseIndex = Array.FindIndex(args, x => x.Equals("--install-license", StringComparison.OrdinalIgnoreCase));
            if (installLicenseIndex >= 0 && installLicenseIndex + 1 < args.Length)
            {
                BuyerLicenseStatus status = RetailLicenseService.InstallLicense(args[installLicenseIndex + 1]);
                Console.WriteLine((status.IsValid ? "LICENSE OK " : "LICENSE FAIL ") + status.Summary);
                return status.IsValid ? 0 : 3;
            }
            if (args.Contains("--install-ffmpeg", StringComparer.OrdinalIgnoreCase))
            {
                var progress = new Progress<(int percent, string status)>(x => Console.WriteLine($"INSTALL {x.percent} {x.status}"));
                await ToolManager.InstallFfmpegAsync(progress, CancellationToken.None);
                Console.WriteLine("INSTALL FFMPEG OK " + AppInfo.ManagedFfmpegExe);
                return FfmpegManager.IsManagedInstallValid() ? 0 : 3;
            }
            int installFfmpegZipIndex = Array.FindIndex(args, x => x.Equals("--install-ffmpeg-zip", StringComparison.OrdinalIgnoreCase));
            if (installFfmpegZipIndex >= 0 && installFfmpegZipIndex + 1 < args.Length)
            {
                var progress = new Progress<(int percent, string status)>(x => Console.WriteLine($"INSTALL {x.percent} {x.status}"));
                await ToolManager.InstallFfmpegFromZipAsync(args[installFfmpegZipIndex + 1], progress, CancellationToken.None);
                Console.WriteLine("INSTALL FFMPEG ZIP OK " + AppInfo.ManagedFfmpegExe);
                return FfmpegManager.IsManagedInstallValid() ? 0 : 3;
            }
            if (args.Contains("--install-decryptor", StringComparer.OrdinalIgnoreCase))
            {
                var progress = new Progress<(int percent, string status)>(x => Console.WriteLine($"INSTALL {x.percent} {x.status}"));
                await ToolManager.InstallDecryptorAsync(progress, CancellationToken.None);
                Console.WriteLine("INSTALL OK " + AppInfo.DecryptorExe);
                return ToolManager.IsDecryptorValid() ? 0 : 3;
            }
            int installZipIndex = Array.FindIndex(args, x => x.Equals("--install-decryptor-zip", StringComparison.OrdinalIgnoreCase));
            if (installZipIndex >= 0 && installZipIndex + 1 < args.Length)
            {
                var progress = new Progress<(int percent, string status)>(x => Console.WriteLine($"INSTALL {x.percent} {x.status}"));
                await ToolManager.InstallDecryptorFromZipAsync(args[installZipIndex + 1], progress, CancellationToken.None);
                Console.WriteLine("INSTALL ZIP OK " + AppInfo.DecryptorExe);
                return ToolManager.IsDecryptorValid() ? 0 : 3;
            }
            if (RetailLicenseService.IsRetailBuild)
            {
                BuyerLicenseStatus license = RetailLicenseService.GetCurrentStatus();
                if (!license.IsValid)
                    throw new UnauthorizedAccessException(
                        "MusicDrop™ 便利版需要有效的 buyer-license.json。请先运行 --install-license <文件路径>；许可证永久、不联网、不绑定电脑。");
            }
            var values = Parse(args);
            string format = values.GetValueOrDefault("format") ?? "原始格式";
            string output = values.GetValueOrDefault("output") ?? throw new ArgumentException("缺少 --output");
            string ffmpeg = ToolManager.FindFfmpeg(values.GetValueOrDefault("ffmpeg") ?? "")
                ?? throw new ArgumentException("未找到 FFmpeg。请先运行 --install-ffmpeg，或通过 --ffmpeg 指定路径。");
            string decryptor = values.GetValueOrDefault("decryptor") ?? AppInfo.DecryptorExe;
            string keyDb = values.GetValueOrDefault("key-db") ?? "";
            string ekeyFile = values.GetValueOrDefault("ekey-file") ?? "";
            string kugouDb = values.GetValueOrDefault("kugou-db") ?? "";
            bool useQqFallback = !string.Equals(values.GetValueOrDefault("qq-fallback"), "false", StringComparison.OrdinalIgnoreCase);
            bool autoStartClients = string.Equals(values.GetValueOrDefault("auto-start-clients"), "true", StringComparison.OrdinalIgnoreCase);
            bool strictPreflight = !string.Equals(values.GetValueOrDefault("strict-preflight"), "false", StringComparison.OrdinalIgnoreCase);
            string qqMusicExe = values.GetValueOrDefault("qqmusic-exe") ?? "";
            int clientReadyTimeout = int.TryParse(values.GetValueOrDefault("client-ready-timeout"), out int parsedTimeout)
                ? parsedTimeout : 30;
            if (clientReadyTimeout is < 1 or > 300)
                throw new ArgumentOutOfRangeException("--client-ready-timeout", "必须为 1 到 300 秒。");
            int cancelAfterMs = int.TryParse(values.GetValueOrDefault("cancel-after-ms"), out int parsedCancel) ? parsedCancel : 0;
            var inputs = values.Where(x => x.Key.StartsWith("input", StringComparison.Ordinal)).Select(x => x.Value!).ToList();
            if (inputs.Count == 0) throw new ArgumentException("至少需要一个 --input");
            var options = new ConversionOptions(
                Format: format,
                Mp3Quality: "V0（约 245 kbps）",
                OutputDirectory: output,
                FfmpegPath: ffmpeg,
                DecryptorPath: decryptor,
                Overwrite: true,
                PlayerProcessDbPath: keyDb,
                ImportedEKeyPath: ekeyFile,
                UseQqFallback: useQqFallback,
                KugouDatabasePath: kugouDb,
                AutoStartRequiredClients: autoStartClients,
                QqMusicExecutablePath: qqMusicExe,
                ClientReadyTimeoutSeconds: clientReadyTimeout,
                StrictBatchPreflight: strictPreflight);
            using var cts = new CancellationTokenSource();
            if (cancelAfterMs > 0) cts.CancelAfter(cancelAfterMs);
            var results = await AudioConverter.ConvertAsync(inputs, options,
                (p, s) => Console.WriteLine($"PROGRESS {p} {s}"),
                s => Console.WriteLine("LOG " + s),
                cts.Token);
            foreach (var result in results)
                Console.WriteLine($"RESULT {(result.Success ? "OK" : "FAIL")} [{result.Route}] {result.InputPath} => {result.OutputPath} {result.Message}");
            return results.All(x => x.Success) ? 0 : 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        int inputIndex = 0;
        for (int i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            string key = args[i][2..];
            string? value = i + 1 < args.Length ? args[++i] : null;
            if (key.Equals("input", StringComparison.OrdinalIgnoreCase)) key = "input" + inputIndex++;
            values[key] = value;
        }
        return values;
    }
}
