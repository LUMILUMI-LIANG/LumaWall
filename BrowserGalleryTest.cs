using System.Reflection;
using System.Text;

namespace LumaWall;

static class BrowserGalleryTest
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void Wait(Task task)
    {
        long start = Environment.TickCount64;
        while (!task.IsCompleted) { if (Environment.TickCount64 - start > 90000) throw new TimeoutException("Gallery browser search timed out."); Application.DoEvents(); Thread.Sleep(10); }
        task.GetAwaiter().GetResult();
    }
    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "browser-gallery-fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        byte[] bytes = OnlineLibraryTest.ImageBytes(); var transport = new OnlineLibraryTest.FixtureTransport();
        transport.Bodies["/second-topic"] = Encoding.UTF8.GetBytes("<h1>Second</h1>" + OnlineLibraryTest.Card("1003"));
        foreach (string id in new[] { "1001", "1002", "1003" })
        { transport.Bodies[$"/100/{id}.png"] = bytes; transport.Bodies[$"/100/thumb-350-{id}.png"] = bytes; }
        var entries = new List<OnlineAsset>(); OnlineDownloadResult? committed = null;
        using var dialog = new OnlineWallpaperDialog(new AlphaProvider(transport), root, () => entries.ToArray(), _ => { }, result =>
        { committed = result; entries.AddRange(result.Items.Select(x => x.Asset)); result.Committed = true; });
        dialog.Show();
        var first = AlphaProvider.ParsePage("<h1>Browser result</h1>" + OnlineLibraryTest.Card("1001") + OnlineLibraryTest.Card("1002"), new("https://alphacoders.com/first-topic"), 1);
        dialog.ShowPage(first, false);
        var gallery = (FlowLayoutPanel)typeof(OnlineWallpaperDialog).GetField("gallery", Private)!.GetValue(dialog)!;
        Check(dialog.ResultCount == 2, "Extracted page must populate the original gallery.");
        foreach (Control panel in gallery.Controls) panel.Controls.OfType<CheckBox>().Single().Checked = true;
        // Browser-extracted pages keep previous selections when navigating.
        Wait(dialog.SearchTopicAsync("https://alphacoders.com/second-topic"));
        // A fresh keyword search intentionally resets selection, so load the two
        // pages through the same path used by the browser extraction action.
        dialog.ShowPage(first, false);
        foreach (Control panel in gallery.Controls) panel.Controls.OfType<CheckBox>().Single().Checked = true;
        dialog.ShowPage(AlphaProvider.ParsePage(Encoding.UTF8.GetString(transport.Bodies["/second-topic"]), new("https://alphacoders.com/second-topic"), 2), false);
        gallery.Controls[0].Controls.OfType<CheckBox>().Single().Checked = true;
        Wait((Task)typeof(OnlineWallpaperDialog).GetMethod("DownloadAsync", Private)!.Invoke(dialog, null)!);
        Check(committed?.Items.Count == 3, "Multi-page selections must download in a single existing batch.");
        Check(committed!.Items.All(x => File.ReadAllBytes(x.Item.Path!).SequenceEqual(bytes) && OnlineStorage.Owns(x.Asset, x.Item.Path!)), "Exact originals and existing managed import path.");
        Check(BrowserPageSearch.Paged(new("https://wall.alphacoders.com/by_sub_category.php?id=123&page=1"), 2).Query.Contains("id=123&page=2"), "Keep topic identifier on next page.");
        dialog.Close();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "browser-gallery-test.txt"), "PASS: browser-extracted page -> original gallery; cross-page multi-selection; three exact original downloads; managed batch import; preserved category pagination.");
    }
    public static void Live()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "browser-gallery-live-fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var dialog = new OnlineWallpaperDialog(new AlphaProvider(), root, () => [], _ => { }, _ => throw new IOException("Read-only test"),
            useBrowserSearch: true, browserProfile: Path.Combine(root, "profile"));
        dialog.Show(); var report = new List<string>();
        foreach (string query in new[] { "葬送的芙莉莲", "轻音少女", "咒术回战" })
        {
            Wait(dialog.SearchTopicAsync(query));
            var status = (Label)typeof(OnlineWallpaperDialog).GetField("state", Private)!.GetValue(dialog)!;
            Check(dialog.ResultCount > 1, query + ": " + status.Text);
            report.Add($"{query}: {dialog.ResultCount} selectable images; {status.Text}");
            var cards = (List<(OnlineImage Image, CheckBox Check, PictureBox Picture)>)typeof(OnlineWallpaperDialog).GetField("cards", Private)!.GetValue(dialog)!;
            long previewStart = Environment.TickCount64;
            while (!cards.Take(4).Any(x => x.Picture.Image != null) && Environment.TickCount64 - previewStart < 20000) { Application.DoEvents(); Thread.Sleep(30); }
            Check(cards.Any(x => x.Picture.Image != null), "Real thumbnails failed to load: " + query);
            using var screenshot = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(screenshot, new(Point.Empty, screenshot.Size));
            screenshot.Save(Path.Combine(AppContext.BaseDirectory, "browser-gallery-live-" + report.Count + ".png"));
            if (query == "葬送的芙莉莲")
            {
                string firstId = cards[0].Image.Id;
                cards[0].Check.Checked = true;
                string current = (string)typeof(OnlineWallpaperDialog).GetField("currentQuery", Private)!.GetValue(dialog)!;
                Wait((Task)typeof(OnlineWallpaperDialog).GetMethod("SearchAsync", Private)!.Invoke(dialog, [current, 2, false])!);
                Check(dialog.ResultCount > 0 && cards[0].Image.Id != firstId, "Page 2 must contain different real images.");
                var selected = (Dictionary<string, OnlineImage>)typeof(OnlineWallpaperDialog).GetField("checkedImages", Private)!.GetValue(dialog)!;
                Check(selected.ContainsKey(firstId), "Selection must survive real website pagination.");
                report.Add("Real page 2 contains different images; page 1 selection preserved.");
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "browser-gallery-live-test.txt"), string.Join("\n", report));
        }
        dialog.Close();
    }
    public static void DownloadLive()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "browser-batch-live-fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        OnlineDownloadResult? result = null;
        using var dialog = new OnlineWallpaperDialog(new AlphaProvider(), root, () => [], _ => { }, batch => { result = batch; batch.Committed = true; },
            useBrowserSearch: true, browserProfile: Path.Combine(root, "profile"));
        dialog.Show(); Wait(dialog.SearchTopicAsync("轻音少女"));
        var cards = (List<(OnlineImage Image, CheckBox Check, PictureBox Picture)>)typeof(OnlineWallpaperDialog).GetField("cards", Private)!.GetValue(dialog)!;
        Check(cards.Count > 1, "No real K-On results.");
        var selected = cards.Where(x => x.Image.Width is > 0 and <= 2000 && x.Image.Height is > 0 and <= 1500).Take(2).ToArray();
        Check(selected.Length == 2, "Need two real images for batch test.");
        foreach (var entry in selected) entry.Check.Checked = true;
        Wait((Task)typeof(OnlineWallpaperDialog).GetMethod("DownloadAsync", Private)!.Invoke(dialog, null)!);
        Check(result?.Items.Count == 2, "Real original batch failed: " + (result == null ? "no commit" : string.Join("; ", result.Errors)));
        foreach (var entry in result!.Items)
        {
            Check(OnlineStorage.Owns(entry.Asset, entry.Item.Path!), "Managed real original."); MediaImport.ValidateImage(entry.Item.Path!);
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "browser-gallery-download-live-test.txt"), "PASS: 中文“轻音少女” -> real browser website results -> select two -> original direct batch download -> isolated managed import, without website countdown modal.\n" + string.Join("\n", result.Items.Select(x => x.Asset.OriginalUrl + " => " + x.Item.Path)));
        dialog.Close();
    }
}
