using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace LumaWall;

sealed record OnlineImage(string Id, string Name, Uri Original, Uri Thumbnail, Uri SourcePage, int Width, int Height);
sealed record OnlinePage(Uri Address, string Title, IReadOnlyList<OnlineImage> Images, int Number);

sealed class OnlineResponse(Stream body, string contentType = "", IDisposable? owner = null) : IDisposable
{
    public Stream Body { get; } = body;
    public string ContentType { get; } = contentType;
    public void Dispose() { Body.Dispose(); owner?.Dispose(); }
}
interface IOnlineTransport
{
    Task<OnlineResponse> OpenAsync(Uri address, CancellationToken token);
}

// Restrict the provider to public Alpha Coders pages and its image servers.
// Redirects are inspected before sending another request, including robots rules.
sealed class AlphaTransport : IOnlineTransport
{
    static readonly HttpClient client = new(new HttpClientHandler
    {
        AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, MaxConnectionsPerServer = 2,
        UseCookies = false
    }) { Timeout = TimeSpan.FromSeconds(35) };
    static readonly SemaphoreSlim requestGate = new(1);
    static readonly SemaphoreSlim robotsGate = new(1);
    static readonly Dictionary<string, (DateTimeOffset Time, string Text)> robots = new(StringComparer.OrdinalIgnoreCase);
    static long lastRequest;

    public async Task<OnlineResponse> OpenAsync(Uri address, CancellationToken token)
    {
        for (int redirect = 0; redirect < 6; redirect++)
        {
            AlphaProvider.ValidateAddress(address);
            await CheckRobotsAsync(address, token).ConfigureAwait(false);
            var response = await SendAsync(address, token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                address = new Uri(address, location); response.Dispose(); continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode; response.Dispose();
                throw new HttpRequestException(code is 401 or 403 or 429
                    ? $"网站暂不接受此请求（{code}），请稍后重试或在浏览器打开网页。"
                    : $"网页或图片请求失败（{code}）。", null, (HttpStatusCode)code);
            }
            return new(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false),
                response.Content.Headers.ContentType?.MediaType ?? "", response);
        }
        throw new IOException("网页跳转次数过多。");
    }

    static async Task<HttpResponseMessage> SendAsync(Uri address, CancellationToken token)
    {
        await requestGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long now = Environment.TickCount64;
            int delay = (int)Math.Clamp(500 - (now - lastRequest), 0, 500);
            if (delay > 0) await Task.Delay(delay, token).ConfigureAwait(false);
            lastRequest = Environment.TickCount64;
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("LumaWall/0.13 (+local-wallpaper-library)");
            request.Headers.Accept.ParseAdd("text/html,image/png,image/jpeg,image/gif,*/*;q=0.5");
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        }
        finally { requestGate.Release(); }
    }

    static async Task CheckRobotsAsync(Uri address, CancellationToken token)
    {
        await robotsGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!robots.TryGetValue(address.Host, out var rules) || DateTimeOffset.UtcNow - rules.Time > TimeSpan.FromMinutes(15))
            {
                using var response = await SendAsync(new Uri(address.GetLeftPart(UriPartial.Authority) + "/robots.txt"), token).ConfigureAwait(false);
                string text;
                if (response.StatusCode == HttpStatusCode.NotFound) text = "";
                else
                {
                    if (!response.IsSuccessStatusCode) throw new IOException("暂时无法读取网站的访问规则，请稍后重试。");
                    using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    text = Encoding.UTF8.GetString(await AlphaProvider.ReadBoundedAsync(body, 65536, token).ConfigureAwait(false));
                }
                robots[address.Host] = rules = (DateTimeOffset.UtcNow, text);
            }
            if (!RobotsAllows(rules.Text, Uri.UnescapeDataString(address.PathAndQuery)))
                throw new IOException("此页面不允许自动读取。请在网站搜索后，粘贴公开主题页网址。");
        }
        finally { robotsGate.Release(); }
    }

    internal static bool RobotsAllows(string text, string path)
    {
        bool applies = false, groupHasRules = false; int allowed = -1, denied = -1;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Split('#')[0].Trim(); int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
            if (key.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
            {
                if (groupHasRules) { applies = false; groupHasRules = false; }
                applies |= value == "*" || value.Equals("LumaWall", StringComparison.OrdinalIgnoreCase); continue;
            }
            groupHasRules = true;
            if (!applies || value.Length == 0) continue;
            if (!key.Equals("Allow", StringComparison.OrdinalIgnoreCase) && !key.Equals("Disallow", StringComparison.OrdinalIgnoreCase)) continue;
            string pattern = "^" + Regex.Escape(value).Replace(@"\*", ".*").Replace(@"\$", "$");
            if (!Regex.IsMatch(path, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))) continue;
            if (key.Equals("Allow", StringComparison.OrdinalIgnoreCase)) allowed = Math.Max(allowed, value.Length);
            else denied = Math.Max(denied, value.Length);
        }
        return denied < 0 || allowed >= denied;
    }
}

sealed class AlphaProvider(IOnlineTransport? transport = null, Func<string, CancellationToken, Task<string?>>? englishLookup = null)
{
    readonly IOnlineTransport transport = transport ?? new AlphaTransport();
    static readonly TimeSpan regexTimeout = TimeSpan.FromSeconds(2);
    internal static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
    static readonly Dictionary<string, string> aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["原神"] = "genshin-impact", ["genshin"] = "genshin-impact", ["genshinimpact"] = "genshin-impact",
        ["崩坏三"] = "honkai-impact-3rd", ["崩壞三"] = "honkai-impact-3rd", ["崩坏3"] = "honkai-impact-3rd", ["崩壞3"] = "honkai-impact-3rd",
        ["崩坏3rd"] = "honkai-impact-3rd", ["honkaiimpact3"] = "honkai-impact-3rd", ["honkaiimpact3rd"] = "honkai-impact-3rd", ["honkai3rd"] = "honkai-impact-3rd",
        ["崩坏星穹铁道"] = "honkai-star-rail", ["崩壞星穹鐵道"] = "honkai-star-rail", ["星穹铁道"] = "honkai-star-rail", ["星穹鐵道"] = "honkai-star-rail",
        ["星铁"] = "honkai-star-rail", ["星鐵"] = "honkai-star-rail", ["honkaistarrail"] = "honkai-star-rail", ["动漫"] = "anime"
    };

    internal static void ValidateAddress(Uri address)
    {
        bool page = address.Host.Equals("alphacoders.com", StringComparison.OrdinalIgnoreCase)
            || address.Host.Equals("wall.alphacoders.com", StringComparison.OrdinalIgnoreCase);
        bool image = Regex.IsMatch(address.Host, @"^images\d*\.alphacoders\.com$", RegexOptions.IgnoreCase, regexTimeout);
        if (address.Scheme != Uri.UriSchemeHttps || !address.IsDefaultPort || address.UserInfo.Length != 0 || (!page && !image))
            throw new ArgumentException("仅支持 Alpha Coders 的公开网页和图片地址。");
    }

    internal static Uri TopicAddress(string query, int page = 1)
    {
        query = query.Normalize(NormalizationForm.FormKC).Trim();
        if (query.Length == 0 || query.Length > 200) throw new ArgumentException("请输入关键词或主题页网址，最多 200 个字符。");
        Uri address;
        if (query.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || query.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            address = new(query); ValidateAddress(address);
            if (address.Host != "alphacoders.com" && address.Host != "wall.alphacoders.com") throw new ArgumentException("请粘贴主题页网址。");
            if (Uri.UnescapeDataString(address.AbsolutePath).StartsWith("/search", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("请粘贴搜索后跳转的主题页网址，或直接输入英文关键词。");
            string preserved = string.Join("&", address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(x => Uri.UnescapeDataString(x.Split('=')[0]) is var key && !key.Equals("page", StringComparison.OrdinalIgnoreCase) && !key.Equals("view", StringComparison.OrdinalIgnoreCase)));
            address = new UriBuilder(address) { Query = preserved, Fragment = "" }.Uri;
        }
        else
        {
            string alias = Regex.Replace(query, @"[\s_:：-]+", "", RegexOptions.None, regexTimeout);
            string slug = ChineseTopics.Find(query) ?? aliases.GetValueOrDefault(alias) ?? query.ToLowerInvariant();
            if (slug.Any(c => c is >= '\u3400' and <= '\u9fff'))
                throw new ArgumentException("暂未收录这个中文名称。请使用英文主题名或粘贴网页链接；支持原神、崩坏三、星穹铁道。可点击“网站中搜索”查找。");
            slug = Regex.Replace(slug.Replace("'", "").Replace("’", ""), @"[\s_:]+", "-", RegexOptions.None, regexTimeout).Trim('-');
            slug = Regex.Replace(slug, "-+", "-", RegexOptions.None, regexTimeout);
            if (slug.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '(' or ')'))) throw new ArgumentException("关键词请使用名称、空格或连字符，例如 genshin-impact。");
            if (slug.Length == 0) throw new ArgumentException("请输入有效的主题名称。");
            if (!slug.EndsWith("-wallpapers", StringComparison.Ordinal)) slug += "-wallpapers";
            address = new("https://alphacoders.com/" + Uri.EscapeDataString(slug));
        }
        string prefix = address.Query.Length > 1 ? address.Query.TrimStart('?') + "&" : "";
        return new UriBuilder(address) { Query = prefix + $"page={Math.Clamp(page, 1, 200)}&view=pagination" }.Uri;
    }

    public async Task<OnlinePage> SearchAsync(string query, int page, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(60));
        string resolved = query;
        if (!Uri.TryCreate(query.Trim(), UriKind.Absolute, out _) && ChineseTopics.ContainsChinese(query)
            && ChineseTopics.Find(query) == null)
        {
            // Preserve existing traditional aliases before attempting network lookup.
            try { TopicAddress(query, page); }
            catch (ArgumentException)
            {
                if (query.Length > 200) throw;
                try { resolved = await (englishLookup ?? ChineseTopics.LookupEnglishAsync)(query, deadline.Token).ConfigureAwait(false)
                    ?? throw new ArgumentException("没有找到这个中文名称的英文对应项，请尝试作品全名、英文名称或主题页链接。"); }
                catch (HttpRequestException ex) { throw new IOException("中文名称查询服务暂时无法连接。常用作品和角色仍可直接搜索，也可输入英文名称或主题页链接。", ex); }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                { throw new IOException("中文名称查询超时，请尝试英文名称或主题页链接。"); }
                if (ChineseTopics.ContainsChinese(resolved)) throw new ArgumentException("未找到有效的英文名称，请使用英文名称或主题页链接。");
            }
        }
        Uri address = TopicAddress(resolved, page);
        var candidates = new List<Uri> { address };
        if (address.Host == "alphacoders.com" && Regex.IsMatch(address.AbsolutePath, @"^/[\w%()-]+/?$", RegexOptions.None, regexTimeout))
        {
            string path = address.AbsolutePath.TrimEnd('/');
            path = path.EndsWith("-wallpapers", StringComparison.OrdinalIgnoreCase) ? path[..^11] : path + "-wallpapers";
            candidates.Add(new UriBuilder(address) { Path = path }.Uri);
        }
        OnlinePage? empty = null;
        foreach (Uri candidate in candidates.Distinct())
        {
            try
            {
                using var response = await transport.OpenAsync(candidate, deadline.Token).ConfigureAwait(false);
                string html = Encoding.UTF8.GetString(await ReadBoundedAsync(response.Body, 4 * 1024 * 1024, deadline.Token).ConfigureAwait(false));
                var result = ParsePage(html, candidate, page);
                if (result.Images.Count > 0) return result;
                empty ??= result;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { }
        }
        if (empty != null) return empty;
        throw new ArgumentException("没有找到对应的公开主题页。请检查英文名称或点击“网站中搜索”，再粘贴主题页链接。");
    }

    internal static OnlinePage ParsePage(string html, Uri address, int page)
    {
        var starts = Regex.Matches(html, @"<div\b[^>]*(?:\bclass\s*=\s*[""'][^""']*\b(?:thumb-container-wallpaper-desktop|css-grid-content)\b[^""']*[""']|\bitemtype\s*=\s*[""']https?://schema\.org/ImageObject[""'])[^>]*>",
            RegexOptions.IgnoreCase, regexTimeout);
        var images = new List<OnlineImage>(); var seen = new HashSet<string>();
        for (int i = 0; i < starts.Count && images.Count < 60; i++)
        {
            int end = i + 1 < starts.Count ? starts[i + 1].Index : Math.Min(html.Length, starts[i].Index + 24000);
            string card = html[starts[i].Index..end];
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match meta in Regex.Matches(card, @"<meta\b[^>]*>", RegexOptions.IgnoreCase, regexTimeout))
            {
                var attributes = Attributes(meta.Value);
                if (attributes.TryGetValue("itemprop", out string? property) && attributes.TryGetValue("content", out string? content))
                    values.TryAdd(property, content);
            }
            if (!Uri.TryCreate(values.GetValueOrDefault("contentUrl"), UriKind.Absolute, out var original)) continue;
            try { ValidateAddress(original); } catch (ArgumentException) { continue; }
            if (!Regex.IsMatch(original.Host, @"^images\d*\.alphacoders\.com$", RegexOptions.IgnoreCase, regexTimeout)
                || !Extensions.Contains(Path.GetExtension(original.AbsolutePath))
                || Path.GetFileName(original.AbsolutePath).StartsWith("thumb", StringComparison.OrdinalIgnoreCase)) continue;
            string id = Path.GetFileNameWithoutExtension(original.AbsolutePath);
            if (!id.All(char.IsAsciiDigit) || id.Length == 0 || !seen.Add(id)) continue;
            Uri? thumbnail = null;
            foreach (Match link in Regex.Matches(card, @"(?:srcset|src|content)\s*=\s*[""'](?<url>https://images\d*\.alphacoders\.com/[^""'\s]+)[""']", RegexOptions.IgnoreCase, regexTimeout))
                if (Uri.TryCreate(WebUtility.HtmlDecode(link.Groups["url"].Value), UriKind.Absolute, out var candidate)
                    && Path.GetFileName(candidate.AbsolutePath).StartsWith("thumb", StringComparison.OrdinalIgnoreCase)
                    && Extensions.Contains(Path.GetExtension(candidate.AbsolutePath))) { thumbnail = candidate; break; }
            if (thumbnail == null) continue;
            Uri source = Uri.TryCreate(values.GetValueOrDefault("url"), UriKind.Absolute, out var detail) ? detail : address;
            try { ValidateAddress(source); } catch (ArgumentException) { source = address; }
            Match size = Regex.Match(card, @"(?:Wallpaper|Ultra HD)\s*\((\d{2,6})\s*x\s*(\d{2,6})\)", RegexOptions.IgnoreCase, regexTimeout);
            int width = int.TryParse(values.GetValueOrDefault("width"), out int metadataWidth) && metadataWidth is > 0 and <= 100000 ? metadataWidth : size.Success ? int.Parse(size.Groups[1].Value) : 0;
            int height = int.TryParse(values.GetValueOrDefault("height"), out int metadataHeight) && metadataHeight is > 0 and <= 100000 ? metadataHeight : size.Success ? int.Parse(size.Groups[2].Value) : 0;
            string name = values.GetValueOrDefault("name") ?? $"Alpha Coders {id}";
            name = new string(name.Where(c => !char.IsControl(c)).Take(WallpaperName.MaximumLength).ToArray()).Trim();
            images.Add(new(id, name.Length == 0 ? $"Alpha Coders {id}" : name, original, thumbnail, source, width, height));
        }
        Match heading = Regex.Match(html, @"<h1\b[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline, regexTimeout);
        string title = heading.Success ? WebUtility.HtmlDecode(Regex.Replace(heading.Groups[1].Value, "<[^>]+>", "", RegexOptions.None, regexTimeout)).Trim() : "Alpha Coders";
        return new(address, title, images, page);
    }

    static Dictionary<string, string> Attributes(string tag)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Regex.Matches(tag, @"([\w:-]+)\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.None, regexTimeout))
            values[match.Groups[1].Value] = WebUtility.HtmlDecode(match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value);
        return values;
    }

    public async Task<Bitmap> ThumbnailAsync(OnlineImage image, CancellationToken token)
    {
        ValidateAddress(image.Thumbnail);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(45));
        using var response = await transport.OpenAsync(image.Thumbnail, deadline.Token).ConfigureAwait(false);
        using var stream = new MemoryStream(await ReadBoundedAsync(response.Body, 4 * 1024 * 1024, deadline.Token).ConfigureAwait(false));
        using var source = Image.FromStream(stream, false, true);
        if ((long)source.Width * source.Height > 4000000) throw new InvalidDataException("缩略图尺寸过大。");
        return ThumbnailCache.Resize(source);
    }

    internal Task<OnlineResponse> OpenOriginalAsync(OnlineImage image, CancellationToken token)
    { ValidateAddress(image.Original); return transport.OpenAsync(image.Original, token); }

    internal static async Task<byte[]> ReadBoundedAsync(Stream source, int maximum, CancellationToken token)
    {
        using var target = new MemoryStream(); byte[] buffer = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (target.Length + read > maximum) throw new InvalidDataException("网页或图片超过允许大小。");
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        return target.ToArray();
    }
}
