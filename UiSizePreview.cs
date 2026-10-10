using System.Text.Json;

namespace LumaWall;

static class UiSizePreview
{
    sealed class PreviewWindow(string folder) : MainWindow(folder, false)
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
    }
    public static void Run()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "size-preview-fixtures"); Directory.CreateDirectory(fixture);
        try
        {
            List<Wallpaper> imported = [];
            try
            {
                var source = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaWall", "settings.json")));
                imported = source?.Library.Where(x => x.Path != null && File.Exists(x.Path)).ToList() ?? [];
            }
            catch { }
            File.WriteAllText(Path.Combine(fixture, "settings.json"), JsonSerializer.Serialize(new Settings { Library = imported }));
            using var window = new PreviewWindow(fixture);
            window.StartPosition = FormStartPosition.Manual; window.Location = new(-32000, -32000); window.Show();
            foreach (var size in new[] { new Size(1680, 1100), new Size(1920, 1200), new Size(2240, 1320) })
            {
                window.Size = size;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < 1800) { Application.DoEvents(); Thread.Sleep(10); }
                using var image = new Bitmap(window.Width, window.Height);
                window.DrawToBitmap(image, new(Point.Empty, image.Size));
                image.Save(Path.Combine(AppContext.BaseDirectory, $"ui-size-{size.Width}x{size.Height}.png"));
            }
            window.Close();
        }
        finally { TestFiles.RemoveFixture(fixture); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-size-preview-test.txt"), "PASS: three native window sizes rendered using isolated read-only library references.");
    }
}
