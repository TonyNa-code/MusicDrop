using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace MFlacDrop;

internal sealed record MusicClientDiscovery(
    bool IsRunning,
    string? ExecutablePath,
    bool IsTrusted,
    string Status,
    string? Detail = null);

internal sealed record MusicClientReadyResult(
    bool WasAlreadyRunning,
    bool WasLaunched,
    string? ExecutablePath,
    string Status);

internal sealed class MusicClientException : InvalidOperationException
{
    public string Code { get; }

    public MusicClientException(string code, string message, Exception? innerException = null)
        : base(message, innerException) => Code = code;
}

/// <summary>
/// Discovers and starts only an authentic Tencent-signed QQMusic.exe.  It never
/// elevates, terminates the client, reads account data or launches a client for
/// formats which can be handled offline.
/// </summary>
internal static class MusicClientManager
{
    private const string QqProcessName = "QQMusic";
    private const string QqExecutableName = "QQMusic.exe";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    internal static bool IsQqMusicRunning() => IsProcessRunning(QqProcessName);

    internal static MusicClientDiscovery DiscoverQqMusic(string configuredPath = "")
    {
        if (IsQqMusicRunning())
        {
            string? runningPath = TryGetRunningExecutablePath();
            if (runningPath is null)
                return new(true, null, false, "无法验证运行中的 QQ 音乐",
                    "Windows 未允许读取进程路径。请确保 QQ 音乐与本工具由同一 Windows 用户和相同权限级别运行。");

            (bool valid, string detail) = ValidateQqMusicExecutable(runningPath);
            return valid
                ? new(true, runningPath, true, "QQ 音乐已运行")
                : new(true, runningPath, false, "运行中的 QQ 音乐未通过身份校验", detail);
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string? normalized = NormalizeCandidate(configuredPath);
            if (normalized is null)
                return new(false, null, false, "指定的 QQ 音乐路径无效", "路径不存在或目标不是 QQMusic.exe。");
            (bool valid, string detail) = ValidateQqMusicExecutable(normalized);
            return valid
                ? new(false, normalized, true, "已找到可信 QQ 音乐")
                : new(false, normalized, false, "指定的 QQ 音乐未通过身份校验", detail);
        }

        foreach (string candidate in EnumerateInstalledCandidates())
        {
            string? normalized = NormalizeCandidate(candidate);
            if (normalized is null) continue;
            (bool valid, _) = ValidateQqMusicExecutable(normalized);
            if (valid) return new(false, normalized, true, "已找到可信 QQ 音乐");
        }

        return new(false, null, false, "未找到 QQ 音乐",
            "可在“密钥与兼容模式”中手动选择官方 QQMusic.exe。");
    }

    internal static async Task<MusicClientReadyResult> EnsureQqMusicReadyAsync(
        string configuredPath,
        bool autoStart,
        TimeSpan timeout,
        Action<string> log,
        CancellationToken ct)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(timeout), "客户端就绪等待时间必须在 0 到 5 分钟之间。");

        MusicClientDiscovery discovery = DiscoverQqMusic(configuredPath);
        if (discovery.IsRunning)
        {
            if (!discovery.IsTrusted)
                throw new MusicClientException("client_identity_failed", discovery.Detail ?? discovery.Status);
            log("QQ 音乐客户端：已运行并通过身份校验");
            return new(true, false, discovery.ExecutablePath, "QQ 音乐已就绪");
        }

        if (!autoStart)
            throw new MusicClientException("client_not_running",
                "此批次包含缺少 EKey 的新版 QQ 文件，但 QQ 音乐未运行且自动启动已关闭。请启动 QQ 音乐后重试。");
        if (discovery.ExecutablePath is null)
            throw new MusicClientException("client_not_found", discovery.Detail ?? discovery.Status);
        if (!discovery.IsTrusted)
            throw new MusicClientException("client_identity_failed", discovery.Detail ?? discovery.Status);

        try
        {
            log("QQ 音乐客户端：正在启动已验证的官方程序");
            _ = Process.Start(new ProcessStartInfo
            {
                FileName = discovery.ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(discovery.ExecutablePath)!,
                UseShellExecute = true,
            }) ?? throw new InvalidOperationException("Windows 未返回已启动的进程。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new MusicClientException("client_launch_failed",
                "无法启动 QQ 音乐。程序不会自动提权；请手动启动 QQ 音乐并确保它与本工具由同一 Windows 用户运行。", ex);
        }

        DateTime deadline = DateTime.UtcNow + timeout;
        int stablePolls = 0;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (IsQqMusicRunning())
            {
                stablePolls++;
                if (stablePolls >= 4)
                {
                    log("QQ 音乐客户端：已启动并稳定运行");
                    return new(false, true, discovery.ExecutablePath, "QQ 音乐已自动启动并就绪");
                }
            }
            else
            {
                stablePolls = 0;
            }
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }

        throw new MusicClientException("client_ready_timeout",
            $"QQ 音乐在 {timeout.TotalSeconds:0} 秒内未稳定进入运行状态。请完成客户端自身的更新或登录提示后重试。");
    }

    internal static (bool IsValid, string Detail) ValidateQqMusicExecutable(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!File.Exists(fullPath)) return (false, "文件不存在。");
            if (!Path.GetFileName(fullPath).Equals(QqExecutableName, StringComparison.OrdinalIgnoreCase))
                return (false, "只允许选择 QQMusic.exe。");

            FileVersionInfo version = FileVersionInfo.GetVersionInfo(fullPath);
            if (!ContainsTencentIdentity(version.CompanyName) && !ContainsTencentIdentity(version.ProductName))
                return (false, "文件版本信息不是腾讯 QQ 音乐。");
            if (!AuthenticodeTrust.IsTrusted(fullPath))
                return (false, "Windows 无法验证该程序的 Authenticode 数字签名。");

#pragma warning disable SYSLIB0026, SYSLIB0057
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(fullPath));
#pragma warning restore SYSLIB0026, SYSLIB0057
            if (!ContainsTencentIdentity(signer.Subject))
                return (false, "数字签名者不是已识别的腾讯实体。");

            return (true, "腾讯文件信息与 Authenticode 数字签名均已验证。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or ArgumentException)
        {
            return (false, "无法验证程序身份：" + ex.Message);
        }
    }

    private static bool ContainsTencentIdentity(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("Tencent", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("腾讯", StringComparison.Ordinal));

    private static string? NormalizeCandidate(string candidate)
    {
        try
        {
            string value = candidate.Trim().Trim('"');
            int iconIndex = value.LastIndexOf(',');
            if (iconIndex > 0 && int.TryParse(value[(iconIndex + 1)..], out _)) value = value[..iconIndex].Trim('"');
            if (Directory.Exists(value)) value = Path.Combine(value, QqExecutableName);
            string fullPath = Path.GetFullPath(value);
            return File.Exists(fullPath) && Path.GetFileName(fullPath).Equals(QqExecutableName, StringComparison.OrdinalIgnoreCase)
                ? fullPath : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    private static string? TryGetRunningExecutablePath()
    {
        Process[] processes = Process.GetProcessesByName(QqProcessName);
        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    if (!process.HasExited && process.MainModule?.FileName is { Length: > 0 } path) return path;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
            return null;
        }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }

    private static bool IsProcessRunning(string processName)
    {
        Process[] processes = Process.GetProcessesByName(processName);
        try { return processes.Any(process => !process.HasExited); }
        catch { return processes.Length > 0; }
        finally
        {
            foreach (Process process in processes) process.Dispose();
        }
    }

    private static IEnumerable<string> EnumerateInstalledCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? value in EnumerateRegistryCandidates().Concat(EnumerateKnownLocations()))
        {
            if (!string.IsNullOrWhiteSpace(value) && seen.Add(value)) yield return value;
        }
    }

    private static IEnumerable<string?> EnumerateRegistryCandidates()
    {
        var results = new List<string?>();
        if (!OperatingSystem.IsWindows()) return results;
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            RegistryKey? baseKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                using (RegistryKey? appPath = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\QQMusic.exe"))
                    if (appPath?.GetValue(null) is string path) results.Add(path);

                using RegistryKey? uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (string subKeyName in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? entry = uninstall.OpenSubKey(subKeyName);
                    string? displayName = entry?.GetValue("DisplayName") as string;
                    string? publisher = entry?.GetValue("Publisher") as string;
                    if (!(displayName?.Contains("QQ音乐", StringComparison.OrdinalIgnoreCase) == true ||
                          displayName?.Contains("QQMusic", StringComparison.OrdinalIgnoreCase) == true)) continue;
                    if (!ContainsTencentIdentity(publisher) && !ContainsTencentIdentity(displayName)) continue;
                    if (entry?.GetValue("DisplayIcon") is string icon) results.Add(icon);
                    if (entry?.GetValue("InstallLocation") is string location) results.Add(location);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { }
            finally { baseKey?.Dispose(); }
        }
        return results;
    }

    private static IEnumerable<string> EnumerateKnownLocations()
    {
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (string root in roots.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(root, "Tencent", "QQMusic", QqExecutableName);
            yield return Path.Combine(root, "QQMusic", QqExecutableName);
        }
    }

    private static class AuthenticodeTrust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        internal static bool IsTrusted(string path)
        {
            IntPtr filePath = IntPtr.Zero;
            IntPtr fileInfoPointer = IntPtr.Zero;
            IntPtr trustDataPointer = IntPtr.Zero;
            try
            {
                filePath = Marshal.StringToCoTaskMemUni(path);
                var fileInfo = new WinTrustFileInfo
                {
                    Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                    FilePath = filePath,
                };
                fileInfoPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustFileInfo>());
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);

                var trustData = new WinTrustData
                {
                    Size = (uint)Marshal.SizeOf<WinTrustData>(),
                    UiChoice = 2,
                    RevocationChecks = 0,
                    UnionChoice = 1,
                    FileInfo = fileInfoPointer,
                    StateAction = 0,
                    ProviderFlags = 0x00000100 | 0x00001000,
                    UiContext = 0,
                };
                trustDataPointer = Marshal.AllocCoTaskMem(Marshal.SizeOf<WinTrustData>());
                Marshal.StructureToPtr(trustData, trustDataPointer, false);
                return WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, trustDataPointer) == 0;
            }
            finally
            {
                if (trustDataPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(trustDataPointer);
                if (fileInfoPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(fileInfoPointer);
                if (filePath != IntPtr.Zero) Marshal.FreeCoTaskMem(filePath);
            }
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern uint WinVerifyTrust(
            IntPtr windowHandle,
            [MarshalAs(UnmanagedType.LPStruct)] Guid actionId,
            IntPtr trustData);

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            public uint Size;
            public IntPtr FilePath;
            public IntPtr FileHandle;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint Size;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr FileInfo;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProviderFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }
    }
}
