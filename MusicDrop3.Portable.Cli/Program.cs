// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MusicDrop.Core;

return await PortableCli.RunAsync(args);

internal static class PortableCli
{
    private const string Version = "3.1.0-preview.1";

    public static async Task<int> RunAsync(string[] args)
    {
        CliOptions options;
        try { options = CliOptions.Parse(args); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            PrintHelp(UseChinese(args));
            return 2;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine("MusicDrop Portable " + Version);
            return 0;
        }
        if (options.ShowHelp || args.Length == 0)
        {
            PrintHelp(options.Chinese);
            return 0;
        }

        var service = new PortableAudioService();
        IReadOnlyList<string> inputs;
        try
        {
            IReadOnlyList<string> manifestInputs = ReadInputManifests(options.InputManifests);
            inputs = ExpandInputs(options.Inputs.Concat(manifestInputs).ToArray(), service);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        if (inputs.Count == 0)
        {
            Console.Error.WriteLine(T(options, "没有找到受支持的输入文件。", "No supported input files were found."));
            return 2;
        }

        QmcKeyring? qmcKeyring;
        try { qmcKeyring = ReadQmcKeyring(options.QmcEKeyFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        var decryptOptions = new PortableDecryptOptions(
            KugouDatabasePath: options.KugouDatabase,
            Overwrite: options.Overwrite,
            QmcKeyring: qmcKeyring);

        Console.WriteLine(T(options,
            $"MusicDrop™ Portable {Version} · {inputs.Count} 个文件 · 并发 {options.Jobs}",
            $"MusicDrop™ Portable {Version} · {inputs.Count} file(s) · {options.Jobs} worker(s)"));
        Console.WriteLine(T(options, "严格预检中…", "Running strict preflight…"));

        PortableProbeResult[] probes = await ProbeAllAsync(
            inputs, service, decryptOptions, options.Jobs, CancellationToken.None);
        foreach (PortableProbeResult probe in probes)
        {
            string status = probe.CanDecrypt
                ? T(options, "就绪", "ready")
                : probe.RequiresExternalKey ? T(options, "缺少本地密钥", "local key required")
                : T(options, "不支持/损坏", "unsupported/damaged");
            Console.WriteLine($"[{status}] {Path.GetFileName(probe.InputPath)} · {probe.Family} · " +
                (probe.AudioExtension?.TrimStart('.').ToUpperInvariant() ?? "—"));
            if (!probe.CanDecrypt && !string.IsNullOrWhiteSpace(probe.Error))
                Console.WriteLine("  " + probe.Error);
        }
        if (options.ProbeOnly) return probes.All(item => item.CanDecrypt) ? 0 : 3;
        if (probes.Any(item => !item.CanDecrypt))
        {
            Console.Error.WriteLine(T(options,
                "严格预检未通过，尚未生成任何正式输出。",
                "Strict preflight failed; no final output was created."));
            return 3;
        }

        string? ffmpeg = null;
        if (!options.Format.Equals("ORIGINAL", StringComparison.OrdinalIgnoreCase))
        {
            ffmpeg = FfmpegTranscoder.Find(options.Ffmpeg);
            if (ffmpeg is null)
            {
                Console.Error.WriteLine(T(options,
                    "未找到 FFmpeg。请使用完整发行包，或通过 --ffmpeg 指定可执行文件。",
                    "FFmpeg was not found. Use a Full package or pass --ffmpeg."));
                return 4;
            }
            try { await FfmpegTranscoder.ValidateAsync(ffmpeg, CancellationToken.None); }
            catch (Exception ex)
            {
                Console.Error.WriteLine(T(options, "FFmpeg 自检失败：", "FFmpeg validation failed: ") + ex.Message);
                return 4;
            }
        }

        string outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);
        IReadOnlyList<ConversionPlan> plans = AllocatePlans(
            probes, service, outputDirectory, options.Format, options.Overwrite);
        string temporaryRoot = Path.Combine(Path.GetTempPath(), "MusicDrop3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryRoot);
        var results = new ConcurrentBag<ConversionOutcome>();
        using var gate = new SemaphoreSlim(options.Jobs, options.Jobs);
        int completed = 0;
        try
        {
            Task[] tasks = plans.Select(async plan =>
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    ConversionOutcome outcome = await ConvertOneAsync(
                        plan, service, decryptOptions, options, ffmpeg, temporaryRoot).ConfigureAwait(false);
                    results.Add(outcome);
                    int done = Interlocked.Increment(ref completed);
                    Console.WriteLine($"[{done}/{plans.Count}] " +
                        (outcome.Success ? "✓ " : "✗ ") + Path.GetFileName(plan.InputPath) +
                        (outcome.Success ? " → " + outcome.OutputPath : " · " + outcome.Message));
                }
                finally { gate.Release(); }
            }).ToArray();
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(temporaryRoot, recursive: true); } catch { }
        }

        int failures = results.Count(item => !item.Success);
        Console.WriteLine(failures == 0
            ? T(options, $"完成：{results.Count} 个文件全部成功。", $"Done: all {results.Count} file(s) succeeded.")
            : T(options, $"完成：成功 {results.Count - failures}，失败 {failures}。",
                $"Done: {results.Count - failures} succeeded, {failures} failed."));
        return failures == 0 ? 0 : 5;
    }

    private static async Task<PortableProbeResult[]> ProbeAllAsync(
        IReadOnlyList<string> inputs,
        PortableAudioService service,
        PortableDecryptOptions options,
        int jobs,
        CancellationToken cancellationToken)
    {
        var results = new PortableProbeResult[inputs.Count];
        using var gate = new SemaphoreSlim(jobs, jobs);
        await Task.WhenAll(inputs.Select(async (input, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { results[index] = await service.ProbeAsync(input, options, cancellationToken).ConfigureAwait(false); }
            finally { gate.Release(); }
        })).ConfigureAwait(false);
        return results;
    }

    private static async Task<ConversionOutcome> ConvertOneAsync(
        ConversionPlan plan,
        PortableAudioService service,
        PortableDecryptOptions decryptOptions,
        CliOptions options,
        string? ffmpeg,
        string temporaryRoot)
    {
        string? decryptedTemporary = null;
        try
        {
            if (options.Format.Equals("ORIGINAL", StringComparison.OrdinalIgnoreCase))
            {
                PortableDecryptResult result = await service.DecryptToFileAsync(
                    plan.InputPath, plan.OutputPath, decryptOptions).ConfigureAwait(false);
                return new(true, result.OutputPath, result.Route);
            }

            string source = plan.InputPath;
            if (!plan.Probe.IsStandardAudio)
            {
                decryptedTemporary = Path.Combine(temporaryRoot,
                    Guid.NewGuid().ToString("N") + plan.Probe.AudioExtension);
                await service.DecryptToFileAsync(
                    plan.InputPath, decryptedTemporary, decryptOptions).ConfigureAwait(false);
                source = decryptedTemporary;
            }
            await FfmpegTranscoder.ConvertAsync(
                ffmpeg!, source, plan.OutputPath, options.Format, options.Overwrite,
                CancellationToken.None).ConfigureAwait(false);
            return new(true, plan.OutputPath, "FFmpeg " + options.Format);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
            InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return new(false, "", ex.Message);
        }
        finally
        {
            try { if (decryptedTemporary is not null && File.Exists(decryptedTemporary)) File.Delete(decryptedTemporary); }
            catch { }
        }
    }

    private static IReadOnlyList<ConversionPlan> AllocatePlans(
        IReadOnlyList<PortableProbeResult> probes,
        PortableAudioService service,
        string outputDirectory,
        string format,
        bool overwrite)
    {
        var plans = new List<ConversionPlan>(probes.Count);
        var reserved = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (PortableProbeResult probe in probes)
        {
            string extension = format.ToUpperInvariant() switch
            {
                "ORIGINAL" => probe.AudioExtension!,
                "FLAC" => ".flac",
                "WAV" => ".wav",
                "MP3" => ".mp3",
                "OGG" => ".ogg",
                _ => throw new ArgumentOutOfRangeException(nameof(format)),
            };
            string stem = service.GetInputStem(probe.InputPath);
            string candidate = Path.Combine(outputDirectory, stem + extension);
            int suffix = 1;
            while (PathsEqual(candidate, probe.InputPath) || reserved.Contains(candidate) ||
                   File.Exists(candidate + ".partial") || (!overwrite && File.Exists(candidate)))
                candidate = Path.Combine(outputDirectory, $"{stem} ({suffix++}){extension}");
            reserved.Add(candidate);
            plans.Add(new(probe.InputPath, candidate, probe));
        }
        return plans;
    }

    private static IReadOnlyList<string> ExpandInputs(
        IReadOnlyList<string> requested, PortableAudioService service)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new HashSet<string>(comparer);
        foreach (string item in requested)
        {
            string full = Path.GetFullPath(item);
            if (File.Exists(full))
            {
                if (service.IsSupportedPath(full)) result.Add(full);
                continue;
            }
            if (Directory.Exists(full))
            {
                foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                    if (service.IsSupportedPath(file)) result.Add(Path.GetFullPath(file));
                continue;
            }
            throw new FileNotFoundException("Input path does not exist: " + full, full);
        }
        return result.OrderBy(value => value, comparer).ToArray();
    }

    private static IReadOnlyList<string> ReadInputManifests(IReadOnlyList<string> manifests)
    {
        var result = new List<string>();
        foreach (string path in manifests)
        {
            var info = new FileInfo(Path.GetFullPath(path));
            if (!info.Exists) throw new FileNotFoundException("Input manifest does not exist.", info.FullName);
            if (info.Length is <= 1 or > 8 * 1024 * 1024)
                throw new InvalidDataException("Input manifest size is outside the 8 MiB safety limit.");
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(info.FullName),
                new JsonDocumentOptions { MaxDepth = 4 });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Input manifest must be a JSON string array.");
            foreach (JsonElement element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
                    throw new InvalidDataException("Input manifest contains a non-string or empty path.");
                result.Add(element.GetString()!);
                if (result.Count > 100_000)
                    throw new InvalidDataException("Input manifests contain too many paths.");
            }
        }
        return result;
    }

    private static QmcKeyring? ReadQmcKeyring(string path)
    {
        return string.IsNullOrWhiteSpace(path) ? null : QmcKeyring.Load(path);
    }

    private static bool PathsEqual(string first, string second) => string.Equals(
        Path.GetFullPath(first).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        Path.GetFullPath(second).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string T(CliOptions options, string chinese, string english) =>
        options.Chinese ? chinese : english;

    private static bool UseChinese(string[] args) => !args.Contains("--lang", StringComparer.OrdinalIgnoreCase) ||
        !args.SkipWhile(value => !value.Equals("--lang", StringComparison.OrdinalIgnoreCase)).Skip(1)
            .FirstOrDefault()?.Equals("en", StringComparison.OrdinalIgnoreCase) == true;

    private static void PrintHelp(bool chinese)
    {
        Console.WriteLine(chinese ? """
            MusicDrop™ Portable 3.1
            离线、跨平台、严格验证的批量音频转换器

            用法：
              musicdrop --input <文件或文件夹> [--input <路径> ...] --output <目录>
                        --format ORIGINAL|FLAC|WAV|MP3|OGG [选项]

            选项：
              --probe                 只识别和预检，不写输出
              --input-manifest <JSON> 从本地 JSON 字符串数组读取批量路径
              --ffmpeg <路径>         指定 FFmpeg（ORIGINAL 不需要）
              --qmc-ekey-file <路径>  从本地文本读取用户已有的 QMC EKey
              --kugou-db <路径>       指定本地 KGMusicV3.db（KGM v5）
              --jobs <1-16>           并发数；默认按 CPU 自动选择
              --overwrite             允许覆盖正式输出
              --lang zh|en            界面语言
              --version               显示版本

            也可以把文件路径直接作为位置参数传入。源文件永不删除或原地修改。
            """ : """
            MusicDrop™ Portable 3.1
            Offline, cross-platform batch audio conversion with strict validation

            Usage:
              musicdrop --input <file-or-folder> [--input <path> ...] --output <directory>
                        --format ORIGINAL|FLAC|WAV|MP3|OGG [options]

            Options:
              --probe                 Probe only; create no output
              --input-manifest <JSON> Read batch paths from a local JSON string array
              --ffmpeg <path>         Select FFmpeg (not required for ORIGINAL)
              --qmc-ekey-file <path>  Read a user-owned QMC EKey from local text
              --kugou-db <path>       Select a local KGMusicV3.db for KGM v5
              --jobs <1-16>           Worker count; automatic by default
              --overwrite             Allow final output replacement
              --lang zh|en            Interface language
              --version               Print version

            File paths can also be passed positionally. Sources are never deleted or modified in place.
            """);
    }

    private sealed record ConversionPlan(string InputPath, string OutputPath, PortableProbeResult Probe);
    private sealed record ConversionOutcome(bool Success, string OutputPath, string Message);

    private sealed record CliOptions(
        IReadOnlyList<string> Inputs,
        IReadOnlyList<string> InputManifests,
        string OutputDirectory,
        string Format,
        string Ffmpeg,
        string QmcEKeyFile,
        string KugouDatabase,
        int Jobs,
        bool Overwrite,
        bool ProbeOnly,
        bool Chinese,
        bool ShowHelp,
        bool ShowVersion)
    {
        public static CliOptions Parse(string[] args)
        {
            var inputs = new List<string>();
            var inputManifests = new List<string>();
            string output = Path.Combine(Environment.CurrentDirectory, "MusicDrop Output");
            string format = "ORIGINAL";
            string ffmpeg = "";
            string qmcEKeyFile = "";
            string kugouDatabase = "";
            int jobs = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            bool overwrite = false;
            bool probe = false;
            bool chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
            bool help = false;
            bool version = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                string Next()
                {
                    if (++i >= args.Length) throw new ArgumentException("Missing value after " + arg);
                    return args[i];
                }
                switch (arg.ToLowerInvariant())
                {
                    case "--input": case "-i": inputs.Add(Next()); break;
                    case "--input-manifest": inputManifests.Add(Next()); break;
                    case "--output": case "-o": output = Next(); break;
                    case "--format": case "-f": format = Next().ToUpperInvariant(); break;
                    case "--ffmpeg": ffmpeg = Next(); break;
                    case "--qmc-ekey-file": qmcEKeyFile = Next(); break;
                    case "--kugou-db": kugouDatabase = Next(); break;
                    case "--jobs":
                        if (!int.TryParse(Next(), out jobs) || jobs is < 1 or > 16)
                            throw new ArgumentException("--jobs must be between 1 and 16.");
                        break;
                    case "--overwrite": overwrite = true; break;
                    case "--probe": probe = true; break;
                    case "--lang":
                        string language = Next();
                        if (!language.Equals("zh", StringComparison.OrdinalIgnoreCase) &&
                            !language.Equals("en", StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("--lang must be zh or en.");
                        chinese = language.Equals("zh", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "--help": case "-h": case "-?": help = true; break;
                    case "--version": version = true; break;
                    default:
                        if (arg.StartsWith('-')) throw new ArgumentException("Unknown option: " + arg);
                        inputs.Add(arg);
                        break;
                }
            }
            if (format is not ("ORIGINAL" or "FLAC" or "WAV" or "MP3" or "OGG"))
                throw new ArgumentException("--format must be ORIGINAL, FLAC, WAV, MP3 or OGG.");
            return new(inputs, inputManifests, output, format, ffmpeg, qmcEKeyFile, kugouDatabase,
                jobs, overwrite, probe, chinese, help, version);
        }
    }
}

internal static class FfmpegTranscoder
{
    public static string? Find(string configured)
    {
        IEnumerable<string> candidates = CandidatePaths(configured);
        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task ValidateAsync(string ffmpeg, CancellationToken cancellationToken)
    {
        ProcessResult result = await RunAsync(ffmpeg, new[] { "-hide_banner", "-version" }, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0 || !result.StandardOutput.Contains("ffmpeg version", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected executable is not a working FFmpeg binary.");
    }

    public static async Task ConvertAsync(
        string ffmpeg,
        string source,
        string output,
        string format,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        string partial = output + ".partial-" + Guid.NewGuid().ToString("N");
        try
        {
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-nostdin", "-i", source,
                "-map", "0:a:0", "-vn", "-map_metadata", "0",
            };
            switch (format.ToUpperInvariant())
            {
                case "FLAC":
                    arguments.AddRange(new[] { "-c:a", "flac", "-compression_level", "5", "-f", "flac", partial });
                    break;
                case "WAV":
                    int bits = await ProbeBitDepthAsync(ffmpeg, source, cancellationToken).ConfigureAwait(false);
                    string codec = bits <= 16 ? "pcm_s16le" : bits <= 24 ? "pcm_s24le" : "pcm_s32le";
                    arguments.AddRange(new[] { "-c:a", codec, "-f", "wav", partial });
                    break;
                case "MP3":
                    arguments.AddRange(new[] { "-c:a", "libmp3lame", "-q:a", "0", "-id3v2_version", "3", "-f", "mp3", partial });
                    break;
                case "OGG":
                    arguments.AddRange(new[] { "-c:a", "libvorbis", "-q:a", "6", "-f", "ogg", partial });
                    break;
                default: throw new ArgumentOutOfRangeException(nameof(format));
            }
            ProcessResult encoded = await RunAsync(ffmpeg, arguments, cancellationToken).ConfigureAwait(false);
            if (encoded.ExitCode != 0) throw new InvalidOperationException(encoded.CombinedOutput.Trim());
            ProcessResult verified = await RunAsync(ffmpeg,
                new[] { "-v", "error", "-xerror", "-i", partial, "-map", "0:a:0", "-f", "null", "-" },
                cancellationToken).ConfigureAwait(false);
            if (verified.ExitCode != 0)
                throw new InvalidDataException("Full output decode verification failed: " + verified.CombinedOutput.Trim());
            if (format.Equals("WAV", StringComparison.OrdinalIgnoreCase))
            {
                int bits = await ProbeBitDepthAsync(ffmpeg, source, cancellationToken).ConfigureAwait(false);
                string sourceMd5 = await PcmMd5Async(ffmpeg, source, bits, cancellationToken).ConfigureAwait(false);
                string outputMd5 = await PcmMd5Async(ffmpeg, partial, bits, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(sourceMd5, outputMd5, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("WAV sample-level PCM MD5 verification failed.");
            }
            File.Move(partial, output, overwrite);
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
        }
    }

    private static async Task<int> ProbeBitDepthAsync(
        string ffmpeg, string source, CancellationToken cancellationToken)
    {
        string? ffprobe = FindSibling(ffmpeg, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
        if (ffprobe is null) return 16;
        ProcessResult result = await RunAsync(ffprobe,
            new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=bits_per_raw_sample,bits_per_sample,sample_fmt", "-of", "json", source },
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) return 16;
        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        JsonElement streams = document.RootElement.GetProperty("streams");
        if (streams.GetArrayLength() == 0) return 16;
        JsonElement stream = streams[0];
        int bits = ReadInt(stream, "bits_per_raw_sample", ReadInt(stream, "bits_per_sample", 0));
        if (bits <= 0 && stream.TryGetProperty("sample_fmt", out JsonElement value))
        {
            string sampleFormat = value.GetString() ?? "";
            bits = sampleFormat.Contains("64", StringComparison.Ordinal) ? 32 :
                sampleFormat.Contains("32", StringComparison.Ordinal) ? 32 :
                sampleFormat.Contains("24", StringComparison.Ordinal) ? 24 : 16;
        }
        return bits <= 16 ? 16 : bits <= 24 ? 24 : 32;
    }

    private static int ReadInt(JsonElement element, string property, int fallback)
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

    private static async Task<string> PcmMd5Async(
        string ffmpeg, string source, int bits, CancellationToken cancellationToken)
    {
        string codec = bits <= 16 ? "pcm_s16le" : bits <= 24 ? "pcm_s24le" : "pcm_s32le";
        ProcessResult result = await RunAsync(ffmpeg,
            new[] { "-v", "error", "-i", source, "-map", "0:a:0", "-c:a", codec, "-f", "md5", "-" },
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidDataException("PCM MD5 failed: " + result.CombinedOutput.Trim());
        return result.StandardOutput.Trim();
    }

    private static async Task<ProcessResult> RunAsync(
        string executable, IEnumerable<string> arguments, CancellationToken cancellationToken)
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
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start " + executable);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static IEnumerable<string> CandidatePaths(string configured)
    {
        string fileName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        if (!string.IsNullOrWhiteSpace(configured)) yield return Path.GetFullPath(configured);
        string app = AppContext.BaseDirectory;
        yield return Path.Combine(app, fileName);
        yield return Path.Combine(app, "tools", "ffmpeg", "bin", fileName);
        if (OperatingSystem.IsMacOS())
        {
            yield return "/opt/homebrew/bin/ffmpeg";
            yield return "/usr/local/bin/ffmpeg";
        }
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (path is null) yield break;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return Path.Combine(directory.Trim(), fileName);
    }

    private static string? FindSibling(string executable, string fileName)
    {
        string sibling = Path.Combine(Path.GetDirectoryName(executable) ?? "", fileName);
        if (File.Exists(sibling)) return sibling;
        string? path = Environment.GetEnvironmentVariable("PATH");
        return path?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory.Trim(), fileName)).FirstOrDefault(File.Exists);
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
    {
        public string CombinedOutput => StandardOutput + Environment.NewLine + StandardError;
    }
}
