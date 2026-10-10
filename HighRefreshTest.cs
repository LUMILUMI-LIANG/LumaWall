using System.Reflection;
using System.Text.Json;

namespace LumaWall;

static class HighRefreshTest
{
    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "fps-settings-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using (var window = new MainWindow(root, false))
            {
                var control = (ComboBox)typeof(MainWindow).GetField("fps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                if (control.Items.Count != FrameRateOptions.Values.Length) throw new InvalidOperationException("High refresh choices are missing.");
                control.SelectedIndex = Array.IndexOf(FrameRateOptions.Values, 144);
                var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(root, "settings.json")))!;
                if (saved.Fps != 144) throw new InvalidOperationException("144fps setting must persist.");
            }
            using (var reopened = new MainWindow(root, false))
            {
                var control = (ComboBox)typeof(MainWindow).GetField("fps", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reopened)!;
                if (FrameRateOptions.Values[control.SelectedIndex] != 144) throw new InvalidOperationException("Reopening must preserve high refresh selection.");
            }
        }
        finally { TestFiles.RemoveFixture(root); }
        RenderedPlaybackTest.RunHighRefresh();
    }
}
