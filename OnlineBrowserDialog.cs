using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace LumaWall;

class OnlineBrowserDialog : Form
{
    readonly WebView2 browser = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(24, 24, 28) };
    readonly TextBox query = new() { Dock = DockStyle.Fill, PlaceholderText = "输入作品名或角色名；中文可点“中文转英文搜索”", MaxLength = 200 };
    readonly TextBox address = new() { Dock = DockStyle.Fill, PlaceholderText = "网页地址" };
    readonly TextBox category = new() { Width = 150, Text = "网站下载", MaxLength = 64 };
    readonly Label state = new() { Dock = DockStyle.Fill, ForeColor = Palette.Muted, AutoEllipsis = true };
    readonly Label location = new() { AutoSize = true, ForeColor = Palette.Muted, MaximumSize = new(550, 32) };
    readonly Button back = Button("后退", 70), forward = Button("前进", 70), search = Button("网站搜索", 100), chinese = Button("中文转英文搜索", 148);
    readonly Button cancel = Button("取消下载", 100);
    readonly Action<string> saveRoot;
    readonly Action<OnlineDownloadResult> commit;
    readonly Func<OnlineAsset[]> existing;
    readonly string profile;
    readonly Func<Form>? legacyGallery;
    readonly Action<OnlinePage>? pageSelected;
    string root;
    bool closing;
    readonly Dictionary<CoreWebView2DownloadOperation, OnlineDownloadResult> pending = [];
    internal WebView2 Browser => browser;
    internal string Status => state.Text;

    public OnlineBrowserDialog(string root, Action<string> saveRoot, Func<OnlineAsset[]> existing,
        Action<OnlineDownloadResult> commit, Func<Form>? legacyGallery = null, string? profile = null, bool autoStart = true,
        Action<OnlinePage>? pageSelected = null)
    {
        this.root = root; this.saveRoot = saveRoot; this.existing = existing; this.commit = commit; this.legacyGallery = legacyGallery;
        this.pageSelected = pageSelected;
        this.profile = profile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaWall", "webview2");
        Text = "在线找壁纸 · 网站浏览器"; Icon = AppIcon.Value;
        Font = new("Microsoft YaHei UI", 10); BackColor = Palette.Surface; ForeColor = Palette.Text;
        StartPosition = FormStartPosition.CenterParent; Size = new(1380, 920); MinimumSize = new(980, 650); ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new(12) };
        foreach (int height in new[] { 42, 42, 38 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 70));
        var nav = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7 };
        foreach (int width in new[] { 76, 76, 76, 76 }) nav.ColumnStyles.Add(new(SizeType.Absolute, width));
        nav.ColumnStyles.Add(new(SizeType.Percent, 100)); nav.ColumnStyles.Add(new(SizeType.Absolute, 76)); nav.ColumnStyles.Add(new(SizeType.Absolute, 125));
        var reload = Button("刷新", 70); var home = Button("首页", 70); var go = Button("前往", 70); var external = Button("外部浏览器", 116);
        Control[] navControls = [back, forward, reload, home, address, go, external];
        for (int i = 0; i < navControls.Length; i++) nav.Controls.Add(navControls[i], i, 0);
        var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4 };
        searchRow.ColumnStyles.Add(new(SizeType.Percent, 100)); searchRow.ColumnStyles.Add(new(SizeType.Absolute, 108));
        searchRow.ColumnStyles.Add(new(SizeType.Absolute, 156)); searchRow.ColumnStyles.Add(new(SizeType.Absolute, 112));
        searchRow.ColumnStyles[3].Width = pageSelected != null ? 170 : 112;
        var gallery = Button(pageSelected != null ? "提取本页 · 多选下载" : "批量图库", pageSelected != null ? 162 : 105); gallery.Enabled = legacyGallery != null || pageSelected != null;
        searchRow.Controls.Add(query, 0, 0); searchRow.Controls.Add(search, 1, 0); searchRow.Controls.Add(chinese, 2, 0); searchRow.Controls.Add(gallery, 3, 0);
        layout.Controls.Add(nav, 0, 0); layout.Controls.Add(searchRow, 0, 1);
        layout.Controls.Add(new Label { Text = "真实网站：可直接搜索、筛选、翻页和下载。中文可先转成英文名称再提交网站；名称可自行修改。支持的图片下载后会自动导入。", Dock = DockStyle.Fill, ForeColor = Palette.Muted }, 0, 2);
        layout.Controls.Add(browser, 0, 3);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        footer.RowStyles.Add(new(SizeType.Absolute, 38)); footer.RowStyles.Add(new(SizeType.Percent, 100));
        var files = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var choose = Button("下载目录", 100);
        files.Controls.AddRange([choose, new Label { Text = "分类", AutoSize = true, Margin = new(8, 8, 3, 0) }, category, cancel, location]);
        if (pageSelected != null)
        {
            files.Controls.Clear(); files.Controls.Add(new Label { AutoSize = true, Text = "找到图片后点右上角“提取本页 · 多选下载”，返回图库勾选并批量下载。无需点击网站下载按钮。", ForeColor = Palette.Text, Margin = new(3, 8, 0, 0) });
        }
        footer.Controls.Add(files, 0, 0); footer.Controls.Add(state, 0, 1); layout.Controls.Add(footer, 0, 4); Controls.Add(layout);
        location.Text = root; state.Text = "正在启动网站浏览器…"; cancel.Enabled = false;
        back.Click += (_, _) => { if (browser.CanGoBack) browser.GoBack(); };
        forward.Click += (_, _) => { if (browser.CanGoForward) browser.GoForward(); };
        reload.Click += (_, _) => { if (browser.CoreWebView2 != null) browser.Reload(); };
        home.Click += (_, _) => Navigate("https://alphacoders.com/");
        go.Click += (_, _) => Navigate(address.Text);
        address.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Navigate(address.Text); } };
        search.Click += (_, _) => Search(); chinese.Click += async (_, _) => await SearchChineseAsync();
        query.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (ChineseTopics.ContainsChinese(query.Text)) await SearchChineseAsync(); else Search(); } };
        external.Click += (_, _) => { try { MainWindow.OpenExternal(browser.Source?.AbsoluteUri ?? "https://alphacoders.com/"); } catch (Exception ex) { state.Text = ex.Message; } };
        gallery.Click += async (_, _) =>
        {
            if (this.pageSelected != null)
            {
                try
                {
                    var core = browser.CoreWebView2 ?? throw new IOException("浏览器尚未就绪。");
                    int number = 1;
                    var pageParameter = new Uri(core.Source).Query.TrimStart('?').Split('&').FirstOrDefault(x => x.StartsWith("page="));
                    if (pageParameter != null && int.TryParse(pageParameter[5..], out int parsed)) number = Math.Clamp(parsed, 1, 200);
                    var result = await BrowserPageSearch.ReadPageAsync(core, number);
                    if (closing) return;
                    if (result.Images.Count == 0) { state.Text = "当前页没有可提取的原图，请先进入作品或壁纸列表页面。"; return; }
                    this.pageSelected(result); Close();
                }
                catch (Exception ex) { if (!closing) state.Text = "提取失败：" + ex.Message; }
            }
            else if (this.legacyGallery != null) { using var dialog = this.legacyGallery(); dialog.ShowDialog(this); }
        };
        choose.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = this.root };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                try { saveRoot(dialog.SelectedPath); this.root = dialog.SelectedPath; location.Text = this.root; }
                catch (Exception ex) { state.Text = "下载目录未保存：" + ex.Message; }
        };
        cancel.Click += (_, _) => { foreach (var download in pending.Keys.ToArray()) download.Cancel(); };
        if (autoStart) Shown += async (_, _) => await InitializeAsync();
    }

    static Button Button(string text, int width) { var button = OnlineWallpaperDialog.ActionButton(text); button.Width = width; return button; }
    internal static Uri SearchAddress(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > 200) throw new ArgumentException("请输入 1～200 个字符的作品或角色名称。");
        // Submit the query to the actual website instead of guessing a topic URL.
        return new("https://alphacoders.com/search/view?q=" + Uri.EscapeDataString(text));
    }
    void Search()
    {
        try { Navigate(SearchAddress(query.Text).AbsoluteUri); }
        catch (Exception ex) { state.Text = ex.Message; }
    }
    internal static async Task<string> EnglishQueryAsync(string text, CancellationToken token)
    {
        SearchAddress(text); // Validate before sending a query to a name service.
        if (!ChineseTopics.ContainsChinese(text)) return text;
        string? known = ChineseTopics.Find(text);
        if (known != null) return known.Replace('-', ' ');
        return await ChineseTopics.LookupEnglishAsync(text, token)
            ?? throw new ArgumentException("未找到对应英文名称。可在输入框填写英文名后点“网站搜索”，或直接在网页中浏览 Anime 分类。");
    }
    async Task SearchChineseAsync()
    {
        search.Enabled = chinese.Enabled = false; state.Text = "正在查询作品的英文名称…";
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string english = await EnglishQueryAsync(query.Text, deadline.Token);
            if (closing) return;
            query.Text = english; Search();
        }
        catch (Exception ex) { if (!closing) state.Text = "英文名称匹配失败：" + ex.Message + "。也可直接使用网页搜索或分类浏览。"; }
        finally { if (!closing) search.Enabled = chinese.Enabled = true; }
    }
    internal void Navigate(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0)
        { state.Text = "请输入有效的 HTTPS 网页地址。"; return; }
        if (browser.CoreWebView2 == null) { state.Text = "浏览器尚未就绪，请稍后重试。"; return; }
        browser.CoreWebView2.Navigate(uri.AbsoluteUri);
    }
    internal async Task InitializeAsync(bool navigateHome = true)
    {
        if (browser.CoreWebView2 != null || closing) return;
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, profile);
            if (closing) return;
            await browser.EnsureCoreWebView2Async(environment);
            if (closing) return;
            var core = browser.CoreWebView2 ?? throw new IOException("WebView2 初始化未完成。");
            core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var target) || target.Scheme != "https")
                { e.Cancel = true; state.Text = "已阻止非 HTTPS 页面或外部程序链接。"; }
                else state.Text = "正在加载网页…";
            };
            core.SourceChanged += (_, _) => { address.Text = core.Source; };
            core.HistoryChanged += (_, _) => { back.Enabled = core.CanGoBack; forward.Enabled = core.CanGoForward; };
            core.NavigationCompleted += (_, e) => state.Text = e.IsSuccess ? "网页已加载 · 在网站中选择图片并下载，即可导入壁纸库。" : "网页加载失败：" + e.WebErrorStatus + "。可刷新或在外部浏览器打开。";
            core.NewWindowRequested += (_, e) => { e.Handled = true; if (e.IsUserInitiated) Navigate(e.Uri); };
            core.DownloadStarting += DownloadStarting;
            core.ProcessFailed += (_, _) => state.Text = "浏览器进程异常，请关闭此窗口后重新打开。";
            if (navigateHome) core.Navigate("https://alphacoders.com/");
        }
        catch (Exception ex)
        {
            state.Text = "浏览器启动失败：" + ex.Message;
            browser.Visible = false;
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new(30) };
            panel.Controls.Add(new Label { Text = "网站浏览器需要 Microsoft Edge WebView2 Runtime。安装后重新打开此窗口；也可以先使用外部浏览器或批量图库。", AutoSize = true, MaximumSize = new(700, 0) });
            var install = Button("WebView2 下载页", 190); install.Click += (_, _) => MainWindow.OpenExternal("https://developer.microsoft.com/microsoft-edge/webview2/");
            panel.Controls.Add(install); ((TableLayoutPanel)browser.Parent!).Controls.Add(panel, 0, 3); panel.BringToFront();
        }
    }

    void DownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true;
        if (pageSelected != null) { e.Cancel = true; state.Text = "请点“提取本页 · 多选下载”，在图库中批量下载。"; return; }
        var operation = e.DownloadOperation;
        try
        {
            var original = new Uri(operation.Uri); AlphaProvider.ValidateAddress(original);
            string extension = Path.GetExtension(e.ResultFilePath).ToLowerInvariant();
            if (!AlphaProvider.Extensions.Contains(extension) || !(operation.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || operation.MimeType == "application/octet-stream"))
                throw new IOException("目前自动导入支持 JPG、PNG、GIF、BMP 原图；压缩包、网页和程序不会下载。可在外部浏览器处理其他格式。");
            if (closing || operation.TotalBytesToReceive > 100 * 1024 * 1024) throw new IOException("图片超过 100 MB 或窗口已关闭。");
            string id = Path.GetFileNameWithoutExtension(original.AbsolutePath);
            if (id.Length == 0 || !id.All(char.IsAsciiDigit)) id = Guid.NewGuid().ToString("N");
            if (existing().Any(x => x.OriginalUrl == original.AbsoluteUri && File.Exists(x.ManagedPath))
                || pending.Values.Any(x => x.Items.Any(y => y.Asset.OriginalUrl == original.AbsoluteUri))) throw new IOException("此原图已在壁纸库中或正在下载。");
            string name = OnlineStorage.CategoryName(category.Text), downloadRoot = OnlineStorage.RootPath(root), folder = Path.Combine(downloadRoot, name);
            OnlineStorage.CheckDirectories(folder); Directory.CreateDirectory(folder); OnlineStorage.CheckDirectories(folder);
            string path = Path.Combine(folder, $"alphacoders-{id}-{Guid.NewGuid():N}{extension}");
            var result = new OnlineDownloadResult(downloadRoot); result.Created.Add(path);
            string title = Path.GetFileNameWithoutExtension(e.ResultFilePath);
            title = new string(title.Where(c => !char.IsControl(c)).Take(WallpaperName.MaximumLength).ToArray());
            result.Items.Add((new(title.Length > 0 ? title : "网站壁纸 " + id, "image", path),
                new(id, name, browser.Source?.AbsoluteUri ?? original.AbsoluteUri, original.AbsoluteUri, downloadRoot, path)));
            e.ResultFilePath = path; pending.Add(operation, result); cancel.Enabled = true;
            operation.BytesReceivedChanged += DownloadProgress;
            operation.StateChanged += DownloadStateChanged;
            state.Text = "正在通过网站下载原图…";
        }
        catch (Exception ex) { e.Cancel = true; state.Text = "未下载：" + ex.Message; }
    }
    void DownloadProgress(object? sender, object e)
    {
        if (sender is not CoreWebView2DownloadOperation operation) return;
        if (operation.BytesReceived > 100 * 1024 * 1024) { operation.Cancel(); return; }
        if (!closing) state.Text = $"下载中：{operation.BytesReceived / 1024d / 1024:F1} MB";
    }
    void DownloadStateChanged(object? sender, object e)
    {
        if (sender is not CoreWebView2DownloadOperation operation || operation.State == CoreWebView2DownloadState.InProgress || !pending.Remove(operation, out var result)) return;
        operation.BytesReceivedChanged -= DownloadProgress; operation.StateChanged -= DownloadStateChanged;
        try
        {
            if (operation.State != CoreWebView2DownloadState.Completed || closing)
                throw new IOException("下载已取消或中断：" + operation.InterruptReason);
            string path = result.Items[0].Item.Path!;
            if (new FileInfo(path).Length > 100 * 1024 * 1024) throw new IOException("图片超过 100 MB。");
            MediaImport.ValidateImage(path); commit(result);
            if (!closing) state.Text = "原图已下载并导入壁纸库：" + result.Items[0].Item.Name;
        }
        catch (Exception ex) { OnlineStorage.Rollback(result); if (!closing) state.Text = "未导入：" + ex.Message; }
        if (!closing) cancel.Enabled = pending.Count > 0;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !closing)
        {
            closing = true;
            var downloads = pending.ToArray();
            foreach (var entry in downloads)
            {
                entry.Key.BytesReceivedChanged -= DownloadProgress; entry.Key.StateChanged -= DownloadStateChanged;
                entry.Key.Cancel();
            }
            pending.Clear(); browser.Dispose();
            foreach (var entry in downloads) OnlineStorage.Rollback(entry.Value);
        }
        base.Dispose(disposing);
    }
}
