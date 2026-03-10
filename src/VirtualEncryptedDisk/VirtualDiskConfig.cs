namespace VirtualEncryptedDisk;

public sealed record VirtualDiskConfig(
    string ContainerPath,
    string MountPoint,
    int SizeMb,
    bool ReadOnly = false,
    string? RuntimeRootPath = null,
    bool AutosaveEnabled = true,
    int AutosaveIntervalSeconds = 5
);
