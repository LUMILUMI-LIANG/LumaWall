using System.Security.Cryptography;
namespace LumaWall;

static class MediaRecovery
{
    internal sealed record Match(Wallpaper Item, string Category, Wallpaper? Previous = null);
    internal static List<Match> Scan(string root, IReadOnlyList<Wallpaper> existing, CancellationToken token)
    {
        var result = new List<Match>();
        if (!Directory.Exists(root)) return result;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var bySize = existing.Where(x => x.Path != null && File.Exists(x.Path)).GroupBy(x => new FileInfo(x.Path!).Length).ToDictionary(x => x.Key, x => x.ToArray());
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Hash(string path) { if (!hashes.TryGetValue(path, out var value)) { using var stream = File.OpenRead(path); value = Convert.ToHexString(SHA256.HashData(stream)); hashes[path] = value; } return value; }
        foreach (string path in Directory.EnumerateFiles(root, "*", options).Where(MediaImport.IsSupported))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                string relative = Path.GetRelativePath(root, path);
                string category = relative.Contains(Path.DirectorySeparatorChar) ? relative.Split(Path.DirectorySeparatorChar)[0] : Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
                Wallpaper? match = existing.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
                if (match == null && bySize.TryGetValue(new FileInfo(path).Length, out var candidates))
                    match = candidates.FirstOrDefault(x => Hash(x.Path!) == Hash(path));
                if (match == null)
                {
                    // Missing records are matched only by an unambiguous original basename.
                    var missing = existing.Where(x => x.Path != null && !File.Exists(x.Path) && Path.GetFileName(x.Path).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (missing.Length == 1) match = missing[0];
                }
                if (match != null && File.Exists(match.Path)) result.Add(new(match, category));
                else result.Add(new(match == null ? new(Path.GetFileNameWithoutExtension(path), MediaImport.IsVideoPath(path) ? "video" : "image", path) : match with { Path = path }, category, match));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }
}

partial class MainWindow
{
    string? ChooseImportCategory()
    {
        using var dialog = new CategoryDialog(settings.MediaCategories.Values.Concat(settings.OnlineAssets.Values.Select(x => x.Category)));
        PrepareDialog(dialog); return dialog.ShowDialog(this) == DialogResult.OK ? dialog.Category : null;
    }
    internal async Task ImportFolderAsync()
    {
        using var picker = new FolderBrowserDialog { Description = "选择图片、GIF、视频所在文件夹（包含子文件夹）" };
        if (picker.ShowDialog(this) != DialogResult.OK) return;
        string? category = ChooseImportCategory(); if (category == null) return;
        var paths = await Task.Run(() => Directory.EnumerateFiles(picker.SelectedPath, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Where(MediaImport.IsSupported).ToArray());
        await ImportFilesAsync(paths, category);
    }
    internal async Task RecoverMediaAsync(string root)
    {
        if (importing) return;
        importing = true;
        try
        {
            var snapshot = settings.Library.ToArray();
            var found = await Task.Run(() => MediaRecovery.Scan(root, snapshot, lifetime.Token), lifetime.Token);
            if (disposed || found.Count == 0) return;
            string settingsPath = Path.Combine(folder, "settings.json");
            if (File.Exists(settingsPath)) File.Copy(settingsPath, Path.Combine(folder, "settings-before-recovery-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json"), false);
            foreach (var match in found.Where(x => x.Previous != null)) RelocateOnlineFile(match.Previous!, match.Item.Path!);
            var newFiles = found.Where(x => !settings.Library.Any(item => item.Key.Equals(x.Item.Key, StringComparison.OrdinalIgnoreCase))).ToArray();
            var copied = await MediaImport.ImportAsync(newFiles.Select(x => x.Item.Path!), Path.Combine(folder, "media"), async (path, token) => { using var poster = await VideoMedia.GetPosterAsync(path, token); }, lifetime.Token);
            if (disposed) { MediaImport.Rollback(copied, Path.Combine(folder, "media")); return; }
            var copiedByName = copied.Items.GroupBy(x => x.Name).ToDictionary(x => x.Key, x => new Queue<Wallpaper>(x));
            foreach (var entry in newFiles)
            {
                found.Remove(entry);
                if (copiedByName.TryGetValue(entry.Item.Name, out var queue) && queue.Count > 0) found.Add(entry with { Item = queue.Dequeue() });
            }
            var old = settings.Library; var oldCategories = settings.MediaCategories;
            settings.Library = new(old); settings.MediaCategories = new(oldCategories, StringComparer.OrdinalIgnoreCase);
            int added = 0, classified = 0;
            foreach (var match in found)
            {
                if (!settings.Library.Any(x => x.Key.Equals(match.Item.Key, StringComparison.OrdinalIgnoreCase))) { settings.Library.Add(match.Item); added++; }
                if (!settings.OnlineAssets.ContainsKey(match.Item.Key) && !settings.MediaCategories.ContainsKey(match.Item.Key)) { settings.MediaCategories[match.Item.Key] = match.Category; classified++; }
            }
            settings.LegacyCategoriesRecovered = true;
            try { Save(); } catch { settings.Library = old; settings.MediaCategories = oldCategories; MediaImport.Rollback(copied, Path.Combine(folder, "media")); throw; }
            foreach (var item in settings.Library) if (!items.Any(x => x.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase))) items.Add(item);
            RefreshOnlineCategories(); RebuildLibraryView();
            status.Text = $"已找回 {added} 项媒体，补回 {classified} 项分类 · 原文件保持不变";
        }
        finally { importing = false; }
    }
    internal void AssignMediaCategory(IEnumerable<Wallpaper> selection, string category)
    {
        var old = settings.MediaCategories; var oldAssets = settings.OnlineAssets;
        settings.MediaCategories = new(old, StringComparer.OrdinalIgnoreCase); settings.OnlineAssets = new(oldAssets, StringComparer.OrdinalIgnoreCase);
        foreach (var item in selection)
        {
            settings.MediaCategories.Remove(item.Key);
            if (category.Length > 0) settings.MediaCategories[item.Key] = category;
            if (settings.OnlineAssets.TryGetValue(item.Key, out var asset)) settings.OnlineAssets[item.Key] = asset with { Category = category.Length > 0 ? category : "未分类" };
        }
        try { Save(); } catch { settings.MediaCategories = old; settings.OnlineAssets = oldAssets; throw; }
        RefreshOnlineCategories(); RebuildLibraryView();
    }
    void OpenMediaManager()
    {
        using var dialog = new Form { Text = "文件与分类", Size = new(1060, 680), MinimumSize = new(800, 480), Font = Font, BackColor = Palette.Surface, ForeColor = Palette.Text, StartPosition = FormStartPosition.CenterParent };
        var list = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = true, BackColor = Palette.Field, ForeColor = Palette.Text, HideSelection = false };
        list.Columns.Add("名称", 260); list.Columns.Add("分类", 170); list.Columns.Add("位置", 540);
        void RefreshRows() { list.Items.Clear(); foreach (var item in settings.Library) list.Items.Add(new ListViewItem([item.Name, settings.MediaCategories.GetValueOrDefault(item.Key) ?? settings.OnlineAssets.GetValueOrDefault(item.Key)?.Category ?? "未分类", item.Path ?? ""]) { Tag = item, ForeColor = File.Exists(item.Path) ? Palette.Text : Color.LightSalmon }); }
        var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, WrapContents = false };
        var import = OnlineWallpaperDialog.ActionButton("导入文件夹"); import.Click += async (_, _) => { try { await ImportFolderAsync(); RefreshRows(); } catch (Exception ex) { MessageBox.Show(dialog, ex.Message); } };
        var recover = OnlineWallpaperDialog.ActionButton("扫描找回/分类"); recover.Click += async (_, _) => { using var picker = new FolderBrowserDialog { SelectedPath = OnlineRoot, Description = "选择旧下载或移动后的根文件夹，按子文件夹恢复分类" }; if (picker.ShowDialog(dialog) == DialogResult.OK) { try { await RecoverMediaAsync(picker.SelectedPath); RefreshRows(); } catch (Exception ex) { MessageBox.Show(dialog, ex.Message); } } };
        var classify = OnlineWallpaperDialog.ActionButton("设置分类"); classify.Click += (_, _) => { if (list.SelectedItems.Count == 0) return; string? category = ChooseImportCategory(); if (category == null) return; try { AssignMediaCategory(list.SelectedItems.Cast<ListViewItem>().Select(x => (Wallpaper)x.Tag!).ToArray(), category); RefreshRows(); } catch (Exception ex) { MessageBox.Show(dialog, ex.Message); } };
        var locate = OnlineWallpaperDialog.ActionButton("重新定位"); locate.Click += (_, _) => { if (list.SelectedItems.Count != 1) return; var item = (Wallpaper)list.SelectedItems[0].Tag!; using var picker = new OpenFileDialog { Title = "选择移动后的图片或视频" }; if (picker.ShowDialog(dialog) == DialogResult.OK) { try { RelocateOnlineFile(item, picker.FileName); RefreshRows(); } catch (Exception ex) { MessageBox.Show(dialog, ex.Message); } } };
        actions.Controls.AddRange([import, recover, classify, locate]); dialog.Controls.Add(list); dialog.Controls.Add(actions); RefreshRows(); PrepareDialog(dialog); dialog.ShowDialog(this);
    }
}

sealed class CategoryDialog : Form
{
    readonly ComboBox category = new() { MaxLength = 64, Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDown, BackColor = Palette.Field, ForeColor = Palette.Text };
    internal string Category => category.Text.Trim();
    internal CategoryDialog(IEnumerable<string> categories)
    {
        Text = "选择分类"; Size = new(520, 240); StartPosition = FormStartPosition.CenterParent; BackColor = Palette.Surface; ForeColor = Palette.Text; Font = new("Microsoft YaHei UI", 10); Padding = new(20);
        category.Items.AddRange(categories.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Cast<object>().ToArray());
        var hint = new Label { Text = "选择已有分类或输入新分类；留空表示不加入分类。\n导入会保存媒体副本，原文件夹移动不影响播放。", Dock = DockStyle.Top, Height = 65 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, FlowDirection = FlowDirection.RightToLeft };
        var ok = EngineSettingsDialog.DialogButton("确定", DialogResult.OK, true); var cancel = EngineSettingsDialog.DialogButton("取消", DialogResult.Cancel, false);
        buttons.Controls.AddRange([ok, cancel]); Controls.Add(category); Controls.Add(hint); Controls.Add(buttons); AcceptButton = ok; CancelButton = cancel;
    }
}
