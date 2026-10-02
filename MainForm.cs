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
    private readonly NumericUpDown retries = new() { Minimum = 0, Maximum = 5, Value = 2, Width = 100 };
    private readonly Label titlePreview = Label("预览：先在队列中添加视频", 10, Muted);
    private readonly TextBox log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(246, 248, 251), ScrollBars = ScrollBars.Vertical };
    private Button start = null!, pause = null!, stop = null!, add = null!, remove = null!, refresh = null!;
    private Panel settings = null!;
    private BiliApi? api;
    private UposUploader? uploader;
    private QueueRunner? runner;
    private HttpClient? http;
    private CancellationTokenSource? runCancel;
    private bool busyAccount, paused, closing, stateReadFailed;
    private string accountId = "";
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 500 };
    private DateTime lastProgress = DateTime.MinValue;
    private readonly Dictionary<string, DataGridViewRow> rows = [];

    public MainForm(bool smokeTest = false)
    {
        this.smokeTest = smokeTest;
        directory = smokeTest ? Path.Combine(Path.GetTempPath(), "BilibiliUploader-smoke") : LocalState.DataDirectory;
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
        table.ColumnStyles.Add(new(SizeType.Absolute, 140)); table.ColumnStyles.Add(new(SizeType.Percent, 100));
        void Row(string label, Control control, int height = 52)
        {
            var row = table.RowCount++; table.RowStyles.Add(new(SizeType.Absolute, height));
            table.Controls.Add(Label(label, 10, Ink), 0, row); table.Controls.Add(control, 1, row);
            control.Margin = new Padding(0, 4, 8, 12);
        }
        Row("标题模板", title);
        Row("支持的变量", Label("{title} 去除录像时间前缀的标题    {filename} 完整文件名    {index} 队列序号\n{record_date} 录像日期 2024-01-02    {record_date:yyyy.M.d} 录像日期 2024.1.2\n{date} 上传日期 2024-01-02    {date:yyyy.M.d} 上传日期 2024.1.2", 10, Muted), 92);
        Row("回放模板", Button("使用直播回放模板", () => title.Text = PublishPreset.ReplayTitleTemplate), 68);
        titlePreview.AutoSize = false; titlePreview.Dock = DockStyle.Fill;
        Row("标题预览", titlePreview, 90);
        Row("投稿分区", category);
        Row("标签", tags); Row("", Label("用逗号分隔，例如：游戏, 直播录像；分区登录后从平台读取。", 9, Muted), 38);
        desc.Multiline = true; desc.ScrollBars = ScrollBars.Vertical; Row("视频简介", desc, 110);
        copyright.Items.AddRange(["原创", "转载"]); copyright.SelectedIndex = 0; Row("作品类型", copyright);
        Row("转载来源", source);
        Row("分片失败重试", retries);
        Row("", Label("发布请求不自动重试；每个视频独立投稿，采用平台默认封面。", 9, Muted), 42);
        Row("", Button("保存预设", SavePreset, true), 68);
        title.TextChanged += (_, _) => UpdatePreview();
        copyright.SelectedIndexChanged += (_, _) => source.Enabled = copyright.SelectedIndex == 1;
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
                PerformLayout();
                using (var capture = new Bitmap(Width, Height))
                {
                    DrawToBitmap(capture, new Rectangle(0, 0, Width, Height));
                    capture.Save(Path.Combine(directory, "queue-preview.png"));
                }
                tabs.SelectedIndex = 1;
                PerformLayout();
                await Task.Delay(150);
                using (var capture = new Bitmap(Width, Height))
                {
                    DrawToBitmap(capture, new Rectangle(0, 0, Width, Height));
                    capture.Save(Path.Combine(directory, "settings-preview.png"));
                }
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
                File.WriteAllText(Path.Combine(directory, "smoke-ready.txt"), "WebView2 initialized; UI ready\nTitle preview: " + titlePreview.Text + "\n" + accountCheck + "\nOfficial page origin: " + origin);
                Close();
                return;
            }
            browser.CoreWebView2.Navigate(BiliApi.MemberHome);
            WriteLog("先在“账号与创作中心”登录，读取分区，再保存投稿预设。");
        }
        catch (Exception ex)
        {
            start.Enabled = false; WriteLog("初始化失败：" + UposUploader.SafeMessage(ex.Message));
            MessageBox.Show("客户端启动失败。请确认已安装 Microsoft Edge WebView2 Runtime。\n" + UposUploader.SafeMessage(ex.Message), Text);
        }
    }
    private void LoadPreset()
    {
        title.Text = state.Preset.TitleTemplate; tags.Text = state.Preset.Tags; desc.Text = state.Preset.Description;
        source.Text = state.Preset.Source; copyright.SelectedIndex = state.Preset.Copyright == 2 ? 1 : 0;
        source.Enabled = copyright.SelectedIndex == 1; retries.Value = Math.Clamp(state.Preset.Retries, 0, 5);
        if (state.Preset.CategoryId > 0) { category.Items.Add(new Category(state.Preset.CategoryId, state.Preset.CategoryName)); category.SelectedIndex = 0; }
    }
    private PublishPreset ReadPreset() => new() { TitleTemplate = title.Text.Trim(), Tags = tags.Text.Trim(), Description = desc.Text,
        CategoryId = (category.SelectedItem as Category)?.Id ?? 0, CategoryName = (category.SelectedItem as Category)?.Name ?? "",
        Copyright = copyright.SelectedIndex == 1 ? 2 : 1, Source = source.Text.Trim(), Retries = (int)retries.Value };
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
        if (j.State == "待核对") { MessageBox.Show("请先核对投稿结果后再移除。", Text); return; }
        state.Jobs.Remove(j); state.Save(directory); RefreshRows(); UpdatePreview();
    }
    private void RefreshRows()
    {
        var selectedId = Selected()?.Id;
        grid.Rows.Clear(); rows.Clear();
        foreach (var j in state.Jobs)
        {
            var row = grid.Rows[grid.Rows.Add(j.Title.Length > 0 ? j.Title : Path.GetFileName(j.Path), Bytes(j.Size), $"{100.0 * j.ConfirmedBytes / Math.Max(1, j.Size):0.0}%", j.State, j.Bvid.Length > 0 ? j.Bvid : j.Aid > 0 ? "av" + j.Aid : "—")];
            row.Tag = j; row.Cells[0].ToolTipText = j.Path; row.Cells[3].ToolTipText = j.Error; rows[j.Id] = row;
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
        if (busyAccount || api == null || runner?.Running == true) return;
        busyAccount = true; refresh.Enabled = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var nav = await api.AccountAsync(timeout.Token); UposUploader.CheckCode(nav);
            var data = nav.GetProperty("data");
            if (!data.TryGetProperty("isLogin", out var login) || !login.GetBoolean()) throw new ApiFailure("请在下方官方页面登录。");
            accountId = data.GetProperty("mid").ToString();
            account.Text = "已登录 · " + data.GetProperty("uname").GetString();
            var pre = await api.CategoriesAsync(timeout.Token); UposUploader.CheckCode(pre);
            var categories = BiliApi.ParseCategories(pre.GetProperty("data"));
            if (categories.Count == 0) throw new ApiFailure("未读取到投稿分区，请稍后重试。");
            var selected = (category.SelectedItem as Category)?.Id ?? state.Preset.CategoryId;
            category.Items.Clear(); category.Items.AddRange(categories.Cast<object>().ToArray());
            category.SelectedItem = categories.FirstOrDefault(c => c.Id == selected);
            WriteLog("登录成功，已读取 " + categories.Count + " 个可用分区。");
        }
        catch (Exception ex)
        {
            accountId = ""; account.Text = "请登录并读取分区";
            if (notify) WriteLog(UposUploader.SafeMessage(ex.Message));
        }
        finally { busyAccount = false; refresh.Enabled = true; }
    }
    private async Task StartAsync()
    {
        if (stateReadFailed || runner == null || api == null || uploader == null || runner.Running) return;
        if (accountId.Length == 0) { ShowAccount(BiliApi.MemberHome); WriteLog("请先登录并读取账号与分区。"); return; }
        var preset = ReadPreset(); var originalAccount = accountId;
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
            }, runCancel.Token);
        }
        catch (OperationCanceledException) { WriteLog("队列已停止。"); }
        catch (Exception ex) { WriteLog(UposUploader.SafeMessage(ex.Message)); MessageBox.Show(UposUploader.SafeMessage(ex.Message), Text); }
        finally
        {
            ToggleRunning(false); runCancel.Dispose(); runCancel = null; speed.Text = "0 B/s"; remaining.Text = "剩余时间 —";
            RefreshRows(); if (closing) Close();
        }
    }
    private void ToggleRunning(bool running)
    {
        start.Enabled = add.Enabled = remove.Enabled = settings.Enabled = refresh.Enabled = !running;
        pause.Enabled = stop.Enabled = running; browser.Enabled = !running;
        pause.Text = "暂停";
        tabs.Selecting -= PreventAccountTab;
        if (running) tabs.Selecting += PreventAccountTab;
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
        if (job.State is "已投稿" or "投稿中" or "待核对" or "失败" or "已停止" or "已上传") return;
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
        var job = Selected(); if (job?.State != "待核对") { WriteLog("所选视频无需核对。"); return; }
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
        timer.Stop(); http?.Dispose(); browser.Dispose();
    }
}
