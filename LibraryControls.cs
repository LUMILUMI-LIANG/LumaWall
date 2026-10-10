using System.Drawing.Drawing2D;

namespace LumaWall;

static class Palette
{
    public static readonly Color Background = Color.FromArgb(15, 20, 29);
    public static readonly Color Surface = Color.FromArgb(22, 29, 40);
    public static readonly Color Field = Color.FromArgb(30, 39, 52);
    public static readonly Color Border = Color.FromArgb(43, 55, 70);
    public static readonly Color Text = Color.FromArgb(237, 242, 248);
    public static readonly Color Muted = Color.FromArgb(151, 168, 187);
    public static readonly Color Accent = Color.FromArgb(84, 220, 199);
}

// Static cached thumbnails; animations run only in the preview and desktop scene.
class ThumbnailLibrary : ScrollableControl
{
    readonly Dictionary<string, Bitmap> thumbnails = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, ThumbnailRequest> pendingPosters = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> readyPosters = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> posterErrors = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> signatures = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> cachedSignatures = new(StringComparer.OrdinalIgnoreCase);
    readonly LinkedList<string> recent = new();
    readonly Dictionary<string, LinkedListNode<string>> recentNodes = new(StringComparer.OrdinalIgnoreCase);
    readonly CancellationTokenSource posterLifetime = new();
    List<Wallpaper> items = [];
    int selectedIndex = -1;
    int selectionAnchor = -1;
    readonly HashSet<string> selectedKeys = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<Wallpaper> SelectedItems => items.Where(x => selectedKeys.Contains(x.Key)).ToArray();
    int thumbnailWidth = 280;
    bool stopping;
    public string? CacheFolder { get; set; }
    public Func<Wallpaper, bool>? IsFavorite { get; set; }
    public IReadOnlyList<Wallpaper> Items => items;
    internal int CachedThumbnailCount => thumbnails.Count;
    internal int LoadingCount => pendingPosters.Count;
    internal bool PosterReady(string key) => readyPosters.Contains(key);
    internal string PosterState(string key) => posterErrors.TryGetValue(key, out var error) ? "failed: " + error
        : readyPosters.Contains(key) ? "ready" : pendingPosters.ContainsKey(key) ? "loading"
        : $"not requested; handle={IsHandleCreated}; disposed={IsDisposed}";
    public event EventHandler? ImageActivated;
    public event EventHandler? SelectedIndexChanged;
    public event EventHandler? ThumbnailSizeChanged;
    public int ThumbnailWidth
    {
        get => thumbnailWidth;
        set
        {
            int next = Math.Clamp(value, 160, 480); if (next == thumbnailWidth) return;
            int anchor = Math.Clamp((-AutoScrollPosition.Y / Math.Max(1, TileHeight + Gap)) * Columns, 0, Math.Max(0, items.Count - 1));
            thumbnailWidth = next; UpdateScrollSize(); AutoScrollPosition = new(0, TileBounds(anchor).Top);
            RequestVisibleThumbnails(); Invalidate(); ThumbnailSizeChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    internal void ResizeThumbnails(int wheelDelta) => ThumbnailWidth += Math.Sign(wheelDelta) * 20;
    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            int next = value >= 0 && value < items.Count ? value : -1;
            if (next == selectedIndex && selectedKeys.Count == (next < 0 ? 0 : 1) && (next < 0 || selectedKeys.Contains(items[next].Key))) return;
            selectedKeys.Clear(); if (next >= 0) selectedKeys.Add(items[next].Key);
            selectionAnchor = next;
            selectedIndex = next; Invalidate(); EnsureSelectionVisible(); RequestVisibleThumbnails();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    int Gap => Scale(16);
    int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
    int Columns => Math.Max(1, (ClientSize.Width + Gap) / (Scale(thumbnailWidth) + Gap));
    int TileWidth => Math.Max(80, Math.Min(Scale(thumbnailWidth), ClientSize.Width));
    int NameLine => Math.Max(Scale(22), Font.Height + Scale(3));
    int TileHeight => (int)Math.Round(TileWidth * 9.0 / 16) + NameLine * 2 + Scale(10);
    public ThumbnailLibrary()
    {
        Dock = DockStyle.Fill; AutoScroll = true; BackColor = Palette.Surface; ForeColor = Palette.Text; TabStop = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
        AccessibleRole = AccessibleRole.List; AccessibleName = "壁纸缩略图库";
    }
    public void SetItems(List<Wallpaper> value)
    {
        items = value;
        selectedKeys.IntersectWith(items.Select(x => x.Key));
        if (selectionAnchor >= items.Count) selectionAnchor = -1;
        var nextSignatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            try
            {
                string signature = ThumbnailCache.Signature(item); nextSignatures[item.Key] = signature;
                if (cachedSignatures.TryGetValue(item.Key, out var previous) && previous != signature) RemoveThumbnail(item.Key);
                if (!signatures.TryGetValue(item.Key, out previous) || previous != signature) posterErrors.Remove(item.Key);
            }
            catch (Exception ex) { RemoveThumbnail(item.Key); posterErrors[item.Key] = ex.Message; }
        }
        signatures.Clear(); foreach (var entry in nextSignatures) signatures[entry.Key] = entry.Value;
        if (selectedIndex >= items.Count) SelectedIndex = items.Count - 1;
        UpdateScrollSize(); Invalidate();
        RequestVisibleThumbnails();
    }
    public void SetView(IReadOnlyList<Wallpaper> value) => SetItems(value as List<Wallpaper> ?? value.ToList());
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); RequestVisibleThumbnails(); }
    IEnumerable<int> RequestedIndices()
    {
        if (selectedIndex >= 0 && selectedIndex < items.Count) yield return selectedIndex;
        int rowHeight = Math.Max(1, TileHeight + Gap), top = Math.Max(0, -AutoScrollPosition.Y);
        int first = Math.Max(0, top / rowHeight - 1) * Columns;
        int last = Math.Min(items.Count, (top / rowHeight + (ClientSize.Height + rowHeight - 1) / rowHeight + 2) * Columns);
        // The cache deliberately stays small even when an unusually large window
        // exposes hundreds of tiles. Normal windows request a few visible rows.
        int remaining = 63;
        for (int i = first; i < last && remaining > 0; i++) if (i != selectedIndex) { remaining--; yield return i; }
    }
    void RequestVisibleThumbnails()
    {
        if (!IsHandleCreated || stopping || IsDisposed || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        var requested = RequestedIndices().Select(i => items[i]).ToArray();
        var wanted = requested.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in pendingPosters.ToArray())
            if (!wanted.Contains(entry.Key) || !signatures.TryGetValue(entry.Key, out var signature) || signature != entry.Value.Signature)
            { pendingPosters.Remove(entry.Key); entry.Value.Cancellation.Cancel(); }
        foreach (var item in requested)
        {
            string key = item.Key;
            if (thumbnails.ContainsKey(key)) { Touch(key); continue; }
            if (pendingPosters.ContainsKey(key) || posterErrors.ContainsKey(key) || !signatures.TryGetValue(key, out var signature)) continue;
            var request = new ThumbnailRequest(signature, CancellationTokenSource.CreateLinkedTokenSource(posterLifetime.Token));
            pendingPosters[key] = request; LoadPoster(item, request);
        }
    }
    async void LoadPoster(Wallpaper item, ThumbnailRequest request)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await ThumbnailCache.LoadAsync(item, request.Signature, CacheFolder, request.Cancellation.Token);
            if (!IsCurrent(item.Key, request)) return;
            RemoveThumbnail(item.Key); thumbnails[item.Key] = bitmap; bitmap = null;
            cachedSignatures[item.Key] = request.Signature; Touch(item.Key);
            while (thumbnails.Count > 64 && recent.First is { } oldest) RemoveThumbnail(oldest.Value);
            readyPosters.Add(item.Key); posterErrors.Remove(item.Key); Invalidate();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsCurrent(item.Key, request)) { posterErrors[item.Key] = ex.Message; Invalidate(); }
        }
        finally
        {
            bitmap?.Dispose();
            if (pendingPosters.TryGetValue(item.Key, out var current) && ReferenceEquals(current, request)) pendingPosters.Remove(item.Key);
            request.Cancellation.Dispose();
        }
    }
    bool IsCurrent(string key, ThumbnailRequest request) => !stopping && !IsDisposed && !request.Cancellation.IsCancellationRequested
        && pendingPosters.TryGetValue(key, out var current) && ReferenceEquals(current, request)
        && signatures.TryGetValue(key, out var signature) && signature == request.Signature;
    void Touch(string key)
    {
        if (recentNodes.Remove(key, out var node)) recent.Remove(node);
        recentNodes[key] = recent.AddLast(key);
    }
    void RemoveThumbnail(string key)
    {
        if (thumbnails.Remove(key, out var previous)) previous.Dispose();
        cachedSignatures.Remove(key); readyPosters.Remove(key);
        if (recentNodes.Remove(key, out var node)) recent.Remove(node);
    }
    sealed record ThumbnailRequest(string Signature, CancellationTokenSource Cancellation);
    void UpdateScrollSize()
    {
        int rows = (items.Count + Columns - 1) / Columns;
        AutoScrollMinSize = new(0, Math.Max(0, rows * (TileHeight + Gap) - Gap));
    }
    Rectangle TileBounds(int index) => new((index % Columns) * (TileWidth + Gap), (index / Columns) * (TileHeight + Gap), TileWidth, TileHeight);
    void EnsureSelectionVisible()
    {
        if (selectedIndex < 0) return;
        var r = TileBounds(selectedIndex); int top = -AutoScrollPosition.Y;
        if (r.Top < top) AutoScrollPosition = new(0, r.Top);
        else if (r.Bottom > top + ClientSize.Height) AutoScrollPosition = new(0, r.Bottom - ClientSize.Height);
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateScrollSize(); EnsureSelectionVisible(); RequestVisibleThumbnails(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); RequestVisibleThumbnails(); var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        if (items.Count == 0)
        {
            TextRenderer.DrawText(g, "还没有壁纸\n将图片、GIF 或视频拖到这里", Font, ClientRectangle, Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak); return;
        }
        int stride = Math.Max(1, TileHeight + Gap), top = Math.Max(0, -AutoScrollPosition.Y);
        int first = top / stride * Columns, last = Math.Min(items.Count, (top / stride + ClientSize.Height / stride + 2) * Columns);
        for (int i = first; i < last; i++)
        {
            var r = TileBounds(i); r.Offset(AutoScrollPosition);
            if (r.Bottom < 0 || r.Top > ClientSize.Height) continue;
            using var fill = new SolidBrush(selectedKeys.Contains(items[i].Key) ? Color.FromArgb(31, 49, 58) : Palette.Field);
            g.FillRectangle(fill, r);
            var picture = new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, (int)Math.Round(r.Width * 9.0 / 16) - 2);
            if (thumbnails.TryGetValue(items[i].Key, out var bitmap)) { Touch(items[i].Key); g.DrawImage(bitmap, picture); }
            else TextRenderer.DrawText(g, posterErrors.ContainsKey(items[i].Key) ? "无法加载媒体" : "正在生成缩略图…", Font, picture, Palette.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            using var border = new Pen(selectedKeys.Contains(items[i].Key) ? Palette.Accent : Palette.Border, selectedKeys.Contains(items[i].Key) ? 2 : 1);
            g.DrawRectangle(border, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
            var name = new Rectangle(r.X + Scale(12), picture.Bottom + Scale(7), r.Width - Scale(24), NameLine);
            TextRenderer.DrawText(g, items[i].Name, Font, name, Palette.Text, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using var small = new Font(Font.FontFamily, Math.Max(8, Font.Size - 1));
            string kind = items[i].Path == null ? "动态场景" : items[i].Kind == "video" ? "视频 · 原声循环" : string.Equals(Path.GetExtension(items[i].Path), ".gif", StringComparison.OrdinalIgnoreCase) ? "GIF 动图" : "本地图片";
            name.Offset(0, NameLine); TextRenderer.DrawText(g, kind, small, name, Palette.Muted, TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (IsFavorite?.Invoke(items[i]) == true)
                TextRenderer.DrawText(g, "★", Font, new Rectangle(r.Left + Scale(10), r.Top + Scale(8), Scale(28), Scale(28)), Color.FromArgb(255, 214, 101), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (selectedKeys.Contains(items[i].Key))
            {
                float cx = r.Right - Scale(22), cy = r.Top + Scale(22), radius = Scale(10);
                using var selected = new SolidBrush(Palette.Accent); g.FillEllipse(selected, cx - radius, cy - radius, radius * 2, radius * 2);
                using var check = new Pen(Palette.Background, Scale(2)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                g.DrawLines(check, [new PointF(cx - Scale(4), cy), new PointF(cx - Scale(1), cy + Scale(3)), new PointF(cx + Scale(5), cy - Scale(4))]);
            }
        }
        if (Focused && selectedIndex >= 0) { var r = TileBounds(selectedIndex); r.Offset(AutoScrollPosition); r.Inflate(-5, -5); ControlPaint.DrawFocusRectangle(g, r, Palette.Accent, BackColor); }
    }
    internal void SelectIndex(int index, bool control = false, bool shift = false)
    {
        if (index < 0 || index >= items.Count) return;
        if (shift && selectionAnchor >= 0)
        {
            if (!control) selectedKeys.Clear();
            for (int i = Math.Min(selectionAnchor, index); i <= Math.Max(selectionAnchor, index); i++) selectedKeys.Add(items[i].Key);
        }
        else if (control)
        {
            if (!selectedKeys.Add(items[index].Key)) selectedKeys.Remove(items[index].Key);
            selectionAnchor = index;
        }
        else { selectedKeys.Clear(); selectedKeys.Add(items[index].Key); selectionAnchor = index; }
        selectedIndex = selectedKeys.Contains(items[index].Key) ? index : items.FindIndex(x => selectedKeys.Contains(x.Key));
        EnsureSelectionVisible(); RequestVisibleThumbnails(); Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return; Focus();
        int x = e.X - AutoScrollPosition.X, y = e.Y - AutoScrollPosition.Y;
        int index = y / Math.Max(1, TileHeight + Gap) * Columns + x / Math.Max(1, TileWidth + Gap);
        if (index >= 0 && index < items.Count && TileBounds(index).Contains(x, y)) SelectIndex(index, ModifierKeys.HasFlag(Keys.Control), ModifierKeys.HasFlag(Keys.Shift));
    }
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        int x = e.X - AutoScrollPosition.X, y = e.Y - AutoScrollPosition.Y;
        int index = y / Math.Max(1, TileHeight + Gap) * Columns + x / Math.Max(1, TileWidth + Gap);
        if (index >= 0 && index < items.Count && TileBounds(index).Contains(x, y)) { SelectedIndex = index; ImageActivated?.Invoke(this, EventArgs.Empty); }
    }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); if (items.Count == 0 || e.Handled) return;
        if (e.Control && e.KeyCode == Keys.A) { selectedKeys.Clear(); foreach (var item in items) selectedKeys.Add(item.Key); if (selectedIndex < 0) selectedIndex = 0; Invalidate(); SelectedIndexChanged?.Invoke(this, EventArgs.Empty); e.Handled = e.SuppressKeyPress = true; return; }
        int next = selectedIndex < 0 ? 0 : selectedIndex;
        switch (e.KeyCode)
        {
            case Keys.Left: next--; break; case Keys.Right: next++; break;
            case Keys.Up: next -= Columns; break; case Keys.Down: next += Columns; break;
            case Keys.Home: next = 0; break; case Keys.End: next = items.Count - 1; break;
            default: return;
        }
        SelectIndex(Math.Clamp(next, 0, items.Count - 1), e.Control, e.Shift); e.Handled = true;
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); RequestVisibleThumbnails(); Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) != 0)
        { ResizeThumbnails(e.Delta); if (e is HandledMouseEventArgs handled) handled.Handled = true; return; }
        base.OnMouseWheel(e);
    }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateScrollSize(); RequestVisibleThumbnails(); Invalidate(); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !stopping)
        {
            stopping = true; posterLifetime.Cancel(); pendingPosters.Clear();
            foreach (var image in thumbnails.Values) image.Dispose(); thumbnails.Clear();
            readyPosters.Clear(); cachedSignatures.Clear(); recent.Clear(); recentNodes.Clear(); posterLifetime.Dispose();
        }
        base.Dispose(disposing);
    }
}

class AdjustmentSlider : Control
{
    int value, minimum, maximum = 100;
    public int Minimum { get => minimum; set { minimum = value; Value = this.value; } }
    public int Maximum { get => maximum; set { maximum = value; Value = this.value; } }
    public int Value { get => value; set { int next = Math.Clamp(value, Minimum, Maximum); if (next == this.value) return; this.value = next; Invalidate(); ValueChanged?.Invoke(this, EventArgs.Empty); } }
    public event EventHandler? ValueChanged;
    public AdjustmentSlider() { DoubleBuffered = true; Height = 36; TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.Slider; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        float x = 10 + (Width - 20) * (Value - Minimum) / (float)Math.Max(1, Maximum - Minimum), y = Height / 2f;
        using var rail = new Pen(Color.FromArgb(65, 78, 91), 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var active = new Pen(Palette.Accent, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(rail, 10, y, Math.Max(11, Width - 10), y); if (x > 10) g.DrawLine(active, 10, y, x, y);
        using var thumb = new SolidBrush(Palette.Text); g.FillEllipse(thumb, x - 7, y - 7, 14, 14);
        if (Focused) { using var ring = new Pen(Palette.Accent, 1); g.DrawEllipse(ring, x - 10, y - 10, 20, 20); }
    }
    void FromPoint(int x) => Value = Minimum + (int)Math.Round(Math.Clamp((x - 10) / (float)Math.Max(1, Width - 20), 0, 1) * (Maximum - Minimum));
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Focus(); Capture = true; FromPoint(e.X); } }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (Capture && e.Button == MouseButtons.Left) FromPoint(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); Capture = false; }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); switch (e.KeyCode) { case Keys.Left: Value--; break; case Keys.Right: Value++; break; case Keys.Home: Value = Minimum; break; case Keys.End: Value = Maximum; break; default: return; } e.Handled = true;
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
}

class PreviewFrame : Panel
{
    readonly Scene scene;
    public double AspectRatio { get; set; } = 16.0 / 9;
    public PreviewFrame(Scene value) { scene = value; Controls.Add(scene); scene.Dock = DockStyle.None; BackColor = Color.Black; }
    protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutScene(); }
    public void LayoutScene()
    {
        int width = Math.Max(1, Width - 2), height = Math.Max(1, Height - 2);
        double ratio = Math.Max(.1, AspectRatio);
        if (width / (double)height > ratio) width = Math.Max(1, (int)(height * ratio)); else height = Math.Max(1, (int)(width / ratio));
        scene.Bounds = new((Width - width) / 2, (Height - height) / 2, width, height);
        Invalidate();
    }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using var pen = new Pen(Palette.Border); e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); }
}
