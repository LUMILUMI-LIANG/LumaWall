using Velopack;
using Velopack.Sources;
namespace LumaWall;
partial class MainWindow
{
    internal const string ReleaseRepository = "https://github.com/LUMILUMI-LIANG/LumaWall";
    bool checkingUpdates;
    internal async Task CheckAppUpdatesAsync()
    {
        if (checkingUpdates) return;
        checkingUpdates = true;
        try
        {
            var manager = new UpdateManager(new GithubSource(ReleaseRepository, null, false));
            if (!manager.IsInstalled)
            {
                if (MessageBox.Show(this, "当前运行的是旧版或开发版本。请从发布页下载安装包，以启用应用内更新。\n是否打开下载页？", "检查更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes) OpenExternal(ReleaseRepository + "/releases/latest");
                return;
            }
            status.Text = "正在检查 GitHub 新版本…";
            var update = await manager.CheckForUpdatesAsync();
            if (disposed) return;
            if (update == null) { status.Text = "已经是最新版本"; MessageBox.Show(this, "已经是最新版本。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            if (MessageBox.Show(this, $"发现新版本 {update.TargetFullRelease.Version}，是否下载并更新？\n壁纸、分类及设置会保留。更新完成后重启应用。", "发现更新", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes) return;
            IProgress<int> progress = new Progress<int>(p => { if (!disposed) status.Text = $"正在下载更新：{p}%"; });
            await manager.DownloadUpdatesAsync(update, p => progress.Report(p), lifetime.Token);
            if (disposed) return;
            TryFlushTransforms(); CaptureCurrentMonitor();
            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            settings.WindowWidth = bounds.Width; settings.WindowHeight = bounds.Height;
            Save();
            manager.ApplyUpdatesAndRestart(update);
        }
        catch (OperationCanceledException) { if (!disposed) status.Text = "更新已取消"; }
        catch (Exception ex) { if (!disposed) { status.Text = "检查更新失败：" + ex.Message; MessageBox.Show(this, status.Text + "\n可稍后重试，或从 GitHub Releases 下载新版安装包。", "检查更新", MessageBoxButtons.OK, MessageBoxIcon.Information); } }
        finally { checkingUpdates = false; }
    }
}
