using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Text.Json;

namespace LumaWall;

// The browser discovers the real search result URL. The existing gallery owns
// selection, thumbnails, direct original downloads, cancellation and import.
sealed class BrowserPageSearch : IDisposable
{
    readonly WebView2 browser = new() { Size = new(800, 600), Location = new(-10000, -10000) };
    readonly string profile;
    readonly CancellationTokenSource lifetime = new();
    bool disposed;
    internal CoreWebView2? Core => browser.CoreWebView2;
    public BrowserPageSearch(Control host, string? profile = null)
    {
        this.profile = profile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaWall", "webview2");
        host.Controls.Add(browser); browser.SendToBack();
    }
    internal async Task InitializeAsync()
    {
        if (Core != null) return;
        var environment = await CoreWebView2Environment.CreateAsync(null, profile);
        lifetime.Token.ThrowIfCancellationRequested();
        await browser.EnsureCoreWebView2Async(environment);
        var core = Core!;
        core.Settings.AreHostObjectsAllowed = false; core.Settings.IsWebMessageEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false; core.Settings.IsGeneralAutofillEnabled = false;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; };
        core.NavigationStarting += (_, e) =>
        {
            if (!IsPage(e.Uri)) e.Cancel = true;
        };
    }
    internal static bool IsPage(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        try { AlphaProvider.ValidateAddress(uri); }
        catch (ArgumentException) { return false; }
        return uri.Host is "alphacoders.com" or "wall.alphacoders.com";
    }
    internal static Uri Paged(Uri address, int page)
    {
        var retained = address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => Uri.UnescapeDataString(x.Split('=')[0]).ToLowerInvariant() is not ("page" or "view"));
        return new UriBuilder(address) { Query = string.Join("&", retained.Append($"page={Math.Clamp(page, 1, 200)}").Append("view=pagination")), Fragment = "" }.Uri;
    }
    internal async Task<OnlinePage> SearchAsync(string query, int page, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(75));
        Uri target;
        if ((query.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || query.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate(query, UriKind.Absolute, out var address))
        {
            if (!IsPage(query)) throw new ArgumentException("请使用 Alpha Coders 的网页地址。");
            target = Paged(address, page);
        }
        else target = Paged(OnlineBrowserDialog.SearchAddress(await OnlineBrowserDialog.EnglishQueryAsync(query, deadline.Token)), page);
        await InitializeAsync(); deadline.Token.ThrowIfCancellationRequested();
        var core = Core!;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong navigationId = 0;
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs e) { if (navigationId == 0) navigationId = e.NavigationId; }
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (navigationId == 0 || e.NavigationId != navigationId) return;
            if (e.IsSuccess) completion.TrySetResult(true);
            else completion.TrySetException(new IOException("网站搜索加载失败：" + e.WebErrorStatus));
        }
        void DomReady(object? sender, CoreWebView2DOMContentLoadedEventArgs e) { if (navigationId != 0 && e.NavigationId == navigationId) completion.TrySetResult(true); }
        core.NavigationStarting += Starting;
        core.NavigationCompleted += Completed;
        core.DOMContentLoaded += DomReady;
        try
        {
            core.Navigate(target.AbsoluteUri);
            await completion.Task.WaitAsync(deadline.Token);
            OnlinePage? result = null;
            for (int attempt = 0; attempt < 16; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                result = await ReadPageAsync(core, page);
                if (result.Images.Count > 0) return result;
                await Task.Delay(400, deadline.Token);
            }
            return result!;
        }
        finally { core.NavigationStarting -= Starting; core.NavigationCompleted -= Completed; core.DOMContentLoaded -= DomReady; if (deadline.IsCancellationRequested && !browser.IsDisposed) core.Stop(); }
    }
    internal static async Task<OnlinePage> ReadPageAsync(CoreWebView2 core, int page = 1)
    {
        if (!IsPage(core.Source)) throw new ArgumentException("请先打开 Alpha Coders 的图片搜索结果或主题页。");
        string? html = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync("document.documentElement.outerHTML"));
        if (html == null || html.Length > 8 * 1024 * 1024) throw new IOException("网页内容为空或超过允许大小。");
        return AlphaProvider.ParsePage(html, new Uri(core.Source), page);
    }
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); browser.Dispose(); lifetime.Dispose(); }
}
