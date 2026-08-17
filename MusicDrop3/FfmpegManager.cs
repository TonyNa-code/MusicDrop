using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;

namespace MFlacDrop;

internal static class FfmpegManager
{
    private const long MaxArchiveBytes = 128L * 1024 * 1024;
    private const long MaxExtractedBytes = 768L * 1024 * 1024;
    private const int MaxArchiveEntries = 4096;
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    private static readonly IReadOnlyDictionary<string, string> RequiredFiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bin/ffmpeg.exe"] = "86a84607db881c93ac23ec8216b454e05ca8ae035ee8209fc2a9b10a845c2c84",
            ["bin/ffprobe.exe"] = "8e174683e435b089d7a9942afec5019e30ae6c550fcabfca3f917beb0768f7a6",
            ["bin/avcodec-62.dll"] = "cc91ca4fc909f3d5a512e5b0d50a3d161305e005ca7febe969b5737acaef2475",
            ["bin/avdevice-62.dll"] = "2a229adf099eb360aad5bdda24a7f3d1a9d151db0e28365b6f428277360c320f",
            ["bin/avfilter-11.dll"] = "e0d301cf78679caf8337a0babde8879924227a892e2e08abe04e9ec88bb9c351",
            ["bin/avformat-62.dll"] = "2fbd044d2a910035032d83dfd81d0f7fe442b73bea56341ccc171c941c62eb91",
            ["bin/avutil-60.dll"] = "fd951227b0d1b574ed964d44ccca59422be1a821b67820600a4ac0a1b558e95a",
            ["bin/swresample-6.dll"] = "81d46648a06852f7123bc05501ec8c12bc396ad6f35b9ef2130ff9e3cadf80e5",
            ["bin/swscale-9.dll"] = "6f1214e30b4ebcef4468ff05954413c36ec83e4c8a0ed3dc7c6a04d42c26b0bd",
            ["LICENSE.txt"] = "da7eabb7bafdf7d3ae5e9f223aa5bdc1eece45ac569dc21b3b037520b4464768",
        };

    public static bool IsManagedInstallValid()
    {
        try
        {
            string marker = Path.Combine(AppInfo.ManagedFfmpegDir, "MUSICDROP-FFMPEG-MANIFEST.json");
            return File.Exists(marker) && ValidateInstalledFileHashes(AppInfo.ManagedFfmpegDir, throwOnError: false);
        }
        catch
        {
            return false;
        }
    }

    public static async Task InstallAsync(IProgress<(int percent, string status)> progress, CancellationToken ct)
    {
        await InstallGate.WaitAsync(ct);
        try
        {
            if (IsManagedInstallValid())
            {
                progress.Report((100, "FFmpeg 已安装且校验通过"));
                return;
            }

            Directory.CreateDirectory(AppInfo.ToolsDir);
            string archive = Path.Combine(AppInfo.ToolsDir, AppInfo.FfmpegArchiveName + ".download");
            try
            {
                progress.Report((1, "正在连接固定 FFmpeg Release…"));
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MusicDrop", "3.0-community.1"));
                using HttpResponseMessage response = await client.GetAsync(
                    AppInfo.FfmpegArchiveUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1;
                if (total > MaxArchiveBytes)
                    throw new InvalidDataException("FFmpeg 下载大小异常，已拒绝下载。");

                await using Stream input = await response.Content.ReadAsStreamAsync(ct);
                await using (var output = new FileStream(archive, FileMode.Create, FileAccess.Write, FileShare.None,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    byte[] buffer = new byte[128 * 1024];
                    long received = 0;
                    while (true)
                    {
                        int count = await input.ReadAsync(buffer, ct);
                        if (count == 0) break;
                        received += count;
                        if (received > MaxArchiveBytes)
                            throw new InvalidDataException("FFmpeg 下载超过安全大小限制，已中止。");
                        await output.WriteAsync(buffer.AsMemory(0, count), ct);
                        int percent = total > 0 ? 2 + (int)Math.Min(63, received * 63 / total) : 2;
                        progress.Report((percent, total > 0
                            ? $"正在下载 FFmpeg… {received / 1024 / 1024} / {total / 1024 / 1024} MB"
                            : $"正在下载 FFmpeg… {received / 1024 / 1024} MB"));
                    }
                    await output.FlushAsync(ct);
                }

                await InstallFromZipCoreAsync(archive, progress, ct);
            }
            finally
            {
                TryDeleteFile(archive);
            }
        }
        finally
        {
            InstallGate.Release();
        }
    }

    public static async Task InstallFromZipAsync(
        string sourceZip,
        IProgress<(int percent, string status)> progress,
        CancellationToken ct)
    {
        await InstallGate.WaitAsync(ct);
        try
        {
            await InstallFromZipCoreAsync(sourceZip, progress, ct);
        }
        finally
        {
            InstallGate.Release();
        }
    }

    private static async Task InstallFromZipCoreAsync(
        string sourceZip,
        IProgress<(int percent, string status)> progress,
        CancellationToken ct)
    {
        if (!File.Exists(sourceZip)) throw new FileNotFoundException("找不到 FFmpeg ZIP。", sourceZip);
        var info = new FileInfo(sourceZip);
        if (info.Length <= 0 || info.Length > MaxArchiveBytes)
            throw new InvalidDataException("FFmpeg ZIP 大小异常，已拒绝安装。");

        progress.Report((68, "正在核对 FFmpeg 压缩包 SHA-256…"));
        string archiveHash = await Task.Run(() => ToolManager.Sha256(sourceZip), ct);
        if (!string.Equals(archiveHash, AppInfo.FfmpegArchiveSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("FFmpeg ZIP 的 SHA-256 与固定 Release 不一致，已拒绝安装。");

        string parent = Path.Combine(AppInfo.ToolsDir, "ffmpeg");
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, ".installing-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(parent, ".backup-" + Guid.NewGuid().ToString("N"));
        try
        {
            progress.Report((72, "压缩包校验通过，正在安全解压…"));
            Directory.CreateDirectory(staging);
            await Task.Run(() => ExtractRequiredFiles(sourceZip, staging, ct), ct);

            progress.Report((82, "正在核对 FFmpeg 程序与 DLL…"));
            ValidateInstalledFileHashes(staging, throwOnError: true);
            await ValidateExecutableCapabilitiesAsync(staging, ct);

            string manifest = JsonSerializer.Serialize(new
            {
                product = "MusicDrop",
                build = AppInfo.FfmpegBuildId,
                archive = AppInfo.FfmpegArchiveName,
                archiveSha256 = AppInfo.FfmpegArchiveSha256,
                installedAtUtc = DateTimeOffset.UtcNow,
                source = AppInfo.FfmpegSourceUrl,
            }, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(Path.Combine(staging, "MUSICDROP-FFMPEG-MANIFEST.json"), manifest, ct);

            progress.Report((96, "正在完成当前用户安装…"));
            if (Directory.Exists(AppInfo.ManagedFfmpegDir))
                Directory.Move(AppInfo.ManagedFfmpegDir, backup);
            try
            {
                Directory.Move(staging, AppInfo.ManagedFfmpegDir);
            }
            catch
            {
                if (!Directory.Exists(AppInfo.ManagedFfmpegDir) && Directory.Exists(backup))
                    Directory.Move(backup, AppInfo.ManagedFfmpegDir);
                throw;
            }
            TryDeleteDirectory(backup);
            if (!IsManagedInstallValid())
                throw new InvalidDataException("FFmpeg 安装后的最终完整性检查失败。");
            progress.Report((100, "FFmpeg 已安装，可直接开始转换"));
        }
        finally
        {
            TryDeleteDirectory(staging);
            TryDeleteDirectory(backup);
        }
    }

    internal static void ValidateArchiveSafetyForTests(string sourceZip)
    {
        using ZipArchive archive = ZipFile.OpenRead(sourceZip);
        _ = InspectArchive(archive);
    }

    private static Dictionary<string, ZipArchiveEntry> InspectArchive(ZipArchive archive)
    {
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxArchiveEntries)
            throw new InvalidDataException("FFmpeg ZIP 的条目数量异常。");

        long totalLength = 0;
        var required = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        string prefix = AppInfo.FfmpegArchiveRoot + "/";
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string normalized = entry.FullName.Replace('\\', '/');
            ValidateEntryName(normalized);
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw new InvalidDataException("FFmpeg ZIP 含符号链接，已拒绝安装。");
            if (entry.Length < 0 || entry.Length > MaxExtractedBytes)
                throw new InvalidDataException("FFmpeg ZIP 含异常大小的条目。");
            totalLength = checked(totalLength + entry.Length);
            if (totalLength > MaxExtractedBytes)
                throw new InvalidDataException("FFmpeg ZIP 解压后体积超过安全限制。");

            if (!normalized.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string relative = normalized[prefix.Length..];
            if (!RequiredFiles.ContainsKey(relative)) continue;
            if (!required.TryAdd(relative, entry))
                throw new InvalidDataException("FFmpeg ZIP 含重复的必要文件。");
        }

        string[] missing = RequiredFiles.Keys.Where(key => !required.ContainsKey(key)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException("FFmpeg ZIP 缺少必要文件：" + string.Join(", ", missing));
        return required;
    }

    private static void ValidateEntryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOf('\0') >= 0 ||
            name.StartsWith("/", StringComparison.Ordinal) ||
            Path.IsPathFullyQualified(name) ||
            name.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(part => part is "." or ".."))
            throw new InvalidDataException("FFmpeg ZIP 含不安全路径，已拒绝安装。");
    }

    private static void ExtractRequiredFiles(string sourceZip, string staging, CancellationToken ct)
    {
        using ZipArchive archive = ZipFile.OpenRead(sourceZip);
        Dictionary<string, ZipArchiveEntry> entries = InspectArchive(archive);
        string stagingFull = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach ((string relative, ZipArchiveEntry entry) in entries)
        {
            ct.ThrowIfCancellationRequested();
            string destination = Path.GetFullPath(Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!destination.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("FFmpeg ZIP 解压路径越界，已拒绝安装。");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using Stream input = entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[128 * 1024];
            long written = 0;
            while (true)
            {
                int count = input.Read(buffer, 0, buffer.Length);
                if (count == 0) break;
                written = checked(written + count);
                if (written > entry.Length || written > MaxExtractedBytes)
                    throw new InvalidDataException("FFmpeg ZIP 条目解压大小异常。");
                output.Write(buffer, 0, count);
                ct.ThrowIfCancellationRequested();
            }
            if (written != entry.Length)
                throw new InvalidDataException("FFmpeg ZIP 条目长度不一致。");
        }
    }

    private static bool ValidateInstalledFileHashes(string root, bool throwOnError)
    {
        foreach ((string relative, string expectedHash) in RequiredFiles)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path) || !string.Equals(ToolManager.Sha256(path), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                if (throwOnError)
                    throw new InvalidDataException("FFmpeg 文件完整性检查失败：" + relative);
                return false;
            }
        }
        return true;
    }

    private static async Task ValidateExecutableCapabilitiesAsync(string root, CancellationToken ct)
    {
        string executable = Path.Combine(root, "bin", "ffmpeg.exe");
        string version = await RunFfmpegAsync(executable, new[] { "-version" }, ct);
        string[] requiredConfiguration =
        {
            AppInfo.FfmpegVersionMarker,
            "--enable-version3",
            "--enable-shared",
            "--enable-libmp3lame",
            "--enable-libvorbis",
        };
        if (requiredConfiguration.Any(value => !version.Contains(value, StringComparison.Ordinal)) ||
            version.Contains("--enable-gpl", StringComparison.Ordinal))
            throw new InvalidDataException("FFmpeg 版本或 LGPL 共享构建配置检查失败。");

        string encoders = await RunFfmpegAsync(executable, new[] { "-hide_banner", "-encoders" }, ct);
        if (!encoders.Contains("libmp3lame", StringComparison.Ordinal) ||
            !encoders.Contains("libvorbis", StringComparison.Ordinal) ||
            !encoders.Contains(" flac ", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("FFmpeg 缺少 FLAC、MP3 或 OGG 必要编码器。");
    }

    private static async Task<string> RunFfmpegAsync(string executable, IEnumerable<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动 FFmpeg 完整性检查。");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        string output = (await stdout) + Environment.NewLine + (await stderr);
        if (process.ExitCode != 0)
            throw new InvalidDataException("FFmpeg 自检退出码异常：" + process.ExitCode);
        return output;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
