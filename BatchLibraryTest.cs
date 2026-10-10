using System.Reflection;
using System.Text.Json;
namespace LumaWall;
static class BatchLibraryTest
{
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static T Field<T>(MainWindow w, string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(w)!;
    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "batch-library-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "media"));
        string externalPath = Path.Combine(root, "external.png"), managedPath = Path.Combine(root, "media", "copy.png");
        using (var image = new Bitmap(40, 40)) image.Save(externalPath); File.Copy(externalPath, managedPath);
        var external = new Wallpaper("外部原图", "image", externalPath); var managed = new Wallpaper("媒体副本", "image", managedPath);
        File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(new Settings { Library = [external, managed], Favorites = [external.Key, managed.Key], MediaCategories = new() { [external.Key] = "原神", [managed.Key] = "原神" }, LegacyCategoriesRecovered = true }));
        using var window = new MainWindow(root, false); window.Show(); Application.DoEvents();
        var library = Field<ThumbnailLibrary>(window, "library");
        library.SelectIndex(0); library.SelectIndex(2, true); Check(library.SelectedItems.Count == 2, "Ctrl select must retain previous selection.");
        library.SelectIndex(2, true); Check(library.SelectedItems.Count == 1, "Ctrl select must toggle off.");
        library.SelectIndex(1); library.SelectIndex(4, false, true); Check(library.SelectedItems.Count == 4, "Shift select must select contiguous range.");
        var category = Field<ComboBox>(window, "libraryCategory"); Check(category.Items.Contains("原神") && !category.Items.Contains("分类 / 原神"), "Category name should be plain.");
        category.SelectedItem = "原神"; Check(library.Items.Count == 2 && library.SelectedItems.Count <= 1, "Filtering must discard hidden selections.");
        library.SelectIndex(0); library.SelectIndex(1, true); Check(library.SelectedItems.Count == 2, "Filtered multi selection failed.");
        using (var capture = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(capture, new(0, 0, window.Width, window.Height)); capture.Save(Path.Combine(root, "multi-select.png")); }
        window.DeleteWallpapers(library.SelectedItems);
        var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(root, "settings.json")))!;
        Check(saved.Library.Count == 0 && saved.Favorites.Count == 0 && saved.MediaCategories.Count == 0, "Batch deletion must clean entries, favorites and categories.");
        Check(File.Exists(externalPath) && !File.Exists(managedPath), "Batch deletion must preserve external originals and clean owned copies.");
        Check(library.Items.Count == 3, "Builtins should remain after deleting selected filtered images.");
        window.Close();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "batch-library-test.txt"), "PASS: Ctrl toggle, Shift range, filter isolation, plain category, batch delete, original safety, metadata cleanup.");
    }
}
