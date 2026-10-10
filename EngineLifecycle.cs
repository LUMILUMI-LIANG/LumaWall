using System.Runtime.InteropServices;

namespace LumaWall;

partial class MainWindow
{
    DesktopLifecycleMonitor? lifecycleMonitor;
    LifecycleSnapshot lastLifecycle = new(false, false, [], false);
    string lastPlaybackError = "尚无播放错误。";
    bool realUserSession;

    void InitializeEngineLifecycle(bool realSession, bool showTray)
    {
        realUserSession = realSession;
        if (!realSession) return;
        lifecycleMonitor = new(this, () => monitorRuntimes.Values.Where(x => x.Window != null).Select(x => x.Bounds), OnLifecycleSnapshot);
        Shown += (_, _) => BeginInvoke((Action)(() =>
        {
            lifecycleMonitor.Refresh();
            if (settings.RestoreOnLaunch) RunAsync(RestoreLastSessionAsync);
        }));
    }

    void OnLifecycleSnapshot(LifecycleSnapshot snapshot)
    {
        if (disposed) return;
        lastLifecycle = snapshot; CaptureCurrentMonitor();
        if (snapshot.RecoveryRequested) RefreshDisplayList();
        foreach (var monitor in monitorRuntimes.Values)
        {
            bool pause = settings.AutoPauseLocked && snapshot.Locked || settings.AutoPauseSleep && snapshot.Suspended
                || settings.AutoPauseFullscreen && snapshot.FullscreenDisplays.Contains(monitor.Bounds);
            if (monitor.AutoPaused == pause) continue;
            monitor.AutoPaused = pause; SetMonitorPaused(monitor);
        }
        if (snapshot.RecoveryRequested && settings.AutoRecover && !snapshot.Locked && !snapshot.Suspended) RunAsync(RecoverDesktopAsync);
        UpdatePlaylistStatus();
    }

    void RefreshDisplayList()
    {
        var connected = Screen.AllScreens;
        var names = connected.Select(x => x.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in monitorRuntimes.Values.Where(x => !names.Contains(x.Id)).ToArray())
            if (monitor.Window != null || monitor.Request != null) StopMonitor(monitor, false);
        foreach (var screen in connected)
        {
            if (monitorRuntimes.TryGetValue(screen.DeviceName, out var existing)) { existing.Bounds = screen.Bounds; continue; }
            if (!settings.Monitors.TryGetValue(screen.DeviceName, out var profile)) profile = new() { Muted = true };
            NormalizeProfile(profile); settings.Monitors[screen.DeviceName] = profile;
            monitorRuntimes[screen.DeviceName] = new(screen.DeviceName, screen.Bounds, profile);
        }
        if (!names.Contains(selectedMonitorId)) { selectedMonitorId = connected.First(x => x.Primary).DeviceName; BindMonitor(CurrentMonitor); }
        changingMonitor = true;
        try
        {
            screens.Items.Clear();
            foreach (var screen in connected) screens.Items.Add($"{screen.DeviceName.Replace("\\\\.\\", "")} · {screen.Bounds.Width}×{screen.Bounds.Height}");
            screens.SelectedIndex = Array.FindIndex(connected, x => x.DeviceName == selectedMonitorId);
        }
        finally { changingMonitor = false; }
        updatingControls = true;
        try { volumeValue.Value = settings.Volume; muteAudio.Checked = settings.Muted; }
        finally { updatingControls = false; }
        preview.Paused = CurrentMonitor.Profile.Paused || CurrentMonitor.AutoPaused;
        UpdatePreviewAspect(); SelectWallpaper(); RefreshPlaylist();
    }

    async Task RecoverDesktopAsync()
    {
        var connected = Screen.AllScreens.Select(x => x.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var monitor in monitorRuntimes.Values.ToArray())
        {
            if (!connected.Contains(monitor.Id) || monitor.Recovering || monitor.Rotating || monitor.Request != null || monitor.Profile.WallpaperKey == null) continue;
            monitor.Recovering = true;
            try
            {
                if (monitor.Window is { IsDisposed: false } window && window.IsHandleCreated && IsWindow(window.Handle))
                {
                    Desktop.Attach(window, monitor.Bounds); SetMonitorPaused(monitor);
                }
                else if (items.FirstOrDefault(x => x.Key.Equals(monitor.Profile.WallpaperKey, StringComparison.OrdinalIgnoreCase)) is { } item)
                {
                    bool rotate = monitor.Profile.PlaylistEnabled;
                    if (rotate && monitor.Playlist.Start(item.Key) is string key) await RotateMonitorAsync(monitor, key);
                    else await SwitchDesktopForMonitorAsync(monitor, item, 0);
                }
            }
            catch (Exception ex) { RecordPlaybackFailure(monitor, monitor.Profile.WallpaperKey ?? "恢复壁纸", ex); }
            finally { monitor.Recovering = false; }
        }
    }

    async Task RestoreLastSessionAsync()
    {
        foreach (var monitor in monitorRuntimes.Values.ToArray())
        {
            if (!monitorRuntimes.TryGetValue(monitor.Id, out var current) || !ReferenceEquals(current, monitor)) continue;
            if (monitor.Profile.WallpaperKey is not string key || monitor.Window != null && !monitor.HoldingPrevious) continue;
            if (virtualDesktopSource?.Read() is { Available: true } snapshot && snapshot.Current != activeVirtualDesktop) break;
            var item = items.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (item == null) continue;
            try
            {
                if (monitor.Profile.PlaylistEnabled && monitor.Profile.Playlist.Keys.Count > 0 && monitor.Playlist.Start(key) is string pending)
                    await RotateMonitorAsync(monitor, pending);
                else await SwitchDesktopForMonitorAsync(monitor, item, 0);
                SetMonitorPaused(monitor);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { RecordPlaybackFailure(monitor, key, ex); }
        }
    }

    void ShowEngineSettings()
    {
        using var dialog = new EngineSettingsDialog(settings, realUserSession);
        PrepareDialog(dialog);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var before = (settings.AutoPauseFullscreen, settings.AutoPauseLocked, settings.AutoPauseSleep, settings.AutoRecover, settings.RestoreOnLaunch);
        settings.AutoPauseFullscreen = dialog.PauseFullscreen; settings.AutoPauseLocked = dialog.PauseLocked; settings.AutoPauseSleep = dialog.PauseSleep;
        settings.AutoRecover = dialog.Recover; settings.RestoreOnLaunch = dialog.Restore;
        try { Save(); }
        catch { (settings.AutoPauseFullscreen, settings.AutoPauseLocked, settings.AutoPauseSleep, settings.AutoRecover, settings.RestoreOnLaunch) = before; throw; }
        if (realUserSession && dialog.StartupChanged) StartupRegistration.SetEnabled(Application.ExecutablePath, dialog.Startup);
        OnLifecycleSnapshot(lastLifecycle with { RecoveryRequested = false });
        status.Text = "引擎设置已保存 · 智能暂停会保留手动暂停状态";
    }

    void ShowDiagnostics()
    {
        CaptureCurrentMonitor(); using var dialog = new DiagnosticsDialog(this); PrepareDialog(dialog); dialog.ShowDialog(this);
    }

    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
}

sealed class EngineSettingsDialog : Form
{
    readonly CheckBox fullscreen = new(), locked = new(), sleep = new(), recover = new(), restore = new(), startup = new();
    readonly bool wasStartup;
    public bool PauseFullscreen => fullscreen.Checked;
    public bool PauseLocked => locked.Checked;
    public bool PauseSleep => sleep.Checked;
    public bool Recover => recover.Checked;
    public bool Restore => restore.Checked;
    public bool Startup => startup.Checked;
    public bool StartupChanged => wasStartup != Startup;

    public EngineSettingsDialog(Settings settings, bool startupAvailable)
    {
        Text = "引擎设置"; AutoScaleMode = AutoScaleMode.Dpi; Font = new("Microsoft YaHei UI", 10);
        ClientSize = new(560, 396); FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = MinimizeBox = false; ShowInTaskbar = false; BackColor = Palette.Surface; ForeColor = Palette.Text;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), ColumnCount = 1, RowCount = 8 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        var choices = new[] { (fullscreen, "全屏游戏或应用时自动暂停当前屏幕", settings.AutoPauseFullscreen),
            (locked, "锁屏时暂停画面和声音", settings.AutoPauseLocked), (sleep, "系统睡眠时暂停，唤醒后恢复", settings.AutoPauseSleep),
            (recover, "桌面或显示器变化后自动恢复壁纸", settings.AutoRecover), (restore, "启动应用后恢复上次壁纸和播放列表", settings.RestoreOnLaunch) };
        int row = 0;
        foreach (var (check, text, value) in choices)
        {
            check.Text = text; check.Checked = value; check.Dock = DockStyle.Fill; check.Margin = Padding.Empty;
            layout.RowStyles.Add(new(SizeType.Absolute, 38)); layout.Controls.Add(check, 0, row++);
        }
        try { wasStartup = startupAvailable && StartupRegistration.IsEnabled(Application.ExecutablePath); } catch { }
        startup.Checked = wasStartup; startup.Enabled = startupAvailable; startup.Text = "登录 Windows 时启动到托盘"; startup.Dock = DockStyle.Fill; startup.Margin = Padding.Empty;
        layout.RowStyles.Add(new(SizeType.Absolute, 38)); layout.Controls.Add(startup, 0, row++);
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.Controls.Add(new Label { Text = "手动暂停不会被自动恢复覆盖。\n多显示器可分别设置原声音量；新屏幕默认静音。", ForeColor = Palette.Muted, Dock = DockStyle.Fill, Margin = new(0, 10, 0, 0) }, 0, row++);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty };
        var save = DialogButton("保存", DialogResult.OK, true); var cancel = DialogButton("取消", DialogResult.Cancel, false);
        buttons.Controls.Add(save); buttons.Controls.Add(cancel); layout.RowStyles.Add(new(SizeType.Absolute, 42)); layout.Controls.Add(buttons, 0, row);
        Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
    }
    internal static Button DialogButton(string text, DialogResult result, bool accent)
    {
        var button = new DarkButton { Text = text, DialogResult = result, Size = new(104, 36), FlatStyle = FlatStyle.Flat,
            BackColor = accent ? Palette.Accent : Palette.Field, ForeColor = accent ? Palette.Background : Palette.Text,
            UseVisualStyleBackColor = false, Margin = new(8, 0, 0, 0), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = 0; return button;
    }
}
