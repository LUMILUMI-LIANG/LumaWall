namespace LumaWall;

// Native Button paints disabled text with the system color, which becomes black
// under the legacy dark theme. Keep native keyboard/click behavior and own paint.
sealed class DarkButton : Button
{
    bool hovered, pressed;
    public DarkButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); pressed = e.Button == MouseButtons.Left; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); pressed = false; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        Color background = !Enabled ? Palette.Surface : pressed ? Color.FromArgb(43, 65, 77)
            : hovered && BackColor == Palette.Field ? Color.FromArgb(47, 67, 79) : BackColor;
        e.Graphics.Clear(background);
        using var border = new Pen(Focused && Enabled ? Palette.Accent : Palette.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        var bounds = Rectangle.Inflate(ClientRectangle, -6, -3);
        TextFormatFlags flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak;
        if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
        if (!ShowKeyboardCues) flags |= TextFormatFlags.HidePrefix;
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds, Enabled ? ForeColor : Color.FromArgb(157, 174, 194), flags);
        if (Focused && Enabled && ShowFocusCues) { var focus = Rectangle.Inflate(ClientRectangle, -4, -4); ControlPaint.DrawFocusRectangle(e.Graphics, focus, Palette.Accent, background); }
    }
}
