using System.Diagnostics;
using System.Text;

namespace LumaWall;

partial class MainWindow
{
    readonly Dictionary<string, (VideoPlayback Source, long Decoded, long Presented, long Painted, double Render)> diagnosticSamples = new(StringComparer.OrdinalIgnoreCase);
    long diagnosticTime;
    TimeSpan diagnosticCpu;

    internal string DiagnosticText()
    {
        CaptureCurrentMonitor();
        long now = Stopwatch.GetTimestamp();
        double seconds = diagnosticTime == 0 ? 0 : Stopwatch.GetElapsedTime(diagnosticTime, now).TotalSeconds;
        using var process = Process.GetCurrentProcess();
        var cpu = process.TotalProcessorTime;
        double usage = seconds <= 0 ? 0 : Math.Clamp((cpu - diagnosticCpu).TotalSeconds / seconds / Environment.ProcessorCount * 100, 0, 100);
        var text = new StringBuilder();
        text.AppendLine( $"LumaWall {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · CPU {usage:F1}% · 内存 {process.WorkingSet64 / 1048576.0:F1} MB");
        text.AppendLine($"缩略图内存缓存 {library.CachedThumbnailCount} / 64 · 正在加载 {library.LoadingCount}");
        text.AppendLine();
        foreach (var monitor in monitorRuntimes.Values)
        {
            text.AppendLine(monitor.Id + $" · {monitor.Bounds.Width}×{monitor.Bounds.Height}");
            text.AppendLine("壁纸：" + (monitor.Active?.Name ?? (monitor.HoldingPrevious ? "暂时保留：" + monitor.HeldWallpaper?.Name : "未播放")));
            text.AppendLine("状态：" + (monitor.HoldingPrevious ? "等待新画面 · 旧画面暂停并静音" : monitor.Scene == null ? "已停止" : monitor.Profile.Paused ? "手动暂停" : monitor.AutoPaused ? "系统自动暂停" : "播放中")
                + (monitor.Profile.Muted ? " · 静音" : $" · 音量 {monitor.Profile.Volume}%"));
            if (monitor.Scene is { } scene && scene.Video is { } video)
            {
                long decoded = video.FrameCount, presented = scene.NativePresentedFrames, dropped = video.DroppedFrameCount;
                long painted = scene.NativePaintedFrames;
                double render = scene.NativeTotalPaintMilliseconds;
                bool found = diagnosticSamples.TryGetValue(monitor.Id, out var before);
                bool sampled = seconds > 0 && found
                    && ReferenceEquals(before.Source, video) && decoded >= before.Decoded && presented >= before.Presented && painted >= before.Painted && render >= before.Render;
                text.AppendLine(!sampled ? "正在采样帧率…" : $"解码发布 {(decoded - before.Decoded) / seconds:F1} FPS · 完整绘制 {(presented - before.Presented) / seconds:F1} FPS");
                long count = sampled ? painted - before.Painted : 0;
                text.AppendLine($"累计丢帧 {dropped} · 本次采样绘制平均 {(count > 0 ? (render - before.Render) / count : 0):F2} ms · 最大 {scene.NativeMaximumPaintMilliseconds:F2} ms");
                text.AppendLine($"视频进度 {video.Clock.PositionTicks / 10000000.0:F1} 秒 · 已循环 {video.LoopCount} 次");
                text.AppendLine($"视频原帧率 {10000000.0 / Math.Max(1, video.FrameDuration):F2} FPS · 刷新上限 {FrameRateOptions.Limit(settings.Fps)} FPS");
                text.AppendLine($"视频处理：{(video.Accelerated ? "显卡视频路径" : "兼容处理路径")} · 画面落后时钟 {Math.Max(0, video.Clock.PositionTicks - video.PresentedPositionTicks) / 10000.0:F1} ms");
                diagnosticSamples[monitor.Id] = (video, decoded, presented, painted, render);
            }
            else diagnosticSamples.Remove(monitor.Id);
            text.AppendLine();
        }
        text.AppendLine("帧率为窗口完成绘制统计；被其他窗口遮住时不能据此判断屏幕实际显示。");
        text.AppendLine(); text.AppendLine("最近错误："); text.AppendLine(lastPlaybackError);
        diagnosticTime = now; diagnosticCpu = cpu;
        return text.ToString();
    }
}

sealed class DiagnosticsDialog : Form
{
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    public DiagnosticsDialog(MainWindow owner)
    {
        Text = "运行状态"; AutoScaleMode = AutoScaleMode.Dpi; Font = new("Microsoft YaHei UI", 10);
        Size = new(760, 600); MinimumSize = new(620, 420); StartPosition = FormStartPosition.CenterParent;
        BackColor = Palette.Surface; ForeColor = Palette.Text; ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(20), ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 48));
        var output = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            BackColor = Palette.Field, ForeColor = Palette.Text, BorderStyle = BorderStyle.FixedSingle, Text = owner.DiagnosticText(), AccessibleName = "性能和错误详情" };
        layout.Controls.Add(output, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = EngineSettingsDialog.DialogButton("关闭", DialogResult.OK, true);
        var copy = EngineSettingsDialog.DialogButton("复制详情", DialogResult.None, false);
        copy.Click += (_, _) => Clipboard.SetText(output.Text);
        var update = OnlineWallpaperDialog.ActionButton("检查更新"); update.Click += async (_, _) => await owner.CheckAppUpdatesAsync();
        buttons.Controls.Add(close); buttons.Controls.Add(copy); buttons.Controls.Add(update); layout.Controls.Add(buttons, 0, 1); Controls.Add(layout); CancelButton = close;
        timer.Tick += (_, _) => { if (!output.Focused) output.Text = owner.DiagnosticText(); }; timer.Start();
    }
    protected override void Dispose(bool disposing) { if (disposing) timer.Dispose(); base.Dispose(disposing); }
}
