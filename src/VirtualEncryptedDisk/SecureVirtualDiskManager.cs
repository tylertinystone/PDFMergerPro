using System.Security.Cryptography;

namespace VirtualEncryptedDisk;

public sealed class SecureVirtualDiskManager
{
    private readonly EncryptionService _encryption;
    private readonly ContainerFileService _container;
    private readonly IThirdPartyDiskDriver _driver;

    private VirtualDiskConfig? _mountedConfig;
    private string? _mountedPassword;
    private CancellationTokenSource? _autosaveCts;
    private Task? _autosaveTask;

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
            StartAutosave();
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
        await StopAutosaveAsync();
        await _driver.UnmountAsync(mountPoint, ct);
        PersistFromDriver();

        _mountedConfig = null;
        _mountedPassword = null;
    }

    private void StartAutosave()
    {
        if (_driver is not IPersistableDiskDriver)
        {
            return;
        }

        _autosaveCts = new CancellationTokenSource();
        _autosaveTask = Task.Run(async () =>
        {
            while (!_autosaveCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), _autosaveCts.Token);
                    PersistSnapshotFromDriver();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // 自动快照失败不应中断挂载流程。
                }
            }
        });
    }

    private async Task StopAutosaveAsync()
    {
        if (_autosaveCts is null)
        {
            return;
        }

        _autosaveCts.Cancel();
        if (_autosaveTask is not null)
        {
            try
            {
                await _autosaveTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _autosaveTask = null;
        _autosaveCts.Dispose();
        _autosaveCts = null;
    }

    private void PersistSnapshotFromDriver()
    {
        if (_mountedConfig is null || _mountedPassword is null || _driver is not IPersistableDiskDriver persistable)
        {
            return;
        }

        var snapshot = persistable.CaptureSnapshotDiskBytes();
        if (snapshot is null)
        {
            return;
        }

        var payload = _encryption.Encrypt(snapshot, _mountedPassword);
        _container.Write(_mountedConfig.ContainerPath, payload);
    }

    private void PersistFromDriver()
    {
        if (_mountedConfig is null || _mountedPassword is null || _driver is not IPersistableDiskDriver persistable)
        {
            return;
        }

        var updated = persistable.TakeUpdatedDiskBytes();
        if (updated is null)
        {
            return;
        }

        var payload = _encryption.Encrypt(updated, _mountedPassword);
        _container.Write(_mountedConfig.ContainerPath, payload);
    }
}
