namespace LumaWall;

partial class MainWindow
{
    readonly Dictionary<string, MonitorRuntime> monitorRuntimes = new(StringComparer.OrdinalIgnoreCase);
    string selectedMonitorId = "";
    bool changingMonitor;
    MonitorRuntime CurrentMonitor => monitorRuntimes[selectedMonitorId];

    void InitializeMonitorProfiles()
    {
        settings.Monitors = new(settings.Monitors ?? [], StringComparer.OrdinalIgnoreCase);
        settings.LayoutModes = new(settings.LayoutModes ?? [], StringComparer.OrdinalIgnoreCase);
        settings.Favorites ??= []; settings.ImportedAt = new(settings.ImportedAt ?? [], StringComparer.OrdinalIgnoreCase);
        var screensNow = Screen.AllScreens;
        bool migratingLegacySettings = settings.Monitors.Count == 0;
        foreach (var screen in screensNow)
        {
            if (!settings.Monitors.TryGetValue(screen.DeviceName, out var profile) || profile == null)
            {
                bool first = migratingLegacySettings && screen.Primary;
                profile = new() { Volume = settings.Volume, Muted = first ? settings.Muted : true,
                    Playlist = settings.Playlist ?? new(), Transforms = first ? settings.Transforms : new(settings.Transforms, StringComparer.OrdinalIgnoreCase),
                    LayoutModes = first ? settings.LayoutModes : new(settings.LayoutModes, StringComparer.OrdinalIgnoreCase) };
                profile.Playlist = profile.Playlist.Normalize(items.Select(x => x.Key));
                settings.Monitors[screen.DeviceName] = profile;
            }
            NormalizeProfile(profile);
            monitorRuntimes[screen.DeviceName] = new(screen.DeviceName, screen.Bounds, profile);
        }
        selectedMonitorId = settings.SelectedMonitorId is string saved && monitorRuntimes.ContainsKey(saved) ? saved : Screen.PrimaryScreen!.DeviceName;
        var current = CurrentMonitor.Profile;
        settings.Transforms = current.Transforms; settings.LayoutModes = current.LayoutModes; settings.Playlist = current.Playlist;
        settings.Volume = current.Volume; settings.Muted = current.Muted;
    }

    void NormalizeProfile(MonitorProfile profile)
    {
        profile.Volume = Math.Clamp(profile.Volume, 0, 100);
        profile.Transforms = new(profile.Transforms ?? [], StringComparer.OrdinalIgnoreCase);
        profile.LayoutModes = new(profile.LayoutModes ?? [], StringComparer.OrdinalIgnoreCase);
        profile.Playlist = (profile.Playlist ?? new()).Normalize(items.Select(x => x.Key));
    }

    // Fields remain aliases of the selected display for the UI and existing integrations.
    void CaptureCurrentMonitor()
    {
        if (!monitorRuntimes.TryGetValue(selectedMonitorId, out var monitor)) return;
        monitor.Window = desktopWindow; monitor.Scene = desktopScene; monitor.Active = activeWallpaper;
        monitor.PendingScene = pendingScene; monitor.Preparing = preparingWallpaper; monitor.Request = playbackRequest;
        monitor.Playlist = playlist; monitor.Rotating = rotating;
        monitor.Profile.Volume = settings.Volume; monitor.Profile.Muted = settings.Muted;
        monitor.Profile.Transforms = settings.Transforms; monitor.Profile.LayoutModes = settings.LayoutModes; monitor.Profile.Playlist = settings.Playlist;
        if (monitor.Active != null || playlist?.Running == true) monitor.Profile.PlaylistEnabled = playlist?.Running == true;
    }

    void BindMonitor(MonitorRuntime monitor)
    {
        desktopWindow = monitor.Window; desktopScene = monitor.Scene; activeWallpaper = monitor.Active;
        pendingScene = monitor.PendingScene; preparingWallpaper = monitor.Preparing; playbackRequest = monitor.Request;
        playlist = monitor.Playlist; rotating = monitor.Rotating;
        settings.Transforms = monitor.Profile.Transforms; settings.LayoutModes = monitor.Profile.LayoutModes; settings.Playlist = monitor.Profile.Playlist;
        settings.Volume = monitor.Profile.Volume; settings.Muted = monitor.Profile.Muted;
    }

    void PublishMonitor(MonitorRuntime monitor)
    {
        if (monitor.Id == selectedMonitorId && monitorRuntimes.TryGetValue(monitor.Id, out var current) && ReferenceEquals(current, monitor)) BindMonitor(monitor);
    }

    void SelectMonitor()
    {
        if (changingMonitor || screens.SelectedIndex < 0) return;
        string id = Screen.AllScreens[screens.SelectedIndex].DeviceName;
        if (id == selectedMonitorId) { UpdatePreviewAspect(); return; }
        TryFlushTransforms(); CaptureCurrentMonitor(); selectedMonitorId = id; BindMonitor(CurrentMonitor);
        preview.Paused = CurrentMonitor.Profile.Paused || CurrentMonitor.AutoPaused;
        updatingControls = true;
        try { volumeValue.Value = settings.Volume; muteAudio.Checked = settings.Muted; }
        finally { updatingControls = false; }
        UpdatePreviewAspect(); SelectWallpaper(); RefreshPlaylist(); settings.SelectedMonitorId = id;
        TrySaveSettings("显示器设置未保存");
    }

    void SetMonitorPaused(MonitorRuntime monitor)
    {
        bool paused = monitor.Profile.Paused || monitor.AutoPaused;
        if (monitor.Scene != null && !monitor.HoldingPrevious) monitor.Scene.Paused = paused;
        monitor.Playlist.Pause(paused);
        if (monitor.Id == selectedMonitorId) preview.Paused = paused;
    }

    void StopMonitor(MonitorRuntime monitor, bool clearSession)
    {
        monitor.Request?.Cancel(); monitor.Playlist.Stop();
        var previous = monitor.Window; var previousScene = monitor.Scene;
        monitor.Window = null; monitor.Scene = null; monitor.Active = null; monitor.HoldingPrevious = false; monitor.HeldWallpaper = null;
        if (clearSession) { monitor.Profile.WallpaperKey = null; monitor.Profile.PlaylistEnabled = false; }
        PublishMonitor(monitor);
        if (monitor.Id == selectedMonitorId) UpdatePreviewPlaybackSource();
        previous?.Close(); previous?.Dispose();
        if (previous == null) previousScene?.Dispose();
    }

    void StopAllMonitors(bool clearSession = true)
    {
        CaptureCurrentMonitor(); preview.ClearScene();
        foreach (var monitor in monitorRuntimes.Values) StopMonitor(monitor, clearSession);
    }

    void RenameRuntimeItem(Wallpaper item, Wallpaper renamed)
    {
        CaptureCurrentMonitor();
        foreach (var monitor in monitorRuntimes.Values)
        {
            if (ReferenceEquals(monitor.Active, item)) monitor.Active = renamed;
            if (ReferenceEquals(monitor.Preparing, item)) monitor.Preparing = renamed;
            if (ReferenceEquals(monitor.HeldWallpaper, item)) monitor.HeldWallpaper = renamed;
        }
        PublishMonitor(CurrentMonitor);
    }

    void TrySaveSettings(string message)
    {
        try { Save(); } catch (Exception ex) { status.Text = message + "：" + ex.Message; }
    }
}
