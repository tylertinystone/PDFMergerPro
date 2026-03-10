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
        var tempImage = Path.Combine(Path.GetTempPath(), $"ved-{Guid.NewGuid():N}.img");
        await File.WriteAllBytesAsync(tempImage, decryptedDiskBytes, ct);

        // -a 新建设备，-t file 文件后端，-f 镜像文件，-m 盘符，-o rw/ro
        var mode = config.ReadOnly ? "ro" : "rw";
        var args = $"-a -t file -f \"{tempImage}\" -m {mountPoint} -o {mode}";

        var code = await RunProcessAsync("imdisk", args, ct);
        if (code != 0)
        {
            File.Delete(tempImage);
            throw new InvalidOperationException($"ImDisk 挂载失败，退出码: {code}。请确认已安装 ImDisk 且以管理员身份运行。\n命令: imdisk {args}");
        }

        _mountedImagePath = tempImage;
    }

    public async Task UnmountAsync(string mountPoint, CancellationToken ct = default)
    {
        var normalized = NormalizeMountPoint(mountPoint);
        var code = await RunProcessAsync("imdisk", $"-D -m {normalized}", ct);

        if (_mountedImagePath is not null && File.Exists(_mountedImagePath))
        {
            File.Delete(_mountedImagePath);
            _mountedImagePath = null;
        }

        if (code != 0)
        {
            throw new InvalidOperationException($"ImDisk 卸载失败，退出码: {code}。");
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

    private static async Task<int> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动进程: {fileName}");
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}
