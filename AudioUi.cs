namespace LumaWall;

partial class MainWindow
{
    readonly AdjustmentSlider volumeSlider = new();
    readonly NumericUpDown volumeValue = new();
    readonly CheckBox muteAudio = new();

    Control CreateAudioControls()
    {
        var area = new BorderedPanel { BackColor = Palette.Field, Padding = new(12, 2, 12, 2) };
        var rows = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        rows.ColumnStyles.Add(new(SizeType.Percent, 100));
        rows.RowStyles.Add(new(SizeType.Percent, 100)); rows.RowStyles.Add(new(SizeType.Absolute, 32));
        var volumeRow = AdjustmentRow("原声音量", volumeSlider, volumeValue, 0, 100, settings.Volume);
        volumeValue.DecimalPlaces = 0;
        rows.Controls.Add(volumeRow, 0, 0);
        muteAudio.Text = "静音"; muteAudio.AutoSize = false; muteAudio.Checked = settings.Muted;
        muteAudio.AccessibleName = "桌面视频静音"; muteAudio.Dock = DockStyle.Fill;
        tooltips.SetToolTip(muteAudio, "视频应用到桌面后播放原声；预览始终静音。");
        muteAudio.Margin = new(6, 0, 0, 0);
        rows.Controls.Add(muteAudio, 0, 1); area.Controls.Add(rows);
        volumeSlider.ValueChanged += (_, _) => volumeValue.Value = volumeSlider.Value;
        volumeValue.ValueChanged += (_, _) =>
        {
            if (updatingControls) return;
            volumeSlider.Value = (int)volumeValue.Value;
            settings.Volume = (int)volumeValue.Value; UpdateAudioSettings();
        };
        muteAudio.CheckedChanged += (_, _) => { if (updatingControls) return; settings.Muted = muteAudio.Checked; UpdateAudioSettings(); };
        return area;
    }
    void UpdateAudioSettings()
    {
        float volume = settings.Muted ? 0 : settings.Volume / 100f;
        if (desktopScene != null) desktopScene.Volume = volume;
        if (pendingScene != null) pendingScene.Volume = volume;
        try { Save(); }
        catch (Exception ex) { status.Text = "声音设置未保存：" + ex.Message; }
    }
}
