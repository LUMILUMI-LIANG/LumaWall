# LumaWall

Windows 动态壁纸应用，支持图片、视频、分类、播放列表和多显示器。

## 下载安装

前往 [最新版本下载页](https://github.com/changyi1015/LumaWall/releases/latest)，展开 **Assets**，下载文件名包含 **Setup.exe** 的安装包并运行。

支持 Windows 10 / 11 x64。安装包包含 .NET 运行环境；缺少 WebView2 时会联网安装。安装包目前未签名，请确认来源为本仓库发布页。

便携包适合临时运行；应用内更新需使用安装版。旧便携版用户请退出旧程序后安装。

## 功能

- 图片和视频壁纸、多显示器、画面缩放与位置调整。
- 壁纸分类、文件夹导入、丢失文件重新定位、批量删除。
- 播放列表和定时轮换、大图预览、深色主题及可调整分区。
- Alpha Coders 中文检索、网站浏览、多选原图下载及导入。

壁纸内容由用户自行导入或下载，本仓库不分发用户的壁纸库。在线服务的可用性取决于网站及网络情况。

## 后续更新

在托盘菜单或“运行状态”中点击 **检查更新**。安装版会检查本仓库的发布版本，确认后下载并重启应用完成更新。

设置和壁纸库保存在 `%LOCALAPPDATA%\LumaWall`，与程序安装目录分开。更新保留此目录。建议定期备份该目录及自行管理的外部壁纸文件。

## 从源码构建

安装 .NET 8 SDK，在 Windows 上运行：

```powershell
dotnet build Wall.csproj -c Release
dotnet run --project Wall.csproj
```

生成安装包和更新包：

```powershell
./scripts/Publish-Release.ps1
```

## 发布新版本

修改 `Wall.csproj` 中的 `Version`（例如 `0.24.1`），更新 `RELEASE_NOTES.md`，将修改推送至 `main`。GitHub Actions 自动构建、验证并创建对应版本的 Release。也可在 Actions 页面手动运行工作流。相同版本已发布时跳过，发布新内容必须递增版本号。

请保留 Release 中的安装包、完整更新包和 JSON 索引，应用内更新需要这些文件。首次公开版本只提供 Windows x64。
