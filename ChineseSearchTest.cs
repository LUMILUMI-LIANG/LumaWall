using System.Text;
using System.Text.Json;

namespace LumaWall;

static class ChineseSearchTest
{
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static void Run()
    {
        foreach (string query in new[] { "申鹤", "申鹤壁纸", "原神 申鹤高清壁纸", "原神：申鹤" })
            Check(Uri.UnescapeDataString(AlphaProvider.TopicAddress(query, 2).AbsolutePath) == "/shenhe-(genshin-impact)-wallpapers", query);
        Check(AlphaProvider.TopicAddress("原神雷电将军").AbsolutePath == "/raiden-shogun-wallpapers", "compound character");
        Check(AlphaProvider.TopicAddress("原神 申鶴壁紙").AbsolutePath == AlphaProvider.TopicAddress("申鹤").AbsolutePath, "traditional character");
        Check(AlphaProvider.TopicAddress("绝区零壁纸").AbsolutePath == "/zenless-zone-zero-wallpapers", "new franchise");
        Check(AlphaProvider.TopicAddress("崩壞：星穹鐵道").AbsolutePath == "/honkai-star-rail-wallpapers", "traditional alias");
        Check(AlphaProvider.TopicAddress("原神壁纸").AbsolutePath == "/genshin-impact-wallpapers", "franchise suffix");
        using var json = JsonDocument.Parse("""{"query":{"pages":[{"langlinks":[{"lang":"en","title":"Test Topic"}]}]}}""");
        Check(ChineseTopics.EnglishTitle(json.RootElement) == "Test Topic", "language links");
        using var missing = JsonDocument.Parse("""{"query":{"pages":[{"missing":true}]}}""");
        Check(ChineseTopics.EnglishTitle(missing.RootElement) == null, "missing title");
        var fixture = new OnlineLibraryTest.FixtureTransport();
        fixture.Bodies["/test-topic-wallpapers"] = Encoding.UTF8.GetBytes("<h1>Test Topic</h1>" + OnlineLibraryTest.Card("1001"));
        int lookups = 0;
        var provider = new AlphaProvider(fixture, (query, token) => { token.ThrowIfCancellationRequested(); lookups++; return Task.FromResult<string?>("Test Topic"); });
        var task = provider.SearchAsync("测试未收录名称", 2, default); AudioTest.Wait(task);
        var page = task.GetAwaiter().GetResult();
        Check(page.Images.Count == 1 && page.Number == 2 && page.Address.Query.Contains("page=2") && lookups == 1, "lookup to parsed gallery");
        var noMatch = new AlphaProvider(fixture, (_, _) => Task.FromResult<string?>(null));
        bool rejected = false;
        try { noMatch.SearchAsync("不存在的名称", 1, default).GetAwaiter().GetResult(); } catch (ArgumentException) { rejected = true; }
        Check(rejected, "unknown names must not silently return a different topic");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        bool stopped = false;
        try { provider.SearchAsync("未收录名称", 1, cancelled.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { stopped = true; }
        Check(stopped, "cancelled lookup");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "chinese-search-test.txt"), "PASS: Chinese names, compound names, wallpaper suffixes, legacy aliases, language-link parsing, gallery results/pagination, missing names and cancellation.");
    }
    public static void Live()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(80));
        var task = new AlphaProvider().SearchAsync("原神 申鹤壁纸", 1, deadline.Token);
        AudioTest.Wait(task); var page = task.GetAwaiter().GetResult();
        Check(page.Images.Count > 0, "Real Chinese character search returned no images.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "chinese-search-live-test.txt"), $"PASS: 原神 申鹤壁纸 => {page.Address}; images={page.Images.Count}");
    }
    public static void NameLive()
    {
        var task = ChineseTopics.LookupEnglishAsync("塞尔达传说", default); AudioTest.Wait(task);
        string? name = task.GetAwaiter().GetResult();
        Check(name == "The Legend of Zelda", "Unexpected English topic: " + name);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "chinese-name-live-test.txt"), "PASS: 塞尔达传说 => " + name);
    }
}
