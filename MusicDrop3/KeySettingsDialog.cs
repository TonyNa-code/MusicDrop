namespace MFlacDrop;

internal sealed class KeySettingsDialog : Form
{
    private readonly TextBox _database = new();
    private readonly TextBox _import = new();
    private readonly TextBox _kugouDatabase = new();
    private readonly TextBox _qqExecutable = new();
    private readonly CheckBox _fallback = new();
    private readonly CheckBox _autoStart = new();
    private readonly CheckBox _strictPreflight = new();

    public string PlayerProcessDbPath => _database.Text.Trim();
    public string ImportedEKeyPath => _import.Text.Trim();
    public string KugouDatabasePath => _kugouDatabase.Text.Trim();
    public string QqMusicExecutablePath => _qqExecutable.Text.Trim();
    public bool UseQqFallback => _fallback.Checked;
    public bool AutoStartRequiredClients => _autoStart.Checked;
    public bool StrictBatchPreflight => _strictPreflight.Checked;

    public KeySettingsDialog(AppSettings settings)
    {
        Text = "密钥与兼容模式";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(780, 540);
        MinimumSize = new Size(700, 520);
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.Sizable;

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 3, RowCount = 10
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 94));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        Controls.Add(body);

        body.Controls.Add(LabelFor("player_process_db"), 0, 0);
        _database.Dock = DockStyle.Fill;
        _database.Text = settings.PlayerProcessDbPath;
        body.Controls.Add(_database, 1, 0);
        body.Controls.Add(ButtonFor("选择…", (_, _) => BrowseDatabase()), 2, 0);

        body.Controls.Add(LabelFor("EKey JSON / TXT"), 0, 1);
        _import.Dock = DockStyle.Fill;
        _import.Text = settings.ImportedEKeyPath;
        body.Controls.Add(_import, 1, 1);
        body.Controls.Add(ButtonFor("选择…", (_, _) => BrowseImport()), 2, 1);

        body.Controls.Add(LabelFor("酷狗 KGG 数据库"), 0, 2);
        _kugouDatabase.Dock = DockStyle.Fill;
        _kugouDatabase.Text = settings.KugouDatabasePath;
        _kugouDatabase.PlaceholderText = "留空则自动查找 %APPDATA%\\Kugou8\\KGMusicV3.db";
        body.Controls.Add(_kugouDatabase, 1, 2);
        body.Controls.Add(ButtonFor("选择…", (_, _) => BrowseKugouDatabase()), 2, 2);

        body.Controls.Add(LabelFor("QQMusic.exe"), 0, 3);
        _qqExecutable.Dock = DockStyle.Fill;
        _qqExecutable.Text = settings.QqMusicExecutablePath;
        _qqExecutable.PlaceholderText = "留空则自动查找官方安装";
        body.Controls.Add(_qqExecutable, 1, 3);
        body.Controls.Add(ButtonFor("选择…", (_, _) => BrowseQqMusic()), 2, 3);

        _fallback.Text = "QQ 文件缺少 EKey 时使用本机兼容模式（仅此兜底可能需要 QQ 音乐）";
        _fallback.Checked = settings.UseQqFallback;
        _fallback.Dock = DockStyle.Fill;
        body.Controls.Add(_fallback, 0, 4);
        body.SetColumnSpan(_fallback, 3);

        _autoStart.Text = "确有需要时自动启动已验证数字签名的 QQ 音乐（不提权、不自动关闭）";
        _autoStart.Checked = settings.AutoStartRequiredClients;
        _autoStart.Dock = DockStyle.Fill;
        body.Controls.Add(_autoStart, 0, 5);
        body.SetColumnSpan(_autoStart, 3);

        _strictPreflight.Text = "严格批量预检：任一文件或依赖未就绪时，整批在输出前停止（推荐）";
        _strictPreflight.Checked = settings.StrictBatchPreflight;
        _strictPreflight.Dock = DockStyle.Fill;
        body.Controls.Add(_strictPreflight, 0, 6);
        body.SetColumnSpan(_strictPreflight, 3);
        _fallback.CheckedChanged += (_, _) => _autoStart.Enabled = _fallback.Checked;
        _autoStart.Enabled = _fallback.Checked;

        var cacheText = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = false,
            ForeColor = Color.FromArgb(70, 80, 100),
            Text = "QQ：优先离线解密；验证成功的 EKey 会由 Windows DPAPI 按当前用户加密缓存。只有仍缺 EKey 的文件才会进入兼容模式。自动启动前同时核对腾讯文件信息、Authenticode 签名和签名者。\n酷狗：KGM v5 会只读查询本机 KGMusicV3.db 中与 AudioHash 匹配的 EKey；留空路径时自动查找。KGM v3、NCM 与 KWM 不依赖客户端后台。"
        };
        body.Controls.Add(cacheText, 0, 7);
        body.SetColumnSpan(cacheText, 3);

        var clear = ButtonFor("清除安全缓存", (_, _) => ClearCache());
        clear.AutoSize = true;
        body.Controls.Add(clear, 0, 8);
        body.SetColumnSpan(clear, 3);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var ok = ButtonFor("确定", (_, _) => { DialogResult = DialogResult.OK; Close(); });
        var cancel = ButtonFor("取消", (_, _) => { DialogResult = DialogResult.Cancel; Close(); });
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        body.Controls.Add(buttons, 0, 9);
        body.SetColumnSpan(buttons, 3);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void BrowseDatabase()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Android QQ 音乐密钥库|player_process_db;*.db|所有文件|*.*",
            Title = "选择 player_process_db"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _database.Text = dialog.FileName;
    }

    private void BrowseImport()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "EKey 文件|*.json;*.txt;*.tsv|JSON|*.json|文本|*.txt;*.tsv|所有文件|*.*",
            Title = "选择 EKey 映射文件"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _import.Text = dialog.FileName;
    }

    private void BrowseKugouDatabase()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "酷狗 KGG 数据库|*.db;*.sqlite;*.sqlite3|所有文件|*.*",
            Title = "选择酷狗 KGM v5 本地密钥数据库"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _kugouDatabase.Text = dialog.FileName;
    }

    private void BrowseQqMusic()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "腾讯 QQ 音乐|QQMusic.exe",
            Title = "选择官方 QQMusic.exe"
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) _qqExecutable.Text = dialog.FileName;
    }

    private void ClearCache()
    {
        if (MessageBox.Show(this, "删除当前 Windows 用户的本地 EKey 安全缓存？", "清除缓存",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            if (File.Exists(AppInfo.EKeyCachePath)) File.Delete(AppInfo.EKeyCachePath);
            MessageBox.Show(this, "缓存已清除。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法清除缓存：" + ex.Message, "失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Label LabelFor(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
    };

    private static Button ButtonFor(string text, EventHandler handler)
    {
        var button = new Button { Text = text, Width = 80, Height = 30 };
        button.Click += handler;
        return button;
    }
}
