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
    private string? _lastPersistedSnapshotHash;

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

        if (ShouldUseChunkedContainer(config))
        {
            var store = ChunkedContainerStore.CreateNew(_encryption, config.ContainerPath, password, config.ChunkSizeBytes);
            store.WriteAllBytes(plainDisk);
            store.Flush();
        }
        else
        {
            var encryptedPayload = _encryption.Encrypt(plainDisk, password);
            _container.Write(config.ContainerPath, encryptedPayload);
        }

        DiagnosticLogger.Info($"Encrypted disk created at '{config.ContainerPath}'.");
        await Task.CompletedTask;
    }

    public async Task<MountResult> MountWithPasswordAsync(VirtualDiskConfig config, string password, CancellationToken ct = default)
    {
        try
        {
            var useChunked = ShouldUseChunkedContainer(config);
            byte[] plain;
            if (useChunked)
            {
                var store = ChunkedContainerStore.Open(_encryption, config.ContainerPath, password);
                plain = store.ReadAllBytes();
            }
            else
            {
                var payload = _container.Read(config.ContainerPath);
                plain = _encryption.Decrypt(payload, password);
            }

            await _driver.MountAsync(config, plain, ct);

            DiagnosticLogger.Info($"Container mode resolved: {(useChunked ? "VEC2(chunked)" : "VED1(legacy)" )}. Path='{config.ContainerPath}'.");
            _lastPersistedSnapshotHash = ComputeHashHex(plain);
            _mountedConfig = config;
            _mountedPassword = password;
            StartAutosave(config);
            DiagnosticLogger.Info($"Mount success. Container='{config.ContainerPath}', MountPoint='{config.MountPoint}'.");
            return MountResult.Mounted();
        }
        catch (CryptographicException ex)
        {
            DiagnosticLogger.Error($"Mount failed due to invalid password. Container='{config.ContainerPath}'.", ex);
            return MountResult.InvalidPassword();
        }
        catch (Exception ex)
        {
            DiagnosticLogger.Error(
                $"Mount failed. Container='{config.ContainerPath}', MountPoint='{config.MountPoint}', Driver='{_driver.GetType().Name}'.",
                ex);
            return MountResult.DriverError(ex.Message);
        }
    }

    public async Task UnmountAsync(string mountPoint, CancellationToken ct = default)
    {
        await StopAutosaveAsync();
        await _driver.UnmountAsync(mountPoint, ct);
        PersistFromDriver();

        DiagnosticLogger.Info($"Unmount completed for MountPoint='{mountPoint}'.");
        _mountedConfig = null;
        _mountedPassword = null;
        _lastPersistedSnapshotHash = null;
    }

    private void StartAutosave(VirtualDiskConfig config)
    {
        if (_driver is not IPersistableDiskDriver)
        {
            return;
        }

        if (!config.AutosaveEnabled)
        {
            DiagnosticLogger.Info("Autosave is disabled by configuration.");
            return;
        }

        var intervalSeconds = Math.Max(1, config.AutosaveIntervalSeconds);
        _autosaveCts = new CancellationTokenSource();
        _autosaveTask = Task.Run(async () =>
        {
            while (!_autosaveCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), _autosaveCts.Token);
                    PersistSnapshotFromDriver();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    DiagnosticLogger.Error("Autosave snapshot failed.", ex);
                }
            }
        });

        DiagnosticLogger.Info($"Autosave started. IntervalSeconds={intervalSeconds}.");
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

        if (_mountedConfig.PersistOnlyWhenChanged)
        {
            var currentHash = ComputeHashHex(snapshot);
            if (string.Equals(currentHash, _lastPersistedSnapshotHash, StringComparison.Ordinal))
            {
                DiagnosticLogger.Info("Autosave skipped because snapshot has no changes.");
                return;
            }

            _lastPersistedSnapshotHash = currentHash;
        }

        if (ShouldUseChunkedContainer(_mountedConfig))
        {
            var store = ChunkedContainerStore.OpenOrCreate(_encryption, _mountedConfig.ContainerPath, _mountedPassword, _mountedConfig.ChunkSizeBytes);
            store.WriteAllBytes(snapshot);
            store.Flush();
        }
        else
        {
            var payload = _encryption.Encrypt(snapshot, _mountedPassword);
            _container.Write(_mountedConfig.ContainerPath, payload);
        }

        DiagnosticLogger.Info($"Autosave snapshot persisted to '{_mountedConfig.ContainerPath}'.");
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
            DiagnosticLogger.Info("No updated disk bytes captured on unmount.");
            return;
        }

        if (_mountedConfig.PersistOnlyWhenChanged)
        {
            var currentHash = ComputeHashHex(updated);
            if (string.Equals(currentHash, _lastPersistedSnapshotHash, StringComparison.Ordinal))
            {
                DiagnosticLogger.Info("Final persist skipped because snapshot has no changes.");
                return;
            }

            _lastPersistedSnapshotHash = currentHash;
        }

        if (ShouldUseChunkedContainer(_mountedConfig))
        {
            var store = ChunkedContainerStore.OpenOrCreate(_encryption, _mountedConfig.ContainerPath, _mountedPassword, _mountedConfig.ChunkSizeBytes);
            store.WriteAllBytes(updated);
            store.Flush();
        }
        else
        {
            var payload = _encryption.Encrypt(updated, _mountedPassword);
            _container.Write(_mountedConfig.ContainerPath, payload);
        }

        DiagnosticLogger.Info($"Final persisted snapshot written to '{_mountedConfig.ContainerPath}'.");
    }

    private bool ShouldUseChunkedContainer(VirtualDiskConfig config)
    {
        if (config.UseChunkedContainerExperimental)
        {
            return true;
        }

        return IsVec2Container(config.ContainerPath);
    }

    private static bool IsVec2Container(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) != 4)
            {
                return false;
            }

            return magic.SequenceEqual("VEC2"u8);
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeHashHex(byte[] data)
    {
        var hash = SHA256.HashData(data);
        return Convert.ToHexString(hash);
    }
}
