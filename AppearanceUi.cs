using System.Runtime.CompilerServices;

namespace LumaWall;

static class UiTypography
{
    internal const string FixedPreview = "LumaWall.FontPreview";
    sealed record FontSpec(string Family, float Size, FontStyle Style, GraphicsUnit Unit);
    sealed class State
    {
        public int Percent = 100;
        public Dictionary<Control, FontSpec> Fonts = [];
        public List<Font> Owned = [];
    }
    static readonly ConditionalWeakTable<Form, State> states = new();
    internal static void Apply(Form form, int percent, bool keepWindowSize = true)
    {
        percent = Math.Clamp(percent, 90, 150);
        var state = states.GetValue(form, _ =>
        {
            var value = new State();
            form.Disposed += (_, _) => { foreach (var font in value.Owned) font.Dispose(); value.Owned.Clear(); value.Fonts.Clear(); };
            return value;
        });
        if (percent == state.Percent && state.Fonts.Count > 0) return;
        foreach (Control control in Descendants(form))
        {
            if (!Equals(control.Tag, FixedPreview) && !state.Fonts.ContainsKey(control)) state.Fonts[control] = new(control.Font.FontFamily.Name, control.Font.Size / (state.Percent / 100f), control.Font.Style, control.Font.Unit);
            if (control is TableLayoutPanel { ColumnCount: 1 } table && (table.ColumnStyles.Count == 0 || table.ColumnStyles[0].SizeType == SizeType.AutoSize))
            { table.ColumnStyles.Clear(); table.ColumnStyles.Add(new(SizeType.Percent, 100)); }
            if (control is TableLayoutPanel rows && rows.RowCount <= 1 && rows.RowStyles.Count == 0 && !rows.AutoSize)
                rows.RowStyles.Add(new(SizeType.Percent, 100));
        }
        float ratio = percent / (float)state.Percent;
        Size size = form.Size, minimum = form.MinimumSize; var working = Screen.FromControl(form).WorkingArea;
        form.SuspendLayout();
        try
        {
            foreach (Control child in form.Controls) child.Scale(new SizeF(ratio, ratio));
            var old = state.Owned; state.Owned = [];
            foreach (var (control, spec) in state.Fonts)
            {
                if (control.IsDisposed) continue;
                var font = new Font(spec.Family, spec.Size * percent / 100f, spec.Style, spec.Unit);
                state.Owned.Add(font); control.Font = font;
                if (control is ComboBox combo) combo.ItemHeight = Math.Max(24, (int)Math.Ceiling(combo.Font.Height + 6 * percent / 100f));
                if (control is ListBox list) list.ItemHeight = Math.Max(18, list.Font.Height + 4);
            }
            foreach (var font in old) font.Dispose();
            form.MinimumSize = new(Math.Min(working.Width - 48, (int)Math.Round(minimum.Width * ratio)), Math.Min(working.Height - 48, (int)Math.Round(minimum.Height * ratio)));
            form.Size = keepWindowSize ? size : new(Math.Min(working.Width - 48, (int)Math.Round(size.Width * ratio)), Math.Min(working.Height - 48, (int)Math.Round(size.Height * ratio)));
            foreach (Control child in form.Controls)
                if (child.Dock == DockStyle.Fill) child.Bounds = form.DisplayRectangle;
            state.Percent = percent;
        }
        finally { form.ResumeLayout(true); form.PerformLayout(); form.Invalidate(true); }
    }
    static IEnumerable<Control> Descendants(Control root)
    { yield return root; foreach (Control child in root.Controls) foreach (var control in Descendants(child)) yield return control; }
}

partial class MainWindow
{
    internal void PrepareDialog(Form dialog) { UiTypography.Apply(dialog, settings.TextScale, false); DarkTheme.Attach(dialog); }
    internal void ApplyMainTextScale(int percent) { UiTypography.Apply(this, percent); UpdateInspectorWidth(); UpdatePlaylistSidebar(); }
    internal void ChangeTextScale(int percent)
    {
        int previous = settings.TextScale; settings.TextScale = Math.Clamp(percent, 90, 150);
        try { Save(); } catch { settings.TextScale = previous; throw; }
        ApplyMainTextScale(settings.TextScale); status.Text = $"文字大小已调整为 {settings.TextScale}%";
    }
    void ShowAppearanceSettings()
    {
        using var dialog = new AppearanceDialog(settings.TextScale); PrepareDialog(dialog);
        if (dialog.ShowDialog(this) == DialogResult.OK) ChangeTextScale(dialog.Percent);
    }
}

sealed class AppearanceDialog : Form
{
    readonly NumericUpDown percent = new() { Minimum = 90, Maximum = 150, Increment = 5, Width = 100 };
    readonly Label sample = new() { Text = "壁纸名称 · 原神 / 星穹铁道\n缩略图与按钮文字预览", Tag = UiTypography.FixedPreview, AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
    public int Percent => (int)percent.Value;
    public AppearanceDialog(int value)
    {
        Text = "界面 · 文字大小"; Font = new("Microsoft YaHei UI", 10); BackColor = Palette.Surface; ForeColor = Palette.Text;
        Size = new(610, 380); MinimumSize = Size; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), ColumnCount = 1, RowCount = 5 };
        foreach (int height in new[] { 38, 44, 90, 70, 44 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.Controls.Add(new Label { Text = "文字大小（90%～150%）", Dock = DockStyle.Fill }, 0, 0);
        var choices = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        percent.Value = Math.Clamp(value, 90, 150); choices.Controls.Add(percent);
        foreach (int preset in new[] { 100, 125, 150 })
        { var button = OnlineWallpaperDialog.ActionButton(preset + "%"); button.Width = 90; button.Click += (_, _) => percent.Value = preset; choices.Controls.Add(button); }
        layout.Controls.Add(choices, 0, 1); layout.Controls.Add(sample, 0, 2);
        layout.Controls.Add(new Label { Text = "调整主界面和应用内弹窗的文字及控件间距。\n保存后立即生效；窗口尺寸和缩略图大小可以独立调整。", Dock = DockStyle.Fill, ForeColor = Palette.Muted }, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var save = EngineSettingsDialog.DialogButton("保存", DialogResult.OK, true); var cancel = EngineSettingsDialog.DialogButton("取消", DialogResult.Cancel, false);
        actions.Controls.AddRange([save, cancel]); layout.Controls.Add(actions, 0, 4); Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
        void UpdateSample() { var old = sample.Font; sample.Font = new(Font.FontFamily, 10 * Percent / 100f); if (!ReferenceEquals(old, Font)) old.Dispose(); }
        percent.ValueChanged += (_, _) => UpdateSample(); UpdateSample();
    }
    protected override void Dispose(bool disposing) { if (disposing) sample.Font.Dispose(); base.Dispose(disposing); }
}
