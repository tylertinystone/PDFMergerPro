using DokanNet;
using DokanNet.Logging;
using System.Runtime.InteropServices;

namespace VirtualEncryptedDisk;

/// <summary>
/// 基于 Dokan.NET 的挂载实现（不依赖 DokanNet.Mirror）。
/// 使用 DokanInstanceBuilder API，兼容新版本 DokanNet。
/// </summary>
public sealed class DokanThirdPartyDiskDriver : IThirdPartyDiskDriver
{
    private string? _mountedRoot;
    private IDokanInstance? _instance;

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

        try
        {
            _instance = await Task.Run(() =>
                new DokanInstanceBuilder(fs)
                    .ConfigureMountPoint(mountPoint)
                    .ConfigureOptions(options)
                    .ConfigureLogger(new ConsoleLogger("[Dokan] "))
                    .Build(), ct);
        }
        catch
        {
            Directory.Delete(root, recursive: true);
            throw;
        }

        _mountedRoot = root;
    }

    public Task UnmountAsync(string mountPoint, CancellationToken ct = default)
    {
        _instance?.Dispose();
        _instance = null;

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
