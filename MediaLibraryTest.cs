using System.Reflection;
using System.Text.Json;
namespace LumaWall;
static class MediaLibraryTest
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Wait(Task task) { while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); } task.GetAwaiter().GetResult(); }
    static void Image(string path, Color color) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var image = new Bitmap(80, 45); using var g = Graphics.FromImage(image); g.Clear(color); image.Save(path); }
    public static void Audit()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaWall");
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(folder, "settings.json")))!;
        string downloads = settings.OnlineDownloadRoot ?? Path.Combine(folder, "downloads", "AlphaCoders");
        var matches = MediaRecovery.Scan(downloads, settings.Library, default);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "media-recovery-audit.txt"), string.Join("\n", matches.GroupBy(x => x.Category).Select(x => $"{x.Key}: {x.Count()} files, {x.Count(y => settings.Library.Any(i => i.Key == y.Item.Key))} matched existing records")));
    }
    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "media-library-fixtures", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string downloads = Path.Combine(root, "old-downloads"), existingPath = Path.Combine(root, "media", "existing.png");
        Image(Path.Combine(downloads, "genshin-impact", "old-name.png"), Color.Coral); Directory.CreateDirectory(Path.GetDirectoryName(existingPath)!); File.Copy(Path.Combine(downloads, "genshin-impact", "old-name.png"), existingPath);
        Image(Path.Combine(downloads, "honkai-impact-3rd", "new.png"), Color.Teal);
        string replacement = Path.Combine(downloads, "k-on", "moved.png"); Image(replacement, Color.Gold);
        var existing = new Wallpaper("旧媒体副本", "image", existingPath); var missing = new Wallpaper("已移动", "image", Path.Combine(root, "missing", "moved.png"));
        var scan = MediaRecovery.Scan(downloads, [existing, missing], default);
        Check(scan.Any(x => x.Item.Key == existing.Key && x.Category == "genshin-impact"), "Recover classification by content even when filenames differ.");
        Check(scan.Any(x => x.Previous == missing && x.Item.Path == replacement), "Recover missing path by unique basename.");
        File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(new Settings { Library = [existing, missing], Favorites = [missing.Key], LegacyCategoriesRecovered = true }));
        using (var window = new MainWindow(root, false))
        {
            window.Show(); Application.DoEvents(); Wait(window.RecoverMediaAsync(downloads));
            Settings Read() => JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(root, "settings.json")))!;
            var saved = Read();
            Check(saved.Library.Count == 3, "Recovery should not duplicate matched media.");
            Check(saved.MediaCategories[existing.Key] == "genshin-impact", "Recovered category must persist.");
            var moved = saved.Library.Single(x => x.Name == "已移动"); Check(moved.Path == replacement && saved.Favorites.Contains(moved.Key), "Relocation must preserve name and favorites.");
            var added = saved.Library.Single(x => x.Name == "new"); Check(Path.GetDirectoryName(added.Path!) == Path.Combine(root, "media"), "Unregistered recovered media must receive managed copies.");
            Wait(window.RecoverMediaAsync(downloads)); Check(Read().Library.Count == 3, "Recovery must be repeatable without duplication.");
            window.AssignMediaCategory([existing], "测试分类"); Check(Read().MediaCategories[existing.Key] == "测试分类", "Manual classification failed.");
            var categoryFilter = (ComboBox)typeof(MainWindow).GetField("libraryCategory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            categoryFilter.SelectedItem = "测试分类";
            var library = (ThumbnailLibrary)typeof(MainWindow).GetField("library", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Check(library.Items.Count == 1 && library.Items[0].Key == existing.Key, "Category filter must find recovered media.");
            window.AssignMediaCategory([existing], ""); Check(!Read().MediaCategories.ContainsKey(existing.Key), "No category option failed.");
            string sourceFolder = Path.Combine(root, "folder-import"); Image(Path.Combine(sourceFolder, "nested", "folder-image.png"), Color.Purple);
            var import = typeof(MainWindow).GetMethod("ImportFilesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Wait((Task)import.Invoke(window, new object[] { new[] { sourceFolder }, "文件夹分类" })!);
            var folderItem = Read().Library.Single(x => x.Name == "folder-image");
            Check(Read().MediaCategories[folderItem.Key] == "文件夹分类", "Recursive folder import must honor chosen category.");
            Directory.Move(sourceFolder, sourceFolder + "-moved"); Check(File.Exists(folderItem.Path), "Moving imported source folder must not break copy.");
            Check(File.Exists(added.Path), "Recovered managed copy must exist.");
            window.Close();
        }
        Directory.Move(downloads, downloads + "-moved");
        var afterMove = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(root, "settings.json")))!;
        Check(File.Exists(afterMove.Library.Single(x => x.Name == "new").Path), "Moving original folder must preserve managed file.");
        Check(Directory.GetFiles(root, "settings-before-recovery-*.json").Length > 0, "Backup before recovery required.");
        using (var dialog = new CategoryDialog(["原神", "轻音少女"])) { UiTypography.Apply(dialog, 150, false); DarkTheme.Attach(dialog); dialog.Show(); Application.DoEvents(); using var capture = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(capture, new(0, 0, dialog.Width, dialog.Height)); capture.Save(Path.Combine(root, "category.png")); dialog.Close(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "media-library-test.txt"), "PASS: hash classification, missing relink + favorites, managed copy, moved-source safety, idempotent scan, manual/empty categories, backup. Fixture: " + root);
    }
}
