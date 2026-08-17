using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace MusicDrop.Desktop;

public sealed partial class MainWindow : Window
{
    private static readonly string[] SupportedSuffixes =
    {
        ".kgm.flac", ".vpr.flac", ".mflac0", ".mflac1", ".mflaca", ".mflach", ".mflacl",
        ".mflacm", ".mflac", ".mgg0", ".mgg1", ".mgga", ".mggh", ".mggl", ".mggm", ".mgg",
        ".qmcflac", ".qmcogg", ".qmc0", ".qmc2", ".qmc3", ".qmc4", ".qmc6", ".qmc8",
        ".ncm", ".kwm", ".kw", ".kgm", ".kgma", ".vpr", ".kgg", ".tm0", ".tm2", ".tm3", ".tm6",
        ".xm", ".x2m", ".x3m", ".flac", ".wav", ".mp3", ".ogg", ".m4a", ".mp4", ".m4b",
        ".aac", ".opus", ".ape", ".wma", ".aiff", ".aif", ".dsf", ".dff",
    };

    private readonly HashSet<string> paths = new(OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private Process? conversionProcess;
    private bool chinese = true;

    public ObservableCollection<QueueItem> Queue { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        chinese = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        OutputBox.Text = Path.Combine(string.IsNullOrWhiteSpace(music) ? Environment.CurrentDirectory : music,
            "MusicDrop Output");
        DragDrop.SetAllowDrop(DropPanel, true);
        DropPanel.AddHandler(DragDrop.DropEvent, OnDrop);
        DropPanel.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        Closing += (_, _) => StopConversion();
        ApplyLanguage();
        UpdateEngineStatus();
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        IEnumerable<IStorageItem>? items = e.DataTransfer.TryGetFiles();
        if (items is null) return;
        await AddStorageItemsAsync(items);
    }

    private async void OnAddClick(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = C("添加音频文件", "Add audio files"),
            AllowMultiple = true,
            FileTypeFilter = new[] { FilePickerFileTypes.All },
        });
        await AddStorageItemsAsync(files);
    }

    private async Task AddStorageItemsAsync(IEnumerable<IStorageItem> items)
    {
        foreach (IStorageItem item in items)
        {
            string? path = item.TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) continue;
            await AddPathAsync(path);
        }
        SetStatus(C($"已加入 {Queue.Count} 个文件", $"{Queue.Count} file(s) queued"));
    }

    private async Task AddPathAsync(string path)
    {
        if (File.Exists(path))
        {
            if (IsSupported(path) && paths.Add(Path.GetFullPath(path)))
            {
                var info = new FileInfo(path);
                Queue.Add(new(info.FullName, info.Name, info.DirectoryName ?? "", FormatBytes(info.Length)));
            }
            return;
        }
        if (!Directory.Exists(path)) return;
        await Task.Run(() =>
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (!IsSupported(file)) continue;
                string full = Path.GetFullPath(file);
                Dispatcher.UIThread.Post(() =>
                {
                    if (!paths.Add(full)) return;
                    var info = new FileInfo(full);
                    Queue.Add(new(full, info.Name, info.DirectoryName ?? "", FormatBytes(info.Length)));
                });
            }
        });
    }

    private void OnClearClick(object? sender, RoutedEventArgs e)
    {
        if (conversionProcess is not null) return;
        Queue.Clear();
        paths.Clear();
        Progress.Value = 0;
        SetStatus(C("等待文件", "Waiting for files"));
    }

    private async void OnChooseOutputClick(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = C("选择输出目录", "Choose output folder"),
            AllowMultiple = false,
        });
        string? path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) OutputBox.Text = path;
    }

    private async void OnChooseKeySourceClick(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = C("选择本地密钥文件", "Choose local key source"),
            AllowMultiple = false,
            FileTypeFilter = new[] { FilePickerFileTypes.All },
        });
        string? path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        if ((sender as Button)?.Tag?.ToString() == "qmc") QmcKeyBox.Text = path;
        else KugouDbBox.Text = path;
    }

    private async void OnStartClick(object? sender, RoutedEventArgs e)
    {
        if (conversionProcess is not null || Queue.Count == 0) return;
        string? cli = FindCli();
        if (cli is null)
        {
            SetStatus(C("缺少 musicdrop-cli，请使用完整发行包。", "musicdrop-cli is missing; use a Full package."));
            return;
        }
        string output = OutputBox.Text?.Trim() ?? "";
        if (output.Length == 0) return;
        string manifest = Path.Combine(Path.GetTempPath(), "MusicDrop3", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        try
        {
            await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(Queue.Select(item => item.Path)));
            var start = new ProcessStartInfo
            {
                FileName = cli,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("--input-manifest"); start.ArgumentList.Add(manifest);
            start.ArgumentList.Add("--output"); start.ArgumentList.Add(output);
            start.ArgumentList.Add("--format"); start.ArgumentList.Add(SelectedFormat());
            start.ArgumentList.Add("--lang"); start.ArgumentList.Add(chinese ? "zh" : "en");
            start.ArgumentList.Add("--jobs"); start.ArgumentList.Add(Math.Clamp(Environment.ProcessorCount / 2, 1, 4).ToString());
            AddIfFile(start, "--qmc-ekey-file", QmcKeyBox.Text);
            AddIfFile(start, "--kugou-db", KugouDbBox.Text);
            string? ffmpeg = FindBundledFfmpeg();
            if (ffmpeg is not null) { start.ArgumentList.Add("--ffmpeg"); start.ArgumentList.Add(ffmpeg); }

            conversionProcess = new Process { StartInfo = start, EnableRaisingEvents = true };
            conversionProcess.OutputDataReceived += OnProcessLine;
            conversionProcess.ErrorDataReceived += OnProcessLine;
            StartButton.IsEnabled = false;
            CancelButton.IsVisible = true;
            Progress.IsIndeterminate = true;
            SetStatus(C("严格预检与转换中…", "Strict preflight and conversion in progress…"));
            conversionProcess.Start();
            conversionProcess.BeginOutputReadLine();
            conversionProcess.BeginErrorReadLine();
            await conversionProcess.WaitForExitAsync();
            int exitCode = conversionProcess.ExitCode;
            SetStatus(exitCode == 0 ? C("全部转换完成", "All conversions completed") :
                C($"转换停止（代码 {exitCode}）", $"Conversion stopped (code {exitCode})"));
            Progress.IsIndeterminate = false;
            Progress.Value = exitCode == 0 ? 100 : Progress.Value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus(ex.Message);
        }
        finally
        {
            conversionProcess?.Dispose();
            conversionProcess = null;
            StartButton.IsEnabled = true;
            CancelButton.IsVisible = false;
            Progress.IsIndeterminate = false;
            try { File.Delete(manifest); } catch { }
        }
    }

    private void OnProcessLine(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;
        Dispatcher.UIThread.Post(() => SetStatus(e.Data));
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        StopConversion();
        SetStatus(C("已请求取消", "Cancellation requested"));
    }

    private void StopConversion()
    {
        try { if (conversionProcess is { HasExited: false }) conversionProcess.Kill(entireProcessTree: true); }
        catch { }
    }

    private void OnLanguageClick(object? sender, RoutedEventArgs e)
    {
        chinese = !chinese;
        ApplyLanguage();
        if (conversionProcess is null) UpdateEngineStatus();
    }

    private void ApplyLanguage()
    {
        LanguageButton.Content = chinese ? "EN" : "中文";
        HeroSubtitle.Text = C("把复杂留给核心，把转换留给一次拖放。", "Complexity in the core. Conversion in one drop.");
        OutputLabel.Text = C("输出目录", "OUTPUT FOLDER");
        FormatLabel.Text = C("输出格式", "OUTPUT FORMAT");
        StartButton.Content = C("开始转换", "Convert");
        QueueTitle.Text = C("转换队列", "Conversion queue");
        DropHint.Text = C("拖入文件或文件夹，也可以点击添加", "Drop files or folders, or add them manually");
        AddButton.Content = C("＋ 添加", "+ Add");
        ClearButton.Content = C("清空", "Clear");
        SafetyNote.Text = C("本地处理 · 不上传 · 不删除源文件 · 正式输出前严格预检",
            "Local only · No upload · Sources preserved · Strict preflight");
        EngineTitle.Text = C("转换引擎", "Conversion engine");
        CancelButton.Content = C("取消", "Cancel");
        AdvancedExpander.Header = C("本地密钥兼容（可选）", "Local key sources (optional)");
        QmcKeyLabel.Text = C("QMC EKey 映射文件", "QMC EKey keyring");
        KugouDbLabel.Text = C("酷狗 KGMusicV3.db", "Kugou KGMusicV3.db");
        CoverageTitle.Text = C("离线覆盖", "Offline coverage");
        BoundaryText.Text = C("缺少文件专属密钥时会准确停止，不伪装成功。",
            "Missing per-file keys stop accurately; encrypted output is never reported as success.");
    }

    private void UpdateEngineStatus()
    {
        string? cli = FindCli();
        string? ffmpeg = FindBundledFfmpeg();
        SetStatus(cli is null
            ? C("开发预览：未找到 CLI 核心", "Developer preview: CLI core not found")
            : ffmpeg is null
                ? C("离线解码就绪；转码需要 FFmpeg", "Offline decoding ready; FFmpeg required for transcoding")
                : C("完整引擎就绪", "Full engine ready"));
    }

    private void SetStatus(string value) => EngineStatus.Text = value;
    private string C(string zh, string en) => chinese ? zh : en;
    private string SelectedFormat() => (FormatBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ORIGINAL";
    private static bool IsSupported(string path) => SupportedSuffixes.Any(suffix =>
        path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));

    private static void AddIfFile(ProcessStartInfo start, string option, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        start.ArgumentList.Add(option);
        start.ArgumentList.Add(Path.GetFullPath(path));
    }

    private static string? FindCli()
    {
        string[] names = OperatingSystem.IsWindows()
            ? new[] { "musicdrop-cli.exe", "musicdrop.exe" }
            : new[] { "musicdrop-cli", "musicdrop" };
        foreach (string name in names)
        {
            string direct = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(direct)) return direct;
            string resources = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", name));
            if (File.Exists(resources)) return resources;
        }
        return null;
    }

    private static string? FindBundledFfmpeg()
    {
        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        string[] candidates =
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", name),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "ffmpeg", "bin", name)),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:F2} GiB",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024:F1} MiB",
        >= 1024 => $"{bytes / 1024d:F1} KiB",
        _ => bytes + " B",
    };

    public sealed record QueueItem(string Path, string Name, string Directory, string Size);
}
