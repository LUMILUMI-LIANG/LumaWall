namespace LumaWall;

partial class MainWindow
{
    readonly TextBox librarySearch = new();
    readonly ComboBox libraryType = new(), librarySort = new(), layoutMode = new();
    readonly ComboBox thumbnailSizes = new();
    static readonly int[] thumbnailWidths = [160, 220, 280, 360, 440];
    bool updatingThumbnailSize;
    readonly CheckBox favoritesOnly = new();
    Button favoriteButton = null!;
    bool updatingLibrary;

    Control CreateLibraryFilters()
    {
        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Margin = Padding.Empty };
        filters.RowStyles.Add(new(SizeType.Absolute, 40)); filters.RowStyles.Add(new(SizeType.Absolute, 36));
        filters.ColumnStyles.Add(new(SizeType.Percent, 100)); filters.ColumnStyles.Add(new(SizeType.Absolute, 120));
        filters.ColumnStyles.Add(new(SizeType.Absolute, 132)); filters.ColumnStyles.Add(new(SizeType.Absolute, 108));
        librarySearch.PlaceholderText = "搜索壁纸名称…"; librarySearch.AccessibleName = "搜索壁纸"; librarySearch.Dock = DockStyle.Fill;
        librarySearch.BackColor = Palette.Field; librarySearch.ForeColor = Palette.Text; librarySearch.BorderStyle = BorderStyle.FixedSingle; librarySearch.Margin = new(0, 4, 10, 8);
        filters.Controls.Add(librarySearch, 0, 0);
        StyleCombo(libraryType); libraryType.AccessibleName = "壁纸类型"; libraryType.Items.AddRange(["全部类型", "图片", "GIF 动图", "视频", "动态场景"]); libraryType.SelectedIndex = 0;
        StyleCombo(librarySort); librarySort.AccessibleName = "壁纸排序"; librarySort.Items.AddRange(["导入顺序", "名称 A–Z", "最近导入"]); librarySort.SelectedIndex = 0;
        libraryType.Margin = new(0, 4, 10, 8); librarySort.Margin = new(0, 4, 10, 8);
        filters.Controls.Add(libraryType, 1, 0); filters.Controls.Add(librarySort, 2, 0);
        favoritesOnly.Text = "只看收藏"; favoritesOnly.Dock = DockStyle.Fill; favoritesOnly.Margin = new(0, 2, 0, 8); filters.Controls.Add(favoritesOnly, 3, 0);
        librarySearch.TextChanged += (_, _) => RebuildLibraryView(); libraryType.SelectedIndexChanged += (_, _) => RebuildLibraryView();
        librarySort.SelectedIndexChanged += (_, _) => RebuildLibraryView(); favoritesOnly.CheckedChanged += (_, _) => RebuildLibraryView();
        StyleCombo(libraryCategory); libraryCategory.AccessibleName = "壁纸分类"; libraryCategory.Margin = new(0, 0, 0, 2);
        filters.Controls.Add(libraryCategory, 0, 1); filters.SetColumnSpan(libraryCategory, 2); libraryCategory.Margin = new(0, 0, 10, 2); RefreshOnlineCategories();
        StyleCombo(thumbnailSizes); thumbnailSizes.AccessibleName = "缩略图大小"; thumbnailSizes.Margin = new(0, 0, 0, 2);
        thumbnailSizes.Items.AddRange(["缩略图 · 小（160）", "缩略图 · 中（220）", "缩略图 · 大（280）", "缩略图 · 特大（360）", "缩略图 · 超大（440）", "缩略图 · 自定义"]);
        filters.Controls.Add(thumbnailSizes, 2, 1); filters.SetColumnSpan(thumbnailSizes, 2);
        library.ThumbnailWidth = settings.ThumbnailWidth = Math.Clamp(settings.ThumbnailWidth, 160, 480); SyncThumbnailSize();
        thumbnailSizes.SelectionChangeCommitted += (_, _) => { if (!updatingThumbnailSize && thumbnailSizes.SelectedIndex is >= 0 and < 5) library.ThumbnailWidth = thumbnailWidths[thumbnailSizes.SelectedIndex]; };
        library.ThumbnailSizeChanged += (_, _) => { settings.ThumbnailWidth = library.ThumbnailWidth; SyncThumbnailSize(); TrySaveSettings("缩略图大小未保存"); };
        tooltips.SetToolTip(thumbnailSizes, "选择预设规格，或在壁纸库中按住 Ctrl 滚动鼠标滚轮调整");
        libraryCategory.SelectedIndexChanged += (_, _) => { if (!updatingCategories) RebuildLibraryView(); };
        return filters;
    }

    void SyncThumbnailSize()
    {
        updatingThumbnailSize = true;
        try { int index = Array.IndexOf(thumbnailWidths, library.ThumbnailWidth); thumbnailSizes.Items[5] = $"缩略图 · 自定义（{library.ThumbnailWidth}）"; thumbnailSizes.SelectedIndex = index < 0 ? 5 : index; }
        finally { updatingThumbnailSize = false; }
    }

    void RebuildLibraryView(Wallpaper? preferred = null, bool preservePreview = false)
    {
        if (updatingLibrary) return;
        var previous = Selected; var target = preferred ?? previous;
        string query = librarySearch.Text.Trim();
        IEnumerable<Wallpaper> view = items.Where(item => (query.Length == 0 || item.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            && MatchesOnlineCategory(item)
            && (!favoritesOnly.Checked || settings.Favorites.Contains(item.Key, StringComparer.OrdinalIgnoreCase))
            && (libraryType.SelectedIndex switch
            {
                1 => item.Kind == "image" && !string.Equals(Path.GetExtension(item.Path), ".gif", StringComparison.OrdinalIgnoreCase),
                2 => item.Kind == "image" && string.Equals(Path.GetExtension(item.Path), ".gif", StringComparison.OrdinalIgnoreCase),
                3 => item.Kind == "video", 4 => item.Path == null, _ => true
            }));
        if (librarySort.SelectedIndex == 1) view = view.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase);
        else if (librarySort.SelectedIndex == 2) view = view.OrderByDescending(x => settings.ImportedAt.GetValueOrDefault(x.Key));
        var visible = query.Length == 0 && !favoritesOnly.Checked && libraryType.SelectedIndex == 0 && librarySort.SelectedIndex == 0 && libraryCategory.SelectedIndex <= 0 ? items : view.ToList();
        updatingLibrary = true;
        try
        {
            library.SetItems(visible);
            int selected = target == null ? -1 : visible.FindIndex(x => ReferenceEquals(x, target));
            library.SelectedIndex = selected >= 0 ? selected : visible.Count > 0 ? 0 : -1;
        }
        finally { updatingLibrary = false; }
        UpdateCount(); UpdateFavoriteButton();
        if (!preservePreview || Selected?.Key != previous?.Key) SelectWallpaper();
        else if (Selected is { } selected) { title.Text = selected.Name; tooltips.SetToolTip(title, selected.Name); }
    }

    void UpdateFavoriteButton()
    {
        if (favoriteButton == null) return;
        favoriteButton.Enabled = Selected != null;
        bool favorite = Selected is { } item && settings.Favorites.Contains(item.Key, StringComparer.OrdinalIgnoreCase);
        favoriteButton.Text = favorite ? "★ 已收藏" : "☆ 收藏";
        favoriteButton.ForeColor = favorite ? Palette.Accent : Palette.Text;
    }

    void ToggleFavorite()
    {
        if (Selected is not { } item) return;
        var previous = settings.Favorites;
        settings.Favorites = new(previous);
        if (settings.Favorites.Contains(item.Key, StringComparer.OrdinalIgnoreCase)) settings.Favorites.RemoveAll(x => x.Equals(item.Key, StringComparison.OrdinalIgnoreCase));
        else settings.Favorites.Add(item.Key);
        try { Save(); } catch { settings.Favorites = previous; throw; }
        RebuildLibraryView(item, true);
    }

    void ChangeLayoutMode()
    {
        if (updatingControls || Selected is not { } item || layoutMode.SelectedIndex < 0) return;
        var mode = (WallpaperLayoutMode)layoutMode.SelectedIndex;
        var previous = settings.LayoutModes;
        settings.LayoutModes = new(previous, StringComparer.OrdinalIgnoreCase) { [item.Key] = mode };
        try { Save(); } catch { settings.LayoutModes = previous; layoutMode.SelectedIndex = (int)preview.LayoutMode; throw; }
        preview.LayoutMode = mode;
        if (activeWallpaper?.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase) == true && desktopScene != null) desktopScene.LayoutMode = mode;
        status.Text = "画面适配已更新 · 缩放和位置设置保留";
    }
}
