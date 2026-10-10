using System.Drawing.Drawing2D;
namespace LumaWall;

partial class MainWindow
{
    TableLayoutPanel libraryWorkspace = null!, body = null!;
    PartitionGrip playlistGrip = null!;
    Control playlistSidebar = null!;
    Button playlistToggle = null!, viewImageButton = null!;
    void SetPlaylistSidebar(bool visible)
    {
        settings.PlaylistSidebarVisible = visible;
        UpdatePlaylistSidebar();
        TrySaveSettings("侧边栏状态未保存");
    }
    void UpdatePlaylistSidebar()
    {
        if (playlistSidebar == null || libraryWorkspace == null) return;
        bool visible = settings.PlaylistSidebarVisible;
        float width = visible ? PlaylistWidth() : 0;
        float gripWidth = visible ? 12 * DeviceDpi / 96f : 0;
        if (Math.Abs(libraryWorkspace.ColumnStyles[2].Width - width) > 1) libraryWorkspace.ColumnStyles[2].Width = width;
        if (libraryWorkspace.ColumnStyles[1].Width != gripWidth) libraryWorkspace.ColumnStyles[1].Width = gripWidth;
        if (playlistGrip != null) playlistGrip.Visible = visible;
        playlistSidebar.Visible = visible;
        playlistToggle.Text = visible ? "收起播放列表 ‹" : "播放列表 ›";
    }
    void OpenImageViewer()
    {
        if (Selected is not { Path: not null, Kind: not "video" } item) return;
        try
        {
            using var viewer = new ImageViewerDialog(item);
            PrepareDialog(viewer);
            viewer.ShowDialog(this);
        }
        catch (Exception ex) { status.Text = "无法打开大图：" + ex.Message; }
    }
}

sealed class ImageViewerDialog : Form
{
    internal readonly ImageViewport Viewport;
    public ImageViewerDialog(Wallpaper item)
    {
        Text = item.Name + " · 大图查看"; Icon = AppIcon.Value;
        BackColor = Palette.Background; ForeColor = Palette.Text; Font = new("Microsoft YaHei UI", 10);
        StartPosition = FormStartPosition.CenterParent; Size = new(1200, 820); MinimumSize = new(640, 440); KeyPreview = true;
        Viewport = new ImageViewport(item.Path!) { Dock = DockStyle.Fill };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new(8), BackColor = Palette.Surface, WrapContents = false };
        var info = new Label { AutoSize = true, ForeColor = Palette.Muted, Margin = new(12, 7, 0, 0) };
        void Add(string text, Action action) { var button = new Button { Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Palette.Field, ForeColor = Palette.Text }; button.Click += (_, _) => action(); bar.Controls.Add(button); }
        Add("适应窗口", Viewport.Fit); Add("原始大小", () => Viewport.Zoom(1, new(Viewport.Width / 2f, Viewport.Height / 2f))); Add("关闭 (Esc)", Close);
        bar.Controls.Add(info);
        Viewport.ViewChanged += (_, _) => info.Text = $"{Viewport.ImageSize.Width} × {Viewport.ImageSize.Height} · {Viewport.ScaleFactor:P0} · 滚轮缩放 / 拖动查看";
        Controls.Add(Viewport); Controls.Add(bar);
        Shown += (_, _) => { Viewport.Fit(); Viewport.Focus(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; } };
    }
}

sealed class ImageViewport : Control
{
    readonly Image image;
    readonly MemoryStream source;
    float scale = 1;
    PointF offset;
    Point last;
    bool fitting = true;
    internal float ScaleFactor => scale;
    internal Size ImageSize => image.Size;
    internal event EventHandler? ViewChanged;
    public ImageViewport(string path)
    {
        source = new MemoryStream(File.ReadAllBytes(path));
        try { image = Image.FromStream(source); }
        catch { source.Dispose(); throw; }
        BackColor = Palette.Background; TabStop = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
    }
    internal void Fit()
    {
        fitting = true;
        scale = Math.Max(.001f, Math.Min(Width / (float)image.Width, Height / (float)image.Height));
        offset = new((Width - image.Width * scale) / 2, (Height - image.Height * scale) / 2);
        Invalidate(); ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    internal void Zoom(float value, PointF anchor)
    {
        fitting = false;
        value = Math.Clamp(value, .001f, 16);
        float ratio = value / scale;
        offset = new(anchor.X - (anchor.X - offset.X) * ratio, anchor.Y - (anchor.Y - offset.Y) * ratio);
        scale = value; Invalidate(); ViewChanged?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); if (fitting && image != null) Fit(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Zoom(scale * (float)Math.Pow(1.2, e.Delta / 120.0), e.Location); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Focus(); if (e.Button == MouseButtons.Left) { last = e.Location; Capture = true; } }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (!Capture) return; fitting = false; offset.X += e.X - last.X; offset.Y += e.Y - last.Y; last = e.Location; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Capture = false; }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); e.Graphics.InterpolationMode = scale > 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
        e.Graphics.DrawImage(image, new RectangleF(offset.X, offset.Y, image.Width * scale, image.Height * scale));
    }
    protected override void Dispose(bool disposing) { if (disposing) { image.Dispose(); source.Dispose(); } base.Dispose(disposing); }
}
