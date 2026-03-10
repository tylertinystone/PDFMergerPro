using System.Security.Cryptography;

namespace VirtualEncryptedDisk;

public sealed class SecureVirtualDiskManager
{
    private readonly EncryptionService _encryption;
    private readonly ContainerFileService _container;
    private readonly IThirdPartyDiskDriver _driver;

    public SecureVirtualDiskManager(
        EncryptionService encryption,
        ContainerFileService container,
        IThirdPartyDiskDriver driver)
    {
        _encryption = encryption;
        _container = container;
        _driver = driver;
    }

    public async Task CreateEncryptedDiskAsync(VirtualDiskConfig config, string password, CancellationToken ct = default)
    {
        var plainDisk = new byte[config.SizeMb * 1024 * 1024];
        Random.Shared.NextBytes(plainDisk);

        var encryptedPayload = _encryption.Encrypt(plainDisk, password);
        _container.Write(config.ContainerPath, encryptedPayload);
        await Task.CompletedTask;
    }

    public async Task<MountResult> MountWithPasswordAsync(VirtualDiskConfig config, string password, CancellationToken ct = default)
    {
        try
        {
            var payload = _container.Read(config.ContainerPath);
            var plain = _encryption.Decrypt(payload, password);
            await _driver.MountAsync(config, plain, ct);
            return MountResult.Mounted();
        }
        catch (CryptographicException)
        {
            return MountResult.InvalidPassword();
        }
        catch (Exception ex)
        {
            return MountResult.DriverError(ex.Message);
        }
    }

    public Task UnmountAsync(string mountPoint, CancellationToken ct = default)
        => _driver.UnmountAsync(mountPoint, ct);
}
