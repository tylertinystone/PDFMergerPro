## WinForms YouTube 下载器（yt-dlp）

这个项目提供一个简单的 WinForms 界面：输入 YouTube 链接，自动调用 `yt-dlp` 下载 1080P MP4。

### 功能
- 输入 YouTube 视频链接
- 选择下载目录
- 可勾选「使用浏览器代理（系统代理）」自动复用浏览器/系统代理配置
- 自动调用以下参数下载/合并为 MP4：
  - `--js-runtimes node`
  - `--cookies cookies.txt`
  - `-f "bestvideo[height<=1080][ext=mp4]+bestaudio[ext=m4a]/best[height<=1080][ext=mp4]/best"`
  - `--merge-output-format mp4`

### 使用前准备
1. 安装 [.NET 8 SDK](https://dotnet.microsoft.com/download)
2. 安装 `yt-dlp` 并加入 PATH
3. 安装 Node.js（给 `--js-runtimes node` 使用）
4. 在程序运行目录放置 `cookies.txt`

### 运行
```bash
dotnet run
```

### 备注
- 本项目 target 为 `net8.0-windows`，请在 Windows 环境运行。
- 开启「使用浏览器代理」后，会根据系统代理设置自动传入 `--proxy` 给 `yt-dlp`。
