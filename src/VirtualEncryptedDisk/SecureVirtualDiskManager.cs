using System.Security.Cryptography;

namespace VirtualEncryptedDisk;

public sealed class SecureVirtualDiskManager
{
    private readonly EncryptionService _encryption;
    private readonly ContainerFileService _container;
    private readonly IThirdPartyDiskDriver _driver;

    private VirtualDiskConfig? _mountedConfig;
    private string? _mountedPassword;

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
        // 初始容器使用空目录归档，便于后续文件系统内容持久化。
        var plainDisk = VirtualDiskArchiveService.CreateEmptyArchive();

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

            _mountedConfig = config;
            _mountedPassword = password;
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

    public async Task UnmountAsync(string mountPoint, CancellationToken ct = default)
    {
        await _driver.UnmountAsync(mountPoint, ct);

        if (_mountedConfig is not null && _mountedPassword is not null && _driver is IPersistableDiskDriver persistable)
        {
            var updated = persistable.TakeUpdatedDiskBytes();
            if (updated is not null)
            {
                var payload = _encryption.Encrypt(updated, _mountedPassword);
                _container.Write(_mountedConfig.ContainerPath, payload);
            }
        }

        _mountedConfig = null;
        _mountedPassword = null;
    }
}
