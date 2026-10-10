using System.Reflection;
using System.Text.Json;

namespace LumaWall;

static class DeletionTest
{
    static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    static void Delete(MainWindow window) => typeof(MainWindow).GetMethod("DeleteSelectedWallpaper", Private)!.Invoke(window, null);
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    public static void Run()
    {
        // Isolated fixtures: never read or change the user's real wallpaper library.
        string root = Path.Combine(AppContext.BaseDirectory, "deletion-fixtures-" + Guid.NewGuid().ToString("N"));
        string mediaFolder = Path.Combine(root, "media"); Directory.CreateDirectory(mediaFolder);
        string original = Path.Combine(root, "original.png"), managed = Path.Combine(mediaFolder, "copy.png");
        using (var image = new Bitmap(16, 16)) image.Save(original);
        File.Copy(original, managed);
        var imported = new Wallpaper("同名壁纸", "image", managed);
        var external = new Wallpaper("同名壁纸", "image", original);
        File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(new Settings { Library = [imported, external] }));
        using (var window = new MainWindow(root, false))
        {
            var list = Field<ThumbnailLibrary>(window, "library");
            var items = Field<List<Wallpaper>>(window, "items");
            Check(list.Items.Count == 5, "Fixture library should contain five wallpapers.");
            using var desktop = new Form(); desktop.Controls.Add(new Scene());
            Set(window, "desktopWindow", desktop); Set(window, "activeWallpaper", items[4]);
            list.SelectedIndex = 3; Delete(window);
            Check(list.Items.Count == 4 && !File.Exists(managed), "Managed copy must be deleted and its row removed.");
            Check(File.Exists(original) && !desktop.IsDisposed, "Original and unrelated playing wallpaper must remain.");
            Check(Field<Wallpaper>(window, "activeWallpaper") == external, "Unrelated desktop playback must continue.");
            var persisted = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(root, "settings.json")))!;
            Check(persisted.Library.Count == 1 && persisted.Library[0] == external, "Deletion must persist, including same-name entries.");
            list.SelectedIndex = 3; Delete(window);
            Check(File.Exists(original) && desktop.IsDisposed, "Deleting active wallpaper must close it and preserve external original.");
            while (list.Items.Count > 0) { list.SelectedIndex = 0; Delete(window); }
            Check(!Field<Button>(window, "deleteButton").Enabled, "Delete must be disabled for empty library.");
            Check(Field<Label>(window, "title").Text == "还没有壁纸", "Empty library must clear preview and display empty state.");
            Delete(window); // No selection is a harmless no-op.
        }
        using (var reopened = new MainWindow(root, false))
            Check(Field<ThumbnailLibrary>(reopened, "library").Items.Count == 0, "Deleted built-ins and imports must stay deleted after reopening.");

        // Failure to persist must leave the selected item and managed file untouched.
        File.Copy(original, managed);
        File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(new Settings { Library = [imported] }));
        Directory.CreateDirectory(Path.Combine(root, "settings.json.tmp"));
        using (var window = new MainWindow(root, false))
        {
            var list = Field<ThumbnailLibrary>(window, "library"); list.SelectedIndex = 3;
            bool failed = false;
            try { Delete(window); } catch (TargetInvocationException) { failed = true; }
            Check(failed && list.Items.Count == 4 && File.Exists(managed), "Save failure must preserve selected wallpaper and media.");
            Check(Field<Settings>(window, "settings").Library.Count == 1, "Save failure must roll back in-memory settings.");
        }
        Directory.Delete(Path.Combine(root, "settings.json.tmp"));
        // A media file referenced by two rows survives until the final row is deleted.
        File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(new Settings { Library = [imported, imported with { Name = "共享副本" }] }));
        using (var window = new MainWindow(root, false))
        {
            var list = Field<ThumbnailLibrary>(window, "library"); list.SelectedIndex = 3; Delete(window);
            Check(File.Exists(managed), "Shared media must survive deletion of the first reference.");
            list.SelectedIndex = 3; Delete(window);
            Check(!File.Exists(managed), "Shared media must be cleaned after the final reference is deleted.");
        }
        File.Delete(original); File.Delete(Path.Combine(root, "settings.json"));
        Directory.Delete(mediaFolder); Directory.Delete(root);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "delete-test.txt"),
            "PASS: managed-copy cleanup; original-file protection; same-name selection; unrelated playback preserved; active wallpaper stopped; built-in deletion persisted; empty library; restart; save-failure rollback; shared-media cleanup.");
    }
}
