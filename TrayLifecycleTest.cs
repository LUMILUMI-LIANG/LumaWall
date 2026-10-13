using System.Reflection;
using System.Text.Json;

namespace LumaWall;

static class TrayLifecycleTest
{
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(MainWindow window, string name)
        => (T)typeof(MainWindow).GetField(name, Flags)!.GetValue(window)!;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static string CreateSettings(string root, string name)
    {
        string folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), JsonSerializer.Serialize(new Settings
        {
            LegacyCategoriesRecovered = true,
            RestoreOnLaunch = false
        }));
        return folder;
    }

    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "tray-lifecycle-fixtures", Guid.NewGuid().ToString("N"));
        string trayFolder = CreateSettings(root, "with-tray");
        MainWindow? window = null;
        MainWindow? noTrayWindow = null;
        try
        {
            window = new MainWindow(trayFolder, true);
            window.Show();
            Application.DoEvents();
            var tray = Field<NotifyIcon>(window, "tray");
            var preview = Field<Scene>(window, "preview");
            Check(tray.Visible && window.Visible && !preview.RenderingSuspended, "Opening the library must show its tray icon and resume preview rendering.");

            var closing = typeof(MainWindow).GetMethod("HandleMainWindowClosing", Flags)!;
            foreach (var reason in new[] { CloseReason.WindowsShutDown, CloseReason.ApplicationExitCall })
            {
                var args = new FormClosingEventArgs(reason, false);
                closing.Invoke(window, [args, true]);
                Check(!args.Cancel && window.Visible, reason + " must not be cancelled or converted into hiding.");
            }

            window.Size = new(window.MinimumSize.Width + 24, window.MinimumSize.Height + 24);
            Field<NumericUpDown>(window, "zoomValue").Value = 135;
            var expectedSize = window.Bounds.Size;
            bool closed = false;
            window.FormClosed += (_, _) => closed = true;
            window.Close();
            Application.DoEvents();

            Check(!window.Visible && !window.IsDisposed && !closed, "Closing the main library must hide it without disposing the application.");
            Check(tray.Visible, "Closing the library must leave a reachable tray icon.");
            Check(preview.RenderingSuspended, "The hidden library must suspend preview rendering.");
            Check(!Field<CancellationTokenSource>(window, "lifetime").IsCancellationRequested, "Hiding the library must keep the application lifetime active.");
            var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(trayFolder, "settings.json")))!;
            Check(saved.WindowWidth == expectedSize.Width && saved.WindowHeight == expectedSize.Height, "Closing to tray must persist window dimensions.");
            Check(saved.Transforms.TryGetValue("builtin:aurora", out var transform) && Math.Abs(transform.Scale - 1.35f) < .001f,
                "Closing to tray must flush pending wallpaper adjustments to settings.");

            window.PresentLibrary();
            Application.DoEvents();
            Check(window.Visible && window.WindowState == FormWindowState.Normal && !preview.RenderingSuspended,
                "Opening the library from tray must restore the window and resume its preview.");

            window.Close();
            Application.DoEvents();
            Check(!window.Visible && !window.IsDisposed, "A restored window must be able to hide again before tray exit.");
            var exit = tray.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "退出");
            exit.PerformClick();
            Application.DoEvents();
            Check(window.IsDisposed && closed, "The tray Exit command must actually close and dispose the application.");
            Check(!tray.Visible, "Exiting must clear the tray icon's visible state.");

            noTrayWindow = new MainWindow(CreateSettings(root, "without-tray"), false);
            noTrayWindow.Show();
            Application.DoEvents();
            Check(!Field<NotifyIcon>(noTrayWindow, "tray").Visible, "The no-tray host must not expose a notification icon.");
            noTrayWindow.Close();
            Application.DoEvents();
            Check(noTrayWindow.IsDisposed, "A no-tray host must close normally instead of becoming an unreachable background window.");

            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "tray-lifecycle-test.txt"),
                "PASS: close hides without disposal, dimensions and pending adjustments saved, preview suspended and restored, tray exit disposes, no-tray close, system shutdown and Application.Exit allowed.");
        }
        finally
        {
            window?.Dispose();
            noTrayWindow?.Dispose();
        }
    }
}
