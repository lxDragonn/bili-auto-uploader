using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace BilibiliUploader;

public sealed class MainForm : Form
{
    private static readonly Color Ink = Color.FromArgb(30, 42, 61), Muted = Color.FromArgb(102, 116, 139), Blue = Color.FromArgb(0, 151, 206);
    private readonly string directory;
    private readonly bool smokeTest;
    private LocalState state = new();
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill, Padding = new Point(22, 12) };
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false, AutoGenerateColumns = false,
        BackgroundColor = Color.White, BorderStyle = BorderStyle.None, CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        AllowUserToResizeRows = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };
    private readonly Label account = Label("尚未登录", 10, Muted);
    private readonly Label speed = Label("0 B/s", 27, Ink);
    private readonly Label detail = Label("添加视频后开始上传", 10, Muted);
    private readonly Label remaining = Label("剩余时间 —", 10, Muted);
    private readonly Label headline = Label("准备就绪", 14, Ink);
    private readonly Label footer = Label("每个视频单独投稿 · 文件保留在原位置", 9, Muted);
    private readonly ProgressBar bar = new() { Dock = DockStyle.Fill, Maximum = 1000, Height = 9, Style = ProgressBarStyle.Continuous };
    private readonly TextBox title = Input("{filename}"), tags = Input(""), desc = Input(""), source = Input("");
    private readonly ComboBox category = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox copyright = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly ComboBox publishMode = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label archiveDescription = Label("每个视频将单独创建投稿。", 9, Muted);
    private readonly Label modeDescription = Label("标题模板用于生成每个新稿件的标题。", 9, Muted);
    private readonly List<Control> archiveMetadata = [];
    private Label templateLabel = null!;
    private readonly NumericUpDown retries = new() { Minimum = 0, Maximum = 5, Value = 2, Width = 100 };
    private readonly Label titlePreview = Label("预览：先在队列中添加视频", 10, Muted);
    private readonly PictureBox coverPreview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(246, 248, 251) };
    private readonly Label coverDescription = Label("平台默认封面", 9, Muted);
    private readonly ComboBox season = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Title" };
    private readonly ComboBox section = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Title" };
    private readonly Label seasonStatus = Label("登录后刷新已有合集；可用范围取决于账号的合集权限。", 9, Muted);
    private readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(246, 248, 251), ScrollBars = ScrollBars.Vertical };
    private Button start = null!, pause = null!, stop = null!, add = null!, remove = null!, refresh = null!, refreshSeasons = null!, selectArchive = null!;
    private Panel settings = null!;
    private BiliApi? api;
    private UposUploader? uploader;
    private QueueRunner? runner;
    private HttpClient? http;
    private CancellationTokenSource? runCancel;
    private bool busyAccount, busySeasons, busyArchives, paused, closing, stateReadFailed, settingSeasonChoices;
    private string accountId = "";
    private string selectedCoverPath = "", selectedSeasonOwnerMid = "";
    private ExistingArchive? selectedArchive;
    private string selectedArchiveOwnerMid = "";
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private DateTime lastProgress = DateTime.MinValue;
    private readonly Dictionary<string, DataGridViewRow> rows = [];

    public MainForm(bool smokeTest = false)
    {
        this.smokeTest = smokeTest;
        directory = smokeTest ? Path.Combine(Path.GetTempPath(), "BilibiliUploader-smoke") : LocalState.DataDirectory;
        if (smokeTest)
        {
            Directory.CreateDirectory(directory);
            File.Delete(Path.Combine(directory, "smoke-ready.txt"));
            File.Delete(Path.Combine(directory, "smoke-failed.txt"));
        }
        Text = "Bilibili 视频上传助手 · " + Application.ProductVersion.Split('+')[0];
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 720); Size = new Size(1220, 850);
        Font = new Font("Microsoft YaHei UI", 10); ForeColor = Ink; BackColor = Color.FromArgb(243, 246, 250);
        AutoScaleMode = AutoScaleMode.Dpi;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(22, 12, 22, 10) };
        layout.RowStyles.Add(new(SizeType.Absolute, 110));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 30));
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
        header.ColumnStyles.Add(new(SizeType.Percent, 70)); header.ColumnStyles.Add(new(SizeType.Percent, 30));
        var branding = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        branding.Controls.Add(Label("Bilibili  /  视频上传助手", 21, Ink));
        branding.Controls.Add(Label("批量选择，自动上传与投稿。", 10, Muted));
        account.Dock = DockStyle.Fill; account.TextAlign = ContentAlignment.MiddleRight;
        header.Controls.Add(branding); header.Controls.Add(account);
        layout.Controls.Add(header, 0, 0); layout.Controls.Add(tabs, 0, 1); layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);
        BuildQueue(); BuildSettings(); BuildAccount();
        try { if (!smokeTest) state = LocalState.Load(directory); }
        catch (Exception) { stateReadFailed = true; MessageBox.Show("本地队列无法读取。为保留投稿记录，本次禁止写入和上传。请检查用户数据目录中的 queue.json 后重启。", Text); }
        LoadPreset(); RefreshRows(); UpdatePreview();
        if (stateReadFailed) { start.Enabled = add.Enabled = remove.Enabled = settings.Enabled = false; }
        Shown += async (_, _) => await InitializeAsync();
        FormClosing += OnClosing;
        timer.Tick += (_, _) => { if (DateTime.UtcNow - lastProgress > TimeSpan.FromSeconds(3)) speed.Text = "0 B/s"; };
        timer.Start();
    }
    private static Label Label(string text, float size, Color color) => new() { Text = text, AutoSize = true, ForeColor = color, Font = new Font("Microsoft YaHei UI", size), Margin = new Padding(0, 4, 0, 4) };
    private static TextBox Input(string value) => new() { Text = value, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle };
    private Button Button(string text, Action click, bool primary = false)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 37, Padding = new Padding(12, 4, 12, 4), FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Blue : Color.White, ForeColor = primary ? Color.White : Ink, Margin = new Padding(0, 0, 10, 0), Cursor = Cursors.Hand };
        b.FlatAppearance.BorderColor = primary ? Blue : Color.FromArgb(212, 220, 229);
        b.Click += (_, _) => click(); return b;
    }
    private TabPage Page(string text) { var p = new TabPage(text) { BackColor = Color.White, Padding = new Padding(20) }; tabs.TabPages.Add(p); return p; }
    private void BuildQueue()
    {
        var page = Page("上传队列");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1 };
        foreach (var h in new[] { 64f, 148f, -1f, 76f, 85f }) layout.RowStyles.Add(new(h == -1 ? SizeType.Percent : SizeType.Absolute, h == -1 ? 100 : h));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        add = Button("＋ 添加视频", AddFiles); remove = Button("移除所选", RemoveSelected);
        actions.Controls.AddRange([add, remove, Button("核对所选结果", ReviewSelected)]);
        layout.Controls.Add(actions, 0, 0);
        var summary = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Padding = new Padding(14, 4, 14, 8), BackColor = Color.FromArgb(241, 248, 252) };
        summary.ColumnStyles.Add(new(SizeType.Percent, 70)); summary.ColumnStyles.Add(new(SizeType.Percent, 30));
        summary.RowStyles.Add(new(SizeType.Absolute, 70)); summary.RowStyles.Add(new(SizeType.Absolute, 38)); summary.RowStyles.Add(new(SizeType.Absolute, 16));
        headline.Dock = DockStyle.Fill; speed.Dock = DockStyle.Fill; speed.TextAlign = ContentAlignment.MiddleRight;
        summary.Controls.Add(headline, 0, 0); summary.Controls.Add(speed, 1, 0);
        summary.Controls.Add(detail, 0, 1); summary.Controls.Add(remaining, 1, 1); summary.Controls.Add(bar, 0, 2); summary.SetColumnSpan(bar, 2);
        layout.Controls.Add(summary, 0, 1);
        grid.ColumnHeadersHeight = 40; grid.RowTemplate.Height = 43; grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle = new() { BackColor = Color.White, ForeColor = Muted, Font = Font, Padding = new Padding(5) };
        grid.DefaultCellStyle = new() { BackColor = Color.White, ForeColor = Ink, SelectionBackColor = Color.FromArgb(229, 245, 253), SelectionForeColor = Ink, Padding = new Padding(5) };
        grid.GridColor = Color.FromArgb(235, 239, 245);
        foreach (var (name, weight) in new[] { ("视频 / 投稿标题", 43), ("大小", 12), ("上传进度", 13), ("状态", 14), ("稿件编号", 18) })
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, FillWeight = weight, SortMode = DataGridViewColumnSortMode.NotSortable });
        grid.SelectionChanged += (_, _) => { var j = Selected(); if (j != null && j.Error.Length > 0) footer.Text = j.Error; UpdatePreview(); };
        layout.Controls.Add(grid, 0, 2);
        var run = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(0, 12, 0, 0) };
        start = Button("开始上传并投稿", () => _ = StartAsync(), true);
        pause = Button("暂停", TogglePause); stop = Button("停止队列", () => runCancel?.Cancel());
        pause.Enabled = stop.Enabled = false;
        run.Controls.AddRange([start, pause, stop, Button("查看创作中心", () => ShowAccount(BiliApi.ManageUrl))]);
        layout.Controls.Add(run, 0, 3); layout.Controls.Add(log, 0, 4); page.Controls.Add(layout);
    }
    private void BuildSettings()
    {
        var page = Page("投稿预设");
        settings = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(8) };
        table.ColumnStyles.Add(new(SizeType.Absolute, 120)); table.ColumnStyles.Add(new(SizeType.Percent, 100));
        Label Row(string label, Control control, int height = 46)
        {
            var row = table.RowCount++; table.RowStyles.Add(new(SizeType.Absolute, height));
            var caption = Label(label, 10, Ink);
            table.Controls.Add(caption, 0, row); table.Controls.Add(control, 1, row);
            control.Margin = new Padding(0, 4, 8, 8);
            return caption;
        }
        publishMode.Items.AddRange(["每个视频单独投稿", "追加到已有投稿（新分P）"]);
        publishMode.SelectedIndex = 0;
        Row("投稿方式", publishMode, 50);
        var archiveEditor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        archiveEditor.ColumnStyles.Add(new(SizeType.Percent, 100)); archiveEditor.ColumnStyles.Add(new(SizeType.Absolute, 175));
        archiveDescription.AutoSize = false; archiveDescription.AutoEllipsis = true; archiveDescription.Dock = DockStyle.Fill;
        selectArchive = Button("选择已有投稿", () => _ = SelectArchiveAsync());
        selectArchive.AutoSize = false; selectArchive.Padding = new Padding(4, 0, 4, 0);
        selectArchive.Margin = new Padding(8, 0, 0, 0); selectArchive.Dock = DockStyle.Top;
        archiveEditor.Controls.Add(archiveDescription); archiveEditor.Controls.Add(selectArchive);
        Row("目标投稿", archiveEditor, 82);
        modeDescription.AutoSize = false; modeDescription.Dock = DockStyle.Fill;
        Row("", modeDescription, 54);
        var titleEditor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        titleEditor.ColumnStyles.Add(new(SizeType.Percent, 100)); titleEditor.ColumnStyles.Add(new(SizeType.Absolute, 175));
        titleEditor.RowStyles.Add(new(SizeType.Percent, 100));
        var replay = Button("使用回放模板", () => title.Text = PublishPreset.ReplayTitleTemplate);
        replay.AutoSize = false; replay.Padding = new Padding(4, 0, 4, 0);
        replay.Margin = new Padding(8, 0, 0, 0); replay.Dock = DockStyle.Fill;
        title.Margin = Padding.Empty; titleEditor.Controls.Add(title); titleEditor.Controls.Add(replay);
        templateLabel = Row("标题模板", titleEditor, 50);
        Row("支持的变量", Label("{title} 录像标题  ·  {filename} 完整文件名  ·  {index} 序号\n{record_date} / {record_date:yyyy.M.d} 录像日期  ·  {date} / {date:yyyy.M.d} 上传日期", 9, Muted), 62);
        titlePreview.AutoSize = false; titlePreview.Dock = DockStyle.Fill;
        Row("标题预览", titlePreview, 52);
        var coverEditor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        coverEditor.ColumnStyles.Add(new(SizeType.Absolute, 150)); coverEditor.ColumnStyles.Add(new(SizeType.Percent, 100));
        coverEditor.RowStyles.Add(new(SizeType.Percent, 100)); coverEditor.RowStyles.Add(new(SizeType.Absolute, 44));
        coverPreview.Margin = new Padding(0, 0, 12, 0); coverEditor.Controls.Add(coverPreview, 0, 0); coverEditor.SetRowSpan(coverPreview, 2);
        coverDescription.AutoSize = false; coverDescription.AutoEllipsis = true; coverDescription.Dock = DockStyle.Fill;
        coverEditor.Controls.Add(coverDescription, 1, 0);
        var coverActions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        var selectCover = Button("选择 JPG / PNG", SelectCover);
        var clearCover = Button("清除封面", () => SetCoverPath(""));
        selectCover.AutoSize = clearCover.AutoSize = false;
        selectCover.Size = new Size(185, 37); clearCover.Size = new Size(122, 37);
        selectCover.Padding = clearCover.Padding = new Padding(4, 0, 4, 0);
        coverActions.Controls.AddRange([selectCover, clearCover]);
        coverEditor.Controls.Add(coverActions, 1, 1); Row("批次封面", coverEditor, 112);
        var seasonEditor = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        seasonEditor.ColumnStyles.Add(new(SizeType.Percent, 100)); seasonEditor.ColumnStyles.Add(new(SizeType.Absolute, 175));
        seasonEditor.RowStyles.Add(new(SizeType.Percent, 100));
        season.Margin = Padding.Empty;
        refreshSeasons = Button("刷新已有合集", () => _ = RefreshSeasonsAsync());
        refreshSeasons.AutoSize = false; refreshSeasons.Padding = new Padding(4, 0, 4, 0);
        refreshSeasons.Margin = new Padding(8, 0, 0, 0); refreshSeasons.Dock = DockStyle.Fill;
        seasonEditor.Controls.Add(season); seasonEditor.Controls.Add(refreshSeasons);
        Row("加入合集", seasonEditor, 50); Row("合集分节", section);
        seasonStatus.AutoSize = false; seasonStatus.Dock = DockStyle.Fill;
        Row("", seasonStatus, 54);
        season.SelectedIndexChanged += (_, _) => OnSeasonChanged();
        Row("投稿分区", category);
        Row("标签", tags); Row("", Label("用逗号分隔，例如：游戏, 直播录像；分区登录后从平台读取。", 9, Muted), 38);
        desc.Multiline = true; desc.ScrollBars = ScrollBars.Vertical; Row("视频简介", desc, 110);
        copyright.Items.AddRange(["原创", "转载"]); copyright.SelectedIndex = 0; Row("作品类型", copyright);
        Row("转载来源", source);
        Row("分片重试", retries);
        Row("", Label("封面与合集应用于当前批次；不选封面时采用平台默认。发布请求不自动重试。", 9, Muted), 42);
        Row("", Button("保存预设", SavePreset, true), 68);
        archiveMetadata.AddRange([coverEditor, seasonEditor, section, seasonStatus, category, tags, desc, copyright]);
        title.TextChanged += (_, _) => UpdatePreview();
        copyright.SelectedIndexChanged += (_, _) => source.Enabled = publishMode.SelectedIndex != 1 && copyright.SelectedIndex == 1;
        publishMode.SelectedIndexChanged += (_, _) => UpdatePublishMode();
        UpdatePublishMode();
        settings.Controls.Add(table); page.Controls.Add(settings);
    }
    private void BuildAccount()
    {
        var page = Page("账号与创作中心");
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.RowStyles.Add(new(SizeType.Absolute, 42)); layout.RowStyles.Add(new(SizeType.Absolute, 34)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        refresh = Button("读取账号与分区", () => _ = RefreshAccountAsync());
        actions.Controls.AddRange([refresh, Button("返回创作中心", () => ShowAccount(BiliApi.MemberHome))]);
        layout.Controls.Add(actions, 0, 0);
        layout.Controls.Add(Label("在下方 Bilibili 官方页面登录。登录状态仅保存在此客户端的独立目录。", 9, Muted), 0, 1);
        layout.Controls.Add(browser, 0, 2); page.Controls.Add(layout);
    }
    private async Task InitializeAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(directory, "WebView2"));
            await browser.EnsureCoreWebView2Async(environment);
            browser.ZoomFactor = 0.7; // The official desktop login page has a wide minimum layout.
            browser.CoreWebView2.Settings.AreHostObjectsAllowed = false;
            browser.CoreWebView2.Settings.IsWebMessageEnabled = false;
            browser.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            browser.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            browser.CoreWebView2.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            browser.CoreWebView2.DownloadStarting += (_, e) => { e.Cancel = true; };
            browser.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!BiliApi.IsBrowserUrl(e.Uri)) e.Cancel = true;
            };
            browser.CoreWebView2.NewWindowRequested += (_, e) =>
            { e.Handled = true; if (runner?.Running != true && BiliApi.IsBrowserUrl(e.Uri)) browser.CoreWebView2.Navigate(e.Uri); };
            browser.CoreWebView2.NavigationCompleted += async (_, e) =>
            { if (!smokeTest && e.IsSuccess && !busyAccount && !(runner?.Running ?? false) && BiliApi.IsMember(browser.Source?.AbsoluteUri ?? "")) await RefreshAccountAsync(false); };
            api = new(browser.CoreWebView2); http = UposUploader.CreateClient(); uploader = new(api, http);
            runner = new(state, directory, uploader);
            runner.Changed += job => { RefreshRows(); headline.Text = job.State + " · " + Path.GetFileName(job.Path); WriteLog(job.State + "：" + job.Title + (job.Error.Length > 0 ? " — " + job.Error : "")); };
            runner.Progressed += OnProgress;
            if (smokeTest)
            {
                headline.Text = "本地界面验证"; account.Text = "测试模式 · 未连接账号";
                state.Jobs.Add(new UploadJob { Path = @"C:\标题预览测试\20240102-120000-123-示例录像.flv" });
                state.Jobs.Add(new UploadJob { Path = @"C:\标题预览测试\20240101-130000-456-第2段-示例内容.mp4" });
                title.Text = PublishPreset.ReplayTitleTemplate;
                RefreshRows(); UpdatePreview();
                grid.ClearSelection(); grid.Rows[1].Selected = true;
                if (titlePreview.Text != "【直播回放2024.1.1】第2段-示例内容")
                    throw new InvalidOperationException("切换视频后的标题预览验证失败。");
                title.Text = "{record_date:yyyy.M.d";
                if (!titlePreview.Text.StartsWith("模板错误："))
                    throw new InvalidOperationException("无效标题模板的提示验证失败。");
                title.Text = PublishPreset.ReplayTitleTemplate;
                grid.ClearSelection(); grid.Rows[0].Selected = true;
                if (titlePreview.Text != "【直播回放2024.1.2】示例录像")
                    throw new InvalidOperationException("录像标题预览验证失败。");
                VerifyCoverAndSeasonUi();
                await VerifyAppendUiAsync();
                PerformLayout();
                using (var capture = new Bitmap(Width, Height))
                {
                    DrawToBitmap(capture, new Rectangle(0, 0, Width, Height));
                    capture.Save(Path.Combine(directory, "queue-preview.png"));
                }
                tabs.SelectedIndex = 1;
                PerformLayout();
                WindowState = FormWindowState.Minimized; WindowState = FormWindowState.Normal;
                TopMost = true; BringToFront(); Activate();
                await Task.Delay(150);
                using (var capture = new Bitmap(Width, Height))
                {
                    DrawToBitmap(capture, new Rectangle(0, 0, Width, Height));
                    capture.Save(Path.Combine(directory, "settings-preview.png"));
                    capture.Save(Path.Combine(directory, "settings-cover-preview.png"));
                }
                CaptureSmokeScreen("settings-cover-screen.png");
                CaptureSmokeScreen("settings-append-screen.png");
                settings.AutoScrollPosition = new Point(0, settings.DisplayRectangle.Height);
                PerformLayout(); await Task.Delay(100);
                using (var capture = new Bitmap(Width, Height))
                {
                    DrawToBitmap(capture, new Rectangle(0, 0, Width, Height));
                    capture.Save(Path.Combine(directory, "settings-bottom-preview.png"));
                }
                CaptureSmokeScreen("settings-bottom-screen.png");
                TopMost = false;
                settings.AutoScrollPosition = Point.Empty;
                var loaded = new TaskCompletionSource<bool>();
                browser.CoreWebView2.NavigationCompleted += (_, e) => loaded.TrySetResult(e.IsSuccess);
                tabs.SelectedIndex = 2;
                browser.CoreWebView2.Navigate(BiliApi.MemberHome);
                string accountCheck;
                try
                {
                    await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    await Task.Delay(3000); // Allow the official site's unauthenticated redirect to settle.
                    await using (var preview = File.Create(Path.Combine(directory, "official-page-preview.png")))
                        await browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, preview);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
                    var nav = await api.AccountAsync(timeout.Token);
                    accountCheck = "Official API reached; login code: " + nav.GetProperty("code").ToString();
                }
                catch (Exception ex) { accountCheck = "Official API check incomplete: " + UposUploader.SafeMessage(ex.Message); }
                var origin = Uri.TryCreate(browser.CoreWebView2.Source, UriKind.Absolute, out var current) ? current.GetLeftPart(UriPartial.Authority) : "unknown";
                File.WriteAllText(Path.Combine(directory, "smoke-ready.txt"), "WebView2 initialized; UI ready\nTitle preview: " + titlePreview.Text
                    + "\nCover and season UI: synthetic image load/clear, simulated season/section selection, preset persistence and refresh lock passed."
                    + "\nAppend UI: mode, explicit target, pagination/search, persistence, disabled global metadata and account change passed."
                    + "\n" + accountCheck + "\nOfficial page origin: " + origin);
                Close();
                return;
            }
            browser.CoreWebView2.Navigate(BiliApi.MemberHome);
            WriteLog("先在“账号与创作中心”登录，读取分区，再保存投稿预设。");
        }
        catch (Exception ex)
        {
            start.Enabled = false; WriteLog("初始化失败：" + UposUploader.SafeMessage(ex.Message));
            if (smokeTest)
            {
                File.WriteAllText(Path.Combine(directory, "smoke-failed.txt"), UposUploader.SafeMessage(ex.Message));
                Close(); return;
            }
            MessageBox.Show("客户端启动失败。请确认已安装 Microsoft Edge WebView2 Runtime。\n" + UposUploader.SafeMessage(ex.Message), Text);
        }
    }
    private void CaptureSmokeScreen(string filename)
    {
        // Capture only this synthetic smoke-test window, never the rest of the desktop.
        if (!smokeTest) return;
        using var capture = new Bitmap(ClientSize.Width, ClientSize.Height);
        using var graphics = Graphics.FromImage(capture);
        graphics.CopyFromScreen(PointToScreen(Point.Empty), Point.Empty, ClientSize);
        capture.Save(Path.Combine(directory, filename));
    }
    private void VerifyCoverAndSeasonUi()
    {
        Directory.CreateDirectory(directory);
        var sample = Path.Combine(directory, "synthetic-cover.png");
        using (var drawing = new Bitmap(1146, 717))
        {
            using var graphics = Graphics.FromImage(drawing);
            graphics.Clear(Color.FromArgb(230, 246, 253));
            using var brush = new SolidBrush(Blue);
            graphics.FillRectangle(brush, 70, 80, 1006, 480);
            using var font = new Font("Microsoft YaHei UI", 42);
            graphics.DrawString("示例封面 · 本地验证", font, Brushes.White, 200, 270);
            drawing.Save(sample, System.Drawing.Imaging.ImageFormat.Png);
        }
        SetCoverPath(sample);
        if (coverPreview.Image == null || ReadPreset().CoverPath != sample) throw new InvalidOperationException("封面加载验证失败。");
        SetCoverPath("");
        if (coverPreview.Image != null || ReadPreset().CoverPath.Length != 0) throw new InvalidOperationException("清除封面验证失败。");
        SetCoverPath(sample);
        VideoSeason[] examples = [new(401, "示例合集（模拟数据）", [new(410, "第一节（模拟）"), new(411, "第二节（模拟）")]),
            new(402, "另一个合集（模拟数据）", [new(420, "默认分节（模拟）")])];
        accountId = "10000";
        SetSeasonChoices(examples);
        season.SelectedIndex = 1; section.SelectedIndex = 1;
        if (ReadPreset().SeasonId != 401 || ReadPreset().SectionId != 411 || ReadPreset().SeasonOwnerMid != "10000")
            throw new InvalidOperationException("合集分节选择验证失败。");
        season.SelectedIndex = 2;
        if (ReadPreset().SectionId != 420) throw new InvalidOperationException("切换合集后的分节验证失败。");
        season.SelectedIndex = 0;
        if (ReadPreset().SeasonId != 0 || ReadPreset().SectionId != 0 || ReadPreset().SeasonOwnerMid.Length != 0 || section.Enabled)
            throw new InvalidOperationException("不加入合集验证失败。");
        season.SelectedIndex = 1; section.SelectedIndex = 1;
        state.Preset = ReadPreset(); state.Save(directory);
        var reloaded = LocalState.Load(directory);
        if (reloaded.Preset.CoverPath != sample || reloaded.Preset.SeasonId != 401 || reloaded.Preset.SectionId != 411 || reloaded.Preset.SeasonOwnerMid != "10000")
            throw new InvalidOperationException("封面与合集的预设保存验证失败。");
        state.Preset = reloaded.Preset; LoadPreset();
        if (ReadPreset().SeasonId != 401 || ReadPreset().SectionId != 411 || coverPreview.Image == null)
            throw new InvalidOperationException("封面与合集的预设还原验证失败。");
        SetSeasonChoices(examples, 401, "示例合集（模拟数据）", 411, "第二节（模拟）", "10000");
        busySeasons = true; UpdateAvailability();
        if (start.Enabled || settings.Enabled) throw new InvalidOperationException("读取合集时未锁定投稿设置。");
        busySeasons = false; UpdateAvailability();
        using (var changed = JsonDocument.Parse("{\"isLogin\":true,\"mid\":10001,\"uname\":\"模拟账号\"}")) ApplyAccount(changed.RootElement);
        if (ReadPreset().SeasonId != 0 || state.Preset.SeasonId != 0) throw new InvalidOperationException("切换账号后未清除原合集。");
        SetSeasonChoices(examples, 401, "示例合集（模拟数据）", 411, "第二节（模拟）", "10001");
        if (!season.Text.Contains("模拟") || !section.Text.Contains("模拟")) throw new InvalidOperationException("合集或分节名称未显示。");
        accountId = ""; account.Text = "测试模式 · 未连接账号";
        seasonStatus.ForeColor = Muted;
        seasonStatus.Text = "界面验证：上方为模拟合集与分节，封面为本地合成图片；未上传或投稿。";
    }
    private async Task VerifyAppendUiAsync()
    {
        await ArchivePickerForm.VerifyUiAsync();
        accountId = "10001";
        var example = new ExistingArchive(501, "BV1xx411c7mQ", "示例已有投稿（模拟数据）", 2);
        SetArchive(example, accountId); publishMode.SelectedIndex = 1;
        if (!ReadPreset().AppendToExisting || ReadPreset().TargetAid != 501 || ReadPreset().TargetOwnerMid != "10001" ||
            category.Enabled || tags.Enabled || section.Enabled || source.Enabled || archiveMetadata.Any(c => c.Enabled))
            throw new InvalidOperationException("追加模式设置验证失败。");
        state.Preset = ReadPreset(); state.Save(directory);
        state.Preset = LocalState.Load(directory).Preset; LoadPreset();
        if (publishMode.SelectedIndex != 1 || ReadPreset().TargetAid != 501 || !archiveDescription.Text.Contains("示例已有投稿"))
            throw new InvalidOperationException("追加目标保存还原失败。");
        publishMode.SelectedIndex = 0;
        if (!category.Enabled || !tags.Enabled || !copyright.Enabled) throw new InvalidOperationException("独立投稿设置未恢复。");
        publishMode.SelectedIndex = 1;
        using (var changed = JsonDocument.Parse("{\"isLogin\":true,\"mid\":10002,\"uname\":\"模拟账号\"}")) ApplyAccount(changed.RootElement);
        if (selectedArchive != null || state.Preset.TargetAid != 0) throw new InvalidOperationException("换账号时未清除追加目标。");
        SetArchive(example, "10002"); accountId = ""; account.Text = "测试模式 · 未连接账号";
        footer.Text = "界面验证使用模拟稿件，未修改任何真实投稿。";
    }
    private void LoadPreset()
    {
        title.Text = state.Preset.TitleTemplate; tags.Text = state.Preset.Tags; desc.Text = state.Preset.Description;
        source.Text = state.Preset.Source; copyright.SelectedIndex = state.Preset.Copyright == 2 ? 1 : 0;
        source.Enabled = copyright.SelectedIndex == 1; retries.Value = Math.Clamp(state.Preset.Retries, 0, 5);
        category.Items.Clear();
        if (state.Preset.CategoryId > 0) { category.Items.Add(new Category(state.Preset.CategoryId, state.Preset.CategoryName)); category.SelectedIndex = 0; }
        SetCoverPath(state.Preset.CoverPath, tolerateInvalid: true);
        SetSeasonChoices([], state.Preset.SeasonId, state.Preset.SeasonTitle, state.Preset.SectionId, state.Preset.SectionTitle, state.Preset.SeasonOwnerMid);
        if (state.Preset.SeasonId > 0) seasonStatus.Text = "已恢复保存的合集，登录后刷新可核对可用状态。";
        SetArchive(state.Preset.TargetAid > 0 ? new ExistingArchive(state.Preset.TargetAid, state.Preset.TargetBvid, state.Preset.TargetTitle) : null,
            state.Preset.TargetOwnerMid);
        publishMode.SelectedIndex = state.Preset.AppendToExisting ? 1 : 0;
        UpdatePublishMode();
    }
    private PublishPreset ReadPreset() => new() { TitleTemplate = title.Text.Trim(), Tags = tags.Text.Trim(), Description = desc.Text,
        CategoryId = (category.SelectedItem as Category)?.Id ?? 0, CategoryName = (category.SelectedItem as Category)?.Name ?? "",
        Copyright = copyright.SelectedIndex == 1 ? 2 : 1, Source = source.Text.Trim(), Retries = (int)retries.Value,
        CoverPath = selectedCoverPath, SeasonId = (season.SelectedItem as VideoSeason)?.Id ?? 0,
        SeasonTitle = (season.SelectedItem as VideoSeason) is { Id: > 0 } picked ? picked.Title : "",
        SectionId = (section.SelectedItem as SeasonSection)?.Id ?? 0,
        SectionTitle = (section.SelectedItem as SeasonSection) is { Id: > 0 } chosen ? chosen.Title : "",
        SeasonOwnerMid = (season.SelectedItem as VideoSeason)?.Id > 0 ? selectedSeasonOwnerMid : "",
        AppendToExisting = publishMode.SelectedIndex == 1, TargetAid = selectedArchive?.Aid ?? 0,
        TargetBvid = selectedArchive?.Bvid ?? "", TargetTitle = selectedArchive?.Title ?? "",
        TargetOwnerMid = selectedArchive == null ? "" : selectedArchiveOwnerMid };
    private void UpdatePublishMode()
    {
        var append = publishMode.SelectedIndex == 1;
        foreach (var control in archiveMetadata) control.Enabled = !append;
        section.Enabled = !append && (season.SelectedItem as VideoSeason)?.Id > 0;
        source.Enabled = !append && copyright.SelectedIndex == 1;
        selectArchive.Enabled = append;
        templateLabel.Text = append ? "新分P模板" : "标题模板";
        start.Text = append ? "开始上传并追加分P" : "开始上传并投稿";
        modeDescription.Text = append ? "模板用于新分P标题；原稿标题、封面、分区、标签和合集保持不变。\n已开始的任务固定目标；更换目标须先移除未完成项，再重新添加。"
            : "每个视频单独创建投稿；封面、分区、标签和合集使用下方预设。";
        UpdateArchiveDescription();
        UpdatePreview();
    }
    private void SetArchive(ExistingArchive? archive, string ownerMid)
    {
        selectedArchive = archive;
        selectedArchiveOwnerMid = archive == null ? "" : ownerMid;
        UpdateArchiveDescription();
    }
    private void UpdateArchiveDescription()
    {
        archiveDescription.Text = publishMode.SelectedIndex != 1 ? "每个视频将单独创建投稿。"
            : selectedArchive == null ? "尚未选择目标，请登录后选择自己的已有投稿。"
            : selectedArchive.Title + "\n" + (selectedArchive.Bvid.Length > 0 ? selectedArchive.Bvid : "av" + selectedArchive.Aid)
                + " · " + (selectedArchive.PartCount > 0 ? "原 " + selectedArchive.PartCount + " P" : "原分P数待读取")
                + " · 新视频依次追加到末尾";
    }
    private async Task SelectArchiveAsync()
    {
        if (api == null || busyAccount || busySeasons || busyArchives || runCancel != null || stateReadFailed) return;
        busyArchives = true; UpdateAvailability();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var nav = await api.AccountAsync(timeout.Token); UposUploader.CheckCode(nav);
            ApplyAccount(nav.GetProperty("data"));
            var ownerMid = accountId;
            async Task<ArchivePage> ReadPage(int page, string keyword, CancellationToken token)
            {
                var current = await api.AccountAsync(token); UposUploader.CheckCode(current);
                var data = current.GetProperty("data");
                ApplyAccount(data);
                if (accountId != ownerMid) throw new ApiFailure("账号已变化，请关闭选择窗口后重新选择投稿。");
                return await api.GetArchivesAsync(page, keyword, token);
            }
            using var picker = new ArchivePickerForm(ReadPage, lifetime.Token);
            if (picker.ShowDialog(this) == DialogResult.OK && picker.SelectedArchive is { } chosen)
            {
                using var verification = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                verification.CancelAfter(TimeSpan.FromSeconds(30));
                var current = await api.AccountAsync(verification.Token); UposUploader.CheckCode(current);
                ApplyAccount(current.GetProperty("data"));
                if (accountId != ownerMid) throw new ApiFailure("账号已变化，未保存旧账号的目标投稿，请重新选择。");
                SetArchive(chosen, ownerMid);
                WriteLog("已选择追加目标：" + chosen.Title + " · " + chosen.Bvid);
            }
        }
        catch (Exception ex) when (!closing && !IsDisposed)
        { WriteLog("读取已有投稿失败：" + UposUploader.SafeMessage(ex.Message)); }
        catch (Exception) when (closing || IsDisposed) { }
        finally { busyArchives = false; if (!IsDisposed) UpdateAvailability(); }
    }
    private void SelectCover()
    {
        using var picker = new OpenFileDialog { Title = "选择当前批次的投稿封面", Filter = "JPG / PNG 图片|*.jpg;*.jpeg;*.png", CheckFileExists = true };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        try { SetCoverPath(picker.FileName); }
        catch (Exception ex) { MessageBox.Show(UposUploader.SafeMessage(ex.Message), "无法使用此封面", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private void SetCoverPath(string path, bool tolerateInvalid = false)
    {
        Bitmap? preview = null;
        string? failure = null;
        if (path.Length > 0)
        {
            try
            {
                using var stream = new MemoryStream(CoverImage.Read(path));
                using var decoded = Image.FromStream(stream);
                preview = new Bitmap(decoded);
            }
            catch (Exception ex) when (tolerateInvalid) { failure = UposUploader.SafeMessage(ex.Message); }
        }
        var previous = coverPreview.Image;
        coverPreview.Image = preview; previous?.Dispose(); selectedCoverPath = path;
        coverDescription.ForeColor = failure == null ? Muted : Color.Firebrick;
        coverDescription.Text = failure != null ? "封面不可用，请重新选择或清除：" + failure
            : path.Length == 0 ? "平台默认封面\nJPG / PNG ≤ 5 MB，至少 960×600，宽高比 4:3–16:9。"
            : Path.GetFileName(path) + "\n" + preview!.Width + " × " + preview.Height + " · 当前批次共用";
    }
    private void SetSeasonChoices(IReadOnlyList<VideoSeason> choices, long wantedSeasonId = 0, string wantedSeasonTitle = "",
        long wantedSectionId = 0, string wantedSectionTitle = "", string ownerMid = "")
    {
        settingSeasonChoices = true;
        try
        {
            season.Items.Clear(); season.Items.Add(new VideoSeason(0, "不加入合集", []));
            season.Items.AddRange(choices.Cast<object>().ToArray());
            var chosen = choices.FirstOrDefault(item => item.Id == wantedSeasonId);
            if (wantedSeasonId > 0 && chosen == null)
            {
                chosen = new VideoSeason(wantedSeasonId, string.IsNullOrWhiteSpace(wantedSeasonTitle) ? "保存的合集（待验证）" : wantedSeasonTitle,
                    wantedSectionId > 0 ? [new SeasonSection(wantedSectionId, wantedSectionTitle)] : []);
                season.Items.Add(chosen);
            }
            season.SelectedItem = chosen ?? season.Items[0];
            selectedSeasonOwnerMid = chosen == null ? "" : ownerMid;
            SetSectionChoices(chosen, wantedSectionId, wantedSectionTitle);
        }
        finally { settingSeasonChoices = false; }
    }
    private void SetSectionChoices(VideoSeason? chosen, long wantedSectionId = 0, string wantedSectionTitle = "")
    {
        section.Items.Clear();
        section.Enabled = publishMode.SelectedIndex != 1 && chosen?.Id > 0;
        if (chosen == null || chosen.Id == 0)
        { section.Items.Add(new SeasonSection(0, "无需选择分节")); section.SelectedIndex = 0; return; }
        section.Items.AddRange(chosen.Sections.Cast<object>().ToArray());
        var selected = chosen.Sections.FirstOrDefault(item => item.Id == wantedSectionId);
        if (wantedSectionId > 0 && selected == null)
        {
            selected = new SeasonSection(wantedSectionId, string.IsNullOrWhiteSpace(wantedSectionTitle) ? "保存的分节（待验证）" : wantedSectionTitle);
            section.Items.Add(selected);
        }
        if (section.Items.Count == 0) section.Items.Add(new SeasonSection(0, "无可用分节，请在创作中心管理合集"));
        section.SelectedItem = selected ?? section.Items[0];
    }
    private void OnSeasonChanged()
    {
        if (settingSeasonChoices) return;
        var selected = season.SelectedItem as VideoSeason;
        selectedSeasonOwnerMid = selected?.Id > 0 ? accountId : "";
        SetSectionChoices(selected);
        seasonStatus.ForeColor = Muted;
        seasonStatus.Text = selected?.Id > 0 ? "当前批次将加入此合集分节；开始前会验证账号与合集。" : "当前批次不加入合集。可在创作中心创建合集后刷新。";
    }
    private void ApplyAccount(JsonElement data)
    {
        if (!data.TryGetProperty("isLogin", out var login) || !login.GetBoolean()) throw new ApiFailure("请在下方官方页面登录。");
        var currentAccount = data.GetProperty("mid").ToString();
        if ((accountId.Length > 0 && accountId != currentAccount) || (selectedArchive != null && selectedArchiveOwnerMid != currentAccount))
        {
            SetArchive(null, "");
            state.Preset.TargetAid = 0;
            state.Preset.TargetBvid = state.Preset.TargetTitle = state.Preset.TargetOwnerMid = "";
            if (!stateReadFailed) state.Save(directory);
            WriteLog("账号发生变化，已清除原账号的目标投稿，请重新选择；队列中已固定的目标保持不变。");
        }
        var hasSavedSeason = (season.SelectedItem as VideoSeason)?.Id > 0;
        if ((accountId.Length > 0 && accountId != currentAccount) || (hasSavedSeason && selectedSeasonOwnerMid != currentAccount))
        {
            SetSeasonChoices([]);
            state.Preset.SeasonId = state.Preset.SectionId = 0;
            state.Preset.SeasonTitle = state.Preset.SectionTitle = state.Preset.SeasonOwnerMid = "";
            if (!stateReadFailed) state.Save(directory);
            WriteLog("账号发生变化，已清除原账号的合集选择，请重新选择。 ");
        }
        accountId = currentAccount;
        account.Text = "已登录 · " + data.GetProperty("uname").GetString();
    }
    private async Task LoadSeasonsAsync(CancellationToken token)
    {
        if (api == null) return;
        var saved = ReadPreset();
        var available = await api.GetSeasonsAsync(token);
        token.ThrowIfCancellationRequested();
        SetSeasonChoices(available, saved.SeasonId, saved.SeasonTitle, saved.SectionId, saved.SectionTitle, saved.SeasonOwnerMid);
        var missing = saved.SeasonId > 0 && !available.Any(item => item.Id == saved.SeasonId && item.Sections.Any(part => part.Id == saved.SectionId));
        seasonStatus.ForeColor = missing ? Color.Firebrick : Muted;
        seasonStatus.Text = missing ? "保存的合集或分节已不可用，已保留原选择；请重新选择，或明确选择“不加入合集”。"
            : available.Count == 0 ? "账号暂无可用合集，或尚未开通合集权限；可在创作中心检查。"
            : "已读取 " + available.Count + " 个合集，当前批次将使用所选合集和分节。";
    }
    private async Task RefreshSeasonsAsync()
    {
        if (api == null || busyAccount || busySeasons || busyArchives || runCancel != null || stateReadFailed || publishMode.SelectedIndex == 1) return;
        busySeasons = true; UpdateAvailability();
        seasonStatus.Text = "正在读取账号与已有合集…";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var nav = await api.AccountAsync(timeout.Token); UposUploader.CheckCode(nav);
            ApplyAccount(nav.GetProperty("data"));
            await LoadSeasonsAsync(timeout.Token);
            WriteLog(seasonStatus.Text);
        }
        catch (Exception ex) when (!closing && !IsDisposed)
        {
            seasonStatus.ForeColor = Color.Firebrick;
            seasonStatus.Text = "合集读取失败，已保留原选择：" + UposUploader.SafeMessage(ex.Message);
            WriteLog(seasonStatus.Text);
        }
        catch (Exception) when (closing || IsDisposed) { }
        finally { busySeasons = false; if (!IsDisposed) UpdateAvailability(); }
    }
    private void SavePreset()
    {
        if (stateReadFailed) return;
        try { state.Preset = ReadPreset(); state.Save(directory); WriteLog("投稿预设已保存。"); }
        catch { MessageBox.Show("无法保存预设，请检查用户目录写入权限。", Text); }
    }
    private void AddFiles()
    {
        using var picker = new OpenFileDialog { Title = "选择要自动上传的视频", Multiselect = true,
            Filter = "视频文件|*.mp4;*.flv;*.avi;*.wmv;*.mov;*.webm;*.mpeg4;*.ts;*.mpg;*.rm;*.rmvb;*.mkv;*.m4v" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        foreach (var path in picker.FileNames)
        {
            if (state.Jobs.Count >= 200) { WriteLog("队列最多保存 200 个视频，请先移除完成项。"); break; }
            try
            {
                var j = UploadJob.Create(path);
                if (state.Jobs.Any(old => string.Equals(old.Path, j.Path, StringComparison.OrdinalIgnoreCase) && old.Size == j.Size && old.ModifiedUtc == j.ModifiedUtc)) continue;
                state.Jobs.Add(j);
            }
            catch (Exception ex) { WriteLog(UposUploader.SafeMessage(ex.Message)); }
        }
        state.Save(directory); RefreshRows(); UpdatePreview();
    }
    private UploadJob? Selected() => grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Tag as UploadJob : null;
    private void RemoveSelected()
    {
        var j = Selected(); if (j == null || runner?.Running == true) return;
        if (j.State is "待核对" or "合集待核对" or "追加待核对") { MessageBox.Show("请先核对投稿、追加分P或合集结果后再移除。", Text); return; }
        state.Jobs.Remove(j); state.Save(directory); RefreshRows(); UpdatePreview();
    }
    private void RefreshRows()
    {
        var selectedId = Selected()?.Id;
        grid.Rows.Clear(); rows.Clear();
        foreach (var j in state.Jobs)
        {
            var row = grid.Rows[grid.Rows.Add(j.Title.Length > 0 ? j.Title : Path.GetFileName(j.Path), Bytes(j.Size), $"{100.0 * j.ConfirmedBytes / Math.Max(1, j.Size):0.0}%", j.State, j.Bvid.Length > 0 ? j.Bvid : j.Aid > 0 ? "av" + j.Aid : "—")];
            row.Tag = j; row.Cells[0].ToolTipText = j.Path + (j.Preset is { AppendToExisting: true } saved
                ? "\n追加目标：" + saved.TargetTitle + " · " + (saved.TargetBvid.Length > 0 ? saved.TargetBvid : "av" + saved.TargetAid) : "");
            row.Cells[3].ToolTipText = j.Error; rows[j.Id] = row;
            if (j.Id == selectedId) row.Selected = true;
        }
    }
    private void UpdatePreview()
    {
        var j = Selected() ?? state.Jobs.FirstOrDefault();
        try
        {
            titlePreview.Text = j == null ? "先在队列中添加视频" : ReadPreset().Title(j.Path, state.Jobs.IndexOf(j) + 1, DateTime.Now);
            titlePreview.ForeColor = Muted;
        }
        catch (InvalidOperationException ex) { titlePreview.Text = "模板错误：" + ex.Message; titlePreview.ForeColor = Color.Firebrick; }
    }
    private void ShowAccount(string url)
    {
        if (runner?.Running == true) { WriteLog("队列运行期间账号保持锁定；停止后可查看创作中心。"); return; }
        tabs.SelectedIndex = 2; if (browser.CoreWebView2 != null) browser.CoreWebView2.Navigate(url);
    }
    private async Task RefreshAccountAsync(bool notify = true)
    {
        if (busyAccount || busySeasons || busyArchives || api == null || runCancel != null) return;
        busyAccount = true; UpdateAvailability();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var nav = await api.AccountAsync(timeout.Token); UposUploader.CheckCode(nav);
            var data = nav.GetProperty("data");
            ApplyAccount(data);
            var pre = await api.CategoriesAsync(timeout.Token); UposUploader.CheckCode(pre);
            var categories = BiliApi.ParseCategories(pre.GetProperty("data"));
            if (categories.Count == 0) throw new ApiFailure("未读取到投稿分区，请稍后重试。");
            var selected = (category.SelectedItem as Category)?.Id ?? state.Preset.CategoryId;
            category.Items.Clear(); category.Items.AddRange(categories.Cast<object>().ToArray());
            category.SelectedItem = categories.FirstOrDefault(c => c.Id == selected);
            WriteLog("登录成功，已读取 " + categories.Count + " 个可用分区。");
            try { if (publishMode.SelectedIndex != 1) await LoadSeasonsAsync(timeout.Token); }
            catch (Exception ex) when (!closing && !IsDisposed)
            {
                seasonStatus.ForeColor = Color.Firebrick;
                seasonStatus.Text = "合集读取失败，已保留原选择：" + UposUploader.SafeMessage(ex.Message);
                WriteLog(seasonStatus.Text);
            }
        }
        catch (Exception ex) when (!closing && !IsDisposed)
        {
            accountId = ""; account.Text = "请登录并读取分区";
            if (notify) WriteLog(UposUploader.SafeMessage(ex.Message));
        }
        catch (Exception) when (closing || IsDisposed) { }
        finally { busyAccount = false; if (!IsDisposed) UpdateAvailability(); }
    }
    private async Task StartAsync()
    {
        if (stateReadFailed || busyAccount || busySeasons || busyArchives || runCancel != null || runner == null || api == null || uploader == null || runner.Running) return;
        if (accountId.Length == 0) { ShowAccount(BiliApi.MemberHome); WriteLog("请先登录并读取账号与分区。"); return; }
        var preset = ReadPreset(); var originalAccount = accountId;
        if (preset.AppendToExisting && preset.TargetAid > 0 && preset.TargetOwnerMid != originalAccount)
        { WriteLog("目标投稿所属账号与当前账号不一致，请重新选择投稿。"); tabs.SelectedIndex = 1; return; }
        if (HasOtherAccountAppendTask(originalAccount))
        { WriteLog("队列中有其他账号的追加分P任务，请切换回对应账号后继续。"); return; }
        if (!preset.AppendToExisting && preset.SeasonId > 0 && preset.SeasonOwnerMid != originalAccount)
        { WriteLog("合集所属账号与当前账号不一致，请刷新后重新选择合集。"); tabs.SelectedIndex = 1; return; }
        if (state.Jobs.Any(job => job.State is "合集待处理" or "合集待核对" && job.Preset is { AppendToExisting: false, SeasonId: > 0 } saved && saved.SeasonOwnerMid != originalAccount))
        { WriteLog("队列中有其他账号的合集任务，请先切换回对应账号再继续。"); return; }
        runCancel = new(); paused = false; uploader.PauseRequested = false;
        ToggleRunning(true);
        try
        {
            state.Preset = preset;
            await runner.RunAsync(preset, async token =>
            {
                var nav = await api.AccountAsync(token); UposUploader.CheckCode(nav);
                if (!nav.TryGetProperty("data", out var data) || !data.TryGetProperty("mid", out var mid) || mid.ToString() != originalAccount)
                    throw new ApiFailure("账号发生变化或登录失效，队列已停止。");
                if ((preset.AppendToExisting && preset.TargetAid > 0 && preset.TargetOwnerMid != mid.ToString()) || HasOtherAccountAppendTask(mid.ToString()))
                    throw new ApiFailure("追加目标所属账号与当前账号不一致，队列已停止。");
                if (!preset.AppendToExisting && preset.SeasonId > 0 && preset.SeasonOwnerMid != mid.ToString())
                    throw new ApiFailure("合集所属账号与当前账号不一致，队列已停止。");
                if (state.Jobs.Any(job => job.State is "合集待处理" or "合集待核对" && job.Preset is { AppendToExisting: false, SeasonId: > 0 } saved && saved.SeasonOwnerMid != mid.ToString()))
                    throw new ApiFailure("合集任务所属账号与当前账号不一致，队列已停止。");
            }, runCancel.Token);
        }
        catch (OperationCanceledException) { WriteLog("队列已停止。"); }
        catch (Exception ex) { WriteLog(UposUploader.SafeMessage(ex.Message)); MessageBox.Show(UposUploader.SafeMessage(ex.Message), Text); }
        finally
        {
            runCancel.Dispose(); runCancel = null; ToggleRunning(false); speed.Text = "0 B/s"; remaining.Text = "剩余时间 —";
            RefreshRows(); if (closing) Close();
        }
    }
    private void ToggleRunning(bool running)
    {
        UpdateAvailability();
        pause.Enabled = stop.Enabled = running; browser.Enabled = !running;
        pause.Text = "暂停";
        tabs.Selecting -= PreventAccountTab;
        if (running) tabs.Selecting += PreventAccountTab;
    }
    private bool HasOtherAccountAppendTask(string mid) => state.Jobs.Any(job =>
        job.State is "等待上传" or "失败" or "已停止" or "已上传" or "追加待核对" &&
        job.Preset is { AppendToExisting: true } saved && saved.TargetOwnerMid != mid);
    private void UpdateAvailability()
    {
        var running = runCancel != null || runner?.Running == true;
        var reading = busyAccount || busySeasons || busyArchives;
        start.Enabled = add.Enabled = remove.Enabled = settings.Enabled = !running && !reading && !stateReadFailed;
        refresh.Enabled = !running && !reading;
        refreshSeasons.Enabled = !running && !reading && publishMode.SelectedIndex != 1;
    }
    private void PreventAccountTab(object? sender, TabControlCancelEventArgs e) { if (e.TabPageIndex == 2) e.Cancel = true; }
    private void TogglePause()
    {
        if (uploader == null) return;
        paused = !paused; uploader.PauseRequested = paused; pause.Text = paused ? "继续" : "暂停";
        WriteLog(paused ? "已请求暂停，将在当前分片完成后暂停。" : "继续上传。");
    }
    private void OnProgress(UploadJob job, UploadProgress p)
    {
        if (IsDisposed || closing) return;
        // Posting progress after completion must not overwrite the confirmed job state.
        if (job.State is "已投稿" or "投稿中" or "待核对" or "失败" or "已停止" or "已上传" or "加入合集中" or "合集待处理" or "合集待核对" or "追加分P中" or "追加待核对" or "已追加") return;
        job.State = p.Stage.StartsWith("分片重试") ? "上传中" : p.Stage;
        var sent = Math.Min(p.Total, p.Confirmed + p.InFlight);
        if (rows.TryGetValue(job.Id, out var row)) { row.Cells[2].Value = $"{100.0 * sent / Math.Max(1, p.Total):0.0}%"; row.Cells[3].Value = p.Stage; }
        bar.Value = (int)Math.Clamp(1000.0 * sent / Math.Max(1, p.Total), 0, 1000);
        headline.Text = p.Stage + " · " + Path.GetFileName(job.Path);
        detail.Text = $"已传输 {Bytes(sent)} / {Bytes(p.Total)}    服务端已确认 {Bytes(p.Confirmed)}";
        speed.Text = Bytes((long)p.Speed) + "/s";
        remaining.Text = p.Speed > 0 ? "约剩余 " + TimeSpan.FromSeconds(Math.Min(864000, (p.Total - sent) / p.Speed)).ToString(@"hh\:mm\:ss") : "剩余时间 —";
        lastProgress = DateTime.UtcNow;
    }
    private void ReviewSelected()
    {
        if (runner?.Running == true) return;
        var job = Selected();
        if (job?.State == "追加待核对")
        {
            var target = job.Preset?.TargetTitle + " · " + job.Preset?.TargetBvid;
            var appendAnswer = MessageBox.Show("请先在创作中心核对目标投稿是否已新增此分P。\n目标：" + target
                + "\n分P：" + job.Title + "\n\n是：已确认分P存在，标记已追加。\n否：已确认分P不存在，允许再试追加。\n取消：保持追加待核对。",
                "核对追加分P结果", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (appendAnswer == DialogResult.Cancel) return;
            if (appendAnswer == DialogResult.Yes)
            {
                job.State = "已追加"; job.Aid = job.Preset?.TargetAid ?? 0; job.Bvid = job.Preset?.TargetBvid ?? "";
                job.Error = "用户已在创作中心核对分P追加成功。";
            }
            else
            {
                job.State = "等待上传"; job.AppendAttempted = false;
                job.Error = "用户确认尚未追加，允许再次尝试；保留原目标及已上传视频。";
            }
            state.Save(directory); RefreshRows(); return;
        }
        if (job?.State == "合集待核对")
        {
            var seasonAnswer = MessageBox.Show("此稿件已经投稿成功，请先在创作中心核对是否已加入目标合集。\n本操作只处理合集状态，不会重新投稿视频。\n\n是：确认已加入合集，标记完成。\n否：确认未加入合集，允许再次尝试加入。\n取消：保持合集待核对。", "核对合集结果", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (seasonAnswer == DialogResult.Cancel) return;
            if (seasonAnswer == DialogResult.Yes) { job.State = "已投稿"; job.SeasonAdded = true; job.Error = "用户已核对稿件加入合集成功。"; }
            else { job.State = "合集待处理"; job.SeasonAttempted = false; job.SeasonAdded = false; job.Error = "用户确认尚未加入合集，允许仅重试加入合集。"; }
            state.Save(directory); RefreshRows(); return;
        }
        if (job?.State != "待核对") { WriteLog("所选视频无需核对。"); return; }
        var answer = MessageBox.Show("请先在创作中心核对。\n\n是：已确认投稿成功，标记已处理。\n否：已确认没有投稿，允许重新提交。\n取消：保持待核对。", "核对结果", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return;
        if (answer == DialogResult.Yes) { job.State = "已投稿"; job.Error = "用户已在创作中心核对。"; }
        else { job.State = "等待上传"; job.PublishAttempted = false; job.Error = "用户确认未投稿，允许再次提交。"; }
        state.Save(directory); RefreshRows();
    }
    private void WriteLog(string message)
    {
        var lines = log.Lines.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(80).Append(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
        log.Lines = lines.ToArray(); log.SelectionStart = log.TextLength; log.ScrollToCaret(); footer.Text = message;
    }
    public static string Bytes(long count)
    { string[] units = ["B", "KB", "MB", "GB", "TB"]; double size = Math.Max(0, count); var i = 0; while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; } return size.ToString(i == 0 ? "0" : "0.00") + " " + units[i]; }
    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (runner?.Running == true)
        {
            if (!closing && MessageBox.Show("队列仍在运行。关闭将停止剩余上传；已发出的投稿请求会保留核对状态。", Text, MessageBoxButtons.OKCancel) != DialogResult.OK) { e.Cancel = true; return; }
            closing = true; e.Cancel = true; runCancel?.Cancel(); return;
        }
        closing = true; lifetime.Cancel();
        timer.Stop(); http?.Dispose(); coverPreview.Image?.Dispose(); coverPreview.Image = null; browser.Dispose(); lifetime.Dispose();
    }
}
