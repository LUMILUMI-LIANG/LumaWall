namespace LumaWall;

static class AppIcon
{
    static readonly Lazy<Icon> icon = new(() =>
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("LumaWall.AppIcon.ico")
            ?? throw new InvalidOperationException("程序图标资源缺失。");
        using var loaded = new Icon(stream);
        return (Icon)loaded.Clone();
    });
    public static Icon Value => icon.Value;
}
