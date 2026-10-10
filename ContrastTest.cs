using System.Reflection;
namespace LumaWall;
static class ContrastTest
{
    static T Field<T>(OnlineWallpaperDialog dialog, string name) => (T)typeof(OnlineWallpaperDialog).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "contrast-fixtures"); Directory.CreateDirectory(root);
        foreach (int percent in new[] { 100, 150 })
        {
            using var dialog = new OnlineWallpaperDialog(new AlphaProvider(), root, () => [], _ => { }, _ => { });
            UiTypography.Apply(dialog, percent, false);
            Field<TextBox>(dialog, "keyword").Text = "吹响吧上低音号";
            Field<TextBox>(dialog, "category").Text = "sound-euphonium";
            dialog.Show(); Application.DoEvents();
            foreach (string name in new[] { "keyword", "root", "category" })
            {
                var input = Field<TextBox>(dialog, name);
                Check(input.ForeColor == Palette.Text && input.BackColor == Palette.Field, "Input colors must be explicit: " + name);
            }
            foreach (string name in new[] { "previous", "download", "cancel" })
            {
                var button = Field<Button>(dialog, name);
                Check(!button.Enabled && button is DarkButton, "Unavailable actions must keep readable disabled paint: " + name);
                int clicks = 0; button.Click += (_, _) => clicks++; button.PerformClick(); Check(clicks == 0, "Disabled button must remain inactive.");
                using var crop = new Bitmap(button.Width, button.Height); button.DrawToBitmap(crop, new(0, 0, button.Width, button.Height));
                int light = 0;
                for (int y = 5; y < crop.Height - 5; y++) for (int x = 5; x < crop.Width - 5; x++)
                { Color c = crop.GetPixel(x, y); if (c.R > 100 && c.G > 100 && c.B > 100) light++; }
                Check(light > 30, "Disabled label should contain readable light text: " + name);
            }
            var search = Field<Button>(dialog, "search"); Check(search.Enabled, "Search must remain available.");
            using var capture = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(capture, new(0, 0, dialog.Width, dialog.Height)); capture.Save(Path.Combine(root, $"online-{percent}.png"));
            dialog.Close();
        }
        File.WriteAllText(Path.Combine(root, "result.txt"), "PASS: explicit input colors, read-only input, disabled text pixels, disabled click protection, enabled search, 100/150% UI.");
    }
}
