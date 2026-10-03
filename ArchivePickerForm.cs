namespace BilibiliUploader;

/// <summary>Reads the signed-in user's archives and requires an explicit selection.</summary>
public sealed class ArchivePickerForm : Form
{
    private readonly Func<int, string, CancellationToken, Task<ArchivePage>> readPage;
    private readonly CancellationTokenSource lifetime;
    private readonly TextBox keyword = new() { Dock = DockStyle.Fill, PlaceholderText = "输入标题关键词搜索自己的投稿" };
    private readonly DataGridView archives = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, RowHeadersVisible = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill, BackgroundColor = Color.White,
        BorderStyle = BorderStyle.FixedSingle, AllowUserToResizeRows = false, AutoGenerateColumns = false
    };
    private readonly Label status = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    private readonly Button search = new() { Text = "搜索", Width = 90, Dock = DockStyle.Fill };
    private readonly Button previous = new() { Text = "上一页", AutoSize = true };
    private readonly Button next = new() { Text = "下一页", AutoSize = true };
    private readonly Button confirm = new() { Text = "追加到所选投稿", AutoSize = true, Enabled = false };
    private bool loading, hasMore, closing;
    private int page = 1;
    private string activeKeyword = "";
    public ExistingArchive? SelectedArchive { get; private set; }

    public ArchivePickerForm(Func<int, string, CancellationToken, Task<ArchivePage>> readPage, CancellationToken token)
    {
        this.readPage = readPage;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Text = "选择已有投稿 · 追加新分P";
        Size = new Size(900, 610); MinimumSize = new Size(720, 480);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Microsoft YaHei UI", 10);
        BackColor = Color.White; MinimizeBox = MaximizeBox = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new(SizeType.Absolute, 42));
        layout.RowStyles.Add(new(SizeType.Absolute, 46));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 40));
        layout.RowStyles.Add(new(SizeType.Absolute, 48));
        layout.Controls.Add(new Label { Text = "选择自己的已有投稿，新视频将按队列顺序追加到原分P列表末尾。", Dock = DockStyle.Fill, AutoSize = false }, 0, 0);
        var query = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        query.ColumnStyles.Add(new(SizeType.Percent, 100)); query.ColumnStyles.Add(new(SizeType.Absolute, 100));
        query.Controls.Add(keyword); query.Controls.Add(search);
        layout.Controls.Add(query, 0, 1);
        archives.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "投稿标题", FillWeight = 60, SortMode = DataGridViewColumnSortMode.NotSortable });
        archives.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "稿件编号", FillWeight = 25, SortMode = DataGridViewColumnSortMode.NotSortable });
        archives.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "原分P数", FillWeight = 15, SortMode = DataGridViewColumnSortMode.NotSortable });
        archives.RowTemplate.Height = 36; archives.ColumnHeadersHeight = 38;
        layout.Controls.Add(archives, 0, 2); layout.Controls.Add(status, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(0, 5, 0, 0) };
        var cancel = new Button { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
        foreach (var button in new[] { previous, next, confirm, cancel }) { button.Height = 34; button.Margin = new Padding(0, 0, 12, 0); }
        actions.Controls.AddRange([previous, next, confirm, cancel]); layout.Controls.Add(actions, 0, 4);
        Controls.Add(layout); CancelButton = cancel;
        search.Click += async (_, _) => await LoadPageAsync(1, keyword.Text.Trim());
        keyword.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.Handled = e.SuppressKeyPress = true;
            await LoadPageAsync(1, keyword.Text.Trim());
        };
        previous.Click += async (_, _) => await LoadPageAsync(page - 1, activeKeyword);
        next.Click += async (_, _) => await LoadPageAsync(page + 1, activeKeyword);
        archives.SelectionChanged += (_, _) => confirm.Enabled = !loading && CurrentArchive() != null;
        archives.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0) ConfirmSelection(); };
        confirm.Click += (_, _) => ConfirmSelection();
        Shown += async (_, _) => await LoadPageAsync(1, "");
        FormClosing += (_, _) => { closing = true; lifetime.Cancel(); };
        UpdateAvailability();
    }
    private ExistingArchive? CurrentArchive() => archives.SelectedRows.Count == 1 ? archives.SelectedRows[0].Tag as ExistingArchive : null;
    private async Task LoadPageAsync(int requestedPage, string query)
    {
        if (loading || closing || requestedPage < 1) return;
        loading = true; SelectedArchive = null;
        archives.Rows.Clear(); status.Text = "正在读取自己的已有投稿…";
        UpdateAvailability();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var result = await readPage(requestedPage, query, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            if (closing || IsDisposed) return;
            page = result.Page; activeKeyword = query; hasMore = result.HasMore;
            foreach (var item in result.Items)
            {
                var row = archives.Rows[archives.Rows.Add(item.Title, item.Bvid.Length > 0 ? item.Bvid : "av" + item.Aid,
                    item.PartCount > 0 ? item.PartCount.ToString() : "待读取")];
                row.Tag = item; row.Cells[0].ToolTipText = item.Title;
            }
            // DataGridView otherwise chooses the first archive as soon as rows are inserted.
            archives.ClearSelection(); archives.CurrentCell = null;
            status.Text = "第 " + page + " 页 · " + result.Items.Count + " 条 · "
                + (result.Items.Count == 0 ? "暂无匹配投稿，可调整关键词后重新搜索。" : "请选中目标投稿，再确认；选择操作不会修改原稿。");
        }
        catch (Exception ex) when (!closing && !IsDisposed)
        {
            hasMore = false;
            status.Text = "读取失败：" + UposUploader.SafeMessage(ex.Message);
        }
        catch (Exception) when (closing || IsDisposed) { }
        finally { loading = false; if (!IsDisposed) UpdateAvailability(); }
    }
    private void UpdateAvailability()
    {
        keyword.Enabled = search.Enabled = archives.Enabled = !loading;
        previous.Enabled = !loading && page > 1;
        next.Enabled = !loading && hasMore;
        confirm.Enabled = !loading && CurrentArchive() != null;
    }
    private void ConfirmSelection()
    {
        if (loading || CurrentArchive() is not { } selected) return;
        SelectedArchive = selected; DialogResult = DialogResult.OK;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { closing = true; lifetime.Cancel(); lifetime.Dispose(); }
        base.Dispose(disposing);
    }
    internal static async Task VerifyUiAsync()
    {
        var calls = new List<(int Page, string Keyword)>();
        using var picker = new ArchivePickerForm((page, keyword, token) =>
        {
            token.ThrowIfCancellationRequested(); calls.Add((page, keyword));
            ExistingArchive[] examples = keyword.Length > 0 ? [new(104, "BV1xx411c7mZ", "关键词示例投稿", 3)]
                : page == 1 ? [new(101, "BV1xx411c7mQ", "示例投稿一", 2), new(102, "BV1xx411c7mW", "示例投稿二", 0)]
                : [new(103, "BV1xx411c7mE", "第二页示例投稿", 1)];
            return Task.FromResult(new ArchivePage(examples, page, keyword.Length == 0 && page == 1));
        }, CancellationToken.None);
        await picker.LoadPageAsync(1, "");
        if (picker.confirm.Enabled || picker.SelectedArchive != null || picker.archives.SelectedRows.Count != 0 || !picker.next.Enabled)
            throw new InvalidOperationException("已有投稿列表不得自动选择首条。");
        await picker.LoadPageAsync(2, "");
        if (!picker.previous.Enabled || picker.next.Enabled || picker.confirm.Enabled || picker.archives.Rows.Count != 1)
            throw new InvalidOperationException("已有投稿分页验证失败。");
        await picker.LoadPageAsync(1, "关键词");
        if (calls.Count != 3 || calls[2] != (1, "关键词") || picker.previous.Enabled || picker.confirm.Enabled)
            throw new InvalidOperationException("已有投稿关键词搜索验证失败。");
        picker.archives.Rows[0].Selected = true;
        picker.ConfirmSelection();
        if (picker.SelectedArchive?.Aid != 104) throw new InvalidOperationException("已有投稿手动选择验证失败。");
    }
}
