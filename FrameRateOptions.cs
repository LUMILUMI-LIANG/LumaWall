namespace LumaWall;

static class FrameRateOptions
{
    public const int Maximum = 240;
    public static readonly int[] Values = [0, 15, 30, 60, 90, 120, 144, 165, 240];
    public static readonly string[] Labels = ["原帧率（最高 240 FPS）", "15 FPS", "30 FPS", "60 FPS", "90 FPS", "120 FPS", "144 FPS", "165 FPS", "240 FPS"];
    public static int Normalize(int value) => Values.Contains(value) ? value : 0;
    public static int Limit(int value) => value <= 0 ? Maximum : Math.Clamp(value, 1, Maximum);
}
