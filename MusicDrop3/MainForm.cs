using System.Diagnostics;
using System.Drawing.Drawing2D;

namespace MFlacDrop;

internal sealed class MainForm : Form
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ListView _files = new();
    private readonly ComboBox _format = new();
    private readonly ComboBox _mp3Quality = new();
    private readonly TextBox _output = new();
    private readonly TextBox _ffmpeg = new();
    private readonly Button _convert = new();
    private readonly Button _cancel = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();
    private readonly Label _dropHint = new();
    private readonly RichTextBox _log = new();
    private readonly CheckBox _overwrite = new();
    private CancellationTokenSource? _cts;
    private bool _closeRequested;
    private int _preflightEpoch;

    internal IReadOnlyList<string> OutputFormats => _format.Items.Cast<string>().ToArray();

    public MainForm()
    {
        Text = AppInfo.WindowTitle;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 650);
        Size = new Size(980, 740);
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(245, 247, 251);
        AllowDrop = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        BuildUi();
        LoadSettings();
        RefreshEnvironmentStatus();

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        FormClosing += OnFormClosing;
    }

    private void BuildUi()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 82, BackColor = Color.FromArgb(28, 39, 65), Padding = new Padding(24, 14, 24, 12) };
        var logo = new PictureBox { Size = new Size(58, 58), Location = new Point(20, 11), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Transparent };
        try
        {
            using Stream? stream = typeof(MainForm).Assembly.GetManifestResourceStream("MFlacDrop.Assets.musicdrop-logo-1024.png");
            if (stream is not null) using (var bitmap = new Bitmap(stream)) logo.Image = new Bitmap(bitmap);
        }
        catch { }
        var title = new Label { Text = "MusicDrop™ 3", ForeColor = Color.White, Font = new Font(Font.FontFamily, 19F, FontStyle.Bold), AutoSize = true, Location = new Point(88, 11) };
        var subtitle = new Label { Text = "拖入文件 · 选择格式 · 点一下转换", ForeColor = Color.FromArgb(193, 204, 225), AutoSize = true, Location = new Point(91, 49) };
        header.Controls.Add(logo);
        header.Controls.Add(title);
        header.Controls.Add(subtitle);
        Controls.Add(header);

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 5 };
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 142));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
        Controls.Add(body);
        body.BringToFront();

        var filePanel = Card();
        filePanel.Padding = new Padding(12);
        body.Controls.Add(filePanel, 0, 0);

        var fileToolbar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 38, FlowDirection = FlowDirection.LeftToRight };
        fileToolbar.Controls.Add(ActionButton("添加文件", (_, _) => AddFilesDialog()));
        fileToolbar.Controls.Add(ActionButton("添加文件夹", (_, _) => AddFolderDialog()));
        fileToolbar.Controls.Add(ActionButton("移除所选", (_, _) => RemoveSelected()));
        fileToolbar.Controls.Add(ActionButton("清空", (_, _) => { _files.Items.Clear(); UpdateDropHint(); }));
        fileToolbar.Controls.Add(ActionButton("一键安装 FFmpeg", async (_, _) => await InstallFfmpegInteractivelyAsync(forcePrompt: true)));
        fileToolbar.Controls.Add(ActionButton("组件与许可", (_, _) => ShowAbout()));
        filePanel.Controls.Add(fileToolbar);

        _files.Dock = DockStyle.Fill;
        _files.View = View.Details;
        _files.FullRowSelect = true;
        _files.HideSelection = false;
        _files.AllowDrop = true;
        _files.Columns.Add("文件", 330);
        _files.Columns.Add("平台 / 格式", 190);
        _files.Columns.Add("解密后编码", 100);
        _files.Columns.Add("大小", 100, HorizontalAlignment.Right);
        _files.Columns.Add("状态", 190);
        _files.DragEnter += OnDragEnter;
        _files.DragDrop += OnDragDrop;
        filePanel.Controls.Add(_files);
        _files.BringToFront();

        _dropHint.Text = "把加密音乐或 FLAC / WAV / MP3 / OGG / M4A 批量拖到这里";
        _dropHint.ForeColor = Color.FromArgb(105, 116, 136);
        _dropHint.Font = new Font(Font.FontFamily, 12F);
        _dropHint.AutoSize = true;
        _dropHint.BackColor = Color.White;
        _dropHint.Anchor = AnchorStyles.None;
        _files.Controls.Add(_dropHint);
        _files.Resize += (_, _) => CenterDropHint();

        var settingsCard = Card();
        body.Controls.Add(settingsCard, 0, 1);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 14, 8), ColumnCount = 4, RowCount = 3 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        settingsCard.Controls.Add(grid);

        grid.Controls.Add(FieldLabel("输出格式"), 0, 0);
        _format.DropDownStyle = ComboBoxStyle.DropDownList;
        _format.Items.AddRange(new object[] { "原始格式", "FLAC", "WAV", "MP3", "OGG" });
        _format.SelectedIndexChanged += (_, _) => _mp3Quality.Enabled = _format.Text == "MP3";
        grid.Controls.Add(_format, 1, 0);
        grid.Controls.Add(FieldLabel("MP3 质量"), 2, 0);
        _mp3Quality.DropDownStyle = ComboBoxStyle.DropDownList;
        _mp3Quality.Items.AddRange(new object[] { "V0（约 245 kbps）", "320 kbps CBR" });
        grid.Controls.Add(_mp3Quality, 3, 0);

        grid.Controls.Add(FieldLabel("输出目录"), 0, 1);
        _output.Dock = DockStyle.Fill;
        grid.Controls.Add(_output, 1, 1);
        var browseOutput = ActionButton("浏览…", (_, _) => BrowseOutput());
        grid.Controls.Add(browseOutput, 2, 1);
        _overwrite.Text = "覆盖同名文件";
        _overwrite.Dock = DockStyle.Fill;
        grid.Controls.Add(_overwrite, 3, 1);

        grid.Controls.Add(FieldLabel("转换引擎"), 0, 2);
        _ffmpeg.Dock = DockStyle.Fill;
        _ffmpeg.PlaceholderText = "自动检测，无需配置";
        grid.Controls.Add(_ffmpeg, 1, 2);
        grid.Controls.Add(ActionButton("选择…", (_, _) => BrowseFfmpeg()), 2, 2);
        var advanced = ActionButton("密钥与兼容模式…", (_, _) => ShowKeySettings());
        grid.Controls.Add(advanced, 3, 2);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 7, 0, 5) };
        _convert.Text = "开始转换";
        _convert.Width = 128;
        _convert.Height = 36;
        _convert.BackColor = Color.FromArgb(53, 110, 235);
        _convert.ForeColor = Color.White;
        _convert.FlatStyle = FlatStyle.Flat;
        _convert.FlatAppearance.BorderSize = 0;
        _convert.Click += async (_, _) => await StartConversionAsync();
        _cancel.Text = "取消";
        _cancel.Width = 88;
        _cancel.Height = 36;
        _cancel.Enabled = false;
        _cancel.Click += (_, _) => _cts?.Cancel();
        buttons.Controls.Add(_convert);
        buttons.Controls.Add(_cancel);
        body.Controls.Add(buttons, 0, 2);

        var progressPanel = new Panel { Dock = DockStyle.Fill };
        _status.Text = "就绪";
        _status.Dock = DockStyle.Top;
        _status.Height = 20;
        _status.ForeColor = Color.FromArgb(65, 75, 95);
        _progress.Dock = DockStyle.Bottom;
        _progress.Height = 16;
        progressPanel.Controls.Add(_status);
        progressPanel.Controls.Add(_progress);
        body.Controls.Add(progressPanel, 0, 3);

        _log.Dock = DockStyle.Fill;
        _log.ReadOnly = true;
        _log.BackColor = Color.FromArgb(29, 33, 42);
        _log.ForeColor = Color.FromArgb(218, 224, 235);
        _log.Font = new Font("Consolas", 9F);
        _log.BorderStyle = BorderStyle.None;
        body.Controls.Add(_log, 0, 4);
    }

    private static Panel Card() => new() { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0, 0, 0, 10) };
    private static Label FieldLabel(string text) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(58, 67, 84) };
    private static Button ActionButton(string text, EventHandler handler)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 30, FlatStyle = FlatStyle.Flat, BackColor = Color.White };
        button.FlatAppearance.BorderColor = Color.FromArgb(208, 214, 226);
        button.Click += handler;
        return button;
    }

    private void LoadSettings()
    {
        _output.Text = _settings.OutputDirectory;
        _ffmpeg.Text = ToolManager.FindFfmpeg(_settings.FfmpegPath) ?? _settings.FfmpegPath;
        _format.SelectedItem = _settings.OutputFormat;
        if (_format.SelectedIndex < 0) _format.SelectedIndex = 0;
        _mp3Quality.SelectedItem = _settings.Mp3Quality;
        if (_mp3Quality.SelectedIndex < 0) _mp3Quality.SelectedIndex = 0;
        UpdateDropHint();
    }

    private void SaveSettings()
    {
        _settings.OutputDirectory = _output.Text.Trim();
        _settings.FfmpegPath = _ffmpeg.Text.Trim();
        _settings.OutputFormat = _format.Text;
        _settings.Mp3Quality = _mp3Quality.Text;
        try { _settings.Save(); } catch { }
    }

    private void ShowKeySettings()
    {
        using var dialog = new KeySettingsDialog(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _settings.PlayerProcessDbPath = dialog.PlayerProcessDbPath;
        _settings.ImportedEKeyPath = dialog.ImportedEKeyPath;
        _settings.KugouDatabasePath = dialog.KugouDatabasePath;
        _settings.UseQqFallback = dialog.UseQqFallback;
        _settings.AutoStartRequiredClients = dialog.AutoStartRequiredClients;
        _settings.QqMusicExecutablePath = dialog.QqMusicExecutablePath;
        _settings.StrictBatchPreflight = dialog.StrictBatchPreflight;
        SaveSettings();
        AppendLog("密钥设置已更新：" +
            $"安全缓存={(File.Exists(AppInfo.EKeyCachePath) ? "有" : "无")} | " +
            $"导入文件={(File.Exists(_settings.ImportedEKeyPath) ? "已选择" : "无")} | " +
            $"QQ Android 密钥库={(File.Exists(_settings.PlayerProcessDbPath) ? "已选择" : "无")} | " +
            $"酷狗 KGG 数据库={(File.Exists(_settings.KugouDatabasePath) ? "已选择" : File.Exists(MusicDrop3.MultiPlatform.KugouDatabaseReader.DefaultDatabasePath) ? "已自动找到" : "无")} | " +
            $"QQ 兼容={(_settings.UseQqFallback ? "开启" : "关闭")} | " +
            $"自动启动={(_settings.AutoStartRequiredClients ? "开启" : "关闭")} | " +
            $"严格预检={(_settings.StrictBatchPreflight ? "开启" : "关闭")}");
        int epoch = Interlocked.Increment(ref _preflightEpoch);
        if (_files.Items.Count > 0) _ = PreflightItemsAsync(_files.Items.Cast<ListViewItem>().ToArray(), epoch);
    }

    private void RefreshEnvironmentStatus()
    {
        MusicClientDiscovery qq = MusicClientManager.DiscoverQqMusic(_settings.QqMusicExecutablePath);
        bool decryptor = ToolManager.IsDecryptorValid();
        string ff = ToolManager.FindFfmpeg(_ffmpeg.Text) ?? "未找到";
        string qqStatus = qq.IsRunning
            ? (qq.IsTrusted ? (decryptor ? "客户端与组件均就绪" : "客户端已运行，组件未安装") : qq.Status)
            : qq.ExecutablePath is not null && qq.IsTrusted
                ? (_settings.AutoStartRequiredClients ? "已找到，可按需自动启动" : "已找到，需手动启动")
                : qq.Status;
        AppendLog($"多平台离线核心：就绪 | QQ 安全缓存：{(File.Exists(AppInfo.EKeyCachePath) ? "有" : "无")} | " +
            $"QQ 可选兜底：{qqStatus} | FFmpeg：{ff}");
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
    }

    private void AddFilesDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "支持的音频|*.mflac;*.mflac0;*.mgg;*.mgg0;*.qmc0;*.qmc3;*.qmcflac;*.qmcogg;*.ncm;*.kgm;*.kgma;*.vpr;*.kgg;*.kgm.flac;*.vpr.flac;*.kwm;*.kw;*.flac;*.wav;*.mp3;*.ogg;*.m4a|所有文件|*.*",
            Multiselect = true,
            Title = "选择要转换的加密音频"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(dialog.FileNames);
    }

    private void AddFolderDialog()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择包含加密音频的文件夹", UseDescriptionForTitle = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) AddPaths(new[] { dialog.SelectedPath });
    }

    private async void AddPaths(IEnumerable<string> paths)
    {
        string[] snapshot = paths.ToArray();
        _status.Text = "正在扫描文件…";
        try
        {
            List<string> files = await Task.Run(() => EnumerateInputFiles(snapshot));
            AddFilesToList(files);
        }
        catch (Exception ex)
        {
            AppendLog("文件扫描失败：" + ex.Message);
        }
        finally
        {
            _status.Text = "就绪";
        }
    }

    private static List<string> EnumerateInputFiles(IEnumerable<string> paths)
    {
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (File.Exists(path) && AudioConverter.IsSupportedInput(path)) files.Add(path);
            else if (Directory.Exists(path)) EnumerateDirectorySafe(path, files);
        }
        return files;
    }

    private static void EnumerateDirectorySafe(string root, List<string> files)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            try
            {
                foreach (string file in Directory.EnumerateFiles(directory))
                    if (AudioConverter.IsSupportedInput(file)) files.Add(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            try
            {
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    try
                    {
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                            pending.Push(child);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private void AddFilesToList(IEnumerable<string> files)
    {
        var existing = _files.Items.Cast<ListViewItem>().Select(x => (string)x.Tag!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<ListViewItem>();
        foreach (string file in files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!existing.Add(file)) continue;
            var info = new FileInfo(file);
            var item = new ListViewItem(new[] { info.Name, "识别中…", "—", FormatBytes(info.Length), "正在安全预检" })
            {
                Tag = file,
                ToolTipText = file,
            };
            _files.Items.Add(item);
            added.Add(item);
        }
        UpdateDropHint();
        if (added.Count > 0) _ = PreflightItemsAsync(added, _preflightEpoch);
    }

    private async Task PreflightItemsAsync(IReadOnlyList<ListViewItem> items, int epoch)
    {
        using var gate = new SemaphoreSlim(3);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync();
            try
            {
                string path = (string)item.Tag!;
                FilePreflightResult result = await AudioConverter.PreflightAsync(
                    path,
                    _settings.PlayerProcessDbPath,
                    _settings.ImportedEKeyPath,
                    _settings.KugouDatabasePath,
                    CancellationToken.None,
                    _settings.UseQqFallback,
                    _settings.AutoStartRequiredClients,
                    _settings.QqMusicExecutablePath,
                    AppInfo.DecryptorExe);
                SafeUi(() =>
                {
                    if (epoch != _preflightEpoch || _cts is not null || item.ListView != _files || item.SubItems.Count < 5) return;
                    item.SubItems[1].Text = result.PlatformFormat;
                    item.SubItems[2].Text = result.AudioCodec;
                    item.SubItems[4].Text = result.Status;
                    if (!string.IsNullOrWhiteSpace(result.Detail))
                        item.ToolTipText = Path.GetFileName(path) + Environment.NewLine + result.Detail;
                });
            }
            catch (Exception ex)
            {
                SafeUi(() =>
                {
                    if (epoch != _preflightEpoch || _cts is not null || item.ListView != _files || item.SubItems.Count < 5) return;
                    item.SubItems[1].Text = "未知";
                    item.SubItems[2].Text = "—";
                    item.SubItems[4].Text = "预检失败";
                    item.ToolTipText = Path.GetFileName((string)item.Tag!) + Environment.NewLine + ex.Message;
                });
            }
            finally { gate.Release(); }
        }).ToArray();
        await Task.WhenAll(tasks);
    }

    private void RemoveSelected()
    {
        foreach (ListViewItem item in _files.SelectedItems) _files.Items.Remove(item);
        UpdateDropHint();
    }

    private void UpdateDropHint() { _dropHint.Visible = _files.Items.Count == 0; CenterDropHint(); }
    private void CenterDropHint() { _dropHint.Location = new Point(Math.Max(12, (_files.ClientSize.Width - _dropHint.Width) / 2), Math.Max(55, (_files.ClientSize.Height - _dropHint.Height) / 2)); }

    private void BrowseOutput()
    {
        using var dialog = new FolderBrowserDialog { Description = "选择转换后的文件保存位置", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(_output.Text) ? _output.Text : "" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _output.Text = dialog.SelectedPath;
    }

    private void BrowseFfmpeg()
    {
        using var dialog = new OpenFileDialog { Filter = "FFmpeg|ffmpeg.exe", Title = "选择 ffmpeg.exe" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _ffmpeg.Text = dialog.FileName;
    }

    private async Task<bool> InstallFfmpegInteractivelyAsync(bool forcePrompt)
    {
        if (FfmpegManager.IsManagedInstallValid() && !forcePrompt)
        {
            _ffmpeg.Text = AppInfo.ManagedFfmpegExe;
            return true;
        }

        string intro = FfmpegManager.IsManagedInstallValid()
            ? "MusicDrop 已有一份校验通过的 FFmpeg。是否从固定 Release 重新安装/修复？"
            : "首次转码需要 FFmpeg。MusicDrop 可以自动下载固定的 BtbN Windows x64 LGPL 共享构建（约 68 MB 下载、约 160 MB 安装）。\n\n无需管理员权限，也无需配置环境变量；下载后会校验 ZIP、程序和 DLL 的 SHA-256，并检查 LGPL 构建配置。是否一键安装？";
        if (MessageBox.Show(this, intro, "一键准备转换引擎", MessageBoxButtons.YesNo,
            MessageBoxIcon.Information) != DialogResult.Yes)
            return false;

        bool ownsBusyState = _cts is null;
        if (ownsBusyState)
        {
            _cts = new CancellationTokenSource();
            SetBusy(true);
        }
        CancellationToken ct = _cts?.Token ?? CancellationToken.None;
        var progress = new Progress<(int percent, string status)>(x =>
        {
            _progress.Value = Math.Clamp(x.percent, 0, 100);
            _status.Text = x.status;
        });
        try
        {
            await ToolManager.InstallFfmpegAsync(progress, ct);
            _ffmpeg.Text = AppInfo.ManagedFfmpegExe;
            _settings.FfmpegPath = AppInfo.ManagedFfmpegExe;
            SaveSettings();
            AppendLog("FFmpeg 固定 LGPL 共享构建已安装，ZIP、EXE 与 DLL 完整性均通过校验。");
            return true;
        }
        catch (OperationCanceledException)
        {
            AppendLog("FFmpeg 安装已取消，临时文件已清理。");
            return false;
        }
        catch (Exception ex)
        {
            AppendLog("FFmpeg 自动安装失败：" + ex.Message);
            var local = MessageBox.Show(this,
                "自动下载未完成。你可以在浏览器中下载 README 指定的固定 FFmpeg ZIP，再交给 MusicDrop 做同样的完整性校验。\n\n是否选择已下载的固定 ZIP？",
                "自动安装未完成", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (local != DialogResult.Yes) return false;
            using var dialog = new OpenFileDialog
            {
                Filter = $"固定 FFmpeg ZIP|{AppInfo.FfmpegArchiveName}|ZIP 文件|*.zip",
                Title = "选择 README 指定的固定 FFmpeg ZIP",
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return false;
            try
            {
                await ToolManager.InstallFfmpegFromZipAsync(dialog.FileName, progress, ct);
                _ffmpeg.Text = AppInfo.ManagedFfmpegExe;
                _settings.FfmpegPath = AppInfo.ManagedFfmpegExe;
                SaveSettings();
                AppendLog("本地 FFmpeg ZIP 已通过全部校验并安装。");
                return true;
            }
            catch (Exception localEx)
            {
                AppendLog("本地 FFmpeg ZIP 安装失败：" + localEx.Message);
                MessageBox.Show(this, localEx.Message, "FFmpeg 安装失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }
        finally
        {
            if (ownsBusyState)
            {
                _cts?.Dispose();
                _cts = null;
                SetBusy(false);
            }
        }
    }

    private async Task<bool> EnsureDecryptorAsync(CancellationToken ct)
    {
        if (ToolManager.IsDecryptorValid()) return true;
        var answer = MessageBox.Show(this,
            "新版 MFLAC 需要调用本机 QQ 音乐进行解密。\n\n首次使用将从 luyikk/qqmusic_decrypt 的 GitHub Release 下载固定版本 v0.1.1（约 26 MB），并核对 GitHub 公布的 SHA-256。该第三方项目公开源码，但未声明明确的软件许可证，因此本工具不直接捆绑它。\n\n是否从官方 Release 下载？",
            "安装解密组件", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (answer != DialogResult.Yes) return false;
        var progress = new Progress<(int percent, string status)>(x => { _progress.Value = Math.Clamp(x.percent, 0, 100); _status.Text = x.status; });
        try
        {
            await ToolManager.InstallDecryptorAsync(progress, ct);
            AppendLog("解密组件下载完成，压缩包与程序 SHA-256 均通过校验。");
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            AppendLog("自动下载失败：" + ex.Message);
            var chooseLocal = MessageBox.Show(this,
                "自动下载失败（常见原因是系统 TLS/证书服务暂时不可用）。\n\n你可以用浏览器从 README 中的固定 GitHub Release 下载 ZIP，然后在这里选择它；程序仍会核对 ZIP 和 EXE 的双重 SHA-256。\n\n是否现在选择已经下载的 ZIP？",
                "自动下载失败", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (chooseLocal != DialogResult.Yes) return false;
            using var zipDialog = new OpenFileDialog
            {
                Filter = "固定 qqmusic_decrypt Release ZIP|qqmusic_des-x86_64-pc-windows-msvc-windows-latest.zip|ZIP 文件|*.zip",
                Title = "选择从官方 GitHub Release 下载的 ZIP"
            };
            if (zipDialog.ShowDialog(this) != DialogResult.OK) return false;
            await ToolManager.InstallDecryptorFromZipAsync(zipDialog.FileName, progress, ct);
            AppendLog("本地 ZIP 与其中 EXE 的 SHA-256 均通过校验。");
            return true;
        }
    }

    private async Task StartConversionAsync()
    {
        if (_files.Items.Count == 0) { MessageBox.Show(this, "请先拖入或添加受支持的加密音频文件。", "没有文件", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        string? ffmpeg = ToolManager.FindFfmpeg(_ffmpeg.Text.Trim());
        if (ffmpeg is null)
        {
            if (!await InstallFfmpegInteractivelyAsync(forcePrompt: false)) return;
            ffmpeg = ToolManager.FindFfmpeg(_ffmpeg.Text.Trim());
            if (ffmpeg is null)
            {
                MessageBox.Show(this, "FFmpeg 安装后仍未通过检测。可以在“选择…”中指定可信的 ffmpeg.exe。", "转换引擎未就绪", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }
        if (string.IsNullOrWhiteSpace(_output.Text)) { MessageBox.Show(this, "请选择输出目录。", "缺少输出目录", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        SetBusy(true);
        Interlocked.Increment(ref _preflightEpoch);
        _cts = new CancellationTokenSource();
        try
        {
            SaveSettings();
            var inputs = _files.Items.Cast<ListViewItem>().Select(x => (string)x.Tag!).ToList();
            FilePreflightResult[] initialPreflight = await Task.WhenAll(inputs.Select(path => AudioConverter.PreflightAsync(
                path,
                _settings.PlayerProcessDbPath,
                _settings.ImportedEKeyPath,
                _settings.KugouDatabasePath,
                _cts.Token,
                useQqFallback: false,
                autoStartRequiredClients: false,
                qqMusicExecutablePath: _settings.QqMusicExecutablePath,
                decryptorPath: AppInfo.DecryptorExe)));
            int compatibilityFiles = initialPreflight.Count(result => result.RequiresQqCompatibility);
            if (compatibilityFiles > 0)
            {
                AppendLog($"检测到 {compatibilityFiles} 个文件需要 QQ 兼容链路；正在验证组件与客户端。");
                if (!_settings.UseQqFallback)
                {
                    if (_settings.StrictBatchPreflight)
                        throw new InvalidDataException("所选文件中有新版 QQ 文件缺少 EKey，但 QQ 兼容模式已关闭。");
                    AppendLog("QQ 兼容模式已关闭；非严格模式将继续处理可离线转换的文件。");
                }
                else if (!ToolManager.IsDecryptorValid() && !await EnsureDecryptorAsync(_cts.Token))
                {
                    if (_settings.StrictBatchPreflight)
                        throw new InvalidDataException("兼容组件未安装，严格批量预检已停止转换。");
                    AppendLog("兼容组件未安装；非严格模式将继续处理可离线转换的文件。");
                }
                if (_settings.UseQqFallback && ToolManager.IsDecryptorValid())
                {
                    try
                    {
                        await MusicClientManager.EnsureQqMusicReadyAsync(
                            _settings.QqMusicExecutablePath,
                            _settings.AutoStartRequiredClients,
                            TimeSpan.FromSeconds(30),
                            AppendLog,
                            _cts.Token);
                    }
                    catch (MusicClientException) when (!_settings.StrictBatchPreflight)
                    {
                        AppendLog("QQ 音乐客户端未就绪；非严格模式将继续处理可离线转换的文件。");
                    }
                }
            }
            var options = new ConversionOptions(_format.Text, _mp3Quality.Text, _output.Text.Trim(), ffmpeg,
                AppInfo.DecryptorExe, _overwrite.Checked, _settings.PlayerProcessDbPath,
                _settings.ImportedEKeyPath, _settings.UseQqFallback, _settings.KugouDatabasePath,
                _settings.AutoStartRequiredClients, _settings.QqMusicExecutablePath, 30,
                _settings.StrictBatchPreflight);
            _ = AudioConverter.ValidateStorageAndPaths(inputs, options);
            foreach (ListViewItem item in _files.Items) item.SubItems[4].Text = "处理中";
            var results = await AudioConverter.ConvertAsync(inputs, options,
                (p, s) => SafeUi(() => { _progress.Value = Math.Clamp(p, 0, 100); _status.Text = s; }),
                s => SafeUi(() => AppendLog(s)), _cts.Token);
            foreach (ListViewItem item in _files.Items)
            {
                var result = results.FirstOrDefault(x => string.Equals(x.InputPath, (string)item.Tag!, StringComparison.OrdinalIgnoreCase));
                item.SubItems[4].Text = result?.Success == true ? $"完成 · {result.Route}" : result?.Route ?? "失败";
            }
            int ok = results.Count(x => x.Success);
            int failed = results.Count - ok;
            MessageBox.Show(this, $"转换完成。\n\n成功：{ok}\n失败：{failed}\n输出：{_output.Text}", "完成", MessageBoxButtons.OK, failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (OperationCanceledException) { _status.Text = "已取消"; AppendLog("用户取消了转换。"); }
        catch (Exception ex) { _status.Text = "失败"; AppendLog(ex.ToString()); MessageBox.Show(this, ex.Message, "转换失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetBusy(false);
            if (_closeRequested && !IsDisposed) BeginInvoke(Close);
        }
    }

    private void SetBusy(bool busy)
    {
        _convert.Enabled = !busy;
        _cancel.Enabled = busy;
        _format.Enabled = !busy;
        _mp3Quality.Enabled = !busy && _format.Text == "MP3";
        if (_files.Parent is not null) _files.Parent.Enabled = !busy;
        if (_output.Parent is not null) _output.Parent.Enabled = !busy;
        if (!busy && _progress.Value == 0) _status.Text = "就绪";
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_cts is not null)
        {
            e.Cancel = true;
            _closeRequested = true;
            _status.Text = "正在取消并清理临时文件…";
            _cts.Cancel();
            return;
        }
        SaveSettings();
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
    }
    private void AppendLog(string text)
    {
        _log.AppendText($"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}");
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void ShowAbout()
    {
        BuyerLicenseStatus license = RetailLicenseService.GetCurrentStatus();
        var answer = MessageBox.Show(this,
            $"{AppInfo.WindowTitle}\n\n授权状态：{license.Summary}\n\n社区版：MIT 开源，无激活、无联网验证、无硬件绑定。便利版可使用不绑定电脑、永久有效的离线签名买家凭证；凭证只用于温和的购买记录提示，不会写入音频。\n\n离线核心：基于 MIT 许可 Unlock Music 算法独立集成。QQ、NCM、KWM、KGM v3 不需要对应客户端后台；KGM v5 可只读查询当前用户 KGMusicV3.db 的匹配 EKey；普通 FLAC/WAV/MP3/OGG/M4A 可加入同一批次。QQ 仅在缺少 EKey 且用户开启兜底时才可能使用本机兼容组件。\n\nFFmpeg：可一键安装固定 BtbN LGPL 共享构建，或使用随完整包提供的同一构建。程序核对压缩包、EXE 与 DLL 哈希，不需要管理员权限。\n\n默认严格批量预检：业务范围内完整转换并校验，或在输出前明确停止；损坏文件、平台更新、缺少本地密钥、磁盘/权限故障等外部条件无法承诺字面意义的 100%。本工具不绕过会员、账号或服务端授权。源文件不会被删除或修改；MP3/OGG 有损，从有损源转为 FLAC/WAV 不会恢复音质。\n\nMusicDrop™ 名称与官方标志用于标识官方发行版；™ 不代表已注册商标。按“是”打开 FFmpeg 构建项目页面。",
            "组件来源与说明", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (answer == DialogResult.Yes) ToolManager.OpenUrl(AppInfo.FfmpegBuildProjectUrl);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
