namespace LumaWall;

class OnlineWallpaperDialog : Form
{
    readonly AlphaProvider provider;
    readonly Func<OnlineAsset[]> existing;
    readonly Action<string> saveRoot;
    readonly Action<OnlineDownloadResult> commit;
    readonly TextBox keyword = new() { Dock = DockStyle.Fill, PlaceholderText = "中文作品 / 角色，如原神、申鹤；也可输入英文或主题页链接", MaxLength = 200 };
    readonly TextBox root = new() { Dock = DockStyle.Fill, ReadOnly = true };
    readonly TextBox category = new() { Dock = DockStyle.Fill, MaxLength = 64 };
    readonly FlowLayoutPanel gallery = new() { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true };
    readonly Label state = new() { Dock = DockStyle.Fill, ForeColor = Palette.Muted, AutoEllipsis = true };
    readonly Label selection = new() { AutoSize = true, ForeColor = Palette.Accent, Margin = new(12, 10, 12, 0) };
    readonly Button search, previous, next, download, cancel, browse, selectAll, clear;
    readonly Dictionary<string, OnlineImage> checkedImages = [];
    readonly List<(OnlineImage Image, CheckBox Check, PictureBox Picture)> cards = [];
    readonly CancellationTokenSource lifetime = new();
    CancellationTokenSource? operation, thumbnails;
    string currentQuery = "";
    int page = 1;
    bool busy, closing;
    readonly BrowserPageSearch? browserSearch;

    public OnlineWallpaperDialog(AlphaProvider provider, string rootPath, Func<OnlineAsset[]> existing,
        Action<string> saveRoot, Action<OnlineDownloadResult> commit, bool useBrowserSearch = false, string? browserProfile = null)
    {
        this.provider = provider; this.existing = existing; this.saveRoot = saveRoot; this.commit = commit;
        Text = "在线找壁纸 · Alpha Coders"; Font = new("Microsoft YaHei UI", 10); BackColor = Palette.Surface; ForeColor = Palette.Text;
        StartPosition = FormStartPosition.CenterParent; Size = new(1220, 860); MinimumSize = new(880, 620); ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new(SizeType.Absolute, 42)); layout.RowStyles.Add(new(SizeType.Absolute, 42)); layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 44)); layout.RowStyles.Add(new(SizeType.Absolute, 74)); layout.RowStyles.Add(new(SizeType.Absolute, 48));
        var top = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 }; top.ColumnStyles.Add(new(SizeType.Percent, 100)); top.ColumnStyles.Add(new(SizeType.Absolute, 100)); top.ColumnStyles.Add(new(SizeType.Absolute, 132));
        search = ActionButton("检索"); search.Click += async (_, _) => await SearchAsync(keyword.Text, 1, true);
        var website = ActionButton("网站浏览"); website.Click += (_, _) =>
        {
            if (busy) return;
            using var dialog = new OnlineBrowserDialog(root.Text, saveRoot, existing, commit,
                pageSelected: result => ShowPage(result, false));
            dialog.ShowDialog(this);
        };
        top.Controls.Add(keyword, 0, 0); top.Controls.Add(search, 1, 0); top.Controls.Add(website, 2, 0); layout.Controls.Add(top, 0, 0);
        layout.Controls.Add(new Label { Text = "输入中文检索，浏览器定位网站真实结果；在此多选、跨页勾选并批量下载原图。也可在“网站浏览”中提取当前页。", Dock = DockStyle.Fill, ForeColor = Palette.Muted }, 0, 1);
        gallery.BackColor = Palette.Background; layout.Controls.Add(gallery, 0, 2);
        var paging = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        previous = ActionButton("上一页"); next = ActionButton("下一页"); previous.Enabled = next.Enabled = false;
        previous.Click += async (_, _) => await SearchAsync(currentQuery, page - 1, false);
        next.Click += async (_, _) => await SearchAsync(currentQuery, page + 1, false);
        selectAll = ActionButton("勾选当前页"); selectAll.Click += (_, _) => { foreach (var card in cards) if (card.Check.Enabled) card.Check.Checked = true; };
        clear = ActionButton("清空勾选"); clear.Click += (_, _) => { checkedImages.Clear(); foreach (var card in cards) card.Check.Checked = false; UpdateSelection(); };
        paging.Controls.AddRange([previous, next, selectAll, clear, selection]); layout.Controls.Add(paging, 0, 3);
        var files = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 2 };
        files.ColumnStyles.Add(new(SizeType.Absolute, 82)); files.ColumnStyles.Add(new(SizeType.Percent, 100));
        files.ColumnStyles.Add(new(SizeType.Absolute, 110)); files.ColumnStyles.Add(new(SizeType.Absolute, 55)); files.ColumnStyles.Add(new(SizeType.Absolute, 220));
        files.RowStyles.Add(new(SizeType.Absolute, 37)); files.RowStyles.Add(new(SizeType.Absolute, 37));
        files.Controls.Add(new Label { Text = "下载目录", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0); files.Controls.Add(root, 1, 0);
        browse = ActionButton("选择目录"); browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = root.Text, Description = "选择原图下载目录，每个分类建立一个子文件夹" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                try { saveRoot(dialog.SelectedPath); root.Text = dialog.SelectedPath; } catch (Exception ex) { state.Text = "目录未保存：" + ex.Message; }
        };
        files.Controls.Add(browse, 2, 0); files.Controls.Add(new Label { Text = "分类", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight }, 3, 0); files.Controls.Add(category, 4, 0);
        files.Controls.Add(state, 0, 1); files.SetColumnSpan(state, 5); layout.Controls.Add(files, 0, 4);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var close = ActionButton("关闭"); close.Click += (_, _) => Close();
        cancel = ActionButton("取消任务"); cancel.Enabled = false; cancel.Click += (_, _) => operation?.Cancel();
        download = ActionButton("下载勾选原图并导入"); download.Width = 205; download.Enabled = false; download.Click += async (_, _) => await DownloadAsync();
        bottom.Controls.AddRange([close, cancel, download]); layout.Controls.Add(bottom, 0, 5); Controls.Add(layout);
        keyword.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter && !busy) { e.SuppressKeyPress = true; await SearchAsync(keyword.Text, 1, true); } };
        root.Text = rootPath; state.Text = "按需读取网页；原图仅在勾选下载时保存。"; UpdateSelection();
        DarkTheme.Attach(this);
        if (useBrowserSearch) browserSearch = new BrowserPageSearch(this, browserProfile);
    }

    internal static Button ActionButton(string text) => new DarkButton()
    {
        Text = text, Width = 132, Height = 34, MinimumSize = new(80, 30), FlatStyle = FlatStyle.Flat,
        BackColor = Palette.Field, ForeColor = Palette.Text, UseVisualStyleBackColor = false, Margin = new(3)
    };
    void SetBusy(bool value)
    {
        busy = value; search.Enabled = keyword.Enabled = category.Enabled = browse.Enabled = selectAll.Enabled = clear.Enabled = !value;
        var known = existing().Where(x => File.Exists(x.ManagedPath)).Select(x => x.Id).ToHashSet();
        foreach (var card in cards) card.Check.Enabled = !value && !known.Contains(card.Image.Id);
        previous.Enabled = !value && page > 1 && currentQuery.Length > 0; next.Enabled = !value && cards.Count > 0 && page < 200;
        cancel.Enabled = value; UpdateSelection();
    }
    internal Task SearchTopicAsync(string query) { keyword.Text = query; return SearchAsync(query, 1, true); }
    internal bool AllThumbnailsReady => cards.Count > 0 && cards.All(x => x.Picture.Image != null);
    internal int ResultCount => cards.Count;
    void UpdateSelection() { selection.Text = $"已勾选 {checkedImages.Count} 张"; download.Enabled = !busy && checkedImages.Count > 0; }

    async Task SearchAsync(string query, int number, bool reset)
    {
        if (busy) return; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); SetBusy(true);
        try
        {
            if (reset) { checkedImages.Clear(); ClearCards(); UpdateSelection(); }
            state.Text = "正在匹配英文名称并读取主题页…";
            var result = browserSearch != null ? await browserSearch.SearchAsync(query, number, operation.Token)
                : await provider.SearchAsync(query, number, operation.Token);
            if (closing) return;
            ShowPage(result, reset);
        }
        catch (OperationCanceledException) { if (!closing) state.Text = "检索已取消。"; }
        catch (Exception ex) { if (!closing) state.Text = "检索失败：" + ex.Message; }
        finally { operation?.Dispose(); operation = null; if (!closing) SetBusy(false); }
    }
    internal void ShowPage(OnlinePage result, bool reset)
    {
            if (closing) return;
            if (reset) checkedImages.Clear();
            ClearCards(); page = result.Number; currentQuery = result.Address.AbsoluteUri;
            if (reset || string.IsNullOrWhiteSpace(category.Text))
            {
                string suggested = Uri.UnescapeDataString(result.Address.AbsolutePath.Trim('/')).Replace("-wallpapers", "");
                suggested = new string(suggested.Where(c => !char.IsControl(c) && !Path.GetInvalidFileNameChars().Contains(c)).ToArray()).Trim().TrimEnd('.');
                if (suggested.Length == 0) suggested = "在线壁纸";
                category.Text = suggested.Length > 64 ? suggested[..64] : suggested;
            }
            var known = existing().Where(x => File.Exists(x.ManagedPath)).Select(x => x.Id).ToHashSet();
            foreach (var image in result.Images) AddCard(image, known.Contains(image.Id));
            state.Text = result.Images.Count == 0 ? "此页没有可下载图片。可点“网站浏览”，进入具体作品或壁纸分类后提取本页。" : $"第 {page} 页 · {result.Images.Count} 张 · {result.Address.GetLeftPart(UriPartial.Path)}";
            thumbnails = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            _ = LoadThumbnailsAsync(cards.ToArray(), thumbnails.Token);
            UpdateSelection(); if (!busy) SetBusy(false);
    }
    void AddCard(OnlineImage image, bool imported)
    {
        int U(int value) => (int)Math.Round(value * Font.Size / 10f);
        var panel = new Panel { Width = U(266), Height = U(226), BackColor = Palette.Surface, Margin = new(U(7)), Padding = new(U(8)) };
        var picture = new PictureBox { Bounds = new(U(8), U(8), U(250), U(140)), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Palette.Field };
        var check = new CheckBox { Bounds = new(U(8), U(154), U(250), U(42)), Text = image.Name, Enabled = !imported,
            Checked = !imported && checkedImages.ContainsKey(image.Id), AutoEllipsis = true, UseMnemonic = false };
        var info = new Label { Bounds = new(U(8), U(200), U(250), U(20)), ForeColor = Palette.Muted,
            Text = imported ? "已在壁纸库中" : image.Width > 0 ? $"原图 {image.Width}×{image.Height}" : "原图 · 未标注尺寸" };
        check.CheckedChanged += (_, _) => { if (check.Checked) checkedImages[image.Id] = image; else checkedImages.Remove(image.Id); UpdateSelection(); };
        picture.Click += (_, _) => { if (!busy && check.Enabled) check.Checked = !check.Checked; };
        panel.Controls.AddRange([picture, check, info]); gallery.Controls.Add(panel); cards.Add((image, check, picture));
    }
    async Task LoadThumbnailsAsync((OnlineImage Image, CheckBox Check, PictureBox Picture)[] entries, CancellationToken token)
    {
        using var limit = new SemaphoreSlim(2);
        await Task.WhenAll(entries.Select(async entry =>
        {
            Bitmap? image = null;
            try
            {
                await limit.WaitAsync(token);
                try { image = await provider.ThumbnailAsync(entry.Image, token); } finally { limit.Release(); }
                if (!token.IsCancellationRequested && !closing && !entry.Picture.IsDisposed) { entry.Picture.Image = image; image = null; }
            }
            catch (Exception) { /* One unavailable thumbnail leaves the original selectable. */ }
            finally { image?.Dispose(); }
        }));
    }
    async Task DownloadAsync()
    {
        if (busy || checkedImages.Count == 0) return;
        OnlineDownloadResult? result = null;
        operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); SetBusy(true); thumbnails?.Cancel();
        try
        {
            var selected = checkedImages.Values.ToArray();
            result = await OnlineStorage.DownloadAsync(provider, selected, root.Text, category.Text, existing(),
                new Progress<string>(text => { if (!closing) state.Text = text; }), operation.Token);
            if (closing) { OnlineStorage.Rollback(result); return; }
            commit(result);
            var known = existing().Where(x => File.Exists(x.ManagedPath)).Select(x => x.Id).ToHashSet();
            foreach (var card in cards)
                if (known.Contains(card.Image.Id)) { card.Check.Checked = false; card.Check.Enabled = false; }
            foreach (string id in checkedImages.Keys.Where(known.Contains).ToArray()) checkedImages.Remove(id);
            state.Text = $"已导入 {result.Items.Count} 张原图 · 已存在 {result.Skipped} 张 · 失败 {result.Errors.Count} 张";
            if (result.Errors.Count > 0) MessageBox.Show(this, string.Join("\n", result.Errors.Take(10)), "部分图片未下载", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) { if (result != null) OnlineStorage.Rollback(result); if (!closing) state.Text = "已取消，本批次没有导入新图片。"; }
        catch (Exception ex) { if (result != null) OnlineStorage.Rollback(result); if (!closing) state.Text = "下载或导入失败：" + ex.Message; }
        finally { operation?.Dispose(); operation = null; if (!closing) SetBusy(false); }
    }
    void ClearCards()
    {
        thumbnails?.Cancel(); thumbnails?.Dispose(); thumbnails = null;
        foreach (var entry in cards) { entry.Picture.Image?.Dispose(); entry.Picture.Image = null; }
        foreach (Control control in gallery.Controls.Cast<Control>().ToArray()) control.Dispose();
        gallery.Controls.Clear(); cards.Clear();
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    { closing = true; lifetime.Cancel(); operation?.Cancel(); ClearCards(); base.OnFormClosing(e); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !closing) { closing = true; lifetime.Cancel(); operation?.Cancel(); ClearCards(); }
        if (disposing) browserSearch?.Dispose();
        base.Dispose(disposing);
    }
}
