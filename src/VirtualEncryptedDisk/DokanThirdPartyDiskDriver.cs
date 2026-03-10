using DokanNet;
using System.Runtime.InteropServices;

namespace VirtualEncryptedDisk;

/// <summary>
/// 基于 Dokan.NET 的挂载实现（不依赖 DokanNet.Mirror）。
/// 思路：将解密后的数据落盘到临时目录，再通过本地透传 IDokanOperations 挂载为盘符。
/// </summary>
public sealed class DokanThirdPartyDiskDriver : IThirdPartyDiskDriver
{
    private string? _mountedRoot;

    public async Task MountAsync(VirtualDiskConfig config, byte[] decryptedDiskBytes, CancellationToken ct = default)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException("Dokan.NET 仅支持 Windows。");
        }

        var mountPoint = NormalizeMountPoint(config.MountPoint);
        if (Directory.Exists($"{mountPoint}\\"))
        {
            throw new InvalidOperationException($"盘符 {mountPoint} 已被占用。");
        }

        var root = Path.Combine(Path.GetTempPath(), $"ved-dokan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        var diskImagePath = Path.Combine(root, "disk.bin");
        await File.WriteAllBytesAsync(diskImagePath, decryptedDiskBytes, ct);

        var fs = new DokanPassthroughOperations(root, config.ReadOnly);
        var options = DokanOptions.FixedDrive;
        if (config.ReadOnly)
        {
            options |= DokanOptions.WriteProtection;
        }

        var status = await Task.Run(() => Dokan.Mount(fs, mountPoint, options, threadCount: 5), ct);
        if (status != DokanStatus.Success)
        {
            Directory.Delete(root, recursive: true);
            throw new InvalidOperationException($"Dokan 挂载失败，状态: {status}。");
        }

        _mountedRoot = root;
    }

    public Task UnmountAsync(string mountPoint, CancellationToken ct = default)
    {
        var normalized = NormalizeMountPoint(mountPoint);
        Dokan.RemoveMountPoint(normalized);

        if (_mountedRoot is not null && Directory.Exists(_mountedRoot))
        {
            Directory.Delete(_mountedRoot, recursive: true);
            _mountedRoot = null;
        }

        return Task.CompletedTask;
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
}
