using Microsoft.Web.WebView2.Core;
using System.Text;
using System.Text.Json;

namespace LumaWall;

static class OnlineBrowserTest
{
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void WaitUntil(Func<bool> condition, int milliseconds = 20000)
    {
        var started = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - started > milliseconds) throw new TimeoutException("Browser test timed out.");
            Application.DoEvents(); Thread.Sleep(15);
        }
    }
    public static void Run()
    {
        Check(OnlineBrowserDialog.SearchAddress("鬼灭之刃").AbsoluteUri.Contains("search/view?q="), "Use actual website search.");
        var english = OnlineBrowserDialog.EnglishQueryAsync("葬送的芙莉莲", default); AudioTest.Wait(english);
        Check(english.Result == "frieren: beyond journey's end", "Convert Chinese name before real website search.");
        string folder = Path.Combine(AppContext.BaseDirectory, "browser-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); byte[] bytes = OnlineLibraryTest.ImageBytes();
        var assets = new List<OnlineAsset>(); bool committed = false, failCommit = false; string? failedPath = null;
        using var dialog = new OnlineBrowserDialog(folder, _ => { }, () => assets.ToArray(), result =>
        {
            var entry = result.Items.Single(); Check(OnlineStorage.Owns(entry.Asset, entry.Item.Path!), "Managed browser download ownership.");
            Check(File.ReadAllBytes(entry.Item.Path!).SequenceEqual(bytes), "Original browser download bytes.");
            if (failCommit) { failedPath = entry.Item.Path; throw new IOException("Simulated settings save failure"); }
            assets.Add(entry.Asset); result.Committed = true; committed = true;
        }, profile: Path.Combine(folder, "profile"), autoStart: false);
        dialog.Show(); AudioTest.Wait(dialog.InitializeAsync(false));
        var core = dialog.Browser.CoreWebView2 ?? throw new IOException(dialog.Status);
        core.AddWebResourceRequestedFilter("https://*.alphacoders.com/lumawall-fixture*", CoreWebView2WebResourceContext.All);
        core.AddWebResourceRequestedFilter("https://images3.alphacoders.com/100/987654321.png", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            bool image = e.Request.Uri.Contains("987654321.png");
            byte[] body = image ? bytes : Encoding.UTF8.GetBytes("<html><title>Browser fixture</title><body><h1>真实浏览器交互测试</h1><a id='download' href='https://images3.alphacoders.com/100/987654321.png' download>下载原图</a></body></html>");
            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(body), 200, "OK", image
                ? "Content-Type: image/png\r\nContent-Disposition: attachment; filename=987654321.png\r\nContent-Length: " + bytes.Length
                : "Content-Type: text/html; charset=utf-8");
        };
        bool loaded = false;
        core.NavigationCompleted += (_, e) => { if (core.Source.Contains("lumawall-fixture")) { Check(e.IsSuccess, "Fixture navigation."); loaded = true; } };
        dialog.Navigate("https://wall.alphacoders.com/lumawall-fixture"); WaitUntil(() => loaded);
        var content = core.ExecuteScriptAsync("document.body.innerText"); AudioTest.Wait(content);
        Check(JsonSerializer.Deserialize<string>(content.Result)!.Contains("真实浏览器交互测试"), "Rendered browser document.");
        AudioTest.Wait(core.ExecuteScriptAsync("document.getElementById('download').click()"));
        WaitUntil(() => committed);
        Check(assets.Count == 1, "Download must commit once.");
        loaded = false; dialog.Navigate("https://wall.alphacoders.com/lumawall-fixture-duplicate"); WaitUntil(() => loaded);
        AudioTest.Wait(core.ExecuteScriptAsync("document.getElementById('download').click()"));
        WaitUntil(() => dialog.Status.Contains("此原图已在壁纸库中"));
        Check(assets.Count == 1, "Duplicate download must not import again.");
        assets.Clear(); failCommit = true;
        loaded = false; dialog.Navigate("https://wall.alphacoders.com/lumawall-fixture-save-failure"); WaitUntil(() => loaded);
        AudioTest.Wait(core.ExecuteScriptAsync("document.getElementById('download').click()"));
        WaitUntil(() => dialog.Status.Contains("Simulated settings save failure"));
        Check(failedPath != null && !File.Exists(failedPath), "Save failure must roll back browser download.");
        dialog.Close();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "online-browser-test.txt"), "PASS: real WebView2 initialization/rendering, actual search URLs, Chinese-to-English query, browser attachment download, exact original bytes, managed ownership, library commit, duplicate protection and save-failure rollback. Isolated fixture: " + folder);
    }
    public static void Live()
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "browser-live-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var dialog = new OnlineBrowserDialog(folder, _ => { }, () => [], _ => throw new InvalidOperationException("Live read-only test does not import."), profile: Path.Combine(folder, "profile"), autoStart: false);
        dialog.Show(); AudioTest.Wait(dialog.InitializeAsync(false));
        var core = dialog.Browser.CoreWebView2 ?? throw new IOException(dialog.Status);
        var report = new List<string>();
        var english = OnlineBrowserDialog.EnglishQueryAsync("葬送的芙莉莲", default); AudioTest.Wait(english);
        foreach (var url in new[] { "https://alphacoders.com/", OnlineBrowserDialog.SearchAddress("Frieren").AbsoluteUri,
            OnlineBrowserDialog.SearchAddress(english.Result).AbsoluteUri })
        {
            bool loaded = false; bool success = false;
            void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e) { loaded = true; success = e.IsSuccess; }
            core.NavigationCompleted += Completed; dialog.Navigate(url); WaitUntil(() => loaded, 45000); core.NavigationCompleted -= Completed;
            Check(success, "Real navigation failed: " + dialog.Status);
            string text = "";
            WaitUntil(() =>
            {
                var textTask = core.ExecuteScriptAsync("document.body?.innerText || ''"); AudioTest.Wait(textTask);
                text = JsonSerializer.Deserialize<string>(textTask.Result) ?? "";
                return text.Length > 50;
            }, 20000);
            report.Add($"URL: {core.Source}\nTitle: {core.DocumentTitle}\nBody: {text[..Math.Min(900, text.Length)]}\n");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "online-browser-live-pages.txt"), string.Join("\n", report));
            using var screenshot = File.Create(Path.Combine(AppContext.BaseDirectory, "browser-live-" + report.Count + ".png"));
            AudioTest.Wait(core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, screenshot));
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "online-browser-live-test.txt"), string.Join("\n", report));
        dialog.Close();
    }
}
