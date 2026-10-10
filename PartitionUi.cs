namespace LumaWall;
partial class MainWindow
{
    float UiScale => DeviceDpi / 96f * settings.TextScale / 100f;
    static float SafeWidth(double ratio, float fallback, int total, float minimum, float maximum)
    {
        float requested = double.IsFinite(ratio) && ratio > 0 ? (float)(ratio * total) : fallback;
        return Math.Clamp(requested, Math.Min(minimum, maximum), Math.Max(1, maximum));
    }
    float PlaylistWidth() => SafeWidth(settings.PlaylistWidthRatio, 360 * UiScale, libraryWorkspace.ClientSize.Width, 210 * UiScale, libraryWorkspace.ClientSize.Width * .65f);
    void UpdateInspectorWidth()
    {
        if (body == null || body.ClientSize.Width <= 0) return;
        int total = Math.Max(1, body.ClientSize.Width - body.Padding.Horizontal);
        float width = SafeWidth(settings.InspectorWidthRatio, 448 * UiScale, total, 280 * UiScale, total * .6f);
        if (Math.Abs(body.ColumnStyles[2].Width - width) > 1) body.ColumnStyles[2].Width = width;
    }
    internal void ResizeInspector(int delta)
    {
        int total = Math.Max(1, body.ClientSize.Width - body.Padding.Horizontal);
        settings.InspectorWidthRatio = SafeWidth((body.ColumnStyles[2].Width - delta) / total, 448 * UiScale, total, 280 * UiScale, total * .6f) / total;
        UpdateInspectorWidth();
    }
    internal void ResizePlaylist(int delta)
    {
        int total = Math.Max(1, libraryWorkspace.ClientSize.Width);
        settings.PlaylistWidthRatio = SafeWidth((PlaylistWidth() - delta) / total, 360 * UiScale, total, 210 * UiScale, total * .65f) / total;
        UpdatePlaylistSidebar();
    }
    void SavePartitionSizes() => TrySaveSettings("分区比例未保存");
    void ResetPartition(bool playlistPanel)
    {
        if (playlistPanel) settings.PlaylistWidthRatio = 0; else settings.InspectorWidthRatio = 0;
        UpdateInspectorWidth(); UpdatePlaylistSidebar(); SavePartitionSizes();
    }
}

// Absolute screen coordinates keep dragging stable while adjacent panels relayout.
sealed class PartitionGrip : Control
{
    readonly Action<int> resize;
    readonly Action save, reset;
    int lastX;
    bool hovered;
    public PartitionGrip(Action<int> resize, Action save, Action reset)
    {
        this.resize = resize; this.save = save; this.reset = reset;
        Cursor = Cursors.VSplit; TabStop = true; AccessibleRole = AccessibleRole.Separator;
        AccessibleName = "拖动调整分区宽度，左右方向键微调，双击恢复默认";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Focus(); lastX = PointToScreen(e.Location).X; Capture = true; } }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (!Capture) return; int x = PointToScreen(e.Location).X; int delta = x - lastX; lastX = x; resize(delta); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (Capture) { Capture = false; save(); } }
    protected override void OnMouseDoubleClick(MouseEventArgs e) { base.OnMouseDoubleClick(e); if (e.Button == MouseButtons.Left) reset(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode is Keys.Left or Keys.Right) { resize((e.KeyCode == Keys.Left ? -1 : 1) * (e.Shift ? 30 : 10)); save(); e.Handled = true; } }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(hovered || Focused ? Palette.Accent : Palette.Border, hovered || Focused ? 3 : 2);
        e.Graphics.DrawLine(pen, Width / 2, 8, Width / 2, Math.Max(8, Height - 8));
        using var dot = new SolidBrush(Palette.Muted);
        for (int y = Height / 2 - 12; y <= Height / 2 + 12; y += 8) e.Graphics.FillEllipse(dot, Width / 2 - 2, y, 4, 4);
    }
}
