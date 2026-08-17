using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace MFlacDrop;

internal static class ToolManager
{
    public static string? FindFfmpeg(string configuredPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath)) candidates.Add(configuredPath);
        if (FfmpegManager.IsManagedInstallValid()) candidates.Add(AppInfo.ManagedFfmpegExe);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe"));
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));
        candidates.Add(@"C:\ffmpeg\bin\ffmpeg.exe");
        candidates.Add(@"C:\Program Files\ffmpeg\bin\ffmpeg.exe");
        foreach (var segment in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (!string.IsNullOrWhiteSpace(segment)) candidates.Add(Path.Combine(segment.Trim('"'), "ffmpeg.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    public static Task InstallFfmpegAsync(IProgress<(int percent, string status)> progress, CancellationToken ct) =>
        FfmpegManager.InstallAsync(progress, ct);

    public static Task InstallFfmpegFromZipAsync(
        string sourceZip,
        IProgress<(int percent, string status)> progress,
        CancellationToken ct) => FfmpegManager.InstallFromZipAsync(sourceZip, progress, ct);

    public static bool IsDecryptorValid() => File.Exists(AppInfo.DecryptorExe) &&
        string.Equals(Sha256(AppInfo.DecryptorExe), AppInfo.DecryptorExeSha256, StringComparison.OrdinalIgnoreCase);

    public static bool IsTrustedDecryptor(string path) => File.Exists(path) &&
        string.Equals(Sha256(path), AppInfo.DecryptorExeSha256, StringComparison.OrdinalIgnoreCase);

    public static async Task InstallDecryptorAsync(IProgress<(int percent, string status)> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(AppInfo.ToolsDir);
        string zipPath = Path.Combine(AppInfo.ToolsDir, "qqmusic_des.zip.download");
        string? extractDir = null;
        string installTemp = Path.Combine(AppInfo.ToolsDir, "qqmusic_des.exe.installing");
        const long MaxDownloadBytes = 128L * 1024 * 1024;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("MusicDrop3", "3.0-experimental.3"));
            using var response = await client.GetAsync(AppInfo.DecryptorZipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? -1;
            if (total > MaxDownloadBytes)
                throw new InvalidDataException("解密组件下载大小异常，已拒绝下载。");
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            await using (var output = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[1024 * 128];
                long read = 0;
                while (true)
                {
                    int n = await input.ReadAsync(buffer, ct);
                    if (n == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (read > MaxDownloadBytes)
                        throw new InvalidDataException("解密组件下载超过安全大小限制，已中止。");
                    int percent = total > 0 ? (int)(read * 100 / total) : 0;
                    progress.Report((percent, $"正在下载解密组件… {percent}%"));
                }
                await output.FlushAsync(ct);
            }
            if (!string.Equals(Sha256(zipPath), AppInfo.DecryptorZipSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("下载文件的 SHA-256 与 GitHub Release 公布值不一致，已拒绝安装。");

            extractDir = Path.Combine(AppInfo.ToolsDir, "extract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir);
            string sourceExe = Directory.GetFiles(extractDir, "qqmusic_des.exe", SearchOption.AllDirectories).Single();
            if (!string.Equals(Sha256(sourceExe), AppInfo.DecryptorExeSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("解压后的程序 SHA-256 校验失败，已拒绝安装。");
            File.Copy(sourceExe, installTemp, overwrite: true);
            if (!string.Equals(Sha256(installTemp), AppInfo.DecryptorExeSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装临时文件 SHA-256 校验失败，已拒绝安装。");
            File.Move(installTemp, AppInfo.DecryptorExe, overwrite: true);
            progress.Report((100, "解密组件已安装并通过 SHA-256 校验"));
        }
        finally
        {
            try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            try { if (File.Exists(installTemp)) File.Delete(installTemp); } catch { }
            try { if (extractDir is not null && Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true); } catch { }
        }
    }

    public static Task InstallDecryptorFromZipAsync(
        string sourceZip,
        IProgress<(int percent, string status)> progress,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(sourceZip)) throw new FileNotFoundException("找不到所选 ZIP。", sourceZip);
            if (!string.Equals(Sha256(sourceZip), AppInfo.DecryptorZipSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("所选 ZIP 的 SHA-256 与固定 GitHub Release 不一致，已拒绝安装。");

            Directory.CreateDirectory(AppInfo.ToolsDir);
            string extractDir = Path.Combine(AppInfo.ToolsDir, "extract-" + Guid.NewGuid().ToString("N"));
            string installTemp = Path.Combine(AppInfo.ToolsDir, "qqmusic_des.exe.installing");
            try
            {
                progress.Report((35, "ZIP SHA-256 已通过，正在解压…"));
                Directory.CreateDirectory(extractDir);
                ZipFile.ExtractToDirectory(sourceZip, extractDir);
                ct.ThrowIfCancellationRequested();
                string sourceExe = Directory.GetFiles(extractDir, "qqmusic_des.exe", SearchOption.AllDirectories).Single();
                if (!string.Equals(Sha256(sourceExe), AppInfo.DecryptorExeSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("ZIP 内 EXE 的 SHA-256 不一致，已拒绝安装。");
                File.Copy(sourceExe, installTemp, overwrite: true);
                if (!string.Equals(Sha256(installTemp), AppInfo.DecryptorExeSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装临时文件 SHA-256 校验失败，已拒绝安装。");
                File.Move(installTemp, AppInfo.DecryptorExe, overwrite: true);
                progress.Report((100, "本地解密组件已安装并通过双重 SHA-256 校验"));
            }
            finally
            {
                try { if (File.Exists(installTemp)) File.Delete(installTemp); } catch { }
                try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true); } catch { }
            }
        }, ct);
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
