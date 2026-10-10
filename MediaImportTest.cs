using System.Drawing.Imaging;

namespace LumaWall;

static class MediaImportTest
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Run()
    {
        var task = RunAsync(); var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (timeout.Elapsed.TotalSeconds > 30) throw new TimeoutException("导入测试超时。");
            Application.DoEvents(); Thread.Sleep(10);
        }
        task.GetAwaiter().GetResult();
    }

    public static async Task RunAsync()
    {
        // Every fixture is inside a uniquely named directory; the real media library is never touched.
        string root = Path.Combine(AppContext.BaseDirectory, "import-fixtures-" + Guid.NewGuid().ToString("N"));
        string media = Path.Combine(root, "media");
        string original = Path.Combine(root, "original.PNG");
        string brokenImage = Path.Combine(root, "broken.png");
        string video = Path.Combine(root, "valid.mp4");
        string brokenVideo = Path.Combine(root, "broken.webm");
        string unsupported = Path.Combine(root, "notes.txt");
        string folder = Path.Combine(root, "folder.png");
        string blockedDestination = Path.Combine(root, "blocked");
        Directory.CreateDirectory(media);
        try
        {
            using (var image = new Bitmap(12, 8))
            {
                using var graphics = Graphics.FromImage(image);
                graphics.Clear(Color.CornflowerBlue);
                image.Save(original, ImageFormat.Png);
            }
            File.WriteAllText(brokenImage, "This is not an image.");
            File.WriteAllBytes(video, [1, 2, 3, 4]);
            File.WriteAllBytes(brokenVideo, [0, 0]);
            File.WriteAllText(unsupported, "Unsupported fixture.");
            File.WriteAllText(blockedDestination, "A regular file cannot be a media directory.");
            Directory.CreateDirectory(folder);
            byte[] originalBytes = File.ReadAllBytes(original);
            DateTime originalModified = File.GetLastWriteTimeUtc(original);
            string existing = Path.Combine(media, "existing.png");
            File.Copy(original, existing);

            Check(MediaImport.IsSupported("image.JPEG") && MediaImport.IsSupported("clip.MKV") &&
                MediaImport.IsVideoPath("clip.MKV") && !MediaImport.IsVideoPath(original) &&
                !MediaImport.IsSupported("notes.txt"), "Supported extension checks must be case insensitive.");

            var images = await MediaImport.ImportAsync(
                [original, Path.Combine(root, ".", "original.PNG"), brokenImage, Path.Combine(root, "missing.png"), unsupported, folder],
                media, null);
            Check(images.Items.Count == 1 && images.Errors.Count == 4,
                "A duplicate is ignored, one valid image imports, and four invalid inputs report errors.");
            string imageCopy = images.Items.Single().Path!;
            Check(images.Items[0].Kind == "image" && images.Items[0].Name == "original" &&
                Path.GetDirectoryName(imageCopy) == media && Guid.TryParseExact(Path.GetFileNameWithoutExtension(imageCopy), "N", out _),
                "Images must use a GUID copy in the media folder while preserving the display name.");
            Check(File.ReadAllBytes(imageCopy).SequenceEqual(originalBytes), "Managed image copy must retain original bytes.");
            Check(Directory.GetFiles(media).Length == 2, "Failed images must leave no copies.");
            CheckOriginal(original, originalBytes, originalModified);

            var noDecoder = await MediaImport.ImportAsync([video], media, null);
            Check(noDecoder.Items.Count == 0 && noDecoder.Errors.Count == 1 && Directory.GetFiles(media).Length == 2,
                "Video import requires a decoder validator and must not leave a copy when unavailable.");

            int validationCalls = 0;
            var videos = await MediaImport.ImportAsync([video, brokenVideo], media, (path, token) =>
            {
                token.ThrowIfCancellationRequested();
                validationCalls++;
                Check(Path.GetDirectoryName(path) == media && path != video && path != brokenVideo && File.Exists(path),
                    "Video decoding must validate the newly created managed copy.");
                if (File.ReadAllBytes(path).Length < 4) throw new InvalidDataException("Fixture decoder rejected damaged video.");
                return Task.CompletedTask;
            });
            Check(validationCalls == 2 && videos.Items.Count == 1 && videos.Errors.Count == 1 &&
                videos.Items[0].Kind == "video" && Directory.GetFiles(media).Length == 3,
                "Valid videos import; decoder failures clean their copied file and preserve successful imports.");
            Check(File.ReadAllBytes(video).SequenceEqual(new byte[] { 1, 2, 3, 4 }) &&
                File.ReadAllBytes(brokenVideo).SequenceEqual(new byte[] { 0, 0 }), "Video sources must remain unchanged.");

            var copyFailure = await MediaImport.ImportAsync([original], blockedDestination, null);
            Check(copyFailure.Items.Count == 0 && copyFailure.Errors.Count == 1 &&
                File.ReadAllText(blockedDestination) == "A regular file cannot be a media directory.",
                "A destination creation failure must report the input and preserve existing files.");

            using (var cancellation = new CancellationTokenSource())
            {
                bool cancelled = false;
                try
                {
                    await MediaImport.ImportAsync([original, video], media, async (_, token) =>
                    {
                        cancellation.Cancel();
                        await Task.Delay(1, token);
                    }, cancellation.Token);
                }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && Directory.GetFiles(media).Length == 3,
                    "Cancellation must remove both earlier successes and the in-progress copy in this batch.");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                bool cancelled = false;
                string unused = Path.Combine(root, "unused-media");
                try { await MediaImport.ImportAsync([original], unused, null, cancellation.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && !Directory.Exists(unused), "An already cancelled import must create nothing.");
            }
            CheckOriginal(original, originalBytes, originalModified);

            Check(MediaImport.Rollback(images, root).Count == 1 && File.Exists(imageCopy),
                "Rollback with a different destination must not delete anything.");
            // Items are public UI data, not authority to delete arbitrary files.
            images.Items.Add(new Wallpaper("external original", "image", original));
            images.Items.Add(new Wallpaper("preexisting library file", "image", existing));
            Check(MediaImport.Rollback(images, media).Count == 0 && !File.Exists(imageCopy) &&
                File.Exists(original) && File.Exists(existing) && File.Exists(videos.Items[0].Path),
                "Save-failure rollback must remove only files created by that result.");
            Check(MediaImport.Rollback(images, media).Count == 0, "Rollback must be idempotent.");
            Check(MediaImport.Rollback(videos, media).Count == 0 && videos.Items.Count == 0 && Directory.GetFiles(media).Length == 1,
                "Rollback must clear successful entries and preserve files from before import.");
            CheckOriginal(original, originalBytes, originalModified);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "media-import-test.txt"),
                "PASS: image validation; GUID copies; source preservation; batch deduplication; partial failure; required video decoder; damaged video cleanup; destination failure; whole-batch cancellation rollback; pre-cancelled no-op; safe save-failure rollback; rollback idempotence.");
        }
        finally
        {
            // Verify the exact test-owned directory before deleting its known fixture files.
            string fullRoot = Path.GetFullPath(root);
            string expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            if (Path.GetDirectoryName(fullRoot) != expectedParent || !Path.GetFileName(fullRoot).StartsWith("import-fixtures-", StringComparison.Ordinal) ||
                (File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to clean an unexpected import fixture directory.");
            foreach (string file in Directory.GetFiles(media)) File.Delete(file);
            Directory.Delete(media);
            Directory.Delete(folder);
            foreach (string file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }

    static void CheckOriginal(string path, byte[] bytes, DateTime modified)
    {
        Check(File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(path) == modified,
            "Import and rollback must not modify the original image.");
    }
}
