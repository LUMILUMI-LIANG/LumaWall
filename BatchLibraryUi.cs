namespace LumaWall;
partial class MainWindow
{
    void ConfirmBatchDelete()
    {
        var selected = library.SelectedItems.ToArray();
        if (selected.Length == 0) return;
        if (MessageBox.Show(this, $"确定删除选中的 {selected.Length} 项壁纸吗？\n将从壁纸库、收藏和播放列表移除，并清理应用管理的副本。外部原始文件保留。", "批量删除壁纸", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        DeleteWallpapers(selected);
    }
    internal void DeleteWallpapers(IReadOnlyList<Wallpaper> selected)
    {
        int count = 0, cleanupFailures = 0;
        try
        {
            foreach (var item in selected.DistinctBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (!items.Contains(item)) continue;
                int index = library.Items.ToList().FindIndex(x => x.Key.Equals(item.Key, StringComparison.OrdinalIgnoreCase));
                if (index < 0) { library.SetView(items); index = items.IndexOf(item); }
                library.SelectedIndex = index;
                DeleteSelectedWallpaper(); count++; if (status.Text.Contains("清理失败")) cleanupFailures++;
            }
        }
        catch (Exception ex)
        {
            status.Text = $"已删除 {count} 项，其余未删除：{ex.Message}";
            MessageBox.Show(this, status.Text, "批量删除未完成", MessageBoxButtons.OK, MessageBoxIcon.Warning); return;
        }
        status.Text = $"已删除 {count} 项壁纸 · 外部原始文件已保留" + (cleanupFailures > 0 ? $" · {cleanupFailures} 项副本清理失败" : "");
        UpdateCount();
    }
}
