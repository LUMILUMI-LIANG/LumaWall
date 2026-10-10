namespace LumaWall;

static class WallpaperName
{
    public const int MaximumLength = 160;

    public static string Normalize(string name)
    {
        string value = name.Trim();
        if (value.Length == 0) throw new ArgumentException("请输入壁纸名称。", nameof(name));
        if (value.Length > MaximumLength) throw new ArgumentException($"名称最多 {MaximumLength} 个字符。", nameof(name));
        if (value.Any(char.IsControl)) throw new ArgumentException("名称不能包含换行或控制字符。", nameof(name));
        return value;
    }
}

partial class MainWindow
{
    static bool CanRename(Wallpaper? item) => item is { Path: not null, Kind: "image" or "video" };

    void RenameWallpaper()
    {
        if (!CanRename(Selected)) return;
        var item = Selected!;
        using var dialog = new RenameWallpaperDialog(item.Name);
        PrepareDialog(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        // Rotation or an asynchronous import may change selection while the dialog is open.
        int index = items.FindIndex(x => ReferenceEquals(x, item));
        if (index < 0) return;
        RenameWallpaperAt(index, dialog.DisplayName);
    }

    void RenameSelectedWallpaper(string name)
    {
        if (!CanRename(Selected)) return;
        RenameWallpaperAt(items.FindIndex(x => ReferenceEquals(x, Selected)), name);
    }

    void RenameWallpaperAt(int index, string name)
    {
        var item = items[index];
        string normalized = WallpaperName.Normalize(name);
        if (normalized == item.Name) return;
        var renamed = item with { Name = normalized };
        var oldLibrary = settings.Library;
        int savedIndex = oldLibrary.FindIndex(x => ReferenceEquals(x, item));
        if (savedIndex < 0) savedIndex = oldLibrary.IndexOf(item);
        if (savedIndex < 0) throw new InvalidOperationException("壁纸不在媒体库中，请重新选择。");

        // Persist only the display name. Do not rename or write any media file.
        settings.Library = new(oldLibrary);
        settings.Library[savedIndex] = renamed;
        try { Save(); }
        catch { settings.Library = oldLibrary; throw; }

        items[index] = renamed;
        RenameRuntimeItem(item, renamed);
        // ThumbnailLibrary shares this list; redraw labels without reloading media.
        RebuildLibraryView(renamed, true); library.Invalidate();
        RefreshPlaylist();
        status.Text = $"已重命名：{renamed.Name} · 原文件保持不变";
    }
}

sealed class RenameWallpaperDialog : Form
{
    readonly TextBox nameInput;
    readonly Label validation;
    readonly Button saveButton;
    public string DisplayName => WallpaperName.Normalize(nameInput.Text);

    public RenameWallpaperDialog(string currentName)
    {
        Text = "重命名壁纸"; AutoScaleMode = AutoScaleMode.Dpi;
        Font = new("Microsoft YaHei UI", 10);
        ClientSize = new(520, 242); FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false;
        BackColor = Palette.Surface; ForeColor = Palette.Text;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (int height in new[] { 30, 38, 30, 54, 42 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.Controls.Add(new Label { Text = "壁纸名称", AutoSize = true, Margin = Padding.Empty }, 0, 0);
        nameInput = new TextBox { Text = currentName, MaxLength = WallpaperName.MaximumLength, Dock = DockStyle.Top,
            BackColor = Palette.Field, ForeColor = Palette.Text, BorderStyle = BorderStyle.FixedSingle, AccessibleName = "壁纸显示名称", Margin = Padding.Empty };
        layout.Controls.Add(nameInput, 0, 1);
        validation = new Label { Dock = DockStyle.Fill, ForeColor = Color.FromArgb(255, 172, 174), Margin = Padding.Empty };
        layout.Controls.Add(validation, 0, 2);
        layout.Controls.Add(new Label { Text = "只修改壁纸引擎中的显示名称。\n原文件及媒体库副本的文件名、内容保持不变。", Dock = DockStyle.Fill, ForeColor = Palette.Muted, Margin = Padding.Empty }, 0, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        saveButton = CreateButton("保存", Palette.Accent, Palette.Background);
        var cancel = CreateButton("取消", Palette.Field, Palette.Text); cancel.DialogResult = DialogResult.Cancel;
        saveButton.Click += (_, _) => { ValidateName(); if (saveButton.Enabled) DialogResult = DialogResult.OK; };
        buttons.Controls.Add(saveButton); buttons.Controls.Add(cancel); layout.Controls.Add(buttons, 0, 4); Controls.Add(layout);
        AcceptButton = saveButton; CancelButton = cancel;
        nameInput.TextChanged += (_, _) => ValidateName();
        Shown += (_, _) => { nameInput.Focus(); nameInput.SelectAll(); };
        ValidateName();
    }

    static Button CreateButton(string text, Color background, Color foreground)
    {
        var button = new Button { Text = text, Size = new(100, 36), FlatStyle = FlatStyle.Flat, BackColor = background,
            ForeColor = foreground, UseVisualStyleBackColor = false, Cursor = Cursors.Hand, Margin = new(8, 0, 0, 0) };
        button.FlatAppearance.BorderSize = 0;
        return button;
    }

    void ValidateName()
    {
        try { _ = DisplayName; validation.Text = ""; saveButton.Enabled = true; }
        catch (ArgumentException ex) { validation.Text = ex.Message.Split('\n')[0]; saveButton.Enabled = false; }
    }
}
