namespace LumaWall;

partial class MainWindow
{
    readonly CancellationTokenSource lifetime = new();
    CancellationTokenSource? playbackRequest;
    Scene? pendingScene;
    Wallpaper? preparingWallpaper;
    PlaylistController playlist = null!;
    readonly System.Windows.Forms.Timer playlistTimer = new() { Interval = 250 };
    readonly ListBox queue = new();
    readonly Label playlistState = new();
    readonly NumericUpDown intervalSeconds = new(), fadeSeconds = new();
    readonly ComboBox rotationMode = new();
    Button rotateButton = null!;
    bool importing, refreshingPlaylist, rotating;

    void InitializePlaylistState()
    {
        settings.Playlist = (settings.Playlist ?? new()).Normalize(items.Select(x => x.Key));
        playlist = new(settings.Playlist);
        CurrentMonitor.Playlist = playlist;
        playlistTimer.Tick += (_, _) =>
        {
            CaptureCurrentMonitor();
            foreach (var monitor in monitorRuntimes.Values.ToArray())
                if (!monitor.Rotating && monitor.Playlist.Tick() is string key) RunAsync(() => RotateMonitorAsync(monitor, key));
            UpdatePlaylistStatus();
        };
        playlistTimer.Start();
    }
    Control CreatePlaylistPanel()
    {
        var panel = new BorderedPanel { Dock = DockStyle.Fill, BackColor = Palette.Field, Padding = new(12, 8, 12, 10), Margin = Padding.Empty };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (var height in new[] { 36, 28 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        foreach (var height in new[] { 42, 116, 42, 44 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new(SizeType.Percent, 100)); heading.ColumnStyles.Add(new(SizeType.Absolute, 66));
        heading.Controls.Add(TextLabel("播放列表", 13, true), 0, 0);
        var close = Button("收起", () => SetPlaylistSidebar(false)); close.MinimumSize = new(40, 30); close.Margin = Padding.Empty; heading.Controls.Add(close, 1, 0);
        layout.Controls.Add(heading, 0, 0);
        var help = TextLabel("添加壁纸后开始定时轮换", 9); help.ForeColor = Palette.Muted; layout.Controls.Add(help, 0, 1);
        queue.Dock = DockStyle.Fill; queue.BackColor = Palette.Background; queue.ForeColor = Palette.Text; queue.BorderStyle = BorderStyle.None; queue.IntegralHeight = false; queue.Margin = new(0, 0, 0, 8); queue.DisplayMember = "Name"; queue.AccessibleName = "播放列表壁纸顺序";
        layout.Controls.Add(queue, 0, 2);
        var options = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3, Margin = Padding.Empty };
        options.ColumnStyles.Add(new(SizeType.Absolute, 68)); options.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (int i in new[] { 0, 1, 2 }) options.RowStyles.Add(new(SizeType.Absolute, 37));
        options.Controls.Add(TextLabel("间隔 / 秒", 9), 0, 0); options.Controls.Add(intervalSeconds, 1, 0);
        options.Controls.Add(TextLabel("轮换模式", 9), 0, 1); options.Controls.Add(rotationMode, 1, 1);
        options.Controls.Add(TextLabel("过渡 / 秒", 9), 0, 2); options.Controls.Add(fadeSeconds, 1, 2);
        StyleNumber(intervalSeconds, 5, 86400, settings.Playlist.IntervalSeconds, 0);
        StyleNumber(fadeSeconds, 0, 10, (decimal)settings.Playlist.FadeSeconds, 1);
        StyleCombo(rotationMode); rotationMode.Items.AddRange(["顺序循环", "随机循环"]); rotationMode.SelectedIndex = settings.Playlist.Shuffle ? 1 : 0;
        intervalSeconds.ValueChanged += (_, _) => ChangePlaylistOptions(); fadeSeconds.ValueChanged += (_, _) => ChangePlaylistOptions();
        // Native handle creation and programmatic refresh can change selection;
        // persist the playback mode only after the user commits a choice.
        rotationMode.SelectionChangeCommitted += (_, _) => ChangePlaylistOptions(true);
        layout.Controls.Add(options, 0, 4);
        var manage = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
        manage.ColumnStyles.Add(new(SizeType.Percent, 46)); manage.ColumnStyles.Add(new(SizeType.Percent, 24)); manage.ColumnStyles.Add(new(SizeType.Percent, 15)); manage.ColumnStyles.Add(new(SizeType.Percent, 15));
        manage.Controls.Add(Button("＋ 加入", AddToPlaylist), 0, 0); manage.Controls.Add(Button("移除", RemoveFromPlaylist), 1, 0);
        manage.Controls.Add(Button("↑", () => MoveInPlaylist(-1)), 2, 0); manage.Controls.Add(Button("↓", () => MoveInPlaylist(1)), 3, 0);
        foreach (Control button in manage.Controls) { button.MinimumSize = new(28, 34); button.Margin = new(0, 0, 4, 0); }
        layout.Controls.Add(manage, 0, 3);
        var play = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        play.ColumnStyles.Add(new(SizeType.Percent, 62)); play.ColumnStyles.Add(new(SizeType.Percent, 38));
        rotateButton = Button("开始轮换", TogglePlaylist); play.Controls.Add(rotateButton, 0, 0);
        play.Controls.Add(Button("下一张", () => { if (!rotating && playlist.Running && playlist.Next() is string key) RunAsync(() => RotateAsync(key)); }), 1, 0);
        foreach (Control button in play.Controls) { button.MinimumSize = new(50, 34); button.Margin = new(0, 0, 4, 0); }
        layout.Controls.Add(play, 0, 5); playlistState.Dock = DockStyle.Fill; playlistState.UseMnemonic = false; playlistState.ForeColor = Palette.Muted; playlistState.TextAlign = ContentAlignment.BottomLeft; playlistState.AutoEllipsis = true; layout.Controls.Add(playlistState, 0, 6);
        panel.Controls.Add(layout); return panel;
    }
    void StyleNumber(NumericUpDown number, decimal minimum, decimal maximum, decimal value, int decimals)
    {
        number.Minimum = minimum; number.Maximum = maximum; number.Value = Math.Clamp(value, minimum, maximum); number.DecimalPlaces = decimals; number.Increment = decimals == 0 ? 1 : .1m;
        number.BackColor = Palette.Background; number.ForeColor = Palette.Text; number.BorderStyle = BorderStyle.FixedSingle; number.Dock = DockStyle.Fill; number.Margin = new(0, 5, 4, 0);
    }
    void ChangePlaylistOptions(bool commitMode = false)
    {
        if (refreshingPlaylist) return;
        var old = settings.Playlist;
        settings.Playlist = new() { Keys = new(old.Keys), IntervalSeconds = (int)intervalSeconds.Value, FadeSeconds = (double)fadeSeconds.Value, Shuffle = commitMode ? rotationMode.SelectedIndex == 1 : old.Shuffle };
        try { Save(); playlist.UpdateOptions(settings.Playlist); }
        catch (Exception ex) { settings.Playlist = old; RefreshPlaylist(); status.Text = "播放列表设置未保存：" + ex.Message; }
    }
    void SavePlaylistKeys(List<string> keys, int select = -1)
    {
        var old = settings.Playlist.Keys; settings.Playlist.Keys = keys;
        try { Save(); } catch { settings.Playlist.Keys = old; throw; }
        if (playlist.PendingKey is string pending && !keys.Contains(pending, StringComparer.OrdinalIgnoreCase)) playbackRequest?.Cancel();
        playlist.UpdateOptions(settings.Playlist, items.Select(x => x.Key)); RefreshPlaylist();
        if (select >= 0 && select < queue.Items.Count) queue.SelectedIndex = select;
    }
    void AddToPlaylist()
    {
        if (Selected is not Wallpaper item) return;
        int existing = settings.Playlist.Keys.FindIndex(x => x.Equals(item.Key, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0) { queue.SelectedIndex = existing; return; }
        SavePlaylistKeys([.. settings.Playlist.Keys, item.Key], settings.Playlist.Keys.Count);
    }
    void RemoveFromPlaylist()
    {
        int index = queue.SelectedIndex; if (index < 0) return;
        var keys = new List<string>(settings.Playlist.Keys); keys.RemoveAt(index); SavePlaylistKeys(keys, Math.Min(index, keys.Count - 1));
    }
    void MoveInPlaylist(int direction)
    {
        int index = queue.SelectedIndex, target = index + direction; if (index < 0 || target < 0 || target >= queue.Items.Count) return;
        var keys = new List<string>(settings.Playlist.Keys); (keys[index], keys[target]) = (keys[target], keys[index]); SavePlaylistKeys(keys, target);
    }
    void RefreshPlaylist()
    {
        int selected = queue.SelectedIndex; refreshingPlaylist = true;
        try
        {
            queue.Items.Clear(); foreach (var key in settings.Playlist.Keys) { var item = items.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)); if (item != null) queue.Items.Add(item); }
            if (queue.Items.Count > 0) queue.SelectedIndex = Math.Clamp(selected, 0, queue.Items.Count - 1);
            intervalSeconds.Value = settings.Playlist.IntervalSeconds; fadeSeconds.Value = (decimal)settings.Playlist.FadeSeconds; rotationMode.SelectedIndex = settings.Playlist.Shuffle ? 1 : 0;
        }
        finally { refreshingPlaylist = false; }
        UpdatePlaylistStatus();
    }
    void UpdatePlaylistStatus()
    {
        rotateButton.Text = playlist.Running ? "停止轮换" : "开始轮换";
        playlistState.Text = !playlist.Running ? $"{queue.Items.Count} 项 · 过渡 0 秒可直接切换" : playlist.Loading ? "正在准备下一张壁纸…" : playlist.Paused ? "轮换已暂停" : $"正在播放：{activeWallpaper?.Name} · {Math.Ceiling(playlist.RemainingSeconds)} 秒后轮换";
    }
    void TogglePlaylist()
    {
        if (playlist.Running) { StopPlaylist(); return; }
        if (rotating) { status.Text = "正在结束上一次切换，请稍后开始轮换"; return; }
        if (settings.Playlist.Keys.Count == 0) { status.Text = "先选择壁纸并点击播放列表的“＋ 加入”"; return; }
        TryFlushTransforms(); CurrentMonitor.Profile.Paused = false; CurrentMonitor.FailedKeys.Clear(); preview.Paused = CurrentMonitor.AutoPaused; playlist.Pause(CurrentMonitor.AutoPaused);
        var preferred = queue.SelectedItem as Wallpaper;
        if (playlist.Start(preferred?.Key) is string key) RunAsync(() => RotateAsync(key));
    }
    void StopPlaylist() { if (playlist.Loading) playbackRequest?.Cancel(); playlist.Stop(); CurrentMonitor.Profile.PlaylistEnabled = false; UpdatePlaylistStatus(); }
    async Task RotateAsync(string key)
    {
        CaptureCurrentMonitor(); await RotateMonitorAsync(CurrentMonitor, key);
    }
    async Task RotateMonitorAsync(MonitorRuntime monitor, string key)
    {
        if (monitor.Rotating || disposed) return;
        monitor.Rotating = true; PublishMonitor(monitor);
        try
        {
            for (int attempt = 0; attempt < Math.Max(1, monitor.Profile.Playlist.Keys.Count); attempt++)
            {
                try
                {
                    if (monitor.FailedKeys.Contains(key)) throw new IOException("本轮已跳过该壁纸。");
                    var item = items.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? throw new IOException("播放列表壁纸已不存在。");
                    await SwitchDesktopForMonitorAsync(monitor, item, monitor.Profile.Playlist.FadeSeconds);
                    if (monitor.Playlist.Running && monitor.Playlist.PendingKey?.Equals(key, StringComparison.OrdinalIgnoreCase) == true) monitor.Playlist.Commit(key);
                    monitor.Profile.PlaylistEnabled = monitor.Playlist.Running;
                    TrySaveSettings("轮换状态未保存");
                    return;
                }
                catch (OperationCanceledException) { monitor.Playlist.Reject(); return; }
                catch (Exception ex)
                {
                    monitor.FailedKeys.Add(key); RecordPlaybackFailure(monitor, key, ex);
                    if (!monitor.Playlist.Running) return;
                    monitor.Playlist.Reject();
                    var keys = monitor.Profile.Playlist.Keys;
                    int failedIndex = keys.FindIndex(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
                    string? next = Enumerable.Range(1, keys.Count).Select(n => keys[(Math.Max(0, failedIndex) + n) % keys.Count])
                        .FirstOrDefault(x => !monitor.FailedKeys.Contains(x));
                    if (next == null) break;
                    key = monitor.Playlist.Start(next)!;
                    monitor.Playlist.Pause(monitor.Profile.Paused || monitor.AutoPaused);
                }
            }
            monitor.Playlist.Stop(); monitor.Profile.PlaylistEnabled = false;
            if (monitor.Id == selectedMonitorId) status.Text = "播放列表暂无可播放项目 · 已保留当前画面，可在运行状态查看原因";
        }
        finally { monitor.Rotating = false; PublishMonitor(monitor); UpdatePlaylistStatus(); }
    }
    async void RunAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed) { status.Text = "操作失败：" + ex.Message; MessageBox.Show(this, ex.Message, "Luma Wall", MessageBoxButtons.OK, MessageBoxIcon.Warning); } }
    }
    async Task SwitchDesktopAsync(Wallpaper item, double transitionSeconds)
    {
        CaptureCurrentMonitor(); await SwitchDesktopForMonitorAsync(CurrentMonitor, item, transitionSeconds);
    }
    async Task SwitchDesktopForMonitorAsync(MonitorRuntime monitor, Wallpaper item, double transitionSeconds)
    {
        if (!monitorRuntimes.TryGetValue(monitor.Id, out var current) || !ReferenceEquals(current, monitor)) throw new OperationCanceledException();
        monitor.Request?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); monitor.Request = request;
        var scene = new TransitionScene { Fps = settings.Fps, Paused = true, Volume = monitor.Profile.Muted ? 0 : monitor.Profile.Volume / 100f };
        Form? window = null;
        Bitmap? snapshot = null;
        monitor.PendingScene = scene; monitor.Preparing = item; PublishMonitor(monitor);
        bool committed = false, reused = false;
        try
        {
            scene.LoadScene(item); status.Text = "正在准备壁纸：" + item.Name;
            await scene.WaitUntilReadyAsync(request.Token); request.Token.ThrowIfCancellationRequested();
            if (!monitorRuntimes.TryGetValue(monitor.Id, out current) || !ReferenceEquals(current, monitor)) throw new OperationCanceledException(request.Token);
            if (virtualDesktopSource?.Read() is { Available: true } desktopSnapshot && desktopSnapshot.Current != activeVirtualDesktop) throw new OperationCanceledException(request.Token);
            // Keep the target row's latest name even when two rows share one file.
            var target = monitor.PendingScene == scene ? monitor.Preparing : item;
            item = items.FirstOrDefault(x => ReferenceEquals(x, target))
                ?? throw new OperationCanceledException("壁纸已从库中移除。", request.Token);
            scene.SetTransform(monitor.Profile.Transforms.TryGetValue(item.Key, out var saved) ? saved : new());
            scene.LayoutMode = monitor.Profile.LayoutModes.GetValueOrDefault(item.Key);
            // A restored paused wallpaper must show its own first frame immediately,
            // rather than freeze a fade with the outgoing image fully covering it.
            if (monitor.HoldingPrevious || monitor.Profile.Paused || monitor.AutoPaused) transitionSeconds = 0;
            if (monitor.Scene != null && monitor.Window?.ClientSize == monitor.Bounds.Size && transitionSeconds > 0)
            {
                snapshot = new Bitmap(monitor.Scene.Width, monitor.Scene.Height);
                monitor.Scene.DrawToBitmap(snapshot, new(Point.Empty, snapshot.Size));
            }
            var previous = monitor.Window;
            TransitionScene playing;
            if (previous is { IsDisposed: false } && previous.ClientSize == monitor.Bounds.Size && monitor.Scene is TransitionScene surface)
            {
                // Attach is a no-op when the host is healthy. Neither its HWND nor its
                // child scene HWND changes, including across virtual-desktop profiles.
                Desktop.Attach(previous, monitor.Bounds);
                if (monitor.Id == selectedMonitorId) preview.ClearScene();
                surface.ReplacePrepared(scene, snapshot, transitionSeconds); snapshot = null;
                window = previous; playing = surface; reused = true;
                playing.Fps = settings.Fps;
            }
            else
            {
                window = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = monitor.Bounds };
                window.Controls.Add(scene);
                if (snapshot != null) { scene.BeginTransition(snapshot, transitionSeconds); snapshot = null; }
                Desktop.Attach(window, monitor.Bounds); playing = scene;
                if (monitor.Scene != null) monitor.Scene.AudioEnabled = false;
            }
            monitor.Window = window; monitor.Scene = playing; monitor.Active = item; monitor.HoldingPrevious = false; monitor.HeldWallpaper = null; committed = true;
            playing.PlaybackFailed += ex => HandlePlaybackFailure(monitor, playing, item, ex);
            playing.AudioFailed += ex => { if (monitor.Scene == playing && !disposed) RecordPlaybackFailure(monitor, item.Key, ex); };
            monitor.Profile.WallpaperKey = item.Key; monitor.FailedKeys.Remove(item.Key); PublishMonitor(monitor);
            lifecycleMonitor?.Refresh();
            playing.AudioEnabled = true;
            playing.Paused = monitor.Profile.Paused || monitor.AutoPaused || monitor.Playlist.Paused;
            playing.Invalidate(); playing.Update();
            if (monitor.Id == selectedMonitorId) UpdatePreviewPlaybackSource();
            if (!reused) { previous?.Close(); previous?.Dispose(); }
            TrySaveSettings("播放状态未保存");
            if (monitor.Id == selectedMonitorId) status.Text = "正在桌面播放：" + item.Name + (item.Kind == "video" ? (monitor.Profile.Muted ? " · 视频已静音" : " · 原声循环播放") : " · 画面调整可实时同步");
        }
        finally
        {
            if (monitor.PendingScene == scene) { monitor.PendingScene = null; monitor.Preparing = null; }
            if (monitor.Request == request) monitor.Request = null;
            PublishMonitor(monitor);
            snapshot?.Dispose();
            if (!committed && !reused) window?.Dispose();
            if (!committed || reused) scene.Dispose();
        }
    }
    void ConfigureFileDrop(Control target)
    {
        target.AllowDrop = true;
        target.DragEnter += (_, e) => { e.Effect = !importing && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None; };
        target.DragDrop += (_, e) => { if (!importing && e.Data?.GetData(DataFormats.FileDrop) is string[] paths) { string? category = ChooseImportCategory(); if (category != null) RunAsync(() => ImportFilesAsync(paths, category)); } };
    }
    async Task ImportFilesAsync(IEnumerable<string> paths, string category = "")
    {
        if (importing) return; importing = true;
        try
        {
            status.Text = "正在检查并导入媒体…";
            string mediaFolder = Path.Combine(folder, "media");
            var expanded = await Task.Run(() => paths.SelectMany(path => Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).Where(MediaImport.IsSupported)
                : new[] { path }).ToArray(), lifetime.Token);
            var result = await MediaImport.ImportAsync(expanded, mediaFolder, async (path, token) => { using var poster = await VideoMedia.GetPosterAsync(path, token); }, lifetime.Token);
            if (disposed) { MediaImport.Rollback(result, mediaFolder); return; }
            var oldCategories = settings.MediaCategories; settings.MediaCategories = new(oldCategories, StringComparer.OrdinalIgnoreCase);
            var old = settings.Library; var oldImportedAt = settings.ImportedAt; settings.Library = [.. old, .. result.Items];
            settings.ImportedAt = new(oldImportedAt, StringComparer.OrdinalIgnoreCase);
            foreach (var item in result.Items) { settings.ImportedAt[item.Key] = DateTimeOffset.UtcNow; if (category.Length > 0) settings.MediaCategories[item.Key] = category; }
            try { Save(); } catch { settings.MediaCategories = oldCategories; settings.Library = old; settings.ImportedAt = oldImportedAt; MediaImport.Rollback(result, mediaFolder); throw; }
            items.AddRange(result.Items); RefreshOnlineCategories(); libraryCategory.SelectedIndex = 0; RebuildLibraryView(result.Items.LastOrDefault()); UpdateCount();
            status.Text = $"已导入 {result.Items.Count} 项" + (result.Errors.Count > 0 ? $" · {result.Errors.Count} 项失败" : " · 原始文件已保留");
            if (result.Errors.Count > 0) MessageBox.Show(this, string.Join("\n", result.Errors), "部分文件未能导入", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        finally { importing = false; }
    }

    void RecordPlaybackFailure(MonitorRuntime monitor, string key, Exception ex)
    {
        lastPlaybackError = $"{DateTimeOffset.Now:g} · {monitor.Id}\r\n{items.FirstOrDefault(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.Name ?? key}\r\n{ex}";
        try
        {
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "playback-errors.log");
            if (File.Exists(path) && new FileInfo(path).Length > 524288) File.WriteAllText(path, "");
            File.AppendAllText(path, lastPlaybackError + "\r\n\r\n");
        }
        catch { }
        if (monitor.Id == selectedMonitorId) status.Text = "壁纸播放失败，正在跳过 · 运行状态可查看详情";
    }
    void HandlePlaybackFailure(MonitorRuntime monitor, Scene scene, Wallpaper item, Exception ex)
    {
        if (monitor.Scene != scene || disposed || !monitorRuntimes.TryGetValue(monitor.Id, out var current) || !ReferenceEquals(current, monitor)) return;
        RecordPlaybackFailure(monitor, item.Key, ex); monitor.FailedKeys.Add(item.Key);
        var next = monitor.Profile.Playlist.Keys.FirstOrDefault(x => !monitor.FailedKeys.Contains(x));
        if (monitor.Playlist.Running && !monitor.Rotating && next != null)
        {
            string key = monitor.Playlist.Start(next)!; monitor.Playlist.Pause(monitor.Profile.Paused || monitor.AutoPaused);
            RunAsync(() => RotateMonitorAsync(monitor, key));
        }
        else { monitor.Playlist.Stop(); monitor.Profile.PlaylistEnabled = false; scene.Paused = true; PublishMonitor(monitor); }
    }
}
