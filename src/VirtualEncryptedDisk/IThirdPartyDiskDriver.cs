namespace VirtualEncryptedDisk;

public interface IThirdPartyDiskDriver
{
    Task MountAsync(VirtualDiskConfig config, byte[] decryptedDiskBytes, CancellationToken ct = default);
    Task UnmountAsync(string mountPoint, CancellationToken ct = default);
}
