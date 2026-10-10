using System.Text.Json;

namespace LumaWall;

partial class MainWindow : Form
{
    readonly string folder;
    readonly Settings settings;
    readonly ThumbnailLibrary library = new();
    readonly Scene preview = new() { EditingEnabled = true, VideoMaximumWidth = 640 };
    readonly PreviewFrame previewFrame;
    readonly Label status = new(), title = new(), libraryCount = new();
    readonly ComboBox screens = new(), fps = new();
    readonly NumericUpDown zoomValue = new(), horizontalValue = new(), verticalValue = new();
    readonly AdjustmentSlider zoomSlider = new(), horizontalSlider = new(), verticalSlider = new();
    readonly Panel transformArea;
    readonly Button renameButton, deleteButton, applyButton, resetButton;
    readonly NotifyIcon tray;
    readonly ToolTip tooltips = new();
    readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 450 };
    Form? desktopWindow;
    Scene? desktopScene;
    Wallpaper? activeWallpaper;
    readonly List<Wallpaper> items;
    bool updatingControls, transformsDirty, disposed;
    Wallpaper? Selected => library.SelectedIndex >= 0 && library.SelectedIndex < library.Items.Count ? library.Items[library.SelectedIndex] : null;

    public MainWindow(string? dataFolder = null, bool showTray = true, IVirtualDesktopSource? virtualSource = null)
    {
        folder = dataFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaWall");
        Text = ""; Icon = AppIcon.Value; ShowIcon = false; AutoScaleMode = AutoScaleMode.Dpi;
        var working = Screen.PrimaryScreen!.WorkingArea;
        Size = new(Math.Min(1920, working.Width - 48), Math.Min(1200, working.Height - 48));
        MinimumSize = new(Math.Min(1180, working.Width - 48), Math.Min(760, working.Height - 48));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Palette.Background; ForeColor = Palette.Text; Font = new("Microsoft YaHei UI", 10);
        try { settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(folder, "settings.json"))) ?? new(); } catch { settings = new(); }
        settings.TextScale = Math.Clamp(settings.TextScale, 90, 150);
        Size = new(Math.Clamp(settings.WindowWidth, MinimumSize.Width, Math.Max(MinimumSize.Width, working.Width - 48)), Math.Clamp(settings.WindowHeight, MinimumSize.Height, Math.Max(MinimumSize.Height, working.Height - 48)));
        settings.MediaCategories = new(settings.MediaCategories ?? [], StringComparer.OrdinalIgnoreCase);
        settings.Library ??= []; settings.DeletedBuiltIns ??= [];
        settings.OnlineAssets = new((settings.OnlineAssets ?? []).Where(x => x.Value != null).ToDictionary(x => x.Key, x => x.Value), StringComparer.OrdinalIgnoreCase);
        settings.Transforms = new(settings.Transforms ?? [], StringComparer.OrdinalIgnoreCase);
        settings.Fps = FrameRateOptions.Normalize(settings.Fps);
        settings.Volume = Math.Clamp(settings.Volume, 0, 100);
        items = [new("极光之境", "aurora"), new("深海潮汐", "ocean"), new("星际漫游", "space")];
        items.RemoveAll(x => settings.DeletedBuiltIns.Contains(x.Kind));
        items.AddRange(settings.Library.Where(x => x.Path != null));
        PrepareVirtualDesktopProfiles(dataFolder == null, virtualSource); InitializeMonitorProfiles(); InitializePlaylistState();
        library.CacheFolder = Path.Combine(folder, "thumbnails"); library.IsFavorite = item => settings.Favorites.Contains(item.Key, StringComparer.OrdinalIgnoreCase);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Margin = Padding.Empty };
        root.ColumnStyles.Add(new(SizeType.Percent, 100));
        root.RowStyles.Add(new(SizeType.Absolute, 86)); root.RowStyles.Add(new(SizeType.Percent, 100)); root.RowStyles.Add(new(SizeType.Absolute, 34)); Controls.Add(root);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(28, 0, 20, 0), ColumnCount = 7, Margin = Padding.Empty };
        header.RowStyles.Add(new(SizeType.Percent, 100));
        header.ColumnStyles.Add(new(SizeType.Absolute, 245)); header.ColumnStyles.Add(new(SizeType.Percent, 100)); header.ColumnStyles.Add(new(SizeType.Absolute, 180)); header.ColumnStyles.Add(new(SizeType.Absolute, 88)); header.ColumnStyles.Add(new(SizeType.Absolute, 88)); header.ColumnStyles.Add(new(SizeType.Absolute, 118)); header.ColumnStyles.Add(new(SizeType.Absolute, 138));
        header.Controls.Add(TextLabel("LUMA WALL", 25, true), 0, 0);
        var subtitle = TextLabel("本地动态壁纸", 13); subtitle.ForeColor = Palette.Muted; header.Controls.Add(subtitle, 1, 0);
        header.Controls.Add(Button("＋ 导入图片 / 视频", Import), 2, 0); header.Controls.Add(Button("设置", ShowEngineSettings), 3, 0);
        header.Controls.Add(Button("界面", ShowAppearanceSettings), 4, 0); header.Controls.Add(Button("运行状态", ShowDiagnostics), 5, 0); header.Controls.Add(Button("收起到托盘", () => Hide()), 6, 0); root.Controls.Add(header, 0, 0);

        body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Padding = new(20, 0, 20, 14), Margin = Padding.Empty };
        body.RowStyles.Add(new(SizeType.Percent, 100));
        body.ColumnStyles.Add(new(SizeType.Percent, 100)); body.ColumnStyles.Add(new(SizeType.Absolute, 16)); body.ColumnStyles.Add(new(SizeType.Absolute, 448)); root.Controls.Add(body, 0, 1);
        body.Controls.Add(new PartitionGrip(delta => ResizeInspector(delta), SavePartitionSizes, () => ResetPartition(false)) { Dock = DockStyle.Fill, Margin = Padding.Empty }, 1, 0);
        body.Resize += (_, _) => UpdateInspectorWidth();
        var libraryPanel = new BorderedPanel { Dock = DockStyle.Fill, Padding = new(18, 12, 18, 18), BackColor = Palette.Surface, Margin = Padding.Empty };
        var libraryLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Margin = Padding.Empty };
        libraryLayout.ColumnStyles.Add(new(SizeType.Percent, 100));
        libraryLayout.RowStyles.Add(new(SizeType.Absolute, 46)); libraryLayout.RowStyles.Add(new(SizeType.Absolute, 78)); libraryLayout.RowStyles.Add(new(SizeType.Percent, 100));
        libraryLayout.RowStyles.Add(new(SizeType.Absolute, 42));
        var libraryHeader = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
        libraryHeader.Controls.Add(new Label { Text = "我的壁纸", Font = new(Font.FontFamily, 17, FontStyle.Bold), AutoSize = true, Margin = new(0, 0, 14, 0) });
        libraryCount.AutoSize = true; libraryCount.ForeColor = Palette.Muted; libraryCount.Margin = new(0, 7, 0, 0); libraryHeader.Controls.Add(libraryCount);
        var onlineButton = Button("在线找壁纸", OpenOnlineLibrary); onlineButton.Dock = DockStyle.None; onlineButton.Size = new(120, 34); onlineButton.Margin = new(12, 0, 0, 0);
        var filesButton = Button("文件与分类", OpenMediaManager); filesButton.Dock = DockStyle.None; filesButton.Size = new(130, 34); filesButton.Margin = new(8, 0, 0, 0);
        libraryHeader.Controls.Add(onlineButton); libraryHeader.Controls.Add(filesButton);
        libraryLayout.Controls.Add(libraryHeader, 0, 0); libraryLayout.Controls.Add(CreateLibraryFilters(), 0, 1); libraryLayout.Controls.Add(library, 0, 2); library.Margin = Padding.Empty;
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new(SizeType.Percent, 100)); footer.ColumnStyles.Add(new(SizeType.Absolute, 110)); footer.ColumnStyles.Add(new(SizeType.Absolute, 140));
        var dropHint = TextLabel("Ctrl 多选 · Shift 连选 · 双击查看大图", 9); dropHint.ForeColor = Palette.Muted; footer.Controls.Add(dropHint, 0, 0);
        viewImageButton = Button("查看大图", OpenImageViewer); viewImageButton.Margin = new(4, 4, 4, 0); footer.Controls.Add(viewImageButton, 1, 0);
        playlistToggle = Button("播放列表 ›", () => SetPlaylistSidebar(!settings.PlaylistSidebarVisible)); playlistToggle.Margin = new(4, 4, 0, 0); footer.Controls.Add(playlistToggle, 2, 0);
        libraryLayout.Controls.Add(footer, 0, 3);
        libraryWorkspace = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, Margin = Padding.Empty };
        libraryWorkspace.ColumnStyles.Add(new(SizeType.Percent, 100)); libraryWorkspace.ColumnStyles.Add(new(SizeType.Absolute, 0)); libraryWorkspace.ColumnStyles.Add(new(SizeType.Absolute, 0));
        libraryWorkspace.Controls.Add(libraryLayout, 0, 0);
        playlistSidebar = CreatePlaylistPanel(); playlistSidebar.Margin = Padding.Empty; libraryWorkspace.Controls.Add(playlistSidebar, 2, 0);
        playlistGrip = new PartitionGrip(delta => ResizePlaylist(delta), SavePartitionSizes, () => ResetPartition(true)) { Dock = DockStyle.Fill, Margin = Padding.Empty }; libraryWorkspace.Controls.Add(playlistGrip, 1, 0);
        libraryWorkspace.Resize += (_, _) => UpdatePlaylistSidebar();
        libraryPanel.Controls.Add(libraryWorkspace); body.Controls.Add(libraryPanel, 0, 0);

        var inspector = new BorderedPanel { Dock = DockStyle.Fill, Padding = new(16), BackColor = Palette.Surface, AutoScroll = true, Margin = Padding.Empty };
        var controls = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = Padding.Empty };
        controls.ColumnStyles.Add(new(SizeType.Percent, 100)); inspector.Controls.Add(controls); body.Controls.Add(inspector, 2, 0);
        controls.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        void FitInspectorContent()
        {
            int width = Math.Max(120, inspector.ClientSize.Width - inspector.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
            controls.MaximumSize = new(width, 0); controls.Width = width;
        }
        inspector.Resize += (_, _) => FitInspectorContent();
        inspector.Layout += (_, _) => FitInspectorContent();
        title.AutoEllipsis = true; title.UseMnemonic = false; title.Font = new(Font.FontFamily, 16, FontStyle.Bold); title.TextAlign = ContentAlignment.MiddleLeft;
        var titleRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        titleRow.ColumnStyles.Add(new(SizeType.Percent, 100)); titleRow.ColumnStyles.Add(new(SizeType.Absolute, 100));
        favoriteButton = Button("☆ 收藏", ToggleFavorite); favoriteButton.Margin = Padding.Empty; title.Dock = DockStyle.Fill;
        titleRow.Controls.Add(title, 0, 0); titleRow.Controls.Add(favoriteButton, 1, 0); AddRow(controls, titleRow, 36);
        previewFrame = new(preview) { Dock = DockStyle.Fill }; AddRow(controls, previewFrame, 152);
        var hint = TextLabel("拖动预览移动 · 滚轮缩放", 9); hint.ForeColor = Palette.Muted; AddRow(controls, hint, 30); AddRow(controls, new Panel(), 10);

        transformArea = new BorderedPanel { BackColor = Palette.Field, Padding = new(12, 6, 12, 8) };
        var transformLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1, Margin = Padding.Empty };
        transformLayout.RowStyles.Add(new(SizeType.Absolute, 30)); transformLayout.RowStyles.Add(new(SizeType.Absolute, 66)); transformLayout.RowStyles.Add(new(SizeType.Absolute, 66)); transformLayout.RowStyles.Add(new(SizeType.Absolute, 66)); transformLayout.RowStyles.Add(new(SizeType.Percent, 100));
        transformLayout.Controls.Add(TextLabel("画面调整", 12, true), 0, 0);
        transformLayout.Controls.Add(AdjustmentRow("缩放", zoomSlider, zoomValue, 25, 300, 100), 0, 1);
        transformLayout.Controls.Add(AdjustmentRow("水平移动", horizontalSlider, horizontalValue, -100, 100, 0), 0, 2);
        transformLayout.Controls.Add(AdjustmentRow("垂直移动", verticalSlider, verticalValue, -100, 100, 0), 0, 3);
        resetButton = Button("重置画面", () => UpdateTransform(new())); resetButton.MinimumSize = new(120, 32); resetButton.Height = 32; resetButton.Dock = DockStyle.Right; resetButton.Margin = Padding.Empty; transformLayout.Controls.Add(resetButton, 0, 4);
        transformArea.Controls.Add(transformLayout); AddRow(controls, transformArea, 285); AddRow(controls, new Panel(), 8);

        var playArea = new BorderedPanel { BackColor = Palette.Field, Padding = new(12, 6, 12, 8) };
        var playLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 2, Margin = Padding.Empty };
        playLayout.ColumnStyles.Add(new(SizeType.Absolute, 76)); playLayout.ColumnStyles.Add(new(SizeType.Percent, 100));
        playLayout.RowStyles.Add(new(SizeType.Absolute, 30)); playLayout.RowStyles.Add(new(SizeType.Absolute, 36)); playLayout.RowStyles.Add(new(SizeType.Absolute, 36)); playLayout.RowStyles.Add(new(SizeType.Absolute, 36));
        playLayout.Controls.Add(TextLabel("播放设置", 12, true), 0, 0); playLayout.SetColumnSpan(playLayout.GetControlFromPosition(0, 0)!, 2);
        playLayout.Controls.Add(TextLabel("显示器", 10), 0, 1); playLayout.Controls.Add(screens, 1, 1);
        playLayout.Controls.Add(TextLabel("帧率", 10), 0, 2); playLayout.Controls.Add(fps, 1, 2);
        playLayout.Controls.Add(TextLabel("适配", 10), 0, 3); playLayout.Controls.Add(layoutMode, 1, 3); StyleCombo(layoutMode);
        layoutMode.Items.AddRange(["填充（裁剪边缘）", "完整显示（保留黑边）", "拉伸", "居中（原始大小）", "平铺"]);
        layoutMode.SelectionChangeCommitted += (_, _) => ChangeLayoutMode();
        playArea.Controls.Add(playLayout); AddRow(controls, playArea, 154); AddRow(controls, new Panel(), 8);
        AddRow(controls, CreateVirtualDesktopRow(), 38); AddRow(controls, new Panel(), 8);
        foreach (var combo in new[] { screens, fps }) StyleCombo(combo);
        foreach (var screen in Screen.AllScreens) screens.Items.Add($"{screen.DeviceName.Replace("\\\\.\\", "")} · {screen.Bounds.Width}×{screen.Bounds.Height}"); screens.SelectedIndex = Array.FindIndex(Screen.AllScreens, x => x.DeviceName == selectedMonitorId);
        fps.Items.AddRange(FrameRateOptions.Labels); fps.SelectedIndex = Array.IndexOf(FrameRateOptions.Values, settings.Fps);
        AddRow(controls, CreateAudioControls(), 108); AddRow(controls, new Panel(), 8);
        applyButton = Button("应用到桌面", Apply); applyButton.BackColor = Palette.Accent; applyButton.ForeColor = Palette.Background; applyButton.Font = new(Font.FontFamily, 11, FontStyle.Bold); AddRow(controls, applyButton, 44); AddRow(controls, new Panel(), 8);
        var playbackButtons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty }; playbackButtons.ColumnStyles.Add(new(SizeType.Percent, 50)); playbackButtons.ColumnStyles.Add(new(SizeType.Percent, 50));
        var pauseButton = Button("暂停 / 继续", TogglePause); pauseButton.Margin = new(0, 0, 4, 0);
        var stopButton = Button("停止当前屏幕", Stop); stopButton.Margin = new(4, 0, 0, 0);
        playbackButtons.Controls.Add(pauseButton, 0, 0); playbackButtons.Controls.Add(stopButton, 1, 0); AddRow(controls, playbackButtons, 40); AddRow(controls, new Panel(), 8);
        var libraryButtons = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        libraryButtons.ColumnStyles.Add(new(SizeType.Percent, 50)); libraryButtons.ColumnStyles.Add(new(SizeType.Percent, 50));
        renameButton = Button("重命名", RenameWallpaper); renameButton.Margin = new(0, 0, 4, 0);
        tooltips.SetToolTip(renameButton, "修改壁纸库显示名称（F2），原文件不变");
        deleteButton = Button("删除壁纸", ConfirmDelete); deleteButton.BackColor = Color.FromArgb(70, 38, 49); deleteButton.Margin = new(4, 0, 0, 0);
        libraryButtons.Controls.Add(renameButton, 0, 0); libraryButtons.Controls.Add(deleteButton, 1, 0); AddRow(controls, libraryButtons, 38);

        status.Dock = DockStyle.Fill; status.UseMnemonic = false; status.TextAlign = ContentAlignment.MiddleLeft; status.Padding = new(24, 0, 0, 0); status.ForeColor = Palette.Muted; status.BackColor = Palette.Surface; status.AutoEllipsis = true; root.Controls.Add(status, 0, 2);
        var menu = new ContextMenuStrip(); menu.Items.Add("打开壁纸库", null, (_, _) => PresentLibrary()); menu.Items.Add("暂停 / 继续当前屏幕", null, (_, _) => TogglePause()); menu.Items.Add("停止所有壁纸", null, (_, _) => { StopAllMonitors(); TrySaveSettings("停止状态未保存"); }); menu.Items.Add("检查更新", null, (_, _) => RunAsync(CheckAppUpdatesAsync)); menu.Items.Add("退出", null, (_, _) => Close());
        tray = new NotifyIcon { Icon = AppIcon.Value, Text = "Luma Wall", Visible = showTray, ContextMenuStrip = menu }; tray.DoubleClick += (_, _) => PresentLibrary();

        library.SetItems(items); UpdateCount(); library.SelectedIndexChanged += (_, _) => { if (!updatingLibrary) { SelectWallpaper(); UpdateCount(); } };
        library.ImageActivated += (_, _) => OpenImageViewer();
        library.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { OpenImageViewer(); e.Handled = true; e.SuppressKeyPress = true; return; }
            if (e.KeyCode is not (Keys.Delete or Keys.F2)) return;
            if (e.KeyCode == Keys.F2) renameButton.PerformClick(); else deleteButton.PerformClick();
            e.Handled = true; e.SuppressKeyPress = true;
        };
        ConfigureFileDrop(library); ConfigureFileDrop(libraryPanel); ConfigureFileDrop(this);
        preview.TransformChanged += UpdateTransform;
        preview.PlaybackFailed += ex => { if (!disposed) status.Text = CurrentMonitor.HoldingPrevious
            ? "当前桌面壁纸未能加载 · 暂时保留上一画面 · " + ex.Message : "视频预览失败：" + ex.Message; };
        preview.MouseUp += (_, _) => TryFlushTransforms();
        screens.SelectedIndexChanged += (_, _) => SelectMonitor();
        fps.SelectedIndexChanged += (_, _) => { if (fps.SelectedIndex < 0) return; settings.Fps = FrameRateOptions.Values[fps.SelectedIndex]; preview.Fps = settings.Fps; foreach (var monitor in monitorRuntimes.Values) if (monitor.Scene != null) monitor.Scene.Fps = settings.Fps; try { Save(); } catch (Exception ex) { status.Text = "帧率设置未保存：" + ex.Message; } };
        foreach (var number in new[] { zoomValue, horizontalValue, verticalValue }) { number.ValueChanged += (_, _) => UpdateFromNumbers(); number.Validated += (_, _) => TryFlushTransforms(); }
        ConnectSlider(zoomSlider, zoomValue); ConnectSlider(horizontalSlider, horizontalValue); ConnectSlider(verticalSlider, verticalValue);
        saveTimer.Tick += (_, _) => TryFlushTransforms();
        FormClosing += (_, _) => { var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds; settings.WindowWidth = bounds.Width; settings.WindowHeight = bounds.Height; TryFlushTransforms(); CaptureCurrentMonitor(); TrySaveSettings("退出前设置未保存"); }; FormClosed += (_, _) => tray.Dispose();
        VisibleChanged += (_, _) => UpdatePreviewVisibility();
        Resize += (_, _) => UpdatePreviewVisibility();
        preview.Fps = settings.Fps;
        preview.Paused = CurrentMonitor.Profile.Paused;
        preview.RenderingSuspended = !Visible || WindowState == FormWindowState.Minimized;
        UpdatePreviewAspect();
        if (items.Count > 0) library.SelectedIndex = 0; else SelectWallpaper();
        RefreshPlaylist();
        InitializeEngineLifecycle(dataFolder == null, showTray);
        ApplyMainTextScale(settings.TextScale);
        UpdateInspectorWidth();
        UpdatePlaylistSidebar();
        DarkTheme.Attach(this);
        StartVirtualDesktopTracking();
        Shown += (_, _) => { if (!settings.LegacyCategoriesRecovered) RunAsync(() => RecoverMediaAsync(OnlineRoot)); };
    }
    Label TextLabel(string text, float size, bool bold = false) => new() { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new(Font.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular), Margin = Padding.Empty };
    static void AddRow(TableLayoutPanel layout, Control control, int height)
    {
        int row = layout.RowCount++; layout.RowStyles.Add(new(SizeType.Absolute, height)); control.Dock = DockStyle.Fill; control.Margin = Padding.Empty; layout.Controls.Add(control, 0, row);
    }
    void StyleCombo(ComboBox combo)
    {
        combo.DropDownStyle = ComboBoxStyle.DropDownList; combo.FlatStyle = FlatStyle.Flat; combo.Dock = DockStyle.Fill; combo.BackColor = Palette.Field; combo.ForeColor = Palette.Text; combo.Margin = new(0, 4, 0, 0); combo.DrawMode = DrawMode.OwnerDrawFixed; combo.ItemHeight = 24;
        combo.DrawItem += (_, e) =>
        {
            using var bg = new SolidBrush((e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0 ? Color.FromArgb(44, 66, 78) : Palette.Field); e.Graphics.FillRectangle(bg, e.Bounds);
            int index = e.Index >= 0 ? e.Index : combo.SelectedIndex;
            if (index >= 0) TextRenderer.DrawText(e.Graphics, combo.Items[index]?.ToString(), Font, e.Bounds, Palette.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
    }
    Control AdjustmentRow(string text, AdjustmentSlider slider, NumericUpDown number, int min, int max, int initial)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2, Margin = Padding.Empty };
        row.ColumnStyles.Add(new(SizeType.Percent, 100)); row.ColumnStyles.Add(new(SizeType.Absolute, 78)); row.ColumnStyles.Add(new(SizeType.Absolute, 22));
        row.RowStyles.Add(new(SizeType.Absolute, 32)); row.RowStyles.Add(new(SizeType.Percent, 100));
        row.Controls.Add(TextLabel(text, 10), 0, 0);
        slider.Maximum = max; slider.Minimum = min; slider.Value = initial; slider.Dock = DockStyle.Fill; slider.AccessibleName = text; slider.Margin = new(2, 0, 2, 0); row.Controls.Add(slider, 0, 1); row.SetColumnSpan(slider, 3);
        number.Minimum = min; number.Maximum = max; number.Value = initial; number.DecimalPlaces = 1; number.Increment = 1; number.BackColor = Palette.Field; number.ForeColor = Palette.Text; number.BorderStyle = BorderStyle.FixedSingle; number.Dock = DockStyle.Fill; number.Margin = new(0, 2, 0, 0); number.AccessibleName = text + "百分比"; row.Controls.Add(number, 1, 0);
        var unit = TextLabel("%", 9); unit.ForeColor = Palette.Muted; unit.TextAlign = ContentAlignment.MiddleRight; row.Controls.Add(unit, 2, 0); return row;
    }
    void ConnectSlider(AdjustmentSlider slider, NumericUpDown number)
    {
        slider.ValueChanged += (_, _) => { if (!updatingControls) number.Value = slider.Value; };
        slider.MouseUp += (_, _) => TryFlushTransforms();
    }
    Button Button(string text, Action action)
    {
        var b = new DarkButton { Text = text, Dock = DockStyle.Fill, MinimumSize = new(80, 34), FlatStyle = FlatStyle.Flat, BackColor = Palette.Field, ForeColor = Palette.Text, Cursor = Cursors.Hand, Margin = new(4, 20, 4, 20), UseVisualStyleBackColor = false };
        b.FlatAppearance.BorderSize = 0; b.FlatAppearance.MouseOverBackColor = Color.FromArgb(47, 67, 79);
        b.Click += (_, _) => { try { action(); } catch (Exception ex) { status.Text = "操作失败：" + ex.Message; MessageBox.Show(this, ex.Message, "Luma Wall", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }; return b;
    }
    void UpdateCount() => libraryCount.Text = (library.Items.Count == items.Count ? $"{items.Count} 张壁纸" : $"{library.Items.Count} / {items.Count} 张") + (library.SelectedItems.Count > 1 ? $" · 已选 {library.SelectedItems.Count}" : "");
    void UpdatePreviewAspect()
    {
        if (screens.SelectedIndex < 0) return; var bounds = Screen.AllScreens[screens.SelectedIndex].Bounds;
        previewFrame.AspectRatio = bounds.Width / (double)bounds.Height; previewFrame.LayoutScene();
        preview.LayoutReferenceSize = bounds.Size;
    }
    void SelectWallpaper()
    {
        viewImageButton.Enabled = Selected is { Path: not null, Kind: not "video" };
        bool selected = Selected != null; deleteButton.Enabled = applyButton.Enabled = transformArea.Enabled = selected;
        deleteButton.Text = library.SelectedItems.Count > 1 ? $"删除选中（{library.SelectedItems.Count}）" : "删除壁纸";
        renameButton.Enabled = CanRename(Selected);
        UpdateFavoriteButton();
        layoutMode.Enabled = selected;
        if (!selected) { preview.ClearScene(); title.Text = items.Count == 0 ? "还没有壁纸" : "没有匹配壁纸"; UpdateTransformControls(new()); status.Text = items.Count == 0 ? "壁纸库为空 · 导入图片、GIF 或视频开始" : "尝试修改搜索或筛选条件"; return; }
        var item = Selected!;
        try
        {
            LoadPreview(item); var transform = settings.Transforms.TryGetValue(item.Key, out var saved) && saved != null ? saved.Normalize() : new WallpaperTransform(); preview.SetTransform(transform); UpdateTransformControls(transform);
            preview.LayoutMode = settings.LayoutModes.GetValueOrDefault(item.Key); layoutMode.SelectedIndex = (int)preview.LayoutMode;
            title.Text = item.Name; tooltips.SetToolTip(title, item.Name); status.Text = item.Kind == "video" ? "正在加载视频预览…" : "预览已就绪 · 拖动或滚轮调整画面，点击“应用到桌面”播放";
            if (item.Kind == "video" && preview.Video is { } source) ReportPreviewReadyAsync(item, source);
        }
        catch (Exception ex) { preview.ClearScene(); applyButton.Enabled = transformArea.Enabled = false; status.Text = "无法加载：" + ex.Message; }
    }
    async void ReportPreviewReadyAsync(Wallpaper item, VideoPlayback source)
    {
        try
        {
            await source.Ready.WaitAsync(TimeSpan.FromSeconds(15), lifetime.Token);
            if (!disposed && ReferenceEquals(preview.Video, source) && Selected?.Key == item.Key && !CurrentMonitor.HoldingPrevious)
                status.Text = "视频预览已就绪 · 应用到桌面后播放原声";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            // Preview errors may arise on a background virtual desktop. Report in
            // the status area without a modal dialog that interrupts desktop changes.
            if (!disposed && ReferenceEquals(preview.Video, source) && Selected?.Key == item.Key)
                status.Text = CurrentMonitor.HoldingPrevious ? "当前桌面壁纸未能加载 · 暂时保留上一画面 · " + ex.Message : "视频预览失败：" + ex.Message;
        }
    }
    void LoadPreview(Wallpaper item)
    {
        if (item.Kind == "video" && activeWallpaper?.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase) == true && desktopScene?.Video is { } source)
            preview.LoadVideoPreview(item, source);
        else preview.LoadScene(item);
    }
    void UpdatePreviewPlaybackSource()
    {
        if (disposed || Disposing || IsDisposed) { preview.ClearScene(); return; }
        if (Selected is not { Kind: "video" } item) return;
        var source = activeWallpaper?.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase) == true ? desktopScene?.Video : null;
        if (source == null ? !preview.IsSharedVideo : ReferenceEquals(preview.Video, source)) return;
        var transform = preview.Transform;
        try { LoadPreview(item); preview.SetTransform(transform); }
        catch (Exception ex) { preview.ClearScene(); status.Text = "视频预览失败：" + ex.Message; }
    }
    void UpdateTransformControls(WallpaperTransform value)
    {
        updatingControls = true;
        try
        {
            zoomValue.Value = Math.Clamp((decimal)value.Scale * 100, 25, 300); horizontalValue.Value = Math.Clamp((decimal)value.OffsetX * 100, -100, 100); verticalValue.Value = Math.Clamp((decimal)value.OffsetY * 100, -100, 100);
            zoomSlider.Value = (int)Math.Round(zoomValue.Value); horizontalSlider.Value = (int)Math.Round(horizontalValue.Value); verticalSlider.Value = (int)Math.Round(verticalValue.Value);
        }
        finally { updatingControls = false; }
    }
    void UpdateFromNumbers()
    {
        if (updatingControls) return;
        UpdateTransform(new((float)zoomValue.Value / 100, (float)horizontalValue.Value / 100, (float)verticalValue.Value / 100));
    }
    void UpdateTransform(WallpaperTransform value)
    {
        if (Selected is not Wallpaper item) return;
        value = value.Normalize(); preview.SetTransform(value); UpdateTransformControls(value); settings.Transforms[item.Key] = value;
        bool live = activeWallpaper?.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase) == true;
        if (live) desktopScene?.SetTransform(value);
        transformsDirty = true; saveTimer.Stop(); saveTimer.Start();
        status.Text = live ? "画面调整已同步到桌面" : "画面调整已更新 · 应用到桌面后生效";
    }
    void TryFlushTransforms()
    {
        saveTimer.Stop(); if (!transformsDirty) return;
        try { Save(); transformsDirty = false; } catch (Exception ex) { status.Text = "画面设置尚未保存：" + ex.Message; }
    }
    void Import()
    {
        using var dialog = new OpenFileDialog { Filter = "壁纸媒体|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.webm;*.mkv|图片与动图|*.png;*.jpg;*.jpeg;*.bmp;*.gif|视频|*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.webm;*.mkv", Multiselect = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var paths = dialog.FileNames; string? category = ChooseImportCategory(); if (category == null) return; RunAsync(() => ImportFilesAsync(paths, category));
    }
    void Save()
    {
        CaptureCurrentMonitor(); settings.SelectedMonitorId = selectedMonitorId; CaptureVirtualDesktopProfile();
        Directory.CreateDirectory(folder); string path = Path.Combine(folder, "settings.json"), temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings)); File.Move(temporary, path, true);
    }
    void ConfirmDelete()
    {
        if (library.SelectedItems.Count > 1) { ConfirmBatchDelete(); return; }
        if (Selected is not Wallpaper item) return;
        var detail = item.Path == null ? "该内置场景将从壁纸库移除。" :
            settings.OnlineAssets.TryGetValue(item.Key, out var asset) && OnlineStorage.Owns(asset, item.Path)
            ? "将从壁纸库移除，并删除引擎下载的这张图片文件。"
            : "将移除壁纸并清理媒体库中的副本，原始文件不会删除。";
        CaptureCurrentMonitor();
        if (monitorRuntimes.Values.Any(x => x.Active?.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase) == true))
            detail += "\n这张壁纸正在桌面播放，删除后将停止相关屏幕上的播放。";
        if (MessageBox.Show(this, $"确定删除“{item.Name}”吗？\n\n{detail}", "删除壁纸", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        if (!ReferenceEquals(Selected, item)) { status.Text = "壁纸选择已变化，请重新选择后删除。"; return; }
        DeleteSelectedWallpaper();
    }
    void DeleteSelectedWallpaper()
    {
        if (Selected is not Wallpaper item) return; int index = items.FindIndex(x => ReferenceEquals(x, item));
        if (index < 0) return; CaptureCurrentMonitor();
        var oldLibrary = settings.Library; var oldDeleted = settings.DeletedBuiltIns; var oldTransforms = settings.Transforms;
        var oldPlaylistKeys = settings.Playlist.Keys;
        var oldProfiles = settings.Monitors; var oldFavorites = settings.Favorites; var oldImportedAt = settings.ImportedAt; var oldLayouts = settings.LayoutModes;
        var oldMediaCategories = settings.MediaCategories;
        var oldOnlineAssets = settings.OnlineAssets; var onlineAsset = oldOnlineAssets.GetValueOrDefault(item.Key);
        var oldVirtualProfiles = settings.VirtualDesktops;
        settings.Library = new(oldLibrary); settings.Library.Remove(item);
        settings.DeletedBuiltIns = item.Path == null ? oldDeleted.Append(item.Kind).Distinct().ToList() : oldDeleted;
        bool hasOtherReference = settings.Library.Any(x => x.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase));
        RemoveVirtualWallpaperReferences(item.Key, hasOtherReference);
        settings.Monitors = oldProfiles.ToDictionary(x => x.Key, x => x.Value.Copy(), StringComparer.OrdinalIgnoreCase);
        foreach (var profile in settings.Monitors.Values)
        {
            if (!hasOtherReference) { profile.Transforms.Remove(item.Key); profile.LayoutModes.Remove(item.Key); }
            profile.Playlist.Keys.RemoveAll(x => x.Equals(item.Key, StringComparison.OrdinalIgnoreCase));
            if (profile.WallpaperKey?.Equals(item.Key, StringComparison.OrdinalIgnoreCase) == true) { profile.WallpaperKey = null; profile.PlaylistEnabled = false; }
        }
        if (!hasOtherReference)
        {
            settings.Favorites = oldFavorites.Where(x => !x.Equals(item.Key, StringComparison.OrdinalIgnoreCase)).ToList();
            settings.ImportedAt = new(oldImportedAt, StringComparer.OrdinalIgnoreCase); settings.ImportedAt.Remove(item.Key);
            settings.OnlineAssets = new(oldOnlineAssets, StringComparer.OrdinalIgnoreCase); settings.OnlineAssets.Remove(item.Key);
            settings.MediaCategories = new(oldMediaCategories, StringComparer.OrdinalIgnoreCase); settings.MediaCategories.Remove(item.Key);
        }
        foreach (var monitor in monitorRuntimes.Values) monitor.Profile = settings.Monitors[monitor.Id];
        BindMonitor(CurrentMonitor);
        try { Save(); transformsDirty = false; saveTimer.Stop(); }
        catch
        {
            settings.Library = oldLibrary; settings.DeletedBuiltIns = oldDeleted; settings.Monitors = oldProfiles;
            settings.Favorites = oldFavorites; settings.ImportedAt = oldImportedAt;
            settings.OnlineAssets = oldOnlineAssets; settings.MediaCategories = oldMediaCategories;
            settings.VirtualDesktops = oldVirtualProfiles;
            foreach (var monitor in monitorRuntimes.Values) monitor.Profile = oldProfiles[monitor.Id];
            settings.Transforms = oldTransforms; settings.LayoutModes = oldLayouts; settings.Playlist.Keys = oldPlaylistKeys; throw;
        }
        bool stopped = false;
        foreach (var monitor in monitorRuntimes.Values)
        {
            if (monitor.Preparing?.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase) == true) { monitor.Request?.Cancel(); monitor.PendingScene?.ClearScene(); }
            if (monitor.Active?.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase) == true) { stopped = true; StopMonitor(monitor, true); }
            else if (monitor.HeldWallpaper?.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase) == true) { stopped = true; StopMonitor(monitor, false); }
            monitor.Playlist.UpdateOptions(monitor.Profile.Playlist);
        }
        PublishMonitor(CurrentMonitor); preview.ClearScene();
        items.RemoveAt(index); RefreshOnlineCategories(); RebuildLibraryView(items.Count > 0 ? items[Math.Min(index, items.Count - 1)] : null); RefreshPlaylist();
        string result = $"已删除：{item.Name}" + (stopped ? " · 桌面播放已停止" : "");
        if (item.Path != null && !settings.Library.Any(x => string.Equals(x.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
            try { if (IsManagedMedia(item.Path) || onlineAsset != null && OnlineStorage.Owns(onlineAsset, item.Path)) File.Delete(item.Path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result += " · 媒体副本清理失败：" + ex.Message; }
        status.Text = result + (items.Count == 0 ? " · 可导入新的图片、GIF 或视频" : "");
    }
    bool IsManagedMedia(string path)
    {
        var mediaFolder = Path.GetFullPath(Path.Combine(folder, "media")); var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(fullPath), mediaFolder, StringComparison.OrdinalIgnoreCase)) return false;
        if (!Directory.Exists(mediaFolder) || !File.Exists(fullPath)) return false;
        return (File.GetAttributes(mediaFolder) & FileAttributes.ReparsePoint) == 0 && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) == 0;
    }
    void Apply()
    {
        if (Selected is not Wallpaper item) return; TryFlushTransforms(); StopPlaylist();
        RunAsync(() => SwitchDesktopAsync(item, settings.Playlist.FadeSeconds));
    }
    void TogglePause() { CaptureCurrentMonitor(); CurrentMonitor.Profile.Paused = !CurrentMonitor.Profile.Paused; SetMonitorPaused(CurrentMonitor); TrySaveSettings("暂停状态未保存"); status.Text = CurrentMonitor.Profile.Paused ? "画面、原声与轮换倒计时已暂停" : CurrentMonitor.AutoPaused ? "手动暂停已取消 · 系统自动暂停仍生效" : "画面与原声已继续"; }
    void Stop()
    {
        CaptureCurrentMonitor(); StopMonitor(CurrentMonitor, true); TrySaveSettings("停止状态未保存");
        status.Text = "桌面壁纸已停止 · 原壁纸已恢复";
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true; virtualDesktopTimer.Dispose(); (virtualDesktopSource as IDisposable)?.Dispose(); lifecycleMonitor?.Dispose(); lifetime.Cancel(); TryFlushTransforms(); StopAllMonitors(false); playlistTimer.Dispose(); saveTimer.Dispose(); tooltips.Dispose(); tray.Dispose(); lifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}

class BorderedPanel : Panel
{
    public BorderedPanel() { DoubleBuffered = true; }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var pen = new Pen(Palette.Border); e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); }
}
