namespace VirtualEncryptedDisk;

/// <summary>
/// 示例驱动适配器：真实环境请替换为 Dokan/WinFsp/ImDisk 等第三方驱动 SDK 调用。
/// </summary>
public sealed class MockThirdPartyDiskDriver : IThirdPartyDiskDriver
{
    public Task MountAsync(VirtualDiskConfig config, byte[] decryptedDiskBytes, CancellationToken ct = default)
    {
        Console.WriteLine($"[Driver] Mount -> {config.MountPoint}, Size={decryptedDiskBytes.Length} bytes, ReadOnly={config.ReadOnly}");
        return Task.CompletedTask;
    }

    public Task UnmountAsync(string mountPoint, CancellationToken ct = default)
    {
        Console.WriteLine($"[Driver] Unmount -> {mountPoint}");
        return Task.CompletedTask;
    }
}
