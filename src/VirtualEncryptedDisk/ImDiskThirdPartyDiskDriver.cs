using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VirtualEncryptedDisk;

/// <summary>
/// 基于 ImDisk 命令行的真实挂载实现。
/// 依赖: Windows + 已安装 imdisk.exe + 管理员权限。
/// </summary>
public sealed class ImDiskThirdPartyDiskDriver : IThirdPartyDiskDriver
{
    private string? _mountedImagePath;

    public async Task MountAsync(VirtualDiskConfig config, byte[] decryptedDiskBytes, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("ImDisk 驱动仅支持在 Windows 上挂载盘符。");
        }

        var mountPoint = NormalizeMountPoint(config.MountPoint);
        EnsureDriveLetterAvailable(mountPoint);
        await EnsureImDiskAvailableAsync(ct);

        var tempImage = Path.Combine(Path.GetTempPath(), $"ved-{Guid.NewGuid():N}.img");
        await File.WriteAllBytesAsync(tempImage, decryptedDiskBytes, ct);

        // -a 新建设备，-t file 文件后端，-f 镜像文件，-m 盘符，-o rw/ro
        var mode = config.ReadOnly ? "ro" : "rw";
        var args = $"-a -t file -f \"{tempImage}\" -m {mountPoint} -o {mode}";

        var result = await RunProcessAsync("imdisk", args, ct);
        if (result.ExitCode != 0)
        {
            File.Delete(tempImage);
            throw new InvalidOperationException(
                "ImDisk 挂载失败。\n" +
                $"退出码: {result.ExitCode}\n" +
                $"命令: imdisk {args}\n" +
                $"stdout: {NormalizeOutput(result.StdOut)}\n" +
                $"stderr: {NormalizeOutput(result.StdErr)}");
        }

        _mountedImagePath = tempImage;
    }

    public async Task UnmountAsync(string mountPoint, CancellationToken ct = default)
    {
        var normalized = NormalizeMountPoint(mountPoint);
        var result = await RunProcessAsync("imdisk", $"-D -m {normalized}", ct);

        if (_mountedImagePath is not null && File.Exists(_mountedImagePath))
        {
            File.Delete(_mountedImagePath);
            _mountedImagePath = null;
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "ImDisk 卸载失败。\n" +
                $"退出码: {result.ExitCode}\n" +
                $"stdout: {NormalizeOutput(result.StdOut)}\n" +
                $"stderr: {NormalizeOutput(result.StdErr)}");
        }
    }

    private static string NormalizeMountPoint(string mountPoint)
    {
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            throw new ArgumentException("挂载盘符不能为空。", nameof(mountPoint));
        }

        var letter = char.ToUpperInvariant(mountPoint.Trim()[0]);
        if (letter is < 'A' or > 'Z')
        {
            throw new ArgumentException("挂载点必须是盘符，例如 R:", nameof(mountPoint));
        }

        return $"{letter}:";
    }

    private static void EnsureDriveLetterAvailable(string mountPoint)
    {
        if (Directory.Exists($"{mountPoint}\\"))
        {
            throw new InvalidOperationException($"盘符 {mountPoint} 已被占用，请更换为未使用盘符。");
        }
    }

    private static async Task EnsureImDiskAvailableAsync(CancellationToken ct)
    {
        var probe = await RunProcessAsync("imdisk", "-h", ct);
        if (probe.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "检测到 ImDisk 不可用或无执行权限。\n" +
                $"退出码: {probe.ExitCode}\n" +
                $"stdout: {NormalizeOutput(probe.StdOut)}\n" +
                $"stderr: {NormalizeOutput(probe.StdErr)}");
        }
    }

    private static string NormalizeOutput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "<empty>";
        }

        return text.Trim();
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动进程: {fileName}");
            var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stdErrTask = process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);
            var stdOut = await stdOutTask;
            var stdErr = await stdErrTask;
            return new ProcessResult(process.ExitCode, stdOut, stdErr);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"无法启动 {fileName}，请确认已安装并在 PATH 中。系统消息: {ex.Message}", ex);
        }
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
