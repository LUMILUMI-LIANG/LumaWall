using System.Diagnostics;

namespace LumaWall;

partial class MainWindow
{
    Button openFileLocationButton = null!;

    void OpenWallpaperLocation()
    {
        var launch = WallpaperFileLocation.CreateLaunch(Selected);
        if (launch == null) return;
        Process.Start(launch);
        status.Text = File.Exists(Selected!.Path)
            ? "已在资源管理器中选中壁纸文件"
            : "原文件已移动或删除 · 已打开原目录，可在“文件与分类”中重新定位";
    }
}

static class WallpaperFileLocation
{
    internal static ProcessStartInfo? CreateLaunch(Wallpaper? item)
    {
        if (item?.Path == null) return null;
        string file = Path.GetFullPath(item.Path);
        if (File.Exists(file))
            return new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true };
        string? directory = Path.GetDirectoryName(file);
        if (directory != null && Directory.Exists(directory))
            return new ProcessStartInfo(directory) { UseShellExecute = true };
        throw new FileNotFoundException("找不到壁纸文件或所在目录。请在“文件与分类”中重新定位文件。", file);
    }
}
