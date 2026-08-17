namespace MFlacDrop;

internal static class AppInfo
{
    public const string AppName = "MusicDrop™ 3";
    public const string AppVersion = "3.1.0-preview.1";
    public const string WindowTitle = "MusicDrop™ 3.1 — Community Preview";
    public const string FfmpegBuildId = "btbn-n8.1.2-34-g9b6c8969e0-20260811-lgpl-shared";
    public const string FfmpegArchiveRoot = "ffmpeg-n8.1.2-34-g9b6c8969e0-win64-lgpl-shared-8.1";
    public const string FfmpegArchiveName = FfmpegArchiveRoot + ".zip";
    public const string FfmpegArchiveUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-08-11-13-11/" + FfmpegArchiveName;
    public const string FfmpegArchiveSha256 = "026f3ba22f0acf4fe58bf4da28a7eb64ffb107b270119684b91e4cace3b577aa";
    public const string FfmpegVersionMarker = "n8.1.2-34-g9b6c8969e0-20260811";
    public const string FfmpegProjectUrl = "https://ffmpeg.org/";
    public const string FfmpegBuildProjectUrl = "https://github.com/BtbN/FFmpeg-Builds";
    public const string FfmpegSourceUrl = "https://github.com/FFmpeg/FFmpeg/commit/9b6c8969e0";
    public const string DecryptorVersion = "v0.1.1";
    public const string DecryptorZipUrl = "https://github.com/luyikk/qqmusic_decrypt/releases/download/v0.1.1/qqmusic_des-x86_64-pc-windows-msvc-windows-latest.zip";
    public const string DecryptorZipSha256 = "c30962f5cce1cb5eb12e051445dde83fb5f45be23ae4e6e2dffa6ad1ac4847e4";
    public const string DecryptorExeSha256 = "b545b78730e341c7398852efbbd4ae2c5129f17baed58389618bbdcb9f51e5cb";
    public const string DecryptorProjectUrl = "https://github.com/luyikk/qqmusic_decrypt";

    public static string DataDir => Environment.GetEnvironmentVariable("MUSICDROP3_DATA_DIR") is { Length: > 0 } custom
        ? Path.GetFullPath(custom)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicDrop3");

    public static string ToolsDir => Path.Combine(DataDir, "tools");
    public static string ManagedFfmpegDir => Path.Combine(ToolsDir, "ffmpeg", FfmpegBuildId);
    public static string ManagedFfmpegExe => Path.Combine(ManagedFfmpegDir, "bin", "ffmpeg.exe");
    public static string ManagedFfprobeExe => Path.Combine(ManagedFfmpegDir, "bin", "ffprobe.exe");
    public static string DecryptorExe => Path.Combine(ToolsDir, "qqmusic_des.exe");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string EKeyCachePath => Path.Combine(DataDir, "ekey-cache.dpapi.json");
    public static string InstalledLicensePath => Path.Combine(DataDir, "buyer-license.json");
}
