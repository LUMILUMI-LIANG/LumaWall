namespace LumaWall;

partial class MainWindow
{
    bool exitRequested;

    void SaveWindowState()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        settings.WindowWidth = bounds.Width;
        settings.WindowHeight = bounds.Height;
        TryFlushTransforms();
        CaptureCurrentMonitor();
        TrySaveSettings("窗口状态未保存");
    }

    void HideToTray()
    {
        // A window without a tray icon must remain reachable (including test hosts).
        if (disposed || !tray.Visible) return;
        SaveWindowState();
        status.Text = "已收起到托盘 · 双击托盘图标打开，右键选择“退出”结束应用";
        Hide();
    }

    void HandleMainWindowClosing(FormClosingEventArgs e, bool showTray)
    {
        SaveWindowState();
        if (e.CloseReason != CloseReason.UserClosing || !showTray || exitRequested || !tray.Visible) return;

        // Keep desktop playback and the application dispatcher alive when closing the library.
        e.Cancel = true;
        status.Text = "已收起到托盘 · 双击托盘图标打开，右键选择“退出”结束应用";
        Hide();
    }

    void ExitApplication()
    {
        if (disposed) return;
        exitRequested = true;
        try { Close(); }
        finally
        {
            // If another closing handler cancelled the request, subsequent X clicks still hide.
            if (!disposed) exitRequested = false;
        }
    }
}
