using System.Diagnostics;
using System.Net;

namespace YoutubeDownloader;

public sealed class MainForm : Form
{
    private readonly TextBox _urlTextBox = new()
    {
        PlaceholderText = "请输入 YouTube 链接，例如 https://www.youtube.com/watch?v=UtnEgKHR0xM",
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        Width = 620
    };

    private readonly Button _downloadButton = new()
    {
        Text = "下载 1080P MP4",
        Width = 140,
        Height = 34
    };

    private readonly TextBox _logTextBox = new()
    {
        Multiline = true,
        ScrollBars = ScrollBars.Vertical,
        ReadOnly = true,
        Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
    };

    private readonly CheckBox _useBrowserProxyCheckBox = new()
    {
        Text = "使用浏览器代理（系统代理）",
        AutoSize = true,
        Checked = true
    };

    private readonly FolderBrowserDialog _folderDialog = new();

    public MainForm()
    {
        Text = "YouTube 下载器 (yt-dlp)";
        Width = 820;
        Height = 520;
        MinimumSize = new Size(720, 420);
        StartPosition = FormStartPosition.CenterScreen;

        var urlLabel = new Label
        {
            Text = "视频地址",
            AutoSize = true,
            Location = new Point(12, 16)
        };

        _urlTextBox.Location = new Point(12, 38);
        _urlTextBox.Width = ClientSize.Width - 180;

        _downloadButton.Location = new Point(_urlTextBox.Right + 10, 35);
        _downloadButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _downloadButton.Click += DownloadButton_Click;

        _useBrowserProxyCheckBox.Location = new Point(12, 65);

        _logTextBox.Location = new Point(12, 94);
        _logTextBox.Size = new Size(ClientSize.Width - 24, ClientSize.Height - 106);

        Controls.Add(urlLabel);
        Controls.Add(_urlTextBox);
        Controls.Add(_downloadButton);
        Controls.Add(_useBrowserProxyCheckBox);
        Controls.Add(_logTextBox);

        Resize += (_, _) =>
        {
            _urlTextBox.Width = ClientSize.Width - 180;
            _downloadButton.Left = _urlTextBox.Right + 10;
            _logTextBox.Size = new Size(ClientSize.Width - 24, ClientSize.Height - 106);
        };
    }

    private async void DownloadButton_Click(object? sender, EventArgs e)
    {
        var url = _urlTextBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            MessageBox.Show("请输入有效的 YouTube 链接。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (_folderDialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var downloadFolder = _folderDialog.SelectedPath;
        var cookiePath = Path.Combine(AppContext.BaseDirectory, "cookies.txt");

        if (!File.Exists(cookiePath))
        {
            MessageBox.Show($"未找到 cookies.txt：{cookiePath}\n请把 cookies.txt 放在程序目录后重试。", "缺少 cookies", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _downloadButton.Enabled = false;
        _logTextBox.Clear();
        AppendLog("开始下载...");

        var proxy = _useBrowserProxyCheckBox.Checked ? GetBrowserProxy(url) : null;
        if (_useBrowserProxyCheckBox.Checked)
        {
            AppendLog(proxy is null
                ? "未检测到浏览器代理，将按直连方式下载。"
                : $"已启用浏览器代理: {proxy}");
        }

        var args = BuildArgs(url, cookiePath, downloadFolder, proxy);
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                AppendLog(eventArgs.Data);
            }
        };

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                AppendLog(eventArgs.Data);
            }
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                AppendLog("下载完成。", true);
                MessageBox.Show("下载完成。", "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                AppendLog($"下载失败，退出码: {process.ExitCode}", true);
                MessageBox.Show($"下载失败，退出码: {process.ExitCode}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        catch (Exception ex)
        {
            AppendLog($"启动 yt-dlp 失败: {ex.Message}", true);
            MessageBox.Show($"启动 yt-dlp 失败，请确认已安装并加入 PATH。\n{ex.Message}", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _downloadButton.Enabled = true;
            process.Dispose();
        }
    }

    private static string BuildArgs(string url, string cookiePath, string outputFolder, string? proxy)
    {
        static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";

        var format = "bestvideo[height<=1080][ext=mp4]+bestaudio[ext=m4a]/best[height<=1080][ext=mp4]/best";
        var outputTemplate = Path.Combine(outputFolder, "%(title)s [%(id)s].%(ext)s");

        var args = new List<string>
        {
            "--js-runtimes node",
            $"--cookies {Quote(cookiePath)}",
            $"-f {Quote(format)}",
            "--merge-output-format mp4",
            $"-o {Quote(outputTemplate)}",
            Quote(url)
        };

        if (!string.IsNullOrWhiteSpace(proxy))
        {
            args.Insert(1, $"--proxy {Quote(proxy)}");
        }

        return string.Join(' ', args);
    }

    private static string? GetBrowserProxy(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var targetUri))
            {
                return null;
            }

            var proxy = WebRequest.GetSystemWebProxy();
            var proxyUri = proxy.GetProxy(targetUri);

            if (proxyUri == targetUri)
            {
                return null;
            }

            return proxyUri.ToString();
        }
        catch
        {
            return null;
        }
    }

    private void AppendLog(string message, bool withTimestamp = false)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => AppendLog(message, withTimestamp)));
            return;
        }

        var line = withTimestamp ? $"[{DateTime.Now:HH:mm:ss}] {message}" : message;
        _logTextBox.AppendText(line + Environment.NewLine);
    }
}
