using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LumaWall;

// Proper names need their published English names rather than literal translation.
static class ChineseTopics
{
    static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["原神"] = "genshin-impact", ["崩坏三"] = "honkai-impact-3rd", ["崩坏3"] = "honkai-impact-3rd",
        ["星穹铁道"] = "honkai-star-rail", ["星铁"] = "honkai-star-rail", ["崩坏星穹铁道"] = "honkai-star-rail",
        ["绝区零"] = "zenless-zone-zero", ["鸣潮"] = "wuthering-waves", ["明日方舟"] = "arknights",
        ["碧蓝档案"] = "blue-archive", ["蔚蓝档案"] = "blue-archive", ["碧蓝航线"] = "azur-lane",
        ["少女前线"] = "girls-frontline", ["战双帕弥什"] = "punishing-gray-raven",
        ["初音未来"] = "hatsune-miku", ["初音"] = "hatsune-miku", ["洛天依"] = "luo-tianyi",
        ["申鹤"] = "shenhe-(genshin-impact)", ["甘雨"] = "ganyu-(genshin-impact)",
        ["雷电将军"] = "raiden-shogun", ["芙宁娜"] = "furina-(genshin-impact)",
        ["胡桃"] = "hu-tao", ["刻晴"] = "keqing-(genshin-impact)", ["纳西妲"] = "nahida-(genshin-impact)",
        ["钟离"] = "zhongli-(genshin-impact)", ["八重神子"] = "yae-miko", ["优菈"] = "eula-(genshin-impact)",
        ["若娜瓦"] = "ronova-(genshin-impact)", ["若陀"] = "azhdaha-(genshin-impact)",
        ["流萤"] = "firefly-(honkai:-star-rail)", ["黄泉"] = "acheron-(honkai:-star-rail)",
        ["镜流"] = "jingliu-(honkai:-star-rail)", ["卡芙卡"] = "kafka-(honkai:-star-rail)",
        ["三月七"] = "march-7th", ["爱莉希雅"] = "elysia-(honkai-impact-3rd)",
        ["桔梗"] = "kikyo-(inuyasha)", ["犬夜叉"] = "inuyasha", ["葬送的芙莉莲"] = "frieren:-beyond-journey's-end",
        ["鬼灭之刃"] = "demon-slayer:-kimetsu-no-yaiba", ["火影忍者"] = "naruto", ["海贼王"] = "one-piece",
        ["进击的巨人"] = "attack-on-titan", ["咒术回战"] = "jujutsu-kaisen", ["间谍过家家"] = "spy-x-family",
        ["新世纪福音战士"] = "neon-genesis-evangelion", ["刀剑神域"] = "sword-art-online",
        ["动漫"] = "anime", ["风景"] = "landscape", ["自然"] = "nature", ["星空"] = "starry-sky",
        ["太空"] = "space", ["猫"] = "cat", ["猫咪"] = "cat", ["樱花"] = "cherry-blossom",
        ["赛博朋克"] = "cyberpunk", ["城市"] = "city", ["海洋"] = "ocean", ["森林"] = "forest"
    };
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    internal static bool ContainsChinese(string text) => text.Any(c => c is >= '\u3400' and <= '\u9fff');
    internal static string Clean(string text)
    {
        text = text.Normalize(NormalizationForm.FormKC).Trim();
        foreach (var pair in new[] { ('壞', '坏'), ('鐵', '铁'), ('絕', '绝'), ('區', '区'), ('鳴', '鸣'), ('藍', '蓝'), ('檔', '档'), ('鶴', '鹤'), ('電', '电'), ('將', '将'), ('軍', '军'), ('螢', '萤'), ('鏡', '镜'), ('櫻', '樱'), ('貓', '猫') })
            text = text.Replace(pair.Item1, pair.Item2);
        return Regex.Replace(text, @"(?:高清|超清|4K|4k|8K|8k|桌面|电脑|動態|动态)*壁[纸紙]$", "", RegexOptions.None, Timeout).Trim();
    }
    internal static string? Find(string text)
    {
        text = Clean(text);
        string key = Regex.Replace(text, @"[\s_:：·-]+", "", RegexOptions.None, Timeout);
        if (Names.TryGetValue(key, out var value)) return value;
        // Strip a known franchise only when the remainder is an exact known name.
        foreach (string series in new[] { "崩坏星穹铁道", "星穹铁道", "崩坏三", "原神", "星铁", "犬夜叉" })
            if (key.StartsWith(series, StringComparison.Ordinal) && Names.TryGetValue(key[series.Length..], out value)) return value;
        return null;
    }

    static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(12) };
    internal static async Task<string?> LookupEnglishAsync(string text, CancellationToken token)
    {
        // Official MediaWiki language links resolve names; no translation account/API key is needed.
        string url = "https://zh.wikipedia.org/w/api.php?action=query&prop=langlinks&lllang=en&redirects=1&format=json&formatversion=2&titles=" + Uri.EscapeDataString(Clean(text));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("LumaWall/0.15 (desktop-wallpaper-topic-lookup)");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] data = await AlphaProvider.ReadBoundedAsync(stream, 256 * 1024, token).ConfigureAwait(false);
        using var json = JsonDocument.Parse(data);
        return EnglishTitle(json.RootElement);
    }
    internal static string? EnglishTitle(JsonElement root)
    {
        if (!root.TryGetProperty("query", out var query) || !query.TryGetProperty("pages", out var pages)) return null;
        foreach (var page in pages.EnumerateArray())
            if (!page.TryGetProperty("missing", out _) && page.TryGetProperty("langlinks", out var links))
                foreach (var link in links.EnumerateArray())
                    if (link.GetProperty("lang").GetString() == "en") return link.GetProperty("title").GetString();
        return null;
    }
}
