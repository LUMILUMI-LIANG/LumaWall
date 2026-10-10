namespace LumaWall;

static class TestFiles
{
    public static void RemoveFixture(string root)
    {
        string full = Path.GetFullPath(root), baseFolder = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(baseFolder, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).Contains("fixtures", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove a directory outside isolated test fixtures.");
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try { if (Directory.Exists(full)) Directory.Delete(full, true); return; }
            catch (IOException) when (attempt < 49) { Application.DoEvents(); Thread.Sleep(50); }
            catch (UnauthorizedAccessException) when (attempt < 49) { Application.DoEvents(); Thread.Sleep(50); }
        }
    }
}
