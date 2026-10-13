using System.Reflection;
using System.Text.Json;

namespace LumaWall;

static class LibraryFileLocationTest
{
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "file-location-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string image = Path.Combine(root, "壁纸 图片 & 01.png"), video = Path.Combine(root, "视频 01.mp4");
        using (var bitmap = new Bitmap(64, 36)) bitmap.Save(image);
        File.WriteAllText(video, "launch fixture only");
        foreach (var item in new[] { new Wallpaper("图片", "image", image), new Wallpaper("视频", "video", video) })
        {
            var launch = WallpaperFileLocation.CreateLaunch(item)!;
            Check(launch.FileName == "explorer.exe" && launch.UseShellExecute, "Files must be revealed using Explorer.");
            Check(launch.Arguments == $"/select,\"{item.Path}\"", "Chinese filenames, spaces and punctuation must retain a single quoted path.");
        }
        Check(WallpaperFileLocation.CreateLaunch(null) == null && WallpaperFileLocation.CreateLaunch(new("内置", "aurora")) == null, "Built-in scenes and empty selections have no file location.");
        var missingFile = WallpaperFileLocation.CreateLaunch(new("丢失", "image", Path.Combine(root, "missing.png")))!;
        Check(missingFile.FileName == root && missingFile.Arguments.Length == 0, "A missing file should still allow inspecting an existing parent directory.");
        bool rejected = false;
        try { WallpaperFileLocation.CreateLaunch(new("丢失目录", "image", Path.Combine(root, "missing-folder", "wallpaper.png"))); }
        catch (FileNotFoundException) { rejected = true; }
        Check(rejected, "A missing directory must report relocation guidance rather than opening an unrelated folder.");
        File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(new Settings { Library = [new("图片", "image", image)], LegacyCategoriesRecovered = true }));
        using var window = new MainWindow(root, false);
        window.Show(); Application.DoEvents();
        var library = Field<ThumbnailLibrary>(window, "library");
        var button = Field<Button>(window, "openFileLocationButton");
        Check(!button.Enabled, "Built-in scene must disable the file button.");
        library.SelectedIndex = library.Items.ToList().FindIndex(x => x.Path == image);
        Check(button.Enabled && button.Text == "打开文件目录", "File wallpapers must expose the location button.");
        window.Size = window.MinimumSize; Application.DoEvents();
        var footer = (TableLayoutPanel)button.Parent!;
        Check(button.Bounds.Right <= footer.ClientSize.Width && button.Width >= 100, "Location button must fit in the library footer.");
        using (var capture = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(capture, new(0, 0, window.Width, window.Height)); capture.Save(Path.Combine(root, "file-location-ui.png")); }
        window.Close();
        Check(File.Exists(image) && File.Exists(video), "Revealing a file must preserve media.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "file-location-test.txt"), "PASS: Chinese/spaced image and video paths, Explorer selection, missing-file folder fallback, missing-directory guidance, built-in disablement, library button layout, media preservation.");
    }
}
