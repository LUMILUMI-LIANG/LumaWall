using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace LumaWall;

sealed record VirtualDesktopEntry(Guid Id, string Name);
sealed record VirtualDesktopSnapshot(Guid Current, IReadOnlyList<VirtualDesktopEntry> Desktops)
{
    public bool Available => Current != Guid.Empty && Desktops.Any(x => x.Id == Current);
}
interface IVirtualDesktopSource { VirtualDesktopSnapshot Read(); }

sealed class WindowsVirtualDesktopSource : IVirtualDesktopSource, IDisposable
{
    const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    IVirtualDesktopManager? manager;
    public VirtualDesktopSnapshot Read()
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(RegistryPath);
            using var session = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{System.Diagnostics.Process.GetCurrentProcess().SessionId}\VirtualDesktops");
            Guid current = ReadGuid(root?.GetValue("CurrentVirtualDesktop")) ?? ReadGuid(session?.GetValue("CurrentVirtualDesktop")) ?? Guid.Empty;
            if (current == Guid.Empty)
            {
                var api = Manager(); IntPtr window = GetForegroundWindow();
                if (window != IntPtr.Zero && api != null && api.IsWindowOnCurrentVirtualDesktop(window, out bool onCurrent) == 0 && onCurrent)
                    api.GetWindowDesktopId(window, out current);
            }
            var identifiers = ReadIds(root?.GetValue("VirtualDesktopIDs") as byte[]);
            if (current != Guid.Empty && !identifiers.Contains(current)) identifiers.Add(current);
            var entries = new List<VirtualDesktopEntry>();
            foreach (Guid id in identifiers)
            {
                using var name = root?.OpenSubKey(@"Desktops\" + id.ToString("B"));
                entries.Add(new(id, name?.GetValue("Name") as string is { Length: > 0 } custom ? custom : $"桌面 {entries.Count + 1}"));
            }
            return new(current, entries);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException or COMException)
        { return new(Guid.Empty, []); }
    }
    internal static Guid? ReadGuid(object? value) => value is byte[] { Length: 16 } bytes && new Guid(bytes) != Guid.Empty ? new Guid(bytes) : null;
    internal static List<Guid> ReadIds(byte[]? bytes)
    {
        var result = new List<Guid>();
        if (bytes == null || bytes.Length > 16 * 256 || bytes.Length % 16 != 0) return result;
        for (int offset = 0; offset < bytes.Length; offset += 16)
        { Guid id = new(bytes.AsSpan(offset, 16)); if (id != Guid.Empty && !result.Contains(id)) result.Add(id); }
        return result;
    }
    IVirtualDesktopManager? Manager()
    {
        if (manager != null) return manager;
        try { manager = (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new("AA509086-5CA9-4C25-8F95-589D3C07B48A"))!)!; }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { }
        return manager;
    }
    internal void MoveOwnWindowToCurrent(IntPtr window)
    {
        var snapshot = Read();
        if (snapshot.Available) { try { Manager()?.MoveWindowToDesktop(window, snapshot.Current); } catch (COMException) { } }
    }
    internal bool MoveOwnWindow(IntPtr window, Guid desktop)
    { try { return Manager()?.MoveWindowToDesktop(window, desktop) == 0; } catch (COMException) { return false; } }
    internal Guid WindowDesktopId(IntPtr window)
    { try { return Manager() is { } api && api.GetWindowDesktopId(window, out var id) == 0 ? id : Guid.Empty; } catch (COMException) { return Guid.Empty; } }
    internal bool WindowOnCurrentDesktop(IntPtr window)
    { try { return Manager() is not { } api || api.IsWindowOnCurrentVirtualDesktop(window, out bool current) != 0 || current; } catch (COMException) { return true; } }
    public void Dispose() { if (manager != null) { Marshal.FinalReleaseComObject(manager); manager = null; } }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [ComImport, Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr window, [MarshalAs(UnmanagedType.Bool)] out bool onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr window, out Guid id);
        [PreserveSig] int MoveWindowToDesktop(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid id);
    }
}

sealed class VirtualDesktopProfile
{
    public string Name { get; set; } = "";
    public Dictionary<string, MonitorProfile> Monitors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public VirtualDesktopProfile Copy() => new() { Name = Name, Monitors = Monitors.Where(x => x.Value != null).ToDictionary(x => x.Key, x => x.Value.Copy(), StringComparer.OrdinalIgnoreCase) };
}
partial class Settings
{
    public string? LastVirtualDesktopId { get; set; }
    public Dictionary<string, VirtualDesktopProfile> VirtualDesktops { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

partial class MainWindow
{
    IVirtualDesktopSource? virtualDesktopSource;
    VirtualDesktopSnapshot virtualSnapshot = new(Guid.Empty, []);
    Guid activeVirtualDesktop;
    readonly System.Windows.Forms.Timer virtualDesktopTimer = new() { Interval = 300 };
    Button virtualDesktopButton = null!;
    bool changingVirtualDesktop;

    void PrepareVirtualDesktopProfiles(bool realSession, IVirtualDesktopSource? source)
    {
        settings.VirtualDesktops = (settings.VirtualDesktops ?? []).Where(x => Guid.TryParse(x.Key, out var id) && id != Guid.Empty && x.Value != null)
            .ToDictionary(x => Guid.Parse(x.Key).ToString(), x => x.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var profile in settings.VirtualDesktops.Values) profile.Monitors = new(profile.Monitors ?? [], StringComparer.OrdinalIgnoreCase);
        virtualDesktopSource = source ?? (realSession ? new WindowsVirtualDesktopSource() : null);
        virtualSnapshot = virtualDesktopSource?.Read() ?? new(Guid.Empty, []);
        activeVirtualDesktop = virtualSnapshot.Available ? virtualSnapshot.Current : Guid.TryParse(settings.LastVirtualDesktopId, out var previous) ? previous : Guid.Empty;
        if (activeVirtualDesktop != Guid.Empty && settings.VirtualDesktops.TryGetValue(activeVirtualDesktop.ToString(), out var saved))
            settings.Monitors = saved.Copy().Monitors;
    }
    void CaptureVirtualDesktopProfile()
    {
        if (activeVirtualDesktop == Guid.Empty) return;
        string key = activeVirtualDesktop.ToString();
        var existing = settings.VirtualDesktops.GetValueOrDefault(key);
        settings.VirtualDesktops[key] = new() { Name = virtualSnapshot.Desktops.FirstOrDefault(x => x.Id == activeVirtualDesktop)?.Name ?? existing?.Name ?? "桌面",
            Monitors = settings.Monitors.Where(x => x.Value != null).ToDictionary(x => x.Key, x => x.Value.Copy(), StringComparer.OrdinalIgnoreCase) };
        settings.LastVirtualDesktopId = key;
    }
    Control CreateVirtualDesktopRow()
    {
        virtualDesktopButton = Button("", OpenVirtualDesktopSettings); RefreshVirtualDesktopLabel();
        tooltips.SetToolTip(virtualDesktopButton, "分别设置 Windows 虚拟桌面的壁纸；在系统切换桌面时自动切换");
        return virtualDesktopButton;
    }
    void RefreshVirtualDesktopLabel()
    {
        if (virtualDesktopButton == null) return;
        string? name = virtualSnapshot.Desktops.FirstOrDefault(x => x.Id == activeVirtualDesktop)?.Name;
        virtualDesktopButton.Text = name == null ? "虚拟桌面 · 查看配置" : $"虚拟桌面：{name} · 配置其他桌面";
    }
    void StartVirtualDesktopTracking()
    {
        if (virtualDesktopSource == null) return;
        virtualDesktopTimer.Tick += async (_, _) =>
        {
            if (disposed) return;
            try
            {
                var snapshot = virtualDesktopSource.Read();
                if (changingVirtualDesktop)
                { if (snapshot.Available && snapshot.Current != activeVirtualDesktop) foreach (var monitor in monitorRuntimes.Values) monitor.Request?.Cancel(); return; }
                await ChangeVirtualDesktopAsync(snapshot, realUserSession);
            }
            catch (Exception ex) { if (!disposed) status.Text = "虚拟桌面壁纸切换失败：" + ex.Message; }
        };
        virtualDesktopTimer.Start();
    }
    internal async Task ChangeVirtualDesktopAsync(VirtualDesktopSnapshot snapshot, bool restore = true)
    {
        if (disposed || changingVirtualDesktop || !snapshot.Available) return;
        virtualSnapshot = snapshot;
        if (snapshot.Current == activeVirtualDesktop) { RefreshVirtualDesktopLabel(); UpdatePreviewVisibility(); return; }
        changingVirtualDesktop = true;
        try
        {
            TryFlushTransforms(); CaptureCurrentMonitor(); CaptureVirtualDesktopProfile();
            // Keep the shell's redirected surface alive while the next desktop prepares.
            // Closing and attaching another layered window exposes the Windows background twice.
            var outgoing = monitorRuntimes.Values.ToArray();
            preview.ClearScene();
            foreach (var monitor in outgoing)
            {
                monitor.Request?.Cancel(); monitor.Playlist.Stop();
                if (monitor.Scene is { } scene) { scene.AudioEnabled = false; scene.Paused = true; }
            }
            monitorRuntimes.Clear();
            activeVirtualDesktop = snapshot.Current;
            settings.Monitors = settings.VirtualDesktops.GetValueOrDefault(activeVirtualDesktop.ToString())?.Copy().Monitors ?? new(StringComparer.OrdinalIgnoreCase);
            settings.Transforms = new(StringComparer.OrdinalIgnoreCase); settings.LayoutModes = new(StringComparer.OrdinalIgnoreCase);
            settings.Playlist = new(); settings.Volume = 100; settings.Muted = false;
            InitializeMonitorProfiles();
            foreach (var previous in outgoing)
            {
                if (restore && monitorRuntimes.TryGetValue(previous.Id, out var next)
                    && next.Profile.WallpaperKey is string key && items.Any(x => x.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                    && previous.Window is { IsDisposed: false } host && previous.Scene is TransitionScene
                    && previous.Bounds == next.Bounds)
                {
                    next.Window = host; next.Scene = previous.Scene; next.HoldingPrevious = true;
                    next.HeldWallpaper = previous.Active ?? previous.HeldWallpaper;
                    previous.Window = null; previous.Scene = null; previous.Active = null;
                }
                else { previous.Window?.Dispose(); if (previous.Window == null) previous.Scene?.Dispose(); }
            }
            BindMonitor(CurrentMonitor);
            updatingControls = true;
            try { volumeValue.Value = settings.Volume; muteAudio.Checked = settings.Muted; }
            finally { updatingControls = false; }
            preview.Paused = CurrentMonitor.Profile.Paused; UpdatePreviewVisibility();
            int selected = library.Items.ToList().FindIndex(x => x.Key == CurrentMonitor.Profile.WallpaperKey);
            if (selected >= 0) library.SelectedIndex = selected;
            SelectWallpaper(); RefreshPlaylist(); RefreshVirtualDesktopLabel();
            string? saveError = null;
            try { Save(); } catch (Exception ex) { saveError = ex.Message; }
            if (restore) await RestoreLastSessionAsync();
            if (!disposed) status.Text = monitorRuntimes.Values.Any(x => x.HoldingPrevious)
                ? "当前桌面壁纸未能加载 · 暂时保留上一画面，可在运行状态查看原因或重新应用"
                : $"已切换到 {snapshot.Desktops.First(x => x.Id == snapshot.Current).Name} · 壁纸及播放设置独立保存" + (saveError == null ? "" : " · 配置未保存：" + saveError);
        }
        finally { changingVirtualDesktop = false; }
    }
    internal void SetVirtualDesktopWallpaper(Guid id, string monitorId, Wallpaper item)
    {
        if (!virtualSnapshot.Desktops.Any(x => x.Id == id) || !items.Any(x => x.Key == item.Key)) throw new ArgumentException("请选择有效的虚拟桌面和壁纸。");
        if (id == activeVirtualDesktop) throw new ArgumentException("当前桌面请在主窗口选择壁纸并点击应用。");
        var previous = settings.VirtualDesktops;
        settings.VirtualDesktops = previous.ToDictionary(x => x.Key, x => x.Value.Copy(), StringComparer.OrdinalIgnoreCase);
        string key = id.ToString();
        if (!settings.VirtualDesktops.TryGetValue(key, out var profile)) settings.VirtualDesktops[key] = profile = new() { Name = virtualSnapshot.Desktops.First(x => x.Id == id).Name };
        if (!profile.Monitors.TryGetValue(monitorId, out var monitor)) profile.Monitors[monitorId] = monitor = new() { Muted = monitorId != Screen.PrimaryScreen!.DeviceName };
        monitor.WallpaperKey = item.Key; monitor.PlaylistEnabled = false;
        monitor.Transforms[item.Key] = settings.Transforms.GetValueOrDefault(item.Key) ?? new();
        monitor.LayoutModes[item.Key] = settings.LayoutModes.GetValueOrDefault(item.Key);
        try { Save(); } catch { settings.VirtualDesktops = previous; throw; }
    }
    internal void PresentLibrary()
    {
        if (virtualDesktopSource is WindowsVirtualDesktopSource source) source.MoveOwnWindowToCurrent(Handle);
        Show(); WindowState = FormWindowState.Normal; Activate();
        UpdatePreviewVisibility();
    }
    void UpdatePreviewVisibility()
    {
        preview.RenderingSuspended = !Visible || WindowState == FormWindowState.Minimized
            || IsHandleCreated && virtualDesktopSource is WindowsVirtualDesktopSource source && !source.WindowOnCurrentDesktop(Handle);
    }
    void OpenVirtualDesktopSettings()
    {
        RunAsync(async () =>
        {
            if (virtualDesktopSource != null) await ChangeVirtualDesktopAsync(virtualDesktopSource.Read(), realUserSession);
            if (disposed) return;
            using var dialog = new VirtualDesktopDialog(this, virtualSnapshot, activeVirtualDesktop, items, Selected);
            PrepareDialog(dialog); dialog.ShowDialog(this);
        });
    }
    internal string? VirtualDesktopWallpaperKey(Guid id, string monitorId)
        => id == activeVirtualDesktop ? settings.Monitors.GetValueOrDefault(monitorId)?.WallpaperKey : settings.VirtualDesktops.GetValueOrDefault(id.ToString())?.Monitors.GetValueOrDefault(monitorId)?.WallpaperKey;

    internal Dictionary<string, VirtualDesktopProfile> CopyVirtualProfiles()
        => settings.VirtualDesktops.ToDictionary(x => x.Key, x => x.Value.Copy(), StringComparer.OrdinalIgnoreCase);
    void RemoveVirtualWallpaperReferences(string key, bool keepTransforms)
    {
        settings.VirtualDesktops = CopyVirtualProfiles();
        foreach (var profile in settings.VirtualDesktops.Values.SelectMany(x => x.Monitors.Values))
        {
            if (!keepTransforms) { profile.Transforms.Remove(key); profile.LayoutModes.Remove(key); }
            profile.Playlist.Keys.RemoveAll(x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (profile.WallpaperKey?.Equals(key, StringComparison.OrdinalIgnoreCase) == true) { profile.WallpaperKey = null; profile.PlaylistEnabled = false; }
        }
    }
    void RemapVirtualWallpaperReferences(string oldKey, string newKey)
    {
        settings.VirtualDesktops = CopyVirtualProfiles();
        foreach (var profile in settings.VirtualDesktops.Values.SelectMany(x => x.Monitors.Values))
        {
            if (profile.WallpaperKey?.Equals(oldKey, StringComparison.OrdinalIgnoreCase) == true) profile.WallpaperKey = newKey;
            profile.Playlist.Keys = profile.Playlist.Keys.Select(x => x.Equals(oldKey, StringComparison.OrdinalIgnoreCase) ? newKey : x).ToList();
            if (profile.Transforms.Remove(oldKey, out var transform)) profile.Transforms[newKey] = transform;
            if (profile.LayoutModes.Remove(oldKey, out var mode)) profile.LayoutModes[newKey] = mode;
        }
    }
}

sealed class VirtualDesktopDialog : Form
{
    readonly ComboBox desktops = new(), monitors = new(), wallpapers = new();
    readonly Label feedback = new() { Dock = DockStyle.Fill, ForeColor = Palette.Muted };
    readonly Button save;
    public VirtualDesktopDialog(MainWindow owner, VirtualDesktopSnapshot snapshot, Guid active, IReadOnlyList<Wallpaper> items, Wallpaper? selected)
    {
        Text = "Windows 虚拟桌面 · 独立壁纸"; Font = new("Microsoft YaHei UI", 10); BackColor = Palette.Surface; ForeColor = Palette.Text;
        Size = new(760, 460); MinimumSize = Size; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), ColumnCount = 2, RowCount = 6 };
        layout.ColumnStyles.Add(new(SizeType.Absolute, 112)); layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        foreach (int height in new[] { 46, 48, 48, 48, 120, 44 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.Controls.Add(new Label { Text = "切换 Windows 桌面时，自动使用该桌面的壁纸、位置、音量和播放列表。", Dock = DockStyle.Fill }, 0, 0); layout.SetColumnSpan(layout.GetControlFromPosition(0, 0)!, 2);
        foreach (var combo in new[] { desktops, monitors, wallpapers }) { combo.DropDownStyle = ComboBoxStyle.DropDownList; combo.Dock = DockStyle.Fill; combo.BackColor = Palette.Field; combo.ForeColor = Palette.Text; }
        desktops.DisplayMember = "Name"; foreach (var desktop in snapshot.Desktops) desktops.Items.Add(desktop with { Name = desktop.Name + (desktop.Id == active ? "（当前）" : "") });
        foreach (var screen in Screen.AllScreens) monitors.Items.Add(screen.DeviceName);
        wallpapers.DisplayMember = "Name"; foreach (var item in items) wallpapers.Items.Add(item);
        int row = 1;
        foreach (var (label, combo) in new[] { ("虚拟桌面", desktops), ("显示器", monitors), ("选择壁纸", wallpapers) })
        { layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row); layout.Controls.Add(combo, 1, row++); }
        layout.Controls.Add(feedback, 0, 4); layout.SetColumnSpan(feedback, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = EngineSettingsDialog.DialogButton("关闭", DialogResult.Cancel, false); save = EngineSettingsDialog.DialogButton("保存壁纸", DialogResult.None, true);
        actions.Controls.AddRange([close, save]); layout.Controls.Add(actions, 0, 5); layout.SetColumnSpan(actions, 2); Controls.Add(layout); CancelButton = close;
        void RefreshSelection()
        {
            if (desktops.SelectedItem is not VirtualDesktopEntry desktop) { save.Enabled = false; feedback.Text = "当前无法识别 Windows 虚拟桌面。请在 Windows 中创建桌面后重新打开；壁纸播放保持可用。"; return; }
            bool current = desktop.Id == active; save.Enabled = !current && wallpapers.SelectedIndex >= 0;
            string? key = owner.VirtualDesktopWallpaperKey(desktop.Id, monitors.SelectedItem?.ToString() ?? "");
            feedback.Text = $"已配置：{items.FirstOrDefault(x => x.Key == key)?.Name ?? "系统原壁纸"}\n" + (current ? "当前桌面使用主窗口的“应用到桌面”；其余设置也在主窗口调整。" : "保存后，在 Windows 切换到此桌面时生效。首次使用未配置的桌面会显示系统原壁纸。");
        }
        desktops.SelectedIndexChanged += (_, _) => RefreshSelection(); monitors.SelectedIndexChanged += (_, _) => RefreshSelection(); wallpapers.SelectedIndexChanged += (_, _) => RefreshSelection();
        save.Click += (_, _) =>
        {
            try { if (desktops.SelectedItem is VirtualDesktopEntry desktop && wallpapers.SelectedItem is Wallpaper item && monitors.SelectedItem is string monitor) { owner.SetVirtualDesktopWallpaper(desktop.Id, monitor, item); feedback.Text = "已保存 · 切换到这个 Windows 桌面时生效。"; } }
            catch (Exception ex) { feedback.Text = ex.Message; }
        };
        if (monitors.Items.Count > 0) monitors.SelectedIndex = 0;
        if (wallpapers.Items.Count > 0) wallpapers.SelectedIndex = Math.Max(0, items.ToList().FindIndex(x => x == selected));
        if (desktops.Items.Count > 0) desktops.SelectedIndex = Math.Max(0, snapshot.Desktops.ToList().FindIndex(x => x.Id != active));
        RefreshSelection();
    }
}
