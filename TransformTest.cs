using System.Text.Json;

namespace LumaWall;

static class TransformTest
{
    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static void Near(float actual, float expected, string message) =>
        Assert(Math.Abs(actual - expected) < .0001f, $"{message}: expected {expected}, received {actual}");

    static Bitmap Render(Scene scene)
    {
        var bitmap = new Bitmap(scene.Width, scene.Height);
        scene.DrawToBitmap(bitmap, new(Point.Empty, bitmap.Size));
        return bitmap;
    }

    static void ColorAt(Bitmap bitmap, int x, int y, Color expected, string message)
    {
        var actual = bitmap.GetPixel(x, y);
        Assert(Math.Abs(actual.R - expected.R) <= 2 && Math.Abs(actual.G - expected.G) <= 2 && Math.Abs(actual.B - expected.B) <= 2,
            $"{message}: ({x},{y}) expected {expected}, received {actual}");
    }

    public static void Run()
    {
        var extreme = new WallpaperTransform(9, -5, 8).Normalize();
        Assert(extreme == new WallpaperTransform(3, -1, 1), "Transform limits were not enforced");
        Assert(new WallpaperTransform(float.NaN, float.PositiveInfinity, float.NegativeInfinity).Normalize() == new WallpaperTransform(), "Invalid saved coordinates must fall back to defaults");
        Assert(new WallpaperTransform(.01f).Normalize().Scale == .25f, "Minimum zoom was not enforced");

        // The same image point must stay under the pointer when zooming in.
        var original = new WallpaperTransform(1.25f, .08f, -.06f);
        var anchor = new PointF(.2f, -.1f);
        var zoomed = original.ZoomAt(2f, anchor);
        Near((anchor.X - original.OffsetX) / original.Scale, (anchor.X - zoomed.OffsetX) / zoomed.Scale, "Horizontal zoom anchor drifted");
        Near((anchor.Y - original.OffsetY) / original.Scale, (anchor.Y - zoomed.OffsetY) / zoomed.Scale, "Vertical zoom anchor drifted");

        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "transform-test-library");
        Directory.CreateDirectory(folder);
        string imagePath = System.IO.Path.Combine(folder, "wide-color-bands.png");
        using (var source = new Bitmap(320, 90))
        {
            using var graphics = Graphics.FromImage(source);
            graphics.FillRectangle(Brushes.Green, 0, 0, 80, 90);
            graphics.FillRectangle(Brushes.Red, 80, 0, 80, 90);
            graphics.FillRectangle(Brushes.Blue, 160, 0, 80, 90);
            graphics.FillRectangle(Brushes.Yellow, 240, 0, 80, 90);
            source.Save(imagePath);
        }
        var wallpaper = new Wallpaper("Test image", "image", imagePath);
        using var preview = new EditableScene { Size = new(320, 180), Paused = true, EditingEnabled = true };
        preview.LoadScene(wallpaper);
        using (var bitmap = Render(preview))
        {
            ColorAt(bitmap, 20, 90, Color.Red, "Default fill must crop the source's outer green band");
            ColorAt(bitmap, 300, 90, Color.Blue, "Default fill must crop the source's outer yellow band");
        }

        var positioned = new WallpaperTransform(.5f, .25f, 0);
        preview.SetTransform(positioned);
        using (var bitmap = Render(preview))
        {
            ColorAt(bitmap, 80, 90, Color.Black, "Moving right must expose black on the left");
            ColorAt(bitmap, 180, 90, Color.Red, "Image position and scale were not applied");
            ColorAt(bitmap, 280, 90, Color.Blue, "The moved image's right side was clipped unexpectedly");
            ColorAt(bitmap, 200, 20, Color.Black, "Zooming out must expose black above the canvas");
        }
        preview.SetTransform(new(.5f, 0, .25f));
        using (var bitmap = Render(preview))
        {
            ColorAt(bitmap, 100, 120, Color.Red, "Vertical movement was not applied");
            ColorAt(bitmap, 260, 120, Color.Black, "Aspect-fill overflow must remain clipped to the transformed canvas");
            ColorAt(bitmap, 160, 30, Color.Black, "Moving down must expose black above the canvas");
        }

        // Normalized offsets keep preview and desktop composition aligned at different resolutions.
        using var desktop = new Scene { Size = new(1280, 720), Paused = true };
        desktop.LoadScene(wallpaper); desktop.SetTransform(positioned);
        preview.SetTransform(positioned);
        using (var small = Render(preview))
        using (var large = Render(desktop))
            foreach (var point in new[] { new Point(80, 90), new Point(180, 90), new Point(280, 90), new Point(200, 20) })
                ColorAt(large, point.X * 4, point.Y * 4, small.GetPixel(point.X, point.Y), "Preview and desktop transforms differ");

        foreach (var kind in new[] { "aurora", "ocean", "space" })
        {
            preview.LoadScene(new(kind, kind)); preview.SetTransform(new(.5f, .25f, 0));
            using var bitmap = Render(preview);
            ColorAt(bitmap, 80, 90, Color.Black, $"{kind}: generated scenes must use the same canvas transformation");
            Assert(bitmap.GetPixel(200, 90).ToArgb() != Color.Black.ToArgb(), $"{kind}: transformed content was not rendered");
        }

        int changes = 0;
        preview.TransformChanged += _ => changes++;
        preview.SetTransform(new());
        Assert(changes == 0, "Programmatic transforms should not raise editing events");
        preview.Drag(new(100, 60), new(132, 78));
        Near(preview.Transform.OffsetX, .1f, "Preview drag horizontal displacement");
        Near(preview.Transform.OffsetY, .1f, "Preview drag vertical displacement");
        Assert(changes == 1, "Dragging must raise one transform change event");
        preview.Wheel(new(160, 90), 120);
        Near(preview.Transform.Scale, 1.1f, "Mouse wheel zoom did not change scale");
        Assert(changes == 2, "Wheel zoom must notify the editor");
        preview.EditingEnabled = false;
        var beforeDisabledInput = preview.Transform;
        preview.Drag(new(100, 60), new(132, 78)); preview.Wheel(new(160, 90), 120);
        Assert(preview.Transform == beforeDisabledInput && changes == 2, "Desktop scenes must ignore editing input");

        var saved = new Settings(); saved.Transforms[wallpaper.Key] = positioned;
        saved.Transforms[new Wallpaper("Aurora", "aurora").Key] = new(1.5f, -.15f, .05f);
        var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(saved))!;
        Assert(restored.Transforms[wallpaper.Key] == positioned, "Imported wallpaper position must survive settings round-trip");
        Assert(restored.Transforms["builtin:aurora"].Scale == 1.5f, "Built-in wallpaper transform must survive settings round-trip");
        File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "transform-test.txt"),
            "PASS: limits and invalid values; pointer-anchored zoom; image fill/crop, scale, horizontal/vertical movement and canvas clipping; resolution-independent preview/desktop composition; all three built-in scenes; mouse editing events; disabled desktop input; per-wallpaper settings round-trip.");
    }

    sealed class EditableScene : Scene
    {
        public void Drag(Point from, Point to)
        {
            OnMouseDown(new(MouseButtons.Left, 1, from.X, from.Y, 0));
            OnMouseMove(new(MouseButtons.Left, 0, to.X, to.Y, 0));
            OnMouseUp(new(MouseButtons.Left, 1, to.X, to.Y, 0));
        }
        public void Wheel(Point at, int delta) => OnMouseWheel(new(MouseButtons.None, 0, at.X, at.Y, delta));
    }
}
