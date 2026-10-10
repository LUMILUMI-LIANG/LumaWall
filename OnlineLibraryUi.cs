using System.Diagnostics;

namespace LumaWall;

partial class MainWindow
{
    readonly ComboBox libraryCategory = new();
    bool updatingCategories;
    internal string OnlineRoot => string.IsNullOrWhiteSpace(settings.OnlineDownloadRoot)
        ? Path.Combine(folder, "downloads", "AlphaCoders") : settings.OnlineDownloadRoot;

    void RefreshOnlineCategories()
    {
        if (libraryCategory.IsDisposed) return;
        string selected = libraryCategory.SelectedItem?.ToString() ?? "全部分类";
        updatingCategories = true;
        try
        {
            libraryCategory.Items.Clear(); libraryCategory.Items.AddRange(["全部分类", "本地 / 内置"]);
            foreach (string category in settings.OnlineAssets.Values.Select(x => x.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase))
                libraryCategory.Items.Add("Alpha Coders / " + category);
            foreach (string category in settings.MediaCategories.Values.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase))
                libraryCategory.Items.Add(category);
            int index = libraryCategory.Items.IndexOf(selected); libraryCategory.SelectedIndex = index >= 0 ? index : 0;
        }
        finally { updatingCategories = false; }
    }
    bool MatchesOnlineCategory(Wallpaper item)
    {
        string selected = libraryCategory.SelectedItem?.ToString() ?? "全部分类";
        if (selected == "全部分类") return true;
        if (libraryCategory.SelectedIndex > 1 && !selected.StartsWith("Alpha Coders / ")) return settings.MediaCategories.TryGetValue(item.Key, out var localCategory) && selected.Equals(localCategory, StringComparison.OrdinalIgnoreCase);
        bool online = settings.OnlineAssets.TryGetValue(item.Key, out var asset);
        return selected == "本地 / 内置" ? !online && !settings.MediaCategories.ContainsKey(item.Key) : online && selected.Equals("Alpha Coders / " + asset!.Category, StringComparison.OrdinalIgnoreCase);
    }
    void OpenOnlineLibrary()
    {
        using var dialog = new OnlineWallpaperDialog(new AlphaProvider(), OnlineRoot,
            () => settings.OnlineAssets.Values.ToArray(), SetOnlineRoot, CommitOnlineDownloads, useBrowserSearch: true);
        PrepareDialog(dialog); dialog.ShowDialog(this);
    }
    void OpenOnlineFiles()
    {
        using var dialog = new OnlineFilesDialog(this); PrepareDialog(dialog); dialog.ShowDialog(this);
    }
    internal void SetOnlineRoot(string root)
    {
        string normalized = OnlineStorage.RootPath(root); OnlineStorage.CheckDirectories(normalized);
        string? previous = settings.OnlineDownloadRoot; settings.OnlineDownloadRoot = normalized;
        try { Save(); } catch { settings.OnlineDownloadRoot = previous; throw; }
    }
    internal void CommitOnlineDownloads(OnlineDownloadResult result)
    {
        if (disposed) { OnlineStorage.Rollback(result); throw new ObjectDisposedException(nameof(MainWindow)); }
        foreach (var entry in result.Items)
            if (!result.Created.Contains(entry.Item.Path!) || !OnlineStorage.Owns(entry.Asset, entry.Item.Path!))
            { OnlineStorage.Rollback(result); throw new IOException("下载文件与本批次记录不匹配。"); }
        var oldLibrary = settings.Library; var oldAssets = settings.OnlineAssets; var oldDates = settings.ImportedAt;
        settings.Library = [.. oldLibrary, .. result.Items.Select(x => x.Item)];
        settings.OnlineAssets = new(oldAssets, StringComparer.OrdinalIgnoreCase);
        settings.ImportedAt = new(oldDates, StringComparer.OrdinalIgnoreCase);
        foreach (var (item, asset) in result.Items)
        { settings.OnlineAssets[item.Key] = asset; settings.ImportedAt[item.Key] = DateTimeOffset.UtcNow; }
        try { Save(); }
        catch
        {
            settings.Library = oldLibrary; settings.OnlineAssets = oldAssets; settings.ImportedAt = oldDates;
            OnlineStorage.Rollback(result); throw;
        }
        result.Committed = true; items.AddRange(result.Items.Select(x => x.Item));
        updatingLibrary = true;
        try
        {
            RefreshOnlineCategories(); libraryCategory.SelectedIndex = 0; librarySearch.Clear();
            libraryType.SelectedIndex = 0; favoritesOnly.Checked = false; librarySort.SelectedIndex = 2;
        }
        finally { updatingLibrary = false; }
        RebuildLibraryView(result.Items.LastOrDefault().Item); RefreshPlaylist();
        status.Text = $"已下载并导入 {result.Items.Count} 张原图 · 文件位置：{result.Root}";
    }

    internal IReadOnlyList<(Wallpaper Item, OnlineAsset Asset)> OnlineEntries()
        => settings.Library.Where(x => settings.OnlineAssets.ContainsKey(x.Key))
            .Select(x => (x, settings.OnlineAssets[x.Key])).ToArray();

    internal void RelocateOnlineFile(Wallpaper item, string replacementPath)
    {
        settings.OnlineAssets.TryGetValue(item.Key, out var asset);
        if (!settings.Library.Contains(item))
            throw new IOException("这张壁纸已不在在线图片库中。");
        if (File.Exists(item.Path)) throw new IOException("原文件仍存在。此功能用于重新关联已在资源管理器移动的文件。");
        if (monitorRuntimes.Values.Any(x => x.Active?.Key == item.Key || x.Preparing?.Key == item.Key || x.HeldWallpaper?.Key == item.Key
            || x.Playlist.Running && x.Profile.Playlist.Keys.Contains(item.Key)))
            throw new IOException("请先停止使用这张图片的壁纸和轮换，再重新定位。");
        string path = Path.GetFullPath(replacementPath);
        if (!AlphaProvider.Extensions.Contains(Path.GetExtension(path))) throw new IOException("请选择图片文件。");
        MediaImport.ValidateImage(path);
        var replacement = item with { Path = path };
        if (settings.Library.Any(x => x.Key == replacement.Key && x != item)) throw new IOException("此文件已在壁纸库中。");
        CaptureCurrentMonitor();
        var oldLibrary = settings.Library; var oldAssets = settings.OnlineAssets; var oldProfiles = settings.Monitors;
        var oldVirtualProfiles = settings.VirtualDesktops;
        var oldFavorites = settings.Favorites; var oldDates = settings.ImportedAt;
        string Map(string key) => key.Equals(item.Key, StringComparison.OrdinalIgnoreCase) ? replacement.Key : key;
        Dictionary<string, T> MapDictionary<T>(Dictionary<string, T> source)
            => source.ToDictionary(x => Map(x.Key), x => x.Value, StringComparer.OrdinalIgnoreCase);
        settings.Library = oldLibrary.Select(x => x == item ? replacement : x).ToList();
        settings.OnlineAssets = new(oldAssets, StringComparer.OrdinalIgnoreCase); settings.OnlineAssets.Remove(item.Key);
        // A manually chosen file is an external original; deleting its library
        // entry must not grant ownership or remove that file.
        if (asset != null) settings.OnlineAssets[replacement.Key] = asset with { ManagedPath = path, Owned = false };
        var oldCategories = settings.MediaCategories; settings.MediaCategories = MapDictionary(oldCategories);
        settings.Favorites = oldFavorites.Select(Map).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.ImportedAt = MapDictionary(oldDates);
        RemapVirtualWallpaperReferences(item.Key, replacement.Key);
        settings.Monitors = oldProfiles.ToDictionary(x => x.Key, x =>
        {
            var profile = x.Value.Copy(); profile.WallpaperKey = profile.WallpaperKey == null ? null : Map(profile.WallpaperKey);
            profile.Transforms = MapDictionary(profile.Transforms); profile.LayoutModes = MapDictionary(profile.LayoutModes);
            profile.Playlist.Keys = profile.Playlist.Keys.Select(Map).ToList(); return profile;
        }, StringComparer.OrdinalIgnoreCase);
        foreach (var runtime in monitorRuntimes.Values) runtime.Profile = settings.Monitors[runtime.Id];
        BindMonitor(CurrentMonitor);
        try { Save(); }
        catch
        {
            settings.MediaCategories = oldCategories;
            settings.Library = oldLibrary; settings.OnlineAssets = oldAssets; settings.Monitors = oldProfiles;
            settings.VirtualDesktops = oldVirtualProfiles;
            settings.Favorites = oldFavorites; settings.ImportedAt = oldDates;
            foreach (var runtime in monitorRuntimes.Values) runtime.Profile = oldProfiles[runtime.Id];
            BindMonitor(CurrentMonitor); throw;
        }
        int index = items.FindIndex(x => x == item); if (index >= 0) items[index] = replacement; else items.Add(replacement);
        foreach (var runtime in monitorRuntimes.Values) runtime.Playlist.UpdateOptions(runtime.Profile.Playlist);
        RefreshOnlineCategories(); RebuildLibraryView(replacement); RefreshPlaylist();
        status.Text = "图片路径已更新 · 所选文件保持原样";
    }

    internal static void OpenExternal(string address)
    {
        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
    }
}

sealed class OnlineFilesDialog : Form
{
    readonly MainWindow owner;
    readonly TextBox root = new() { ReadOnly = true, Dock = DockStyle.Fill };
    readonly ListView entries = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true };
    readonly Button locate;
    readonly Label feedback = new() { Dock = DockStyle.Fill, ForeColor = Palette.Muted };
    public OnlineFilesDialog(MainWindow owner)
    {
        this.owner = owner; Text = "在线图片 · 文件路径管理"; Font = new("Microsoft YaHei UI", 10);
        BackColor = Palette.Surface; ForeColor = Palette.Text; StartPosition = FormStartPosition.CenterParent;
        Size = new(1060, 620); MinimumSize = new(800, 480); ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), ColumnCount = 1, RowCount = 5 };
        foreach (int height in new[] { 38, 38 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 46)); layout.RowStyles.Add(new(SizeType.Absolute, 44));
        var rootRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3 }; rootRow.ColumnStyles.Add(new(SizeType.Absolute, 114)); rootRow.ColumnStyles.Add(new(SizeType.Percent, 100)); rootRow.ColumnStyles.Add(new(SizeType.Absolute, 110));
        rootRow.Controls.Add(new Label { Text = "以后下载到", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        rootRow.Controls.Add(root, 1, 0);
        var browse = OnlineWallpaperDialog.ActionButton("选择目录"); browse.Click += (_, _) => TryAction(() =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = owner.OnlineRoot, Description = "选择今后下载原图的根目录" };
            if (dialog.ShowDialog(this) == DialogResult.OK) { owner.SetOnlineRoot(dialog.SelectedPath); root.Text = owner.OnlineRoot; feedback.Text = "已保存新目录，已有图片继续使用原路径。"; }
        });
        rootRow.Controls.Add(browse, 2, 0); layout.Controls.Add(rootRow, 0, 0);
        layout.Controls.Add(new Label { Text = "每个分类对应一个子文件夹。修改下载目录影响以后下载；已有文件路径在下方保留。", Dock = DockStyle.Fill, ForeColor = Palette.Muted }, 0, 1);
        entries.BackColor = Palette.Field; entries.ForeColor = Palette.Text;
        entries.Columns.Add("图片", 230); entries.Columns.Add("分类", 145); entries.Columns.Add("实际文件路径", 555);
        layout.Controls.Add(entries, 0, 2); layout.Controls.Add(feedback, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        var open = OnlineWallpaperDialog.ActionButton("打开所在文件夹"); open.Click += (_, _) => TryAction(() =>
        { if (Selected is { } selected) MainWindow.OpenExternal(Path.GetDirectoryName(selected.Item.Path!)!); });
        var source = OnlineWallpaperDialog.ActionButton("打开来源网页"); source.Click += (_, _) => TryAction(() =>
        { if (Selected is { } selected) { AlphaProvider.ValidateAddress(new Uri(selected.Asset.SourcePage)); MainWindow.OpenExternal(selected.Asset.SourcePage); } });
        locate = OnlineWallpaperDialog.ActionButton("重新定位文件"); locate.Enabled = false; locate.Click += (_, _) => TryAction(() =>
        {
            if (Selected is not { } selected) return;
            using var dialog = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif", Title = "选择已移动的图片文件" };
            if (dialog.ShowDialog(this) == DialogResult.OK) { owner.RelocateOnlineFile(selected.Item, dialog.FileName); RefreshRows(); feedback.Text = "已重新关联文件。引擎不会删除手动选择的原文件。"; }
        });
        var close = OnlineWallpaperDialog.ActionButton("关闭"); close.DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([open, source, locate, close]); layout.Controls.Add(buttons, 0, 4); Controls.Add(layout); CancelButton = close;
        entries.SelectedIndexChanged += (_, _) =>
        {
            locate.Enabled = Selected is { } selected && !File.Exists(selected.Item.Path);
            if (Selected is { } entry) feedback.Text = (File.Exists(entry.Item.Path) ? "文件存在 · " : "文件缺失，可重新定位 · ") + entry.Item.Path;
        };
        root.Text = owner.OnlineRoot; RefreshRows();
    }
    (Wallpaper Item, OnlineAsset Asset)? Selected => entries.SelectedItems.Count == 0 ? null : ((Wallpaper, OnlineAsset))entries.SelectedItems[0].Tag!;
    void RefreshRows()
    {
        entries.Items.Clear();
        foreach (var entry in owner.OnlineEntries())
            entries.Items.Add(new ListViewItem([entry.Item.Name, entry.Asset.Category, entry.Item.Path ?? ""])
            { Tag = entry, ToolTipText = entry.Item.Path, ForeColor = File.Exists(entry.Item.Path) ? Palette.Text : Color.LightSalmon });
    }
    void TryAction(Action action) { try { action(); } catch (Exception ex) { feedback.Text = ex.Message; } }
}
