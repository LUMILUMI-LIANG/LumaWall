namespace LumaWall;

sealed class MediaImportResult
{
    readonly HashSet<string> createdFiles = new(StringComparer.OrdinalIgnoreCase);
    internal string MediaFolder { get; }
    internal IEnumerable<string> CreatedFiles => createdFiles.ToArray();
    public List<Wallpaper> Items { get; } = [];
    public List<string> Errors { get; } = [];

    internal MediaImportResult(string mediaFolder) => MediaFolder = mediaFolder;
    internal void RegisterCreated(string path) => createdFiles.Add(path);
    internal void ForgetCreated(string path) => createdFiles.Remove(path);
}

static class MediaImport
{
    static readonly HashSet<string> imageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
    static readonly HashSet<string> videoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".webm", ".mkv" };

    // These lightweight helpers classify extensions; ImportAsync validates the file itself.
    public static bool IsSupported(string? path) => imageExtensions.Contains(Extension(path)) || IsVideoPath(path);
    public static bool IsVideoPath(string? path) => videoExtensions.Contains(Extension(path));
    static string Extension(string? path)
    {
        try { return Path.GetExtension(path) ?? ""; }
        catch (ArgumentException) { return ""; }
    }

    public static async Task<MediaImportResult> ImportAsync(IEnumerable<string> paths, string mediaFolder,
        Func<string, CancellationToken, Task>? validateVideo, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string destination = NormalizeFolder(mediaFolder);
        var result = new MediaImportResult(destination);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string path in paths)
            {
                token.ThrowIfCancellationRequested();
                string? target = null;
                try
                {
                    if (string.IsNullOrWhiteSpace(path)) throw new InvalidDataException("文件路径为空。");
                    string source = Path.GetFullPath(path);
                    if (!seen.Add(source)) continue;
                    if (Directory.Exists(source)) throw new InvalidDataException("不能导入文件夹。");
                    if (!File.Exists(source)) throw new FileNotFoundException("文件不存在或无法访问。");
                    if (!IsSupported(source)) throw new InvalidDataException("不支持此文件类型。");
                    bool video = IsVideoPath(source);
                    if (video && validateVideo is null) throw new InvalidDataException("视频解码验证暂不可用，未导入此视频。");

                    EnsureSafeDirectory(destination);
                    target = Path.Combine(destination, Guid.NewGuid().ToString("N") + Extension(source).ToLowerInvariant());
                    await CopyNewFileAsync(source, target, result, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (video) await validateVideo!(target, token).ConfigureAwait(false);
                    else ValidateImage(target);
                    token.ThrowIfCancellationRequested();
                    result.Items.Add(new Wallpaper(Path.GetFileNameWithoutExtension(source), video ? "video" : "image", target));
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    result.Errors.Add($"{DisplayName(path)}：{ex.Message}");
                    if (target is not null) CleanCreatedFile(result, target, destination, result.Errors);
                }
            }
            token.ThrowIfCancellationRequested();
            return result;
        }
        catch
        {
            // A cancelled batch never leaves copies whose result the caller cannot receive.
            Rollback(result, destination);
            throw;
        }
    }

    static async Task CopyNewFileAsync(string source, string target, MediaImportResult result, CancellationToken token)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        result.RegisterCreated(target); // Register only after CreateNew succeeded; never own an existing file.
        await input.CopyToAsync(output, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
    }

    internal static void ValidateImage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        if (image.Width <= 0 || image.Height <= 0 || (long)image.Width * image.Height > 100000000)
            throw new InvalidDataException("图片尺寸无效或超过 1 亿像素。");
        // Force a decode rather than trusting a recognized extension or header.
        using var decoded = new Bitmap(image.Width, image.Height);
        using var graphics = Graphics.FromImage(decoded);
        graphics.DrawImage(image, new Rectangle(0, 0, decoded.Width, decoded.Height));
    }

    // Save failure callers can roll back only the copies registered by this import.
    // Items may have been edited by a caller; it is deliberately not the deletion ledger.
    public static IReadOnlyList<string> Rollback(MediaImportResult result, string mediaFolder)
    {
        ArgumentNullException.ThrowIfNull(result);
        var errors = new List<string>();
        string destination;
        try { destination = NormalizeFolder(mediaFolder); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { errors.Add("无法回滚导入：" + ex.Message); return errors; }
        if (!string.Equals(destination, result.MediaFolder, StringComparison.OrdinalIgnoreCase))
        { errors.Add("无法回滚导入：媒体库路径与本次导入不匹配。"); return errors; }
        foreach (string file in result.CreatedFiles) CleanCreatedFile(result, file, destination, errors);
        result.Items.RemoveAll(item => item.Path is not null && !File.Exists(item.Path));
        return errors;
    }

    static void CleanCreatedFile(MediaImportResult result, string path, string destination, List<string> errors)
    {
        if (!result.CreatedFiles.Contains(path, StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), destination, StringComparison.OrdinalIgnoreCase))
                throw new IOException("副本不在本次导入的媒体库中。");
            EnsureNoDirectoryLinks(destination);
            if (File.Exists(path))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("媒体副本变成了链接，未删除。");
                File.Delete(path);
            }
            result.ForgetCreated(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { errors.Add($"{Path.GetFileName(path)}：媒体副本清理失败，{ex.Message}"); }
    }

    static string NormalizeFolder(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    static void EnsureSafeDirectory(string path)
    {
        EnsureNoDirectoryLinks(path);
        Directory.CreateDirectory(path);
        EnsureNoDirectoryLinks(path);
    }
    static void EnsureNoDirectoryLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("媒体库路径包含目录链接，无法安全写入或清理。");
    }
    static string DisplayName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "未命名文件";
        try { return Path.GetFileName(path) is { Length: > 0 } name ? name : path; }
        catch (ArgumentException) { return path; }
    }
}
