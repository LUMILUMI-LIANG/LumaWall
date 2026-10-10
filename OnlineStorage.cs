namespace LumaWall;

sealed record OnlineAsset(string Id, string Category, string SourcePage, string OriginalUrl, string ManagedRoot, string ManagedPath, bool Owned = true);
partial class Settings
{
    public string? OnlineDownloadRoot { get; set; }
    public Dictionary<string, OnlineAsset> OnlineAssets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

sealed class OnlineDownloadResult(string root)
{
    public string Root { get; } = root;
    public List<(Wallpaper Item, OnlineAsset Asset)> Items { get; } = [];
    public List<string> Errors { get; } = [];
    public int Skipped { get; internal set; }
    internal HashSet<string> Created { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal bool Committed { get; set; }
}

static class OnlineStorage
{
    public static string CategoryName(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value.Length > 64 || value is "." or ".." || value.EndsWith('.') || value.EndsWith(' ')
            || value.Any(c => char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c)))
            throw new ArgumentException("分类名称需为 1～64 个字符，不能含路径分隔符或文件名非法字符。");
        string stem = value.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[0-9]$"))
            throw new ArgumentException("此分类名称被 Windows 保留，请使用其他名称。");
        return value;
    }

    internal static string RootPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new ArgumentException("请选择绝对路径的下载文件夹。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    internal static void CheckDirectories(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("下载路径包含目录链接，无法安全写入或清理。");
    }

    public static async Task<OnlineDownloadResult> DownloadAsync(AlphaProvider provider, IEnumerable<OnlineImage> selections,
        string rootPath, string category, IEnumerable<OnlineAsset> existing, IProgress<string>? progress, CancellationToken token)
    {
        string root = RootPath(rootPath), name = CategoryName(category), folder = Path.Combine(root, name);
        var result = new OnlineDownloadResult(root);
        var known = existing.ToArray(); var seen = new HashSet<string>();
        CheckDirectories(folder); Directory.CreateDirectory(folder); CheckDirectories(folder);
        try
        {
            foreach (var image in selections)
            {
                token.ThrowIfCancellationRequested();
                if (!seen.Add(image.Id)) continue;
                if (known.Any(x => x.Id == image.Id && File.Exists(x.ManagedPath))) { result.Skipped++; continue; }
                string? part = null, target = null;
                try
                {
                    string extension = Path.GetExtension(image.Original.AbsolutePath).ToLowerInvariant();
                    if (!AlphaProvider.Extensions.Contains(extension) || !image.Id.All(char.IsAsciiDigit) || image.Id.Length == 0)
                        throw new InvalidDataException("原图地址或图片格式无效。");
                    target = Path.Combine(folder, $"alphacoders-{image.Id}-{Guid.NewGuid():N}{extension}");
                    part = target + ".part";
                    progress?.Report("正在下载原图：" + image.Name);
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(3));
                    using (var response = await provider.OpenOriginalAsync(image, deadline.Token).ConfigureAwait(false))
                    {
                        if (response.ContentType.Length > 0 && !response.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                            && response.ContentType != "application/octet-stream") throw new InvalidDataException("网站返回了网页，未保存为图片。");
                        CheckDirectories(folder);
                        using var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                        result.Created.Add(part);
                        long bytes = 0; byte[] buffer = new byte[81920]; int read;
                        while ((read = await response.Body.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) > 0)
                        {
                            bytes += read; if (bytes > 100 * 1024 * 1024) throw new InvalidDataException("原图超过 100 MB，未导入。");
                            await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token).ConfigureAwait(false);
                        }
                        await output.FlushAsync(deadline.Token).ConfigureAwait(false);
                    }
                    await Task.Run(() => MediaImport.ValidateImage(part), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested(); CheckDirectories(folder);
                    File.Move(part, target); result.Created.Remove(part); result.Created.Add(target);
                    var item = new Wallpaper(image.Name, "image", target);
                    result.Items.Add((item, new(image.Id, name, image.SourcePage.AbsoluteUri, image.Original.AbsoluteUri, root, target)));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    result.Errors.Add(image.Name + "：" + ex.Message);
                    if (part != null) DeleteCreated(result, part);
                    if (target != null) DeleteCreated(result, target);
                }
            }
            token.ThrowIfCancellationRequested(); return result;
        }
        catch { Rollback(result); throw; }
    }

    internal static bool Owns(OnlineAsset asset, string path)
    {
        try
        {
            if (!asset.Owned || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(asset.ManagedPath), StringComparison.OrdinalIgnoreCase)) return false;
            string folder = Path.Combine(RootPath(asset.ManagedRoot), CategoryName(asset.Category));
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), folder, StringComparison.OrdinalIgnoreCase)) return false;
            string name = Path.GetFileNameWithoutExtension(path), prefix = "alphacoders-" + asset.Id + "-";
            if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _)) return false;
            CheckDirectories(folder);
            return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return false; }
    }

    public static void Rollback(OnlineDownloadResult result)
    {
        if (result.Committed) return;
        foreach (string path in result.Created.ToArray()) DeleteCreated(result, path);
        result.Items.Clear();
    }
    static void DeleteCreated(OnlineDownloadResult result, string path)
    {
        if (!result.Created.Contains(path)) return;
        try
        {
            string full = Path.GetFullPath(path), root = RootPath(result.Root);
            string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || Path.GetDirectoryName(Path.GetDirectoryName(full)) != root) throw new IOException("清理路径不在本批次下载分类中。");
            CheckDirectories(Path.GetDirectoryName(full)!);
            if (File.Exists(full))
            {
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new IOException("下载文件变成了链接，未删除。");
                File.Delete(full);
            }
            result.Created.Remove(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { result.Errors.Add("下载文件清理失败：" + ex.Message); }
    }
}
